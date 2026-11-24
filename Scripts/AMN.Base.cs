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
using SpaceEngineers.CommonLibs;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace SpaceEngineers.UWBlockPrograms.SampleMainNs
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private string _gridName;
        private int _tickCount = 0;
        private int _requestSequence;
        private Autopilot _autopilot;
        private Logger _logger;
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();
        private readonly List<IMyCargoContainer> _containers = new List<IMyCargoContainer>();
        private readonly List<IMyAssembler> _assemblers = new List<IMyAssembler>();
        private readonly List<IMyRefinery> _refineries = new List<IMyRefinery>();
        private readonly List<IMyCockpit> _cockpits = new List<IMyCockpit>();
        private readonly List<IMyRemoteControl> _remoteControls = new List<IMyRemoteControl>();
        private readonly Dictionary<string, double> _minimums = new Dictionary<string, double>();
        private readonly Dictionary<string, string> _requestIds = new Dictionary<string, string>();
        private readonly HashSet<string> _lowResources = new HashSet<string>();
        private readonly HashSet<string> _pendingResourceClears = new HashSet<string>();
        private IMyBroadcastListener _listener;
        private TaskBusProducer _taskBusProducer;

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
            _gridName = Me.CubeGrid.CustomName;
            _listener = IGC.RegisterBroadcastListener(CommunicationTag);
            _taskBusProducer = new TaskBusProducer(CommunicationTag, (channel, envelope, targetId) =>
                IGC.SendBroadcastMessage(channel, envelope, TransmissionDistance.TransmissionDistanceMax));

            var autopilotBlockName = "RemCon";
            var programmingBlockName = "Prog";
            _autopilot = new Autopilot(GridTerminalSystem.GetBlockWithName(autopilotBlockName));
            var programBlock = GridTerminalSystem.GetBlockWithName(programmingBlockName) as IMyProgrammableBlock;
            _logger = new Logger(programBlock);
            _logger.Clear();
            _logger.LogTrace("Logger initialized");
            LoadMinimums();
        }
        public void Main(string args)
        {
            _tickCount++;
            var command = args ?? string.Empty;
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
                if (message.Source == Me.EntityId)
                    continue;

                var json = message.Data as string;
                string eventType;
                string payloadJson;
                if (string.IsNullOrEmpty(json) || !TaskBusIgcHelper.TryParseEnvelope(json, out eventType, out payloadJson))
                    continue;

                string targetJson;
                long targetId;
                if (TaskBusIgcHelper.TryGetJsonProperty(json, "target_id", out targetJson) &&
                    long.TryParse(targetJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out targetId) &&
                    targetId != 0 && targetId != Me.EntityId)
                    continue;

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
                        string requestState;
                        var isDone = TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "state", out stateJson) &&
                            TaskBusIgcHelper.TryReadJsonString(stateJson, out requestState) && requestState == "DONE";
                        var isCancelled = TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "state", out stateJson) &&
                            TaskBusIgcHelper.TryReadJsonString(stateJson, out requestState) && requestState == "CANCELLED";
                        var resource = _requestIds.FirstOrDefault(entry => entry.Value == requestId).Key;
                        if ((isDone || isCancelled) && !string.IsNullOrEmpty(resource) && !_pendingResourceClears.Contains(resource))
                        {
                            _requestIds.Remove(resource);
                            _lowResources.Remove(resource);
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
                        if (!string.IsNullOrEmpty(resource) && !_lowResources.Contains(resource) && _pendingResourceClears.Remove(resource))
                        {
                            _requestIds.Remove(resource);
                            SaveMinimums();
                        }
                    }
                }
                else if (eventType == "TASK_REJECTED")
                {
                    Echo("Broker rejected resource request: " + payloadJson);
                }
                else if (eventType == "MSG")
                {
                    string text;
                    Echo(TaskBusIgcHelper.TryReadJsonString(payloadJson, out text)
                        ? $"From PB {message.Source}: {text}"
                        : $"Invalid MSG payload from PB {message.Source}");
                }
                else if (eventType != "TASK_ASSIGN" && eventType != "TASK_ACCEPTED" && eventType != "TASK_DONE" &&
                    eventType != "TASK_POLL" && eventType != "TASK_PUBLISH" && eventType != "TASK_CANCEL" &&
                    eventType != "TASK_CANCEL_ACK" && eventType != "TASK_PUBLISHED_ACK" && eventType != "TASK_REJECTED")
                    Echo($"From PB {message.Source}: {eventType} {payloadJson}");
            }

            //Base_resourse SCAN
            _containers.Clear();
            _assemblers.Clear();
            _refineries.Clear();
            _cockpits.Clear();
            _remoteControls.Clear();

            GridTerminalSystem.GetBlocksOfType(_containers, container => container.IsFunctional);
            GridTerminalSystem.GetBlocksOfType(_assemblers, assembler => assembler.IsFunctional);
            GridTerminalSystem.GetBlocksOfType(_refineries, refinery => refinery.IsFunctional);
            GridTerminalSystem.GetBlocksOfType(_cockpits, cockpit => cockpit.IsFunctional);
            GridTerminalSystem.GetBlocksOfType(_remoteControls, rc => rc.IsFunctional);

            var resources = new Dictionary<string, double>();
            var items = new List<MyInventoryItem>();

            void ScanInventory(IMyInventory inventory)
            {
                if (inventory == null) return;
                items.Clear();
                inventory.GetItems(items);
                foreach (var item in items)
                {
                    var itemName = GetResourceKey(item.Type);
                    var amount = (double)item.Amount;
                    if (resources.ContainsKey(itemName))
                        resources[itemName] += amount;
                    else
                        resources.Add(itemName, amount);
                }
            }

            foreach (var container in _containers) ScanInventory(container.GetInventory());
            foreach (var assembler in _assemblers) ScanInventory(assembler.GetInventory());
            foreach (var refinery in _refineries) ScanInventory(refinery.GetInventory());
            foreach (var cockpit in _cockpits) ScanInventory(cockpit.GetInventory());
            foreach (var rc in _remoteControls) ScanInventory(rc.GetInventory());

            Echo($"Cargo containers: {_containers.Count}, Assemblers: {_assemblers.Count}, Refineries: {_refineries.Count}, Cockpits: {_cockpits.Count}, Remote controls: {_remoteControls.Count}");
            foreach (var resource in resources)
                Echo($"{resource.Key}: {resource.Value:0.##}");

            UpdateMinimumTasks(resources);
            UpdateCockpitPanel(resources);

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
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out minimum) || minimum < 0)
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
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out minimum) && minimum >= 0)
                    _minimums[key] = minimum;
            }
        }

        private void SaveMinimums()
        {
            var storage = new StringBuilder();
            storage.AppendLine("REQSEQ|" + _requestSequence.ToString(CultureInfo.InvariantCulture));
            foreach (var minimum in _minimums.OrderBy(entry => entry.Key))
                storage.AppendLine("MIN|" + minimum.Key + "=" + minimum.Value.ToString(CultureInfo.InvariantCulture));
            foreach (var request in _requestIds.OrderBy(entry => entry.Key))
                storage.AppendLine("REQUEST|" + TaskBusIgcHelper.EncodeStorage(request.Key) + "|" + TaskBusIgcHelper.EncodeStorage(request.Value));
            foreach (var resource in _lowResources.OrderBy(key => key))
                storage.AppendLine("LOW|" + resource);
            foreach (var resource in _pendingResourceClears.OrderBy(key => key))
                storage.AppendLine("CLEAR_PENDING|" + resource);
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
            GridTerminalSystem.GetBlocksOfType(_cockpits, cockpit => cockpit.IsFunctional);

            var cockpit = _cockpits.FirstOrDefault(block => block.IsUnderControl) ?? _cockpits.FirstOrDefault();
            if (cockpit == null || cockpit.SurfaceCount == 0)
            {
                Echo("No cockpit text surface found.");
                return;
            }

            var surface = cockpit.GetSurface(0);
            surface.ContentType = ContentType.TEXT_AND_IMAGE;
            surface.Font = "Monospace";
            surface.FontSize = 0.8f;
            surface.Alignment = TextAlignment.LEFT;

            var display = new StringBuilder();
            display.AppendLine(_gridName);
            display.AppendLine("RESOURCE MINIMUMS");
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

            display.AppendLine();
            display.AppendLine("PB argument:");
            display.AppendLine("set Type/Subtype amount");
            surface.WriteText(display.ToString());
        }

        private void UpdateMinimumTasks(Dictionary<string, double> resources)
        {
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
                    if (_pendingResourceClears.Remove(minimum.Key))
                        stateChanged = true;
                    if (!_lowResources.Contains(minimum.Key))
                        stateChanged = true;
                    string requestId;
                    if (!_requestIds.TryGetValue(minimum.Key, out requestId))
                    {
                        requestId = Me.EntityId.ToString(CultureInfo.InvariantCulture) + "-" + (++_requestSequence).ToString(CultureInfo.InvariantCulture);
                        _requestIds[minimum.Key] = requestId;
                        createdRequest = true;
                        stateChanged = true;
                    }

                    if (createdRequest || _tickCount % 60 == 1 || _tickCount == 1)
                        SendResourceRequest(minimum.Key, current, minimum.Value, requestId);
                }
                else if (_lowResources.Contains(minimum.Key))
                {
                    if (!_pendingResourceClears.Contains(minimum.Key))
                    {
                        _pendingResourceClears.Add(minimum.Key);
                        QueueResourceClear(minimum.Key);
                        newClears.Add(minimum.Key);
                        stateChanged = true;
                    }
                }
            }

            if (_tickCount % 60 == 1 || _tickCount == 1)
            {
                foreach (var resource in _pendingResourceClears)
                    SendResourceClear(resource);
            }
            else
                foreach (var resource in newClears)
                    SendResourceClear(resource);

            _lowResources.Clear();
            foreach (var resource in currentLowResources)
                _lowResources.Add(resource);
            if (stateChanged)
                SaveMinimums();
        }

        private void SendResourceRequest(string resource, double current, double minimum, string requestId)
        {
            var position = Me.CubeGrid.GetPosition();
            var payload = "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) +
                ",\"group_id\":\"MiningFleet\",\"resource\":" + TaskBusIgcHelper.ToJsonString(resource) +
                ",\"key\":" + TaskBusIgcHelper.ToJsonString(resource) +
                ",\"current\":" + current.ToString("R", CultureInfo.InvariantCulture) +
                ",\"minimum\":" + minimum.ToString("R", CultureInfo.InvariantCulture) +
                ",\"amount\":" + (minimum - current).ToString("R", CultureInfo.InvariantCulture) +
                ",\"steps\":[\"survey\",\"mine\",\"intermediate\",\"deliver\"]" +
                ",\"base_grid_id\":" + Me.CubeGrid.EntityId.ToString(CultureInfo.InvariantCulture) +
                ",\"base_grid_name\":" + TaskBusIgcHelper.ToJsonString(_gridName) +
                ",\"base_position\":{\"x\":" + position.X.ToString("R", CultureInfo.InvariantCulture) +
                ",\"y\":" + position.Y.ToString("R", CultureInfo.InvariantCulture) +
                ",\"z\":" + position.Z.ToString("R", CultureInfo.InvariantCulture) + "}}";
            SendMessage("TASK_PUBLISH", payload, 0, requestId);
        }

        private void SendResourceClear(string resource)
        {
            string requestId;
            if (!_requestIds.TryGetValue(resource, out requestId))
                return;
            SendMessage("TASK_CANCEL", "{\"request_id\":" + TaskBusIgcHelper.ToJsonString(requestId) + "}", 0, "cancel:" + requestId);
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