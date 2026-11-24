#region Prelude
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using VRageMath;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;
using SpaceEngineers.Libs.IgcCore;

namespace SpaceEngineers.Apps.ShipLink
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private const string FleetTag = "SE1-FLEET-STATUS";
        private const int DisplayUpdateIntervalTicks = 60;
        private const int FleetTimeoutTicks = 300;
        private const int MaxFleetPeers = 16;

        private sealed class FleetStatus
        {
            public string Name;
            public string Speed;
            public bool Piloted;
            public int SeenTick;
        }

        private string _fleetName;
        private readonly Dictionary<long, FleetStatus> _fleetPeers = new Dictionary<long, FleetStatus>();

        private string _shipName;
        private string _baseName = "Base";
        private bool _connectedToBase;
        private int _lastMessageTick;
        private string _lastEventType;
        private string _lastPayload;
        private string _activeTaskId;
        private string _activeTaskStage;
        private string _activeTaskDetails;
        private int _tick;
        private int _displayTick;
        private IMyBroadcastListener _listener;
        private IMyBroadcastListener _fleetListener;
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();
        private readonly List<IMyCockpit> _cockpits = new List<IMyCockpit>();
        private readonly List<IMyTextSurface> _surfaces = new List<IMyTextSurface>();

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update1;
            _shipName = Me.CubeGrid.CustomName;
            _fleetName = Storage;
            _listener = IGC.RegisterBroadcastListener(CommunicationTag);
            _fleetListener = IGC.RegisterBroadcastListener(FleetTag);
        }

        public void Main(string argument)
        {
            _tick++;
            _displayTick++;

            var command = (argument ?? string.Empty).Trim();
            if (command.StartsWith("base ", StringComparison.OrdinalIgnoreCase))
                _baseName = command.Substring(5).Trim();
            else if (command.StartsWith("fleet ", StringComparison.OrdinalIgnoreCase))
            {
                var name = command.Substring(6).Trim();
                _fleetName = string.Equals(name, "off", StringComparison.OrdinalIgnoreCase) ? "" : name;
                Storage = _fleetName;
                _fleetPeers.Clear();
            }

            // Scan antennas
            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas, antenna => antenna.IsFunctional && antenna.Enabled);

            // Scan cockpits and collect text surfaces
            _cockpits.Clear();
            _surfaces.Clear();
            GridTerminalSystem.GetBlocksOfType(_cockpits, cockpit => cockpit.IsFunctional);
            foreach (var cockpit in _cockpits)
            {
                for (int i = 0; i < cockpit.SurfaceCount; i++)
                {
                    var surface = cockpit.GetSurface(i);
                    if (surface.ContentType == ContentType.TEXT_AND_IMAGE ||
                        surface.ContentType == ContentType.TEXT_AND_IMAGE)
                    {
                        _surfaces.Add(surface);
                    }
                }
            }

            // Process IGC messages
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

                _connectedToBase = true;
                _lastMessageTick = _tick;
                _lastEventType = eventType;
                _lastPayload = payloadJson;

                if (eventType == "TASK_ASSIGN")
                {
                    ParseTaskAssignment(payloadJson);
                }
                else if (eventType == "TASK_DONE_ACK")
                {
                    _activeTaskId = null;
                    _activeTaskStage = null;
                    _activeTaskDetails = null;
                }
                else if (eventType == "TASK_REVOKED")
                {
                    _activeTaskId = null;
                    _activeTaskStage = null;
                    _activeTaskDetails = null;
                }
                else if (eventType == "QUEUE_EMPTY")
                {
                    if (string.IsNullOrEmpty(_activeTaskId))
                    {
                        _lastEventType = "QUEUE_EMPTY";
                    }
                }
            }

            while (_fleetListener.HasPendingMessage)
            {
                var message = _fleetListener.AcceptMessage();
                string eventType;
                string payload;
                if (message.Source != Me.EntityId && !string.IsNullOrEmpty(_fleetName) &&
                    TaskBusIgcHelper.TryParseEnvelope(message.Data as string, out eventType, out payload) &&
                    eventType == "FLEET_STATUS")
                    ReceiveFleetStatus(message.Source, payload);
            }

            // Connection timeout: if no message for 300 ticks (~5 seconds at Update1)
            if (_tick - _lastMessageTick > 300)
            {
                _connectedToBase = false;
            }

            // Update display periodically
            if (_displayTick >= DisplayUpdateIntervalTicks)
            {
                _displayTick = 0;
                UpdateFleetStatus();
                UpdateCockpitDisplay();
            }
        }

        private void UpdateFleetStatus()
        {
            if (string.IsNullOrEmpty(_fleetName))
                return;

            foreach (var peer in _fleetPeers.Where(entry => _tick - entry.Value.SeenTick > FleetTimeoutTicks).ToList())
                _fleetPeers.Remove(peer.Key);

            if (_antennas.Count == 0)
                return;
            var cockpit = _cockpits.FirstOrDefault(block => block.IsUnderControl) ?? _cockpits.FirstOrDefault();
            var speed = cockpit == null ? "unknown" : cockpit.GetShipSpeed().ToString("0.0", CultureInfo.InvariantCulture);
            var payload = "{\"fleet\":" + TaskBusIgcHelper.ToJsonString(_fleetName) +
                ",\"name\":" + TaskBusIgcHelper.ToJsonString(_shipName.Substring(0, Math.Min(_shipName.Length, 64))) +
                ",\"speed\":" + TaskBusIgcHelper.ToJsonString(speed) +
                ",\"piloted\":" + (cockpit != null && cockpit.IsUnderControl ? "true" : "false") + "}";
            IGC.SendBroadcastMessage(FleetTag, TaskBusProtocol.CreateEnvelope("FLEET_STATUS", payload),
                TransmissionDistance.TransmissionDistanceMax);
        }

        private void ReceiveFleetStatus(long source, string payload)
        {
            if (string.IsNullOrEmpty(_fleetName))
                return;

            string value;
            string fleet;
            string name;
            string speed;
            if (!TaskBusIgcHelper.TryGetJsonProperty(payload, "fleet", out value) ||
                !TaskBusIgcHelper.TryReadJsonString(value, out fleet) || fleet != _fleetName ||
                !TaskBusIgcHelper.TryGetJsonProperty(payload, "name", out value) ||
                !TaskBusIgcHelper.TryReadJsonString(value, out name) || name.Length > 64 ||
                !TaskBusIgcHelper.TryGetJsonProperty(payload, "speed", out value) ||
                !TaskBusIgcHelper.TryReadJsonString(value, out speed) || speed.Length > 12 ||
                !TaskBusIgcHelper.TryGetJsonProperty(payload, "piloted", out value) ||
                (value != "true" && value != "false"))
                return;

            FleetStatus status;
            if (!_fleetPeers.TryGetValue(source, out status))
            {
                if (_fleetPeers.Count >= MaxFleetPeers)
                    return;
                status = new FleetStatus();
                _fleetPeers.Add(source, status);
            }
            status.Name = name;
            status.Speed = speed;
            status.Piloted = value == "true";
            status.SeenTick = _tick;
        }

        private void ParseTaskAssignment(string payloadJson)
        {
            string taskIdJson;
            string taskId;
            if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "task_id", out taskIdJson) &&
                TaskBusIgcHelper.TryReadJsonString(taskIdJson, out taskId))
            {
                _activeTaskId = taskId;
            }

            string detailsJson;
            string details;
            if (TaskBusIgcHelper.TryGetJsonProperty(payloadJson, "details", out detailsJson) &&
                TaskBusIgcHelper.TryReadJsonString(detailsJson, out details))
            {
                _activeTaskDetails = details;
            }

            // Try to extract stage from details if it's a JSON with steps
            string stepsJson;
            List<string> steps;
            if (TaskBusProtocol.TryGetProperty(detailsJson, "steps", out stepsJson) &&
                TaskBusProtocol.TryReadStringArray(stepsJson, out steps) &&
                steps.Count > 0)
            {
                _activeTaskStage = steps[0];
            }
            else
            {
                _activeTaskStage = "execute";
            }
        }

        private void UpdateCockpitDisplay()
        {
            if (_surfaces.Count == 0)
                return;

            var display = new StringBuilder();
            display.AppendLine("=== SHIP-LINK STATUS ===");
            display.AppendLine($"Ship: {_shipName}");
            display.AppendLine($"Base: {_baseName}");
            display.AppendLine();

            // Connection status
            if (_connectedToBase)
            {
                display.AppendLine("Connection: ONLINE");
                display.AppendLine($"Last event: {_lastEventType}");
                display.AppendLine($"Ticks since last msg: {_tick - _lastMessageTick}");
            }
            else
            {
                display.AppendLine("Connection: OFFLINE");
                display.AppendLine("No IGC signal from base.");
            }

            display.AppendLine();

            // Antenna status
            var functionalAntennas = _antennas.Count(a => a.IsFunctional && a.Enabled);
            display.AppendLine($"Antennas: {functionalAntennas} active");

            display.AppendLine();

            // Active task
            if (!string.IsNullOrEmpty(_activeTaskId))
            {
                display.AppendLine("=== ACTIVE TASK ===");
                display.AppendLine($"Task ID: {_activeTaskId}");
                display.AppendLine($"Stage: {_activeTaskStage}");
                if (!string.IsNullOrEmpty(_activeTaskDetails))
                {
                    display.AppendLine($"Details: {_activeTaskDetails}");
                }
            }
            else
            {
                display.AppendLine("=== ACTIVE TASK ===");
                display.AppendLine("No active task.");
                display.AppendLine("Waiting for TASK_ASSIGN from base...");
            }

            display.AppendLine();
            if (!string.IsNullOrEmpty(_fleetName))
            {
                display.AppendLine("=== FLEET " + _fleetName + " ===");
                if (_fleetPeers.Count == 0)
                    display.AppendLine("No recent wingmate reports.");
                foreach (var peer in _fleetPeers.OrderBy(entry => entry.Value.Name))
                {
                    var status = peer.Value;
                    display.AppendLine(status.Name + " | " + status.Speed + " m/s | " +
                        (status.Piloted ? "piloted" : "unoccupied"));
                }
            }
            display.AppendLine($"Tick: {_tick}");

            var text = display.ToString();
            foreach (var surface in _surfaces)
            {
                surface.ContentType = ContentType.TEXT_AND_IMAGE;
                surface.Font = "Monospace";
                surface.FontSize = 0.8f;
                surface.Alignment = TextAlignment.LEFT;
                surface.WriteText(text);
            }
        }

        #region PreludeFooter
    }
}
#endregion