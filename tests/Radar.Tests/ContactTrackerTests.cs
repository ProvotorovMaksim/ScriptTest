using NUnit.Framework;
using VRageMath;
using SpaceEngineers.Libs.RadarCore;

namespace Tests.Radar.Tests
{
    [TestFixture]
    public class ContactTrackerTests
    {
        [Test]
        public void ContactTracker_Initial_State()
        {
            var tracker = new ContactTracker();
            Assert.AreEqual(0L, tracker.EntityId);
            Assert.AreEqual("", tracker.Name);
            Assert.IsFalse(tracker.IsActive);
        }

        [Test]
        public void ContactTracker_Update_and_Lose()
        {
            var tracker = new ContactTracker();
            var now = DateTime.UtcNow;

            tracker.Update(new MyDetectedEntityInfo
            {
                EntityId = 12345L,
                DisplayNameText = "Test Ship",
                Position = new Vector3D(1000, 0, 0),
                Velocity = Vector3D.Zero
            }, 15000.0, now);

            Assert.AreEqual(12345L, tracker.EntityId);
            Assert.AreEqual("Test Ship", tracker.Name);
            Assert.IsTrue(tracker.IsActive);
            Assert.IsFalse(string.IsNullOrEmpty(tracker.Name));
            Assert.IsTrue(tracker.AgeSeconds >= 0);

            // Lose the contact
            tracker.Lose();

            Assert.AreEqual(0L, tracker.EntityId);
            Assert.AreEqual("", tracker.Name);
            Assert.IsFalse(tracker.IsActive);
        }

        [Test]
        public void ContactTracker_Age_Calculation()
        {
            var tracker = new ContactTracker();
            var now = DateTime.UtcNow;

            tracker.Update(new MyDetectedEntityInfo
            {
                EntityId = 12345L,
                DisplayNameText = "Test",
                Position = Vector3D.Zero,
                Velocity = Vector3D.Zero
            }, 10000.0, now);

            // Age should be 0 shortly after update
            Assert.IsTrue(tracker.AgeSeconds >= 0 && tracker.AgeSeconds < 10);
        }
    }
}
TESTEOF
echo "ContactTrackerTests.cs written"