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
        private const string BackupPanelName = "[TASKBUS BACKUP]";
        private const string BackupVersion = "TASKBUS-BACKUP-1|";
        private const string GalnetRelayName = "[GALNET RELAY]";
        private readonly Dictionary<long, long> _galnetRoutes = new Dictionary<long, long>();
        private readonly Queue<string> _galnetReplies = new Queue<string>();
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private const int MaxTasks = 100;
        private const int RetainedRequestCount = 500;
        private const int LeaseTimeoutTicks = 180;
        private static readonly long LeaseTimeoutUtc = TimeSpan.FromSeconds(30).Ticks;
        private int _tick;
        private int _sequence;
        private bool _paused;
        private bool _retired;
        private long _resumeAfterUtc;
        private readonly List<TaskRecord> _tasks = new List<TaskRecord>();
        private readonly List<string> _cancelledRequestIds = new List<string>();
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
            public bool HasParent;
            public bool BaseStockConfirmed;
            public string State;
            public int Attempts;
            public int LastDispatchTick;
            public long LeaseExpiresUtc;
            public int LeaseEpoch;
        }

        private readonly Dictionary<string, string> _surveyReports = new Dictionary<string, string>();

        private static string ReadSurveyReport(string payload)
        {
            string raw;
            string report;
            return TaskBusIgcHelper.TryGetJsonProperty(payload, "survey_report", out raw) &&
                TaskBusIgcHelper.TryReadJsonString(raw, out report) && report.Length <= 320 ? report : null;
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
            _producer = new TaskBusProducer(CommunicationTag, SendEnvelope);
            LoadQueue();
            if (!_paused)
                ExpireConsumerLeases();
            SaveAfterParentLeaseChange(false);
        }

        public void Main(string argument)
        {
            var command = argument ?? string.Empty;
            if (string.Equals(command, "broker pause", StringComparison.OrdinalIgnoreCase))
            {
                if (!_paused)
                {
                    _paused = true;
                    // A worker may still hold a lease that was requeued by the tick timeout.
                    _resumeAfterUtc = _sequence == 0 ? 0 : DateTime.UtcNow.Ticks + LeaseTimeoutUtc;
                    foreach (var task in _tasks)
                        _resumeAfterUtc = Math.Max(_resumeAfterUtc, task.LeaseExpiresUtc);
                    _galnetReplies.Clear();
                    ExpireConsumerLeases(force: true);
                    SaveAfterParentLeaseChange(false);
                    SaveQueue();
                }
                ShowStatus();
                return;
            }
            if (_paused)
            {
                for (int i = 0; i < 32 && _listener.HasPendingMessage; i++)
                    _listener.AcceptMessage();
                if (string.Equals(command, "broker resume", StringComparison.OrdinalIgnoreCase))
                {
                    if (_retired)
                    {
                        Echo("Broker retired after handoff. Resume is disabled.");
                        return;
                    }
                    if (DateTime.UtcNow.Ticks < _resumeAfterUtc)
                    {
                        Echo("Broker remains paused until " +
                            new DateTime(_resumeAfterUtc, DateTimeKind.Utc).ToString("u", CultureInfo.InvariantCulture));
                        return;
                    }
                    _paused = false;
                    _resumeAfterUtc = 0;
                    SaveQueue();
                    Echo("Broker resumed.");
                }
                else if (string.Equals(command, "broker export", StringComparison.OrdinalIgnoreCase))
                    TransferBackup(import: false);
                else if (string.Equals(command, "broker handoff", StringComparison.OrdinalIgnoreCase))
                    TransferBackup(import: false, retire: true);
                else if (string.Equals(command, "broker import", StringComparison.OrdinalIgnoreCase))
                    TransferBackup(import: true);
                else if (string.Equals(command, "status", StringComparison.OrdinalIgnoreCase))
                    ShowStatus();
                return;
            }
            if (command.StartsWith("galnet recv ", StringComparison.Ordinal))
            {
                ExpireConsumerLeases();
                ReceiveGalnet(command);
                return;
            }

            _tick++;
            FlushGalnetReply();
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

                TaskBusEnvelope envelope;
                if (!TaskBusProtocol.TryParseEnvelope(message.Data as string, out envelope) ||
                    (envelope.TargetId != 0 && envelope.TargetId != Me.EntityId))
                    continue;

                HandleMessage(message.Source, envelope.EventType, envelope.PayloadJson);
            }
        }

        private IMyProgrammableBlock FindGalnetRelay()
        {
            var relay = GridTerminalSystem.GetBlockWithName(GalnetRelayName) as IMyProgrammableBlock;
            return relay != null && relay.EntityId != Me.EntityId && relay.CubeGrid == Me.CubeGrid &&
                relay.IsWorking ? relay : null;
        }

        private void ReceiveGalnet(string command)
        {
            if (FindGalnetRelay() == null)
                return;

            var fields = command.Split(new[] { ' ' }, 7, StringSplitOptions.RemoveEmptyEntries);
            long relayId;
            long workerId;
            TaskBusEnvelope envelope;
            if (fields.Length != 7 || fields[4] != "taskbus" ||
                !long.TryParse(fields[2], out relayId) || relayId == 0 ||
                !long.TryParse(fields[5], out workerId) || workerId == 0 || workerId == Me.EntityId ||
                !TaskBusProtocol.TryParseEnvelope(fields[6], out envelope) || envelope.TargetId != Me.EntityId)
                return;

            long knownRelay;
            if (_galnetRoutes.TryGetValue(workerId, out knownRelay))
            {
                if (knownRelay != relayId)
                    return;
            }
            else
            {
                if (_galnetRoutes.Count >= 128)
                    return;
                _galnetRoutes.Add(workerId, relayId);
            }
            HandleMessage(workerId, envelope.EventType, envelope.PayloadJson);
        }

        private void SendEnvelope(string channel, string envelope, long targetId)
        {
            long relayId;
            if (_galnetRoutes.TryGetValue(targetId, out relayId))
            {
                var command = "app " + relayId + " taskbus " + Me.EntityId + " " + envelope;
                if (_galnetReplies.Count < 32 && !_galnetReplies.Contains(command))
                    _galnetReplies.Enqueue(command);
                return;
            }
            IGC.SendBroadcastMessage(channel, envelope, TransmissionDistance.TransmissionDistanceMax);
        }

        private void FlushGalnetReply()
        {
            if (_galnetReplies.Count == 0)
                return;
            var relay = FindGalnetRelay();
            if (relay != null && relay.TryRun(_galnetReplies.Peek()))
                _galnetReplies.Dequeue();
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

            if (eventType == "TASK_QUERY")
            {
                string raw;
                string requestId;
                if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "request_id", out raw) &&
                    TaskBusIgcHelper.TryReadJsonString(raw, out requestId) &&
                    !string.IsNullOrWhiteSpace(requestId))
                {
                    RecordBaseStock(sourceId, requestId, payloadJson);
                    if (!ReportKnownRequest(sourceId, requestId))
                        SendRequestStatus(sourceId, requestId, "UNKNOWN");
                }
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

            if (eventType == "TASK_DONE")
            {
                MarkTaskDone(sourceId, taskId, leaseEpoch, ReadSurveyReport(payloadJson));
                return;
            }

            var task = _tasks.FirstOrDefault(entry => entry.Id == taskId &&
                entry.WorkerId == sourceId && entry.LeaseEpoch == leaseEpoch);
            if (task == null || (task.State != "LEASED" && task.State != "ACTIVE"))
                return;

            task.State = "ACTIVE";
            RenewLease(task);
            SaveQueue();
            Echo("Consumer " + sourceId + " persisted task " + task.Id);
        }

        private void HandleTaskPublish(long requesterId, string requestJson)
        {
            string requestIdJson;
            string requestId;
            if (!TaskBusIgcHelper.TryGetJsonProperty(requestJson, "request_id", out requestIdJson) ||
                !TaskBusIgcHelper.TryReadJsonString(requestIdJson, out requestId) || string.IsNullOrWhiteSpace(requestId))
                return;

            string keyJson;
            string taskKey;
            if (!TaskBusIgcHelper.TryGetJsonProperty(requestJson, "key", out keyJson) || !TaskBusIgcHelper.TryReadJsonString(keyJson, out taskKey))
                taskKey = "";

            if (ReportKnownRequest(requesterId, requestId))
                return;
            if (!HasParentLease(requesterId, requestJson))
            {
                SendMessage("TASK_REJECTED", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) +
                    ",\"reason\":\"PARENT_LEASE_INVALID\"}", requesterId);
                return;
            }
            if (_tasks.Count(task => task.State == "QUEUED" || task.State == "LEASED" || task.State == "ACTIVE") >= MaxTasks)
            {
                SendMessage("TASK_REJECTED", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) + ",\"reason\":\"QUEUE_FULL\"}", requesterId);
                Echo("Queue full. Finish or cancel an open task to free capacity.");
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
                HasParent = ReferencesParent(requestJson),
                State = "QUEUED"
            });
            SaveQueue();
            SendMessage("TASK_PUBLISHED_ACK", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) + ",\"task_id\":" + TaskBusIgcHelper.ToJsonString(id) + ",\"state\":\"QUEUED\"}", requesterId, "ack:" + requestId);
            Echo("Queued " + id + " (" + taskKey + ")");
        }

        private void RecordBaseStock(long requesterId, string requestId, string payload)
        {
            double stock;
            if (!TaskBusIgcHelper.TryReadFiniteNumber(payload, "stock", out stock))
                return;
            var task = _tasks.FirstOrDefault(entry => entry.RequesterId == requesterId &&
                entry.RequestId == requestId && entry.GroupId == "MiningPoints");
            double minimum;
            if (task == null || task.BaseStockConfirmed ||
                !TaskBusIgcHelper.TryReadFiniteNumber(task.RequestJson, "minimum", out minimum) ||
                minimum <= 0 || stock < minimum)
                return;

            task.BaseStockConfirmed = true;
            SaveQueue();
        }

        private bool ReportKnownRequest(long requesterId, string requestId)
        {
            var existing = _tasks.FirstOrDefault(task => task.RequesterId == requesterId && task.RequestId == requestId);
            if (existing != null)
            {
                SendRequestStatus(requesterId, requestId,
                    existing.State == "CLEARED" ? "CANCELLED" : existing.State, existing.Id);
                return true;
            }
            var scopedId = GetScopedRequestId(requesterId, requestId);
            var state = _completedRequestIds.Contains(scopedId) ? "DONE" :
                _cancelledRequestIds.Contains(scopedId) ? "CANCELLED" : null;
            if (state == null)
                return false;
            string report;
            _surveyReports.TryGetValue(scopedId, out report);
            SendRequestStatus(requesterId, requestId, state, surveyReport: report);
            return true;
        }

        private void SendRequestStatus(long requesterId, string requestId, string state, string taskId = null, string surveyReport = null)
        {
            var payload = "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId);
            if (taskId != null)
                payload += ",\"task_id\":" + TaskBusIgcHelper.ToJsonString(taskId);
            if (surveyReport != null)
                payload += ",\"survey_report\":" + TaskBusIgcHelper.ToJsonString(surveyReport);
            payload += ",\"state\":" + TaskBusIgcHelper.ToJsonString(state) + "}";
            SendMessage("TASK_PUBLISHED_ACK", payload, requesterId, "ack:" + requestId);
        }

        private bool HasParentLease(long requesterId, string requestJson)
        {
            string parentIdJson;
            string epochJson;
            bool hasParentId = TaskBusIgcHelper.TryGetJsonProperty(requestJson, "parent_task_id", out parentIdJson);
            bool hasEpoch = TaskBusIgcHelper.TryGetJsonProperty(requestJson, "parent_lease_epoch", out epochJson);
            if (!hasParentId && !hasEpoch)
                return true;

            string parentId;
            int epoch;
            if (!hasParentId || !hasEpoch || !TaskBusIgcHelper.TryReadJsonString(parentIdJson, out parentId) ||
                !int.TryParse(epochJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch))
                return false;

            return _tasks.Any(task => task.Id == parentId && task.GroupId == "MiningPoints" && !task.HasParent &&
                task.WorkerId == requesterId && task.LeaseEpoch == epoch &&
                (task.State == "LEASED" || task.State == "ACTIVE"));
        }

        private static bool ReferencesParent(string requestJson)
        {
            string value;
            return TaskBusIgcHelper.TryGetJsonProperty(requestJson, "parent_task_id", out value) ||
                TaskBusIgcHelper.TryGetJsonProperty(requestJson, "parent_lease_epoch", out value);
        }

        private void SaveAfterParentLeaseChange(bool notifyWorkers)
        {
            var invalid = _tasks.Where(task => task.HasParent &&
                !HasParentLease(task.RequesterId, task.RequestJson)).ToList();
            foreach (var child in invalid)
            {
                _tasks.Remove(child);
                AddCancelledRequest(GetScopedRequestId(child.RequesterId, child.RequestId));
            }
            if (invalid.Count > 0 || notifyWorkers)
                SaveQueue();
            if (!notifyWorkers)
                return;

            foreach (var child in invalid)
                if (child.WorkerId != 0)
                    SendTaskRevoked(child.WorkerId, child.Id, child.LeaseEpoch);
        }

        private void CancelTask(long requesterId, string payloadJson)
        {
            string requestIdJson;
            string requestId;
            if (!TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "request_id", out requestIdJson) ||
                !TaskBusIgcHelper.TryReadJsonString(requestIdJson, out requestId) || string.IsNullOrWhiteSpace(requestId))
                return;

            var scopedId = GetScopedRequestId(requesterId, requestId);
            var task = _tasks.FirstOrDefault(entry => entry.RequestId == requestId && entry.RequesterId == requesterId);
            bool cancelled = task != null && task.State != "DONE" && _tasks.Remove(task);
            bool recorded = !_completedRequestIds.Contains(scopedId) && AddCancelledRequest(scopedId);
            if (cancelled && task.GroupId == "MiningPoints")
                SaveAfterParentLeaseChange(true);
            else if (cancelled || recorded)
                SaveQueue();
            if (cancelled && task.WorkerId != 0)
                SendMessage("TASK_CANCEL", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(task.Id) +
                    ",\"lease_epoch\":" + task.LeaseEpoch.ToString(CultureInfo.InvariantCulture) + "}", task.WorkerId);
            SendMessage("TASK_CANCEL_ACK", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) + "}", requesterId, "cancel-ack:" + requestId);
        }

        private void HandleConsumerPoll(long consumerId, string payloadJson)
        {
            string groupJson;
            string groupId;
            if (!TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "group_id", out groupJson) || !TaskBusIgcHelper.TryReadJsonString(groupJson, out groupId))
                groupId = "default";

            string surveyFlag;
            bool surveyCapable = groupId == "MiningFleet" &&
                TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "survey_capable", out surveyFlag) &&
                surveyFlag == "true";
            Func<TaskRecord, bool> acceptsGroup = task =>
                task.GroupId == groupId || (surveyCapable && task.GroupId == "Scouts");

            string completedJson;
            string completedId;
            if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "completed_task_id", out completedJson) &&
                TaskBusIgcHelper.TryReadJsonString(completedJson, out completedId))
            {
                string completedEpochJson;
                int completedEpoch;
                if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "lease_epoch", out completedEpochJson) &&
                    int.TryParse(completedEpochJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out completedEpoch))
                    MarkTaskDone(consumerId, completedId, completedEpoch, ReadSurveyReport(payloadJson));
                return;
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
                    !acceptsGroup(previous) || previous.LeaseEpoch != activeEpoch ||
                    (previous.State != "LEASED" && previous.State != "ACTIVE"))
                {
                    SendTaskRevoked(consumerId, activeId, activeEpoch);
                    return;
                }
            }

            var assigned = _tasks.FirstOrDefault(task => task.WorkerId == consumerId &&
                acceptsGroup(task) && (task.State == "LEASED" || task.State == "ACTIVE"));
            if (assigned != null)
            {
                RenewLease(assigned);
                DispatchTask(assigned);
                SaveQueue();
                return;
            }

            var next = _tasks.FirstOrDefault(task => task.State == "QUEUED" && acceptsGroup(task) && task.RequesterId != consumerId);
            if (next == null)
            {
                SendMessage("QUEUE_EMPTY", "null", consumerId);
                return;
            }

            next.WorkerId = consumerId;
            next.LeaseEpoch++;
            next.State = "LEASED";
            RenewLease(next);
            SaveQueue();
            DispatchTask(next);
            SaveQueue();
        }

        private void RenewLease(TaskRecord task)
        {
            task.LastDispatchTick = _tick;
            task.LeaseExpiresUtc = Math.Max(task.LeaseExpiresUtc + 1, DateTime.UtcNow.Ticks + LeaseTimeoutUtc);
        }

        private void ExpireConsumerLeases(bool force = false)
        {
            var changed = false;
            var parentChanged = false;
            long now = DateTime.UtcNow.Ticks;
            foreach (var task in _tasks.Where(entry => entry.State == "LEASED" || entry.State == "ACTIVE"))
            {
                if (!force && _tick - task.LastDispatchTick <= LeaseTimeoutTicks &&
                    (task.LeaseExpiresUtc == 0 || now < task.LeaseExpiresUtc))
                    continue;

                task.WorkerId = 0;
                task.LeaseExpiresUtc = 0;
                task.State = "QUEUED";
                task.LeaseEpoch++;
                changed = true;
                parentChanged |= task.GroupId == "MiningPoints";
                Echo("Lease expired; requeued " + task.Id);
            }
            if (parentChanged && !_paused)
                SaveAfterParentLeaseChange(true);
            else if (changed)
                SaveQueue();
        }

        private void MarkTaskDone(long consumerId, string taskId, int leaseEpoch, string surveyReport)
        {
            var task = _tasks.FirstOrDefault(entry => entry.Id == taskId && entry.WorkerId == consumerId && entry.LeaseEpoch == leaseEpoch);
            if (task == null)
            {
                var existingCommit = _commits.FirstOrDefault(entry => entry.TaskId == taskId && entry.ConsumerId == consumerId && entry.LeaseEpoch == leaseEpoch);
                if (existingCommit != null)
                    SendTaskDoneAck(existingCommit);
                else
                    SendTaskRevoked(consumerId, taskId, leaseEpoch);
                return;
            }

            if (task.State == "DONE")
            {
                SendTaskDoneAck(new TaskCommit { TaskId = taskId, ConsumerId = consumerId, LeaseEpoch = leaseEpoch });
                return;
            }

            if (task.State != "LEASED" && task.State != "ACTIVE")
            {
                SendTaskRevoked(consumerId, taskId, leaseEpoch);
                return;
            }

            AddCompletedRequest(GetScopedRequestId(task.RequesterId, task.RequestId),
                task.HasParent && task.GroupId == "Scouts" ? surveyReport : null);
            AddTaskCommit(taskId, consumerId, leaseEpoch);
            _tasks.Remove(task);
            if (task.GroupId == "MiningPoints")
                SaveAfterParentLeaseChange(true);
            else
                SaveQueue();
            SendTaskDoneAck(new TaskCommit { TaskId = taskId, ConsumerId = consumerId, LeaseEpoch = leaseEpoch });
            Echo("Completed: " + task.Id);
        }

        private void SendTaskRevoked(long consumerId, string taskId, int leaseEpoch)
        {
            SendMessage("TASK_REVOKED", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(taskId) +
                ",\"lease_epoch\":" + leaseEpoch.ToString(CultureInfo.InvariantCulture) + "}", consumerId);
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
                ",\"lease_expires_utc_ticks\":" + task.LeaseExpiresUtc.ToString(CultureInfo.InvariantCulture) +
                ",\"base_stock_confirmed\":" + (task.BaseStockConfirmed ? "true" : "false") +
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

        private void AddCompletedRequest(string requestId, string surveyReport = null)
        {
            if (string.IsNullOrEmpty(requestId) || _completedRequestIds.Contains(requestId))
                return;
            _completedRequestIds.Add(requestId);
            if (surveyReport != null)
                _surveyReports[requestId] = surveyReport;
            while (_completedRequestIds.Count > RetainedRequestCount)
            {
                _surveyReports.Remove(_completedRequestIds[0]);
                _completedRequestIds.RemoveAt(0);
            }
        }

        private bool AddCancelledRequest(string requestId)
        {
            if (string.IsNullOrEmpty(requestId) || _cancelledRequestIds.Contains(requestId))
                return false;
            _cancelledRequestIds.Add(requestId);
            while (_cancelledRequestIds.Count > RetainedRequestCount)
                _cancelledRequestIds.RemoveAt(0);
            return true;
        }

        private string GetScopedRequestId(long producerId, string requestId)
        {
            return producerId.ToString(CultureInfo.InvariantCulture) + ":" + requestId;
        }

        private void ShowStatus()
        {
            Echo("Broker block ID: " + Me.EntityId);
            Echo((_retired ? "Retired. " : _paused ? "Paused. " : "Active. ") + "Tasks: " + _tasks.Count);
            foreach (var task in _tasks)
                Echo(task.Id + " " + task.State + " worker=" + task.WorkerId + " " + task.Key);
        }

        private void TransferBackup(bool import, bool retire = false)
        {
            var panel = GridTerminalSystem.GetBlockWithName(BackupPanelName) as IMyTextPanel;
            if (panel == null || !panel.IsWorking || panel.CubeGrid != Me.CubeGrid)
            {
                Echo("A working " + BackupPanelName + " text panel is required on this grid.");
                return;
            }

            if (!import)
            {
                var existing = panel.GetText();
                if (existing.Length != 0 && !existing.StartsWith(BackupVersion, StringComparison.Ordinal))
                {
                    Echo("Backup panel contains unrelated text.");
                    return;
                }
                var queueText = Storage.Replace("\r\n", "\n").Replace("\nRETIRED|1\n", "\n");
                var backup = BackupVersion + BackupChecksum(queueText) + "\n" + queueText;
                if (!panel.WriteText(backup, false) || panel.GetText().Replace("\r\n", "\n") != backup)
                    Echo("Backup write failed verification.");
                else if (retire)
                {
                    _retired = true;
                    SaveQueue();
                    Echo("Broker backup exported. Source is retired.");
                }
                else
                    Echo("Broker backup exported.");
                return;
            }

            if (_retired || _sequence != 0 || _tasks.Count != 0 || _completedRequestIds.Count != 0 ||
                _cancelledRequestIds.Count != 0 || _commits.Count != 0)
            {
                Echo("Import requires an empty broker.");
                return;
            }

            var snapshot = panel.GetText().Replace("\r\n", "\n");
            var separator = snapshot.IndexOf('\n');
            if (separator <= BackupVersion.Length || !snapshot.StartsWith(BackupVersion, StringComparison.Ordinal))
            {
                Echo("Unrecognized broker backup.");
                return;
            }
            var queue = snapshot.Substring(separator + 1);
            if (snapshot.Substring(BackupVersion.Length, separator - BackupVersion.Length) != BackupChecksum(queue) ||
                !queue.StartsWith("SEQ|", StringComparison.Ordinal) ||
                queue.IndexOf("\nPAUSED|1\n", StringComparison.Ordinal) < 0 ||
                queue.IndexOf("\nRETIRED|1\n", StringComparison.Ordinal) >= 0)
            {
                Echo("Broker backup failed verification.");
                return;
            }

            Storage = queue;
            LoadQueue();
            SaveAfterParentLeaseChange(false);
            Echo("Broker backup imported. Broker remains paused.");
        }

        private static string BackupChecksum(string value)
        {
            uint hash = 2166136261;
            foreach (var character in value)
                hash = unchecked((hash ^ character) * 16777619);
            return hash.ToString("X8", CultureInfo.InvariantCulture);
        }

        private void LoadQueue()
        {
            _tasks.Clear();
            _cancelledRequestIds.Clear();
            _completedRequestIds.Clear();
            _surveyReports.Clear();
            _commits.Clear();
            _sequence = 0;
            _paused = false;
            _retired = false;
            _resumeAfterUtc = -1;
            if (string.IsNullOrWhiteSpace(Storage))
                return;

            foreach (var line in Storage.Split('\n'))
            {
                var parts = line.Split('|');
                if (parts.Length == 2 && parts[0] == "PAUSED")
                {
                    _paused = parts[1].Trim() == "1";
                    continue;
                }
                if (parts.Length == 2 && parts[0] == "RETIRED")
                {
                    _retired = parts[1].Trim() == "1";
                    continue;
                }
                if (parts.Length == 2 && parts[0] == "RESUME_AFTER_UTC")
                {
                    long deadline;
                    if (long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out deadline) &&
                        deadline >= 0 && deadline <= DateTime.MaxValue.Ticks)
                        _resumeAfterUtc = deadline;
                    continue;
                }
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
                if ((parts.Length == 2 || parts.Length == 3) && parts[0] == "COMPLETE")
                {
                    try { AddCompletedRequest(TaskBusIgcHelper.DecodeStorage(parts[1]),
                        parts.Length == 3 ? TaskBusIgcHelper.DecodeStorage(parts[2]) : null); }
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
                if (parts[0] != "TASK" || (parts.Length != 9 && parts.Length != 12 && parts.Length != 13 && parts.Length != 14))
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
                    if (parts.Length >= 12)
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
                    long leaseExpiry = 0;
                    if (parts.Length == 14)
                        long.TryParse(parts[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out leaseExpiry);
                    _tasks.Add(new TaskRecord
                    {
                        Id = id,
                        RequestId = requestId,
                        GroupId = groupId,
                        RequesterId = requesterId,
                        WorkerId = workerId,
                        Key = resource,
                        RequestJson = requestJson,
                        HasParent = ReferencesParent(requestJson),
                        State = state,
                        Attempts = attempts,
                        LastDispatchTick = 0,
                        LeaseEpoch = leaseEpoch,
                        LeaseExpiresUtc = leaseExpiry,
                        BaseStockConfirmed = parts.Length >= 13 && parts[parts.Length - 1].Trim() == "1"
                    });
                }
                catch (FormatException)
                {
                    continue;
                }
            }
            _paused |= _retired;
            if (_paused && _resumeAfterUtc < 0)
                _resumeAfterUtc = DateTime.UtcNow.Ticks + LeaseTimeoutUtc;
        }

        private void SaveQueue()
        {
            var storage = new StringBuilder();
            storage.AppendLine("SEQ|" + _sequence.ToString(CultureInfo.InvariantCulture));
            if (_paused)
            {
                storage.AppendLine("PAUSED|1");
                storage.AppendLine("RESUME_AFTER_UTC|" + _resumeAfterUtc.ToString(CultureInfo.InvariantCulture));
            }
            if (_retired)
                storage.AppendLine("RETIRED|1");
            foreach (var requestId in _cancelledRequestIds)
                storage.AppendLine("CANCEL|" + TaskBusIgcHelper.EncodeStorage(requestId));
            foreach (var requestId in _completedRequestIds)
            {
                storage.Append("COMPLETE|").Append(TaskBusIgcHelper.EncodeStorage(requestId));
                string report;
                if (_surveyReports.TryGetValue(requestId, out report))
                    storage.Append('|').Append(TaskBusIgcHelper.EncodeStorage(report));
                storage.AppendLine();
            }
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
                    .Append(task.LeaseEpoch.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(task.LeaseExpiresUtc.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .AppendLine(task.BaseStockConfirmed ? "1" : "0");
            }
            Storage = storage.ToString();
        }

        #region PreludeFooter
    }
}
#endregion
