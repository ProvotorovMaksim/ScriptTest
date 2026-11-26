using System;

namespace SpaceEngineers.Apps.FlightControl
{
    internal static class ThrustSafety
    {
        public static bool CanSupport(double mass, double x, double y, double z, double[] capacity, double reserve)
        {
            return mass > 0 && !double.IsInfinity(mass) &&
                AxisCanSupport(x, capacity[0], capacity[1], reserve) &&
                AxisCanSupport(y, capacity[2], capacity[3], reserve) &&
                AxisCanSupport(z, capacity[4], capacity[5], reserve);
        }

        private static bool AxisCanSupport(double force, double positive, double negative, double reserve)
        {
            return positive >= 0 && !double.IsInfinity(positive) &&
                negative >= 0 && !double.IsInfinity(negative) &&
                Math.Abs(force) <= (force < 0 ? negative : positive) * (1 - reserve);
        }
    }
}
