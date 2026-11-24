using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;
using VRageMath;

namespace SpaceEngineers.Apps.FlightControl
{
    public sealed class Program : MyGridProgram
    {
        // PB BODY START
        private readonly FlightController flight = new FlightController();
        private readonly List<IMyShipController> controllers = new List<IMyShipController>();
        private const string DockSuccessTimer = "[FCS DOCK OK]";
        private const string DockFailureTimer = "[FCS DOCK FAIL]";
        private const string DroneControllerName = "[FCS DRONE]";
        private bool droneDock;
        private readonly List<IMyRemoteControl> remotes = new List<IMyRemoteControl>();
        private const string DockConnectorName = "[FCS DOCK]";
        private IMyShipConnector dockConnector;
        private Vector3D? dockPosition;
        private MatrixD dockFrame;
        private long dockPortId;
        private int dockTicks;
        private bool dockCharging;
        private bool departureRequested;
        private int departureCharge = 95;
        private readonly List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();
        private bool showDockGuide;
        private const string StatusPanelName = "[FCS STATUS]";
        private const int StatusPanelInterval = 15;
        private const double DefaultBrakingDistance = 50;
        private const double MinimumBrakingDistance = 1;
        private const double MaximumBrakingDistance = 500;
        private IMyTextPanel statusPanel;
        private int statusPanelTicks;
        private IMyShipController cockpit;
        private bool previousThrusterControl;
        private bool previousDampeners;
        private Mode mode;
        private string status = "FCS off. Commands: normal, manual, off, status.";

        public Program()
        {
            var settings = Storage.Split('\n');
            var limits = settings[0].Split(';');
            double distance;
            if (TryReadBrakingDistance(limits[0], out distance))
                flight.BrakingDistance = distance;
            int charge;
            if (limits.Length == 2 && TryReadDepartureCharge(limits[1], out charge))
                departureCharge = charge;
            Vector3D position;
            Vector3D forward;
            Vector3D up;
            if (settings.Length >= 4 && TryReadDockVector(settings[1], out position) &&
                TryReadDockVector(settings[2], out forward) && TryReadDockVector(settings[3], out up) &&
                Math.Abs(forward.LengthSquared() - 1) < 0.001 && Math.Abs(up.LengthSquared() - 1) < 0.001 &&
                Math.Abs(Vector3D.Dot(forward, up)) < 0.001)
            {
                dockPosition = position;
                dockFrame = MatrixD.CreateWorld(position, forward, up);
                if (settings.Length >= 5)
                    long.TryParse(settings[4], out dockPortId);
                departureRequested = dockPortId != 0 && settings.Length >= 6 && settings[5] == "departing";
                dockCharging = departureRequested ||
                    (dockPortId != 0 && settings.Length >= 6 && settings[5] == "charging");
                if (dockCharging)
                    Runtime.UpdateFrequency = UpdateFrequency.Update10;
            }
        }

        public void Main(string argument, UpdateType source)
        {
            var previousMode = mode;
            var command = (argument ?? "").Trim().ToLowerInvariant();
            switch (command)
            {
                case "normal": Activate(Mode.Normal); break;
                case "aero on": flight.EstimateAerodynamics = true; break;
                case "aero off": flight.EstimateAerodynamics = false; break;
                case "manual": Activate(Mode.Manual); break;
                case "mass":
                    Echo(mode == Mode.Off ? "Engage FCS before requesting mass limits." :
                        flight.AssessMass(cockpit, () => Runtime.CurrentInstructionCount < Runtime.MaxInstructionCount / 2));
                    return;
                case "dock record": RecordDockPose(); break;
                case "dock approach": BeginDockApproach(false); break;
                case "dock drone": BeginDockApproach(true); break;
                case "dock depart": DepartBerth(true); break;
                case "dock guide":
                    dockConnector = FindDockConnector();
                    showDockGuide = dockConnector != null && dockPosition.HasValue;
                    if (!showDockGuide)
                        Echo("Dock guide requires a recorded berth and a connector named " + DockConnectorName + ".");
                    break;
                case "dock off":
                    showDockGuide = false;
                    if (departureRequested)
                    {
                        departureRequested = false;
                        SaveSettings();
                        status = "FCS: departure cancelled; continuing to charge.";
                    }
                    if (mode == Mode.Dock)
                        Release("FCS off: docking cancelled.");
                    break;
                case "off": Release("FCS off."); break;
                default:
                    if (command.StartsWith("brake ", StringComparison.Ordinal))
                        SetBrakingDistance(command.Substring(6));
                    else if (command.StartsWith("dock charge ", StringComparison.Ordinal))
                    {
                        int charge;
                        if (TryReadDepartureCharge(command.Substring(12), out charge))
                        {
                            departureCharge = charge;
                            SaveSettings();
                            Echo("Departure charge: " + departureCharge + "%.");
                        }
                        else
                            Echo("Usage: dock charge <whole percent, 0 to 100>.");
                    }
                    break;
            }

            if (mode != Mode.Off && (source & UpdateType.Update1) != 0)
            {
                try
                {
                    if (cockpit.CubeGrid != Me.CubeGrid || !cockpit.IsWorking)
                        Release("FCS off: flight controller unavailable.");
                    else if (droneDock && PilotHasControl())
                        Release("FCS off: player took control of the drone.");
                    else if (!droneDock && !cockpit.IsUnderControl)
                        Release("FCS off: pilot absent.");
                    else if (RemoteAutopilotEnabled())
                        Release("FCS off: remote autopilot took control.");
                    else if (mode == Mode.Dock)
                        UpdateDockApproach();
                    else
                        status = flight.Update(cockpit, mode, Runtime.TimeSinceLastRun.TotalSeconds,
                            () => Runtime.CurrentInstructionCount < Runtime.MaxInstructionCount / 2);
                }
                catch (Exception error)
                {
                    Release("FCS off: " + error.Message);
                }
            }

            if (dockCharging && (source & UpdateType.Update10) != 0)
                CheckDockPower();

            var displayStatus = showDockGuide ? status + GetDockStatus() : status;
            Echo(displayStatus);
            bool refreshPanel = command.Length != 0 || (source & UpdateType.Update1) == 0;
            if (refreshPanel || previousMode != mode || (statusPanel != null && ++statusPanelTicks >= StatusPanelInterval))
                WriteStatusPanel(refreshPanel, displayStatus);
        }

