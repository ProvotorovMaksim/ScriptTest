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
            public bool NeedsAssistance;
            public int SeenTick;
            public long Expires;
        }

        private const string RelayName = "[GALNET RELAY]";
        private const int MaxContacts = 16;
        private static readonly long HeartbeatLifetime = TimeSpan.FromSeconds(12).Ticks;
        private const int HeartbeatIntervalTicks = 360;
        private readonly HashSet<long> _heartbeatRelays = new HashSet<long>();
        private readonly Dictionary<long, FleetStatus> _galnetPeers = new Dictionary<long, FleetStatus>();
        private int _lastHeartbeatTick = -HeartbeatIntervalTicks;
        private static readonly long ContactLifetime = TimeSpan.FromSeconds(30).Ticks;
        private sealed class FleetContact
        {
            public string Name;
            public long Source;
            public long Expires;
            public bool Assistance;
            public Vector3D Position;
        }
        private readonly List<FleetContact> _contacts = new List<FleetContact>();

        private string _fleetName;
        private bool _needsAssistance;
        private readonly Dictionary<long, FleetStatus> _fleetPeers = new Dictionary<long, FleetStatus>();

        private string _shipName;
        private string _baseName = "Base";
        private bool _connectedToBase;
        private int _lastMessageTick;
        private string _lastEventType;
        private long _workerId;
        private long _brokerId;
        private int _activeLeaseEpoch;
        private string _activeTaskId;
        private string _activeTaskStage;
        private string _activeTaskDetails;
        private double? _requestedAmount;
        private Vector3D? _deliveryPosition;
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
            var settings = Storage.Split(new[] { '\n' }, 3);
            _fleetName = settings[0];
            if (settings.Length > 1)
                long.TryParse(settings[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _workerId);
            if (settings.Length > 2)
                long.TryParse(settings[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _brokerId);
            const string heartbeatSetting = "GalnetFleetPeer=";
            foreach (var line in Me.CustomData.Split('\n'))
            {
                long peer;
                if (_heartbeatRelays.Count < 4 && line.StartsWith(heartbeatSetting, StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(line.Substring(heartbeatSetting.Length), out peer) && peer != 0)
                    _heartbeatRelays.Add(peer);
            }
            _listener = IGC.RegisterBroadcastListener(CommunicationTag);
            _fleetListener = IGC.RegisterBroadcastListener(FleetTag);
        }

        public void Main(string argument)
        {
            var command = (argument ?? string.Empty).Trim();
            if (command.StartsWith("galnet recv ", StringComparison.Ordinal))
            {
                ReceiveFleetMark(command);
                return;
            }

            _tick++;
            _displayTick++;
            if (command.StartsWith("base ", StringComparison.OrdinalIgnoreCase))
                _baseName = command.Substring(5).Trim();
            else if (command.StartsWith("fleet ", StringComparison.OrdinalIgnoreCase))
            {
                var name = command.Substring(6).Trim();
                _fleetName = string.Equals(name, "off", StringComparison.OrdinalIgnoreCase) ? "" : name;
                SaveSettings();
                _needsAssistance = false;
                _fleetPeers.Clear();
                _galnetPeers.Clear();
                _lastHeartbeatTick = _tick - HeartbeatIntervalTicks;
                _contacts.Clear();
            }

            else if (command.StartsWith("worker ", StringComparison.OrdinalIgnoreCase))
            {
                long workerId;
                if (!long.TryParse(command.Substring(7), out workerId) || workerId < 0)
                    Echo("Usage: worker <consumer programmable block ID>, or worker 0 to disable");
                else
                {
                    _workerId = workerId;
                    ResetWorkerStatus();
                    SaveSettings();
                }
            }

            else if (command.StartsWith("broker ", StringComparison.OrdinalIgnoreCase))
            {
                long brokerId;
                if (!long.TryParse(command.Substring(7), out brokerId) || brokerId < 0)
                    Echo("Usage: broker <programmable block ID>, or broker 0 for any broker");
                else
                {
                    _brokerId = brokerId;
                    ResetWorkerStatus();
                    SaveSettings();
                }
            }
            else if (string.Equals(command, "assist on", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(command, "assist off", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(_fleetName))
                    Echo("Join a fleet before requesting assistance.");
                else
                {
                    _needsAssistance = string.Equals(command, "assist on", StringComparison.OrdinalIgnoreCase);
                    _displayTick = DisplayUpdateIntervalTicks;
                }
            }

            else if (command.StartsWith("assist send ", StringComparison.OrdinalIgnoreCase))
                SendAssistance(command.Substring(12).Trim());
            else if (command.StartsWith("contact ", StringComparison.OrdinalIgnoreCase))
                SendContact(command.Substring(8));

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
                if (message.Source == Me.EntityId || (_brokerId != 0 && message.Source != _brokerId))
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

        private IMyProgrammableBlock FindGalnetRelay()
        {
            var relay = GridTerminalSystem.GetBlockWithName(RelayName) as IMyProgrammableBlock;
            return relay != null && relay.CubeGrid == Me.CubeGrid && relay.IsWorking && relay.EntityId != Me.EntityId
                ? relay : null;
        }

        private void SendContact(string argument)
        {
            int delimiter = argument.IndexOf(' ');
            long target;
            string name;
            double x;
            double y;
            double z;
            if (string.IsNullOrEmpty(_fleetName) || _fleetName.Length > 64 ||
                delimiter < 1 || !long.TryParse(argument.Substring(0, delimiter), out target) || target == 0 ||
                argument.Length - delimiter > 200 ||
                !TaskBusIgcHelper.TryReadOreGps(argument.Substring(delimiter + 1), out name, out x, out y, out z) ||
                name.Length > 64)
            {
                Echo("Join a fleet and use contact <relay ID> GPS:Name:X:Y:Z:.");
                return;
            }
            SendFleetMark(target, "contact", argument.Substring(delimiter + 1));
        }

        private void SendAssistance(string argument)
        {
            long target;
            if (string.IsNullOrEmpty(_fleetName) || _fleetName.Length > 64 ||
                !long.TryParse(argument, out target) || target == 0)
            {
                Echo("Join a fleet and use assist send <relay ID>.");
                return;
            }

            var position = Me.CubeGrid.GetPosition();
            var culture = CultureInfo.InvariantCulture;
            var gps = "GPS:Assistance:" + position.X.ToString("R", culture) + ":" +
                position.Y.ToString("R", culture) + ":" + position.Z.ToString("R", culture) + ":";
            SendFleetMark(target, "assist", gps);
        }

        private void SendFleetMark(long target, string kind, string gps)
        {
            var relay = FindGalnetRelay();
            if (relay == null || !SendFleetPacket(relay, target, kind, gps, ContactLifetime))
                Echo("Could not hand the fleet report to the local Galnet relay.");
        }

        private bool SendFleetPacket(IMyProgrammableBlock relay, long target, string kind, string payload, long lifetime)
        {
            long deadline = DateTime.UtcNow.Ticks + lifetime;
            return target != relay.EntityId && relay.TryRun(
                "app-until " + deadline + " " + target + " " + kind + "|" + deadline + "|" +
                TaskBusIgcHelper.EncodeStorage(_fleetName) + "|" +
                TaskBusIgcHelper.EncodeStorage(payload));
        }

        private void ReceiveFleetMark(string command)
        {
            if (string.IsNullOrEmpty(_fleetName) || FindGalnetRelay() == null)
                return;
            var envelope = command.Split(new[] { ' ' }, 5, StringSplitOptions.RemoveEmptyEntries);
            long source;
            if (envelope.Length != 5 || !long.TryParse(envelope[2], out source) || source == 0)
                return;

            var fields = envelope[4].Split('|');
            long expiry;
            long now = DateTime.UtcNow.Ticks;
            if (fields.Length != 4 || !long.TryParse(fields[1], out expiry) || expiry <= now ||
                expiry - now > ContactLifetime)
                return;
            try
            {
                if (TaskBusIgcHelper.DecodeStorage(fields[2]) != _fleetName)
                    return;
                if (fields[0] == "heartbeat")
                {
                    if (_heartbeatRelays.Contains(source) && expiry - now <= HeartbeatLifetime)
                        ReceiveFleetStatus(source, TaskBusIgcHelper.DecodeStorage(fields[3]), expiry);
                    return;
                }
                if (fields[0] != "contact" && fields[0] != "assist")
                    return;

                string name;
                double x;
                double y;
                double z;
                if (!TaskBusIgcHelper.TryReadOreGps(TaskBusIgcHelper.DecodeStorage(fields[3]),
                    out name, out x, out y, out z) || name.Length > 64)
                    return;

                bool assistance = fields[0] == "assist";
                _contacts.RemoveAll(contact => contact.Expires <= now);
                var existing = _contacts.FirstOrDefault(contact => contact.Source == source &&
                    contact.Assistance == assistance && (assistance || contact.Name == name));
                if (existing == null)
                {
                    if (_contacts.Count >= MaxContacts)
                    {
                        var oldestContact = assistance
                            ? _contacts.Where(contact => !contact.Assistance).OrderBy(contact => contact.Expires).FirstOrDefault()
                            : null;
                        if (oldestContact == null)
                            return;
                        _contacts.Remove(oldestContact);
                    }
                    existing = new FleetContact { Source = source, Name = name, Assistance = assistance };
                    _contacts.Add(existing);
                }
                else if (existing.Expires >= expiry)
                    return;

                existing.Expires = expiry;
                existing.Position = new Vector3D(x, y, z);
                _displayTick = DisplayUpdateIntervalTicks;
            }
            catch (FormatException) { }
        }

        private void UpdateFleetStatus()
        {
            if (string.IsNullOrEmpty(_fleetName))
                return;

            foreach (var peer in _fleetPeers.Where(entry => _tick - entry.Value.SeenTick > FleetTimeoutTicks).ToList())
                _fleetPeers.Remove(peer.Key);

            bool heartbeatDue = _fleetName.Length <= 64 && _heartbeatRelays.Count != 0 &&
                _tick - _lastHeartbeatTick >= HeartbeatIntervalTicks;
            if (_antennas.Count == 0 && !heartbeatDue)
                return;
            var cockpit = _cockpits.FirstOrDefault(block => block.IsUnderControl) ?? _cockpits.FirstOrDefault();
            var speed = cockpit == null ? "unknown" : cockpit.GetShipSpeed().ToString("0.0", CultureInfo.InvariantCulture);
            var payload = "{\"fleet\":" + TaskBusIgcHelper.ToJsonString(_fleetName) +
                ",\"name\":" + TaskBusIgcHelper.ToJsonString(_shipName.Substring(0, Math.Min(_shipName.Length, 64))) +
                ",\"speed\":" + TaskBusIgcHelper.ToJsonString(speed) +
                ",\"piloted\":" + (cockpit != null && cockpit.IsUnderControl ? "true" : "false") +
                ",\"needs_assistance\":" + (_needsAssistance ? "true" : "false") + "}";
            if (_antennas.Count > 0)
                IGC.SendBroadcastMessage(FleetTag, TaskBusProtocol.CreateEnvelope("FLEET_STATUS", payload),
                    TransmissionDistance.TransmissionDistanceMax);
            if (heartbeatDue)
            {
                _lastHeartbeatTick = _tick;
                var relay = FindGalnetRelay();
                if (relay != null)
                    foreach (long peer in _heartbeatRelays)
                        SendFleetPacket(relay, peer, "heartbeat", payload, HeartbeatLifetime);
            }
        }

        private void ReceiveFleetStatus(long source, string payload, long expiry = 0)
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

            var peers = expiry == 0 ? _fleetPeers : _galnetPeers;
            FleetStatus status;
            if (!peers.TryGetValue(source, out status))
            {
                if (peers.Count >= MaxFleetPeers)
                    return;
                status = new FleetStatus();
                peers.Add(source, status);
            }
            else if (expiry != 0 && expiry <= status.Expires)
                return;
            status.Expires = expiry;
            status.Name = name;
            status.Speed = speed;
            status.Piloted = value == "true";
            status.NeedsAssistance = TaskBusIgcHelper.TryGetJsonProperty(payload, "needs_assistance", out value) &&
                value == "true";
            status.SeenTick = _tick;
        }

        private void SaveSettings()
        {
            Storage = _fleetName + "\n" + _workerId.ToString(CultureInfo.InvariantCulture) +
                "\n" + _brokerId.ToString(CultureInfo.InvariantCulture);
        }

        private void ResetWorkerStatus()
        {
            _connectedToBase = false;
            ClearTask();
            _displayTick = DisplayUpdateIntervalTicks;
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
            double amount;
            _requestedAmount = TaskBusIgcHelper.TryReadFiniteNumber(details, "amount", out amount) &&
                amount > 0 ? (double?)amount : null;

            string position;
            double x;
            double y;
            double z;
            _deliveryPosition = TaskBusIgcHelper.TryGetJsonProperty(details, "base_position", out position) &&
                TaskBusIgcHelper.TryReadFiniteNumber(position, "x", out x) &&
                TaskBusIgcHelper.TryReadFiniteNumber(position, "y", out y) &&
                TaskBusIgcHelper.TryReadFiniteNumber(position, "z", out z)
                ? (Vector3D?)new Vector3D(x, y, z) : null;
        }

        private void ClearTask()
        {
            _activeTaskId = null;
            _activeTaskStage = null;
            _activeTaskDetails = null;
            _requestedAmount = null;
            _deliveryPosition = null;
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
            if (_workerId != 0)
                display.AppendLine("Broker: " + (_brokerId == 0 ? "any" : _brokerId.ToString(CultureInfo.InvariantCulture)));
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
                if (_requestedAmount.HasValue)
                    display.AppendLine("Requested: " + _requestedAmount.Value.ToString("G", CultureInfo.InvariantCulture));
                if (_deliveryPosition.HasValue)
                {
                    double distance = Vector3D.Distance(Me.CubeGrid.GetPosition(), _deliveryPosition.Value);
                    if (!double.IsInfinity(distance) && !double.IsNaN(distance))
                        display.AppendLine("Range to recorded base: " +
                            (distance / 1000).ToString("0.000", CultureInfo.InvariantCulture) + " km");
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
                if (_needsAssistance)
                    display.AppendLine(_antennas.Count > 0 ? "ASSISTANCE REQUEST ACTIVE" :
                        "ASSISTANCE REQUEST WAITING FOR ANTENNA");
                if (_fleetPeers.Count == 0 && _galnetPeers.Count == 0)
                    display.AppendLine("No recent wingmate reports.");
                foreach (var peer in _fleetPeers.OrderByDescending(entry => entry.Value.NeedsAssistance)
                    .ThenBy(entry => entry.Value.Name))
                {
                    var status = peer.Value;
                    display.AppendLine((status.NeedsAssistance ? "[ASSIST] " : "") +
                        status.Name + " | " + status.Speed + " m/s | " +
                        (status.Piloted ? "piloted" : "unoccupied"));
                }
            }
            long now = DateTime.UtcNow.Ticks;
            if (_galnetPeers.Count > 0)
            {
                display.AppendLine("=== GALNET WINGMATES ===");
                foreach (var peer in _galnetPeers.OrderBy(entry => entry.Key))
                {
                    var status = peer.Value;
                    bool live = status.Expires > now;
                    long age = Math.Max(0L, (now - (status.Expires - HeartbeatLifetime)) / TimeSpan.TicksPerSecond);
                    display.AppendLine((live && status.NeedsAssistance ? "[ASSIST] " : "") + status.Name +
                        " | relay " + peer.Key + " | " + (live ? "online" : "stale") + " | " + age + " s old");
                    display.AppendLine(status.Speed + " m/s | " + (status.Piloted ? "piloted" : "unoccupied"));
                }
            }
            _contacts.RemoveAll(contact => contact.Expires <= now);
            if (_contacts.Count > 0)
            {
                display.AppendLine("=== GALNET FLEET MARKS ===");
                foreach (var contact in _contacts.OrderByDescending(entry => entry.Assistance).ThenByDescending(entry => entry.Expires).Take(8))
                {
                    var point = contact.Position;
                    long ageSeconds = Math.Max(0L, (now - (contact.Expires - ContactLifetime)) / TimeSpan.TicksPerSecond);
                    display.AppendLine((contact.Assistance ? "[ASSIST] " : "") + contact.Name +
                        " | relay " + contact.Source + " | " + ageSeconds + " s old");
                    display.AppendLine("X/Y/Z: " + Math.Round(point.X, 1) + " / " +
                        Math.Round(point.Y, 1) + " / " + Math.Round(point.Z, 1));
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