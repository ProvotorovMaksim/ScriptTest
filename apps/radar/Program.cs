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
                    _scanning = true;
                    _state = RadarState.ACQUIRING;
                    Runtime.UpdateFrequency = UpdateFrequency.Update10;
                    break;
                case "unlock":
                    if (_parking)
                        StopMotion(RadarState.IDLE);
                    _contact.Clear();
                    _gimbal.Stop();
                    _state = _scanning ? RadarState.ACQUIRING : RadarState.IDLE;
                    break;
                case "off":
                    _scanning = false;
                    _parking = false;
                    _scanProblem = null;
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
                    else if (command.StartsWith("share ", StringComparison.OrdinalIgnoreCase))
                        ShareContact(command.Substring(6), now);
                    else
                        Echo("Radar commands: on, off, scan, unlock, status, mode fixed, mode gimbal, gimbal calibrate, gimbal park, range <meters>, share <relay ID>");
                    break;
            }
        }

        private void ShareContact(string argument, DateTime now)
        {
            long target;
            if (!long.TryParse(argument, out target) || target == 0)
            {
                Echo("Use share <relay ID>.");
                return;
            }
            if (!_contact.IsActive || _contact.AgeSecondsAt(now) > 2)
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

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var name = "Radar " + _contact.EntityId.ToString(culture);
            if (!shipLink.TryRun("contact " + target.ToString(culture) + " " + ContactGps(name)))
                Echo("ShipLink did not accept the command. Retry share.");
        }

        private string ContactGps(string name)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var position = _contact.Position;
            return "GPS:" + name + ":" + position.X.ToString("0.00", culture) + ":" +
                position.Y.ToString("0.00", culture) + ":" + position.Z.ToString("0.00", culture) + ":";
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
            bool tracking = _contact.IsActive;
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

            Vector3D target = tracking ? _predictor.Predict(_contact.Position, _contact.Velocity, age) : Vector3D.Zero;
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
                    if (tracking)
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
                double range = tracking ? Vector3D.Distance(camera.GetPosition(), target) : maxRange;
                if (range <= 0 || range > maxRange || (tracking ? !camera.CanScan(target) : !camera.CanScan(range)))
                    continue;

                rays++;
                var hit = tracking ? camera.Raycast(target) : camera.Raycast(range);
                if (hit.IsEmpty() || (tracking && hit.EntityId != _contact.EntityId))
                    continue;

                _contact.Update(hit, Vector3D.Distance(Me.GetPosition(), hit.Position), now);
                _predictor.UpdateState(_contact.Position, _contact.Velocity);
                _state = RadarState.LOCKED;
                return;
            }

            if (!eligibleCamera)
            {
                LoseTrackingHardware(gimbal ? "A working, raycast-enabled radar camera must be on the hinge head." :
                    "No working, raycast-enabled camera in the radar group or with the configured name prefix.");
                return;
            }
            if (gimbal && !tracking && rays > 0)
                _gimbal.AdvanceSweep();
            _state = tracking ? RadarState.COASTING :
                rays == 0 && gimbalReadyToScan ? RadarState.CHARGING : RadarState.ACQUIRING;
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
            if (_contact.EntityId != 0)
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                status += (_contact.IsActive ? "\nContact: " : "\nLast seen: ") +
                    _contact.Name + " [" + _contact.EntityId + "]\n" +
                    _contact.Type + " | " + _contact.Relationship + "\n" +
                    "Last measurement: " + _contact.AgeSecondsAt(now).ToString("0.0", culture) +
                    " s ago | " + _contact.Range.ToString("0", culture) + " m | " +
                    _contact.Velocity.Length().ToString("0.0", culture) + " m/s\n" +
                    ContactGps("Radar last fix");
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
