using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Libs.IgcCore;

namespace SpaceEngineers.Libs.TaskBusConsumer
{
    public sealed class GalnetWorkerBridge
    {
        private const string Setting = "GalnetBrokerRelay=";
        public const string RelayName = "[GALNET RELAY]";
        private readonly Queue<string> _outbox = new Queue<string>();
        private readonly IMyProgrammableBlock _local;
        private readonly Func<IMyProgrammableBlock> _findRelay;
        private readonly Action<string, long> _sendDirect;
        private readonly Action<string> _echo;
        private readonly long _brokerRelayId;

        public bool IsConfigured { get; }
        public bool IsAvailable => _brokerRelayId != 0 && FindRelay() != null;

        public GalnetWorkerBridge(IMyProgrammableBlock local, string settings,
            Func<IMyProgrammableBlock> findRelay, Action<string, long> sendDirect, Action<string> echo)
        {
            _local = local;
            _findRelay = findRelay;
            _sendDirect = sendDirect;
            _echo = echo;
            foreach (var line in (settings ?? "").Split('\n'))
            {
                if (!line.StartsWith(Setting, StringComparison.OrdinalIgnoreCase))
                    continue;
                IsConfigured = true;
                long.TryParse(line.Substring(Setting.Length), out _brokerRelayId);
                break;
            }
        }

        public void Send(string envelope, long targetId)
        {
            if (!IsConfigured)
            {
                _sendDirect(envelope, targetId);
                return;
            }
            if (_brokerRelayId == 0 || targetId == 0)
            {
                _echo("Configure GalnetBrokerRelay and broker <TaskBroker PB ID>.");
                return;
            }

            var command = "app " + _brokerRelayId + " taskbus " + _local.EntityId + " " + envelope;
            if (_outbox.Count < 16 && !_outbox.Contains(command))
                _outbox.Enqueue(command);
        }

        public bool TryReceive(string command, Action<long, string> receive)
        {
            if (!command.StartsWith("galnet recv ", StringComparison.Ordinal))
                return false;
            if (!IsAvailable)
                return true;

            var fields = command.Split(new[] { ' ' }, 7, StringSplitOptions.RemoveEmptyEntries);
            long origin;
            long broker;
            TaskBusEnvelope envelope;
            if (fields.Length == 7 && fields[4] == "taskbus" &&
                long.TryParse(fields[2], out origin) && origin == _brokerRelayId &&
                long.TryParse(fields[5], out broker) && broker != 0 &&
                TaskBusProtocol.TryParseEnvelope(fields[6], out envelope) && envelope.TargetId == _local.EntityId)
                receive(broker, fields[6]);
            return true;
        }

        public void Flush()
        {
            if (_outbox.Count == 0)
                return;
            var relay = FindRelay();
            if (relay != null && relay.TryRun(_outbox.Peek()))
                _outbox.Dequeue();
        }

        private IMyProgrammableBlock FindRelay()
        {
            var relay = _findRelay();
            return relay != null && relay.EntityId != _local.EntityId && relay.CubeGrid == _local.CubeGrid &&
                relay.IsWorking ? relay : null;
        }
    }
}