        private void SetBrakingDistance(string argument)
        {
            double distance;
            if (!TryReadBrakingDistance(argument, out distance))
            {
                Echo("Usage: brake <distance in metres, 1 to 500>.");
                return;
            }
            flight.BrakingDistance = distance;
            SaveSettings();
            Echo("Braking distance budget: " + FormatNumber(distance) + " m per axis.");
        }

        private static bool TryReadBrakingDistance(string text, out double distance)
        {
            return double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out distance) &&
                distance >= MinimumBrakingDistance && distance <= MaximumBrakingDistance;
        }

        private IMyShipConnector FindDockConnector()
        {
            var connector = GridTerminalSystem.GetBlockWithName(DockConnectorName) as IMyShipConnector;
            return connector != null && connector.CubeGrid == Me.CubeGrid ? connector : null;
        }

        private void RecordDockPose()
        {
            dockConnector = FindDockConnector();
            if (dockConnector == null || dockConnector.Status != MyShipConnectorStatus.Connected)
            {
                Echo("Connect " + DockConnectorName + " to the berth before recording.");
                return;
            }

            var port = dockConnector.OtherConnector;
            dockPosition = dockConnector.GetPosition();
            dockFrame = dockConnector.WorldMatrix;
            dockPortId = port == null ? 0 : port.EntityId;
            SaveSettings();
            Echo("Recorded the connected ship pose for this berth.");
        }

        private void BeginDockApproach(bool uncrewed)
        {
            if (mode == Mode.Dock)
                return;

            if (uncrewed && mode != Mode.Off)
            {
                Echo("Turn FCS off before starting an uncrewed approach.");
                SignalDockResult(false);
                return;
            }

            dockConnector = FindDockConnector();
            if (!dockPosition.HasValue || dockPortId == 0 || dockConnector == null)
            {
                Echo("Record the berth while connected before starting an approach.");
                if (uncrewed)
                    SignalDockResult(false);
                return;
            }

            bool activationHandled = Activate(Mode.Dock, uncrewed);
            if (mode == Mode.Dock)
            {
                dockTicks = 0;
                showDockGuide = true;
                status = "FCS dock: checking final approach.";
            }
            else if (uncrewed && !activationHandled)
                SignalDockResult(false);
        }

        private void UpdateDockApproach()
        {
            const double alignment = 0.996194698; // cosine of five degrees
            if (++dockTicks > 1200 || dockConnector == null || dockConnector.CubeGrid != Me.CubeGrid ||
                !dockConnector.IsWorking)
            {
                Release("FCS off: docking timed out or connector unavailable.");
                return;
            }

            if (cockpit.MoveIndicator.LengthSquared() > 0.01 ||
                cockpit.RotationIndicator.LengthSquared() > 0.01 || Math.Abs(cockpit.RollIndicator) > 0.1)
            {
                Release("FCS off: pilot took over docking.");
                return;
            }

            var port = dockConnector.OtherConnector;
            var connectorStatus = dockConnector.Status;
            if (connectorStatus == MyShipConnectorStatus.Connected)
            {
                bool atRecordedBerth = port != null && port.EntityId == dockPortId;
                Release(atRecordedBerth ? "FCS off: connected to recorded berth." :
                    "FCS off: connected to a different port.", atRecordedBerth);
                return;
            }

            var frame = dockConnector.WorldMatrix;
            var delta = dockPosition.Value - dockConnector.GetPosition();
            var correction = Vector3D.TransformNormal(delta, MatrixD.Transpose(dockFrame));
            var velocity = cockpit.GetShipVelocities();
            if (delta.LengthSquared() > 9 || Math.Abs(correction.X) > 0.4 || Math.Abs(correction.Y) > 0.4 ||
                correction.Z > 0.15 || Vector3D.Dot(frame.Forward, dockFrame.Forward) < alignment ||
                Vector3D.Dot(frame.Up, dockFrame.Up) < alignment || velocity.LinearVelocity.LengthSquared() > 1)
            {
                Release("FCS off: outside the final docking corridor.");
                return;
            }

            if (connectorStatus == MyShipConnectorStatus.Connectable)
            {
                if (port == null || port.EntityId != dockPortId)
                {
                    Release("FCS off: connector target does not match the recorded berth.");
                    return;
                }
                if (velocity.LinearVelocity.LengthSquared() <= 0.04 && delta.LengthSquared() <= 0.36)
                {
                    dockConnector.Connect();
                    if (dockConnector.Status == MyShipConnectorStatus.Connected)
                    {
                        Release("FCS off: connected to recorded berth.", true);
                        return;
                    }
                }
            }

            var desiredVelocity = delta * 0.65;
            if (desiredVelocity.LengthSquared() > 0.1225)
                desiredVelocity = Vector3D.Normalize(desiredVelocity) * 0.35;
            if (connectorStatus == MyShipConnectorStatus.Connectable && delta.LengthSquared() <= 0.36)
                desiredVelocity = Vector3D.Zero;

            flight.Approach(cockpit, velocity.LinearVelocity, velocity.AngularVelocity, desiredVelocity);
            status = droneDock ? "FCS dock: uncrewed final approach. Player control cancels." :
                "FCS dock: creep to recorded berth. Pilot input cancels.";
        }

