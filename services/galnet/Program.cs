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
        private const int LaserRetryTicks = 60;
        private readonly Dictionary<string, string> _lasers = new Dictionary<string, string>();
        private const int MaxHops = 8;
        private readonly Dictionary<long, long> _peers = new Dictionary<long, long>();
        private readonly GalnetTopology _topology = new GalnetTopology();
        private long _generation;
        private long _sequence;
        private long _tick;
        private static readonly long MessageLifetime = TimeSpan.FromMinutes(5).Ticks;
        private const string ApplicationBlockName = "[GALNET APP]";
        private const int MaxApplicationLength = 1024;
        private const int MaxPending = 16;
        private const int MaxReceived = 64;
        private readonly List<string> _outbox = new List<string>();
        private readonly Queue<string> _received = new Queue<string>();
        private string _lastDelivery = "No message received.";

        public Program()
        {
            var settings = Storage.Split('\n');
            long.TryParse(settings[0], out _generation);
            _generation++;
            for (int i = 1; i < settings.Length; i++)
            {
                var line = settings[i].TrimEnd('\r');
                long peer;
                if (line.StartsWith("SEND:", StringComparison.Ordinal) && _outbox.Count < MaxPending)
                {
                    var packet = line.Substring(5);
                    if (IsCurrentPacket(packet, DateTime.UtcNow.Ticks))
                        _outbox.Add(packet);
                }
                else if (line.StartsWith("SEEN:", StringComparison.Ordinal) && _received.Count < MaxReceived)
                    _received.Enqueue(line.Substring(5));
                else if (line.StartsWith("LAST:", StringComparison.Ordinal))
                {
                    try { _lastDelivery = TaskBusIgcHelper.DecodeStorage(line.Substring(5)); }
                    catch (FormatException) { }
                }
                else if (_peers.Count < 32 && long.TryParse(line, out peer) && peer != 0 && peer != Me.EntityId)
                    _peers[peer] = -LinkLifetime;
            }
            foreach (var line in Me.CustomData.Split('\n'))
            {
                if (!line.StartsWith("Laser=", StringComparison.OrdinalIgnoreCase))
                    continue;
                int delimiter = line.IndexOf('|', 6);
                if (delimiter < 7 || _lasers.Count >= 16)
                    continue;
                var name = line.Substring(6, delimiter - 6).Trim();
                var gps = line.Substring(delimiter + 1).Trim();
                string label;
                double x;
                double y;
                double z;
                if (name.Length != 0 && TaskBusIgcHelper.TryReadOreGps(gps, out label, out x, out y, out z))
                    _lasers[name] = gps;
            }
            SaveConfiguration();
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
        }

        public void Main(string argument, UpdateType source)
        {
            bool periodic = (source & UpdateType.Update10) != 0;
            if (periodic)
                _tick++;

            if (periodic && _tick % LaserRetryTicks == 1)
                ReconnectLasers();

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
            if (periodic && _tick % AdvertiseEvery == 1)
            {
                long now = DateTime.UtcNow.Ticks;
                int expired = _outbox.RemoveAll(packet => !IsCurrentPacket(packet, now));
                if (expired > 0)
                {
                    SaveConfiguration();
                    Echo("Expired queued messages: " + expired);
                }
                foreach (var pending in _outbox)
                    ForwardMessage(pending, 0);
            }

            var command = (argument ?? "").Trim();
            if (command.StartsWith("peer ", StringComparison.OrdinalIgnoreCase))
                ChangePeer(command);
            else if (string.Equals(command, "laser status", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var configured in _lasers)
                {
                    var antenna = FindLaser(configured.Key);
                    Echo(configured.Key + ": " + (antenna == null ? "not found" : antenna.Status.ToString()));
                }
            }
            else if (command.StartsWith("send ", StringComparison.OrdinalIgnoreCase))
                QueueMessage("DATA", command.Substring(5), 255);
            else if (command.StartsWith("app ", StringComparison.OrdinalIgnoreCase))
                QueueMessage("APP", command.Substring(4), MaxApplicationLength);
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
            Echo("Pending: " + _outbox.Count + " | " + _lastDelivery);
            foreach (var peer in _peers)
                Echo(peer.Key + (IsLive(peer.Key) ? " online" : " offline"));
        }

        private IMyLaserAntenna FindLaser(string name)
        {
            var antenna = GridTerminalSystem.GetBlockWithName(name) as IMyLaserAntenna;
            return antenna != null && antenna.CubeGrid == Me.CubeGrid ? antenna : null;
        }

        private void ReconnectLasers()
        {
            foreach (var configured in _lasers)
            {
                try
                {
                    var antenna = FindLaser(configured.Key);
                    if (antenna == null || !antenna.IsWorking || antenna.Status != MyLaserAntennaStatus.Idle)
                        continue;
                    antenna.SetTargetCoords(configured.Value);
                    antenna.Connect();
                }
                catch (Exception error)
                {
                    Echo(configured.Key + ": reconnect failed. " + error.Message);
                }
            }
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
            if (!IsLive(sender) || string.IsNullOrEmpty(packet) || packet.Length > 6144)
                return;

            if (packet.StartsWith("DATA|", StringComparison.Ordinal) ||
                packet.StartsWith("APP|", StringComparison.Ordinal) ||
                packet.StartsWith("ACK|", StringComparison.Ordinal))
            {
                ForwardMessage(packet, sender);
                return;
            }

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

        private void QueueMessage(string kind, string argument, int maxLength)
        {
            int split = argument.IndexOf(' ');
            long destination;
            if (split < 1 || !long.TryParse(argument.Substring(0, split), out destination) ||
                destination == 0 || destination == Me.EntityId || argument.Length - split - 1 > maxLength || split == argument.Length - 1 ||
                _outbox.Count >= MaxPending)
            {
                Echo("Use send/app <node ID> <payload>. Max " + maxLength + " characters. Outbox holds 16 messages.");
                return;
            }

            var id = _generation + ":" + (++_sequence);
            var packet = kind + "|" + Me.EntityId + "|" + destination + "|" + id + "|" + MaxHops + "|" +
                (DateTime.UtcNow.Ticks + MessageLifetime) + "|" +
                TaskBusIgcHelper.EncodeStorage(argument.Substring(split + 1));
            _outbox.Add(packet);
            SaveConfiguration();
            ForwardMessage(packet, 0);
        }

        private void ForwardMessage(string packet, long previousHop)
        {
            var fields = packet.Split('|');
            long origin;
            long destination;
            int hops;
            long expires;
            if (fields.Length != 7 || (fields[0] != "DATA" && fields[0] != "APP" && fields[0] != "ACK") ||
                !long.TryParse(fields[5], out expires) || expires <= DateTime.UtcNow.Ticks ||
                !long.TryParse(fields[1], out origin) || origin == 0 ||
                !long.TryParse(fields[2], out destination) || destination == 0 ||
                !int.TryParse(fields[4], out hops) || hops < 1 || hops > MaxHops ||
                fields[3].Length == 0 || fields[3].Length > 48 ||
                (hops == MaxHops && previousHop != 0 && previousHop != origin))
                return;

            if (destination == Me.EntityId)
            {
                if (fields[0] != "ACK")
                    AcceptData(fields[0], origin, fields[3], expires, fields[6]);
                else
                {
                    var address = "|" + Me.EntityId + "|" + origin + "|" + fields[3] + "|";
                    if (_outbox.RemoveAll(item => item.StartsWith("DATA" + address, StringComparison.Ordinal) ||
                        item.StartsWith("APP" + address, StringComparison.Ordinal)) > 0)
                        SaveConfiguration();
                }
                return;
            }

            long nextHop;
            if (hops <= 1 || !_topology.TryGetNextHop(Me.EntityId, destination, _tick, out nextHop) ||
                nextHop == previousHop || !IsLive(nextHop))
                return;

            if (previousHop != 0)
            {
                fields[4] = (hops - 1).ToString();
                packet = string.Join("|", fields);
            }
            IGC.SendUnicastMessage(nextHop, Channel, packet);
        }

        private static bool IsCurrentPacket(string packet, long now)
        {
            var fields = packet.Split('|');
            long expires;
            return fields.Length == 7 && (fields[0] == "DATA" || fields[0] == "APP") && long.TryParse(fields[5], out expires) &&
                expires > now;
        }

        private void AcceptData(string kind, long origin, string id, long expires, string encoded)
        {
            var key = origin + ":" + id;
            if (!_received.Contains(key))
            {
                string message;
                try { message = TaskBusIgcHelper.DecodeStorage(encoded); }
                catch (FormatException) { return; }
                if (message.Length > (kind == "APP" ? MaxApplicationLength : 255) ||
                    (kind == "APP" && !DeliverApplication(origin, id, message)))
                    return;
                if (_received.Count >= MaxReceived)
                    _received.Dequeue();
                _received.Enqueue(key);
                _lastDelivery = kind == "APP" ? "Application packet from " + origin + "." :
                    "From " + origin + ": " + message;
                SaveConfiguration();
            }
            ForwardMessage("ACK|" + Me.EntityId + "|" + origin + "|" + id + "|" + MaxHops + "|" + expires + "|", 0);
        }

        private bool DeliverApplication(long origin, string id, string payload)
        {
            try
            {
                var block = GridTerminalSystem.GetBlockWithName(ApplicationBlockName) as IMyProgrammableBlock;
                return block != null && block.EntityId != Me.EntityId && block.CubeGrid == Me.CubeGrid &&
                    block.IsWorking && block.TryRun("galnet recv " + origin + " " + id + " " + payload);
            }
            catch (Exception error)
            {
                Echo("Local Galnet application unavailable: " + error.Message);
                return false;
            }
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
            var lines = new List<string> { _generation.ToString() };
            foreach (var peer in _peers.Keys) lines.Add(peer.ToString());
            foreach (var packet in _outbox) lines.Add("SEND:" + packet);
            foreach (var key in _received) lines.Add("SEEN:" + key);
            lines.Add("LAST:" + TaskBusIgcHelper.EncodeStorage(_lastDelivery));
            Storage = string.Join("\n", lines);
        }

        #region PreludeFooter
    }
}
#endregion
