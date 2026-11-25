#region Prelude
using System;
using VRageMath;
using Sandbox.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;
using SpaceEngineers.Libs.RadarCore;
using SpaceEngineers.Libs.RadarCore;

namespace SpaceEngineers.Apps.Radar
{
    public sealed class Program : MyGridProgram
    {
        #endregion

        #region Fields

        private RadarConfig _config = new RadarConfig();
        private RadarState _state = RadarState.IDLE;
        private ContactObservation _currentContact = new ContactObservation();
        private ContactTracker _contactTracker = new ContactTracker();
        private CameraManager _cameraManager = new CameraManager();
        private GimbalController _gimbalController;
        private IMyTextPanel _lcdPanel;
        private StringBuilder _lcdText = new StringBuilder();

        #endregion

namespace SpaceEngineers.Apps.Radar
{
    public sealed class Program : MyGridProgram
    {
        #endregion

        #region Fields

        private RadarConfig _config = new RadarConfig();
        private RadarState _state = RadarState.IDLE;
        private ContactObservation _currentContact = new ContactObservation();
        private ContactTracker _contactTracker = new ContactTracker();
        private CameraManager _cameraManager = new CameraManager();
        private IMyTextPanel _lcdPanel;
        private StringBuilder _lcdText = new StringBuilder();

#endregion

        #region Construction

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
        }

        #endregion

        #region Configuration Loading

        private void LoadConfig()
        {
            var cd = Me.CustomData;
            if (!string.IsNullOrEmpty(cd))
            {
                TryParseCustomData(cd, out _config, out _);
            }
        }

        private static bool TryParseCustomData(string customData, out RadarConfig config, out string error)
        {
            return RadarConfig.TryParse(customData, out config, out error);
        }

        #endregion

        #region Camera Discovery