        private string GetDockStatus()
        {
            try
            {
                if (dockConnector == null || dockConnector.CubeGrid != Me.CubeGrid || !dockConnector.IsWorking)
                    return "\nDock guide: ship connector unavailable.";

                var frame = dockConnector.WorldMatrix;
                var delta = dockPosition.Value - dockConnector.GetPosition();
                var correction = Vector3D.TransformNormal(delta, MatrixD.Transpose(dockFrame));
                var angle = Math.Acos(Clamp(Vector3D.Dot(frame.Forward, dockFrame.Forward), -1, 1)) * 180 / Math.PI;
                var upError = Math.Acos(Clamp(Vector3D.Dot(frame.Up, dockFrame.Up), -1, 1)) * 180 / Math.PI;
                return "\nDOCK GUIDE | " + dockConnector.Status +
                    "\nBerth correction R/U/F: " + Math.Round(correction.X, 2) + " / " +
                    Math.Round(correction.Y, 2) + " / " + Math.Round(-correction.Z, 2) + " m" +
                    "\nForward/up alignment errors: " + Math.Round(angle, 1) + " / " +
                    Math.Round(upError, 1) + " deg";
            }
            catch (Exception)
            {
                dockConnector = null;
                return "\nDock guide: ship connector unavailable.";
            }
        }

        private void SaveSettings()
        {
            Storage = FormatNumber(flight.BrakingDistance) + ";" +
                departureCharge.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (dockPosition.HasValue)
                Storage += "\n" + FormatDockVector(dockPosition.Value) + "\n" +
                    FormatDockVector(dockFrame.Forward) + "\n" + FormatDockVector(dockFrame.Up) +
                    "\n" + dockPortId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                (departureRequested ? "\ndeparting" : dockCharging ? "\ncharging" : "");
        }

        private static string FormatDockVector(Vector3D vector) =>
            FormatNumber(vector.X) + ";" + FormatNumber(vector.Y) + ";" + FormatNumber(vector.Z);

        private static string FormatNumber(double value) =>
            value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static bool TryReadDockVector(string text, out Vector3D vector)
        {
            vector = Vector3D.Zero;
            var values = text.Split(';');
            return values.Length == 3 && TryReadDockNumber(values[0], out vector.X) &&
                TryReadDockNumber(values[1], out vector.Y) && TryReadDockNumber(values[2], out vector.Z);
        }

