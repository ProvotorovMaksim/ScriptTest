#region Prelude
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using SpaceEngineers.Libs.IgcCore;
using SpaceEngineers.Libs.TaskBusProducer;

namespace SpaceEngineers.Services.Broker
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private const int MaxTasks = 100;
        private const int LeaseTimeoutTicks = 180;
        private int _tick;
        private int _sequence;
        private readonly List<TaskRecord> _tasks = new List<TaskRecord>();
        private readonly HashSet<string> _cancelledRequestIds = new HashSet<string>();
        private readonly List<string> _completedRequestIds = new List<string>();
        private readonly List<TaskCommit> _commits = new List<TaskCommit>();
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();
        private IMyBroadcastListener _listener;
        private TaskBusProducer _producer;

        private sealed class TaskRecord
        {
            public string Id;
            public string RequestId;
            public string GroupId;
            public long RequesterId;
            public long WorkerId;
            public string Key;
            public string RequestJson;
            public string State;
            public int Attempts;
            public int LastDispatchTick;
            public int LeaseEpoch;
        }

        private sealed class TaskCommit
        {
            public string TaskId;
            public long ConsumerId;
            public int LeaseEpoch;
        }

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
            _listener = IGC.RegisterBroadcastListener(CommunicationTag);
            _producer = new TaskBusProducer(CommunicationTag, (channel, envelope, targetId) =>
                IGC.SendBroadcastMessage(channel, envelope, TransmissionDistance.TransmissionDistanceMax));
            LoadQueue();
        }

        public void Main(string argument)
        {
            _tick++;
            var command = argument ?? string.Empty;
            if (string.Equals(command, "purge", StringComparison.OrdinalIgnoreCase))
                PurgeClosedTasks();
            else if (string.Equals(command, "status", StringComparison.OrdinalIgnoreCase))
                ShowStatus();

            ExpireConsumerLeases();

            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas, antenna => antenna.IsFunctional && antenna.Enabled);
            if (_antennas.Count == 0)
            {
                Echo("Enable a functional radio antenna to run the broker.");
                return;
            }

            while (_listener.HasPendingMessage)
            {
                var message = _listener.AcceptMessage();
                if (message.Source == Me.EntityId)
                    continue;

                var json = message.Data as string;
                string eventType;
                string payloadJson;
                if (string.IsNullOrEmpty(json) || !TaskBusIgcHelper.TryParseEnvelope(json, out eventType, out payloadJson))
                    continue;

                HandleMessage(message.Source, eventType, payloadJson);
            }
        }

        private void HandleMessage(long sourceId, string eventType, string payloadJson)
        {
            if (eventType == "TASK_POLL")
            {
                HandleConsumerPoll(sourceId, payloadJson);
                return;
            }

            if (eventType == "TASK_PUBLISH")
            {
                HandleTaskPublish(sourceId, payloadJson);
                return;
            }

            if (eventType == "TASK_CANCEL")
            {
                CancelTask(sourceId, payloadJson);
                return;
            }

            if (eventType != "TASK_RECEIVED" && eventType != "TASK_DONE")
                return;

            string taskIdJson;
            string taskId;
            if (!TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "task_id", out taskIdJson) || !TaskBusIgcHelper.TryReadJsonString(taskIdJson, out taskId))
                return;
            int leaseEpoch;
            string epochJson;
            if (!TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "lease_epoch", out epochJson) ||
                !int.TryParse(epochJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out leaseEpoch))
                return;

            var task = _tasks.FirstOrDefault(entry => entry.Id == taskId);
            if (task == null)
            {
                if (eventType == "TASK_DONE")
                {
                    var commit = _commits.FirstOrDefault(entry => entry.TaskId == taskId && entry.ConsumerId == sourceId && entry.LeaseEpoch == leaseEpoch);
                    if (commit != null)
                        SendTaskDoneAck(commit);
                }
                return;
            }
            if (task.State == "CLEARED")
                return;
            if (task.WorkerId == sourceId && task.LeaseEpoch == leaseEpoch && task.State == "DONE" && eventType == "TASK_DONE")
            {
                SendMessage("TASK_DONE_ACK", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(taskId) + ",\"lease_epoch\":" + leaseEpoch.ToString(CultureInfo.InvariantCulture) + "}", sourceId, "done-ack:" + taskId + ":" + leaseEpoch.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (task.State == "DONE")
                return;
            if (task.WorkerId != sourceId || (task.State != "LEASED" && task.State != "ACTIVE"))
                return;
            if (leaseEpoch != task.LeaseEpoch)
                return;

            if (eventType == "TASK_RECEIVED")
            {
                task.WorkerId = sourceId;
                task.State = "ACTIVE";
                task.LastDispatchTick = _tick;
                Echo("Consumer " + sourceId + " persisted task " + task.Id);
            }
            else
            {
                AddCompletedRequest(GetScopedRequestId(task.RequesterId, task.RequestId));
                AddTaskCommit(task.Id, sourceId, leaseEpoch);
                _tasks.Remove(task);
                Echo("Completed: " + task.Id);
            }
            SaveQueue();
            if (eventType == "TASK_DONE")
                SendTaskDoneAck(new TaskCommit { TaskId = taskId, ConsumerId = sourceId, LeaseEpoch = leaseEpoch });
        }

        private void HandleTaskPublish(long requesterId, string requestJson)
        {
            string requestIdJson;
            string requestId;
            if (!TaskBusIgcHelper.TryGetJsonProperty(requestJson, "request_id", out requestIdJson) || !TaskBusIgcHelper.TryReadJsonString(requestIdJson, out requestId))
                return;

            string keyJson;
            string taskKey;
            if (!TaskBusIgcHelper.TryGetJsonProperty(requestJson, "key", out keyJson) || !TaskBusIgcHelper.TryReadJsonString(keyJson, out taskKey))
                taskKey = "";

            var scopedRequestId = GetScopedRequestId(requesterId, requestId);
            var existing = _tasks.FirstOrDefault(task => task.RequesterId == requesterId && task.RequestId == requestId);
            if (existing != null)
            {
                SendMessage("TASK_PUBLISHED_ACK", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) + ",\"task_id\":" + TaskBusIgcHelper.ToJsonString(existing.Id) + ",\"state\":" + TaskBusIgcHelper.ToJsonString(existing.State) + "}", requesterId, "ack:" + requestId);
                return;
            }
            if (_completedRequestIds.Contains(scopedRequestId))
            {
                SendMessage("TASK_PUBLISHED_ACK", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) + ",\"state\":\"DONE\"}", requesterId, "ack:" + requestId);
                return;
            }
            if (_cancelledRequestIds.Contains(scopedRequestId))
            {
                SendMessage("TASK_PUBLISHED_ACK", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) + ",\"state\":\"CANCELLED\"}", requesterId, "ack:" + requestId);
                return;
            }
            if (_tasks.Count(task => task.State == "QUEUED" || task.State == "LEASED" || task.State == "ACTIVE") >= MaxTasks)
            {
                SendMessage("TASK_REJECTED", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) + ",\"reason\":\"QUEUE_FULL\"}", requesterId);
                Echo("Queue full. Run 'purge' after closing requests.");
                return;
            }

            string groupJson;
            string groupId;
            if (!TaskBusIgcHelper.TryGetJsonProperty(requestJson, "group_id", out groupJson) || !TaskBusIgcHelper.TryReadJsonString(groupJson, out groupId))
                groupId = "default";

            var id = requesterId.ToString(CultureInfo.InvariantCulture) + "-" + (++_sequence).ToString(CultureInfo.InvariantCulture);
            _tasks.Add(new TaskRecord
            {
                Id = id,
                RequestId = requestId,
                GroupId = groupId,
                RequesterId = requesterId,
                Key = taskKey,
                RequestJson = requestJson,
                State = "QUEUED"
            });
            SaveQueue();
            SendMessage("TASK_PUBLISHED_ACK", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) + ",\"task_id\":" + TaskBusIgcHelper.ToJsonString(id) + ",\"state\":\"QUEUED\"}", requesterId, "ack:" + requestId);
            Echo("Queued " + id + " (" + taskKey + ")");
        }

        private void CancelTask(long requesterId, string payloadJson)
        {
            string requestIdJson;
            string requestId;
            if (!TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "request_id", out requestIdJson) || !TaskBusIgcHelper.TryReadJsonString(requestIdJson, out requestId))
                return;

            AddCancelledRequest(GetScopedRequestId(requesterId, requestId));
            var task = _tasks.FirstOrDefault(entry => entry.RequestId == requestId && entry.RequesterId == requesterId);
            if (task != null && task.State != "DONE")
            {
                task.State = "CLEARED";
                SaveQueue();
                if (task.WorkerId != 0)
                    SendMessage("TASK_CANCEL", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(task.Id) + ",\"lease_epoch\":" + task.LeaseEpoch.ToString(CultureInfo.InvariantCulture) + "}", task.WorkerId);
            }
            SaveQueue();
            SendMessage("TASK_CANCEL_ACK", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) + "}", requesterId, "cancel-ack:" + requestId);
        }

        private void HandleConsumerPoll(long consumerId, string payloadJson)
        {
            string groupJson;
            string groupId;
            if (!TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "group_id", out groupJson) || !TaskBusIgcHelper.TryReadJsonString(groupJson, out groupId))
                groupId = "default";

            string completedJson;
            string completedId;
            if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "completed_task_id", out completedJson) &&
                TaskBusIgcHelper.TryReadJsonString(completedJson, out completedId))
            {
                string completedEpochJson;
                int completedEpoch;
                if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "lease_epoch", out completedEpochJson) &&
                    int.TryParse(completedEpochJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out completedEpoch))
                    MarkTaskDone(consumerId, completedId, completedEpoch);
            }

            string activeIdJson;
            string activeId;
            if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "active_task_id", out activeIdJson) &&
                TaskBusIgcHelper.TryReadJsonString(activeIdJson, out activeId))
            {
                var previous = _tasks.FirstOrDefault(task => task.Id == activeId);
                string activeEpochJson;
                var activeEpoch = -1;
                var hasActiveEpoch = TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "active_lease_epoch", out activeEpochJson) &&
                    int.TryParse(activeEpochJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out activeEpoch);
                if (!hasActiveEpoch)
                    return;
                if (previous == null || previous.WorkerId != consumerId ||
                    previous.GroupId != groupId || previous.LeaseEpoch != activeEpoch ||
                    (previous.State != "LEASED" && previous.State != "ACTIVE"))
                {
                    SendMessage("TASK_REVOKED", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(activeId) +
                        ",\"lease_epoch\":" + activeEpoch.ToString(CultureInfo.InvariantCulture) + "}", consumerId);
                    return;
                }
            }

            var assigned = _tasks.FirstOrDefault(task => task.WorkerId == consumerId &&
                task.GroupId == groupId && (task.State == "LEASED" || task.State == "ACTIVE"));
            if (assigned != null)
            {
                assigned.LastDispatchTick = _tick;
                DispatchTask(assigned);
                SaveQueue();
                return;
            }

            var next = _tasks.FirstOrDefault(task => task.State == "QUEUED" && task.GroupId == groupId && task.RequesterId != consumerId);
            if (next == null)
            {
                SendMessage("QUEUE_EMPTY", "null", consumerId);
                return;
            }

            next.WorkerId = consumerId;
            next.LeaseEpoch++;
            next.State = "LEASED";
            next.LastDispatchTick = _tick;
            SaveQueue();
            DispatchTask(next);
            SaveQueue();
        }

        private void ExpireConsumerLeases()
        {
            var changed = false;
            foreach (var task in _tasks.Where(entry => entry.State == "LEASED" || entry.State == "ACTIVE"))
            {
                if (_tick - task.LastDispatchTick <= LeaseTimeoutTicks)
                    continue;

                task.WorkerId = 0;
                task.State = "QUEUED";
                task.LeaseEpoch++;
                changed = true;
                Echo("Lease expired; requeued " + task.Id);
            }
            if (changed)
                SaveQueue();
        }

        private void MarkTaskDone(long consumerId, string taskId, int leaseEpoch)
        {
            var task = _tasks.FirstOrDefault(entry => entry.Id == taskId && entry.WorkerId == consumerId && entry.LeaseEpoch == leaseEpoch);
            if (task == null)
            {
                var existingCommit = _commits.FirstOrDefault(entry => entry.TaskId == taskId && entry.ConsumerId == consumerId && entry.LeaseEpoch == leaseEpoch);
                if (existingCommit != null)
                    SendTaskDoneAck(existingCommit);
                return;
            }
            if (task.State == "CLEARED")
                return;

            if (task.State == "DONE")
            {
                SendTaskDoneAck(new TaskCommit { TaskId = taskId, ConsumerId = consumerId, LeaseEpoch = leaseEpoch });
                return;
            }

            AddCompletedRequest(GetScopedRequestId(task.RequesterId, task.RequestId));
            AddTaskCommit(taskId, consumerId, leaseEpoch);
            _tasks.Remove(task);
            SaveQueue();
            SendTaskDoneAck(new TaskCommit { TaskId = taskId, ConsumerId = consumerId, LeaseEpoch = leaseEpoch });
            Echo("Completed: " + task.Id);
        }

        private void AddTaskCommit(string taskId, long consumerId, int leaseEpoch)
        {
            if (_commits.Any(entry => entry.TaskId == taskId && entry.ConsumerId == consumerId && entry.LeaseEpoch == leaseEpoch))
                return;
            _commits.Add(new TaskCommit { TaskId = taskId, ConsumerId = consumerId, LeaseEpoch = leaseEpoch });
            while (_commits.Count > 500)
                _commits.RemoveAt(0);
        }

        private void SendTaskDoneAck(TaskCommit commit)
        {
            SendMessage("TASK_DONE_ACK", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(commit.TaskId) + ",\"lease_epoch\":" + commit.LeaseEpoch.ToString(CultureInfo.InvariantCulture) + "}", commit.ConsumerId, "done-ack:" + commit.TaskId + ":" + commit.LeaseEpoch.ToString(CultureInfo.InvariantCulture));
        }

        private void DispatchTask(TaskRecord task)
        {
            var payload = "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(task.Id) +
                ",\"request_id\":" + TaskBusIgcHelper.ToJsonString(task.RequestId) +
                ",\"requester_id\":" + task.RequesterId.ToString(CultureInfo.InvariantCulture) +
                ",\"key\":" + TaskBusIgcHelper.ToJsonString(task.Key) +
                ",\"group_id\":" + TaskBusIgcHelper.ToJsonString(task.GroupId) +
                ",\"lease_epoch\":" + task.LeaseEpoch.ToString(CultureInfo.InvariantCulture) +
                ",\"details\":" + task.RequestJson + "}";
            SendMessage("TASK_ASSIGN", payload, task.WorkerId, "assign:" + task.Id + ":" + task.LeaseEpoch.ToString(CultureInfo.InvariantCulture));
            task.Attempts++;
            Echo("Sent " + task.Id + " to PB " + task.WorkerId + " (attempt " + task.Attempts + ")");
        }

        private void SendMessage(string eventType, string payloadJson, long targetId = 0, string messageId = null)
        {
            _producer.Publish(eventType, payloadJson, messageId, targetId);
        }

        private void PurgeClosedTasks()
        {
            var removed = _tasks.RemoveAll(task => task.State == "CLEARED");
            SaveQueue();
            Echo("Purged " + removed + " terminal records; request dedupe tombstones are retained.");
        }

        private void AddCompletedRequest(string requestId)
        {
            if (string.IsNullOrEmpty(requestId) || _completedRequestIds.Contains(requestId))
                return;
            _completedRequestIds.Add(requestId);
            while (_completedRequestIds.Count > 500)
                _completedRequestIds.RemoveAt(0);
        }

        private void AddCancelledRequest(string requestId)
        {
            if (string.IsNullOrEmpty(requestId) || !_cancelledRequestIds.Add(requestId))
                return;
            while (_cancelledRequestIds.Count > 500)
                _cancelledRequestIds.Remove(_cancelledRequestIds.First());
        }

        private string GetScopedRequestId(long producerId, string requestId)
        {
            return producerId.ToString(CultureInfo.InvariantCulture) + ":" + requestId;
        }

        private void ShowStatus()
        {
            Echo("Tasks: " + _tasks.Count);
            foreach (var task in _tasks)
                Echo(task.Id + " " + task.State + " worker=" + task.WorkerId + " " + task.Key);
        }

        private void LoadQueue()
        {
            _tasks.Clear();
            _cancelledRequestIds.Clear();
            _completedRequestIds.Clear();
            _commits.Clear();
            _sequence = 0;
            if (string.IsNullOrWhiteSpace(Storage))
                return;

            foreach (var line in Storage.Split('\n'))
            {
                var parts = line.Split('|');
                if (parts.Length == 2 && parts[0] == "SEQ")
                {
                    int.TryParse(parts[1], out _sequence);
                    continue;
                }
                if (parts.Length == 2 && parts[0] == "CANCEL")
                {
                    try { AddCancelledRequest(TaskBusIgcHelper.DecodeStorage(parts[1])); }
                    catch (FormatException) { }
                    continue;
                }
                if (parts.Length == 2 && parts[0] == "COMPLETE")
                {
                    try { AddCompletedRequest(TaskBusIgcHelper.DecodeStorage(parts[1])); }
                    catch (FormatException) { }
                    continue;
                }
                if (parts.Length == 4 && parts[0] == "COMMIT")
                {
                    long consumerId;
                    int epoch;
                    try
                    {
                        if (long.TryParse(parts[2], out consumerId) && int.TryParse(parts[3], out epoch))
                            AddTaskCommit(TaskBusIgcHelper.DecodeStorage(parts[1]), consumerId, epoch);
                    }
                    catch (FormatException) { }
                    continue;
                }
                if (parts[0] != "TASK" || (parts.Length != 9 && parts.Length != 12))
                    continue;

                try
                {
                    long requesterId;
                    long workerId;
                    int attempts;
                    int leaseEpoch = 0;
                    string requestId;
                    string groupId;
                    string resource;
                    string requestJson;
                    string state;
                    string id;
                    if (parts.Length == 12)
                    {
                        id = parts[1];
                        requestId = TaskBusIgcHelper.DecodeStorage(parts[2]);
                        groupId = TaskBusIgcHelper.DecodeStorage(parts[3]);
                        if (!long.TryParse(parts[4], out requesterId) || !long.TryParse(parts[5], out workerId) || !int.TryParse(parts[9], out attempts) || !int.TryParse(parts[11], out leaseEpoch))
                            continue;
                        resource = TaskBusIgcHelper.DecodeStorage(parts[6]);
                        requestJson = TaskBusIgcHelper.DecodeStorage(parts[7]);
                        state = parts[8];
                    }
                    else
                    {
                        id = parts[1];
                        requestId = id;
                        groupId = "default";
                        if (!long.TryParse(parts[2], out requesterId) || !long.TryParse(parts[3], out workerId) || !int.TryParse(parts[7], out attempts))
                            continue;
                        resource = TaskBusIgcHelper.DecodeStorage(parts[4]);
                        requestJson = TaskBusIgcHelper.DecodeStorage(parts[5]);
                        state = parts[6];
                    }
                    _tasks.Add(new TaskRecord
                    {
                        Id = id,
                        RequestId = requestId,
                        GroupId = groupId,
                        RequesterId = requesterId,
                        WorkerId = workerId,
                        Key = resource,
                        RequestJson = requestJson,
                        State = state,
                        Attempts = attempts,
                        LastDispatchTick = 0,
                        LeaseEpoch = leaseEpoch
                    });
                }
                catch (FormatException)
                {
                    continue;
                }
            }
        }

        private void SaveQueue()
        {
            var storage = new StringBuilder();
            storage.AppendLine("SEQ|" + _sequence.ToString(CultureInfo.InvariantCulture));
            foreach (var requestId in _cancelledRequestIds)
                storage.AppendLine("CANCEL|" + TaskBusIgcHelper.EncodeStorage(requestId));
            foreach (var requestId in _completedRequestIds)
                storage.AppendLine("COMPLETE|" + TaskBusIgcHelper.EncodeStorage(requestId));
            foreach (var commit in _commits)
                storage.AppendLine("COMMIT|" + TaskBusIgcHelper.EncodeStorage(commit.TaskId) + "|" + commit.ConsumerId.ToString(CultureInfo.InvariantCulture) + "|" + commit.LeaseEpoch.ToString(CultureInfo.InvariantCulture));
            foreach (var task in _tasks)
            {
                storage.Append("TASK|").Append(task.Id).Append('|')
                    .Append(TaskBusIgcHelper.EncodeStorage(task.RequestId)).Append('|')
                    .Append(TaskBusIgcHelper.EncodeStorage(task.GroupId)).Append('|')
                    .Append(task.RequesterId.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(task.WorkerId.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(TaskBusIgcHelper.EncodeStorage(task.Key)).Append('|')
                    .Append(TaskBusIgcHelper.EncodeStorage(task.RequestJson)).Append('|')
                    .Append(task.State).Append('|')
                    .Append(task.Attempts.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(task.LastDispatchTick.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .AppendLine(task.LeaseEpoch.ToString(CultureInfo.InvariantCulture));
            }
            Storage = storage.ToString();
        }

        #region PreludeFooter
    }
}
#endregion
