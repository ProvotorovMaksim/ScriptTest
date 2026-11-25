using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using System.Linq;
using System.Text;
using VRageMath;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;

namespace SpaceEngineers.Apps.ShipLink
{
    using SpaceEngineers.Libs.IgcCore;

    public sealed class Program : MyGridProgram
    {
        // PB BODY START
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
            public Vector3D? AssistancePosition;
            public int SeenTick;
            public long Expires;
        }

        private const string HazardCameraName = "[SHIPLINK SCOUT]";
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
            public bool Hazard;
            public Vector3D Position;
            public string RadarMotion;
        }
        private sealed class AiDetection
        {
            public long EntityId;
            public long Expires;
        }

        private readonly Dictionary<long, AiDetection> _aiDetections = new Dictionary<long, AiDetection>();
        private sealed class WeaponProfile
        {
            public string Name;
            public double Speed;
        }
        private readonly List<WeaponProfile> _weaponProfiles = new List<WeaponProfile>();
        private int _weaponSelected = -1;
        private int _weaponCursor;
        private bool _showWeaponSettings;
        private string _lastFcsSample;
        private string _journalSelection;
        private sealed class PendingObservation
        {
            public string Gps;
            public long Expires;
            public readonly HashSet<long> Peers = new HashSet<long>();
        }
        private readonly Dictionary<string, PendingObservation> _pendingObservations =
            new Dictionary<string, PendingObservation>();
        private bool _autoTurret;
        private long _nextTurretSample;
        private readonly List<FleetContact> _contacts = new List<FleetContact>();
        private readonly Dictionary<string, long> _withdrawnMarks = new Dictionary<string, long>();
        private FleetContact _trackedContact;
        private long _trackedAssistanceRelay;

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
            var settings = Storage.Split('\n');
            _fleetName = settings[0];
            if (settings.Length > 1)
                long.TryParse(settings[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _workerId);
            if (settings.Length > 2)
                long.TryParse(settings[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _brokerId);
            LoadWithdrawnMarks(settings);
            const string heartbeatSetting = "GalnetFleetPeer=";
            foreach (var line in Me.CustomData.Split('\n'))
            {
                const string profileSetting = "WeaponProfile=";
                if (line.StartsWith(profileSetting, StringComparison.OrdinalIgnoreCase))
                    ReadWeaponProfile(line.Substring(profileSetting.Length));
                if (string.Equals(line.Trim(), "ShareTurretContacts=true", StringComparison.OrdinalIgnoreCase))
                    _autoTurret = true;
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

            if (command.StartsWith("contact publish ", StringComparison.Ordinal))
            {
                PublishLocalObservation("Radar", command.Substring(16));
                return;
            }

            if (command.Length == 0)
            {
                SampleTurret();
                FlushObservations();
            }
            _tick++;
            _displayTick++;
            if (command.StartsWith("base ", StringComparison.OrdinalIgnoreCase))
                _baseName = command.Substring(5).Trim();
            else if (command.StartsWith("fleet ", StringComparison.OrdinalIgnoreCase))
            {
                var name = command.Substring(6).Trim();
                var nextFleet = string.Equals(name, "off", StringComparison.OrdinalIgnoreCase) ? "" : name;
                if (_fleetName != nextFleet)
                    _withdrawnMarks.Clear();
                _fleetName = nextFleet;
                _needsAssistance = false;
                _fleetPeers.Clear();
                _galnetPeers.Clear();
                _lastHeartbeatTick = _tick - HeartbeatIntervalTicks;
                _contacts.Clear();
                _journalSelection = null;
                _pendingObservations.Clear();
                _aiDetections.Clear();
                _trackedContact = null;
                _trackedAssistanceRelay = 0;
                SaveSettings();
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
                    _lastHeartbeatTick = _tick - HeartbeatIntervalTicks;
                    _displayTick = DisplayUpdateIntervalTicks;
                }
            }

            else if (command.StartsWith("assist send ", StringComparison.OrdinalIgnoreCase))
                SendAssistance(command.Substring(12).Trim());
            else if (command.StartsWith("assist clear ", StringComparison.OrdinalIgnoreCase))
                SendFleetWithdrawal(command.Substring(13).Trim(), "assist");
            else if (command.StartsWith("assist track ", StringComparison.OrdinalIgnoreCase))
                TrackAssistance(command.Substring(13).Trim());
            else if (command.StartsWith("contact clear ", StringComparison.OrdinalIgnoreCase))
                SendFleetWithdrawal(command.Substring(14).Trim(), "contact");
            else if (command.StartsWith("hazard clear ", StringComparison.OrdinalIgnoreCase))
                SendFleetWithdrawal(command.Substring(13).Trim(), "hazard");
            else if (command.StartsWith("hazard scan ", StringComparison.OrdinalIgnoreCase))
                ScanAndShareHazard(command.Substring(12).Trim());
            else if (string.Equals(command, "settings", StringComparison.OrdinalIgnoreCase))
            {
                _showWeaponSettings = !_showWeaponSettings;
                _weaponCursor = Math.Max(0, _weaponSelected);
                _displayTick = DisplayUpdateIntervalTicks;
            }
            else if (string.Equals(command, "up", StringComparison.OrdinalIgnoreCase))
                MovePilotSelection(-1);
            else if (string.Equals(command, "down", StringComparison.OrdinalIgnoreCase))
                MovePilotSelection(1);
            else if (string.Equals(command, "apply", StringComparison.OrdinalIgnoreCase))
            {
                if (_showWeaponSettings && _weaponProfiles.Count > 0)
                {
                    _weaponSelected = _weaponCursor;
                    _showWeaponSettings = false;
                    _displayTick = DisplayUpdateIntervalTicks;
                }
                else if (!_showWeaponSettings)
                    ApplyJournalEntry();
            }
            else if (command.StartsWith("contact ai ", StringComparison.OrdinalIgnoreCase))
                ShareAiDetection(command.Substring(11).Trim());
            else if (command.StartsWith("contact cockpit ", StringComparison.OrdinalIgnoreCase))
                ShareCockpitContact(command.Substring(16).Trim());
            else if (command.StartsWith("contact turret ", StringComparison.OrdinalIgnoreCase))
                ShareTurretContact(command.Substring(15).Trim());
            else if (string.Equals(command, "contact acquire", StringComparison.OrdinalIgnoreCase))
                CueLocalRadar();
            else if (command.StartsWith("contact track ", StringComparison.OrdinalIgnoreCase))
                TrackFleetContact(command.Substring(14).Trim());
            else if (string.Equals(command, "contact untrack", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "assist untrack", StringComparison.OrdinalIgnoreCase))
            {
                _trackedContact = null;
                _trackedAssistanceRelay = 0;
                _displayTick = DisplayUpdateIntervalTicks;
            }
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
                UpdateFcsTarget();
                UpdateCockpitDisplay();
            }
        }

        private IMyProgrammableBlock FindGalnetRelay()
        {
            var relay = GridTerminalSystem.GetBlockWithName(RelayName) as IMyProgrammableBlock;
            return relay != null && relay.CubeGrid == Me.CubeGrid && relay.IsWorking && relay.EntityId != Me.EntityId
                ? relay : null;
        }

        private void TrackFleetContact(string argument)
        {
            int delimiter = argument.IndexOf(' ');
            long relay;
            if (delimiter < 1 || !long.TryParse(argument.Substring(0, delimiter), out relay))
            {
                Echo("Use contact track <relay ID> <mark name>.");
                return;
            }

            string name = argument.Substring(delimiter + 1).Trim();
            long now = DateTime.UtcNow.Ticks;
            var mark = _contacts.Where(contact => contact.Source == relay && contact.Name == name && contact.Expires > now)
                .OrderBy(contact => contact.Assistance).FirstOrDefault();
            if (mark == null)
            {
                Echo("No active mark with that relay ID and name.");
                return;
            }

            _trackedContact = mark;
            _trackedAssistanceRelay = 0;
            _displayTick = DisplayUpdateIntervalTicks;
        }

        private void ReadWeaponProfile(string setting)
        {
            var fields = setting.Split('|');
            double speed;
            if (_weaponProfiles.Count >= 12 || fields.Length != 2 ||
                string.IsNullOrWhiteSpace(fields[0]) || fields[0].Trim().Length > 32 ||
                !double.TryParse(fields[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out speed) ||
                !(speed > 0 && speed <= 100000))
                return;
            _weaponProfiles.Add(new WeaponProfile { Name = fields[0].Trim(), Speed = speed });
        }

        private void MovePilotSelection(int step)
        {
            if (!_showWeaponSettings)
                SelectJournalEntry(step);
            else if (_weaponProfiles.Count > 0)
            {
                _weaponCursor = (_weaponCursor + step + _weaponProfiles.Count) % _weaponProfiles.Count;
                _displayTick = DisplayUpdateIntervalTicks;
            }
        }

        private void UpdateFcsTarget()
        {
            long now = DateTime.UtcNow.Ticks;
            var selected = _trackedContact;
            var mark = selected == null ? null : _contacts.FirstOrDefault(candidate =>
                candidate.Expires > now && !candidate.Assistance && !candidate.Hazard &&
                candidate.Source == selected.Source && candidate.Name == selected.Name);
            string command = null;
            long entityId;
            if (_weaponSelected >= 0 && mark != null && mark.RadarMotion != null &&
                TryReadSensedEntityId(mark.Name, out entityId))
            {
                var motion = mark.RadarMotion.Trim().Split(' ');
                long measured;
                if (motion.Length == 4 && long.TryParse(motion[0], out measured) &&
                    measured > 0 && measured <= now && now - measured <= 2 * TimeSpan.TicksPerSecond)
                {
                    var culture = System.Globalization.CultureInfo.InvariantCulture;
                    command = "aim sample " + entityId.ToString(culture) + " " + motion[0] + " " +
                        mark.Position.X.ToString("R", culture) + " " + mark.Position.Y.ToString("R", culture) +
                        " " + mark.Position.Z.ToString("R", culture) + " " + motion[1] + " " + motion[2] +
                        " " + motion[3] + " " + _weaponProfiles[_weaponSelected].Speed.ToString("R", culture);
                }
            }
            if (command == _lastFcsSample)
                return;
            var fcs = GridTerminalSystem.GetBlockWithName("[SHIPLINK FCS]") as IMyProgrammableBlock;
            if (fcs != null && fcs.IsWorking && fcs.CubeGrid == Me.CubeGrid && fcs.EntityId != Me.EntityId &&
                fcs.TryRun(command ?? "aim clear"))
                _lastFcsSample = command;
        }

        private FleetContact[] JournalEntries(long now) =>
            _contacts.Where(mark => !mark.Assistance && !mark.Hazard && mark.Expires > now)
                .OrderBy(mark => mark.Source).ThenBy(mark => mark.Name, StringComparer.Ordinal).ToArray();

        private static string JournalKey(FleetContact mark) => MarkKey(mark.Source, "contact", mark.Name);

        private void SelectJournalEntry(int step)
        {
            var entries = JournalEntries(DateTime.UtcNow.Ticks);
            if (entries.Length == 0)
                return;
            int current = Array.FindIndex(entries, mark => JournalKey(mark) == _journalSelection);
            int next = current < 0 ? (step > 0 ? 0 : entries.Length - 1) :
                (current + step + entries.Length) % entries.Length;
            _journalSelection = JournalKey(entries[next]);
            _displayTick = DisplayUpdateIntervalTicks;
        }

        private void ApplyJournalEntry()
        {
            var entries = JournalEntries(DateTime.UtcNow.Ticks);
            var selected = entries.FirstOrDefault(mark => JournalKey(mark) == _journalSelection);
            if (selected == null)
            {
                Echo("Select a current contact with up or down first.");
                return;
            }
            _trackedContact = selected;
            _trackedAssistanceRelay = 0;
            _displayTick = DisplayUpdateIntervalTicks;
        }

        private void PublishLocalObservation(string source, string gps)
        {
            if (gps.Length > 200)
                return;
            long now = DateTime.UtcNow.Ticks;
            if (!RecordFleetMark(Me.EntityId, "contact", gps, now + ContactLifetime, now) ||
                string.IsNullOrEmpty(_fleetName) || _fleetName.Length > 64 || _heartbeatRelays.Count == 0)
                return;
            var observation = new PendingObservation { Gps = gps, Expires = now + 2 * TimeSpan.TicksPerSecond };
            observation.Peers.UnionWith(_heartbeatRelays);
            _pendingObservations[source] = observation;
        }

        private void FlushObservations()
        {
            if (_pendingObservations.Count == 0)
                return;

            var relay = FindGalnetRelay();
            long now = DateTime.UtcNow.Ticks;
            foreach (var entry in _pendingObservations.ToArray())
            {
                var observation = entry.Value;
                if (observation.Expires <= now)
                {
                    _pendingObservations.Remove(entry.Key);
                    continue;
                }
                if (relay == null)
                    continue;
                foreach (long peer in observation.Peers.ToArray())
                    if (peer == relay.EntityId || SendFleetPacket(relay, peer, "contact", observation.Gps, ContactLifetime))
                        observation.Peers.Remove(peer);
                if (observation.Peers.Count == 0)
                    _pendingObservations.Remove(entry.Key);
            }
        }

        private IMyLargeTurretBase FindObservationTurret()
        {
            var turret = GridTerminalSystem.GetBlockWithName("[SHIPLINK TURRET]") as IMyLargeTurretBase;
            return turret != null && turret.IsWorking && turret.CubeGrid == Me.CubeGrid ? turret : null;
        }

        private void SampleTurret()
        {
            if (!_autoTurret)
                return;
            long now = DateTime.UtcNow.Ticks;
            if (now < _nextTurretSample)
                return;
            _nextTurretSample = now + TimeSpan.TicksPerSecond;

            var turret = FindObservationTurret();
            if (turret == null)
                return;
            var hit = turret.GetTargetedEntity();
            if (!hit.IsEmpty() && hit.EntityId != 0)
                PublishLocalObservation("Turret", FormatObservation("Turret", hit, now));
        }

        private void CueLocalRadar()
        {
            var mark = _trackedContact;
            long entityId;
            if (mark == null || mark.Assistance || mark.Hazard || mark.Expires <= DateTime.UtcNow.Ticks ||
                !TryReadSensedEntityId(mark.Name, out entityId))
            {
                Echo("Select a current Radar, Turret or Cockpit mark with contact track first.");
                return;
            }

            var radar = GridTerminalSystem.GetBlockWithName("[RADAR]") as IMyProgrammableBlock;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (radar == null || radar.CubeGrid != Me.CubeGrid || !radar.IsWorking || radar.EntityId == Me.EntityId ||
                !radar.TryRun("acquire " + entityId.ToString(culture) + " " + mark.Expires.ToString(culture) +
                    " " + mark.Position.X.ToString("R", culture) + " " + mark.Position.Y.ToString("R", culture) +
                    " " + mark.Position.Z.ToString("R", culture) + mark.RadarMotion))
                Echo("A working local [RADAR] programmable block is required.");
        }

        private static bool TryReadSensedEntityId(string name, out long entityId)
        {
            entityId = 0;
            int prefixLength = name.StartsWith("Radar ", StringComparison.Ordinal) ? 6 :
                name.StartsWith("Turret ", StringComparison.Ordinal) ? 7 :
                name.StartsWith("Cockpit ", StringComparison.Ordinal) ? 8 : 0;
            return prefixLength > 0 && long.TryParse(name.Substring(prefixLength), out entityId) && entityId != 0;
        }

        private void ShareTurretContact(string argument)
        {
            long relay;
            if (!long.TryParse(argument, out relay) || relay == 0)
            {
                Echo("Use contact turret <relay ID>.");
                return;
            }

            var turret = FindObservationTurret();
            if (turret == null)
            {
                Echo("A working local [SHIPLINK TURRET] is required.");
                return;
            }

            ShareObservation(relay, "Turret", turret.GetTargetedEntity());
        }

        private void ShareAiDetection(string argument)
        {
            long relay;
            if (string.IsNullOrEmpty(_fleetName) || !long.TryParse(argument, out relay) ||
                !_heartbeatRelays.Contains(relay))
            {
                Echo("Join a fleet and use contact ai <configured wingmate relay ID>.");
                return;
            }

            var block = GridTerminalSystem.GetBlockWithName("[SHIPLINK AI]") as IMyOffensiveCombatBlock;
            if (block == null || !block.IsWorking || block.CubeGrid != Me.CubeGrid)
            {
                Echo("A working local [SHIPLINK AI] offensive block is required.");
                return;
            }

            var search = block.SearchEnemyComponent;
            long entityId = search?.FoundEnemyId ?? 0;
            if (entityId == 0)
            {
                Echo("The offensive AI has no current detection.");
                return;
            }

            SendFleetMark(relay, "ai-detection", entityId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private void ShareCockpitContact(string argument)
        {
            var fields = argument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            long relay;
            double range;
            if (fields.Length != 2 || !long.TryParse(fields[0], out relay) || relay == 0 ||
                !double.TryParse(fields[1], System.Globalization.NumberStyles.Float, culture, out range) ||
                !(range >= 1 && range <= 5000))
            {
                Echo("Use contact cockpit <relay ID> <1 to 5000 metres>.");
                return;
            }

            _cockpits.Clear();
            GridTerminalSystem.GetBlocksOfType(_cockpits, block => block.IsFunctional && block.CubeGrid == Me.CubeGrid);
            var cockpit = _cockpits.FirstOrDefault(block => block.IsUnderControl);
            var camera = GridTerminalSystem.GetBlockWithName("[SHIPLINK COCKPIT CAM]") as IMyCameraBlock;
            if (cockpit == null || camera == null || !camera.IsWorking || camera.CubeGrid != Me.CubeGrid ||
                Vector3D.Dot(cockpit.WorldMatrix.Forward, camera.WorldMatrix.Forward) < 0.999)
            {
                Echo("Use an occupied cockpit and a working [SHIPLINK COCKPIT CAM] facing the same way.");
                return;
            }

            camera.EnableRaycast = true;
            if (!camera.CanScan(range))
            {
                Echo("The cockpit camera cannot scan that distance yet.");
                return;
            }

            ShareObservation(relay, "Cockpit", camera.Raycast(range));
        }

        private void ShareObservation(long relay, string source, MyDetectedEntityInfo hit)
        {
            if (hit.IsEmpty() || hit.EntityId == 0)
            {
                Echo("No current contact from " + source + ".");
                return;
            }

            SendContact(relay.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
                FormatObservation(source, hit, DateTime.UtcNow.Ticks));
        }

        private static string FormatObservation(string source, MyDetectedEntityInfo hit, long measured)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var position = hit.Position;
            var velocity = hit.Velocity;
            return "GPS:" + source + " " + hit.EntityId.ToString(culture) + ":" +
                position.X.ToString("0.00", culture) + ":" + position.Y.ToString("0.00", culture) + ":" +
                position.Z.ToString("0.00", culture) + ":RV1:" + measured.ToString(culture) + ":" +
                velocity.X.ToString("R", culture) + ":" + velocity.Y.ToString("R", culture) + ":" +
                velocity.Z.ToString("R", culture) + ":";
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

        private void ScanAndShareHazard(string argument)
        {
            var parts = argument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            long target;
            double range;
            if (string.IsNullOrEmpty(_fleetName) || _fleetName.Length > 64 || parts.Length != 2 ||
                !long.TryParse(parts[0], out target) || !_heartbeatRelays.Contains(target) ||
                !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out range) ||
                !(range >= 1 && range <= 5000))
            {
                Echo("Use hazard scan <configured wingmate relay ID> <1 to 5000 metres>.");
                return;
            }
            var relay = FindGalnetRelay();
            var camera = GridTerminalSystem.GetBlockWithName(HazardCameraName) as IMyCameraBlock;
            if (relay == null || target == relay.EntityId || camera == null ||
                camera.CubeGrid != Me.CubeGrid || !camera.IsWorking)
            {
                Echo("A working local Galnet relay and " + HazardCameraName + " camera are required.");
                return;
            }

            camera.EnableRaycast = true;
            if (!camera.CanScan(range))
            {
                Echo("Camera cannot scan the requested distance yet.");
                return;
            }
            var hit = camera.Raycast(range);
            if (hit.IsEmpty() || !hit.HitPosition.HasValue)
            {
                Echo("No obstacle detected along the camera ray.");
                return;
            }

            var point = hit.HitPosition.Value;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var gps = "GPS:Obstacle:" + point.X.ToString("R", culture) + ":" +
                point.Y.ToString("R", culture) + ":" + point.Z.ToString("R", culture) + ":";
            if (!SendFleetPacket(relay, target, "hazard", gps, ContactLifetime))
                Echo("Could not hand the observation to the local Galnet relay.");
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

            SendFleetMark(target, "assist", GetAssistanceGps());
        }

        private string GetAssistanceGps()
        {
            var position = Me.CubeGrid.GetPosition();
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return "GPS:Assistance:" + position.X.ToString("R", culture) + ":" +
                position.Y.ToString("R", culture) + ":" + position.Z.ToString("R", culture) + ":";
        }

        private void SendFleetWithdrawal(string argument, string kind)
        {
            int split = argument.IndexOf(' ');
            bool named = kind == "contact";
            string id = split < 0 ? argument : argument.Substring(0, split);
            string name = !named || split < 0 ? "" : argument.Substring(split + 1).Trim();
            long target;
            if (string.IsNullOrEmpty(_fleetName) || _fleetName.Length > 64 ||
                !long.TryParse(id, out target) || target == 0 ||
                (named ? name.Length == 0 || name.Length > 64 : split >= 0) ||
                (kind == "hazard" && !_heartbeatRelays.Contains(target)))
            {
                Echo("Use " + kind + " clear <relay ID>" + (named ? " <mark name>." : "."));
                return;
            }
            if (kind == "hazard")
                name = "Obstacle";
            else if (kind == "assist")
                name = "assist";
            SendFleetMark(target, kind + "-clear", name);
        }

        private static string MarkKind(FleetContact mark) => mark.Assistance ? "assist" : mark.Hazard ? "hazard" : "contact";

        private static string MarkKey(long source, string kind, string name) =>
            source.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + kind + (kind == "assist" ? "" : ":" + name);

        private void WithdrawFleetMark(long source, string kind, string name, long expires, long now)
        {
            if (name.Length == 0 || name.Length > 64)
                return;

            foreach (var key in _withdrawnMarks.Where(mark => mark.Value <= now).Select(mark => mark.Key).ToArray())
                _withdrawnMarks.Remove(key);
            var id = MarkKey(source, kind, name);
            long previous;
            if (_withdrawnMarks.TryGetValue(id, out previous) && previous >= expires)
                return;

            if (_withdrawnMarks.Count >= 32 && !_withdrawnMarks.ContainsKey(id))
                _withdrawnMarks.Remove(_withdrawnMarks.OrderBy(mark => mark.Value).First().Key);
            _withdrawnMarks[id] = expires;
            SaveSettings();
            _contacts.RemoveAll(mark => mark.Source == source && MarkKind(mark) == kind &&
                (kind == "assist" || mark.Name == name) && mark.Expires <= expires);
            if (_trackedContact != null && _trackedContact.Source == source && MarkKind(_trackedContact) == kind &&
                (kind == "assist" || _trackedContact.Name == name) && _trackedContact.Expires <= expires)
                _trackedContact = null;

            _displayTick = DisplayUpdateIntervalTicks;
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
                if (fields[0] == "ai-detection")
                {
                    long detectedId;
                    if (!_heartbeatRelays.Contains(source) ||
                        !long.TryParse(TaskBusIgcHelper.DecodeStorage(fields[3]), out detectedId) || detectedId == 0)
                        return;
                    AiDetection previous;
                    if (!_aiDetections.TryGetValue(source, out previous) || previous.Expires < expiry)
                        _aiDetections[source] = new AiDetection { EntityId = detectedId, Expires = expiry };
                    _displayTick = DisplayUpdateIntervalTicks;
                    return;
                }
                if (fields[0] == "contact-clear" || fields[0] == "assist-clear" || fields[0] == "hazard-clear")
                {
                    if (fields[0] != "hazard-clear" || _heartbeatRelays.Contains(source))
                        WithdrawFleetMark(source, fields[0].Substring(0, fields[0].Length - "-clear".Length),
                            TaskBusIgcHelper.DecodeStorage(fields[3]), expiry, now);
                    return;
                }
                bool hazard = fields[0] == "hazard";
                if (hazard ? !_heartbeatRelays.Contains(source) : fields[0] != "contact" && fields[0] != "assist")
                    return;

                RecordFleetMark(source, fields[0], TaskBusIgcHelper.DecodeStorage(fields[3]), expiry, now);
            }
            catch (FormatException) { }
        }

        private bool RecordFleetMark(long source, string kind, string gps, long expiry, long now)
        {
            bool hazard = kind == "hazard";
            string name;
            double x;
            double y;
            double z;
            if (!TaskBusIgcHelper.TryReadOreGps(gps,
                out name, out x, out y, out z) || name.Length > 64)
                return false;

            bool assistance = kind == "assist";
            long withdrawnUntil;
            if (_withdrawnMarks.TryGetValue(MarkKey(source, kind, name), out withdrawnUntil) &&
                withdrawnUntil >= expiry)
                return false;

            _contacts.RemoveAll(contact => contact.Expires <= now);
            var existing = _contacts.FirstOrDefault(contact => contact.Source == source &&
                contact.Assistance == assistance && contact.Hazard == hazard && (assistance || contact.Name == name));
            if (existing == null)
            {
                if (_contacts.Count >= MaxContacts)
                {
                    var oldestContact = assistance
                        ? _contacts.Where(contact => !contact.Assistance).OrderBy(contact => contact.Expires).FirstOrDefault()
                        : null;
                    if (oldestContact == null)
                        return false;
                    _contacts.Remove(oldestContact);
                }
                existing = new FleetContact { Source = source, Name = name, Assistance = assistance, Hazard = hazard };
                _contacts.Add(existing);
            }
            else if (existing.Expires >= expiry)
                return false;

            existing.Expires = expiry;
            existing.Position = new Vector3D(x, y, z);
            existing.RadarMotion = null;
            long entityId;
            if (!assistance && !hazard && gps.Length <= 200 && TryReadSensedEntityId(name, out entityId))
            {
                var gpsFields = gps.Split(':');
                if (gpsFields.Length == 11 && gpsFields[5] == "RV1")
                    existing.RadarMotion = " " + gpsFields[6] + " " + gpsFields[7] + " " +
                        gpsFields[8] + " " + gpsFields[9];
            }
            _displayTick = DisplayUpdateIntervalTicks;
            return true;
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
            var speed = cockpit == null ? "unknown" : cockpit.GetShipSpeed().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            var payload = "{\"fleet\":" + TaskBusIgcHelper.ToJsonString(_fleetName) +
                ",\"name\":" + TaskBusIgcHelper.ToJsonString(_shipName.Substring(0, Math.Min(_shipName.Length, 64))) +
                ",\"speed\":" + TaskBusIgcHelper.ToJsonString(speed) +
                ",\"piloted\":" + (cockpit != null && cockpit.IsUnderControl ? "true" : "false") +
                ",\"needs_assistance\":" + (_needsAssistance ? "true" : "false");
            if (_antennas.Count > 0)
                IGC.SendBroadcastMessage(FleetTag, TaskBusProtocol.CreateEnvelope("FLEET_STATUS", payload + "}"),
                    TransmissionDistance.TransmissionDistanceMax);
            if (heartbeatDue)
            {
                _lastHeartbeatTick = _tick;
                var relay = FindGalnetRelay();
                if (relay != null)
                {
                    var heartbeat = payload + (_needsAssistance
                        ? ",\"assistance_gps\":" + TaskBusIgcHelper.ToJsonString(GetAssistanceGps()) : "") + "}";
                    foreach (long peer in _heartbeatRelays)
                        SendFleetPacket(relay, peer, "heartbeat", heartbeat, HeartbeatLifetime);
                }
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
            status.AssistancePosition = null;
            string gps;
            string label;
            double x;
            double y;
            double z;
            if (expiry != 0 && status.NeedsAssistance &&
                TaskBusIgcHelper.TryGetJsonProperty(payload, "assistance_gps", out value) &&
                TaskBusIgcHelper.TryReadJsonString(value, out gps) &&
                TaskBusIgcHelper.TryReadOreGps(gps, out label, out x, out y, out z) && label == "Assistance")
                status.AssistancePosition = new Vector3D(x, y, z);
            status.SeenTick = _tick;
        }

        private void TrackAssistance(string argument)
        {
            long relay;
            FleetStatus status;
            if (!long.TryParse(argument, out relay) || !_galnetPeers.TryGetValue(relay, out status) ||
                status.Expires <= DateTime.UtcNow.Ticks || !status.NeedsAssistance || !status.AssistancePosition.HasValue)
            {
                Echo("Use assist track <relay ID> for a live Galnet assistance position.");
                return;
            }

            _trackedContact = null;
            _trackedAssistanceRelay = relay;
            _displayTick = DisplayUpdateIntervalTicks;
        }

        private void AppendTrackedAssistance(StringBuilder display, long now)
        {
            if (_trackedAssistanceRelay == 0)
                return;
            FleetStatus status;
            if (!_galnetPeers.TryGetValue(_trackedAssistanceRelay, out status) || status.Expires <= now)
            {
                display.AppendLine("Tracked assistance: waiting for a fresh Galnet heartbeat.");
                return;
            }
            if (!status.NeedsAssistance)
            {
                display.AppendLine("Tracked ship cleared its assistance request.");
                _trackedAssistanceRelay = 0;
                return;
            }
            if (!status.AssistancePosition.HasValue)
            {
                display.AppendLine("Tracked assistance: the current heartbeat has no position.");
                return;
            }

            display.AppendLine("=== TRACKED ASSISTANCE ===");
            long age = Math.Max(0L, (now - (status.Expires - HeartbeatLifetime)) / TimeSpan.TicksPerSecond);
            display.AppendLine(status.Name + " | relay " + _trackedAssistanceRelay + " | " + age + " s old");
            AppendPositionGuide(display, status.AssistancePosition.Value);
        }

        private void AppendTrackedContact(StringBuilder display, long now)
        {
            if (_trackedContact == null)
                return;

            var fresh = _contacts.FirstOrDefault(contact => contact.Source == _trackedContact.Source &&
                contact.Name == _trackedContact.Name && contact.Assistance == _trackedContact.Assistance &&
                contact.Hazard == _trackedContact.Hazard);
            if (fresh != null && fresh.Expires >= _trackedContact.Expires)
                _trackedContact = fresh;

            var mark = _trackedContact;
            bool stale = mark.Expires <= now;
            long age = Math.Max(0L, (now - (mark.Expires - ContactLifetime)) / TimeSpan.TicksPerSecond);
            display.AppendLine("=== TRACKED FLEET MARK ===");
            display.AppendLine(mark.Name + " | relay " + mark.Source + " | " + age + " s old" +
                (stale ? " | LAST KNOWN" : ""));
            AppendPositionGuide(display, mark.Position);
        }

        private void AppendCourseHazard(StringBuilder display, long now)
        {
            const double warningSeconds = 12;
            const double corridorRadiusSquared = 50 * 50;
            var cockpit = _cockpits.FirstOrDefault(block => block.IsUnderControl && block.CubeGrid == Me.CubeGrid) ??
                _cockpits.FirstOrDefault(block => block.CubeGrid == Me.CubeGrid);
            if (cockpit == null)
                return;
            var velocity = cockpit.GetShipVelocities().LinearVelocity;
            double speedSquared = velocity.LengthSquared();
            if (!(speedSquared >= 0.25) || double.IsInfinity(speedSquared))
                return;

            var origin = cockpit.GetPosition();
            FleetContact nearest = null;
            double soonest = warningSeconds;
            double lateral = 0;
            foreach (var mark in _contacts)
            {
                if (!mark.Hazard || mark.Expires <= now)
                    continue;
                var offset = mark.Position - origin;
                double seconds = Vector3D.Dot(offset, velocity) / speedSquared;
                if (!(seconds > 0 && seconds <= soonest))
                    continue;
                double distanceSquared = (offset - velocity * seconds).LengthSquared();
                if (!(distanceSquared <= corridorRadiusSquared))
                    continue;
                nearest = mark;
                soonest = seconds;
                lateral = Math.Sqrt(distanceSquared);
            }

            if (nearest != null)
                display.AppendLine("COURSE HAZARD | " + nearest.Name + " | " +
                    soonest.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s ahead | " +
                    Math.Round(lateral) + " m off course");
        }

        private void AppendPositionGuide(StringBuilder display, Vector3D position)
        {
            var cockpit = _cockpits.FirstOrDefault(block => block.IsUnderControl) ?? _cockpits.FirstOrDefault();
            var delta = position - (cockpit == null ? Me.CubeGrid.GetPosition() : cockpit.GetPosition());
            display.AppendLine("Range to reported position: " + Math.Round(delta.Length()) + " m");
            if (cockpit != null)
            {
                var frame = cockpit.WorldMatrix;
                display.AppendLine("Right / Up / Forward: " + Math.Round(Vector3D.Dot(delta, frame.Right)) + " / " +
                    Math.Round(Vector3D.Dot(delta, frame.Up)) + " / " + Math.Round(Vector3D.Dot(delta, frame.Forward)) + " m");
            }
        }

        private void LoadWithdrawnMarks(string[] settings)
        {
            long now = DateTime.UtcNow.Ticks;
            for (int i = 3; i < settings.Length && _withdrawnMarks.Count < 32; i++)
            {
                var fields = settings[i].Split('|');
                long deadline;
                if (fields.Length != 3 || fields[0] != "MARK_CLEAR" ||
                    !long.TryParse(fields[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out deadline) ||
                    deadline <= now || deadline - now > ContactLifetime)
                    continue;
                try
                {
                    var key = TaskBusIgcHelper.DecodeStorage(fields[1]);
                    if (key.Length > 0 && key.Length <= 100)
                        _withdrawnMarks[key] = deadline;
                }
                catch (FormatException) { }
            }
        }

        private void SaveSettings()
        {
            var saved = new StringBuilder(_fleetName).Append('\n')
                .Append(_workerId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n')
                .Append(_brokerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            long now = DateTime.UtcNow.Ticks;
            foreach (var withdrawal in _withdrawnMarks)
            {
                if (withdrawal.Value > now)
                    saved.Append("\nMARK_CLEAR|").Append(TaskBusIgcHelper.EncodeStorage(withdrawal.Key))
                        .Append('|').Append(withdrawal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            Storage = saved.ToString();
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
                !int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out epoch))
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
                display.AppendLine("Broker: " + (_brokerId == 0 ? "any" : _brokerId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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

            long now = DateTime.UtcNow.Ticks;
            AppendCourseHazard(display, now);

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
                    display.AppendLine("Requested: " + _requestedAmount.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
                if (_deliveryPosition.HasValue)
                {
                    double distance = Vector3D.Distance(Me.CubeGrid.GetPosition(), _deliveryPosition.Value);
                    if (!double.IsInfinity(distance) && !double.IsNaN(distance))
                        display.AppendLine("Range to recorded base: " +
                            (distance / 1000).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " km");
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
                    display.AppendLine(_antennas.Count > 0 || (_heartbeatRelays.Count > 0 && FindGalnetRelay() != null)
                        ? "ASSISTANCE REQUEST ACTIVE" : "ASSISTANCE REQUEST WAITING FOR LINK");
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
                    if (live && status.AssistancePosition.HasValue)
                    {
                        var position = status.AssistancePosition.Value;
                        display.AppendLine("Assistance X/Y/Z: " + Math.Round(position.X, 1) + " / " +
                            Math.Round(position.Y, 1) + " / " + Math.Round(position.Z, 1));
                    }
                }
            }
            _contacts.RemoveAll(contact => contact.Expires <= now);
            AppendTrackedAssistance(display, now);
            foreach (var detection in _aiDetections.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                _aiDetections.Remove(detection);
            foreach (var detection in _aiDetections.OrderBy(pair => pair.Key))
                display.AppendLine("AI detection from " + detection.Key + ": " + detection.Value.EntityId + " (ID only)");

            if (_weaponSelected >= 0)
                display.AppendLine("Weapon profile: " + _weaponProfiles[_weaponSelected].Name);
            if (_showWeaponSettings)
            {
                display.AppendLine("=== WEAPON PROFILES | UP / DOWN / APPLY ===");
                if (_weaponProfiles.Count == 0)
                    display.AppendLine("Add WeaponProfile=Name|speed in Custom Data.");
                for (int index = 0; index < _weaponProfiles.Count; index++)
                    display.AppendLine((index == _weaponCursor ? "> " : "  ") + _weaponProfiles[index].Name +
                        " | " + _weaponProfiles[index].Speed + " m/s");
            }

            var journal = JournalEntries(now);
            if (journal.Length > 0)
            {
                display.AppendLine("=== CONTACT JOURNAL | UP / DOWN / APPLY ===");
                int selected = Array.FindIndex(journal, mark => JournalKey(mark) == _journalSelection);
                int first = Math.Max(0, selected - 3);
                foreach (var entry in journal.Skip(first).Take(8))
                    display.AppendLine((JournalKey(entry) == _journalSelection ? "> " : "  ") +
                        entry.Name + " | " + (entry.Source == Me.EntityId ? "local" : entry.Source.ToString()));
            }

            AppendTrackedContact(display, now);
            if (_contacts.Count > 0)
            {
                display.AppendLine("=== GALNET FLEET MARKS ===");
                foreach (var contact in _contacts.OrderByDescending(entry => entry.Assistance).ThenByDescending(entry => entry.Hazard).ThenByDescending(entry => entry.Expires).Take(8))
                {
                    var point = contact.Position;
                    long ageSeconds = Math.Max(0L, (now - (contact.Expires - ContactLifetime)) / TimeSpan.TicksPerSecond);
                    display.AppendLine((contact.Assistance ? "[ASSIST] " : contact.Hazard ? "[HAZARD] " : "") + contact.Name +
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

        // PB BODY END
    }
}
