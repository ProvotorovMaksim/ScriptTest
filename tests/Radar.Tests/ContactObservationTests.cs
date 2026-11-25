using NUnit.Framework;
using VRageMath;
using SpaceEngineers.Libs.RadarCore;

namespace Tests.Radar.Tests
{
    [TestFixture]
    public class ContactObservationTests
    {
        [Test]
        public void NetworkCueRequiresFreshFiniteCoordinatesAndMatchingEntity()
        {
            Assert.That(RadarCue.TryParse("42 110 20.5 -3 9", 100, out var cue), Is.True);
            Assert.That(cue.Position, Is.EqualTo(new Vector3D(20.5, -3, 9)));
            Assert.That(cue.Matches(42), Is.True);
            Assert.That(cue.Matches(43), Is.False);
            Assert.That(RadarCue.TryParse("42 100 20.5 -3 9", 100, out _), Is.False);
            Assert.That(RadarCue.TryParse("42 110 Infinity -3 9", 100, out _), Is.False);
        }

        [Test]
        public void ContactObservation_Default_Values()
        {
            var obs = new ContactObservation();
            Assert.AreEqual(0L, obs.EntityId);
            Assert.AreEqual("", obs.Name);
            Assert.AreEqual("", obs.Type);
            Assert.AreEqual("", obs.Relationship);
            Assert.AreEqual(Vector3D.Zero, obs.Position);
            Assert.AreEqual(Vector3D.Zero, obs.Velocity);
            Assert.IsNull(obs.HitPosition);
            Assert.AreEqual(0.0, obs.Range);
            Assert.IsFalse(obs.IsMeasured);
            Assert.AreEqual(Vector3D.Zero, obs.Direction);
        }

        [Test]
        public void ContactObservation_Set_Values()
        {
            var obs = new ContactObservation
            {
                EntityId = 99999L,
                Name = "Test Ship",
                Type = "LargeGrid",
                Relationship = "Neutral",
                Position = new Vector3D(1000, 500, -500),
                Velocity = new Vector3D(50, 0, 0),
                HitPosition = new Vector3D(1000, 0, 0),
                Range = 15000.0,
                IsMeasured = true,
                Direction = Vector3D.Forward
            };

            Assert.AreEqual(99999L, obs.EntityId);
            Assert.AreEqual("Test Ship", obs.Name);
            Assert.AreEqual("LargeGrid", obs.Type);
            Assert.AreEqual("Neutral", obs.Relationship);
            Assert.AreEqual(new Vector3D(1000, 500, -500), obs.Position);
            Assert.AreEqual(new Vector3D(50, 0, 0), obs.Velocity);
            Assert.AreEqual(new Vector3D(1000, 0, 0), obs.HitPosition);
            Assert.AreEqual(15000.0, obs.Range);
            Assert.IsTrue(obs.IsMeasured);
            Assert.AreEqual(Vector3D.Forward, obs.Direction);
        }

        [Test]
        public void ContactObservation_Range_Calculation()
        {
            var obs = new ContactObservation
            {
                Position = new Vector3D(1000, 0, 0),
                HitPosition = new Vector3D(1000, 0, 0)
            };

            var distance = Vector3D.Distance(Vector3D.Zero, obs.Position);
            Assert.AreEqual(1000.0, distance);

            obs.Range = distance;
            Assert.AreEqual(1000.0, obs.Range);
        }
    }
}
