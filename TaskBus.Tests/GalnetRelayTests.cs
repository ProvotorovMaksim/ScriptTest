using System;
using System.Collections.Generic;
using SpaceEngineers.Libs.TaskBusConsumer;
using System.Linq;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Libs.IgcCore;
using Xunit;
using Broker = SpaceEngineers.Services.Broker.Program;
using Relay = SpaceEngineers.Services.Galnet.Program;

namespace TaskBus.Tests
{
    public sealed class GalnetRelayTests
    {
        [Fact]
        public void SharedRelayDeliversLoopbackTaskBusOnlyToTheAddressedApplication()
        {
            MyGridProgram.CustomDataAtStartup = "App=42\nApp=43";
            Relay relay;
            try { relay = Start(4, ""); }
            finally { MyGridProgram.CustomDataAtStartup = null; }

            var received = new List<string>();
            relay.GridTerminalSystem.ApplicationsById[42] = new ApplicationBlock(42, command =>
            {
                received.Add(command);
                return true;
            });
            relay.GridTerminalSystem.ApplicationsById[43] = new ApplicationBlock(43, _ =>
                throw new InvalidOperationException("Delivered to the wrong block"));
            string Command(long target) => "app 4 taskbus 43 " +
                TaskBusProtocol.CreateEnvelope("TASK_QUERY", "{\"request_id\":\"ore\"}", targetId: target);

            relay.Main(Command(99), UpdateType.Terminal);
            Assert.DoesNotContain("SEND:", relay.Storage);
            relay.Main(Command(42), UpdateType.Terminal);
            Assert.Empty(received);
            Assert.Contains("SEND:", relay.Storage);

            relay.Main("", UpdateType.Update10);
            Assert.StartsWith("galnet recv 4 ", Assert.Single(received));
            Assert.DoesNotContain("SEND:", relay.Storage);
            Assert.Empty(relay.IGC.UnicastOutbox);
        }

        [Fact]
        public void BasePublishesQueriesAndCancelsThroughTheBrokersOwnRelay()
        {
            MyGridProgram.CustomDataAtStartup = "App=1\nApp=9";
            Relay relay;
            try { relay = Start(4, ""); }
            finally { MyGridProgram.CustomDataAtStartup = null; }
            var broker = new Broker();
            var relayBlock = new ApplicationBlock(4, command =>
            {
                relay.Main(command, UpdateType.Terminal);
                return true;
            });
            broker.GridTerminalSystem.Relay = relayBlock;
            relay.GridTerminalSystem.ApplicationsById[1] = new ApplicationBlock(1, command =>
            {
                broker.Main(command);
                return true;
            });

            var bridge = new GalnetWorkerBridge(new ApplicationBlock(9, _ => true), "GalnetBrokerRelay=4",
                () => relayBlock, (_, __) => throw new InvalidOperationException("Radio not allowed"), _ => { });
            var replies = new List<TaskBusEnvelope>();
            relay.GridTerminalSystem.ApplicationsById[9] = new ApplicationBlock(9, command =>
                bridge.TryReceive(command, (source, payload) =>
                {
                    Assert.Equal(1L, source);
                    Assert.True(TaskBusProtocol.TryParseEnvelope(payload, out var reply));
                    replies.Add(reply);
                }));

            var publish = Exchange("TASK_PUBLISH", "{\"request_id\":\"iron\",\"group_id\":\"MiningPoints\"}");
            Assert.Equal("TASK_PUBLISHED_ACK", publish.EventType);
            Assert.Contains("\"state\":\"QUEUED\"", publish.PayloadJson);

            var query = Exchange("TASK_QUERY", "{\"request_id\":\"iron\"}");
            Assert.Equal("TASK_PUBLISHED_ACK", query.EventType);
            Assert.Contains("\"state\":\"QUEUED\"", query.PayloadJson);

            var cancel = Exchange("TASK_CANCEL", "{\"request_id\":\"iron\"}");
            Assert.Equal("TASK_CANCEL_ACK", cancel.EventType);
            Assert.Contains("\"state\":\"CANCELLED\"", Exchange("TASK_QUERY",
                "{\"request_id\":\"iron\"}").PayloadJson);

            TaskBusEnvelope Exchange(string eventType, string payload)
            {
                replies.Clear();
                bridge.Send(TaskBusProtocol.CreateEnvelope(eventType, payload, targetId: 1), 1);
                for (int tick = 0; tick < 48 && replies.Count == 0; tick++)
                {
                    bridge.Flush();
                    broker.Main("");
                    relay.Main("", UpdateType.Update10);
                }
                Assert.DoesNotContain("SEND:", relay.Storage);
                Assert.Empty(relay.IGC.UnicastOutbox);
                Assert.Empty(broker.IGC.Outbox);
                return Assert.Single(replies);
            }
        }

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
            nodes[1].Main("network status", UpdateType.Terminal);
            Assert.Contains(nodes[1].Output, line => line.EndsWith("waiting for route"));

