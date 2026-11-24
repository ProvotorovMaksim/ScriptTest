using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Libs.IgcCore;

namespace SpaceEngineers.Libs.TaskBusConsumer
{
    /// <summary>
    /// Callback for sending a pre-built JSON IGC envelope from the consumer.
    /// </summary>
    public delegate void ConsumerSendDelegate(string envelope, long targetId = 0);

    /// <summary>
    /// Callback for reading/writing PB Storage.
    /// </summary>
    public delegate string ConsumerGetStorageDelegate();
    public delegate void ConsumerSetStorageDelegate(string value);

    /// <summary>
    /// Callback for status output (Echo).
    /// </summary>
    public delegate void ConsumerEchoDelegate(string message);

    /// <summary>
    /// Reusable TaskBus consumer logic. Encapsulates task polling, lease handling,
    /// stage progression and durable state storage so that any PB program can
    /// become a consumer by delegating to this class.
    /// </summary>
    public sealed class TaskBusConsumer
    {
        private const int DefaultPollIntervalTicks = 30;

        private readonly string _communicationTag;
        private readonly int _pollIntervalTicks;
        private readonly ConsumerSendDelegate _send;
        private readonly ConsumerGetStorageDelegate _getStorage;
        private readonly ConsumerSetStorageDelegate _setStorage;
        private readonly ConsumerEchoDelegate _echo;
        private readonly long _entityId;
        private readonly string _gridName;

        private IMyBroadcastListener _listener;

        private string _consumerGroup = "default";
        private string _activeTaskId;
        private string _activeTaskJson;
        private readonly List<string> _stages = new List<string>();
        private int _stageIndex;
        private string _lastRevokedTaskId;
        private string _pendingCompletedTaskId;
        private int _leaseEpoch;
        private int _pendingCompletedEpoch;
        private int _tick;


        public TaskBusConsumer(
            string communicationTag,
            ConsumerSendDelegate send,
            ConsumerGetStorageDelegate getStorage,
            ConsumerSetStorageDelegate setStorage,
            ConsumerEchoDelegate echo,
            long entityId,
            string gridName,
            int pollIntervalTicks = DefaultPollIntervalTicks)
        {
            _communicationTag = communicationTag ?? throw new ArgumentNullException(nameof(communicationTag));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _getStorage = getStorage ?? throw new ArgumentNullException(nameof(getStorage));
            _setStorage = setStorage ?? throw new ArgumentNullException(nameof(setStorage));
            _echo = echo ?? throw new ArgumentNullException(nameof(echo));
            _entityId = entityId;
            _gridName = gridName ?? string.Empty;
            _pollIntervalTicks = pollIntervalTicks > 0 ? pollIntervalTicks : DefaultPollIntervalTicks;
        }

        /// <summary>
        /// Initializes the consumer: registers the IGC listener, creates the producer
        /// and loads durable state from Storage. Call once from the PB Program constructor.
        /// </summary>
        /// <param name="registerListener">Callback that registers an IGC broadcast listener.</param>
        public void Initialize(Func<string, IMyBroadcastListener> registerListener)
        {
            _listener = registerListener(_communicationTag);
            LoadState();
        }

        /// <summary>
        /// Processes an incoming IGC message. Returns true if the message was handled.
        /// </summary>
        public bool ProcessIncomingMessage(long sourceId, string json)
        {
            if (string.IsNullOrEmpty(json))
                return false;

            string eventType;
            string payloadJson;
            if (!TaskBusIgcHelper.TryParseEnvelope(json, out eventType, out payloadJson))
                return false;

            long targetId;
            string targetJson;
            if (TaskBusIgcHelper.TryGetJsonProperty(json, "target_id", out targetJson) &&
                long.TryParse(targetJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out targetId) &&
                targetId != 0 && targetId != _entityId)
                return false;

            switch (eventType)
            {
                case "TASK_ASSIGN":
                    ReceiveTask(payloadJson);
                    return true;
                case "QUEUE_EMPTY":
                    HandleEmptyQueue();
                    return true;
                case "TASK_DONE_ACK":
                    HandleDoneAck(payloadJson);
                    return true;
                case "TASK_CANCEL":
                case "TASK_REVOKED":
                    HandleCancel(payloadJson);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Processes a PB chat command. Call from Program.Main with the <c>argument</c> parameter.
        /// </summary>
        public void ProcessCommand(string command)
        {
            var cmd = (command ?? string.Empty).Trim();
            if (cmd == "status")
            {
                ShowStatus();
                return;
            }
            if (cmd.StartsWith("group ", StringComparison.OrdinalIgnoreCase))
            {
                SetConsumerGroup(cmd.Substring(6).Trim());
                return;
            }
            if (cmd.StartsWith("stage ", StringComparison.OrdinalIgnoreCase))
            {
                AdvanceStage(cmd.Substring(6).Trim());
                return;
            }
        }

        /// <summary>
        /// Called every tick. Increments the internal tick and polls the broker when due.
        /// </summary>
        public void Tick()
        {
            _tick++;
            if (_tick % _pollIntervalTicks == 0 || !string.IsNullOrEmpty(_pendingCompletedTaskId))
                PollBroker();
        }

        /// <summary>
        /// Returns a multi-line status string for Echo.
        /// </summary>
        public string GetStatus()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Consumer: " + _gridName);
            sb.AppendLine("Group: " + _consumerGroup);
            sb.AppendLine("Active task: " + (_activeTaskId ?? "none"));
            sb.AppendLine("Stage: " + (string.IsNullOrEmpty(_activeTaskId) ? "idle" : CurrentStage()));
            sb.AppendLine("Pending completion: " + (_pendingCompletedTaskId ?? "none"));
            sb.AppendLine("Last revoked: " + (_lastRevokedTaskId ?? "none"));
            sb.AppendLine("Lease epoch: " + _leaseEpoch);
            if (!string.IsNullOrEmpty(_activeTaskId))
                sb.AppendLine(GetActiveTaskSummary());
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Returns true if there is an active task being worked on.
        /// </summary>
        public bool HasActiveTask => !string.IsNullOrEmpty(_activeTaskId);

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
            if (!TaskBusIgcHelper.TryGetJsonProperty(taskJson, "task_id", out taskIdJson) ||
                !TaskBusIgcHelper.TryReadJsonString(taskIdJson, out taskId))
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
                    SendMessage("TASK_DONE", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(taskId) +
                        ",\"lease_epoch\":" + leaseEpoch.ToString(CultureInfo.InvariantCulture) + "}");
                    return;
                }
            }