        private static bool TryReadDockNumber(string text, out double number) =>
            double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out number) &&
            !double.IsNaN(number) && !double.IsInfinity(number);

        private void WriteStatusPanel(bool refreshPanel, string displayStatus)
        {
            statusPanelTicks = 0;
            try
            {
                if (refreshPanel)
                    statusPanel = GridTerminalSystem.GetBlockWithName(StatusPanelName) as IMyTextPanel;
                if (statusPanel == null || statusPanel.CubeGrid != Me.CubeGrid || !statusPanel.IsWorking)
                    return;

                if (statusPanel.ContentType != ContentType.TEXT_AND_IMAGE)
                    statusPanel.ContentType = ContentType.TEXT_AND_IMAGE;
                statusPanel.WriteText(displayStatus);
            }
            catch (Exception error)
            {
                statusPanel = null;
                Echo("FCS status panel unavailable: " + error.Message);
            }
        }

        private bool Activate(Mode requested, bool uncrewed = false)
        {
            if (droneDock)
                Release("FCS off: switching from uncrewed docking.");
            if (mode != Mode.Off)
            {
                mode = requested;
                return true;
            }

            IMyShipController selected = null;
            remotes.Clear();
            controllers.Clear();
            GridTerminalSystem.GetBlocksOfType(controllers, block => block.IsSameConstructAs(Me));
            foreach (var controller in controllers)
            {
                var remote = controller as IMyRemoteControl;
                if (remote != null)
                    remotes.Add(remote);
                if (controller.CubeGrid != Me.CubeGrid || !controller.IsWorking)
                    continue;

                if (uncrewed)
                {
                    if (controller.CustomName == DroneControllerName)
                    {
                        if (selected != null)
                        {
                            status = "FCS off: more than one " + DroneControllerName + " controller.";
                            return false;
                        }
                        selected = controller;
                    }
                }
                else if (controller is IMyCockpit && controller.IsUnderControl && controller.CustomName.Contains("[FCS]"))
                    selected = controller;
            }

            if (uncrewed && PilotHasControl())
            {
                status = "FCS off: leave all controllers before starting an uncrewed approach.";
                return false;
            }

            if (RemoteAutopilotEnabled())
            {
                status = "FCS off: disable the remote autopilot first.";
                return false;
            }
            if (selected == null)
            {
                status = uncrewed ? "FCS off: need a controller named " + DroneControllerName + "." :
                    "FCS off: occupy a cockpit tagged [FCS].";
                return false;
            }

            var thrusters = new List<IMyThrust>();
            var gyros = new List<IMyGyro>();
            GridTerminalSystem.GetBlocksOfType(thrusters, block => block.CubeGrid == selected.CubeGrid);
            GridTerminalSystem.GetBlocksOfType(gyros, block => block.CubeGrid == selected.CubeGrid);
            previousThrusterControl = selected.ControlThrusters;
            previousDampeners = selected.DampenersOverride;
            if (!flight.Acquire(thrusters, gyros, selected.WorldMatrix))
            {
                cockpit = null;
                status = "FCS off: need thrusters and gyros free of existing overrides.";
                return false;
            }

            cockpit = selected;
            droneDock = uncrewed;
            mode = requested;
            try
            {
                cockpit.ControlThrusters = false;
                cockpit.DampenersOverride = false;
                Runtime.UpdateFrequency = dockCharging ? UpdateFrequency.Update1 | UpdateFrequency.Update10 : UpdateFrequency.Update1;
            }
            catch (Exception error)
            {
                Release("FCS off: engagement failed. " + error.Message);
            }
            return true;
        }

        private bool PilotHasControl()
        {
            foreach (var controller in controllers)
                if (controller.CubeGrid == Me.CubeGrid && controller.IsUnderControl)
                    return true;
            return false;
        }

        private bool RemoteAutopilotEnabled()
        {
            foreach (var remote in remotes)
                if (remote.IsAutoPilotEnabled)
                    return true;
            return false;
        }

        private void Release(string reason, bool dockingSucceeded = false)
        {
            status = reason;
            if (mode == Mode.Off)
                return;

            bool signalDroneResult = droneDock;
            var previousCockpit = cockpit;
            cockpit = null;
            mode = Mode.Off;
            droneDock = false;
            Runtime.UpdateFrequency = dockCharging ? UpdateFrequency.Update10 : UpdateFrequency.None;

            int failures = flight.Release();
            try { previousCockpit.ControlThrusters = previousThrusterControl; }
            catch (Exception) { failures++; }
            try { previousCockpit.DampenersOverride = previousDampeners; }
            catch (Exception) { failures++; }

            if (failures > 0)
                status += "\nCould not restore " + failures + " actuator or cockpit settings.";
            if (signalDroneResult)
            {
                bool ready = dockingSucceeded && failures == 0;
                if (ready)
                {
                    dockCharging = true;
                    SaveSettings();
                    Runtime.UpdateFrequency = UpdateFrequency.Update10;
                    ready = SetDockBatteryMode(ChargeMode.Recharge);
                }
                SignalDockResult(ready);
            }
        }

        private bool SetDockBatteryMode(ChargeMode chargeMode)
        {
            try
            {
                batteries.Clear();
                GridTerminalSystem.GetBlocksOfType(batteries, battery => battery.CubeGrid == Me.CubeGrid);
                bool allSet = true;
                foreach (var battery in batteries)
                {
                    try { battery.ChargeMode = chargeMode; }
                    catch (Exception) { allSet = false; }
                }
                if (allSet)
                    return true;
            }
            catch (Exception)
            {
                // A later power check can retry after the terminal becomes available.
            }

            status = "FCS: could not set all ship batteries to " + chargeMode + ".";
            return false;
        }

        private static bool TryReadDepartureCharge(string text, out int charge) =>
            int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out charge) && charge >= 0 && charge <= 100;

        private double GetBatteryChargePercent()
        {
            batteries.Clear();
            GridTerminalSystem.GetBlocksOfType(batteries, battery => battery.CubeGrid == Me.CubeGrid &&
                battery.IsFunctional && battery.Enabled);
            double stored = 0;
            double capacity = 0;
            foreach (var battery in batteries)
            {
                stored += battery.CurrentStoredPower;
                capacity += battery.MaxStoredPower;
            }
            return capacity > 0 ? 100 * stored / capacity : 100;
        }

        private void CheckDockPower()
        {
            try
            {
                var connector = FindDockConnector();
                if (connector != null && connector.Status == MyShipConnectorStatus.Connected)
                {
                    if (departureRequested && mode == Mode.Off)
                        DepartBerth(false);
                    return;
                }

                if (SetDockBatteryMode(ChargeMode.Auto))
                {
                    dockCharging = false;
                    departureRequested = false;
                    SaveSettings();
                    if (mode == Mode.Off)
                        Runtime.UpdateFrequency = UpdateFrequency.None;
                    status = "FCS: undocked. Ship batteries set to Auto.";
                }
            }
            catch (Exception error)
            {
                status = "FCS: dock power check failed. " + error.Message;
            }
        }

        private void DepartBerth(bool requestedNow)
        {
            if (mode != Mode.Off)
            {
                Echo("Turn FCS off before requesting departure.");
                return;
            }

            try
            {
                var connector = FindDockConnector();
                var port = connector == null ? null : connector.OtherConnector;
                if (connector == null || connector.Status != MyShipConnectorStatus.Connected ||
                    port == null || port.EntityId != dockPortId)
                {
                    status = "FCS: departure requires a connection to the recorded berth.";
                    departureRequested = false;
                    SaveSettings();
                    return;
                }
                if (dockCharging)
                {
                    double charge = GetBatteryChargePercent();
                    if (charge < departureCharge)
                    {
                        if (requestedNow && !departureRequested)
                        {
                            departureRequested = true;
                            SaveSettings();
                        }
                        status = "FCS: waiting for battery charge " + Math.Round(charge, 1) +
                            "% / " + departureCharge + "%.";
                        return;
                    }
                }
                if (!SetDockBatteryMode(ChargeMode.Auto))
                    return;

                connector.Disconnect();
                departureRequested = false;
                if (connector.Status != MyShipConnectorStatus.Connected)
                {
                    dockCharging = false;
                    Runtime.UpdateFrequency = UpdateFrequency.None;
                }
                SaveSettings();
                status = "FCS: ship batteries set to Auto. Disconnect requested.";
            }
            catch (Exception error)
            {
                status = "FCS: departure failed. " + error.Message;
            }
        }

        private void SignalDockResult(bool success)
        {
            string timerName = success ? DockSuccessTimer : DockFailureTimer;
            try
            {
                var timer = GridTerminalSystem.GetBlockWithName(timerName) as IMyTimerBlock;
                if (timer != null && timer.CubeGrid == Me.CubeGrid && timer.IsWorking)
                    timer.StartCountdown();
            }
            catch (Exception error)
            {
                Echo("Could not start " + timerName + ": " + error.Message);
            }
        }

        private enum Mode { Off, Normal, Manual, Dock }

        private sealed class FlightController
        {
            private const double DeadZone = 0.03;
            private const double Acceleration = 5;
            public double BrakingDistance { get; set; } = DefaultBrakingDistance;
            private const double GyroLimit = 1.2;
            private const double ThrustReserve = 0.10;
            private const double LiftAccelerationReserve = 1.0;
            private const double MaximumResidualLeverArm = 0.05;
            private readonly ThrustAllocator thrust = new ThrustAllocator();
            private readonly List<IMyGyro> gyros = new List<IMyGyro>();
            private readonly double[] capacity = new double[6];
            private Vector3D previousVelocity;
            private Vector3D disturbance;
            private bool hasSample;
            public bool EstimateAerodynamics { get; set; }

            public bool Acquire(List<IMyThrust> thrusters, List<IMyGyro> availableGyros, MatrixD frame)
            {
                foreach (var gyro in availableGyros)
                    if (gyro.GyroOverride)
                        return false;

                bool gyroAvailable = false;
                foreach (var gyro in availableGyros)
                    gyroAvailable |= gyro.IsWorking;
                if (!gyroAvailable || !thrust.Acquire(thrusters, frame))
                    return false;

                gyros.AddRange(availableGyros);
                return true;
            }

            public string Update(IMyShipController pilot, Mode mode, double dt, Func<bool> hasInstructionBudget)
            {
                var frame = pilot.WorldMatrix;
                var velocity = pilot.GetShipVelocities();
                double mass = pilot.CalculateShipMass().PhysicalMass;
                if (mass <= 0)
                    throw new InvalidOperationException("ship mass unavailable");

                var gravity = pilot.GetNaturalGravity();
                var center = pilot.CenterOfMass;
                var measuredForce = thrust.Measure(frame, center, capacity);
                // Enable this estimator through the PB command for free-flight trials.
                if (EstimateAerodynamics && hasSample && dt >= 0.008 && dt <= 0.15 &&
                    velocity.LinearVelocity.LengthSquared() > 4)
                {
                    var estimate = (velocity.LinearVelocity - previousVelocity) / dt - gravity -
                        Vector3D.TransformNormal(measuredForce / mass, frame);
                    double magnitude = estimate.Length();
                    if (magnitude > 4)
                        estimate *= 4 / magnitude;
                    disturbance += (estimate - disturbance) * Math.Min(dt, 0.05);
                }
                else
                    disturbance = Vector3D.Zero;
                previousVelocity = velocity.LinearVelocity;
                hasSample = true;

                var worldToLocal = MatrixD.Transpose(frame);
                var localVelocity = Vector3D.TransformNormal(velocity.LinearVelocity, worldToLocal);
                var hold = Vector3D.TransformNormal(-(gravity + disturbance) * mass, worldToLocal);
                var input = pilot.MoveIndicator;
                var rotation = pilot.RotationIndicator;
                var turn = new Vector3D(rotation.X, rotation.Y, pilot.RollIndicator);
                var request = new Vector3D(Command(input.X), Command(input.Y), Command(input.Z));
                var target = Vector3D.Zero;
                var pilotOnly = Vector3D.Zero;
                var stabilization = Vector3D.Zero;
                bool risk = false;
                bool saturated = false;
                double longestStop = 0;
                int limitingAxis = 0;

                for (int axis = 0; axis < 3; axis++)
                {
                    double forward = capacity[axis * 2];
                    double reverse = capacity[axis * 2 + 1];
                    double support = hold.GetDim(axis);
                    double speed = localVelocity.GetDim(axis);
                    double axisInput = request.GetDim(axis);
                    double command = axisInput * mass * Acceleration;
                    double brakingForce = speed >= 0 ? reverse + support : forward - support;
                    double brakingAcceleration = Math.Max(0, brakingForce) / mass;
                    double safeSpeed = Math.Sqrt(2 * BrakingDistance * brakingAcceleration);
                    if (speed != 0)
                    {
                        double stoppingDistance = brakingAcceleration > 0
                            ? speed * speed / (2 * brakingAcceleration) : double.PositiveInfinity;
                        if (stoppingDistance > longestStop)
                        {
                            longestStop = stoppingDistance;
                            limitingAxis = axis;
                        }
                    }
                    bool overspeed = Math.Abs(speed) > safeSpeed;
                    bool buildsSpeedWithoutBrake = command > 0 && reverse + support <= 0 ||
                        command < 0 && forward - support <= 0;
                    bool unsupported = support > forward || support < -reverse;
                    risk |= unsupported || overspeed || buildsSpeedWithoutBrake;

                    double force;
                    if (mode == Mode.Manual && command != 0)
                    {
                        // On an active axis, assistance can add force only along the input.
                        double physicalLimit = command > 0 ? forward : reverse;
                        double pilotForce = Math.Min(1, Math.Abs(axisInput)) * physicalLimit;
                        pilotOnly.SetDim(axis, Math.Sign(command) * pilotForce);
                        double alignedHelp = Math.Max(0, Math.Sign(command) * support);
                        force = Math.Sign(command) * Math.Min(physicalLimit, pilotForce + alignedHelp);
                        saturated |= pilotForce + alignedHelp > physicalLimit || physicalLimit <= 0;
                    }
                    else
                    {
                        double lower = -reverse;
                        double upper = forward;
                        if (mode == Mode.Normal)
                        {
                            lower *= 1 - ThrustReserve;
                            upper *= 1 - ThrustReserve;
                            if (overspeed || buildsSpeedWithoutBrake)
                            {
                                double correction = Math.Max(0, Math.Abs(speed) - safeSpeed) * mass * 0.8;
                                if (speed > 0 || command > 0 && speed == 0)
                                    command = Math.Min(command, -correction);
                                else
                                    command = Math.Max(command, correction);
                            }
                        }
                        double desired = support + (command == 0 ? -speed * mass * 0.6 : command);
                        force = Clamp(desired, lower, upper);
                        // Use full available force to hold when the reserve cannot be maintained.
                        if (mode == Mode.Normal && (support < lower || support > upper))
                            force = Clamp(support, -reverse, forward);
                        saturated |= Math.Abs(desired - force) > 1;
                    }
                    target.SetDim(axis, force);
                    double dampedSupport = support - speed * mass * 0.6;
                    stabilization.SetDim(axis, overspeed ? force : Clamp(dampedSupport, -reverse, forward));
                }

                var gyroMask = new Vector3D(Command(turn.X) == 0 ? 1 : 0,
                    Command(turn.Y) == 0 ? 1 : 0, Command(turn.Z) == 0 ? 1 : 0);
                var requestedRate = new Vector3D(Rotation(turn.X, 0), Rotation(turn.Y, 0), Rotation(turn.Z, 0));
                bool turnRisk = !HasSupportDuringTurn(hold, target - hold, requestedRate, hasInstructionBudget);
                bool balanceChecked = hasInstructionBudget();
                bool balanceRisk = balanceChecked && !HasBalancedThrust(target, gyroMask);
                if (mode == Mode.Manual && balanceRisk && request.LengthSquared() > 0)
                    target = pilotOnly;
                if (mode == Mode.Normal && (risk || turnRisk || balanceRisk || !balanceChecked))
                {
                    target = stabilization;
                    turn = Vector3D.Zero;
                    gyroMask = Vector3D.One;
                }
                var residualMoment = thrust.Solve(target, gyroMask);
                thrust.Apply();
                var localAngularVelocity = Vector3D.TransformNormal(velocity.AngularVelocity, worldToLocal);
                var rotationCommand = new Vector3D(
                    Rotation(turn.X, localAngularVelocity.X),
                    Rotation(turn.Y, localAngularVelocity.Y),
                    Rotation(turn.Z, localAngularVelocity.Z));
                ApplyGyros(frame, rotationCommand);

                return (mode == Mode.Manual ? "FCS manual assist" : "FCS normal") +
                    (risk ? "\nWarning: insufficient support or braking reserve." : "") +
                    (turnRisk ? "\nWarning: the requested turn reduces support reserve." : "") +
                    (saturated ? "\nThrust saturated." : "") +
                    (balanceRisk ? "\nWarning: limited moment compensation for the requested thrust." : "") +
                    (!balanceChecked ? "\nThrust balance check skipped: instruction budget." : "") +
                    "\nSpeed: " + Math.Round(localVelocity.Length(), 1) + " m/s" +
                    "\nMax axis stopping estimate (" + "XYZ"[limitingAxis] + "): " +
                    (double.IsPositiveInfinity(longestStop) ? "unavailable" : Math.Round(longestStop, 1) + " m") +
                    "\nBraking distance budget: " + BrakingDistance + " m per axis" +
                    "\nResidual thrust moment: " + Math.Round(residualMoment.Length()) + " N m";
            }

            public void Approach(IMyShipController pilot, Vector3D velocity, Vector3D angularVelocity,
                Vector3D desiredVelocity)
            {
                var frame = pilot.WorldMatrix;
                var toLocal = MatrixD.Transpose(frame);
                var mass = pilot.CalculateShipMass().PhysicalMass;
                if (mass <= 0)
                    throw new InvalidOperationException("ship mass unavailable");

                thrust.Measure(frame, pilot.CenterOfMass, capacity);
                var acceleration = (desiredVelocity - velocity) * 1.5;
                var target = Vector3D.TransformNormal((acceleration - pilot.GetNaturalGravity()) * mass, toLocal);
                for (int axis = 0; axis < 3; axis++)
                {
                    double force = target.GetDim(axis);
                    if (force > capacity[axis * 2] * (1 - ThrustReserve) ||
                        force < -capacity[axis * 2 + 1] * (1 - ThrustReserve))
                        throw new InvalidOperationException("insufficient thrust reserve for docking");
                }

                var moment = thrust.Solve(target, Vector3D.One);
                if (moment.LengthSquared() > target.LengthSquared() * MaximumResidualLeverArm * MaximumResidualLeverArm)
                    throw new InvalidOperationException("insufficient moment compensation for docking");

                thrust.Apply();
                var spin = Vector3D.TransformNormal(angularVelocity, toLocal);
                ApplyGyros(frame, new Vector3D(Rotation(0, spin.X), Rotation(0, spin.Y), Rotation(0, spin.Z)));
            }

            private void ApplyGyros(MatrixD frame, Vector3D rotationCommand)
            {
                var worldRotation = Vector3D.TransformNormal(rotationCommand, frame);
                foreach (var gyro in gyros)
                {
                    if (!gyro.IsWorking)
                        continue;
                    var local = Vector3D.TransformNormal(worldRotation, MatrixD.Transpose(gyro.WorldMatrix));
                    gyro.Pitch = (float)Clamp(local.X, -GyroLimit, GyroLimit);
                    gyro.Yaw = (float)Clamp(local.Y, -GyroLimit, GyroLimit);
                    gyro.Roll = (float)Clamp(local.Z, -GyroLimit, GyroLimit);
                    gyro.GyroOverride = true;
                }
            }

            public string AssessMass(IMyShipController pilot, Func<bool> hasInstructionBudget)
            {
                double gravity = pilot.GetNaturalGravity().Length();
                double mass = pilot.CalculateShipMass().PhysicalMass;
                if (gravity < 0.05)
                    return "Mass: " + Math.Round(mass) + " kg. Natural gravity too low for a lift estimate.";

                // This only samples blocks and computes hypothetical commands.
                thrust.Measure(pilot.WorldMatrix, pilot.CenterOfMass, capacity);
                double requiredAcceleration = gravity + LiftAccelerationReserve;
                double level = EstimateLiftLimit(Vector3D.Up, capacity[2], requiredAcceleration, hasInstructionBudget);
                double extraction = EstimateLiftLimit(Vector3D.Backward, capacity[4], requiredAcceleration,
                    hasInstructionBudget);

                return "Mass: " + Math.Round(mass) + " kg" +
                    "\nLevel flight: " + FormatLiftLimit(level, mass) +
                    "\nNose-down extraction: " + FormatLiftLimit(extraction, mass);
            }

            private double EstimateLiftLimit(Vector3D direction, double axialThrust, double acceleration,
                Func<bool> hasInstructionBudget)
            {
                double lower = 0;
                double upper = axialThrust * (1 - ThrustReserve);
                if (upper <= 0)
                    return 0;
                for (int i = 0; i < 8; i++)
                {
                    if (!hasInstructionBudget())
                        return double.NaN;
                    // Check the full reserved limit first so balanced frames can finish in one solve.
                    double force = i == 0 ? upper : (lower + upper) / 2;
                    var moment = thrust.Solve(direction * force, Vector3D.One);
                    if (moment.LengthSquared() <= force * force * MaximumResidualLeverArm * MaximumResidualLeverArm)
                    {
                        lower = force;
                        if (i == 0)
                            break;
                    }
                    else
                        upper = force;
                }
                return lower / acceleration;
            }

            private static string FormatLiftLimit(double limit, double currentMass)
            {
                if (double.IsNaN(limit))
                    return "instruction budget reached";
                double roundedLimit = Math.Floor(limit);
                double remaining = Math.Floor(limit - currentMass);
                return roundedLimit + " kg (" + (remaining > 0 ? "+" : "") + remaining + " kg margin)";
            }

            private bool HasBalancedThrust(Vector3D force, Vector3D torqueMask)
            {
                var moment = thrust.Solve(force, torqueMask) * torqueMask;
                return moment.LengthSquared() <= force.LengthSquared() *
                    MaximumResidualLeverArm * MaximumResidualLeverArm;
            }

            private bool HasSupportDuringTurn(Vector3D support, Vector3D maneuverForce,
                Vector3D rate, Func<bool> hasInstructionBudget)
            {
                double speed = rate.Length();
                if (speed < 0.001)
                    return true;

                // Check intermediate attitudes over one second of the rate command.
                for (int sample = 1; sample <= 4; sample++)
                {
                    if (!hasInstructionBudget())
                        return false;

                    var next = maneuverForce + Vector3D.TransformNormal(support,
                        MatrixD.CreateFromAxisAngle(rate / speed, -speed * sample / 4));
                    for (int axis = 0; axis < 3; axis++)
                    {
                        double force = next.GetDim(axis);
                        double available = capacity[axis * 2 + (force < 0 ? 1 : 0)];
                        if (Math.Abs(force) > available * (1 - ThrustReserve))
                            return false;
                    }
                    if (!HasBalancedThrust(next, Vector3D.One))
                        return false;
                }
                return true;
            }

            public int Release()
            {
                int failures = thrust.Release();
                foreach (var gyro in gyros)
                {
                    try
                    {
                        gyro.GyroOverride = false;
                        gyro.Pitch = gyro.Yaw = gyro.Roll = 0;
                    }
                    catch (Exception) { failures++; }
                }
                gyros.Clear();
                hasSample = false;
                disturbance = Vector3D.Zero;
                return failures;
            }

            private static double Rotation(double input, double angularVelocity)
            {
                return Command(input) == 0
                    ? Clamp(-angularVelocity * 0.75, -GyroLimit, GyroLimit)
                    : Clamp(input * 0.12, -GyroLimit, GyroLimit);
            }

            private static double Command(double input) => Math.Abs(input) > DeadZone ? input : 0;
        }

        private sealed class ThrustAllocator
        {
            private sealed class Engine
            {
                public IMyThrust Block;
                public double Available;
                public double Force;
                public Vector3D MomentPerNewton;
            }

            private readonly List<Engine>[] groups = new List<Engine>[6];

            public bool Acquire(List<IMyThrust> blocks, MatrixD frame)
            {
                foreach (var block in blocks)
                    if (block.ThrustOverridePercentage > 0)
                        return false;

                for (int i = 0; i < groups.Length; i++)
                    groups[i] = new List<Engine>();
                var toLocal = MatrixD.Transpose(frame);
                bool available = false;
                foreach (var block in blocks)
                {
                    var direction = Vector3D.TransformNormal(block.WorldMatrix.Backward, toLocal);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        double component = direction.GetDim(axis);
                        if (Math.Abs(component) < 0.999)
                            continue;
                        groups[axis * 2 + (component < 0 ? 1 : 0)].Add(new Engine { Block = block });
                        available |= block.IsWorking;
                        break;
                    }
                }
                return available;
            }

            public Vector3D Measure(MatrixD frame, Vector3D center, double[] capacity)
            {
                var toLocal = MatrixD.Transpose(frame);
                var force = Vector3D.Zero;
                for (int group = 0; group < groups.Length; group++)
                {
                    double total = 0;
                    foreach (var engine in groups[group])
                    {
                        var block = engine.Block;
                        var direction = Vector3D.TransformNormal(block.WorldMatrix.Backward, toLocal);
                        var leverArm = Vector3D.TransformNormal(block.GetPosition() - center, toLocal);
                        engine.MomentPerNewton = Vector3D.Cross(leverArm, direction);
                        engine.Available = block.IsWorking ? Math.Max(0, block.MaxEffectiveThrust) : 0;
                        total += engine.Available;
                        force += direction * block.CurrentThrust;
                    }
                    capacity[group] = total;
                }
                return force;
            }

            public Vector3D Solve(Vector3D target, Vector3D torqueMask)
            {
                var moment = Vector3D.Zero;
                for (int group = 0; group < groups.Length; group++)
                {
                    double demand = Math.Max(0, target.GetDim(group / 2) * (group % 2 == 0 ? 1 : -1));
                    double total = 0;
                    foreach (var engine in groups[group])
                        total += engine.Available;
                    double fraction = total > 0 ? Math.Min(1, demand / total) : 0;
                    foreach (var engine in groups[group])
                    {
                        engine.Force = engine.Available * fraction;
                        moment += engine.MomentPerNewton * engine.Force;
                    }
                }

                // Redistribute force between parallel engines. These transfers preserve pilot force.
                // Adjacent pairs and two passes bound work by the number of configured engines.
                for (int pass = 0; pass < 2; pass++)
                    foreach (var group in groups)
                        for (int i = 1; i < group.Count; i++)
                        {
                            var from = group[i - 1];
                            var to = group[i];
                            var change = to.MomentPerNewton - from.MomentPerNewton;
                            var controlled = change * torqueMask;
                            if ((change - controlled).LengthSquared() > 0.0001)
                                continue;
                            double denominator = controlled.LengthSquared();
                            if (denominator < 0.0001)
                                continue;
                            double transfer = Clamp(-Vector3D.Dot(moment, controlled) / denominator,
                                -Math.Min(to.Force, from.Available - from.Force),
                                Math.Min(from.Force, to.Available - to.Force));
                            from.Force -= transfer;
                            to.Force += transfer;
                            moment += change * transfer;
                        }

                return moment;
            }

            public void Apply()
            {
                foreach (var group in groups)
                    foreach (var engine in group)
                        engine.Block.ThrustOverridePercentage = engine.Available > 0
                            ? (float)Clamp(engine.Force / engine.Available, 0, 1) : 0;
            }

            public int Release()
            {
                int failures = 0;
                foreach (var group in groups)
                {
                    foreach (var engine in group)
                    {
                        try { engine.Block.ThrustOverridePercentage = 0; }
                        catch (Exception) { failures++; }
                    }
                }
                return failures;
            }
        }

        private static double Clamp(double value, double minimum, double maximum) =>
            Math.Max(minimum, Math.Min(maximum, value));
        // PB BODY END
    }
}
