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
            relay.Main("network status", UpdateType.Terminal);
            Assert.Contains(relay.Output, line => line.EndsWith("waiting for local application"));

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
        public void RejectedSendsTrySecondReserveBeforeHeartbeatsExpire()
        {
            var relay = Start(1, "0\n2\n3\n5");
            const string channel = "SE1-GALNET-V1";
            relay.IGC.UnicastListener.Inbox.Enqueue((2, channel, "HELLO"));
            relay.IGC.UnicastListener.Inbox.Enqueue((3, channel, "HELLO"));
            relay.IGC.UnicastListener.Inbox.Enqueue((5, channel, "HELLO"));
            relay.IGC.UnicastListener.Inbox.Enqueue((5, channel, "LINK|5|1|1|8|1,4"));
            relay.IGC.UnicastListener.Inbox.Enqueue((2, channel, "LINK|2|1|1|8|1,4"));
            relay.IGC.UnicastListener.Inbox.Enqueue((3, channel, "LINK|3|1|1|8|1,4"));
            relay.IGC.UnicastListener.Inbox.Enqueue((2, channel, "LINK|4|1|1|7|2,3,5"));
            relay.Main("", UpdateType.Update10);
            relay.Main("route 4", UpdateType.Terminal);
            Assert.Contains("Next hop: 2", relay.Output);

            relay.IGC.UnicastOutbox.Clear();
            var attempts = new List<long>();
            relay.IGC.AllowUnicast = target =>
            {
                attempts.Add(target);
                return target == 5;
            };
            relay.Main("send 4 payload", UpdateType.Terminal);

            Assert.Equal(new long[] { 2, 3, 5 }, attempts);
            var sent = Assert.Single(relay.IGC.UnicastOutbox);
            Assert.Equal(5L, sent.Target);
            Assert.StartsWith("DATA|1|4|", sent.Data);
            Assert.Contains("SEND:", relay.Storage);
            var fields = sent.Data.Split('|');
            relay.Main("network status", UpdateType.Terminal);
            Assert.Contains("Last accepted hop: 5 for " + fields[3] + " to 4", relay.Output);
            Assert.Contains("No outbound delivery acknowledged yet.", relay.Output);

            relay.IGC.UnicastListener.Inbox.Enqueue((5, channel, "ACK|4|1|" + fields[3] + "|7|" + fields[5] + "|"));
            relay.Main("network status", UpdateType.Terminal);
            Assert.Contains("Last ACK: " + fields[3] + " from 4", relay.Output);
            Assert.DoesNotContain("SEND:", relay.Storage);
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
        public void LeaseTrafficExpiresSoonerThanCompletionMessages()
        {
            var relay = Start(4, "0\n1");
            long started = DateTime.UtcNow.Ticks;
            foreach (var eventType in new[] { "TASK_POLL", "TASK_ASSIGN", "TASK_RECEIVED", "QUEUE_EMPTY", "TASK_DONE" })
                relay.Main("app 1 taskbus 12 " + TaskBusProtocol.CreateEnvelope(eventType, "{}", targetId: 44), UpdateType.Terminal);

            var packets = relay.Storage.Split('\n').Where(line => line.StartsWith("SEND:")).ToArray();
            Assert.Equal(5, packets.Length);
            foreach (var packet in packets.Take(4))
                Assert.InRange(long.Parse(packet.Split('|')[5]) - started,
                    TimeSpan.FromSeconds(20).Ticks, TimeSpan.FromSeconds(22).Ticks);
            Assert.True(long.Parse(packets[4].Split('|')[5]) - started >= TimeSpan.FromMinutes(5).Ticks);
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
        public void NewHeartbeatReplacesOlderSampleForTheSameDestinationAndFleetAcrossRestart()
        {
            long first = DateTime.UtcNow.AddSeconds(8).Ticks;
            var relay = Start(4, "0\n1\n2");
            string Command(long target, long deadline, string fleet, string payload) =>
                "app-until " + deadline + " " + target + " heartbeat|" + deadline + "|" +
                TaskBusIgcHelper.EncodeStorage(fleet) + "|" + TaskBusIgcHelper.EncodeStorage(payload);

            relay.Main(Command(1, first, "fleet", "old"), UpdateType.Terminal);
            relay.Main(Command(2, first, "fleet", "other destination"), UpdateType.Terminal);
            relay.Main(Command(1, first, "second fleet", "other fleet"), UpdateType.Terminal);
            relay = Start(4, relay.Storage);
            relay.Main(Command(1, first + TimeSpan.TicksPerSecond, "fleet", "latest"), UpdateType.Terminal);
            relay.Main(Command(1, first, "fleet", "late"), UpdateType.Terminal);

            var values = relay.Storage.Split('\n').Where(line => line.StartsWith("SEND:"))
                .Select(line => TaskBusIgcHelper.DecodeStorage(line.Split('|')[6]).Split('|')[3])
                .Select(TaskBusIgcHelper.DecodeStorage).ToArray();
            Assert.Equal(new[] { "latest", "other destination", "other fleet" }, values);
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
            Assert.EndsWith("|0", Assert.Single(broker.Storage.Split('\n').Where(line => line.StartsWith("TASK|"))).TrimEnd('\r'));

            baseReplies.Clear();
            baseBridge.Send(TaskBusProtocol.CreateEnvelope("TASK_QUERY",
                "{\"request_id\":\"replenish\",\"stock\":100}", targetId: 1), 1);
            for (int tick = 0; tick < 150 && baseReplies.Count == 0; tick++)
                Tick();
            Assert.Contains(baseReplies, envelope => envelope.Contains("\"request_id\":\"replenish\""));
            Assert.EndsWith("|1", Assert.Single(broker.Storage.Split('\n').Where(line => line.StartsWith("TASK|"))).TrimEnd('\r'));
            Assert.Empty(broker.IGC.Outbox);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MiningPointClosesSurveyMineAndTransportThroughGalnetWithBaseReceipt(bool interruptDelivery)
        {
            Relay workerRelay;
            MyGridProgram.CustomDataAtStartup = "App=9\nApp=40\nApp=42\nApp=43";
            try { workerRelay = Start(21, "0\n22"); }
            finally { MyGridProgram.CustomDataAtStartup = null; }
            var brokerRelay = Start(22, "0\n21");
            var broker = new Broker();
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

            var relayBlock = new ApplicationBlock(21, command =>
            {
                workerRelay.Main(command, UpdateType.Terminal);
                return true;
            });
            var bridges = new List<GalnetWorkerBridge>();
            GalnetWorkerBridge Connect(long id, Action<long, string> receive)
            {
                var bridge = new GalnetWorkerBridge(new ApplicationBlock(id, _ => true), "GalnetBrokerRelay=22",
                    () => relayBlock, (_, __) => throw new InvalidOperationException("Direct IGC disabled"), _ => { });
                workerRelay.GridTerminalSystem.ApplicationsById[id] = new ApplicationBlock(id,
                    command => bridge.TryReceive(command, receive));
                bridges.Add(bridge);
                return bridge;
            }

            var replies = new List<string>();
            var baseBridge = Connect(9, (_, envelope) => replies.Add(envelope));
            TaskBusConsumer point = null;
            var pointBridge = Connect(40, (source, envelope) => point.ProcessIncomingMessage(source, envelope));
            double buffer = 0;
            point = new TaskBusConsumer(pointBridge.Send, () => "", _ => { }, _ => { }, 40, "Point",
                gridId: 500, readMiningBuffer: _ => buffer, autoAcceptSurvey: true);
            point.ProcessCommand("group MiningPoints");
            point.ProcessCommand("point auto");

            TaskBusConsumer platform = null;
            var platformBridge = Connect(42, (source, envelope) => platform.ProcessIncomingMessage(source, envelope));
            platform = new TaskBusConsumer(platformBridge.Send, () => "", _ => { }, _ => { }, 42, "Platform",
                miningPlatform: true);

            TaskBusConsumer hauler = null;
            var haulerBridge = Connect(43, (source, envelope) => hauler.ProcessIncomingMessage(source, envelope));
            long dock = 500;
            double cargo = 50;
            string haulerStorage = "";
            TaskBusConsumer StartHauler() => new TaskBusConsumer(haulerBridge.Send, () => haulerStorage,
                value => haulerStorage = value, _ => { }, 43, "Hauler",
                readHaulerCargo: _ => cargo, readDockedGrid: () => dock, autoHauler: true);
            hauler = StartHauler();
            hauler.ProcessCommand("group Haulers");
            foreach (var worker in new[] { point, platform, hauler })
                worker.ProcessCommand("broker 1");

            bool linkOnline = true;
            void Move(Relay from, long source, Relay to, long target)
            {
                if (linkOnline)
                    foreach (var packet in from.IGC.UnicastOutbox.Where(packet => packet.Target == target))
                        to.IGC.UnicastListener.Inbox.Enqueue((source, packet.Tag, packet.Data));
                from.IGC.UnicastOutbox.Clear();
            }

            void Tick()
            {
                foreach (var worker in new[] { point, platform, hauler })
                    worker.Tick();
                foreach (var bridge in bridges)
                    bridge.Flush();
                broker.Main("");
                workerRelay.Main("", UpdateType.Update10);
                brokerRelay.Main("", UpdateType.Update10);
                Move(workerRelay, 21, brokerRelay, 22);
                Move(brokerRelay, 22, workerRelay, 21);
            }

            void Until(Func<bool> done)
            {
                for (int tick = 0; tick < 600 && !done(); tick++)
                    Tick();
                Assert.True(done(), point.GetStatus() + "\n" + platform.GetStatus() + "\n" + hauler.GetStatus());
            }

            baseBridge.Send(TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"replenish\",\"group_id\":\"MiningPoints\",\"resource\":\"Ore/Iron\",\"amount\":50," +
                "\"minimum\":100,\"base_grid_id\":600,\"receipt_required\":true,\"site_required\":true," +
                "\"steps\":[\"survey\",\"mine\",\"transport\"]}", targetId: 1), 1);
            Until(() => platform.TryGetSurveyLease(out _, out _));
            platform.CompleteSurvey("1 positive sample. GPS:Iron:10:20:30: | GPS:Observation:10:20:60:");
            Until(() => platform.TryGetMiningApproach(out _, out _, out _, out _));
            platform.RecordMiningDeposit(50);
            Assert.True(platform.TryCompleteMiningDeposit());
            Until(() => point.GetStatus().Contains("Mining child complete"));
            Assert.Contains("Stage: mine", point.GetStatus());

            buffer = 50;
            Until(() => hauler.GetStatus().Contains("Stage: deliver"));
            linkOnline = !interruptDelivery;
            dock = 600;
            cargo = 0;
            if (interruptDelivery)
            {
                Until(() => hauler.GetStatus().Contains("Pending completion: 40-4"));
                hauler = StartHauler();
                Assert.Contains("Pending completion: 40-4", hauler.GetStatus());
                for (int tick = 0; tick < 54; tick++)
                    Tick();
                Assert.Contains("TASK|40-4|", broker.Storage);
                Assert.Contains("Pending completion: 40-4", hauler.GetStatus());
                Assert.DoesNotContain("Hauler task complete.", point.GetStatus());
                linkOnline = true;
            }
            Until(() => point.GetStatus().Contains("Hauler task complete."));
            Assert.Single(broker.Storage.Split('\n').Where(line => line.StartsWith("COMMIT|") &&
                line.Contains(TaskBusIgcHelper.EncodeStorage("40-4"))));
            Assert.True(point.HasActiveTask);
            Assert.Contains("Waiting for base stock confirmation", point.GetStatus());

            baseBridge.Send(TaskBusProtocol.CreateEnvelope("TASK_QUERY",
                "{\"request_id\":\"replenish\",\"stock\":100}", targetId: 1), 1);
            Until(() => !point.HasActiveTask && point.GetStatus().Contains("Pending completion: none"));
            baseBridge.Send(TaskBusProtocol.CreateEnvelope("TASK_QUERY",
                "{\"request_id\":\"replenish\"}", targetId: 1), 1);
            Until(() => replies.Any(reply => reply.Contains("\"request_id\":\"replenish\"") &&
                reply.Contains("\"state\":\"DONE\"")));
            Assert.DoesNotContain("TASK|", broker.Storage);
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
