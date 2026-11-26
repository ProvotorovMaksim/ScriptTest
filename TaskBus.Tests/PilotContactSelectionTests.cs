using System;
using SpaceEngineers.Apps.ShipLink;
using Xunit;

namespace TaskBus.Tests
{
    public sealed class PilotContactSelectionTests
    {
        [Theory]
        [InlineData("WEAPON_PROFILE|old", false)]
        [InlineData("PILOT_CONTACT|42", false)]
        [InlineData("PILOT_CONTACT|42|0", false)]
        [InlineData("PILOT_CONTACT|bad|17", false)]
        [InlineData("PILOT_CONTACT|42|17", true)]
        public void ReadsOnlyValidContactRecords(string record, bool expected)
        {
            Assert.Equal(expected, PilotContactSelection.TryRead(record, out long source, out long entity));
            if (expected)
            {
                Assert.Equal(42, source);
                Assert.Equal(17, entity);
            }
        }

        [Fact]
        public void RequiresFreshObservationFromTheSavedSource()
        {
            long now = TimeSpan.FromSeconds(10).Ticks;
            Assert.True(PilotContactSelection.IsFreshFrom(42, 42, now, now + 1, now));
            Assert.False(PilotContactSelection.IsFreshFrom(42, 41, now, now + 1, now));
            Assert.False(PilotContactSelection.IsFreshFrom(42, 42, now, now, now));
            Assert.False(PilotContactSelection.IsFreshFrom(42, 42, now - TimeSpan.FromSeconds(3).Ticks, now + 1, now));
            Assert.False(PilotContactSelection.IsFreshFrom(42, 42, now + 1, now + 2, now));
        }
    }
}
