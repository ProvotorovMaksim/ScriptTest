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
                Command(argument.Trim());
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

        private void Command(string command)
        {
            switch (command.ToLowerInvariant())
            {
                case "on":
                case "scan":
                    Discover();
                    _scanProblem = null;
                    _scanning = true;
                    _state = RadarState.ACQUIRING;
                    Runtime.UpdateFrequency = UpdateFrequency.Update10;
                    break;
                case "unlock":
                    _contact.Clear();
                    _gimbal.Stop();
                    _state = _scanning ? RadarState.ACQUIRING : RadarState.IDLE;
                    break;
                case "off":
                    _scanning = false;
                    _scanProblem = null;
                    _contact.Clear();
                    _gimbal.Stop();
                    Runtime.UpdateFrequency = UpdateFrequency.None;
                    _state = RadarState.IDLE;
                    break;
                case "mode fixed":
                case "mode gimbal":
                    _gimbal.Unbind();
                    _scanProblem = null;
                    _contact.Clear();
                    _config.Mode = command.EndsWith("gimbal", StringComparison.OrdinalIgnoreCase) ? "Gimbal" : "Fixed";
                    _state = _scanning ? RadarState.ACQUIRING : RadarState.IDLE;
                    Discover();
                    break;
                case "status":
                    break;
                default:
                    double range;
                    if (command.StartsWith("range ", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(command.Substring(6), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out range) &&
                        range > 0 && !double.IsInfinity(range))
                        _config.MaxRange = range;
                    else
                        Echo("Radar commands: on, off, scan, unlock, status, mode fixed, mode gimbal, range <meters>");
                    break;
            }
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
                _gimbal.Stop();
                _state = RadarState.UNAVAILABLE;
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
                if (!camera.IsWorking || (gimbal && !_gimbal.HasCamera(camera)))
                    continue;

                eligibleCamera = true;
                if (gimbal && !gimbalDriven)
                {
                    if (tracking)
                        _gimbal.Aim(camera, target);
                    else
                        gimbalReadyToScan = _gimbal.HoldSweepPosition();
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
                _gimbal.Stop();
                _scanProblem = gimbal ? "A working radar camera must be on the hinge head." :
                    "No working camera in the radar group or with the configured name prefix.";
                _state = RadarState.UNAVAILABLE;
                return;
            }
            if (gimbal && !tracking && rays > 0)
                _gimbal.AdvanceSweep();
            _state = tracking ? RadarState.COASTING :
                rays == 0 && gimbalReadyToScan ? RadarState.CHARGING : RadarState.ACQUIRING;
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
                status += (_contact.IsActive ? "\nContact: " : "\nLast seen: ") +
                    _contact.Name + " [" + _contact.EntityId + "]\nAge: " +
                    _contact.AgeSecondsAt(now).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s | Range: " +
                    _contact.Range.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m";
                if (!_contact.IsActive)
                    status += "\nLast position: " + _contact.Position.ToString();
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
