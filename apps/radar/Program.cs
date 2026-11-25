using System;
using VRageMath;
using Sandbox.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;

namespace SpaceEngineers.Apps.Radar
{
    using SpaceEngineers.Libs.RadarCore;

    public sealed class Program : MyGridProgram
    {
        // PB BODY START

        private const double ContactTimeoutSeconds = 5;
        private readonly ContactTracker _contact = new ContactTracker();
        private readonly PositionPredictor _predictor = new PositionPredictor();
        private readonly RadarConfig _config;
        private readonly CameraManager _cameras;
        private readonly GimbalController _gimbal;
        private IMyTextPanel _lcd;
        private bool _scanning;
        private bool _parking;
        private RadarCue _cue;
        private long _nextJournalPublish;
        private string _scanProblem;
        private RadarState _state = RadarState.IDLE;

        public Program()
        {
            string error;
            RadarConfig.TryParse(Me.CustomData, out _config, out error);
            _cameras = new CameraManager(_config);
            _gimbal = new GimbalController(_config);
            Discover();
        }

        public void Main(string argument, UpdateType updateSource)
        {
            var now = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(argument))
                Command(argument.Trim(), now);
            else if (_parking)
                Park(now);
            else if (_scanning)
                Scan(now);
            Render(now);
        }

        private void Discover()
        {
            _cameras.DiscoverCameras(GridTerminalSystem, Me);
            _lcd = GridTerminalSystem.GetBlockWithName("[RADAR LCD]") as IMyTextPanel;
            if (IsGimbal)
                _gimbal.Bind(GridTerminalSystem, Me);
        }

