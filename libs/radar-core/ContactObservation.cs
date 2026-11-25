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
}