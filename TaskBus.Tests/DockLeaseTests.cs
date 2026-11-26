using System;
using SpaceEngineers.Apps.FlightControl;
using Xunit;

namespace TaskBus.Tests
{
    public sealed class DockLeaseTests
    {
        private static readonly long Second = TimeSpan.TicksPerSecond;
        private static readonly long Started = new DateTime(2026, 1, 1).Ticks;

        [Fact]
        public void OnlyACurrentDeadlineWithinThirtySecondsCanStart()
        {
            Assert.False(DockLease.CanStart(Started, Started));
            Assert.False(DockLease.CanStart(Started, Started - 1));
            Assert.True(DockLease.CanStart(Started, Started + 30 * Second));
            Assert.False(DockLease.CanStart(Started, Started + 30 * Second + 1));
        }

        [Fact]
        public void MatchingRefreshExtendsDeadlineButCannotExtendApproachLimit()
        {
            var lease = new DockLease();
            long request = Started + 10 * Second;
            lease.Start(Started, request);

            lease.Refresh(Started + 6 * Second, request, Started + 35 * Second);
            lease.Refresh(Started + 7 * Second, request, Started + 20 * Second);

            Assert.False(lease.IsExpired(Started + 29 * Second));
            Assert.True(lease.IsExpired(Started + 30 * Second));
            Assert.True(lease.Matches(request));
        }

        [Fact]
        public void LateAndUnrelatedRefreshesCannotReviveAnApproach()
        {
            var lease = new DockLease();
            long request = Started + 10 * Second;
            lease.Start(Started, request);
            lease.Refresh(Started + 2 * Second, request + 1, Started + 25 * Second);
            lease.Refresh(Started + 2 * Second, request, Started + 32 * Second + 1);
            lease.Refresh(request, request, Started + 25 * Second);

            Assert.True(lease.IsExpired(request));
            Assert.False(lease.Matches(request + 1));

            lease.Clear();
            Assert.False(lease.IsExpired(request));
            Assert.False(lease.Matches(request));
        }
    }
}
