#region Prelude
using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Libs.IgcCore;

namespace SpaceEngineers.Services.Galnet
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string Channel = "SE1-GALNET-V1";
        private const int AdvertiseEvery = 12;
        private const int LinkLifetime = 48;
        private const int AdvertisementLifetime = 72;
        private const int MaxHops = 8;
        private readonly Dictionary<long, long> _peers = new Dictionary<long, long>();
        private readonly GalnetTopology _topology = new GalnetTopology();
        private long _generation;
        private long _sequence;
        private long _tick;

        public Program()
        {
            var settings = Storage.Split('\n');
            long.TryParse(settings[0], out _generation);
            _generation++;
            for (int i = 1; i < settings.Length && _peers.Count < 32; i++)
            {
                long peer;
                if (long.TryParse(settings[i], out peer) && peer != 0 && peer != Me.EntityId)
                    _peers[peer] = -LinkLifetime;
            }
            SaveConfiguration();
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
        }

        public void Main(string argument, UpdateType source)
        {
            bool periodic = (source & UpdateType.Update10) != 0;
            if (periodic)
                _tick++;

            var listener = IGC.UnicastListener;
            for (int processed = 0; processed < 32 && listener.HasPendingMessage; processed++)
            {
                var message = listener.AcceptMessage();
                if (message.Tag == Channel && _peers.ContainsKey(message.Source))
                    Receive(message.Source, message.Data as string);
            }

            if (periodic && (_tick % AdvertiseEvery == 1 ||
                _peers.Values.Any(seen => _tick - seen == LinkLifetime)))
                Advertise();

            var command = (argument ?? "").Trim();
            if (command.StartsWith("peer ", StringComparison.OrdinalIgnoreCase))
                ChangePeer(command);
            else if (command.StartsWith("route ", StringComparison.OrdinalIgnoreCase))
            {
                long destination;
                long hop;
                if (long.TryParse(command.Substring(6), out destination) &&
                    _topology.TryGetNextHop(Me.EntityId, destination, _tick, out hop) && IsLive(hop))
                    Echo("Next hop: " + hop);
                else
                    Echo("No current route.");
            }

            Echo("Galnet node: " + Me.EntityId);
            foreach (var peer in _peers)
                Echo(peer.Key + (IsLive(peer.Key) ? " online" : " offline"));
        }

        private bool IsLive(long peer)
        {
            long seen;
            return _peers.TryGetValue(peer, out seen) && _tick - seen < LinkLifetime;
        }

        private void Advertise()
        {
            var neighbors = new List<long>();
            foreach (var peer in _peers.Keys)
            {
                IGC.SendUnicastMessage(peer, Channel, "HELLO");
                if (IsLive(peer))
                    neighbors.Add(peer);
            }
            if (_topology.Observe(Me.EntityId, _generation, ++_sequence, _tick + LinkLifetime, neighbors, _tick))
                Flood(Me.EntityId, _generation, _sequence, MaxHops, neighbors, 0);
        }

        private void Receive(long sender, string packet)
        {
            if (packet == "HELLO")
            {
                _peers[sender] = _tick;
                return;
            }
            if (!IsLive(sender) || string.IsNullOrEmpty(packet) || packet.Length > 1024)
                return;

            var fields = packet.Split('|');
            long owner;
            long generation;
            long sequence;
            int remaining;
            if (fields.Length != 6 || fields[0] != "LINK" ||
                !long.TryParse(fields[1], out owner) || owner == Me.EntityId ||
                !long.TryParse(fields[2], out generation) || !long.TryParse(fields[3], out sequence) ||
                !int.TryParse(fields[4], out remaining) || remaining < 1 || remaining > MaxHops ||
                (remaining == MaxHops && owner != sender))
                return;

            var neighbors = new List<long>();
            if (fields[5].Length != 0)
            {
                var ids = fields[5].Split(',');
                if (ids.Length > 32)
                    return;
                foreach (var id in ids)
                {
                    long neighbor;
                    if (!long.TryParse(id, out neighbor))
                        return;
                    neighbors.Add(neighbor);
                }
            }
            if (_topology.Observe(owner, generation, sequence, _tick + AdvertisementLifetime, neighbors, _tick) &&
                remaining > 1)
                Flood(owner, generation, sequence, remaining - 1, neighbors, sender);
        }

        private void Flood(long owner, long generation, long sequence, int remaining,
            List<long> neighbors, long exclude)
        {
            var packet = "LINK|" + owner + "|" + generation + "|" + sequence + "|" + remaining +
                "|" + string.Join(",", neighbors);
            foreach (var peer in _peers.Keys)
                if (peer != exclude && IsLive(peer))
                    IGC.SendUnicastMessage(peer, Channel, packet);
        }

        private void ChangePeer(string command)
        {
            var parts = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            long peer;
            if (parts.Length != 3 || !long.TryParse(parts[2], out peer) || peer == 0 || peer == Me.EntityId)
            {
                Echo("Use peer add <ID> or peer remove <ID>.");
                return;
            }
            if (parts[1] == "add" && !_peers.ContainsKey(peer) && _peers.Count < 32)
                _peers.Add(peer, -LinkLifetime);
            else if (parts[1] != "remove" || !_peers.Remove(peer))
                return;

            SaveConfiguration();
            Advertise();
        }

        private void SaveConfiguration()
        {
            Storage = _generation + "\n" + string.Join("\n", _peers.Keys);
        }

        #region PreludeFooter
    }
}
#endregion
