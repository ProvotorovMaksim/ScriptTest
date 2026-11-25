using System;
using VRageMath;
using Sandbox.ModAPI.Ingame;

namespace SpaceEngineers.Libs.RadarCore
{
    public sealed class GimbalController
    {
        private readonly RadarConfig _config;
        private IMyMotorStator _azimuth;
        private IMyMotorStator _elevation;
        private readonly AzimuthSweep _sweep = new AzimuthSweep();

        public GimbalController(RadarConfig config)
        {
            _config = config;
        }

        private bool HasConnectedAxes => _azimuth != null && _elevation != null &&
            _azimuth.TopGrid != null && _azimuth.TopGrid == _elevation.CubeGrid && _elevation.TopGrid != null;

        public string GetUnavailableReason()
        {
            if (!HasConnectedAxes)
                return "Expected rotor on the radar grid, hinge on the rotor head, and a hinge head.";
            if (!_azimuth.IsWorking || !_elevation.IsWorking || _azimuth.RotorLock || _elevation.RotorLock)
                return "Both gimbal axes must be powered, enabled and unlocked.";
            if (LowerLimit(_azimuth, _config.PlatformAzimuthMin) > UpperLimit(_azimuth, _config.PlatformAzimuthMax) ||
                LowerLimit(_elevation, _config.PlatformElevationMin) > UpperLimit(_elevation, _config.PlatformElevationMax))
                return "Gimbal hardware and configured travel limits do not overlap.";
            return null;
        }

        public void Bind(IMyGridTerminalSystem terminal, IMyTerminalBlock owner)
        {
            Unbind();
            _azimuth = terminal.GetBlockWithName(_config.AzimuthBlock) as IMyMotorStator;
            _elevation = terminal.GetBlockWithName(_config.ElevationBlock) as IMyMotorStator;
            if (_azimuth == null || _azimuth.CubeGrid != owner.CubeGrid ||
                _elevation == null || _elevation == _azimuth || !_elevation.IsSameConstructAs(owner))
            {
                _azimuth = null;
                _elevation = null;
                return;
            }
            if (HasConnectedAxes)
                Stop();
        }

        public bool HasCamera(IMyCameraBlock camera) => _elevation != null && camera.CubeGrid == _elevation.TopGrid;

        public void Aim(IMyCameraBlock camera, Vector3D target)
        {
            _sweep.Reset();
            var forward = camera.WorldMatrix.Forward;
            var direction = target - camera.GetPosition();
            Drive(_azimuth, forward, direction, _config.InvertAzimuth,
                _config.PlatformAzimuthMin, _config.PlatformAzimuthMax);
            Drive(_elevation, forward, direction, _config.InvertElevation,
                _config.PlatformElevationMin, _config.PlatformElevationMax);
        }

        private void Drive(IMyMotorStator motor, Vector3D forward, Vector3D direction, bool invert, double min, double max)
        {
            var axis = motor.WorldMatrix.Up;
            forward -= axis * Vector3D.Dot(forward, axis);
            direction -= axis * Vector3D.Dot(direction, axis);
            double error = forward.LengthSquared() < 1e-8 || direction.LengthSquared() < 1e-8 ? 0 :
                Math.Atan2(Vector3D.Dot(axis, Vector3D.Cross(forward, direction)), Vector3D.Dot(forward, direction));
            if (invert)
                error = -error;

            double lower = LowerLimit(motor, min);
            double upper = UpperLimit(motor, max);
            double angle = GimbalAngles.InTravelFrame(motor.Angle, lower, upper);
            DriveTo(motor, angle, angle + error, lower, upper);
        }

