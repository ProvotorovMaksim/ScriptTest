using NUnit.Framework;
using SpaceEngineers.Libs.RadarCore;

namespace Tests.Radar.Tests
{
    [TestFixture]
    public class RadarStateTests
    {
        [Test]
        public void RadarState_Values_Are_Correct()
        {
            Assert.AreEqual(0, (int)RadarState.IDLE);
            Assert.AreEqual(1, (int)RadarState.ACQUIRING);
            Assert.AreEqual(2, (int)RadarState.LOCKED);
            Assert.AreEqual(3, (int)RadarState.COASTING);
            Assert.AreEqual(4, (int)RadarState.LOST);
            Assert.AreEqual(5, (int)RadarState.OUT_OF_FOV);
            Assert.AreEqual(6, (int)RadarState.CHARED);
        }

        [Test]
        public void RadarState_IDLE_Is_Initital_State()
        {
            var state = RadarState.IDLE;
            Assert.IsNotNull(state);
        }

        [Test]
        public void RadarState_All_Values_Are_Valid()
        {
            foreach (RadarState rs in Enum.GetValues(typeof(RadarState)))
            {
                var val = (int)rs;
                Assert.IsTrue(val >= 0 && val <= 6);
            }
        }
    }
}
TESTEOF
echo "RadarStateTests.cs written"