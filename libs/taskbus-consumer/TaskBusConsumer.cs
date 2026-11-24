using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
        private const int LeaseConfirmationTimeoutTicks = 120;

        private readonly int _pollIntervalTicks;
        private readonly ConsumerSendDelegate _send;
        private readonly ConsumerGetStorageDelegate _getStorage;
        private readonly ConsumerSetStorageDelegate _setStorage;
        private readonly ConsumerEchoDelegate _echo;
        private readonly long _entityId;
        private readonly string _gridName;

        private long _brokerId;
        private bool _autoPoint;
        private string _completedChildRequest;
        private string _childState;
        private string _oreSite;
        private string _oreApproach;
        private bool _baseStockConfirmed;
        private string _consumerGroup = "default";
        private string _activeTaskId;
        private string _activeTaskJson;
        private readonly List<string> _stages = new List<string>();
        private int _stageIndex;
        private string _lastRevokedTaskId;
        private int _lastRevokedEpoch = -1;
        private string _pendingCompletedTaskId;
        private int _leaseEpoch;
        private int _pendingCompletedEpoch;
        private int _tick;
        private int? _lastLeaseConfirmationTick;


        public TaskBusConsumer(
            ConsumerSendDelegate send,
            ConsumerGetStorageDelegate getStorage,
            ConsumerSetStorageDelegate setStorage,
            ConsumerEchoDelegate echo,
            long entityId,
            string gridName,
            int pollIntervalTicks = DefaultPollIntervalTicks)
        {
            if (send == null) throw new ArgumentNullException(nameof(send));
            _send = send;
            if (getStorage == null) throw new ArgumentNullException(nameof(getStorage));
            _getStorage = getStorage;
            if (setStorage == null) throw new ArgumentNullException(nameof(setStorage));
            _setStorage = setStorage;
            if (echo == null) throw new ArgumentNullException(nameof(echo));
            _echo = echo;
            _entityId = entityId;
            _gridName = gridName ?? string.Empty;
            _pollIntervalTicks = pollIntervalTicks > 0 ? pollIntervalTicks : DefaultPollIntervalTicks;
            LoadState();
        }

        /// <summary>
        /// Processes an incoming IGC message. Returns true if the message was handled.
        /// </summary>
        public bool ProcessIncomingMessage(long sourceId, string json)
        {
            if ((_brokerId != 0 && sourceId != _brokerId) || string.IsNullOrEmpty(json))
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
                case "TASK_PUBLISHED_ACK":
                    return HandleChildAcknowledgement(payloadJson);
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
            if (cmd == "point auto" || cmd == "point manual")
            {
                if (HasActiveTask || !string.IsNullOrEmpty(_pendingCompletedTaskId))
                    _echo("Change point mode only while idle.");
                else
                {
                    _autoPoint = cmd == "point auto";
                    SaveState();
                }
                return;
            }
            if (cmd.StartsWith("broker ", StringComparison.OrdinalIgnoreCase))
            {
                long brokerId;
                if (!long.TryParse(cmd.Substring(7).Trim(), out brokerId) || brokerId < 0)
                    _echo("Usage: broker <block ID>, or broker 0 for broadcast.");
                else if (HasActiveTask || !string.IsNullOrEmpty(_pendingCompletedTaskId))
                    _echo("Change broker only after the current task and completion are resolved.");
                else if (_brokerId != brokerId)
                {
                    _brokerId = brokerId;
                    _lastRevokedTaskId = null;
                    _lastRevokedEpoch = -1;
                    SaveState();
                }
                return;
            }
            if (cmd.StartsWith("group ", StringComparison.OrdinalIgnoreCase))
            {
                SetConsumerGroup(cmd.Substring(6).Trim());
                return;
            }
            if (cmd.StartsWith("site ", StringComparison.OrdinalIgnoreCase) ||
                cmd.StartsWith("approach ", StringComparison.OrdinalIgnoreCase))
            {
                SetSurveyWaypoint(cmd);
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
        public void Tick(bool canPoll = true)
        {
            _tick++;
            if (!canPoll)
            {
                _lastLeaseConfirmationTick = null;
                return;
            }
            if (_tick % _pollIntervalTicks == 0 || !string.IsNullOrEmpty(_pendingCompletedTaskId))
                PollBroker();
            if (_tick % _pollIntervalTicks == 0 && IsAutomaticPoint && HasRecentLeaseConfirmation)
                PublishCurrentChild();
        }

        /// <summary>
        /// Returns a multi-line status string for Echo.
        /// </summary>
        public string GetStatus()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Consumer: " + _gridName);
            sb.AppendLine("Group: " + _consumerGroup);
            sb.AppendLine("Broker: " + (_brokerId == 0 ? "broadcast" : _brokerId.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine("Active task: " + (_activeTaskId ?? "none"));
            sb.AppendLine("Stage: " + (string.IsNullOrEmpty(_activeTaskId) ? "idle" : CurrentStage()));
            if (IsAutomaticPoint)
            {
                sb.AppendLine("Child request: " + CurrentChildRequestId());
                sb.AppendLine("Child status: " + (_childState ?? "awaiting broker"));
                if (CurrentStage() == "transport" && AssignmentRequires("receipt_required"))
                    sb.AppendLine(_baseStockConfirmed ? "Base stock confirmed" : "Waiting for base stock confirmation");
                if (_completedChildRequest == CurrentChildRequestId())
                    sb.AppendLine("Child task complete. Confirm stage.");
            }
            sb.AppendLine("Pending completion: " + (_pendingCompletedTaskId ?? "none"));
            sb.AppendLine("Last revoked: " + (_lastRevokedTaskId ?? "none"));
            sb.AppendLine("Lease epoch: " + _leaseEpoch);
            if (!string.IsNullOrEmpty(_activeTaskId))
            {
                sb.AppendLine(HasRecentLeaseConfirmation ? "Lease confirmed" : "Waiting for lease confirmation");
                sb.AppendLine(GetActiveTaskSummary());
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Returns true if there is an active task being worked on.
        /// </summary>
        public bool HasActiveTask => !string.IsNullOrEmpty(_activeTaskId);

        private bool HasRecentLeaseConfirmation => _lastLeaseConfirmationTick.HasValue &&
            _tick - _lastLeaseConfirmationTick.Value < LeaseConfirmationTimeoutTicks;

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

            if (taskId == _lastRevokedTaskId && leaseEpoch <= _lastRevokedEpoch)
                return;

            if (!string.IsNullOrEmpty(_pendingCompletedTaskId))
            {
                if (_pendingCompletedTaskId == taskId && _pendingCompletedEpoch == leaseEpoch)
                {
                    SendMessage("TASK_DONE", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(taskId) +
                        ",\"lease_epoch\":" + leaseEpoch.ToString(CultureInfo.InvariantCulture) + "}");
                }
                return;
            }

            if (_activeTaskId != taskId)
            {
                if (!string.IsNullOrEmpty(_activeTaskId))
                {
                    _echo("Received another task while one is active; keeping current task " + _activeTaskId);
                    return;
                }

                _activeTaskId = taskId;
                _completedChildRequest = null;
                _childState = null;
                _oreSite = null;
                _oreApproach = null;
                _activeTaskJson = taskJson;
                LoadWorkflowStages(taskJson);
                _stageIndex = 0;
                _leaseEpoch = leaseEpoch;
                SaveState();
                _echo("Durably received " + taskId + ". Run stage: " + CurrentStage());
            }
            else if (leaseEpoch > _leaseEpoch)
            {
                _leaseEpoch = leaseEpoch;
                _completedChildRequest = null;
                _childState = null;
                _activeTaskJson = taskJson;
                SaveState();
            }
            else if (leaseEpoch < _leaseEpoch)
                return;

            string confirmation;
            _baseStockConfirmed = TaskBusIgcHelper.TryGetJsonProperty(taskJson, "base_stock_confirmed", out confirmation) &&
                confirmation == "true";
            _lastLeaseConfirmationTick = _tick;
            SendMessage("TASK_RECEIVED", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(taskId) +
                ",\"lease_epoch\":" + _leaseEpoch.ToString(CultureInfo.InvariantCulture) + "}");
        }

        private bool IsAutomaticPoint => _autoPoint && _consumerGroup == "MiningPoints" && HasActiveTask;

        private string CurrentChildRequestId() => _activeTaskId + ":" +
            _leaseEpoch.ToString(CultureInfo.InvariantCulture) + ":" + _stageIndex.ToString(CultureInfo.InvariantCulture);

        private void SetSurveyWaypoint(string command)
        {
            if (!IsAutomaticPoint || CurrentStage() != "survey" || !HasRecentLeaseConfirmation)
            {
                _echo("Set a waypoint during the confirmed survey stage.");
                return;
            }

            int separator = command.IndexOf(' ');
            var gps = command.Substring(separator + 1).Trim();
            string name;
            double x;
            double y;
            double z;
            if (!TaskBusIgcHelper.TryReadOreGps(gps, out name, out x, out y, out z) || name.Length > 64)
            {
                _echo("Use site GPS:Ore:X:Y:Z: or approach GPS:Name:X:Y:Z:.");
                return;
            }

            var position = "{\"x\":" + x.ToString("R", CultureInfo.InvariantCulture) +
                ",\"y\":" + y.ToString("R", CultureInfo.InvariantCulture) +
                ",\"z\":" + z.ToString("R", CultureInfo.InvariantCulture) + "}";
            bool isSite = separator == 4;
            if (isSite)
            {
                string details;
                string raw;
                string resource;
                if (TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out details) &&
                    TaskBusIgcHelper.TryGetJsonProperty(details, "resource", out raw) &&
                    TaskBusIgcHelper.TryReadJsonString(raw, out resource) &&
                    resource.StartsWith("Ore/", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, resource.Substring(4), StringComparison.OrdinalIgnoreCase))
                {
                    _echo("The selected deposit does not match the requested ore.");
                    return;
                }
            }
            if (position == (isSite ? _oreApproach : _oreSite))
            {
                _echo("Choose distinct ore and approach positions.");
                return;
            }
            if (isSite)
                _oreSite = position;
            else
                _oreApproach = position;
            SaveState();
        }

        private void PublishCurrentChild()
        {
            string group;
            string childStage;
            switch (CurrentStage())
            {
                case "survey": group = "Scouts"; childStage = "survey"; break;
                case "mine": group = "MiningFleet"; childStage = "mine"; break;
                case "transport": group = "Haulers"; childStage = "deliver"; break;
                default: return;
            }

            var requestId = CurrentChildRequestId();
            if (_completedChildRequest == requestId)
                return;

            string details;
            if (!TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out details))
                return;

            var payload = new StringBuilder("{\"request_id\":");
            payload.Append(TaskBusIgcHelper.ToJsonString(requestId))
                .Append(",\"group_id\":").Append(TaskBusIgcHelper.ToJsonString(group))
                .Append(",\"parent_task_id\":").Append(TaskBusIgcHelper.ToJsonString(_activeTaskId))
                .Append(",\"parent_lease_epoch\":").Append(_leaseEpoch.ToString(CultureInfo.InvariantCulture))
                .Append(",\"steps\":[").Append(TaskBusIgcHelper.ToJsonString(childStage)).Append(']');
            foreach (var field in new[] { "key", "resource", "amount", "base_grid_id", "base_grid_name", "base_position" })
            {
                string value;
                if (TaskBusIgcHelper.TryGetJsonProperty(details, field, out value))
                    payload.Append(',').Append(TaskBusIgcHelper.ToJsonString(field)).Append(':').Append(value);
            }
            if (CurrentStage() == "mine" && _oreSite != null && _oreApproach != null)
                payload.Append(",\"ore_position\":").Append(_oreSite)
                    .Append(",\"approach_position\":").Append(_oreApproach);
            SendMessage("TASK_PUBLISH", payload.Append('}').ToString());
        }

        private bool HandleChildAcknowledgement(string payload)
        {
            string value;
            string request;
            string state;
            if (!IsAutomaticPoint ||
                !TaskBusIgcHelper.TryGetJsonProperty(payload, "request_id", out value) ||
                !TaskBusIgcHelper.TryReadJsonString(value, out request) || request != CurrentChildRequestId() ||
                !TaskBusIgcHelper.TryGetJsonProperty(payload, "state", out value) ||
                !TaskBusIgcHelper.TryReadJsonString(value, out state))
                return false;

            _childState = state;
            if (state == "DONE")
                _completedChildRequest = request;
            return true;
        }

        private void AdvanceStage(string completedStage)
        {
            if (string.IsNullOrEmpty(_activeTaskId))
            {
                _echo("No active task. Waiting for broker poll.");
                return;
            }

            if (!HasRecentLeaseConfirmation)
            {
                _echo("Waiting for lease confirmation from the broker. Stage was not advanced.");
                return;
            }

            var expected = CurrentStage();
            if (!string.Equals(completedStage, expected, StringComparison.OrdinalIgnoreCase))
            {
                _echo("Current stage is " + expected + "; report: stage " + expected);
                return;
            }

            if (IsAutomaticPoint && _completedChildRequest != CurrentChildRequestId())
            {
                _echo("Waiting for the broker to confirm the child task is complete.");
                return;
            }

            if (IsAutomaticPoint && expected == "transport" && AssignmentRequires("receipt_required") && !_baseStockConfirmed)
            {
                _echo("Waiting for stock confirmation from the requesting base.");
                return;
            }

            if (IsAutomaticPoint && expected == "survey" && AssignmentRequires("site_required") &&
                (_oreSite == null || _oreApproach == null))
            {
                _echo("Save both the ore site and an approach point before confirming survey.");
                return;
            }

            _stageIndex++;
            _childState = null;
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

        private bool AssignmentRequires(string field)
        {
            string details;
            string value;
            return TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out details) &&
                TaskBusIgcHelper.TryGetJsonProperty(details, field, out value) && value == "true";
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
            string epochJson;
            int leaseEpoch;
            if (!TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "task_id", out taskIdJson) ||
                !TaskBusIgcHelper.TryReadJsonString(taskIdJson, out taskId) ||
                !TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "lease_epoch", out epochJson) ||
                !int.TryParse(epochJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out leaseEpoch))
                return;

            if (!string.IsNullOrEmpty(_pendingCompletedTaskId) &&
                taskId == _pendingCompletedTaskId && leaseEpoch == _pendingCompletedEpoch)
            {
                _pendingCompletedTaskId = null;
                _pendingCompletedEpoch = 0;
                _lastRevokedTaskId = taskId;
                _lastRevokedEpoch = leaseEpoch;
                SaveState();
                _echo("Broker revoked the completion lease for " + taskId);
                return;
            }

            if (taskId != _activeTaskId || leaseEpoch != _leaseEpoch)
                return;

            _lastRevokedTaskId = taskId;
            _lastRevokedEpoch = leaseEpoch;
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
            string details;
            if (!TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "key", out keyJson) ||
                !TaskBusIgcHelper.TryReadJsonString(keyJson, out key) ||
                !TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out details))
                return _activeTaskJson;

            string resourceJson;
            string resource;
            double amount;
            if (!TaskBusIgcHelper.TryGetJsonProperty(details, "resource", out resourceJson) ||
                !TaskBusIgcHelper.TryReadJsonString(resourceJson, out resource) ||
                !TaskBusIgcHelper.TryReadFiniteNumber(details, "amount", out amount) || amount <= 0)
                return key + " details=" + details;

            var summary = new StringBuilder();
            summary.AppendLine("Resource: " + resource);
            summary.Append("Requested amount: " + amount.ToString("G", CultureInfo.InvariantCulture));

            string baseNameJson;
            string baseName;
            if (TaskBusIgcHelper.TryGetJsonProperty(details, "base_grid_name", out baseNameJson) &&
                TaskBusIgcHelper.TryReadJsonString(baseNameJson, out baseName))
                summary.Append("\nDelivery base: " + baseName);

            AppendPosition(summary, details, "base_position", "Base at request time");
            AppendPosition(summary, details, "approach_position", "Mining approach");
            AppendPosition(summary, details, "ore_position", "Ore deposit");
            return summary.ToString();
        }

        private static void AppendPosition(StringBuilder summary, string details, string field, string label)
        {
            string position;
            double x;
            double y;
            double z;
            if (TaskBusIgcHelper.TryGetJsonProperty(details, field, out position) &&
                TaskBusIgcHelper.TryReadFiniteNumber(position, "x", out x) &&
                TaskBusIgcHelper.TryReadFiniteNumber(position, "y", out y) &&
                TaskBusIgcHelper.TryReadFiniteNumber(position, "z", out z))
            {
                var culture = CultureInfo.InvariantCulture;
                summary.Append("\n" + label + " (X, Y, Z): " +
                    x.ToString("0.##", culture) + ", " + y.ToString("0.##", culture) + ", " + z.ToString("0.##", culture));
            }
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
                        List<string> stages;
                        if (TaskBusProtocol.TryReadStringArray(TaskBusIgcHelper.DecodeStorage(value), out stages))
                            _stages.AddRange(stages);
                    }
                    else if (key == "ORE_SITE") _oreSite = TaskBusIgcHelper.DecodeStorage(value);
                    else if (key == "ORE_APPROACH") _oreApproach = TaskBusIgcHelper.DecodeStorage(value);
                    else if (key == "POINT_AUTO") _autoPoint = value.Trim() == "1";
                    else if (key == "BROKER") long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _brokerId);
                    else if (key == "GROUP") _consumerGroup = TaskBusIgcHelper.DecodeStorage(value);
                    else if (key == "REVOKED_EPOCH")
                    {
                        int revokedEpoch;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out revokedEpoch))
                            _lastRevokedEpoch = revokedEpoch;
                    }
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
                if (_oreSite != null) storage.AppendLine("ORE_SITE|" + TaskBusIgcHelper.EncodeStorage(_oreSite));
                if (_oreApproach != null) storage.AppendLine("ORE_APPROACH|" + TaskBusIgcHelper.EncodeStorage(_oreApproach));
                storage.AppendLine("LEASE_EPOCH|" + _leaseEpoch.ToString(CultureInfo.InvariantCulture));
            }
            if (_autoPoint) storage.AppendLine("POINT_AUTO|1");
            storage.AppendLine("BROKER|" + _brokerId.ToString(CultureInfo.InvariantCulture));
            storage.AppendLine("GROUP|" + TaskBusIgcHelper.EncodeStorage(_consumerGroup ?? "default"));
            if (!string.IsNullOrEmpty(_lastRevokedTaskId))
            {
                storage.AppendLine("REVOKED|" + TaskBusIgcHelper.EncodeStorage(_lastRevokedTaskId));
                storage.AppendLine("REVOKED_EPOCH|" + _lastRevokedEpoch.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(_pendingCompletedTaskId))
            {
                storage.AppendLine("COMPLETED|" + TaskBusIgcHelper.EncodeStorage(_pendingCompletedTaskId));
                storage.AppendLine("COMPLETED_EPOCH|" + _pendingCompletedEpoch.ToString(CultureInfo.InvariantCulture));
            }
            _setStorage(storage.ToString());
        }

        private void SendMessage(string eventType, string payloadJson)
        {
            var envelope = TaskBusProtocol.CreateEnvelope(eventType, payloadJson, targetId: _brokerId);
            _send(envelope, _brokerId);
        }

    }
}
