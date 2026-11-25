using System;
using VRageMath;

namespace SpaceEngineers.Libs.RadarCore
{
    public sealed class PositionPredictor
    {
        private const double DefaultDt = 0.1; // 100ms default step
        private double _lastTick = 0.0;
        private Vector3D _lastPosition = Vector3D.Zero;
        private Vector3D _lastVelocity = Vector3D.Zero;
        private bool _hasLastPosition = false;

        /// <summary>
        /// Predict position at given time ticks from now.
        /// </summary>
        public Vector3D Predict(Vector3D currentPosition, Vector3D velocity, double dt)
        {
            if (!hasLastPosition || velocity == Vector3D.Zero)
            {
                return currentPosition + velocity * dt;
            }

            // Use last known state for more stable extrapolation
            var elapsed = _lastTick > 0 ? dt : DefaultDt;
            return _lastPosition + _lastVelocity * elapsed;
        }

        /// <summary>
        /// Update internal state with measured position and time.
        /// </summary>
        public void UpdateState(Vector3D position, Vector3D velocity, double tick)
        {
            _lastPosition = position;
            _lastVelocity = velocity;
            _lastTick = tick;
            _hasLastPosition = true;
        }

        /// <summary>
        /// Reset predictor state.
        /// </summary>
        public void Reset()
        {
            _lastPosition = Vector3D.Zero;
            _lastVelocity = Vector3D.Zero;
            _lastTick = 0.0;
            _hasLastPosition = false;
        }
    }
}