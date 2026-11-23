using System;
using System.Globalization;
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
            if (TaskBusProtocol.TryParseEnvelope(json, out var envelope))
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
    }
}