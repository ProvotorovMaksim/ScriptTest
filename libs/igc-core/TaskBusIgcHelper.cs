using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text;
using SpaceEngineers.Libs.IgcCore;

namespace SpaceEngineers.Libs.IgcCore
{
    /// <summary>
    /// Shared helper for Space Engineers programmable blocks that communicate via TaskBus IGC messages.
    /// Provides JSON property extraction, envelope parsing, and base64 storage encoding/decoding
    /// so that each PB program does not duplicate the same boilerplate.
    /// </summary>
    public static class TaskBusIgcHelper
    {
        /// <summary>Parses a TaskBus JSON envelope into event type and payload.</summary>
        public static bool TryParseEnvelope(string json, out string eventType, out string payloadJson)
        {
            TaskBusEnvelope envelope;
            if (TaskBusProtocol.TryParseEnvelope(json, out envelope))
            {
                eventType = envelope.EventType;
                payloadJson = envelope.PayloadJson;
                return true;
            }
            eventType = null;
            payloadJson = null;
            return false;
        }

        /// <summary>Extracts a raw JSON property value as a string.</summary>
        public static bool TryGetJsonProperty(string json, string propertyName, out string rawValue)
        {
            return TaskBusProtocol.TryGetProperty(json, propertyName, out rawValue);
        }

        /// <summary>Reads a JSON string value from a raw JSON fragment.</summary>
        public static bool TryReadJsonString(string json, out string value)
        {
            return TaskBusProtocol.TryReadString(json, out value);
        }

        /// <summary>Quotes a string for safe inclusion in JSON.</summary>
        public static string ToJsonString(string value)
        {
            return TaskBusProtocol.Quote(value ?? string.Empty);
        }

        /// <summary>Encodes a string to Base64 for PB Storage persistence.</summary>
        public static string EncodeStorage(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        /// <summary>Decodes a Base64 string from PB Storage back to UTF-8.</summary>
        public static string DecodeStorage(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        public static bool TryReadFiniteNumber(string json, string key, out double value)
        {
            value = 0;
            string raw;
            return TryGetJsonProperty(json, key, out raw) &&
                TryParseFiniteNumber(raw, out value);
        }
        /// <summary>Reads a GPS survey report from an ore detector adapter.</summary>
        public static string FindNearestOre(IEnumerable<string> observations, string ore,
            double originX, double originY, double originZ, double radius)
        {
            string best = null;
            double bestDistanceSquared = radius * radius;
            foreach (var report in observations)
            {
                string name;
                double x;
                double y;
                double z;
                if (!TryReadOreGps(report, out name, out x, out y, out z) ||
                    !string.Equals(name, ore, StringComparison.OrdinalIgnoreCase))
                    continue;

                double dx = x - originX;
                double dy = y - originY;
                double dz = z - originZ;
                double distanceSquared = dx * dx + dy * dy + dz * dz;
                if (distanceSquared < bestDistanceSquared ||
                    (distanceSquared == bestDistanceSquared && (best == null || string.CompareOrdinal(report, best) < 0)))
                {
                    best = report;
                    bestDistanceSquared = distanceSquared;
                }
            }
            return best;
        }

        public static bool TryReadOreGps(string report, out string ore, out double x, out double y, out double z)
        {
            ore = null;
            x = y = z = 0;
            if (string.IsNullOrWhiteSpace(report))
                return false;

            var fields = report.Trim().Split(':');
            double parsedX;
            double parsedY;
            double parsedZ;
            if (fields.Length < 6 || fields[0] != "GPS" || fields[fields.Length - 1] != "" ||
                string.IsNullOrWhiteSpace(fields[1]) ||
                !TryParseFiniteNumber(fields[2], out parsedX) ||
                !TryParseFiniteNumber(fields[3], out parsedY) ||
                !TryParseFiniteNumber(fields[4], out parsedZ))
                return false;

            ore = fields[1].Trim();
            x = parsedX;
            y = parsedY;
            z = parsedZ;
            return true;
        }

        private static bool TryParseFiniteNumber(string value, out double result) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            !double.IsNaN(result) && !double.IsInfinity(result);
    }
}