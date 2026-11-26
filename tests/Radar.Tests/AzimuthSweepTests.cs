using System;
using NUnit.Framework;
using SpaceEngineers.Libs.RadarCore;

namespace Tests.Radar.Tests
{
    [TestFixture]
    public class AzimuthSweepTests
    {
        [TestCase(181, 0, 270, 181)]
        [TestCase(-179, 0, 270, 181)]
        [TestCase(179, -270, 0, -181)]
        [TestCase(355, -90, 90, -5)]
        [TestCase(179, -180, 180, 179)]
        public void MotorAngleUsesTheConfiguredTravelFrame(
            double measured, double lower, double upper, double expected)
        {
            const double radians = Math.PI / 180;
            Assert.That(GimbalAngles.InTravelFrame(measured * radians, lower * radians, upper * radians),
                Is.EqualTo(expected * radians).Within(1e-9));
        }

        [Test]
        public void AxisMonitorDetectsAStallAndResetsForTheNextPosition()
        {
            var progress = new GimbalAxisProgress();
            var start = new DateTime(2026, 1, 1);
            Assert.That(progress.IsStalled(0.3, start, 0.001), Is.False);
            Assert.That(progress.IsStalled(0.29, start.AddSeconds(4), 0.001), Is.False);
            Assert.That(progress.IsStalled(0.29, start.AddSeconds(8), 0.001), Is.False);
            Assert.That(progress.IsStalled(0.29, start.AddSeconds(9), 0.001), Is.True);
            Assert.That(progress.IsStalled(0.001, start.AddSeconds(10), 0.001), Is.False);
            Assert.That(progress.IsStalled(0.03, start.AddSeconds(11), 0.001), Is.False);
        }

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