        private void Command(string command, DateTime now)
        {
            switch (command.ToLowerInvariant())
            {
                case "on":
                case "scan":
                    Discover();
                    _scanProblem = null;
                    _parking = false;
                    _cue = null;
                    _scanning = true;
                    _state = RadarState.ACQUIRING;
                    Runtime.UpdateFrequency = UpdateFrequency.Update10;
                    break;
                case "unlock":
                    if (_parking)
                        StopMotion(RadarState.IDLE);
                    _cue = null;
                    _contact.Clear();
                    _gimbal.Stop();
                    _state = _scanning ? RadarState.ACQUIRING : RadarState.IDLE;
                    break;
                case "off":
                    _scanning = false;
                    _parking = false;
                    _scanProblem = null;
                    _cue = null;
                    _contact.Clear();
                    _gimbal.Stop();
                    Runtime.UpdateFrequency = UpdateFrequency.None;
                    _state = RadarState.IDLE;
                    break;
                case "mode fixed":
                case "mode gimbal":
                    if (_parking)
                        StopMotion(RadarState.IDLE);
                    _gimbal.Unbind();
                    _scanProblem = null;
                    _cue = null;
                    _contact.Clear();
                    _config.Mode = command.EndsWith("gimbal", StringComparison.OrdinalIgnoreCase) ? "Gimbal" : "Fixed";
                    _state = _scanning ? RadarState.ACQUIRING : RadarState.IDLE;
                    Discover();
                    break;
                case "gimbal calibrate":
                    Calibrate();
                    break;
                case "gimbal park":
                    if (!IsGimbal)
                    {
                        Echo("Switch to gimbal mode before parking.");
                        break;
                    }
                    Discover();
                    _scanning = false;
                    _parking = true;
                    _cue = null;
                    _contact.Clear();
                    Runtime.UpdateFrequency = UpdateFrequency.Update10;
                    Park(now);
                    break;
                case "status":
                    break;
                default:
                    double range;
                    if (command.StartsWith("range ", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(command.Substring(6), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out range) &&
                        range > 0 && !double.IsInfinity(range))
                        _config.MaxRange = range;
                    else if (command.StartsWith("acquire ", StringComparison.OrdinalIgnoreCase))
                        Acquire(command.Substring(8), now);
                    else if (command.StartsWith("share ", StringComparison.OrdinalIgnoreCase))
                        ShareContact(command.Substring(6), now);
                    else
                        Echo("Radar commands: on, off, scan, unlock, status, mode fixed, mode gimbal, gimbal calibrate, gimbal park, range <meters>, share <relay ID>");
                    break;
            }
        }

        private void Acquire(string argument, DateTime now)
        {
            RadarCue cue;
            if (!RadarCue.TryParse(argument, now.Ticks, out cue))
            {
                Echo("Invalid or expired acquisition cue.");
                return;
            }

            Discover();
            _contact.Lose();
            _scanProblem = null;
            _parking = false;
            _scanning = true;
            _cue = cue;
            _state = RadarState.ACQUIRING;
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
        }

        private void ShareContact(string argument, DateTime now)
        {
            long target;
            if (!long.TryParse(argument, out target) || target == 0)
            {
                Echo("Use share <relay ID>.");
                return;
            }
            string shareCommand;
            if (!_contact.TryFormatShareCommand(target, now, out shareCommand))
            {
                Echo("Sharing requires a radar measurement within the last two seconds.");
                return;
            }

            var shipLink = GridTerminalSystem.GetBlockWithName(_config.ShipLinkBlock) as IMyProgrammableBlock;
            if (shipLink == null || shipLink.CubeGrid != Me.CubeGrid || !shipLink.IsWorking || shipLink.EntityId == Me.EntityId)
            {
                Echo("A working local ShipLink PB named " + _config.ShipLinkBlock + " is required.");
                return;
            }

            if (!shipLink.TryRun(shareCommand))
                Echo("ShipLink did not accept the command. Retry share.");
        }

        private void Calibrate()
        {
            if (!IsGimbal || _scanning || _parking)
            {
                Echo("Stop Radar and select gimbal mode before calibrating.");
                return;
            }

            Discover();
            _scanProblem = _gimbal.GetUnavailableReason();
            if (_scanProblem != null)
            {
                _state = RadarState.UNAVAILABLE;
                return;
            }

            _gimbal.CaptureParkPosition();
            _state = RadarState.IDLE;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            Echo("Park position captured. To retain it across reloads, add under [Gimbal]:");
            Echo("ParkAzimuthDegrees=" + _config.ParkAzimuthDegrees.ToString("0.###", culture));
            Echo("ParkElevationDegrees=" + _config.ParkElevationDegrees.ToString("0.###", culture));
        }

        private void Park(DateTime now)
        {
            _scanProblem = _gimbal.GetUnavailableReason();
            if (_scanProblem != null)
            {
                StopMotion(RadarState.UNAVAILABLE);
                return;
            }

            bool stalled;
            if (_gimbal.Park(now, out stalled))
                StopMotion(RadarState.PARKED);
            else if (stalled)
            {
                _scanProblem = "Parking stopped: an axis did not move toward its setpoint.";
                StopMotion(RadarState.UNAVAILABLE);
            }
            else
                _state = RadarState.PARKING;
        }

        private void StopMotion(RadarState state)
        {
            _scanning = false;
            _parking = false;
            _gimbal.Stop();
            Runtime.UpdateFrequency = UpdateFrequency.None;
            _state = state;
        }

        private void Scan(DateTime now)
        {
            var cue = _cue;
            if (cue != null && cue.Expires <= now.Ticks)
            {
                _cue = null;
                _scanProblem = "Network cue expired before local confirmation.";
                StopMotion(RadarState.LOST);
                return;
            }

            bool tracking = _contact.IsActive;
            bool pointed = tracking || cue != null;
            double age = _contact.AgeSecondsAt(now);
            if (tracking && age >= ContactTimeoutSeconds + _contact.Range / 2000)
            {
                _contact.Lose();
                _gimbal.Stop();
                _state = RadarState.LOST;
                return;
            }

            bool gimbal = IsGimbal;
            _scanProblem = gimbal ? _gimbal.GetUnavailableReason() : null;
            if (_scanProblem != null)
            {
                LoseTrackingHardware(_scanProblem);
                return;
            }

            Vector3D target = tracking ? _predictor.Predict(_contact.Position, _contact.Velocity, age) : cue != null ? cue.PositionAt(now.Ticks) : Vector3D.Zero;
            bool eligibleCamera = false;
            bool gimbalDriven = false;
            bool gimbalReadyToScan = true;
            int rays = 0;
            for (int i = 0; i < _cameras.Count() && rays < Math.Max(1, _config.MaxRaycastsPerUpdate); i++)
            {
                var camera = _cameras.GetNext();
                if (!camera.IsWorking || !camera.EnableRaycast || !camera.IsSameConstructAs(Me) ||
                    (gimbal && !_gimbal.HasCamera(camera)))
                    continue;

                eligibleCamera = true;
                if (gimbal && !gimbalDriven)
                {
                    if (pointed)
                        _gimbal.Aim(camera, target);
                    else
                    {
                        bool stalled;
                        gimbalReadyToScan = _gimbal.HoldSweepPosition(now, out stalled);
                        if (stalled)
                        {
                            _scanProblem = "Search stopped: the azimuth axis did not reach its scan position.";
                            StopMotion(RadarState.UNAVAILABLE);
                            return;
                        }
                    }
                    gimbalDriven = true;
                }

                if (!gimbalReadyToScan)
                    break;

                double maxRange = camera.RaycastDistanceLimit > 0
                    ? Math.Min(_config.MaxRange, camera.RaycastDistanceLimit) : _config.MaxRange;
                double range = pointed ? Vector3D.Distance(camera.GetPosition(), target) : maxRange;
                if (range <= 0 || range > maxRange || (pointed ? !camera.CanScan(target) : !camera.CanScan(range)))
                    continue;

                rays++;
                var hit = pointed ? camera.Raycast(target) : camera.Raycast(range);
                if (hit.IsEmpty() || (tracking && hit.EntityId != _contact.EntityId) ||
                    (cue != null && !cue.Matches(hit.EntityId)))
                    continue;

                _cue = null;
                _contact.Update(hit, Vector3D.Distance(Me.GetPosition(), hit.Position), now);
                _predictor.UpdateState(_contact.Position, _contact.Velocity);
                if (_config.ShareContacts && now.Ticks >= _nextJournalPublish)
                    PublishJournalObservation(now.Ticks);
                _state = RadarState.LOCKED;
                return;
            }

            if (!eligibleCamera)
            {
                LoseTrackingHardware(gimbal ? "A working, raycast-enabled radar camera must be on the hinge head." :
                    "No working, raycast-enabled camera in the radar group or with the configured name prefix.");
                return;
            }
            if (gimbal && !pointed && rays > 0)
                _gimbal.AdvanceSweep();
            _state = tracking ? RadarState.COASTING :
                rays == 0 && gimbalReadyToScan ? RadarState.CHARGING : RadarState.ACQUIRING;
        }

        private void PublishJournalObservation(long now)
        {
            _nextJournalPublish = now + TimeSpan.TicksPerSecond;
            var shipLink = GridTerminalSystem.GetBlockWithName(_config.ShipLinkBlock) as IMyProgrammableBlock;
            if (shipLink != null && shipLink.CubeGrid == Me.CubeGrid && shipLink.IsWorking && shipLink.EntityId != Me.EntityId)
                shipLink.TryRun("contact publish " + _contact.FormatObservation());
        }

        private void LoseTrackingHardware(string problem)
        {
            _contact.Lose();
            _gimbal.Stop();
            _scanProblem = problem;
            _state = RadarState.UNAVAILABLE;
        }

        private bool IsGimbal => string.Equals(_config.Mode, "Gimbal", StringComparison.OrdinalIgnoreCase);

        private void Render(DateTime now)
        {
            var status = "Radar " + _config.Mode + " | " + _state + "\nCameras: " + _cameras.Count();
            string problem = _scanProblem ?? (IsGimbal ? _gimbal.GetUnavailableReason() : null);
            if (problem != null)
                status += "\n" + problem;
            if (_cue != null)
                status += "\nAwaiting local confirmation: " + _cue.EntityId;
            if (_contact.EntityId != 0)
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                status += (_contact.IsActive ? "\nContact: " : "\nLast seen: ") +
                    _contact.Name + " [" + _contact.EntityId + "]\n" +
                    _contact.Type + " | " + _contact.Relationship + "\n" +
                    "Last measurement: " + _contact.AgeSecondsAt(now).ToString("0.0", culture) +
                    " s ago | " + _contact.Range.ToString("0", culture) + " m | " +
                    _contact.Velocity.Length().ToString("0.0", culture) + " m/s\n" +
                    _contact.FormatGps("Radar last fix");
            }
            Echo(status);
            if (_lcd != null && _lcd.IsSameConstructAs(Me))
            {
                _lcd.ContentType = ContentType.TEXT_AND_IMAGE;
                _lcd.WriteText(status, false);
            }
        }

        // PB BODY END
    }
}
