using System;

namespace SpaceEngineers.Apps.ShipLink
{
    internal static class PilotContactSelection
    {
        public static bool TryRead(string line, out long source, out long entity)
        {
            source = entity = 0;
            if (!line.StartsWith("PILOT_CONTACT|", StringComparison.Ordinal))
                return false;
            var fields = line.Split('|');
            return fields.Length == 3 && long.TryParse(fields[1], out source) && source != 0 &&
                long.TryParse(fields[2], out entity) && entity != 0;
        }

        public static bool IsFreshFrom(long selectedSource, long source, long measured, long expires, long now) =>
            source == selectedSource && expires > now && measured > 0 && measured <= now &&
            now - measured <= 2 * TimeSpan.TicksPerSecond;
    }
}
