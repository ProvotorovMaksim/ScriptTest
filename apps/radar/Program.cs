using System;
using System.Collections.Generic;
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
        private bool _retryContactCenter;
        private readonly HashSet<long> _fixedAxisNext = new HashSet<long>();
        private long _lastHitCameraId;
        private IMyCameraBlock _repeatAcquisitionCamera;
        private IMyCameraBlock _gimbalAimCamera;
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
        private string _lastLoss;
        private string _lastRay = "No rays yet.";
        private int _misses;
        private int _availableCameras;
        private double _scanCharge;
        private readonly HashSet<long> _ownGrids = new HashSet<long>();
        private readonly List<IMyMechanicalConnectionBlock> _connections = new List<IMyMechanicalConnectionBlock>();
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
            RefreshOwnGrids();
            _cameras.DiscoverCameras(GridTerminalSystem, Me);
            _fixedAxisNext.Clear();
            _repeatAcquisitionCamera = null;
            _gimbalAimCamera = null;
            _lcd = GridTerminalSystem.GetBlockWithName("[RADAR LCD]") as IMyTextPanel;
            if (IsGimbal)
                _gimbal.Bind(GridTerminalSystem, Me);
        }

        private void RefreshOwnGrids()
        {
            _ownGrids.Clear();
            _ownGrids.Add(Me.CubeGrid.EntityId);
            _connections.Clear();
            GridTerminalSystem.GetBlocksOfType(_connections, block => block.IsSameConstructAs(Me));
            foreach (var connection in _connections)
            {
                _ownGrids.Add(connection.CubeGrid.EntityId);
                if (connection.TopGrid != null)
                    _ownGrids.Add(connection.TopGrid.EntityId);
            }
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
            if (_ownGrids.Contains(cue.EntityId))
            {
                Echo("Acquisition cue refers to the radar construct.");
                return;
            }
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
                _lastLoss = "Confirmation timed out. " + _lastRay;
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
            _availableCameras = 0;
            _scanCharge = 0;
            var acquisitionCamera = tracking && !gimbal ? _repeatAcquisitionCamera : null;
            _repeatAcquisitionCamera = null;
            int cameraAttempts = _cameras.Count() + (acquisitionCamera == null ? 0 : 1);
            for (int i = 0; i < cameraAttempts && rays < Math.Max(1, _config.MaxRaycastsPerUpdate); i++)
            {
                bool repeatAcquisition = i == 0 && acquisitionCamera != null;
                var camera = repeatAcquisition ? acquisitionCamera : _cameras.GetNext();
                if (!repeatAcquisition && acquisitionCamera != null && camera.EntityId == acquisitionCamera.EntityId)
                    continue;
                if (!camera.IsWorking || !camera.EnableRaycast || !camera.IsSameConstructAs(Me) ||
                    (gimbal && !_gimbal.HasCamera(camera)))
                    continue;

                eligibleCamera = true;
                if (gimbal && !gimbalDriven)
                {
                    if (pointed)
                    {
                        if (_gimbalAimCamera == null || !_gimbalAimCamera.IsWorking ||
                            !_gimbalAimCamera.IsSameConstructAs(Me) || !_gimbal.HasCamera(_gimbalAimCamera))
                            _gimbalAimCamera = camera;
                        _gimbal.Aim(_gimbalAimCamera, target);
                    }
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
                var rayTarget = target;
                var origin = camera.GetPosition();
                _scanCharge = Math.Max(_scanCharge, camera.AvailableScanRange);
                if (tracking && _contact.HitPosition.HasValue && !_retryContactCenter &&
                    (gimbal || camera.EntityId == _lastHitCameraId))
                {
                    var surface = target + (_contact.HitPosition.Value - _contact.Position);
                    var direction = surface - origin;
                    double distance = direction.Length();
                    if (distance > 0 && distance <= maxRange)
                    {
                        var beyondSurface = surface + direction * (Math.Min(1.0, maxRange - distance) / distance);
                        if (camera.CanScan(beyondSurface))
                            rayTarget = beyondSurface;
                    }
                }

                double range = pointed ? Vector3D.Distance(origin, rayTarget) : maxRange;
                bool canPoint = pointed && range > 0 && range <= maxRange + 0.000001 && camera.CanScan(rayTarget);
                bool fixedTracking = tracking && !gimbal;
                double axisRange = maxRange;
                if (fixedTracking)
                {
                    double extent = _contact.BoundingBox.HasValue
                        ? (_contact.BoundingBox.Value.Max - _contact.BoundingBox.Value.Min).Length() * 0.5 : 0;
                    axisRange = Math.Min(maxRange, Vector3D.Distance(origin, target) + extent + 2);
                }
                bool axis = !pointed || (fixedTracking &&
                    (repeatAcquisition || _fixedAxisNext.Contains(camera.EntityId) || !canPoint) && camera.CanScan(axisRange));
                if (axis ? axisRange <= 0 || (!fixedTracking && !camera.CanScan(axisRange)) : !canPoint)
                {
                    _lastRay = camera.CustomName + (range > maxRange ? ": beyond range limit." :
                        !camera.CanScan(range) ? ": insufficient scan charge." : ": scan point unavailable (angle or range).");
                    continue;
                }

                _availableCameras++;
                rays++;
                var hit = axis ? camera.Raycast(axisRange) : camera.Raycast(rayTarget);
                if (tracking)
                {
                    _retryContactCenter = !axis && rayTarget != target;
                    if (fixedTracking)
                    {
                        if (axis)
                            _fixedAxisNext.Remove(camera.EntityId);
                        else
                            _fixedAxisNext.Add(camera.EntityId);
                    }
                }
                string rejection = hit.IsEmpty() ? "empty ray" :
                    !ContactTracker.IsShipContact(hit.Type, hit.EntityId, _ownGrids.Contains(hit.EntityId)) ? "rejected " + hit.Type + " #" + hit.EntityId :
                    tracking && hit.EntityId != _contact.EntityId || cue != null && !cue.Matches(hit.EntityId) ? "different entity #" + hit.EntityId : null;
                if (rejection != null)
                {
                    _misses++;
                    _lastRay = camera.CustomName + ": " + rejection;
                    continue;
                }
                _misses = 0;
                _lastRay = camera.CustomName + ": confirmed #" + hit.EntityId;

                _cue = null;
                _retryContactCenter = false;
                _lastHitCameraId = camera.EntityId;
                if (!tracking)
                {
                    _fixedAxisNext.Clear();
                    if (!gimbal && axis)
                        _repeatAcquisitionCamera = camera;
                }
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
            if (tracking)
            {
                _scanProblem = rays == 0 ? "Tracking: no camera could fire an eligible ray." :
                    "Tracking: raycast did not confirm the locked entity.";
                if (rays == 0)
                    _retryContactCenter = false;
            }
            _state = tracking ? (age <= 1 ? RadarState.LOCKED : RadarState.COASTING) :
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
            _lastLoss = problem;
            _state = RadarState.UNAVAILABLE;
        }

        private bool IsGimbal => string.Equals(_config.Mode, "Gimbal", StringComparison.OrdinalIgnoreCase);

        private void Render(DateTime now)
        {
            var status = "Radar " + _config.Mode + " | " + _state + "\nCameras: " + _cameras.Count();
            status += "\nScan limit: " + Math.Round(_config.MaxRange, 1) + " m | ready this pass: " + _availableCameras +
                " | max observed charge: " + Math.Round(_scanCharge, 1) + " m\n" + _lastRay + " | missed rays: " + _misses;
            if (_lastLoss != null)
                status += "\nLast loss: " + _lastLoss;
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
                    " s ago | " + _contact.Range.ToString("0", culture) + " m to center" +
                    (_contact.HitPosition.HasValue ? " | " + Vector3D.Distance(Me.GetPosition(), _contact.HitPosition.Value).ToString("0.0", culture) + " m to hit" : "") +
                    " | " + _contact.Velocity.Length().ToString("0.0", culture) + " m/s\n" +
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
