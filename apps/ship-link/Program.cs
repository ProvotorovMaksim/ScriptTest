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
        private long _workerId;
        private int _activeLeaseEpoch;
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
            var settings = Storage.Split(new[] { '\n' }, 2);
            _fleetName = settings[0];
            if (settings.Length == 2)
                long.TryParse(settings[1], out _workerId);
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
                SaveSettings();
                _fleetPeers.Clear();
            }

            else if (command.StartsWith("worker ", StringComparison.OrdinalIgnoreCase))
            {
                long workerId;
                if (!long.TryParse(command.Substring(7), out workerId) || workerId < 0)
                    Echo("Usage: worker <consumer programmable block ID>, or worker 0 to disable");
                else
                {
                    _workerId = workerId;
                    _connectedToBase = false;
                    ClearTask();
                    SaveSettings();
                }
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

                TaskBusEnvelope envelope;
                if (_workerId != 0 && TaskBusProtocol.TryParseEnvelope(message.Data as string, out envelope) &&
                    envelope.TargetId == _workerId)
                    ReceiveWorkerStatus(envelope);
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

            // Allow for more than one worker poll interval before showing a timeout.
            if (_tick - _lastMessageTick > 900)
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

        private void SaveSettings()
        {
            Storage = _fleetName + "\n" + _workerId.ToString(CultureInfo.InvariantCulture);
        }

        private void ReceiveWorkerStatus(TaskBusEnvelope envelope)
        {
            var eventType = envelope.EventType;
            if (eventType != "TASK_ASSIGN" && eventType != "TASK_DONE_ACK" &&
                eventType != "TASK_CANCEL" && eventType != "TASK_REVOKED" && eventType != "QUEUE_EMPTY")
                return;

            _connectedToBase = true;
            _lastMessageTick = _tick;
            _lastEventType = eventType;
            if (eventType == "QUEUE_EMPTY")
                return;

            string value;
            string taskId;
            int epoch;
            var payload = envelope.PayloadJson;
            if (!TaskBusIgcHelper.TryGetJsonProperty(payload, "task_id", out value) ||
                !TaskBusIgcHelper.TryReadJsonString(value, out taskId) ||
                !TaskBusIgcHelper.TryGetJsonProperty(payload, "lease_epoch", out value) ||
                !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch))
                return;

            if (eventType != "TASK_ASSIGN")
            {
                if (_activeTaskId == taskId && _activeLeaseEpoch == epoch)
                    ClearTask();
                return;
            }
            if (_activeTaskId == taskId && epoch < _activeLeaseEpoch)
                return;

            _activeTaskId = taskId;
            _activeLeaseEpoch = epoch;
            string details;
            string key;
            _activeTaskDetails = TaskBusIgcHelper.TryGetJsonProperty(payload, "key", out value) &&
                TaskBusIgcHelper.TryReadJsonString(value, out key) ? key : null;
            string stepsJson;
            List<string> steps;
            _activeTaskStage = TaskBusIgcHelper.TryGetJsonProperty(payload, "details", out details) &&
                TaskBusProtocol.TryGetProperty(details, "steps", out stepsJson) &&
                TaskBusProtocol.TryReadStringArray(stepsJson, out steps) && steps.Count > 0
                ? steps[0] : "execute";
        }

        private void ClearTask()
        {
            _activeTaskId = null;
            _activeTaskStage = null;
            _activeTaskDetails = null;
            _activeLeaseEpoch = 0;
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
            if (_workerId == 0)
                display.AppendLine("Task monitoring disabled. Run worker <consumer block ID>.");
            else if (_connectedToBase)
            {
                display.AppendLine("Connection: ONLINE");
                display.AppendLine($"Last event: {_lastEventType}");
                display.AppendLine($"Ticks since last msg: {_tick - _lastMessageTick}");
            }
            else
            {
                display.AppendLine("Connection: OFFLINE");
                display.AppendLine("No recent broker response for this worker.");
            }

            display.AppendLine();

            // Antenna status
            var functionalAntennas = _antennas.Count(a => a.IsFunctional && a.Enabled);
            display.AppendLine($"Antennas: {functionalAntennas} active");

            display.AppendLine();

            // Active task
            if (!string.IsNullOrEmpty(_activeTaskId))
            {
                display.AppendLine(_connectedToBase ? "=== ACTIVE TASK ===" : "=== LAST SEEN TASK ===");
                display.AppendLine($"Task ID: {_activeTaskId}");
                display.AppendLine($"Lease: {_activeLeaseEpoch}");
                display.AppendLine($"First workflow stage: {_activeTaskStage}");
                if (!string.IsNullOrEmpty(_activeTaskDetails))
                {
                    display.AppendLine($"Details: {_activeTaskDetails}");
                }
            }
            else
            {
                display.AppendLine("=== ACTIVE TASK ===");
                display.AppendLine("No active task.");
                if (_workerId != 0)
                    display.AppendLine("Waiting for TASK_ASSIGN for this worker...");
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