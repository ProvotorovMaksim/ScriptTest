#region Prelude
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using System.Globalization;
using System.Linq;
using VRageMath;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;
using System.Text;
using SpaceEngineers.Libs.IgcCore;
using SpaceEngineers.Libs.TaskBusProducer;
using SpaceEngineers.Libs.TaskBusConsumer;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace SpaceEngineers.Apps.Amn
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private const string SurveyProperty = "Survey.DetectedOre";
        private const double OreCandidateRadius = 5000;
        private const int MaxOreObservations = 256;
        private long _surveyRecipient;
        private readonly Queue<string> _surveyOutbox = new Queue<string>();
        private readonly HashSet<string> _oreObservations = new HashSet<string>();
        private readonly List<IMyOreDetector> _oreDetectors = new List<IMyOreDetector>();
        private string _gridName;
        private int _tickCount = 0;
        private int _requestSequence;
        private long _brokerId;
        private bool _dispatchToPoint;
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();
        private readonly List<IMyCargoContainer> _containers = new List<IMyCargoContainer>();
        private readonly List<IMyAssembler> _assemblers = new List<IMyAssembler>();
        private readonly List<IMyRefinery> _refineries = new List<IMyRefinery>();
        private readonly List<IMyCockpit> _cockpits = new List<IMyCockpit>();
        private readonly List<IMyRemoteControl> _remoteControls = new List<IMyRemoteControl>();
        private readonly Dictionary<string, double> _minimums = new Dictionary<string, double>();
        private readonly Dictionary<string, string> _requestIds = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _requestStates = new Dictionary<string, string>();
        private readonly HashSet<string> _lowResources = new HashSet<string>();
        private readonly HashSet<string> _pendingResourceClears = new HashSet<string>();
        private IMyBroadcastListener _listener;
        private TaskBusProducer _taskBusProducer;
        private readonly GalnetWorkerBridge _galnet;

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
            _gridName = Me.CubeGrid.CustomName;
            _listener = IGC.RegisterBroadcastListener(CommunicationTag);
            _galnet = new GalnetWorkerBridge(Me, Me.CustomData,
                () => GridTerminalSystem.GetBlockWithName(GalnetWorkerBridge.RelayName) as IMyProgrammableBlock,
                (envelope, _) => IGC.SendBroadcastMessage(CommunicationTag, envelope, TransmissionDistance.TransmissionDistanceMax),
                message => Echo(message));
            _taskBusProducer = new TaskBusProducer(CommunicationTag, SendEnvelope);

            LoadMinimums();
        }
        public void Main(string args)
        {
            var command = args ?? string.Empty;
            if (_galnet.TryReceive(command, (source, envelope) => HandleIncoming(source, envelope, true)))
                return;
            _tickCount++;
            if (string.Equals(command, "survey", StringComparison.OrdinalIgnoreCase))
            {
                ScanOreDetectors();
                return;
            }
            if (string.Equals(command, "survey list", StringComparison.OrdinalIgnoreCase))
            {
                Echo("Survey block ID: " + Me.EntityId);
                Echo("Ore observations: " + _oreObservations.Count + ". Pending transfers: " + _surveyOutbox.Count);
                foreach (var observation in _oreObservations.OrderBy(value => value).Take(15))
                    Echo(observation);
                return;
            }
            if (command.StartsWith("survey nearest ", StringComparison.OrdinalIgnoreCase))
            {
                FindNearbyOre(command.Substring(15).Trim());
                return;
            }
            if (string.Equals(command, "survey cancel", StringComparison.OrdinalIgnoreCase))
            {
                _surveyOutbox.Clear();
                _surveyRecipient = 0;
                SaveMinimums();
                Echo("Pending survey transfer cancelled.");
            }
            if (command.StartsWith("survey send ", StringComparison.OrdinalIgnoreCase))
                QueueSurveyTransfer(command.Substring(12));
            if (string.Equals(command, "dispatch point", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "dispatch fleet", StringComparison.OrdinalIgnoreCase))
            {
                if (_requestIds.Count != 0)
                    Echo("Resolve open requests before changing the dispatch group.");
                else
                {
                    _dispatchToPoint = string.Equals(command, "dispatch point", StringComparison.OrdinalIgnoreCase);
                    SaveMinimums();
                    Echo("Dispatch group: " + DispatchGroup);
                }
            }
            bool retryBrokerRequests = false;
            if (command.StartsWith("broker ", StringComparison.OrdinalIgnoreCase))
            {
                const string restoredCommand = "broker restored ";
                bool restored = command.StartsWith(restoredCommand, StringComparison.OrdinalIgnoreCase);
                long brokerId;
                if (!long.TryParse(command.Substring(restored ? restoredCommand.Length : 7).Trim(), out brokerId) ||
                    brokerId < 0 || (restored && brokerId == 0))
                    Echo("Usage: broker <block ID>, broker 0, or broker restored <block ID>.");
                else if (brokerId != _brokerId)
                {
                    if (!restored && _requestIds.Count != 0)
                    {
                        Echo("Resolve open requests before switching, or use broker restored for a restored queue.");
                        return;
                    }
                    _galnet.DiscardPendingSends();
                    _requestStates.Clear();
                    _brokerId = brokerId;
                    SaveMinimums();
                    retryBrokerRequests = true;
                    Echo("Broker selected: " + brokerId);
                }
            }
            if (command.StartsWith("set ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("remove ", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "help", StringComparison.OrdinalIgnoreCase))
            {
                HandleMinimumCommand(command);
            }
            //IGC connection
            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas, antenna => antenna.IsFunctional && antenna.Enabled);

            if (_antennas.Count == 0)
            {
                Echo("No enabled, functional radio antenna found.");
            }

            if (_antennas.Count > 0)
            {
                if (string.Equals(command, "ping", StringComparison.OrdinalIgnoreCase))
                    SendMessage("PING", "null");
                else if (command.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
                    SendMessage("MSG", TaskBusIgcHelper.ToJsonString(command.Substring(4)));
            }

            while (_listener.HasPendingMessage)
            {
                var message = _listener.AcceptMessage();
                if (message.Source != Me.EntityId)
                    HandleIncoming(message.Source, message.Data as string, false);
            }

            if (_antennas.Count > 0 && _surveyRecipient != 0 && _surveyOutbox.Count > 0 &&
                (_tickCount % 6 == 1 || command.StartsWith("survey send ", StringComparison.OrdinalIgnoreCase)))
                SendMessage("ORE_OBSERVATION", TaskBusIgcHelper.ToJsonString(_surveyOutbox.Peek()), _surveyRecipient);

            //Base_resourse SCAN
            _containers.Clear();
            _assemblers.Clear();
            _refineries.Clear();
            _cockpits.Clear();
            _remoteControls.Clear();

            GridTerminalSystem.GetBlocksOfType(_containers, container => container.IsFunctional && container.IsSameConstructAs(Me));
            GridTerminalSystem.GetBlocksOfType(_assemblers, assembler => assembler.IsFunctional && assembler.IsSameConstructAs(Me));
            GridTerminalSystem.GetBlocksOfType(_refineries, refinery => refinery.IsFunctional && refinery.IsSameConstructAs(Me));
            GridTerminalSystem.GetBlocksOfType(_cockpits, block => block.IsFunctional && block.IsSameConstructAs(Me));
            GridTerminalSystem.GetBlocksOfType(_remoteControls, rc => rc.IsFunctional && rc.IsSameConstructAs(Me));

            var resources = new Dictionary<string, double>();
            var items = new List<MyInventoryItem>();

            Action<IMyTerminalBlock> scanInventories = block =>
            {
                for (int index = 0; index < block.InventoryCount; index++)
                {
                    var inventory = block.GetInventory(index);
                    if (inventory == null)
                        continue;
                    items.Clear();
                    inventory.GetItems(items);
                    foreach (var item in items)
                    {
                        var itemName = GetResourceKey(item.Type);
                        double previous;
                        resources.TryGetValue(itemName, out previous);
                        resources[itemName] = previous + (double)item.Amount;
                    }
                }
            };

            foreach (var container in _containers) scanInventories(container);
            foreach (var assembler in _assemblers) scanInventories(assembler);
            foreach (var refinery in _refineries) scanInventories(refinery);
            foreach (var cockpit in _cockpits) scanInventories(cockpit);
            foreach (var rc in _remoteControls) scanInventories(rc);

            Echo($"Cargo containers: {_containers.Count}, Assemblers: {_assemblers.Count}, Refineries: {_refineries.Count}, Cockpits: {_cockpits.Count}, Remote controls: {_remoteControls.Count}");
            foreach (var resource in resources)
                Echo($"{resource.Key}: {resource.Value:0.##}");

            UpdateMinimumTasks(resources, retryBrokerRequests);
            UpdateCockpitPanel(resources);
            _galnet.Flush();

        }

        private string GetResourceKey(MyItemType itemType)
        {
            var typeName = itemType.TypeId.ToString().Replace("MyObjectBuilder_", string.Empty);
            return typeName + "/" + itemType.SubtypeId;
        }

        private void HandleMinimumCommand(string command)
        {
            var parts = command.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && string.Equals(parts[0], "set", StringComparison.OrdinalIgnoreCase))
            {
                double minimum;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out minimum) || double.IsNaN(minimum) || double.IsInfinity(minimum) || minimum < 0)
                {
                    Echo("Usage: set Component/SteelPlate 500");
                    return;
                }

                _minimums[parts[1]] = minimum;
                SaveMinimums();
                Echo($"Minimum set: {parts[1]} = {minimum:0.##}");
                return;
            }

            if (parts.Length == 2 && string.Equals(parts[0], "remove", StringComparison.OrdinalIgnoreCase))
            {
                QueueResourceClear(parts[1]);
                _minimums.Remove(parts[1]);
                _lowResources.Remove(parts[1]);
                SaveMinimums();
                Echo($"Minimum removed: {parts[1]}");
                return;
            }

            Echo("Commands: set Type/Subtype amount | remove Type/Subtype");
            Echo("Example: set Component/SteelPlate 500");
        }

        private void ScanOreDetectors()
        {
            _oreDetectors.Clear();
            GridTerminalSystem.GetBlocksOfType(_oreDetectors,
                detector => detector.IsWorking && detector.IsSameConstructAs(Me));
            int available = 0;
            int added = 0;
            foreach (var detector in _oreDetectors)
            {
                var property = detector.GetProperty(SurveyProperty)
                    as Sandbox.ModAPI.Interfaces.ITerminalProperty<string>;
                if (property == null)
                    continue;
                try
                {
                    string reports = property.GetValue(detector);
                    available++;
                    if (string.IsNullOrEmpty(reports))
                        continue;
                    foreach (var report in reports.Split('\n'))
                        if (AddOreObservation(report))
                            added++;
                }
                catch (Exception error)
                {
                    Echo("Ore detector read failed: " + error.Message);
                }
            }
            if (added > 0)
                SaveMinimums();
            Echo(available == 0 ? "No working detector exposes " + SurveyProperty + "." :
                "New observations: " + added + ". Catalog: " + _oreObservations.Count + "/" + MaxOreObservations + ".");
        }

        private void FindNearbyOre(string argument)
        {
            int split = argument.LastIndexOf(' ');
            double radius;
            if (split < 1 || !double.TryParse(argument.Substring(split + 1), NumberStyles.Float,
                CultureInfo.InvariantCulture, out radius) || !(radius > 0) || double.IsInfinity(radius))
            {
                Echo("Usage: survey nearest <ore name> <radius in metres>.");
                return;
            }

            var position = Me.CubeGrid.GetPosition();
            var report = TaskBusIgcHelper.FindNearestOre(_oreObservations, argument.Substring(0, split).Trim(),
                position.X, position.Y, position.Z, radius);
            Echo(report ?? "No catalogued observation in range. Survey coverage unknown.");
        }

        private void QueueSurveyTransfer(string argument)
        {
            long recipient;
            if (!long.TryParse(argument, out recipient) || recipient == 0 || recipient == Me.EntityId)
            {
                Echo("Usage: survey send <destination programmable block ID>.");
                return;
            }
            if (_surveyOutbox.Count != 0)
            {
                Echo("Survey transfer already pending for block " + _surveyRecipient + ".");
                return;
            }
            _surveyRecipient = recipient;
            foreach (var observation in _oreObservations.OrderBy(value => value))
                _surveyOutbox.Enqueue(observation);
            SaveMinimums();
            Echo("Survey observations queued: " + _surveyOutbox.Count + ".");
        }

        private void HandleSurveyTransfer(long sender, string eventType, string payload)
        {
            string observation;
            if (!TaskBusIgcHelper.TryReadJsonString(payload, out observation))
                return;

            if (eventType == "ORE_OBSERVATION_ACK")
            {
                if (sender == _surveyRecipient && _surveyOutbox.Count > 0 && _surveyOutbox.Peek() == observation)
                {
                    _surveyOutbox.Dequeue();
                    SaveMinimums();
                }
                return;
            }

            string normalized;
            if (!TryNormalizeOreObservation(observation, out normalized))
                return;
            if (!_oreObservations.Contains(normalized))
            {
                if (_oreObservations.Count >= MaxOreObservations)
                {
                    Echo("Ore catalog full. Incoming observation could not be saved.");
                    return;
                }
                _oreObservations.Add(normalized);
                SaveMinimums();
            }
            SendMessage("ORE_OBSERVATION_ACK", payload, sender);
        }

        private bool AddOreObservation(string report)
        {
            string observation;
            return _oreObservations.Count < MaxOreObservations &&
                TryNormalizeOreObservation(report, out observation) && _oreObservations.Add(observation);
        }

        private static bool TryNormalizeOreObservation(string report, out string observation)
        {
            observation = null;
            string ore;
            double x;
            double y;
            double z;
            if (!TaskBusIgcHelper.TryReadOreGps(report, out ore, out x, out y, out z) || ore.Length > 64)
                return false;

            var culture = CultureInfo.InvariantCulture;
            observation = "GPS:" + ore + ":" + x.ToString("R", culture) + ":" +
                y.ToString("R", culture) + ":" + z.ToString("R", culture) + ":";
            return true;
        }

        private void LoadMinimums()
        {
            _minimums.Clear();
            _requestIds.Clear();
            _lowResources.Clear();
            _pendingResourceClears.Clear();
            _requestSequence = 0;
            if (string.IsNullOrWhiteSpace(Storage))
                return;

            var lines = Storage.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("SURVEY_TARGET|", StringComparison.Ordinal))
                {
                    long.TryParse(line.Substring(14), NumberStyles.Integer, CultureInfo.InvariantCulture, out _surveyRecipient);
                    continue;
                }
                if (line.StartsWith("SURVEY_PENDING|", StringComparison.Ordinal))
                {
                    try
                    {
                        string observation;
                        if (_surveyOutbox.Count < MaxOreObservations &&
                            TryNormalizeOreObservation(TaskBusIgcHelper.DecodeStorage(line.Substring(15)), out observation))
                            _surveyOutbox.Enqueue(observation);
                    }
                    catch (FormatException) { }
                    continue;
                }
                if (line.StartsWith("ORE|", StringComparison.Ordinal))
                {
                    try { AddOreObservation(TaskBusIgcHelper.DecodeStorage(line.Substring(4))); }
                    catch (FormatException) { }
                    continue;
                }
                if (line.StartsWith("DISPATCH|", StringComparison.Ordinal))
                {
                    _dispatchToPoint = line.Substring(9).Trim() == "point";
                    continue;
                }
                if (line.StartsWith("BROKER|", StringComparison.Ordinal))
                {
                    long.TryParse(line.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out _brokerId);
                    continue;
                }
                if (line.StartsWith("REQSEQ|", StringComparison.Ordinal))
                {
                    int.TryParse(line.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out _requestSequence);
                    continue;
                }
                if (line.StartsWith("REQUEST|", StringComparison.Ordinal))
                {
                    var fields = line.Split('|');
                    if (fields.Length != 3 && fields.Length != 4)
                        continue;
                    try
                    {
                        var resource = TaskBusIgcHelper.DecodeStorage(fields[1]);
                        var requestId = TaskBusIgcHelper.DecodeStorage(fields[2]);
                        _requestIds[resource] = requestId;
                    }
                    catch (FormatException) { }
                    continue;
                }
                if (line.StartsWith("LOW|", StringComparison.Ordinal))
                {
                    _lowResources.Add(line.Substring(4));
                    continue;
                }
                if (line.StartsWith("CLEAR_PENDING|", StringComparison.Ordinal))
                {
                    _pendingResourceClears.Add(line.Substring(14));
                    continue;
                }

                var minimumLine = line.StartsWith("MIN|", StringComparison.Ordinal) ? line.Substring(4) : line;
                var separator = minimumLine.IndexOf('=');
                if (separator <= 0)
                    continue;

                double minimum;
                var key = minimumLine.Substring(0, separator).Trim();
                var value = minimumLine.Substring(separator + 1).Trim();
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out minimum) && !double.IsInfinity(minimum) && minimum >= 0)
                    _minimums[key] = minimum;
            }
        }

        private void SaveMinimums()
        {
            var storage = new StringBuilder();
            storage.AppendLine("DISPATCH|" + (_dispatchToPoint ? "point" : "fleet"));
            storage.AppendLine("BROKER|" + _brokerId.ToString(CultureInfo.InvariantCulture));
            storage.AppendLine("REQSEQ|" + _requestSequence.ToString(CultureInfo.InvariantCulture));
            foreach (var minimum in _minimums.OrderBy(entry => entry.Key))
                storage.AppendLine("MIN|" + minimum.Key + "=" + minimum.Value.ToString(CultureInfo.InvariantCulture));
            foreach (var request in _requestIds.OrderBy(entry => entry.Key))
                storage.AppendLine("REQUEST|" + TaskBusIgcHelper.EncodeStorage(request.Key) + "|" + TaskBusIgcHelper.EncodeStorage(request.Value));
            foreach (var resource in _lowResources.OrderBy(key => key))
                storage.AppendLine("LOW|" + resource);
            foreach (var resource in _pendingResourceClears.OrderBy(key => key))
                storage.AppendLine("CLEAR_PENDING|" + resource);
            foreach (var observation in _oreObservations.OrderBy(value => value))
                storage.AppendLine("ORE|" + TaskBusIgcHelper.EncodeStorage(observation));
            if (_surveyOutbox.Count > 0)
            {
                storage.AppendLine("SURVEY_TARGET|" + _surveyRecipient.ToString(CultureInfo.InvariantCulture));
                foreach (var observation in _surveyOutbox)
                    storage.AppendLine("SURVEY_PENDING|" + TaskBusIgcHelper.EncodeStorage(observation));
            }
            Storage = storage.ToString();
        }

        private void QueueResourceClear(string resource)
        {
            if (_requestIds.ContainsKey(resource))
                _pendingResourceClears.Add(resource);
        }

        private void UpdateCockpitPanel(Dictionary<string, double> resources)
        {
            _cockpits.Clear();
            GridTerminalSystem.GetBlocksOfType(_cockpits, block => block.IsFunctional && block.IsSameConstructAs(Me));

            var cockpit = _cockpits.FirstOrDefault(block => block.IsUnderControl) ?? _cockpits.FirstOrDefault();
            var monitor = GridTerminalSystem.GetBlockWithName("[AMN STATUS]") as IMyTextPanel;
            if (monitor != null && (monitor.CubeGrid != Me.CubeGrid || !monitor.IsWorking))
                monitor = null;
            if ((cockpit == null || cockpit.SurfaceCount == 0) && monitor == null)
                return;

            var display = new StringBuilder();
            display.AppendLine(_gridName);
            display.AppendLine("RESOURCE MINIMUMS");
            display.AppendLine("Dispatch: " + DispatchGroup);
            display.AppendLine("-----------------");
            if (_minimums.Count == 0)
            {
                display.AppendLine("No minimums configured.");
            }
            else
            {
                foreach (var minimum in _minimums.OrderBy(entry => entry.Key))
                {
                    double current;
                    resources.TryGetValue(minimum.Key, out current);
                    var state = current >= minimum.Value ? "OK" : "LOW";
                    display.AppendLine($"{state} {minimum.Key}");
                    display.AppendLine($"  {current:0.##} / {minimum.Value:0.##}");
                }
            }

            if (_requestIds.Count != 0)
            {
                display.AppendLine();
                display.AppendLine("AMN REQUESTS");
                foreach (var request in _requestIds.OrderBy(entry => entry.Key))
                {
                    string state;
                    if (_pendingResourceClears.Contains(request.Key))
                        state = "Cancelling";
                    else if (!_requestStates.TryGetValue(request.Key, out state))
                        state = "Awaiting broker";
                    display.AppendLine(request.Key + " | " + state);
                }
            }

            var text = display.ToString();
            if (cockpit != null && cockpit.SurfaceCount > 0)
                WriteRequestDisplay(cockpit.GetSurface(0), text);
            if (monitor != null)
                WriteRequestDisplay(monitor, text);
        }

        private static void WriteRequestDisplay(IMyTextSurface surface, string text)
        {
            surface.ContentType = ContentType.TEXT_AND_IMAGE;
            surface.Font = "Monospace";
            surface.FontSize = 0.8f;
            surface.Alignment = TextAlignment.LEFT;
            surface.WriteText(text);
        }

        private void UpdateMinimumTasks(Dictionary<string, double> resources, bool retryBrokerRequests)
        {
            bool retryOutstanding = retryBrokerRequests || _tickCount % 60 == 1;
            var currentLowResources = new HashSet<string>();
            var stateChanged = false;
            var newClears = new HashSet<string>();
            foreach (var minimum in _minimums)
            {
                double current;
                resources.TryGetValue(minimum.Key, out current);
                if (current < minimum.Value)
                {
                    var createdRequest = false;
                    currentLowResources.Add(minimum.Key);
                    if (!_lowResources.Contains(minimum.Key))
                        stateChanged = true;
                    // Cancellation is retried until acknowledged, even if demand returns.
                    if (_pendingResourceClears.Contains(minimum.Key))
                        continue;
                    string requestId;
                    if (!_requestIds.TryGetValue(minimum.Key, out requestId))
                    {
                        requestId = Me.EntityId.ToString(CultureInfo.InvariantCulture) + "-" + (++_requestSequence).ToString(CultureInfo.InvariantCulture);
                        _requestIds[minimum.Key] = requestId;
                        createdRequest = true;
                        SaveMinimums();
                    }

                    if (createdRequest || retryOutstanding)
                        SendResourceRequest(minimum.Key, current, minimum.Value, requestId);
                }
                else
                {
                    string requestId;
                    if (!_requestIds.TryGetValue(minimum.Key, out requestId) || _pendingResourceClears.Contains(minimum.Key))
                        continue;

                    if (_dispatchToPoint)
                    {
                        if (_lowResources.Contains(minimum.Key) || retryOutstanding)
                            SendMessage("TASK_QUERY", "{\"request_id\":" +
                                TaskBusIgcHelper.ToJsonString(requestId) + ",\"stock\":" +
                                current.ToString("R", CultureInfo.InvariantCulture) + "}", _brokerId);
                    }
                    else
                    {
                        _pendingResourceClears.Add(minimum.Key);
                        newClears.Add(minimum.Key);
                        stateChanged = true;
                    }
                }
            }

            _lowResources.Clear();
            foreach (var resource in currentLowResources)
                _lowResources.Add(resource);
            if (stateChanged)
                SaveMinimums();

            var clearsToSend = retryOutstanding ? _pendingResourceClears : newClears;
            foreach (var resource in clearsToSend)
                SendResourceClear(resource);
        }

        private string DispatchGroup => _dispatchToPoint ? "MiningPoints" : "MiningFleet";

        private void SendResourceRequest(string resource, double current, double minimum, string requestId)
        {
            var position = Me.CubeGrid.GetPosition();
            var candidate = resource.StartsWith("Ore/", StringComparison.OrdinalIgnoreCase)
                ? TaskBusIgcHelper.FindNearestOre(_oreObservations, resource.Substring(4),
                    position.X, position.Y, position.Z, OreCandidateRadius)
                : null;
            var payload = "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) +
                ",\"group_id\":" + TaskBusIgcHelper.ToJsonString(DispatchGroup) +
                ",\"resource\":" + TaskBusIgcHelper.ToJsonString(resource) +
                ",\"key\":" + TaskBusIgcHelper.ToJsonString(resource) +
                ",\"current\":" + current.ToString("R", CultureInfo.InvariantCulture) +
                ",\"minimum\":" + minimum.ToString("R", CultureInfo.InvariantCulture) +
                ",\"amount\":" + (minimum - current).ToString("R", CultureInfo.InvariantCulture) +
                (_dispatchToPoint ? ",\"receipt_required\":true,\"site_required\":true,\"steps\":[\"survey\",\"mine\",\"transport\"]" :
                    ",\"steps\":[\"survey\",\"mine\",\"intermediate\",\"deliver\"]") +
                (candidate == null ? "" : ",\"ore_candidate_gps\":" + TaskBusIgcHelper.ToJsonString(candidate)) +
                ",\"base_grid_id\":" + Me.CubeGrid.EntityId.ToString(CultureInfo.InvariantCulture) +
                ",\"base_grid_name\":" + TaskBusIgcHelper.ToJsonString(_gridName) +
                ",\"base_position\":{\"x\":" + position.X.ToString("R", CultureInfo.InvariantCulture) +
                ",\"y\":" + position.Y.ToString("R", CultureInfo.InvariantCulture) +
                ",\"z\":" + position.Z.ToString("R", CultureInfo.InvariantCulture) + "}}";
            SendMessage("TASK_PUBLISH", payload, _brokerId, requestId);
        }

        private void SendResourceClear(string resource)
        {
            string requestId;
            if (!_requestIds.TryGetValue(resource, out requestId))
                return;
            SendMessage("TASK_CANCEL", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) + "}", _brokerId, "cancel:" + requestId);
        }

        private void HandleIncoming(long source, string json, bool viaGalnet)
        {
            string eventType;
            string payloadJson;
            if (string.IsNullOrEmpty(json) || !TaskBusIgcHelper.TryParseEnvelope(json, out eventType, out payloadJson))
                return;

            string targetJson;
            long targetId = 0;
            if (TaskBusIgcHelper.TryGetJsonProperty(json, "target_id", out targetJson) &&
                long.TryParse(targetJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out targetId) &&
                targetId != 0 && targetId != Me.EntityId)
                return;

            if (eventType == "ORE_OBSERVATION" || eventType == "ORE_OBSERVATION_ACK")
            {
                if (targetId == Me.EntityId)
                    HandleSurveyTransfer(source, eventType, payloadJson);
                return;
            }

            bool brokerReply = eventType == "TASK_PUBLISHED_ACK" || eventType == "TASK_CANCEL_ACK" ||
                eventType == "TASK_REJECTED";
            if (brokerReply && ((_brokerId != 0 && source != _brokerId) ||
                (_galnet.IsConfigured && !viaGalnet)))
                return;

            if (eventType == "PING")
                SendMessage("PONG", "null");
            else if (eventType == "TASK_PUBLISHED_ACK")
            {
                string requestIdJson;
                string requestId;
                if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "request_id", out requestIdJson) &&
                    TaskBusIgcHelper.TryReadJsonString(requestIdJson, out requestId))
                {
                    string stateJson;
                    string requestState = null;
                    bool hasState = TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "state", out stateJson) &&
                        TaskBusIgcHelper.TryReadJsonString(stateJson, out requestState);
                    var resource = _requestIds.FirstOrDefault(entry => entry.Value == requestId).Key;
                    if (hasState && !string.IsNullOrEmpty(resource) && !_pendingResourceClears.Contains(resource))
                    {
                        if (requestState == "DONE" || requestState == "CANCELLED")
                        {
                            _requestIds.Remove(resource);
                            _requestStates.Remove(resource);
                            _lowResources.Remove(resource);
                        }
                        else
                        {
                            string raw;
                            string phase;
                            string childState;
                            if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "phase", out raw) &&
                                TaskBusIgcHelper.TryReadJsonString(raw, out phase) &&
                                TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "child_state", out raw) &&
                                TaskBusIgcHelper.TryReadJsonString(raw, out childState))
                                _requestStates[resource] = requestState + " / " + phase + " " + childState;
                            else
                                _requestStates[resource] = requestState;
                            if (requestState == "UNKNOWN" && _dispatchToPoint && !_lowResources.Contains(resource))
                                QueueResourceClear(resource);
                        }
                    }
                    SaveMinimums();
                }
            }
            else if (eventType == "TASK_CANCEL_ACK")
            {
                string requestIdJson;
                string requestId;
                if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "request_id", out requestIdJson) && TaskBusIgcHelper.TryReadJsonString(requestIdJson, out requestId))
                {
                    var resource = _requestIds.FirstOrDefault(entry => entry.Value == requestId).Key;
                    if (!string.IsNullOrEmpty(resource) && _pendingResourceClears.Remove(resource))
                    {
                        _requestIds.Remove(resource);
                        _requestStates.Remove(resource);
                        SaveMinimums();
                    }
                }
            }
            else if (eventType == "TASK_REJECTED")
            {
                string raw;
                string requestId;
                string reason;
                if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "request_id", out raw) &&
                    TaskBusIgcHelper.TryReadJsonString(raw, out requestId) &&
                    TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "reason", out raw) &&
                    TaskBusIgcHelper.TryReadJsonString(raw, out reason))
                {
                    var resource = _requestIds.FirstOrDefault(entry => entry.Value == requestId).Key;
                    if (resource != null)
                        _requestStates[resource] = "Rejected: " + reason;
                }
                Echo("Broker rejected resource request: " + payloadJson);
            }
            else if (eventType == "MSG")
            {
                string text;
                Echo(TaskBusIgcHelper.TryReadJsonString(payloadJson, out text)
                    ? $"From PB {source}: {text}"
                    : $"Invalid MSG payload from PB {source}");
            }
            else if (eventType != "TASK_ASSIGN" && eventType != "TASK_ACCEPTED" && eventType != "TASK_DONE" &&
                eventType != "TASK_POLL" && eventType != "TASK_PUBLISH" && eventType != "TASK_CANCEL" &&
                eventType != "TASK_CANCEL_ACK" && eventType != "TASK_PUBLISHED_ACK" && eventType != "TASK_REJECTED")
                Echo($"From PB {source}: {eventType} {payloadJson}");
        }

        private void SendEnvelope(string channel, string envelope, long targetId)
        {
            TaskBusEnvelope message;
            if (_galnet.IsConfigured && TaskBusProtocol.TryParseEnvelope(envelope, out message) &&
                (message.EventType == "TASK_PUBLISH" || message.EventType == "TASK_QUERY" ||
                    message.EventType == "TASK_CANCEL"))
                _galnet.Send(envelope, targetId);
            else
                IGC.SendBroadcastMessage(channel, envelope, TransmissionDistance.TransmissionDistanceMax);
        }

        private void SendMessage(string eventType, string payloadJson, long targetId = 0, string messageId = null)
        {
            var envelope = _taskBusProducer.Publish(eventType, payloadJson, messageId, targetId);
            Echo($"Sent: {envelope}");
        }

        #region PreludeFooter
    }
}
#endregion