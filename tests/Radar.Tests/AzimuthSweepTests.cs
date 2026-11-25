using System;
using NUnit.Framework;
using SpaceEngineers.Libs.RadarCore;

namespace Tests.Radar.Tests
{
    [TestFixture]
    public class AzimuthSweepTests
    {
        [Test]
        public void HoldsPositionUntilAdvancedAndReversesAtTravelLimit()
        {
            var sweep = new AzimuthSweep();
            const double upper = 0.10;
            const double lower = -0.10;
            const double step = 5 * Math.PI / 180;

            Assert.That(sweep.Goal(0, lower, upper), Is.Zero);
            sweep.Advance(0, lower, upper);
            Assert.That(sweep.Goal(0.02, lower, upper), Is.EqualTo(step));
            sweep.Advance(0.02, lower, upper);
            Assert.That(sweep.Goal(0.02, lower, upper), Is.EqualTo(step));

            sweep.Advance(step, lower, upper);
            Assert.That(sweep.Goal(step, lower, upper), Is.EqualTo(upper));
            Assert.That(sweep.Goal(upper, lower, upper), Is.EqualTo(upper));
            sweep.Advance(upper, lower, upper);
            Assert.That(sweep.Goal(upper, lower, upper), Is.EqualTo(upper - step));

            sweep.Reset();
            Assert.That(sweep.Goal(0.04, lower, upper), Is.EqualTo(0.04));
        }
    }
}
