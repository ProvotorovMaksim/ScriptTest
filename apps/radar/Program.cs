#region Prelude
using System;
using VRageMath;
using Sandbox.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;
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
            _gimbalController = new GimbalController(_config);
        }

        private static bool TryParseCustomData(string customData, out RadarConfig config, out string error)
        {
            return RadarConfig.TryParse(customData, out config, out error);
        }

        #endregion

#endregion

        #region Command Processing

        private void ProcessCommand(string command)
        {
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
                    SaveConfig();
                    Echo("Radar off, target lost.");
                    break;

                case "scan":
                    if (_cameraManager.HasCameras())
                    {
                        Echo("Scanning...");
                        _state = RadarState.ACQUIRING;
                    }
                    else
                    {
                        Echo("No radar cameras found.");
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
                    }
                    else
                    {
                        Echo($"Current mode: {_config.Mode}");
                        Echo($"Gimbal enabled: {_gimbalController.IsEnabled()}");
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
        #endregion