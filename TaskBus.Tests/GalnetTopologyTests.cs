using SpaceEngineers.Libs.IgcCore;
using Xunit;

namespace TaskBus.Tests
{
    public sealed class GalnetTopologyTests
    {
        [Fact]
        public void ExpiredPrimaryLinkUsesReserveAndRecoversAfterNewAdvertisement()
        {
            var topology = new GalnetTopology();
            Assert.True(topology.Observe(1, 1, 1, 100, new long[] { 3, 2 }, 0));
            Assert.True(topology.Observe(2, 1, 1, 10, new long[] { 1, 4 }, 0));
            Assert.True(topology.Observe(3, 1, 1, 20, new long[] { 1, 4 }, 0));
            Assert.True(topology.Observe(4, 1, 1, 100, new long[] { 2, 3 }, 0));

            Assert.True(topology.TryGetNextHop(1, 4, 9, out var hop));
            Assert.Equal(2L, hop);
            Assert.True(topology.TryGetNextHop(1, 4, 10, out hop));
            Assert.Equal(3L, hop);
            Assert.False(topology.TryGetNextHop(1, 4, 20, out _));

            Assert.False(topology.Observe(2, 1, 1, 100, new long[] { 1, 4 }, 20));
            Assert.True(topology.Observe(2, 2, 0, 100, new long[] { 1, 4 }, 20));
            Assert.True(topology.TryGetNextHop(1, 4, 20, out hop));
            Assert.Equal(2L, hop);
        }

        [Fact]
        public void ALinkNeedsCurrentAdvertisementsFromBothEndpoints()
        {
            var topology = new GalnetTopology();
            topology.Observe(1, 1, 1, 10, new long[] { 2 }, 0);
            topology.Observe(2, 1, 1, 10, new long[] { 3 }, 0);
            Assert.False(topology.TryGetNextHop(1, 2, 1, out _));

            Assert.True(topology.Observe(2, 1, 2, 10, new long[] { 1 }, 1));
            Assert.False(topology.Observe(2, 1, 1, 20, new long[] { 3 }, 2));
            Assert.True(topology.TryGetNextHop(1, 2, 2, out var hop));
            Assert.Equal(2L, hop);
        }
    }
}
