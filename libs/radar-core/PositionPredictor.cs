using System;
using VRageMath;

namespace SpaceEngineers.Libs.RadarCore
{
    public sealed class PositionPredictor
    {
        private Vector3D _lastPosition = Vector3D.Zero;
        private Vector3D _lastVelocity = Vector3D.Zero;
        private bool _hasLastPosition = false;

        /// <summary>
        /// Predict position at given time ticks from now.
        /// </summary>
        public Vector3D Predict(Vector3D currentPosition, Vector3D velocity, double dt)
        {
            if (!_hasLastPosition)
            {
                return currentPosition + velocity * dt;
            }

            // Use last known state for more stable extrapolation
            return _lastPosition + _lastVelocity * dt;
        }

        /// <summary>
        /// Update internal state with measured position and time.
        /// </summary>
        public void UpdateState(Vector3D position, Vector3D velocity)
        {
            _lastPosition = position;
            _lastVelocity = velocity;
            _hasLastPosition = true;
        }

        /// <summary>
        /// Reset predictor state.
        /// </summary>
        public void Reset()
        {
            _lastPosition = Vector3D.Zero;
            _lastVelocity = Vector3D.Zero;
            _hasLastPosition = false;
        }
    }
}