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
        private int _surveyAttempt;
        private string _childSurveyReport;
        private string _pendingSurveyReport;
        private string _childState;
        private string _oreSite;
        private string _oreApproach;
        private readonly Dictionary<string, string> _knownSites =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _baseStockConfirmed;
        private readonly bool _miningPlatform;
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
        private long _leaseExpiresUtc;
        private long _leaseConfirmationSource;


        private readonly Func<string> _getGridPositionJson;
        private readonly Func<string, double?> _readHaulerCargo;
        private double? _pickupCargo;
        private readonly long _gridId;
        private readonly Func<long> _readDockedGrid;
        private readonly bool _autoHauler;
        private readonly Action<long, string, double> _unloadHaulerCargo;
        private readonly Action<long, string, double> _loadHaulerCargo;
        private readonly Func<string, double?> _readMiningBuffer;

        public TaskBusConsumer(
            ConsumerSendDelegate send,
            ConsumerGetStorageDelegate getStorage,
            ConsumerSetStorageDelegate setStorage,
            ConsumerEchoDelegate echo,
            long entityId,
            string gridName,
            int pollIntervalTicks = DefaultPollIntervalTicks, bool miningPlatform = false, Func<string> getGridPositionJson = null,
            Func<string, double?> readHaulerCargo = null, long gridId = 0, Func<long> readDockedGrid = null, bool autoHauler = false,
            Action<long, string, double> unloadHaulerCargo = null, Action<long, string, double> loadHaulerCargo = null,
            Func<string, double?> readMiningBuffer = null)
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
            _miningPlatform = miningPlatform;
            _getGridPositionJson = getGridPositionJson;
            _readHaulerCargo = readHaulerCargo;
            _gridId = gridId;
            _readDockedGrid = readDockedGrid;
            _unloadHaulerCargo = unloadHaulerCargo;
            _loadHaulerCargo = loadHaulerCargo;
            _readMiningBuffer = readMiningBuffer;
            _autoHauler = autoHauler && _readDockedGrid != null && _readHaulerCargo != null;
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
                    ReceiveTask(sourceId, payloadJson);
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
            if (string.Equals(cmd, "survey retry", StringComparison.OrdinalIgnoreCase))
            {
                RetrySurvey();
                return;
            }
            if (string.Equals(cmd, "site report", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cmd, "approach report", StringComparison.OrdinalIgnoreCase))
            {
                SetWaypointFromSurveyReport(cmd.StartsWith("approach", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (cmd == "site remember")
            {
                RememberSurveySite();
                return;
            }
            if (cmd.StartsWith("site forget ", StringComparison.OrdinalIgnoreCase))
            {
                if (_knownSites.Remove(cmd.Substring(12).Trim()))
                    SaveState();
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
            {
                if (CurrentStage() == "transport" && _completedChildRequest == CurrentChildRequestId() &&
                    _baseStockConfirmed && AssignmentRequires("receipt_required"))
                    AdvanceStage("transport");
                else if (CurrentStage() == "mine" && _readMiningBuffer != null &&
                    _completedChildRequest == CurrentChildRequestId())
                    AdvanceStage("mine", automatic: true);
                else
                    PublishCurrentChild();
            }
            if (_autoHauler && _consumerGroup == "Haulers" && HasActiveTask &&
                HasRecentLeaseConfirmation && _tick % _pollIntervalTicks == 0)
            {
                var stage = CurrentStage();
                if (stage == "pickup" || (stage == "deliver" && _pickupCargo.HasValue))
                    AdvanceStage(stage, automatic: true);
            }
        }

        /// <summary>
        /// Returns a multi-line status string for Echo.
        /// </summary>
        public string GetStatus()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Consumer: " + _gridName);
            sb.AppendLine("Group: " + (_miningPlatform ? "MiningFleet + Scouts" : _consumerGroup));
            sb.AppendLine("Broker: " + (_brokerId == 0 ? "broadcast" : _brokerId.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine("Active task: " + (_activeTaskId ?? "none"));
            sb.AppendLine("Stage: " + (string.IsNullOrEmpty(_activeTaskId) ? "idle" : CurrentStage()));
            if (IsAutomaticPoint)
            {
                sb.AppendLine("Child request: " + CurrentChildRequestId());
                sb.AppendLine("Child status: " + (_completedChildRequest == CurrentChildRequestId() ?
                    "DONE" : (_childState ?? "awaiting broker")));
                if (CurrentStage() == "survey" && _childSurveyReport != null)
                    sb.AppendLine("Survey report: " + _childSurveyReport);
                if (CurrentStage() == "transport" && AssignmentRequires("receipt_required"))
                    sb.AppendLine(_baseStockConfirmed ? "Base stock confirmed" : "Waiting for base stock confirmation");
                if (_completedChildRequest == CurrentChildRequestId())
                    sb.AppendLine(CurrentStage() == "transport" && AssignmentRequires("receipt_required")
                        ? "Hauler task complete."
                        : CurrentStage() == "mine" && _readMiningBuffer != null
                            ? "Mining child complete. Checking buffer before dispatch."
                            : "Child task complete. Confirm stage.");
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

        public bool TryGetSurveyLease(out string lease, out string ore)
        {
            lease = null;
            ore = null;
            if (!_miningPlatform || !HasActiveTask || CurrentStage() != "survey" || !HasRecentLeaseConfirmation)
                return false;

            var resource = GetRequestedResource();
            if (string.IsNullOrEmpty(resource) || !resource.StartsWith("Ore/", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(resource.Substring(4)) || resource.Length > 68)
                return false;

            ore = resource.Substring(4);
            lease = _leaseConfirmationSource.ToString(CultureInfo.InvariantCulture) + ":" +
                _leaseEpoch.ToString(CultureInfo.InvariantCulture) + ":" + _activeTaskId;
            return true;
        }

        public bool TryGetMiningPoint(out long grid)
        {
            grid = 0;
            string details;
            string value;
            return _miningPlatform && HasActiveTask && CurrentStage() == "mine" && HasRecentLeaseConfirmation &&
                TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out details) &&
                TaskBusIgcHelper.TryGetJsonProperty(details, "mining_point_grid_id", out value) &&
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out grid) && grid != 0;
        }

        public bool TryGetMiningCargoTarget(out string resource, out double amount)
        {
            resource = null;
            amount = 0;
            if (!_miningPlatform || !HasActiveTask || CurrentStage() != "mine" || !HasRecentLeaseConfirmation)
                return false;

            string details;
            resource = GetRequestedResource();
            return !string.IsNullOrEmpty(resource) &&
                TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out details) &&
                TaskBusIgcHelper.TryReadFiniteNumber(details, "amount", out amount) && amount > 0;
        }

        public bool TryGetMiningApproach(out string taskId, out double x, out double y, out double z)
        {
            taskId = null;
            x = y = z = 0;
            if ((!_miningPlatform && _consumerGroup != "MiningFleet") || !HasActiveTask || CurrentStage() != "mine" ||
                !HasRecentLeaseConfirmation)
                return false;

            if (!TryReadMiningPosition("approach_position", out x, out y, out z))
                return false;
            taskId = _activeTaskId;
            return true;
        }

        public bool TryGetMiningOrePosition(out double x, out double y, out double z)
        {
            x = y = z = 0;
            return _miningPlatform && HasActiveTask && CurrentStage() == "mine" && HasRecentLeaseConfirmation &&
                TryReadMiningPosition("ore_position", out x, out y, out z);
        }

        private bool TryReadMiningPosition(string field, out double x, out double y, out double z)
        {
            x = y = z = 0;
            string details;
            string position;
            return TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out details) &&
                TaskBusIgcHelper.TryGetJsonProperty(details, field, out position) &&
                TaskBusIgcHelper.TryReadFiniteNumber(position, "x", out x) &&
                TaskBusIgcHelper.TryReadFiniteNumber(position, "y", out y) &&
                TaskBusIgcHelper.TryReadFiniteNumber(position, "z", out z);
        }

        private bool HasRecentLeaseConfirmation => _lastLeaseConfirmationTick.HasValue &&
            _tick - _lastLeaseConfirmationTick.Value < LeaseConfirmationTimeoutTicks &&
            (_leaseExpiresUtc == 0 || DateTime.UtcNow.Ticks < _leaseExpiresUtc);

        private void PollBroker()
        {
            var payload = "{\"group_id\":" + TaskBusIgcHelper.ToJsonString(_miningPlatform ? "MiningFleet" : _consumerGroup) +
                (_miningPlatform ? ",\"survey_capable\":true" : "") +
                ",\"consumer_grid\":" + TaskBusIgcHelper.ToJsonString(_gridName) +
                ",\"active_task_id\":" + (string.IsNullOrEmpty(_activeTaskId) ? "null" : TaskBusIgcHelper.ToJsonString(_activeTaskId)) +
                ",\"active_lease_epoch\":" + _leaseEpoch.ToString(CultureInfo.InvariantCulture) +
                ",\"completed_task_id\":" + (string.IsNullOrEmpty(_pendingCompletedTaskId) ? "null" : TaskBusIgcHelper.ToJsonString(_pendingCompletedTaskId)) +
                ",\"lease_epoch\":" + _pendingCompletedEpoch.ToString(CultureInfo.InvariantCulture) + SurveyReportField() + "}";
            SendMessage("TASK_POLL", payload);
        }

        private void ReceiveTask(long sourceId, string taskJson)
        {
            if (_miningPlatform)
            {
                string groupJson;
                string groupId;
                if (!TaskBusIgcHelper.TryGetJsonProperty(taskJson, "group_id", out groupJson) ||
                    !TaskBusIgcHelper.TryReadJsonString(groupJson, out groupId) ||
                    (groupId != "MiningFleet" && groupId != "Scouts"))
                    return;
            }

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

            string deadline;
            long expires = 0;
            if (TaskBusIgcHelper.TryGetJsonProperty(taskJson, "lease_expires_utc_ticks", out deadline) &&
                (!long.TryParse(deadline, NumberStyles.Integer, CultureInfo.InvariantCulture, out expires) ||
                    expires <= DateTime.UtcNow.Ticks || expires > DateTime.MaxValue.Ticks))
                return;
            if (taskId == _activeTaskId && leaseEpoch == _leaseEpoch && sourceId == _leaseConfirmationSource &&
                expires < _leaseExpiresUtc)
                return;

            if (taskId == _lastRevokedTaskId && leaseEpoch <= _lastRevokedEpoch)
                return;

            if (!string.IsNullOrEmpty(_pendingCompletedTaskId))
            {
                if (_pendingCompletedTaskId == taskId && _pendingCompletedEpoch == leaseEpoch)
                {
                    SendPendingCompletion();
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
                _surveyAttempt = 0;
                _childSurveyReport = null;
                _childState = null;
                _pickupCargo = null;
                _oreSite = null;
                _oreApproach = null;
                _activeTaskJson = taskJson;
                LoadWorkflowStages(taskJson);
                _stageIndex = 0;
                _leaseEpoch = leaseEpoch;
                ReuseKnownSite();
                SaveState();
                _echo("Durably received " + taskId + ". Run stage: " + CurrentStage());
            }
            else if (leaseEpoch > _leaseEpoch)
            {
                _leaseEpoch = leaseEpoch;
                _completedChildRequest = null;
                _surveyAttempt = 0;
                _childSurveyReport = null;
                _childState = null;
                _activeTaskJson = taskJson;
                SaveState();
            }
            else if (leaseEpoch < _leaseEpoch)
                return;

            string confirmation;
            _baseStockConfirmed = TaskBusIgcHelper.TryGetJsonProperty(taskJson, "base_stock_confirmed", out confirmation) &&
                confirmation == "true";
            _leaseExpiresUtc = expires;
            _leaseConfirmationSource = sourceId;
            _lastLeaseConfirmationTick = _tick;
            SendMessage("TASK_RECEIVED", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(taskId) +
                ",\"lease_epoch\":" + _leaseEpoch.ToString(CultureInfo.InvariantCulture) + "}");
        }

        private bool IsAutomaticPoint => !_miningPlatform && _autoPoint && _consumerGroup == "MiningPoints" && HasActiveTask;

        private string CurrentChildRequestId() => _activeTaskId + ":" +
            _leaseEpoch.ToString(CultureInfo.InvariantCulture) + ":" + _stageIndex.ToString(CultureInfo.InvariantCulture) +
            (CurrentStage() == "survey" && _surveyAttempt > 0 ?
                ":retry:" + _surveyAttempt.ToString(CultureInfo.InvariantCulture) : "");

        private void RetrySurvey()
        {
            if (!IsAutomaticPoint || CurrentStage() != "survey" || !HasRecentLeaseConfirmation ||
                _completedChildRequest != CurrentChildRequestId() || _surveyAttempt == int.MaxValue)
            {
                _echo("A completed survey under the current lease is required to retry.");
                return;
            }

            _surveyAttempt++;
            _completedChildRequest = null;
            _childSurveyReport = null;
            _childState = null;
            _oreSite = null;
            _oreApproach = null;
            SaveState();
            PublishCurrentChild();
            _echo("Survey retry requested.");
        }

        private string GetRequestedResource()
        {
            string details;
            string raw;
            string resource;
            return TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out details) &&
                TaskBusIgcHelper.TryGetJsonProperty(details, "resource", out raw) &&
                TaskBusIgcHelper.TryReadJsonString(raw, out resource) ? resource : null;
        }

        private void RememberSurveySite()
        {
            var resource = GetRequestedResource();
            if (!IsAutomaticPoint || CurrentStage() != "survey" || !HasRecentLeaseConfirmation ||
                _completedChildRequest != CurrentChildRequestId() || _oreSite == null || _oreApproach == null ||
                string.IsNullOrEmpty(resource) || !resource.StartsWith("Ore/", StringComparison.OrdinalIgnoreCase))
            {
                _echo("A completed survey and two selected waypoints are required.");
                return;
            }
            if (_knownSites.Count >= 32 && !_knownSites.ContainsKey(resource))
            {
                _echo("Known-site catalog is full.");
                return;
            }

            _knownSites[resource] = _oreSite + ";" + _oreApproach;
            SaveState();
            _echo("Remembered site for " + resource + ".");
        }

        private void ReuseKnownSite()
        {
            if (!IsAutomaticPoint || _stages.Count < 2 || _stages[0] != "survey" || _stages[1] != "mine")
                return;
            var resource = GetRequestedResource();
            string saved;
            if (resource == null || !_knownSites.TryGetValue(resource, out saved))
                return;

            var points = saved.Split(';');
            _oreSite = points[0];
            _oreApproach = points[1];
            _stageIndex = 1;
            _echo("Using known site for " + resource + ".");
        }

        private void SetWaypointFromSurveyReport(bool approach)
        {
            if (!IsAutomaticPoint || CurrentStage() != "survey" || !HasRecentLeaseConfirmation ||
                _completedChildRequest != CurrentChildRequestId())
            {
                _echo("A completed survey under the current lease is required.");
                return;
            }

            var report = _childSurveyReport ?? "";
            int separator = report.IndexOf(" | GPS:Observation:", StringComparison.Ordinal);
            if (approach)
            {
                if (separator >= 0)
                    SetSurveyWaypoint("approach " + report.Substring(separator + 3));
                else
                    _echo("The survey report has no observation position.");
                return;
            }

            var oreReport = separator < 0 ? report : report.Substring(0, separator);
            int marker = oreReport.LastIndexOf(" GPS:", StringComparison.Ordinal);
            if (marker < 0)
            {
                _echo("The survey report has no ore GPS.");
                return;
            }

            SetSurveyWaypoint("site " + oreReport.Substring(marker + 1));
        }

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
                .Append(",\"steps\":[");
            if (group == "Haulers")
                payload.Append("\"pickup\",");
            payload.Append(TaskBusIgcHelper.ToJsonString(childStage)).Append(']');
            foreach (var field in new[] { "key", "resource", "amount", "base_grid_id", "base_grid_name", "base_position" })
            {
                string value;
                if (TaskBusIgcHelper.TryGetJsonProperty(details, field, out value))
                    payload.Append(',').Append(TaskBusIgcHelper.ToJsonString(field)).Append(':').Append(value);
            }
            string candidate;
            if (childStage == "survey" && TaskBusIgcHelper.TryGetJsonProperty(details, "ore_candidate_gps", out candidate))
                payload.Append(",\"ore_candidate_gps\":").Append(candidate);
            if (CurrentStage() == "mine" && _oreSite != null && _oreApproach != null)
                payload.Append(",\"ore_position\":").Append(_oreSite)
                    .Append(",\"approach_position\":").Append(_oreApproach);
            if (childStage == "deliver" && _getGridPositionJson != null)
                payload.Append(",\"pickup_position\":").Append(_getGridPositionJson())
                    .Append(",\"pickup_name\":").Append(TaskBusIgcHelper.ToJsonString(_gridName));
            if (childStage == "mine" && _gridId != 0)
                payload.Append(",\"mining_point_grid_id\":").Append(_gridId.ToString(CultureInfo.InvariantCulture));
            if (childStage == "deliver" && _gridId != 0)
                payload.Append(",\"pickup_grid_id\":").Append(_gridId.ToString(CultureInfo.InvariantCulture));
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

            if (state != "DONE")
            {
                if (_completedChildRequest != request)
                    _childState = state;
                return true;
            }

            bool changed = _completedChildRequest != request;
            _childState = state;
            _completedChildRequest = request;
            string report;
            if (CurrentStage() == "survey" && TaskBusIgcHelper.TryGetJsonProperty(payload, "survey_report", out value) &&
                TaskBusIgcHelper.TryReadJsonString(value, out report) && report.Length <= 320 && report != _childSurveyReport)
            {
                _childSurveyReport = report;
                changed = true;
            }
            if (changed)
                SaveState();
            return true;
        }

        public void CompleteSurvey(string report)
        {
            string lease;
            string ore;
            if (TryGetSurveyLease(out lease, out ore) && !string.IsNullOrWhiteSpace(report) && report.Length <= 320)
                AdvanceStage("survey", report);
        }

        private string SurveyReportField()
        {
            return _pendingSurveyReport == null ? "" :
                ",\"survey_report\":" + TaskBusIgcHelper.ToJsonString(_pendingSurveyReport);
        }

        private void SendPendingCompletion()
        {
            SendMessage("TASK_DONE", "{\"task_id\":" + TaskBusIgcHelper.ToJsonString(_pendingCompletedTaskId) +
                ",\"lease_epoch\":" + _pendingCompletedEpoch.ToString(CultureInfo.InvariantCulture) +
                SurveyReportField() + "}");
        }

        private void AdvanceStage(string completedStage, string surveyReport = null, bool automatic = false)
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

            if (_consumerGroup == "Haulers" && _readDockedGrid != null &&
                (expected == "pickup" || expected == "deliver") && !CheckHaulerDock(expected == "pickup", automatic))
                return;

            if (_consumerGroup == "Haulers" &&
                ((expected == "pickup" && _readHaulerCargo != null) || (expected == "deliver" && _pickupCargo.HasValue)) &&
                !CheckHaulerCargo(expected == "pickup", automatic))
                return;

            if (IsAutomaticPoint && _completedChildRequest != CurrentChildRequestId())
            {
                _echo("Waiting for the broker to confirm the child task is complete.");
                return;
            }

            if (IsAutomaticPoint && expected == "mine" && _readMiningBuffer != null && !MiningBufferReady())
            {
                if (!automatic)
                    _echo("Waiting for the requested quantity in the configured mining buffer.");
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
            _completedChildRequest = null;
            _childSurveyReport = null;
            if (_stageIndex >= _stages.Count)
            {
                _pendingCompletedTaskId = _activeTaskId;
                _pendingCompletedEpoch = _leaseEpoch;
                _pendingSurveyReport = surveyReport;
                _activeTaskId = null;
                _activeTaskJson = null;
                _stages.Clear();
                _stageIndex = 0;
                SaveState();
                SendPendingCompletion();
                _echo("Workflow complete; waiting for broker commit ACK.");
            }
            else
            {
                SaveState();
                _echo("Stage saved. Next stage: " + CurrentStage());
            }
        }

        private string _miningDepositLease;
        private double _miningDeposited;

        public double MiningDeposited
        {
            get
            {
                return _miningDepositLease != null && _miningDepositLease == ConfirmedLeaseIdentity ? _miningDeposited : 0;
            }
        }

        public void RecordMiningDeposit(double transferred)
        {
            string resource;
            double target;
            if (!(transferred > 0) || double.IsInfinity(transferred) || !TryGetMiningCargoTarget(out resource, out target))
                return;
            _miningDeposited = Math.Min(target, MiningDeposited + transferred);
            _miningDepositLease = ConfirmedLeaseIdentity;
            SaveState();
        }

        public bool TryCompleteMiningDeposit()
        {
            string resource;
            double target;
            if (!TryGetMiningCargoTarget(out resource, out target) || MiningDeposited + 0.0000001 < target)
                return false;
            AdvanceStage("mine");
            return true;
        }

        public long SupervisedCruiseRemote { get; private set; }

        public void SetSupervisedCruiseRemote(long remoteId)
        {
            if (SupervisedCruiseRemote == remoteId)
                return;
            SupervisedCruiseRemote = remoteId;
            SaveState();
        }

        public string ConfirmedLeaseIdentity => HasActiveTask && HasRecentLeaseConfirmation
            ? _leaseConfirmationSource.ToString(CultureInfo.InvariantCulture) + ":" +
                _leaseEpoch.ToString(CultureInfo.InvariantCulture) + ":" + _activeTaskId
            : null;

        public bool TryGetHaulerDestination(out string taskId, out bool pickup, out long grid)
        {
            taskId = null;
            grid = 0;
            pickup = CurrentStage() == "pickup";
            if (_consumerGroup != "Haulers" || !HasActiveTask || !HasRecentLeaseConfirmation ||
                (!pickup && CurrentStage() != "deliver") || !TryReadHaulerGrid(pickup, out grid))
                return false;

            taskId = _activeTaskId;
            return true;
        }

        private bool MiningBufferReady()
        {
            string details;
            double target;
            var resource = GetRequestedResource();
            return !string.IsNullOrEmpty(resource) &&
                TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out details) &&
                TaskBusIgcHelper.TryReadFiniteNumber(details, "amount", out target) && target > 0 &&
                _readMiningBuffer(resource) >= target;
        }

        public bool CanDepartHaulerPickup()
        {
            long pickupGrid;
            return _consumerGroup == "Haulers" && HasActiveTask && HasRecentLeaseConfirmation &&
                CurrentStage() == "deliver" && _stageIndex > 0 && _stages[_stageIndex - 1] == "pickup" &&
                _readDockedGrid != null && TryReadHaulerGrid(true, out pickupGrid) &&
                _readDockedGrid() == pickupGrid;
        }

        private bool TryReadHaulerGrid(bool pickup, out long grid)
        {
            grid = 0;
            string field = pickup ? "pickup_grid_id" : "base_grid_id";
            string details;
            string value;
            return TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out details) &&
                TaskBusIgcHelper.TryGetJsonProperty(details, field, out value) &&
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out grid) && grid != 0;
        }

        private bool CheckHaulerDock(bool pickup, bool automatic)
        {
            long grid;
            if (!TryReadHaulerGrid(pickup, out grid))
            {
                if (!automatic)
                    _echo("The assignment has no " + (pickup ? "pickup" : "delivery") + " grid ID to verify.");
                return false;
            }

            if (_readDockedGrid() == grid)
                return true;

            if (!automatic)
                _echo("Dock at the assigned " + (pickup ? "mining point" : "delivery base") + " before confirming.");
            return false;
        }

        private bool CheckHaulerCargo(bool pickup, bool automatic)
        {
            string details;
            double amount;
            var resource = GetRequestedResource();
            if (string.IsNullOrEmpty(resource) ||
                !TaskBusIgcHelper.TryGetJsonProperty(_activeTaskJson, "details", out details) ||
                !TaskBusIgcHelper.TryReadFiniteNumber(details, "amount", out amount) || amount <= 0)
            {
                if (!automatic)
                    _echo("The transport assignment has no valid resource amount.");
                return false;
            }

            double? loaded = _readHaulerCargo == null ? null : _readHaulerCargo(resource);
            if (!loaded.HasValue || !(loaded.Value >= 0) || double.IsInfinity(loaded.Value))
            {
                if (!automatic)
                    _echo("Cargo measurement unavailable. Check the configured hauler container.");
                return false;
            }

            if (pickup)
            {
                if (loaded.Value < amount)
                {
                    if (automatic && _loadHaulerCargo != null)
                        _loadHaulerCargo(_readDockedGrid(), resource, amount - loaded.Value);
                    else if (!automatic)
                        _echo("Load at least " + amount.ToString("G", CultureInfo.InvariantCulture) + " of " + resource + ".");
                    return false;
                }
                _pickupCargo = loaded.Value;
            }
            else
            {
                double unloaded = _pickupCargo.Value - loaded.Value;
                if (unloaded + 0.0000001 < amount)
                {
                    if (automatic && _unloadHaulerCargo != null)
                        _unloadHaulerCargo(_readDockedGrid(), resource, Math.Min(loaded.Value, amount - unloaded));
                    else if (!automatic)
                        _echo("Unload at least " + amount.ToString("G", CultureInfo.InvariantCulture) + " of " + resource +
                            ". Measured decrease: " + unloaded.ToString("G", CultureInfo.InvariantCulture));
                    return false;
                }
            }
            return true;
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
            if (_miningPlatform)
            {
                _echo("Mining platforms accept both mining and survey assignments.");
                return;
            }

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
                _pendingSurveyReport = null;
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
                _pendingSurveyReport = null;
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

            string candidateJson;
            string candidate;
            if (TaskBusIgcHelper.TryGetJsonProperty(details, "ore_candidate_gps", out candidateJson) &&
                TaskBusIgcHelper.TryReadJsonString(candidateJson, out candidate))
                summary.Append("\nOre candidate to verify: " + candidate);

            string pickupNameJson;
            string pickupName;
            if (TaskBusIgcHelper.TryGetJsonProperty(details, "pickup_name", out pickupNameJson) &&
                TaskBusIgcHelper.TryReadJsonString(pickupNameJson, out pickupName))
                summary.Append("\nPickup point: " + pickupName);
            AppendPosition(summary, details, "pickup_position", "Pickup at dispatch time");
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
                    else if (key == "SURVEY_ATTEMPT")
                        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _surveyAttempt);
                    else if (key == "STAGE_INDEX") int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _stageIndex);
                    else if (key == "STAGES")
                    {
                        List<string> stages;
                        if (TaskBusProtocol.TryReadStringArray(TaskBusIgcHelper.DecodeStorage(value), out stages))
                            _stages.AddRange(stages);
                    }
                    else if (key == "KNOWN_SITE")
                    {
                        var fields = value.Split('|');
                        if (fields.Length == 2 && _knownSites.Count < 32)
                        {
                            var resource = TaskBusIgcHelper.DecodeStorage(fields[0]);
                            var points = TaskBusIgcHelper.DecodeStorage(fields[1]);
                            if (points.Split(';').Length == 2)
                                _knownSites[resource] = points;
                        }
                    }
                    else if (key == "PICKUP_CARGO")
                    {
                        double cargo;
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out cargo) &&
                            cargo >= 0 && !double.IsInfinity(cargo))
                            _pickupCargo = cargo;
                    }
                    else if (key == "MINING_DEPOSIT")
                    {
                        var fields = value.Split('|');
                        double deposited;
                        if (fields.Length == 2 && double.TryParse(fields[1], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out deposited) && deposited > 0 && !double.IsInfinity(deposited))
                        {
                            _miningDepositLease = TaskBusIgcHelper.DecodeStorage(fields[0]);
                            _miningDeposited = deposited;
                        }
                    }
                    else if (key == "CRUISE_REMOTE")
                    {
                        long remoteId;
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out remoteId))
                            SupervisedCruiseRemote = remoteId;
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
                    else if (key == "SURVEY_RESULT") _pendingSurveyReport = TaskBusIgcHelper.DecodeStorage(value);
                    else if (key == "CHILD_COMPLETED") _completedChildRequest = TaskBusIgcHelper.DecodeStorage(value);
                    else if (key == "CHILD_SURVEY_RESULT") _childSurveyReport = TaskBusIgcHelper.DecodeStorage(value);
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
                if (_surveyAttempt > 0)
                    storage.AppendLine("SURVEY_ATTEMPT|" + _surveyAttempt.ToString(CultureInfo.InvariantCulture));
                storage.AppendLine("STAGE_INDEX|" + _stageIndex.ToString(CultureInfo.InvariantCulture));
                var stagesJson = "[" + string.Join(",", _stages.Select(TaskBusIgcHelper.ToJsonString).ToArray()) + "]";
                storage.AppendLine("STAGES|" + TaskBusIgcHelper.EncodeStorage(stagesJson));
                if (_completedChildRequest != null) storage.AppendLine("CHILD_COMPLETED|" + TaskBusIgcHelper.EncodeStorage(_completedChildRequest));
                if (_childSurveyReport != null) storage.AppendLine("CHILD_SURVEY_RESULT|" + TaskBusIgcHelper.EncodeStorage(_childSurveyReport));
                if (_pickupCargo.HasValue)
                    storage.AppendLine("PICKUP_CARGO|" + _pickupCargo.Value.ToString("R", CultureInfo.InvariantCulture));
                if (_oreSite != null) storage.AppendLine("ORE_SITE|" + TaskBusIgcHelper.EncodeStorage(_oreSite));
                if (_oreApproach != null) storage.AppendLine("ORE_APPROACH|" + TaskBusIgcHelper.EncodeStorage(_oreApproach));
                storage.AppendLine("LEASE_EPOCH|" + _leaseEpoch.ToString(CultureInfo.InvariantCulture));
            }
            foreach (var site in _knownSites)
                storage.AppendLine("KNOWN_SITE|" + TaskBusIgcHelper.EncodeStorage(site.Key) +
                    "|" + TaskBusIgcHelper.EncodeStorage(site.Value));
            if (_miningDepositLease != null)
                storage.AppendLine("MINING_DEPOSIT|" + TaskBusIgcHelper.EncodeStorage(_miningDepositLease) +
                    "|" + _miningDeposited.ToString("R", CultureInfo.InvariantCulture));
            if (SupervisedCruiseRemote != 0)
                storage.AppendLine("CRUISE_REMOTE|" + SupervisedCruiseRemote.ToString(CultureInfo.InvariantCulture));
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
                if (_pendingSurveyReport != null) storage.AppendLine("SURVEY_RESULT|" + TaskBusIgcHelper.EncodeStorage(_pendingSurveyReport));
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
