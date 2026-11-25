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
        private bool _sweepPositive = true;

        public GimbalController(RadarConfig config)
        {
            _config = config;
        }

        public bool IsReady => _azimuth != null && _elevation != null &&
            _azimuth.IsWorking && _elevation.IsWorking && _azimuth.TopGrid != null &&
            _azimuth.TopGrid == _elevation.CubeGrid && _elevation.TopGrid != null;

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
            if (IsReady)
                Stop();
        }

        public bool HasCamera(IMyCameraBlock camera) => IsReady && camera.CubeGrid == _elevation.TopGrid;

        public void Aim(IMyCameraBlock camera, Vector3D target)
        {
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

            double angle = ReadAngle(motor);
            DriveTo(motor, angle, angle + error, LowerLimit(motor, min), UpperLimit(motor, max));
        }

        public void Sweep()
        {
            double angle = ReadAngle(_azimuth);
            double lower = LowerLimit(_azimuth, _config.PlatformAzimuthMin);
            double upper = UpperLimit(_azimuth, _config.PlatformAzimuthMax);
            if (angle >= upper - 0.01)
                _sweepPositive = false;
            else if (angle <= lower + 0.01)
                _sweepPositive = true;

            DriveTo(_azimuth, angle, _sweepPositive ? upper : lower, lower, upper);
            _elevation.TargetVelocityRad = 0;
        }

        private static double ReadAngle(IMyMotorStator motor) =>
            Math.Atan2(Math.Sin(motor.Angle), Math.Cos(motor.Angle));

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
            if (_azimuth != null)
                _azimuth.TargetVelocityRad = 0;
            if (_elevation != null)
                _elevation.TargetVelocityRad = 0;
        }
    }
}
