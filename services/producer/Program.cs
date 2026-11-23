#region Prelude
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using System.Globalization;
using SpaceEngineers.Libs.TaskBusProducer;

namespace SpaceEngineers.Services.Producer
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string CommunicationTag = "SE1-SHIP-LINK";

        private readonly TaskBusProducer.TaskBusProducer _producer;
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();
        private int _sequence;

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update1;

            _producer = new TaskBusProducer.TaskBusProducer(CommunicationTag,
                (channel, envelope, targetId) =>
                    IGC.SendBroadcastMessage(channel, envelope, targetId, TransmissionDistance.TransmissionDistanceMax));
        }

        public void Main(string argument)
        {
            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas, antenna => antenna.IsFunctional && antenna.Enabled);
            if (_antennas.Count == 0)
            {
                Echo("Enable a functional radio antenna to publish tasks.");
                return;
            }

            var cmd = (argument ?? string.Empty).Trim();
            if (cmd == "status")
            {
                ShowStatus();
                return;
            }

            if (!string.IsNullOrEmpty(cmd))
            {
                PublishTask(cmd);
                return;
            }

            Echo("Producer ready. Run 'publish <json>' to send a task, or 'status'.");
        }

        private void PublishTask(string requestJson)
        {
            var requestId = "req-" + (++_sequence).ToString(CultureInfo.InvariantCulture);
            var payload = "{\"request_id\":" + Quote(requestId) + ",\"payload\":" + requestJson + "}";
            var envelope = _producer.Publish("TASK_PUBLISH", payload, requestId);
            Echo("Published " + requestId + ": " + envelope);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private void ShowStatus()
        {
            Echo("Producer: tag=" + CommunicationTag);
            Echo("Messages published: " + _sequence);
        }

        #region PreludeFooter
    }
}
#endregion