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
        public void TrackerAgesAndRetainsLastMeasurementUntilExplicitlyCleared()
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

            Assert.That(tracker.AgeSecondsAt(now.AddSeconds(-1)), Is.Zero);
            Assert.That(tracker.AgeSecondsAt(now.AddSeconds(3)), Is.EqualTo(3));

            tracker.Lose();
            Assert.That(tracker.EntityId, Is.EqualTo(12345L));
            Assert.That(tracker.Position, Is.EqualTo(position));
            Assert.That(tracker.IsActive, Is.False);
            Assert.That(tracker.AgeSecondsAt(now.AddSeconds(6)), Is.EqualTo(6));

            tracker.Clear();
            Assert.That(tracker.AgeSecondsAt(now), Is.Zero);
            Assert.That(tracker.EntityId, Is.Zero);
            Assert.That(tracker.IsActive, Is.False);
            Assert.That(tracker.HitPosition, Is.Null);
        }

        [Test]
        [SetCulture("ru-RU")]
        public void ShareCommandKeepsInvariantGpsAndRejectsExpiredOrLostObservations()
        {
            var tracker = new ContactTracker();
            var now = new DateTime(2026, 1, 1);
            var position = new Vector3D(1000.25, -4.5, 7);
            Assert.That(tracker.TryFormatShareCommand(42, now, out _), Is.False);

            tracker.Update(new MyDetectedEntityInfo(12345L, "Test Ship", MyDetectedEntityType.LargeGrid,
                position, MatrixD.Identity, Vector3.Zero, MyRelationsBetweenPlayerAndBlock.Neutral,
                new BoundingBoxD(position - Vector3D.One, position + Vector3D.One), now.Ticks), 1000, now);

            Assert.That(tracker.TryFormatShareCommand(42, now.AddSeconds(2), out var command), Is.True);
            Assert.That(command, Is.EqualTo("contact 42 GPS:Radar 12345:1000.25:-4.50:7.00:"));
            Assert.That(tracker.TryFormatShareCommand(0, now, out _), Is.False);
            Assert.That(tracker.TryFormatShareCommand(42, now.AddTicks(-1), out _), Is.False);
            Assert.That(tracker.TryFormatShareCommand(42, now.AddSeconds(2).AddTicks(1), out _), Is.False);
            tracker.Lose();
            Assert.That(tracker.TryFormatShareCommand(42, now, out _), Is.False);
            Assert.That(tracker.FormatGps("Radar last fix"), Is.EqualTo("GPS:Radar last fix:1000.25:-4.50:7.00:"));
        }
    }
}
