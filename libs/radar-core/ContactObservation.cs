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

        public bool Matches(long entityId) => entityId == EntityId;

        public static bool TryParse(string argument, long now, out RadarCue cue)
        {
            cue = null;
            var fields = argument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            long id, expires;
            double x, y, z;
            if (fields.Length != 5 || !long.TryParse(fields[0], out id) || id == 0 ||
                !long.TryParse(fields[1], out expires) || expires <= now ||
                expires - now > TimeSpan.FromSeconds(30).Ticks ||
                !TryReadCoordinate(fields[2], out x) || !TryReadCoordinate(fields[3], out y) ||
                !TryReadCoordinate(fields[4], out z))
                return false;
            cue = new RadarCue { EntityId = id, Expires = expires, Position = new Vector3D(x, y, z) };
            return true;
        }

        private static bool TryReadCoordinate(string text, out double coordinate) =>
            double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out coordinate) &&
                !double.IsInfinity(coordinate) && !double.IsNaN(coordinate);
    }
}