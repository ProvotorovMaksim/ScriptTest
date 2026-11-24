using System;
using System.Globalization;
using System.Text;

namespace SpaceEngineers.Libs.IgcCore
{
    /// <summary>
    /// Represents a parsed TaskBus JSON envelope exchanged over IGC between programmable blocks.
    /// </summary>
    public sealed class TaskBusEnvelope
    {
        /// <summary>The type of the event/message (e.g. TASK_PUBLISH, TASK_ASSIGN, TASK_DONE).</summary>
        public string EventType;
        /// <summary>The JSON payload string, or null if absent.</summary>
        public string PayloadJson;
        /// <summary>Optional unique message identifier for deduplication.</summary>
        public string MessageId;
        /// <summary>Optional target entity ID for directed messages.</summary>
        public long TargetId;
    }

    /// <summary>
    /// Provides encoding and decoding for the versioned TaskBus JSON envelope protocol
    /// used for all IGC communication between broker, consumers, and producers.
    /// </summary>
    public static class TaskBusProtocol
    {
        /// <summary>
        /// Creates a compact JSON envelope string with protocol version 1.
        /// </summary>
        /// <param name="eventType">Event type name.</param>
        /// <param name="payloadJson">JSON payload string, or null.</param>
        /// <param name="messageId">Optional unique message ID.</param>
        /// <param name="targetId">Optional target entity ID (0 = broadcast).</param>
        /// <returns>A JSON-encoded envelope string.</returns>
        public static string CreateEnvelope(string eventType, string payloadJson, string messageId = null, long targetId = 0)
        {
            var json = new StringBuilder();
            json.Append("{\"protocol\":1,\"event_type\":").Append(Quote(eventType));
            json.Append(",\"payload\":").Append(string.IsNullOrEmpty(payloadJson) ? "null" : payloadJson);
            if (!string.IsNullOrEmpty(messageId))
                json.Append(",\"message_id\":").Append(Quote(messageId));
            if (targetId != 0)
                json.Append(",\"target_id\":").Append(targetId.ToString(CultureInfo.InvariantCulture));
            json.Append('}');
            return json.ToString();
        }

        public static bool TryParseEnvelope(string json, out TaskBusEnvelope envelope)
        {
            envelope = null;
            string eventTypeJson;
            string payloadJson;
            string eventType;
            if (!TryGetProperty(json, "event_type", out eventTypeJson) ||
                !TryReadString(eventTypeJson, out eventType) ||
                !TryGetProperty(json, "payload", out payloadJson))
                return false;

            var result = new TaskBusEnvelope
            {
                EventType = eventType,
                PayloadJson = payloadJson
            };
            string messageIdJson;
            string messageId;
            if (TryGetProperty(json, "message_id", out messageIdJson) && TryReadString(messageIdJson, out messageId))
                result.MessageId = messageId;
            string targetIdJson;
            long targetId;
            if (TryGetProperty(json, "target_id", out targetIdJson) &&
                long.TryParse(targetIdJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out targetId))
                result.TargetId = targetId;

            envelope = result;
            return true;
        }

        public static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            foreach (var character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                            result.Append("\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            result.Append(character);
                        break;
                }
            }
            result.Append('"');
            return result.ToString();
        }

        public static bool TryGetProperty(string json, string propertyName, out string rawValue)
        {
            rawValue = null;
            if (string.IsNullOrEmpty(json))
                return false;
            var index = 0;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index++] != '{')
                return false;

            while (index < json.Length)
            {
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == '}')
                    return false;
                string key;
                if (!TryReadString(json, ref index, out key))
                    return false;
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index++] != ':')
                    return false;
                SkipWhitespace(json, ref index);
                var valueStart = index;
                if (!SkipValue(json, ref index))
                    return false;
                if (key == propertyName)
                {
                    rawValue = json.Substring(valueStart, index - valueStart).Trim();
                    return true;
                }
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index++] != ',')
                    return false;
            }
            return false;
        }

        public static bool TryReadString(string json, out string value)
        {
            var index = 0;
            return TryReadString(json, ref index, out value) && index == json.Length;
        }

        public static bool TryReadStringArray(string json, out System.Collections.Generic.List<string> values)
        {
            values = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(json))
                return false;
            var index = 0;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index++] != '[')
                return false;
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == ']')
                return ++index == json.Length;

            while (index < json.Length)
            {
                SkipWhitespace(json, ref index);
                string item;
                if (!TryReadString(json, ref index, out item))
                    return false;
                values.Add(item);
                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                    return false;
                if (json[index++] == ']')
                    return index == json.Length;
                if (json[index - 1] != ',')
                    return false;
            }
            return false;
        }

        private static bool TryReadString(string json, ref int index, out string value)
        {
            value = null;
            if (index >= json.Length || json[index++] != '"')
                return false;
            var result = new StringBuilder();
            while (index < json.Length)
            {
                var character = json[index++];
                if (character == '"')
                {
                    value = result.ToString();
                    return true;
                }
                if (character < 0x20)
                    return false;
                if (character != '\\')
                {
                    result.Append(character);
                    continue;
                }
                if (index >= json.Length)
                    return false;
                character = json[index++];
                switch (character)
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        int codePoint;
                        if (index + 4 > json.Length || !int.TryParse(json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codePoint))
                            return false;
                        result.Append((char)codePoint);
                        index += 4;
                        break;
                    default: return false;
                }
            }
            return false;
        }

        private static bool SkipValue(string json, ref int index)
        {
            if (index >= json.Length)
                return false;
            if (json[index] == '"')
            {
                string ignored;
                return TryReadString(json, ref index, out ignored);
            }
            if (json[index] == '{' || json[index] == '[')
            {
                var depth = 0;
                var inString = false;
                var escaped = false;
                while (index < json.Length)
                {
                    var character = json[index++];
                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (character == '\\') escaped = true;
                        else if (character == '"') inString = false;
                    }
                    else if (character == '"') inString = true;
                    else if (character == '{' || character == '[') depth++;
                    else if (character == '}' || character == ']')
                    {
                        depth--;
                        if (depth == 0) return true;
                    }
                }
                return false;
            }
            var start = index;
            while (index < json.Length && json[index] != ',' && json[index] != '}')
                index++;
            while (index > start && char.IsWhiteSpace(json[index - 1]))
                index--;
            return index > start;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
                index++;
        }
    }
}
