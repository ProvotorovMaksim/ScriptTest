using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Libs.RadarCore;

namespace Tests.Radar.Tests
{
    [TestFixture]
    public class CameraManagerTests
    {
        [Test]
        public void EmptyManagerHasNoCamera()
        {
            var manager = new CameraManager(new RadarConfig());
            Assert.That(manager.Count(), Is.Zero);
            Assert.That(manager.GetNext(), Is.Null);
        }
    }
}
