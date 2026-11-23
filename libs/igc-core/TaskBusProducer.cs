using System;

namespace SpaceEngineers.Libs.IgcCore
{
    /// <summary>
    /// Callback used by <see cref="TaskBusProducer"/> to send an IGC message.
    /// </summary>
    /// <param name="channel">IGC communication channel/tag.</param>
    /// <param name="envelope">JSON-encoded TaskBus envelope.</param>
    /// <param name="targetId">Target entity ID (0 = broadcast).</param>
    public delegate void TaskBusSend(string channel, string envelope, long targetId);

    /// <summary>
    /// Reusable transport-facing producer API for publishing TaskBus events over IGC.
    /// </summary>
    public sealed class TaskBusProducer
    {
        private readonly string _channel;
        private readonly TaskBusSend _send;

        /// <summary>
        /// Creates a producer bound to a specific IGC channel.
        /// </summary>
        /// <param name="channel">IGC communication channel/tag (must not be null or whitespace).</param>
        /// <param name="send">Callback used to transmit the envelope over IGC.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="channel"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="send"/> is null.</exception>
        public TaskBusProducer(string channel, TaskBusSend send)
        {
            if (string.IsNullOrWhiteSpace(channel))
                throw new ArgumentException("A channel is required.", nameof(channel));
            if (send == null)
                throw new ArgumentNullException(nameof(send));
            _channel = channel;
            _send = send;
        }

        /// <summary>
        /// Publishes an event by creating a JSON envelope and sending it over IGC.
        /// </summary>
        /// <param name="eventType">Event type name (e.g. TASK_PUBLISH, TASK_ASSIGN).</param>
        /// <param name="payloadJson">JSON payload string, or null.</param>
        /// <param name="messageId">Optional unique message ID for deduplication.</param>
        /// <param name="targetId">Optional target entity ID (0 = broadcast).</param>
        /// <returns>The JSON-encoded envelope that was sent.</returns>
        public string Publish(string eventType, string payloadJson, string messageId = null, long targetId = 0)
        {
            var envelope = TaskBusProtocol.CreateEnvelope(eventType, payloadJson, messageId, targetId);
            _send(_channel, envelope, targetId);
            return envelope;
        }
    }
}
