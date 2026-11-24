using System.Collections.Generic;
using System.Linq;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Libs.IgcCore;
using Xunit;
using Relay = SpaceEngineers.Services.Galnet.Program;

namespace TaskBus.Tests
{
    public sealed class GalnetRelayTests
    {
        [Fact]
        public void DeliveryUsesReserveAfterRestartAndRetriesLostAck()
        {
            var nodes = new Dictionary<long, Relay>
            {
                { 1, Start(1, "0\n2\n3") },
                { 2, Start(2, "0\n1\n4") },
                { 3, Start(3, "0\n1\n4") },
                { 4, Start(4, "0\n2\n3") }
            };
            var offline = new HashSet<long>();
            bool dropAck = true;
            int dataFromReserve = 0;

            void Advance(int ticks)
            {
                for (int tick = 0; tick < ticks; tick++)
                {
                    foreach (var node in nodes.Where(pair => !offline.Contains(pair.Key)))
                    {
                        node.Value.Output.Clear();
                        node.Value.Main("", UpdateType.Update10);
                    }
                    foreach (var node in nodes)
                    {
                        foreach (var packet in node.Value.IGC.UnicastOutbox)
                        {
                            if (offline.Contains(node.Key) || offline.Contains(packet.Target) ||
                                (dropAck && packet.Data.StartsWith("ACK|")))
                                continue;
                            Assert.True((node.Key is 1 or 4) != (packet.Target is 1 or 4));
                            if (node.Key == 3 && packet.Data.StartsWith("DATA|"))
                                dataFromReserve++;
                            nodes[packet.Target].IGC.UnicastListener.Inbox.Enqueue(
                                (node.Key, packet.Tag, packet.Data));
                        }
                        node.Value.IGC.UnicastOutbox.Clear();
                    }
                }
            }

            Advance(40);
            nodes[1].Main("route 4", UpdateType.Terminal);
            Assert.Contains("Next hop: 2", nodes[1].Output);
            offline.Add(2);
            offline.Add(3);
            nodes[1].Main("send 4 payload", UpdateType.Terminal);
            nodes[1] = Start(1, nodes[1].Storage);
            Advance(70);
            Assert.Contains("SEND:", nodes[1].Storage);
            Assert.DoesNotContain("SEEN:", nodes[4].Storage);

            offline.Remove(3);
            Advance(50);
            Assert.Contains("SEND:", nodes[1].Storage);
            Assert.Single(nodes[4].Storage.Split('\n').Where(line => line.StartsWith("SEEN:")));
            Assert.True(dataFromReserve > 0);

            nodes[4] = Start(4, nodes[4].Storage);
            dropAck = false;
            Advance(60);
            Assert.DoesNotContain("SEND:", nodes[1].Storage);
            Assert.Single(nodes[4].Storage.Split('\n').Where(line => line.StartsWith("SEEN:")));
            Assert.Contains("LAST:" + TaskBusIgcHelper.EncodeStorage("From 1: payload"), nodes[4].Storage);
        }

        private static Relay Start(long id, string storage)
        {
            MyGridProgram.EntityIdAtStartup = id;
            MyGridProgram.StorageAtStartup = storage;
            try { return new Relay(); }
            finally
            {
                MyGridProgram.EntityIdAtStartup = 0;
                MyGridProgram.StorageAtStartup = null;
            }
        }
    }
}
