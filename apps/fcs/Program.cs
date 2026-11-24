using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using VRageMath;

namespace SpaceEngineers.Apps.FlightControl
{
    public sealed class Program : MyGridProgram
    {
        // PB BODY START
        private readonly FlightController flight = new FlightController();
        private readonly List<IMyRemoteControl> remotes = new List<IMyRemoteControl>();
        private IMyCockpit cockpit;
        private bool previousThrusterControl;
        private bool previousDampeners;
        private Mode mode;
        private string status = "FCS off. Commands: normal, manual, off, status.";

        public void Main(string argument, UpdateType source)
        {
            switch ((argument ?? "").Trim().ToLowerInvariant())
            {
                case "normal": Activate(Mode.Normal); break;
                case "aero on": flight.EstimateAerodynamics = true; break;
                case "aero off": flight.EstimateAerodynamics = false; break;
                case "manual": Activate(Mode.Manual); break;
                case "off": Release("FCS off."); break;
            }

            if (mode != Mode.Off && (source & UpdateType.Update1) != 0)
            {
                try
                {
                    if (!cockpit.IsWorking || !cockpit.IsUnderControl)
                        Release("FCS off: cockpit unavailable or pilot absent.");
                    else if (RemoteAutopilotEnabled())
                        Release("FCS off: remote autopilot took control.");
                    else
                        status = flight.Update(cockpit, mode, Runtime.TimeSinceLastRun.TotalSeconds);
                }
                catch (Exception error)
                {
                    Release("FCS off: " + error.Message);
                }
            }

            Echo(status);
        }

        private void Activate(Mode requested)
        {
            if (mode != Mode.Off)
            {
                mode = requested;
                return;
            }

            IMyCockpit selected = null;
            remotes.Clear();
            var controllers = new List<IMyShipController>();
            GridTerminalSystem.GetBlocksOfType(controllers, block => block.IsSameConstructAs(Me));
            foreach (var controller in controllers)
            {
                var remote = controller as IMyRemoteControl;
                if (remote != null)
                    remotes.Add(remote);
                var candidate = controller as IMyCockpit;
                if (candidate != null && candidate.CubeGrid == Me.CubeGrid && candidate.IsWorking &&
                    candidate.IsUnderControl && candidate.CustomName.Contains("[FCS]"))
                    selected = candidate;
            }

            if (RemoteAutopilotEnabled())
            {
                status = "FCS off: disable the remote autopilot first.";
                return;
            }
            if (selected == null)
            {
                status = "FCS off: occupy a cockpit tagged [FCS].";
                return;
            }

            var thrusters = new List<IMyThrust>();
            var gyros = new List<IMyGyro>();
            GridTerminalSystem.GetBlocksOfType(thrusters, block => block.CubeGrid == selected.CubeGrid);
            GridTerminalSystem.GetBlocksOfType(gyros, block => block.CubeGrid == selected.CubeGrid);
            if (!flight.Acquire(thrusters, gyros, selected.WorldMatrix))
            {
                cockpit = null;
                status = "FCS off: need thrusters and gyros free of existing overrides.";
                return;
            }

            cockpit = selected;
            previousThrusterControl = cockpit.ControlThrusters;
            previousDampeners = cockpit.DampenersOverride;
            cockpit.ControlThrusters = false;
            cockpit.DampenersOverride = false;
            mode = requested;
            Runtime.UpdateFrequency = UpdateFrequency.Update1;
        }

        private bool RemoteAutopilotEnabled()
        {
            foreach (var remote in remotes)
                if (remote.IsAutoPilotEnabled)
                    return true;
            return false;
        }

        private void Release(string reason)
        {
            if (mode != Mode.Off)
            {
                flight.Release();
                cockpit.ControlThrusters = previousThrusterControl;
                cockpit.DampenersOverride = previousDampeners;
                cockpit = null;
                mode = Mode.Off;
                Runtime.UpdateFrequency = UpdateFrequency.None;
            }
            status = reason;
        }

        private enum Mode { Off, Normal, Manual }