        public bool HoldSweepPosition()
        {
            double lower = LowerLimit(_azimuth, _config.PlatformAzimuthMin);
            double upper = UpperLimit(_azimuth, _config.PlatformAzimuthMax);
            double angle = GimbalAngles.InTravelFrame(_azimuth.Angle, lower, upper);
            _elevation.TargetVelocityRad = 0;
            if (lower > upper)
            {
                _azimuth.TargetVelocityRad = 0;
                return false;
            }

            double goal = _sweep.Goal(angle, lower, upper);
            if (AzimuthSweep.IsAtGoal(angle, goal))
            {
                _azimuth.TargetVelocityRad = 0;
                return true;
            }

            DriveTo(_azimuth, angle, goal, lower, upper);
            return false;
        }

        public void AdvanceSweep()
        {
            double lower = LowerLimit(_azimuth, _config.PlatformAzimuthMin);
            double upper = UpperLimit(_azimuth, _config.PlatformAzimuthMax);
            _sweep.Advance(GimbalAngles.InTravelFrame(_azimuth.Angle, lower, upper), lower, upper);
        }

        public bool ParkAtZero()
        {
            bool azimuthParked = ParkAxis(_azimuth, _config.PlatformAzimuthMin, _config.PlatformAzimuthMax);
            bool elevationParked = ParkAxis(_elevation, _config.PlatformElevationMin, _config.PlatformElevationMax);
            return azimuthParked && elevationParked;
        }

        private bool ParkAxis(IMyMotorStator motor, double minimum, double maximum)
        {
            double lower = LowerLimit(motor, minimum);
            double upper = UpperLimit(motor, maximum);
            double angle = GimbalAngles.InTravelFrame(motor.Angle, lower, upper);
            double goal = Math.Max(lower, Math.Min(0, upper));
            DriveTo(motor, angle, goal, lower, upper);
            return Math.Abs(goal - angle) <= 0.002;
        }

        private static double LowerLimit(IMyMotorStator motor, double minimum) =>
            Math.Max(minimum * Math.PI / 180, motor.LowerLimitRad);

        private static double UpperLimit(IMyMotorStator motor, double maximum) =>
            Math.Min(maximum * Math.PI / 180, motor.UpperLimitRad);

        private void DriveTo(IMyMotorStator motor, double angle, double goal, double lower, double upper)
        {
            double speed = _config.PlatformSpeedLimitDegreesPerSecond * Math.PI / 180;
            double error = lower <= upper ? Math.Max(lower, Math.Min(upper, goal)) - angle : 0;
            motor.TargetVelocityRad = Math.Abs(error) < 0.002 ? 0 :
                (float)Math.Max(-speed, Math.Min(speed, error * 2));
        }

        public void Unbind()
        {
            Stop();
            _azimuth = null;
            _elevation = null;
        }

        public void Stop()
        {
            _sweep.Reset();
            if (_azimuth != null)
                _azimuth.TargetVelocityRad = 0;
            if (_elevation != null)
                _elevation.TargetVelocityRad = 0;
        }
    }
    public static class GimbalAngles
    {
        public static double InTravelFrame(double angle, double lower, double upper)
        {
            if (lower > upper || (angle >= lower && angle <= upper))
                return angle;
            double midpoint = lower + (upper - lower) / 2;
            double turn = 2 * Math.PI;
            return angle + turn * Math.Round((midpoint - angle) / turn);
        }
    }

    public sealed class AzimuthSweep
    {
        private const double Step = 5 * Math.PI / 180;
        private const double Tolerance = 0.003;
        private bool _positive = true;
        private double? _goal;

        public double Goal(double angle, double lower, double upper) =>
            Math.Max(lower, Math.Min(upper, _goal ?? angle));

        public static bool IsAtGoal(double angle, double goal) => Math.Abs(angle - goal) <= Tolerance;

        public void Advance(double angle, double lower, double upper)
        {
            if (!IsAtGoal(angle, Goal(angle, lower, upper)))
                return;
            if (angle >= upper - Tolerance)
                _positive = false;
            else if (angle <= lower + Tolerance)
                _positive = true;

            _goal = Math.Max(lower, Math.Min(upper, angle + (_positive ? Step : -Step)));
        }

        public void Reset()
        {
            _goal = null;
            _positive = true;
        }
    }

}