            if (_activeTaskId != taskId)
            {
                if (!string.IsNullOrEmpty(_activeTaskId))
                {
                    _echo("Received another task while one is active; keeping current task " + _activeTaskId);
                    return;
                }

                _activeTaskId = taskId;
                _activeTaskJson = taskJson;
                LoadWorkflowStages(taskJson);
                _stageIndex = 0;
                _leaseEpoch = leaseEpoch;
                _lastRevokedTaskId = null;
                SaveState();
                _echo("Durably received " + taskId + ". Run stage: " + CurrentStage());
            }
            else if (leaseEpoch > _leaseEpoch)
            {
                _leaseEpoch = leaseEpoch;
                _activeTaskJson = taskJson;
                SaveState();
            }
            else if (leaseEpoch < _leaseEpoch)
                return;

            SendMessage("TASK_RECEIVED", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(taskId) +
                ",\"lease_epoch\":" + _leaseEpoch.ToString(CultureInfo.InvariantCulture) + "}");
        }

        private void AdvanceStage(string completedStage)
        {
            if (string.IsNullOrEmpty(_activeTaskId))
            {
                _echo("No active task. Waiting for broker poll.");
                return;
            }

            var expected = CurrentStage();
            if (!string.Equals(completedStage, expected, StringComparison.OrdinalIgnoreCase))
            {
                _echo("Current stage is " + expected + "; report: stage " + expected);
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
                SendMessage("TASK_DONE", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(_pendingCompletedTaskId) +
                    ",\"lease_epoch\":" + _pendingCompletedEpoch.ToString(CultureInfo.InvariantCulture) + "}");
                _echo("Workflow complete; waiting for broker commit ACK.");
            }
            else
            {
                SaveState();
                _echo("Stage saved. Next stage: " + CurrentStage());
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
                _echo("Set group only while idle: group <name>");
                return;
            }
            _consumerGroup = group;
            SaveState();
            _echo("Consumer group set to " + _consumerGroup);
        }

        private void HandleEmptyQueue()
        {
            _echo(string.IsNullOrEmpty(_activeTaskId) ? "No task available." : "Task remains active: " + _activeTaskId);
        }

        private void HandleDoneAck(string payloadJson)
        {
            string taskIdJson;
            string taskId;
            string epochJson;
            int leaseEpoch;
            if (string.IsNullOrEmpty(_pendingCompletedTaskId) ||
                !TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "task_id", out taskIdJson) ||
                !TaskBusIgcHelper.TryReadJsonString(taskIdJson, out taskId) ||
                !TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "lease_epoch", out epochJson) ||
                !int.TryParse(epochJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out leaseEpoch))
                return;

            if (taskId == _pendingCompletedTaskId && leaseEpoch == _pendingCompletedEpoch)
            {
                _pendingCompletedTaskId = null;
                _pendingCompletedEpoch = 0;
                SaveState();
                _echo("Broker committed " + taskId);
            }
        }

        private void HandleCancel(string payloadJson)
        {
            string taskIdJson;
            string taskId;
            if (!TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "task_id", out taskIdJson) ||
                !TaskBusIgcHelper.TryReadJsonString(taskIdJson, out taskId) ||
                taskId != _activeTaskId)
                return;

            _lastRevokedTaskId = taskId;
            _activeTaskId = null;
            _activeTaskJson = null;
            _leaseEpoch = 0;
            SaveState();
            _echo("Lease fenced for " + taskId + ". Do not continue work under the revoked lease.");
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
            _echo(GetStatus());
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

            var storage = _getStorage();
            if (string.IsNullOrWhiteSpace(storage))
                return;

            foreach (var line in storage.Split('\n'))
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
            _setStorage(storage.ToString());
        }

        private void SendMessage(string eventType, string payloadJson, long targetId = 0, string messageId = null)
        {
            var envelope = TaskBusProtocol.CreateEnvelope(eventType, payloadJson, messageId, targetId);
            _send(envelope, targetId);
        }

        /// <summary>
        /// Pumps all pending IGC messages through the consumer. Call from Program.Main each tick.
        /// </summary>
        public void PumpMessages()
        {
            while (_listener.HasPendingMessage)
            {
                var message = _listener.AcceptMessage();
                if (message.Source == _entityId)
                    continue;
                var json = message.Data as string;
                ProcessIncomingMessage(message.Source, json);
            }
        }
    }
}