        private sealed class FlightController
        {
            private const double DeadZone = 0.03;
            private const double Acceleration = 5;
            private const double BrakingDistance = 50;
            private const double GyroLimit = 1.2;
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

            public string Update(IMyCockpit pilot, Mode mode, double dt)
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
                bool risk = false;
                bool saturated = false;

                for (int axis = 0; axis < 3; axis++)
                {
                    double forward = capacity[axis * 2];
                    double reverse = capacity[axis * 2 + 1];
                    double support = hold.GetDim(axis);
                    double speed = localVelocity.GetDim(axis);
                    double axisInput = request.GetDim(axis);
                    double command = axisInput * mass * Acceleration;
                    double brakingForce = speed >= 0 ? reverse + support : forward - support;
                    double safeSpeed = Math.Sqrt(2 * BrakingDistance * Math.Max(0, brakingForce) / mass);
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
                            lower *= 0.9;
                            upper *= 0.9;
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
                }

                var gyroMask = new Vector3D(Command(turn.X) == 0 ? 1 : 0,
                    Command(turn.Y) == 0 ? 1 : 0, Command(turn.Z) == 0 ? 1 : 0);
                var requestedRate = new Vector3D(Rotation(turn.X, 0), Rotation(turn.Y, 0), Rotation(turn.Z, 0));
                bool turnRisk = !HasSupportDuringTurn(hold, requestedRate);
                if (mode == Mode.Normal && (risk || turnRisk))
                {
                    turn = Vector3D.Zero;
                    gyroMask = Vector3D.One;
                }
                var residualMoment = thrust.Apply(target, gyroMask);
                var localAngularVelocity = Vector3D.TransformNormal(velocity.AngularVelocity, worldToLocal);
                var rotationCommand = new Vector3D(
                    Rotation(turn.X, localAngularVelocity.X),
                    Rotation(turn.Y, localAngularVelocity.Y),
                    Rotation(turn.Z, localAngularVelocity.Z));
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

                return (mode == Mode.Manual ? "FCS manual assist" : "FCS normal") +
                    (risk ? "\nWarning: insufficient support or braking reserve." : "") +
                    (turnRisk ? "\nWarning: the requested turn reduces support reserve." : "") +
                    (saturated ? "\nThrust saturated." : "") +
                    "\nResidual thrust moment: " + Math.Round(residualMoment.Length()) + " N m";
            }

            private bool HasSupportDuringTurn(Vector3D support, Vector3D rate)
            {
                double speed = rate.Length();
                if (speed < 0.001)
                    return true;

                // Check intermediate attitudes over one second of the rate command.
                for (int sample = 1; sample <= 4; sample++)
                {
                    var next = Vector3D.TransformNormal(support,
                        MatrixD.CreateFromAxisAngle(rate / speed, -speed * sample / 4));
                    for (int axis = 0; axis < 3; axis++)
                    {
                        double force = next.GetDim(axis);
                        double available = capacity[axis * 2 + (force < 0 ? 1 : 0)];
                        if (Math.Abs(force) > available * 0.9)
                            return false;
                    }
                }
                return true;
            }

            public void Release()
            {
                thrust.Release();
                foreach (var gyro in gyros)
                {
                    gyro.GyroOverride = false;
                    gyro.Pitch = gyro.Yaw = gyro.Roll = 0;
                }
                gyros.Clear();
                hasSample = false;
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

            public Vector3D Apply(Vector3D target, Vector3D torqueMask)
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

                foreach (var group in groups)
                    foreach (var engine in group)
                        engine.Block.ThrustOverridePercentage = engine.Available > 0
                            ? (float)Clamp(engine.Force / engine.Available, 0, 1) : 0;
                return moment;
            }

            public void Release()
            {
                foreach (var group in groups)
                    foreach (var engine in group)
                        engine.Block.ThrustOverridePercentage = 0;
            }
        }

        private static double Clamp(double value, double minimum, double maximum) =>
            Math.Max(minimum, Math.Min(maximum, value));
        // PB BODY END
    }
}
