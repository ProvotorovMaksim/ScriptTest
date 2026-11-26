using System;
using VRageMath;

namespace SpaceEngineers.Libs.RadarCore
{
    /// <summary>
    /// Единичное наблюдение за игровым объектом.
    /// IsMeasured = true — фактическое измерение через Raycast.
    /// IsMeasured = false — расчётное положение между измерениями.
    /// </summary>
    public sealed class ContactObservation
    {
        public long EntityId;
        public string Name = "";
        public string Type = "";
        public string Relationship = "";
        public Vector3D Position;
        public Vector3D Velocity;
        public BoundingBoxD? BoundingBox;
        public Vector3D? HitPosition;
        public long TimestampTicks;
        public bool IsMeasured;
        public double Range;
        public Vector3D Direction;
    }

    public sealed class RadarCue
    {
        public long EntityId { get; private set; }
        public long Expires { get; private set; }
        public Vector3D Position { get; private set; }

        private long _measuredAt;
        private Vector3D _velocity;

        public Vector3D PositionAt(long now) => _measuredAt == 0 ? Position :
            Position + _velocity * ((now - _measuredAt) / (double)TimeSpan.TicksPerSecond);

        public bool Matches(long entityId) => entityId == EntityId;

        public static bool TryParse(string argument, long now, out RadarCue cue)
        {
            cue = null;
            var fields = argument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            long id, expires;
            double x, y, z;
            if ((fields.Length != 5 && fields.Length != 9) || !long.TryParse(fields[0], out id) || id == 0 ||
                !long.TryParse(fields[1], out expires) || expires <= now ||
                expires - now > TimeSpan.FromSeconds(30).Ticks ||
                !TryReadCoordinate(fields[2], out x) || !TryReadCoordinate(fields[3], out y) ||
                !TryReadCoordinate(fields[4], out z))
                return false;
            long measuredAt = 0;
            Vector3D velocity = Vector3D.Zero;
            if (fields.Length == 9)
            {
                double vx, vy, vz;
                if (!long.TryParse(fields[5], out measuredAt) || measuredAt <= 0 || measuredAt > now ||
                    expires - measuredAt > TimeSpan.FromSeconds(32).Ticks ||
                    !TryReadCoordinate(fields[6], out vx) || !TryReadCoordinate(fields[7], out vy) ||
                    !TryReadCoordinate(fields[8], out vz))
                    return false;
                velocity = new Vector3D(vx, vy, vz);
            }
            cue = new RadarCue { EntityId = id, Expires = expires, Position = new Vector3D(x, y, z),
                _measuredAt = measuredAt, _velocity = velocity };
            return true;
        }

        private static bool TryReadCoordinate(string text, out double coordinate) =>
            double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out coordinate) &&
                !double.IsInfinity(coordinate) && !double.IsNaN(coordinate);
    }
}