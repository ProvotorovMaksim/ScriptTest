#region Prelude
using System;
using System.Globalization;
using VRageMath;
using Sandbox.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;
using SpaceEngineers.Libs.RadarCore;

namespace SpaceEngineers.Apps.Radar
{
    public sealed class Program : MyGridProgram
    {
        #endregion

        private const double ContactTimeoutSeconds = 5;
        private readonly ContactTracker _contact = new ContactTracker();
        private readonly PositionPredictor _predictor = new PositionPredictor();
        private readonly RadarConfig _config;
        private readonly CameraManager _cameras;
        private readonly GimbalController _gimbal;
        private IMyTextPanel _lcd;
        private bool _scanning;
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
            if (!string.IsNullOrWhiteSpace(argument))
                Command(argument.Trim());
            else if (_scanning)
                Scan(DateTime.UtcNow);
            Render();
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
                    _scanning = true;
                    _state = RadarState.ACQUIRING;
                    Runtime.UpdateFrequency = UpdateFrequency.Update10;
                    break;
                case "unlock":
                    _contact.Lose();
                    _gimbal.Stop();
                    _state = _scanning ? RadarState.ACQUIRING : RadarState.IDLE;
                    break;
                case "off":
                    _scanning = false;
                    _contact.Lose();
                    _gimbal.Stop();
                    Runtime.UpdateFrequency = UpdateFrequency.None;
                    _state = RadarState.IDLE;
                    break;
                case "mode fixed":
                case "mode gimbal":
                    _gimbal.Unbind();
                    _contact.Lose();
                    _config.Mode = command.EndsWith("gimbal", StringComparison.OrdinalIgnoreCase) ? "Gimbal" : "Fixed";
                    _state = _scanning ? RadarState.ACQUIRING : RadarState.IDLE;
                    Discover();
                    break;
                case "status":
                    break;
                default:
                    double range;
                    if (command.StartsWith("range ", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(command.Substring(6), NumberStyles.Float, CultureInfo.InvariantCulture, out range) &&
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
            double age = tracking ? (now - _contact.LastSeen).TotalSeconds : 0;
            if (tracking && age >= ContactTimeoutSeconds + _contact.Range / 2000)
            {
                _contact.Lose();
                _gimbal.Stop();
                _state = RadarState.LOST;
                return;
            }

            bool gimbal = IsGimbal;
            if (gimbal && !_gimbal.IsReady)
            {
                _gimbal.Stop();
                _state = RadarState.OUT_OF_FOV;
                return;
            }

            Vector3D target = tracking ? _predictor.Predict(_contact.Position, _contact.Velocity, Math.Max(0, age)) : Vector3D.Zero;
            bool gimbalDriven = false;
            int rays = 0;
            for (int i = 0; i < _cameras.Count() && rays < Math.Max(1, _config.MaxRaycastsPerUpdate); i++)
            {
                var camera = _cameras.GetNext();
                if (!camera.IsWorking || (gimbal && !_gimbal.HasCamera(camera)))
                    continue;

                if (gimbal && !gimbalDriven)
                {
                    if (tracking)
                        _gimbal.Aim(camera, target);
                    else
                        _gimbal.Sweep();
                    gimbalDriven = true;
                }

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

            if (gimbal && !gimbalDriven)
                _gimbal.Stop();
            _state = tracking ? RadarState.COASTING : rays == 0 ? RadarState.CHARGING : RadarState.ACQUIRING;
        }

        private bool IsGimbal => string.Equals(_config.Mode, "Gimbal", StringComparison.OrdinalIgnoreCase);

        private void Render()
        {
            var status = "Radar " + _config.Mode + " | " + _state + "\nCameras: " + _cameras.Count();
            if (IsGimbal && !_gimbal.IsReady)
                status += "\nAzimuth rotor and elevation hinge required.";
            if (_contact.IsActive)
                status += "\n" + _contact.Name + " [" + _contact.EntityId + "]\nLast range: " +
                    _contact.Range.ToString("0", CultureInfo.InvariantCulture) + " m";
            Echo(status);
            if (_lcd != null && _lcd.IsSameConstructAs(Me))
            {
                _lcd.ContentType = ContentType.TEXT_AND_IMAGE;
                _lcd.WriteText(status, false);
            }
        }

        #region PreludeFooter
    }
}
#endregion
