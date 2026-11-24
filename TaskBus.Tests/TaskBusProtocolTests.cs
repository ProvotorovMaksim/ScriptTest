using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;
using SpaceEngineers.Libs.IgcCore;

namespace TaskBus.Tests
{
    public class TaskBusProtocolTests
    {
        [Theory]
        [InlineData("GPS:Iron:1234.5:-6789:0:#FF00FF00:")]
        [InlineData("GPS:Iron:1234.5:-6789:0:\r")]
        public void OreGpsParsesSurveyCoordinates(string row)
        {
            Assert.True(TaskBusIgcHelper.TryReadOreGps(row, out var ore, out var x, out var y, out var z));
            Assert.Equal("Iron", ore);
            Assert.Equal(1234.5, x);
            Assert.Equal(-6789, y);
            Assert.Equal(0, z);
        }

        [Theory]
        [InlineData("GPS:Iron:NaN:0:2:")]
        [InlineData("GPS::1:0:2:")]
        [InlineData("GPS:Iron:1:0:2")]
        public void OreGpsRejectsMalformedRows(string row)
        {
            Assert.False(TaskBusIgcHelper.TryReadOreGps(row, out _, out _, out _, out _));
        }

        [Fact]
        public void CreateEnvelope_Basic_ReturnsValidJson()
        {
            var json = TaskBusProtocol.CreateEnvelope("TASK_PUBLISH", "{\"request_id\":\"abc\"}");
            Assert.Contains("\"protocol\":1", json);
            Assert.Contains("\"event_type\":\"TASK_PUBLISH\"", json);
            Assert.Contains("\"payload\":{\"request_id\":\"abc\"}", json);
        }

        [Fact]
        public void CreateEnvelope_WithMessageIdAndTargetId_IncludesBoth()
        {
            var json = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN", "null", "msg-123", 42);
            Assert.Contains("\"message_id\":\"msg-123\"", json);
            Assert.Contains("\"target_id\":42", json);
        }

        [Fact]
        public void CreateEnvelope_NullMessageId_OmitsField()
        {
            var json = TaskBusProtocol.CreateEnvelope("EVENT", "null", null, 0);
            Assert.DoesNotContain("\"message_id\"", json);
            Assert.DoesNotContain("\"target_id\"", json);
        }

        [Fact]
        public void TryParseEnvelope_Valid_ReturnsTrue()
        {
            var json = TaskBusProtocol.CreateEnvelope("TASK_POLL", "{\"group_id\":\"default\"}");
            Assert.True(TaskBusProtocol.TryParseEnvelope(json, out var envelope));
            Assert.Equal("TASK_POLL", envelope.EventType);
            Assert.Equal("{\"group_id\":\"default\"}", envelope.PayloadJson);
        }

        [Fact]
        public void TryParseEnvelope_WithMessageId_ParsesIt()
        {
            var json = TaskBusProtocol.CreateEnvelope("TASK_DONE", "null", "done-msg-7", 0);
            Assert.True(TaskBusProtocol.TryParseEnvelope(json, out var envelope));
            Assert.Equal("done-msg-7", envelope.MessageId);
        }

        [Fact]
        public void TryParseEnvelope_InvalidJson_ReturnsFalse()
        {
            Assert.False(TaskBusProtocol.TryParseEnvelope("not json", out _));
            Assert.False(TaskBusProtocol.TryParseEnvelope("", out _));
            Assert.False(TaskBusProtocol.TryParseEnvelope(null, out _));
        }

        [Fact]
        public void TryParseEnvelope_MissingEventType_ReturnsFalse()
        {
            Assert.False(TaskBusProtocol.TryParseEnvelope("{\"payload\":null}", out _));
        }

        [Fact]
        public void TryParseEnvelope_MissingPayload_ReturnsFalse()
        {
            Assert.False(TaskBusProtocol.TryParseEnvelope("{\"event_type\":\"X\"}", out _));
        }

        [Fact]
        public void Quote_SpecialCharacters_EscapesCorrectly()
        {
            Assert.Equal("\"hello\\nworld\"", TaskBusProtocol.Quote("hello\nworld"));
            Assert.Equal("\"say \\\"hi\\\"\"", TaskBusProtocol.Quote("say \"hi\""));
            Assert.Equal("\"\\\\\"", TaskBusProtocol.Quote(@"\"));
            Assert.Equal("\"\\u0001\"", TaskBusProtocol.Quote("\u0001"));
        }

        [Fact]
        public void Quote_Null_ReturnsEmptyQuotedString()
        {
            Assert.Equal("\"\"", TaskBusProtocol.Quote(null));
        }

        [Fact]
        public void TryGetProperty_ExistingProperty_ReturnsRawValue()
        {
            Assert.True(TaskBusProtocol.TryGetProperty("{\"a\":1,\"b\":2}", "b", out var raw));
            Assert.Equal("2", raw);
        }

        [Fact]
        public void TryGetProperty_MissingProperty_ReturnsFalse()
        {
            Assert.False(TaskBusProtocol.TryGetProperty("{\"a\":1}", "c", out _));
        }

        [Fact]
        public void TryReadString_QuotedString_ReturnsValue()
        {
            Assert.True(TaskBusProtocol.TryReadString("\"hello\"", out var value));
            Assert.Equal("hello", value);
        }

        [Fact]
        public void TryReadString_Unquoted_ReturnsFalse()
        {
            Assert.False(TaskBusProtocol.TryReadString("hello", out _));
        }

        [Fact]
        public void TryReadStringArray_ValidArray_ReturnsItems()
        {
            Assert.True(TaskBusProtocol.TryReadStringArray("[\"survey\",\"mine\",\"deliver\"]", out var items));
            Assert.Equal(new List<string> { "survey", "mine", "deliver" }, items);
        }

        [Fact]
        public void TryReadStringArray_EmptyArray_ReturnsEmptyList()
        {
            Assert.True(TaskBusProtocol.TryReadStringArray("[]", out var items));
            Assert.Empty(items);
        }

        [Fact]
        public void TryReadStringArray_Invalid_ReturnsFalse()
        {
            Assert.False(TaskBusProtocol.TryReadStringArray("not array", out _));
            Assert.False(TaskBusProtocol.TryReadStringArray("", out _));
        }

        [Fact]
        public void CreateEnvelope_PayloadWithQuotes_EscapesCorrectly()
        {
            var payload = "{\"key\":\"value with \\\"quotes\\\"\"}";
            var json = TaskBusProtocol.CreateEnvelope("TEST", payload);
            Assert.True(TaskBusProtocol.TryParseEnvelope(json, out var envelope));
            Assert.Equal(payload, envelope.PayloadJson);
        }
    }
}