        private void DiscoverCameras()
        {
private void LoadConfig()
        {
            var cd = Me.CustomData;
            if (!string.IsNullOrEmpty(cd))
            {
                TryParseCustomData(cd, out _config, out _);
            }
            _gimbalController = new GimbalController(_config);
        }
            if (GridTerminalSystem != null)
            {
#endregion

        #region Command Processing

        private void ProcessCommand(string command)
        {
case "mode":
                    if (args.Length > 1)
                    {
                        var mode = args[1].ToLowerInvariant();
                        if (mode == "fixed")
                        {
                            _config.Mode = "Fixed";
                            _gimbalController.Disable();
                            Echo($"Mode set to Fixed.");
                        }
                        else if (mode == "gimbal")
                        {
                            _config.Mode = "Gimbal";
                            _gimbalController.Enable();
                            Echo($"Mode set to Gimbal.");
                        }
                        else
                        {
                            Echo($"Unknown mode: {mode}. Use 'fixed' or 'gimbal'.");
                        }
case "gimbal home":
                    _gimbalController.Calibrate();
                    Echo("Gimbal calibrated to home position.");
                    break;

                case "gimbal stop":
                    _gimbalController.Disable();
                    Echo("Gimbal stopped and parked.");
                    break;

                case "gimbal calibrate":
                    _gimbalController.Calibrate();
                    Echo("Gimbal calibrated.");
                    break;

                case "gimbal azimuth":
                    if (args.Length > 1 && double.TryParse(args[1], out var az))
                    {
                        _gimbalController.SetAzimuth(az);
                        Echo($"Gimbal azimuth set to {az:0.##}°");
                    }
                    else
                    {
                        Echo($"Current gimbal azimuth: {_gimbalController.GetAzimuth():0.##}°");
                    }
#endregion

        #region LCD

        private void UpdateLcd()
        {
            _lcdPanel ??= GridTerminalSystem?.GetBlockWithName("[RADAR LCD]") as IMyTextPanel;
            if (_lcdPanel == null) return;

            _lcdText.Clear();
            _lcdText.AppendLine($"Mode: {_config.Mode}");
            _lcdText.AppendLine($"State: {_state}");
            _lcdText.AppendLine($"Target: {_currentContact.Name}");
            _lcdText.AppendLine($"Range: {_currentContact.Range:0.##} m");
            _lcdText.AppendLine($"Cameras: {_cameraManager.Count()}");

            _lcdPanel.WriteText(_lcdText.ToString(), writePublic: false);
        }

        #endregion

        #region Configuration Saving
case "off":
                    _state = RadarState.IDLE;
                    _contactTracker.Lose();
                    _positionPredictor.Reset();
                    SaveConfig(); // Save config before turning off
                    Echo("Radar off, target lost.");
                    break;

        private string SaveConfig()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[Radar]");
            sb.AppendLine($"Mode={_config.Mode}");
            sb.AppendLine($"MaxRange={_config.MaxRange}");
            sb.AppendLine($"CameraGroup={_config.CameraGroup}");
            sb.AppendLine($"CameraPrefix={_config.CameraPrefix}");
            return sb.ToString();
        }

        #endregion
                    break;

                case "gimbal elevation":
                    if (args.Length > 1 && double.TryParse(args[1], out var el))
                    {
                        _gimbalController.SetElevation(el);
                        Echo($"Gimbal elevation set to {el:0.##}°");
                    }
                    else
                    {
                        Echo($"Current gimbal elevation: {_gimbalController.GetElevation():0.##}°");
                    }
                    break;
#endregion

        #region Error Handling and Safety

        private void SafeRaycast(IMyCameraBlock camera)
        {
            try
            {
                if (camera == null) return;
                camera.EnableRaycast = true;
            }
            catch
            {
                // Camera may be destroyed or disabled
                Echo("Camera operation failed (block may be damaged or removed).");
            }
        }

        private void SafeLcdWrite()
        {
            try
            {
                _lcdPanel?.WriteText(_lcdText.ToString(), writePublic: false);
            }
            catch
            {
                // LCD may be removed or disabled
            }
        }

        #endregion
                    }
                    else
                    {
                        Echo($"Current mode: {_config.Mode}");
                        Echo($"Gimbal enabled: {_gimbalController.IsEnabled()}");
                    }
                    break;
            var args = command.Split(' ');
            var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            switch (cmd)
            {
                case "help":
                    Echo("Radar commands: help, status, on, off, scan, lock, unlock, mode, range");
                    break;

                case "status":
                    Echo($"Mode: {_config.Mode}");
                    Echo($"State: {_state}");
                    Echo($"Target: {_currentContact.Name} (Id: {_currentContact.EntityId})");
                    Echo($"Range: {_currentContact.Range:0.##} m");
                    Echo($"Cameras: {_cameraManager.Count()}");
                    break;

                case "on":
                    _state = RadarState.IDLE;
                    Echo("Radar on.");
                    break;

                case "off":
                    _state = RadarState.IDLE;
                    _contactTracker.Lose();
                    Echo("Radar off, target lost.");
                    break;

                case "scan":
                    if (_cameraManager.HasCameras())
                    {
                        Echo("Scanning...");
                        _state = RadarState.ACQUIRING;
                    }
#endregion

        #region Main Loop

        public void Main(string argument, UpdateType updateSource)
        {
            LoadConfig();

            if (!string.IsNullOrWhiteSpace(argument))
            {
                ProcessCommand(argument);
                return;
            }

            // Camera discovery
            if (_cameraManager.Count() == 0) DiscoverCameras();

            // State machine
            switch (_state)
            {
                case RadarState.IDLE:
                    break;

                case RadarState.ACQUIRING:
                    if (_cameraManager.HasCameras())
                    {
                        var cam = _cameraManager.GetCurrent();
                        if (cam != null)
                        {
                            cam.EnableRaycast = true;
                            var range = _config.MaxRange;
                            var rayResult = cam.Raycast(range);

                            if (!rayResult.IsEmpty())
                            {
                                var entity = rayResult.Value.Entity;
                                if (entity != null)
                                {
                                    var pos = entity.GetPosition();
                                    var vel = entity.LinearVelocity;
#endregion

        #region LCD

        private void UpdateLcd()
        {
            _lcdPanel ??= GridTerminalSystem?.GetBlockWithName("[RADAR LCD]") as IMyTextPanel;
            if (_lcdPanel == null) return;

            _lcdText.Clear();
            _lcdText.AppendLine($"Mode: {_config.Mode}");
            _lcdText.AppendLine($"State: {_state}");
            _lcdText.AppendLine($"Target: {_currentContact.Name}");
            _lcdText.AppendLine($"Range: {_currentContact.Range:0.##} m");
            _lcdText.AppendLine($"Cameras: {_cameraManager.Count()}");

            _lcdPanel.WriteText(_lcdText.ToString(), writePublic: false);
        }

        #endregion
    }
}
                                    var name = entity.DisplayName;

                                    _currentContact = new ContactObservation
                                    {
                                        EntityId = entity.EntityId,
                                        Name = name ?? "Unknown",
                                        Range = Vector3D.Distance(Me.CubeGrid.GetPosition(), pos),
                                        IsMeasured = true,
                                        Position = pos,
                                        Velocity = vel,
                                        Direction = cam.WorldMatrix.Forward
                                    };

                                    _contactTracker.Update(new MyDetectedEntityInfo
                                    {
                                        EntityId = entity.EntityId,
                                        DisplayNameText = name,
                                        Position = pos,
                                        Velocity = vel
                                    }, _currentContact.Range, DateTime.UtcNow);

                                    _state = RadarState.LOCKED;
                                    Echo($"Target acquired: {name}");
                                }
                            }
                            else
                            {
                                _state = RadarState.OUT_OF_FOV;
                                Echo("Nothing in range.");
                            }
                        }
                        else
                        {
                            _state = RadarState.IDLE;
                            Echo("No camera available.");
                        }
                    }
                    else
                    {
                        Echo("No radar cameras configured.");
                        _state = RadarState.IDLE;
                    }
                    break;

                case RadarState.LOCKED:
                    UpdateLcd();
                    break;

                case RadarState.OUT_OF_FOV:
                    UpdateLcd();
                    _state = RadarState.IDLE;
                    break;
            }

            UpdateLcd();
        }

        #endregion
                    else
                    {
                        Echo("No radar cameras found.");
                    }
                    break;

                case "lock":
                    _state = RadarState.LOCKED;
                    Echo("Target locked.");
                    break;

                case "unlock":
                    _state = RadarState.IDLE;
                    _contactTracker.Lose();
                    Echo("Target unlocked.");
                    break;

                case "mode":
                    if (args.Length > 1)
                    {
                        _config.Mode = args[1];
                        Echo($"Mode set to {_config.Mode}");
                    }
                    else
                    {
                        Echo($"Current mode: {_config.Mode}");
                    }
                    break;

                case "range":
                    if (args.Length > 1 && double.TryParse(args[1], out var r))
                    {
                        _config.MaxRange = r;
                        Echo($"Max range set to {r} m");
                    }
                    else
                    {
                        Echo($"Max range: {_config.MaxRange} m");
                    }
                    break;

                default:
                    Echo($"Unknown command: {cmd}. Type 'help'.");
                    break;
            }
        }

        #endregion
                _cameraManager.DiscoverCameras(GridTerminalSystem);
            }
        }

        #endregion
        #endregion
