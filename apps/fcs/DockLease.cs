using System;

namespace SpaceEngineers.Apps.FlightControl
{
    // PB BODY START
    internal sealed class DockLease
    {
        private static readonly long ApproachLimitTicks = TimeSpan.FromSeconds(30).Ticks;
        private long requestId;
        private long deadlineUtc;
        private long limitUtc;

        public static bool CanStart(long now, long deadline) =>
            deadline > now && deadline <= now + ApproachLimitTicks;

        public void Start(long now, long deadline)
        {
            requestId = deadline;
            deadlineUtc = deadline;
            limitUtc = now + ApproachLimitTicks;
        }

        public bool Matches(long id) => requestId != 0 && requestId == id;

        public bool IsExpired(long now) => requestId != 0 && now >= deadlineUtc;

        public void Refresh(long now, long id, long deadline)
        {
            if (Matches(id) && !IsExpired(now) && deadline > deadlineUtc && CanStart(now, deadline))
                deadlineUtc = Math.Min(deadline, limitUtc);
        }

        public void Clear()
        {
            requestId = 0;
            deadlineUtc = 0;
            limitUtc = 0;
        }
    }
    // PB BODY END
}
