using System;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage.Game;
using VRageMath;
using SpaceEngineers.Libs.RadarCore;

namespace Tests.Radar.Tests
{
    [TestFixture]
    public class ContactTrackerTests
    {
        [Test]
        public void TrackerRecordsAndClearsARealRaycastObservation()
        {
            var tracker = new ContactTracker();
            var now = DateTime.UtcNow;
            var position = new Vector3D(1000, 0, 0);
            var info = new MyDetectedEntityInfo(12345L, "Test Ship", MyDetectedEntityType.LargeGrid,
                position, MatrixD.Identity, Vector3.Zero, MyRelationsBetweenPlayerAndBlock.Neutral,
                new BoundingBoxD(position - Vector3D.One, position + Vector3D.One), now.Ticks);
            tracker.Update(info, 1000, now);

            Assert.That(tracker.EntityId, Is.EqualTo(12345L));
            Assert.That(tracker.Name, Is.EqualTo("Test Ship"));
            Assert.That(tracker.Position, Is.EqualTo(position));
            Assert.That(tracker.LastSeen, Is.EqualTo(now));
            Assert.That(tracker.IsActive, Is.True);

            tracker.Lose();
            Assert.That(tracker.EntityId, Is.Zero);
            Assert.That(tracker.IsActive, Is.False);
            Assert.That(tracker.HitPosition, Is.Null);
        }
    }
}