            offline.Remove(3);
            Advance(50);
            Assert.Contains("SEND:", nodes[1].Storage);
            Assert.Single(nodes[4].Storage.Split('\n').Where(line => line.StartsWith("SEEN:")));
            Assert.True(dataFromReserve > 0);
            nodes[1].Main("network status", UpdateType.Terminal);
            Assert.Contains(nodes[1].Output, line => line.EndsWith("route via 3, awaiting ACK"));

            nodes[4] = Start(4, nodes[4].Storage);
            dropAck = false;
            Advance(60);
            Assert.DoesNotContain("SEND:", nodes[1].Storage);
            Assert.Single(nodes[4].Storage.Split('\n').Where(line => line.StartsWith("SEEN:")));
            Assert.Contains("LAST:" + TaskBusIgcHelper.EncodeStorage("From 1: payload"), nodes[4].Storage);
        }

        [Fact]
        public void RelayDropsExpiredOutboxAndDoesNotAcknowledgeExpiredArrival()
        {
            var relay = Start(4, "0\n1\nSEND:DATA|4|1|1:4|8|1|YQ==");
            Assert.DoesNotContain("SEND:", relay.Storage);

            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "HELLO"));
            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "DATA|1|4|1:1|8|1|YQ=="));
            relay.Main("", UpdateType.Update10);
            Assert.DoesNotContain(relay.IGC.UnicastOutbox, packet => packet.Data.StartsWith("ACK|"));
            Assert.DoesNotContain("SEEN:", relay.Storage);
        }

        [Fact]
        public void ReceiptLogReservesCapacityForAddressedTaskBusDelivery()
        {
            long expires = DateTime.UtcNow.AddMinutes(1).Ticks;
            var receipts = Enumerable.Range(0, 48).Select(i => "SEEN:1:old" + i + "|" + expires);
            var relay = Start(4, "0\n1\n" + string.Join("\n", receipts));
            var application = new TestApplication { Accept = true };
            relay.GridTerminalSystem.Application = application;
            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "HELLO"));
            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "LINK|1|1|1|8|4"));
            var mark = TaskBusIgcHelper.EncodeStorage("heartbeat|sample");
            var task = TaskBusIgcHelper.EncodeStorage("taskbus 12 " +
                TaskBusProtocol.CreateEnvelope("TASK_QUERY", "{\"request_id\":\"ore\"}", targetId: 44));
            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "APP|1|4|mark|8|" + expires + "|" + mark));
            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "APP|1|4|task|8|" + expires + "|" + task));
            relay.Main("", UpdateType.Update10);

            Assert.Equal(1, application.Calls);
            Assert.Contains("SEEN:1:task", relay.Storage);
            Assert.DoesNotContain("SEEN:1:mark", relay.Storage);
        }

        [Fact]
        public void FullReceiptLogKeepsLiveReceiptsUntilTheirDeadline()
        {
            var expires = DateTime.UtcNow.AddMinutes(1).Ticks;
            var receipts = Enumerable.Range(0, 64).Select(i => "SEEN:1:old" + i + "|" + expires);
            var relay = Start(4, "0\n1\n" + string.Join("\n", receipts));
            var payload = TaskBusIgcHelper.EncodeStorage("taskbus 12 " +
                TaskBusProtocol.CreateEnvelope("TASK_QUERY", "{\"request_id\":\"ore\"}", targetId: 44));
            var application = new TestApplication { Accept = true };
            relay.GridTerminalSystem.Application = application;

            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "HELLO"));
            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "APP|1|4|new|8|" + expires + "|" + payload));
            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "APP|1|4|old0|8|" + expires + "|YQ=="));
            relay.Main("", UpdateType.Update10);

            Assert.Equal(0, application.Calls);
            Assert.DoesNotContain("SEEN:1:new", relay.Storage);
            Assert.Contains("SEEN:1:old0", relay.Storage);
            Assert.DoesNotContain(relay.IGC.UnicastOutbox, packet => packet.Data.StartsWith("ACK|4|1|new|"));

            var expired = DateTime.UtcNow.AddMinutes(-1).Ticks;
            var restart = Start(4, relay.Storage.Replace("SEEN:1:old0|" + expires, "SEEN:1:old0|" + expired));
            restart.GridTerminalSystem.Application = application;
            restart.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "HELLO"));
            restart.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "APP|1|4|new|8|" + expires + "|" + payload));
            restart.Main("", UpdateType.Update10);
            Assert.Equal(1, application.Calls);
            Assert.Contains("SEEN:1:new", restart.Storage);
        }

        [Fact]
        public void RepeatedApplicationKeepsOriginalPacketAcrossRestart()
        {
            var relay = Start(4, "0\n1\n2");
            relay.Main("app 1 repeated payload", UpdateType.Terminal);
            var packet = Assert.Single(relay.Storage.Split('\n').Where(line => line.StartsWith("SEND:")));
            relay = Start(4, relay.Storage);
            for (int i = 0; i < 20; i++)
                relay.Main("app 1 repeated payload", UpdateType.Terminal);

            Assert.Equal(packet, Assert.Single(relay.Storage.Split('\n').Where(line => line.StartsWith("SEND:"))));
            relay.Main("app 2 repeated payload", UpdateType.Terminal);
            relay.Main("app 1 different payload", UpdateType.Terminal);
            Assert.Equal(3, relay.Storage.Split('\n').Count(line => line.StartsWith("SEND:")));
        }

        [Fact]
        public void RepeatedApplicationWaitsForTheRelayRetryInterval()
        {
            var relay = Start(4, "0\n1");
            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "HELLO"));
            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "LINK|1|1|1|8|4"));
            relay.Main("", UpdateType.Update10);
            relay.Main("app 1 repeated payload", UpdateType.Terminal);
            var packet = Assert.Single(relay.IGC.UnicastOutbox.Where(item => item.Data.StartsWith("APP|")));
            relay.IGC.UnicastOutbox.Clear();

            for (int i = 0; i < 10; i++)
            {
                relay.Main("app 1 repeated payload", UpdateType.Terminal);
                relay.Main("", UpdateType.Update10);
            }
            Assert.DoesNotContain(relay.IGC.UnicastOutbox, item => item.Data.StartsWith("APP|"));

            relay.Main("", UpdateType.Update10);
            relay.Main("", UpdateType.Update10);
            var retry = Assert.Single(relay.IGC.UnicastOutbox.Where(item => item.Data.StartsWith("APP|")));
            Assert.Equal(packet.Data, retry.Data);
        }

        [Fact]
        public void TimedApplicationsLeaveSpaceForRegularDelivery()
        {
            var relay = Start(4, "0\n1");
            long deadline = DateTime.UtcNow.AddSeconds(30).Ticks;
            for (int i = 0; i < 16; i++)
                relay.Main("app-until " + deadline + " 1 mark", UpdateType.Terminal);

            Assert.Equal(12, relay.Storage.Split('\n').Count(line => line.StartsWith("SEND:")));
            for (int i = 0; i < 5; i++)
                relay.Main("app 1 task-" + i, UpdateType.Terminal);

            Assert.Equal(16, relay.Storage.Split('\n').Count(line => line.StartsWith("SEND:")));
            Assert.Equal(4, relay.Storage.Split('\n').Count(line =>
                line.StartsWith("SEND:APP|") && TaskBusIgcHelper.DecodeStorage(line.Split('|')[6]).StartsWith("task-")));
        }

        [Fact]
        public void TimedApplicationUsesItsOwnDeadlineAcrossRestart()
        {
            var relay = Start(4, "0\n1");
            long deadline = DateTime.UtcNow.AddSeconds(30).Ticks;
            relay.Main("app-until 1 1 expired", UpdateType.Terminal);
            Assert.DoesNotContain("SEND:", relay.Storage);
            relay.Main("app-until " + deadline + " 1 current", UpdateType.Terminal);
            Assert.Contains("|" + deadline + "|", relay.Storage);

            var expired = Start(4, relay.Storage.Replace("|" + deadline + "|", "|1|"));
            Assert.DoesNotContain("SEND:", expired.Storage);
        }

        [Fact]
        public void ApplicationPacketIsAcknowledgedOnlyAfterLocalHandoff()
        {
            var relay = Start(4, "0\n1");
            var application = new TestApplication();
            relay.GridTerminalSystem.Application = application;
            var expires = System.DateTime.UtcNow.AddMinutes(1).Ticks;
            var packet = "APP|1|4|1:7|8|" + expires + "|" + TaskBusIgcHelper.EncodeStorage("test payload");

            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "HELLO"));
            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", "LINK|1|1|1|8|4"));
            relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", packet));
            relay.Main("", UpdateType.Update10);
            Assert.DoesNotContain("SEEN:", relay.Storage);

            application.Accept = true;
            for (int repeat = 0; repeat < 2; repeat++)
            {
                relay.IGC.UnicastListener.Inbox.Enqueue((1, "SE1-GALNET-V1", packet));
                relay.Main("", UpdateType.Update10);
            }
            Assert.Equal(2, application.Calls);
            Assert.Equal("galnet recv 1 1:7 test payload", application.LastCommand);
            Assert.Single(relay.Storage.Split('\n').Where(line => line.StartsWith("SEEN:")));
            Assert.Contains(relay.IGC.UnicastOutbox, message => message.Data.StartsWith("ACK|4|1|1:7|"));
        }

        private sealed class TestApplication : IMyProgrammableBlock
        {
            public long EntityId => 44;
            public long CubeGrid => 1;
            public bool IsWorking => true;
            public bool Accept;
            public int Calls;
            public string LastCommand;

            public bool TryRun(string command)
            {
                Calls++;
                LastCommand = command;
                return Accept;
            }
        }

        [Fact]
        public void BasePublishesAndWorkerCompletesTaskThroughGalnet()
        {
            var workerRelay = Start(21, "0\n22");
            var brokerRelay = Start(22, "0\n21\n23");
            var baseRelay = Start(23, "0\n22");
            MyGridProgram.StorageAtStartup = "";
            Broker broker;
            try { broker = new Broker(); }
            finally { MyGridProgram.StorageAtStartup = null; }

            var workerBlock = new ApplicationBlock(42, _ => true);
            var localRelay = new ApplicationBlock(21, command =>
            {
                workerRelay.Main(command, UpdateType.Terminal);
                return true;
            });
            var bridge = new GalnetWorkerBridge(workerBlock, "GalnetBrokerRelay=22",
                () => localRelay, (_, __) => throw new InvalidOperationException("Direct IGC disabled"), _ => { });
            string storage = "";
            var worker = new TaskBusConsumer(bridge.Send, () => storage, value => storage = value,
                _ => { }, 42, "Platform", 30, miningPlatform: true);
            worker.ProcessCommand("broker 1");

            workerRelay.GridTerminalSystem.Application = new ApplicationBlock(42,
                command => bridge.TryReceive(command, (source, envelope) => worker.ProcessIncomingMessage(source, envelope)));
            brokerRelay.GridTerminalSystem.Application = new ApplicationBlock(1, command =>
            {
                broker.Main(command);
                return true;
            });
            broker.GridTerminalSystem.Relay = new ApplicationBlock(22, command =>
            {
                brokerRelay.Main(command, UpdateType.Terminal);
                return true;
            });

            var baseBlock = new ApplicationBlock(9, _ => true);
            var baseLocalRelay = new ApplicationBlock(23, command =>
            {
                baseRelay.Main(command, UpdateType.Terminal);
                return true;
            });
            var baseBridge = new GalnetWorkerBridge(baseBlock, "GalnetBrokerRelay=22",
                () => baseLocalRelay, (_, __) => throw new InvalidOperationException("Direct IGC disabled"), _ => { });
            var baseReplies = new List<string>();
            baseRelay.GridTerminalSystem.Application = new ApplicationBlock(9, command =>
                baseBridge.TryReceive(command, (source, envelope) =>
                {
                    Assert.Equal(1L, source);
                    baseReplies.Add(envelope);
                }));
            baseBridge.Send(TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"ore\",\"group_id\":\"MiningFleet\",\"steps\":[\"mine\"]}", targetId: 1), 1);

            var relays = new Dictionary<long, Relay> { { 21, workerRelay }, { 22, brokerRelay }, { 23, baseRelay } };
            bool baseLinkOnline = false;
            void Tick()
            {
                baseBridge.Flush();
                worker.Tick();
                bridge.Flush();
                broker.Main("");
                foreach (var relay in relays.Values)
                    relay.Main("", UpdateType.Update10);
                foreach (var relay in relays)
                {
                    foreach (var packet in relay.Value.IGC.UnicastOutbox)
                    {
                        if (baseLinkOnline || (relay.Key != 23 && packet.Target != 23))
                            relays[packet.Target].IGC.UnicastListener.Inbox.Enqueue((relay.Key, packet.Tag, packet.Data));
                    }
                    relay.Value.IGC.UnicastOutbox.Clear();
                }
            }

            for (int tick = 0; tick < 40; tick++)
                Tick();
            Assert.Empty(baseReplies);
            Assert.False(worker.HasActiveTask);
            Assert.Contains("SEND:", baseRelay.Storage);

            baseLinkOnline = true;
            for (int tick = 0; tick < 150 && baseReplies.Count == 0; tick++)
                Tick();
            Assert.Contains(baseReplies, envelope => envelope.Contains("TASK_PUBLISHED_ACK") &&
                envelope.Contains("\"request_id\":\"ore\""));

            for (int tick = 0; tick < 150 && !worker.HasActiveTask; tick++)
                Tick();
            Assert.True(worker.HasActiveTask);
            Assert.Contains("Stage: mine", worker.GetStatus());
            worker.ProcessCommand("stage mine");
            Assert.Contains("Pending completion: 9-1", worker.GetStatus());
            for (int tick = 0; tick < 150 && !worker.GetStatus().Contains("Pending completion: none"); tick++)
                Tick();
            Assert.Contains("Pending completion: none", worker.GetStatus());
            Assert.DoesNotContain("TASK|", broker.Storage);
            baseReplies.Clear();
            baseBridge.Send(TaskBusProtocol.CreateEnvelope("TASK_QUERY",
                "{\"request_id\":\"ore\"}", targetId: 1), 1);
            for (int tick = 0; tick < 150 && baseReplies.Count == 0; tick++)
                Tick();
            Assert.Contains(baseReplies, envelope => envelope.Contains("TASK_PUBLISHED_ACK") &&
                envelope.Contains("\"state\":\"DONE\""));
            baseReplies.Clear();
            baseBridge.Send(TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"replenish\",\"group_id\":\"MiningPoints\",\"minimum\":100}", targetId: 1), 1);
            for (int tick = 0; tick < 150 && baseReplies.Count == 0; tick++)
                Tick();
            Assert.Contains(baseReplies, envelope => envelope.Contains("\"request_id\":\"replenish\""));
            Assert.EndsWith("|0", Assert.Single(broker.Storage.Split('\n').Where(line => line.StartsWith("TASK|"))));

            baseReplies.Clear();
            baseBridge.Send(TaskBusProtocol.CreateEnvelope("TASK_QUERY",
                "{\"request_id\":\"replenish\",\"stock\":100}", targetId: 1), 1);
            for (int tick = 0; tick < 150 && baseReplies.Count == 0; tick++)
                Tick();
            Assert.Contains(baseReplies, envelope => envelope.Contains("\"request_id\":\"replenish\""));
            Assert.EndsWith("|1", Assert.Single(broker.Storage.Split('\n').Where(line => line.StartsWith("TASK|"))));
            Assert.Empty(broker.IGC.Outbox);
        }

        private sealed class ApplicationBlock : IMyProgrammableBlock
        {
            private readonly Func<string, bool> _run;
            public long EntityId { get; }
            public long CubeGrid => 1;
            public bool IsWorking => true;

            public ApplicationBlock(long entityId, Func<string, bool> run)
            {
                EntityId = entityId;
                _run = run;
            }

            public bool TryRun(string command) => _run(command);
        }

        [Fact]
        public void ConfiguredLaserReconnectsOnlyWhileIdle()
        {
            MyGridProgram.CustomDataAtStartup = "Laser=Laser A|GPS:Peer:1:2:3:";
            Relay relay;
            try { relay = Start(4, "0"); }
            finally { MyGridProgram.CustomDataAtStartup = null; }
            var laser = new TestLaser();
            relay.GridTerminalSystem.Laser = laser;
            relay.Main("", UpdateType.Update10);
            Assert.Equal(1, laser.Connects);
            Assert.Equal("GPS:Peer:1:2:3:", laser.Target);

            laser.State = MyLaserAntennaStatus.Connected;
            for (int tick = 0; tick < 60; tick++)
                relay.Main("", UpdateType.Update10);
            Assert.Equal(1, laser.Connects);

            laser.State = MyLaserAntennaStatus.Idle;
            for (int tick = 0; tick < 60; tick++)
                relay.Main("", UpdateType.Update10);
            Assert.Equal(2, laser.Connects);
        }

        private sealed class TestLaser : IMyLaserAntenna
        {
            public long CubeGrid => 1;
            public bool IsWorking => true;
            public MyLaserAntennaStatus Status => State;
            public MyLaserAntennaStatus State;
            public int Connects;
            public string Target;
            public void SetTargetCoords(string gps) => Target = gps;
            public void Connect() => Connects++;
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
