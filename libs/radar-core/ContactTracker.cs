using System;
using VRageMath;
using Sandbox.ModAPI.Ingame;

namespace SpaceEngineers.Libs.RadarCore
{
    public sealed class ContactTracker
    {
        public long EntityId { get; private set; }
        public string Name { get; private set; } = "";
        public string Type { get; private set; } = "";
        public string Relationship { get; private set; } = "";
        public Vector3D Position { get; private set; }
        public Vector3D Velocity { get; private set; }
        public BoundingBoxD? BoundingBox { get; private set; }
        public Vector3D? HitPosition { get; private set; }
        public double Range { get; private set; }
        public double AgeSeconds { get; private set; }
        public DateTime LastSeen { get; private set; }
        public bool IsActive { get; private set; }

        public void Update(MyDetectedEntityInfo info, double range, DateTime now)
        {
            EntityId = info.EntityId;
            Name = info.Name;
            Type = info.Type.ToString();
            Relationship = info.Relationship.ToString();
            Position = info.Position;
            Velocity = info.Velocity;
            BoundingBox = info.BoundingBox;
            HitPosition = info.HitPosition;
            Range = range;
            LastSeen = now;
            AgeSeconds = (now - LastSeen).TotalSeconds;
            IsActive = EntityId != 0;
        }

        public void Lose()
        {
            EntityId = 0;
            Name = "";
            Type = "";
            Relationship = "";
            Position = Vector3D.Zero;
            Velocity = Vector3D.Zero;
            BoundingBox = null;
            HitPosition = null;
            Range = 0.0;
            IsActive = false;
        }
    }
}