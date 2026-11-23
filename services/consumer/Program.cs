#region Prelude
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using SpaceEngineers.CommonLibs;

namespace SpaceEngineers.Services.Consumer
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private const int PollIntervalTicks = 30;
        private string _gridName;
        private string _consumerGroup;
        private string _activeTaskId;
        private string _activeTaskJson;
        private readonly List<string> _stages = new List<string>();
        private int _stageIndex;
        private string _lastRevokedTaskId;
        private string _pendingCompletedTaskId;
        private int _leaseEpoch;
        private int _pendingCompletedEpoch;
        private int _tick;
        private IMyBroadcastListener _listener;
        private TaskBusProducer _producer;
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
            _gridName = Me.CubeGrid.CustomName;
            _listener = IGC.RegisterBroadcastListener(CommunicationTag);
            _producer = new TaskBusProducer(CommunicationTag, (channel, envelope, targetId) =>
                IGC.SendBroadcastMessage(channel, envelope, TransmissionDistance.TransmissionDistanceMax));
            LoadState();
        }

        public void Main(string argument)
        {
            _tick++;
            var command = (argument ?? string.Empty).Trim();
            if (command == "status")
                ShowStatus();
            else if (command.StartsWith("group ", StringComparison.OrdinalIgnoreCase))
                SetConsumerGroup(command.Substring(6).Trim());
            else if (command.StartsWith("stage ", StringComparison.OrdinalIgnoreCase))
                AdvanceStage(command.Substring(6).Trim());

            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas, antenna => antenna.IsFunctional && antenna.Enabled);
            if (_antennas.Count == 0)
            {
                Echo("Enable a functional radio antenna to connect to the broker.");
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

                long targetId;
                string targetJson;
                if (TaskBusIgcHelper.TryGetJsonProperty(json, "target_id", out targetJson) &&
                    long.TryParse(targetJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out targetId) &&
                    targetId != 0 && targetId != Me.EntityId)
                    continue;

                if (eventType == "TASK_ASSIGN")
                    ReceiveTask(payloadJson);
                else if (eventType == "QUEUE_EMPTY")
                    HandleEmptyQueue();
                else if (eventType == "TASK_DONE_ACK")
                    HandleDoneAck(payloadJson);
                else if (eventType == "TASK_CANCEL" || eventType == "TASK_REVOKED")
                    HandleCancel(payloadJson);
            }

            if (_tick % PollIntervalTicks == 0 || !string.IsNullOrEmpty(_pendingCompletedTaskId))
                PollBroker();

            if (!string.IsNullOrEmpty(_activeTaskId))
                Echo("Task " + _activeTaskId + " / " + CurrentStage() + ": " + GetActiveTaskSummary());
        }

        private void PollBroker()
        {
            var payload = "{\"group_id\":" + TaskBusIgcHelper.ToJsonString(_consumerGroup) +
                ",\"consumer_grid\":" + TaskBusIgcHelper.ToJsonString(_gridName) +
                ",\"active_task_id\":" + (string.IsNullOrEmpty(_activeTaskId) ? "null" : TaskBusIgcHelper.ToJsonString(_activeTaskId)) +
                ",\"active_lease_epoch\":" + _leaseEpoch.ToString(CultureInfo.InvariantCulture) +
                ",\"completed_task_id\":" + (string.IsNullOrEmpty(_pendingCompletedTaskId) ? "null" : TaskBusIgcHelper.ToJsonString(_pendingCompletedTaskId)) +
                ",\"lease_epoch\":" + _pendingCompletedEpoch.ToString(CultureInfo.InvariantCulture) + "}";
            SendMessage("TASK_POLL", payload);
        }

        private void ReceiveTask(string taskJson)
        {
            string taskIdJson;
            string taskId;
            if (!TaskBusIgcHelper.TryGetJsonProperty(taskJson, "task_id", out taskIdJson) || !TaskBusIgcHelper.TryReadJsonString(taskIdJson, out taskId))
                return;

            string epochJson;
            int leaseEpoch;
            if (!TaskBusIgcHelper.TryGetJsonProperty(taskJson, "lease_epoch", out epochJson) ||
                !int.TryParse(epochJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out leaseEpoch))
                return;

            if (!string.IsNullOrEmpty(_pendingCompletedTaskId))
            {
                if (_pendingCompletedTaskId == taskId && _pendingCompletedEpoch == leaseEpoch)
                {
                    SendMessage("TASK_DONE", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(taskId) + ",\"lease_epoch\":" + leaseEpoch.ToString(CultureInfo.InvariantCulture) + "}");
                    return;
                }
            }

            if (_activeTaskId != taskId)
            {
                if (!string.IsNullOrEmpty(_activeTaskId))
                {
                    Echo("Received another task while one is active; keeping current task " + _activeTaskId);
                    return;
                }

                _activeTaskId = taskId;
                _activeTaskJson = taskJson;
                LoadWorkflowStages(taskJson);
                _stageIndex = 0;
                _leaseEpoch = leaseEpoch;
                _lastRevokedTaskId = null;
                SaveState();
                Echo("Durably received " + taskId + ". Run stage: " + CurrentStage());
            }
            else if (leaseEpoch > _leaseEpoch)
            {
                _leaseEpoch = leaseEpoch;
                _activeTaskJson = taskJson;
                SaveState();
            }
            else if (leaseEpoch < _leaseEpoch)
                return;

            SendMessage("TASK_RECEIVED", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(taskId) + ",\"lease_epoch\":" + _leaseEpoch.ToString(CultureInfo.InvariantCulture) + "}");
        }

        private void AdvanceStage(string completedStage)
        {
            if (string.IsNullOrEmpty(_activeTaskId))
            {
                Echo("No active task. Waiting for broker poll.");
                return;
            }

            var expected = CurrentStage();
            if (!string.Equals(completedStage, expected, StringComparison.OrdinalIgnoreCase))
            {
                Echo("Current stage is " + expected + "; report: stage " + expected);
                return;
            }

            _stageIndex++;
            if (_stageIndex >= _stages.Count)
            {
                _pendingCompletedTaskId = _activeTaskId;
                _pendingCompletedEpoch = _leaseEpoch;
                _activeTaskId = null;
                _activeTaskJson = null;
                _stages.Clear();
                _stageIndex = 0;
                SaveState();
                SendMessage("TASK_DONE", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(_pendingCompletedTaskId) + ",\"lease_epoch\":" + _pendingCompletedEpoch.ToString(CultureInfo.InvariantCulture) + "}");
                Echo("Workflow complete; waiting for broker commit ACK.");
            }
            else
            {
                SaveState();
                Echo("Stage saved. Next stage: " + CurrentStage());
            }
        }

        private string CurrentStage()
        {
            return _stageIndex >= 0 && _stageIndex < _stages.Count ? _stages[_stageIndex] : "execute";
        }

        private void LoadWorkflowStages(string assignmentJson)
        {
            _stages.Clear();
            string detailsJson;
            string stepsJson;
            List<string> steps;
            if (TaskBusIgcHelper.TryGetJsonProperty(assignmentJson, "details", out detailsJson) &&
                TaskBusProtocol.TryGetProperty(detailsJson, "steps", out stepsJson) &&
                TaskBusProtocol.TryReadStringArray(stepsJson, out steps))
                _stages.AddRange(steps);
            if (_stages.Count == 0)
                _stages.Add("execute");
        }

        private void SetConsumerGroup(string group)
        {
            if (string.IsNullOrWhiteSpace(group) || !string.IsNullOrEmpty(_activeTaskId))
            {
                Echo("Set group only while idle: group <name>");
                return;
            }
            _consumerGroup = group;
            SaveState();
            Echo("Consumer group set to " + _consumerGroup);
        }

        private void HandleEmptyQueue()
        {
            Echo(string.IsNullOrEmpty(_activeTaskId) ? "No task available." : "Task remains active: " + _activeTaskId);
        }

        private void HandleDoneAck(string payloadJson)
        {
            string taskIdJson;
            string taskId;
            string epochJson;
            int leaseEpoch;
            if (string.IsNullOrEmpty(_pendingCompletedTaskId) ||
                !TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "task_id", out taskIdJson) || !TaskBusIgcHelper.TryReadJsonString(taskIdJson, out taskId) ||
                !TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "lease_epoch", out epochJson) ||
                !int.TryParse(epochJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out leaseEpoch))
                return;

            if (taskId == _pendingCompletedTaskId && leaseEpoch == _pendingCompletedEpoch)
            {
                _pendingCompletedTaskId = null;
                _pendingCompletedEpoch = 0;
                SaveState();
                Echo("Broker committed " + taskId);
            }
        }

        private void HandleCancel(string payloadJson)
        {
            string taskIdJson;
            string taskId;
            if (!TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "task_id", out taskIdJson) || !TaskBusIgcHelper.TryReadJsonString(taskIdJson, out taskId) || taskId != _activeTaskId)
                return;

            _lastRevokedTaskId = taskId;
            _activeTaskId = null;
            _activeTaskJson = null;
            _leaseEpoch = 0;
            SaveState();
            Echo("Lease fenced for " + taskId + ". Do not continue work under the revoked lease.");
        }

        private string GetActiveTaskSummary()
        {
            if (string.IsNullOrEmpty(_activeTaskJson))
                return "task payload unavailable";

            string keyJson;
            string key;
            string amountJson;
            if (TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "key", out keyJson) &&
                TaskBusIgcHelper.TryReadJsonString(keyJson, out key) &&
                TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out amountJson))
                return key + " details=" + amountJson;

            return _activeTaskJson;
        }

        private void ShowStatus()
        {
            Echo("Consumer: " + _gridName);
            Echo("Group: " + _consumerGroup);
            Echo("Active task: " + (_activeTaskId ?? "none"));
            Echo("Stage: " + (string.IsNullOrEmpty(_activeTaskId) ? "idle" : CurrentStage()));
            Echo("Pending completion: " + (_pendingCompletedTaskId ?? "none"));
            Echo("Last revoked: " + (_lastRevokedTaskId ?? "none"));
            Echo("Lease epoch: " + _leaseEpoch);
            if (!string.IsNullOrEmpty(_activeTaskId))
                Echo(GetActiveTaskSummary());
        }

        private void LoadState()
        {
            _activeTaskId = null;
            _activeTaskJson = null;
            _consumerGroup = "default";
            _stages.Clear();
            _stageIndex = 0;
            _lastRevokedTaskId = null;
            _pendingCompletedTaskId = null;
            _leaseEpoch = 0;
            _pendingCompletedEpoch = 0;
            if (string.IsNullOrWhiteSpace(Storage))
                return;

            foreach (var line in Storage.Split('\n'))
            {
                var separator = line.IndexOf('|');
                if (separator <= 0)
                    continue;

                var key = line.Substring(0, separator);
                var value = line.Substring(separator + 1);
                try
                {
                    if (key == "ACTIVE_ID") _activeTaskId = TaskBusIgcHelper.DecodeStorage(value);
                    else if (key == "ACTIVE_JSON") _activeTaskJson = TaskBusIgcHelper.DecodeStorage(value);
                    else if (key == "STAGE_INDEX") int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _stageIndex);
                    else if (key == "STAGES")
                    {
                        if (TaskBusProtocol.TryReadStringArray(TaskBusIgcHelper.DecodeStorage(value), out var stages))
                            _stages.AddRange(stages);
                    }
                    else if (key == "GROUP") _consumerGroup = TaskBusIgcHelper.DecodeStorage(value);
                    else if (key == "REVOKED") _lastRevokedTaskId = TaskBusIgcHelper.DecodeStorage(value);
                    else if (key == "COMPLETED") _pendingCompletedTaskId = TaskBusIgcHelper.DecodeStorage(value);
                    else if (key == "LEASE_EPOCH") int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _leaseEpoch);
                    else if (key == "COMPLETED_EPOCH") int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _pendingCompletedEpoch);
                }
                catch (FormatException)
                {
                    _activeTaskId = null;
                    _activeTaskJson = null;
                    _stages.Clear();
                    _stageIndex = 0;
                    _lastRevokedTaskId = null;
                    _pendingCompletedTaskId = null;
                    return;
                }
            }
        }

        private void SaveState()
        {
            var storage = new StringBuilder();
            if (!string.IsNullOrEmpty(_activeTaskId))
            {
                storage.AppendLine("ACTIVE_ID|" + TaskBusIgcHelper.EncodeStorage(_activeTaskId));
                storage.AppendLine("ACTIVE_JSON|" + TaskBusIgcHelper.EncodeStorage(_activeTaskJson ?? string.Empty));
                storage.AppendLine("STAGE_INDEX|" + _stageIndex.ToString(CultureInfo.InvariantCulture));
                var stagesJson = "[" + string.Join(",", _stages.Select(TaskBusIgcHelper.ToJsonString).ToArray()) + "]";
                storage.AppendLine("STAGES|" + TaskBusIgcHelper.EncodeStorage(stagesJson));
                storage.AppendLine("LEASE_EPOCH|" + _leaseEpoch.ToString(CultureInfo.InvariantCulture));
            }
            storage.AppendLine("GROUP|" + TaskBusIgcHelper.EncodeStorage(_consumerGroup ?? "default"));
            if (!string.IsNullOrEmpty(_lastRevokedTaskId))
                storage.AppendLine("REVOKED|" + TaskBusIgcHelper.EncodeStorage(_lastRevokedTaskId));
            if (!string.IsNullOrEmpty(_pendingCompletedTaskId))
            {
                storage.AppendLine("COMPLETED|" + TaskBusIgcHelper.EncodeStorage(_pendingCompletedTaskId));
                storage.AppendLine("COMPLETED_EPOCH|" + _pendingCompletedEpoch.ToString(CultureInfo.InvariantCulture));
            }
            Storage = storage.ToString();
        }

        private void SendMessage(string eventType, string payloadJson)
        {
            _producer.Publish(eventType, payloadJson);
        }

        #region PreludeFooter
    }
}
#endregion
