using SpaceEngineers.Apps.FlightControl;
using Xunit;

namespace TaskBus.Tests
{
    public sealed class ThrustSafetyTests
    {
        [Theory]
        [InlineData(0, 0, 0, true)]
        [InlineData(0, 900, 0, true)]
        [InlineData(0, 901, 0, false)]
        [InlineData(0, -900, 0, false)]
        [InlineData(0, -450, 0, true)]
        [InlineData(90, 0, -180, true)]
        [InlineData(91, 0, -180, false)]
        [InlineData(double.NaN, 0, 0, false)]
        [InlineData(double.PositiveInfinity, 0, 0, false)]
        public void ChecksDirectionalEffectiveThrustWithReserve(double x, double y, double z, bool expected)
        {
            var capacity = new double[] { 100, 0, 1000, 500, 0, 200 };
            Assert.Equal(expected, ThrustSafety.CanSupport(100, x, y, z, capacity, 0.1));
        }

        [Fact]
        public void RejectsInvalidMassAndCapacity()
        {
            var capacity = new double[6];
            Assert.False(ThrustSafety.CanSupport(0, 0, 0, 0, capacity, 0.1));
            Assert.False(ThrustSafety.CanSupport(double.NaN, 0, 0, 0, capacity, 0.1));
            capacity[0] = double.PositiveInfinity;
            Assert.False(ThrustSafety.CanSupport(100, 0, 0, 0, capacity, 0.1));
        }
    }
}
