using System.Collections.Generic;
using SpaceEngineers.Libs.IgcCore;
using SpaceEngineers.Libs.TaskBusConsumer;
using Xunit;

namespace TaskBus.Tests
{
    public sealed class TaskBusConsumerTests
    {
        [Fact]
        public void MiningApproachRequiresARecentlyConfirmedLease()
        {
            string storage = "";
            TaskBusConsumer Start() => new TaskBusConsumer((_, __) => { }, () => storage,
                value => storage = value, _ => { }, 42, "Miner");
            var worker = Start();
            worker.ProcessCommand("group MiningFleet");
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"mine-1\",\"lease_epoch\":1,\"details\":{" +
                "\"steps\":[\"mine\"],\"approach_position\":{\"x\":10,\"y\":20,\"z\":30}}}", targetId: 42);
            worker.ProcessIncomingMessage(1, assignment);
            Assert.True(worker.TryGetMiningApproach(out var id, out var x, out var y, out var z));
            Assert.Equal("mine-1", id);
            Assert.Equal((10d, 20d, 30d), (x, y, z));
            worker = Start();
            Assert.False(worker.TryGetMiningApproach(out _, out _, out _, out _));
        }

        [Fact]
        public void AutomaticMiningPointRetriesChildAndRequiresItsCompletion()
        {
            string storage = "";
            var sent = new List<string>();
            TaskBusConsumer Start() => new TaskBusConsumer((message, _) => sent.Add(message),
                () => storage, value => storage = value, _ => { }, 42, "Point", 1);

            var point = Start();
            point.ProcessCommand("group MiningPoints");
            point.ProcessCommand("point auto");
            var parent = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"parent\",\"lease_epoch\":7,\"details\":{\"site_required\":true,\"resource\":\"Ore/Iron\",\"steps\":[\"survey\",\"mine\"]}}", targetId: 42);
            point.ProcessIncomingMessage(1, parent);
            point.Tick();
            var firstPublication = sent[sent.Count - 1];
            Assert.Contains("\"group_id\":\"Scouts\"", firstPublication);
            Assert.Contains("\"parent_lease_epoch\":7", firstPublication);
            point.ProcessCommand("stage survey");
            Assert.Contains("Stage: survey", point.GetStatus());

            point = Start();
            point.ProcessIncomingMessage(1, parent);
            point.Tick();
            Assert.Equal(firstPublication, sent[sent.Count - 1]);
            point.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_PUBLISHED_ACK",
                "{\"request_id\":\"parent:7:0\",\"state\":\"DONE\"}", targetId: 42));
            point.ProcessCommand("stage survey");
            Assert.Contains("Stage: survey", point.GetStatus());
            point.ProcessCommand("site GPS:Gold:10:20:30:");
            point.ProcessCommand("approach GPS:Entry:10:20:50:");
            point.ProcessCommand("stage survey");
            Assert.Contains("Stage: survey", point.GetStatus());
            point.ProcessCommand("site GPS:Iron:10:20:30:");
            point = Start();
            point.ProcessIncomingMessage(1, parent);
            point.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_PUBLISHED_ACK",
                "{\"request_id\":\"parent:7:0\",\"state\":\"DONE\"}", targetId: 42));
            point.ProcessCommand("stage survey");
            Assert.Contains("Stage: mine", point.GetStatus());
            point.Tick();
            Assert.Contains("\"group_id\":\"MiningFleet\"", sent[sent.Count - 1]);
            Assert.Contains("\"ore_position\":{\"x\":10,\"y\":20,\"z\":30}", sent[sent.Count - 1]);
            Assert.Contains("\"approach_position\":{\"x\":10,\"y\":20,\"z\":50}", sent[sent.Count - 1]);
        }

        [Fact]
        public void WorkerPollsAndAcceptsAssignmentsOnlyFromStoredBroker()
        {
            string storage = "";
            var sent = new List<string>();
            TaskBusConsumer Start() => new TaskBusConsumer((message, _) => sent.Add(message),
                () => storage, value => storage = value, _ => { }, 42, "Worker", 1);

            var worker = Start();
            worker.ProcessCommand("broker 17");
            worker = Start();
            worker.Tick();
            Assert.True(TaskBusProtocol.TryParseEnvelope(Assert.Single(sent), out var poll));
            Assert.Equal(17L, poll.TargetId);

            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"work-1\",\"lease_epoch\":3}", targetId: 42);
            Assert.False(worker.ProcessIncomingMessage(18, assignment));
            Assert.False(worker.HasActiveTask);
            Assert.True(worker.ProcessIncomingMessage(17, assignment));
            Assert.True(worker.HasActiveTask);
            worker.ProcessCommand("broker 18");
            Assert.Contains("Broker: 17", worker.GetStatus());
        }

        [Fact]
        public void MiningStatusShowsQuantityAndDeliveryBaseAfterRestart()
        {
            string storage = "";
            TaskBusConsumer Start() => new TaskBusConsumer((_, __) => { },
                () => storage, value => storage = value, _ => { }, 42, "Worker");
            var worker = Start();
            var payload = "{\"task_id\":\"ore-1\",\"lease_epoch\":1,\"key\":\"Ore/Iron\",\"details\":{" +
                "\"resource\":\"Ore/Iron\",\"amount\":12.5,\"base_grid_name\":\"Refinery\"," +
                "\"base_position\":{\"x\":100,\"y\":-200.25,\"z\":300}," +
                "\"ore_position\":{\"x\":10,\"y\":20,\"z\":30},\"approach_position\":{\"x\":10,\"y\":20,\"z\":50}," +
                "\"steps\":[\"survey\"]}}";
            worker.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_ASSIGN", payload, targetId: 42));

            var status = Start().GetStatus();
            Assert.Contains("Resource: Ore/Iron", status);
            Assert.Contains("Requested amount: 12.5", status);
            Assert.Contains("Delivery base: Refinery", status);
            Assert.Contains("Base at request time (X, Y, Z): 100, -200.25, 300", status);
            Assert.Contains("Mining approach (X, Y, Z): 10, 20, 50", status);
            Assert.Contains("Ore deposit (X, Y, Z): 10, 20, 30", status);
        }

        [Fact]
        public void StageWaitsForLeaseConfirmationAfterRestartAndTimeout()
        {
            string storage = "";
            TaskBusConsumer Start() => new TaskBusConsumer((_, __) => { },
                () => storage, value => storage = value, _ => { }, 42, "Worker");
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"work-1\",\"lease_epoch\":3}", targetId: 42);

            var worker = Start();
            worker.ProcessIncomingMessage(1, assignment);
            worker = Start();
            worker.ProcessCommand("stage execute");
            Assert.True(worker.HasActiveTask);

            worker.ProcessIncomingMessage(1, assignment);
            for (int tick = 0; tick < 120; tick++)
                worker.Tick();
            worker.ProcessCommand("stage execute");
            Assert.True(worker.HasActiveTask);

            worker.ProcessIncomingMessage(1, assignment);
            worker.Tick(false);
            worker.ProcessCommand("stage execute");
            Assert.True(worker.HasActiveTask);

            worker.ProcessIncomingMessage(1, assignment);
            worker.ProcessCommand("stage execute");
            Assert.False(worker.HasActiveTask);
            Assert.Contains("Pending completion: work-1", worker.GetStatus());
        }

        [Fact]
        public void RevokedCompletionReleasesWorkerForAnotherTask()
        {
            string storage = "";
            var worker = new TaskBusConsumer((_, __) => { }, () => storage,
                value => storage = value, _ => { }, 42, "Worker", 1);

            void Deliver(string type, string taskId, int epoch)
            {
                var envelope = TaskBusProtocol.CreateEnvelope(type,
                    "{\"task_id\":\"" + taskId + "\",\"lease_epoch\":" + epoch + "}", targetId: 42);
                Assert.True(worker.ProcessIncomingMessage(1, envelope));
            }

            Deliver("TASK_ASSIGN", "old", 3);
            worker.ProcessCommand("stage execute");
            Deliver("TASK_REVOKED", "old", 2);
            Deliver("TASK_ASSIGN", "new", 1);
            Assert.False(worker.HasActiveTask);
            Assert.Contains("Pending completion: old", worker.GetStatus());

            Deliver("TASK_REVOKED", "old", 3);
            worker = new TaskBusConsumer((_, __) => { }, () => storage,
                value => storage = value, _ => { }, 42, "Worker", 1);
            Deliver("TASK_ASSIGN", "new", 1);
            Assert.True(worker.HasActiveTask);
            Assert.Contains("Pending completion: none", worker.GetStatus());
        }

        [Theory]
        [InlineData("TASK_CANCEL")]
        [InlineData("TASK_REVOKED")]
        public void DelayedLeaseMessageCannotClearNewerLeaseAfterRestart(string eventType)
        {
            string storage = "";
            var sent = new List<string>();
            TaskBusConsumer StartConsumer() => new TaskBusConsumer((message, _) => sent.Add(message),
                () => storage, value => storage = value, _ => { }, 42, "Worker", 1);

            var worker = StartConsumer();
            worker.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"work-1\",\"lease_epoch\":3}", targetId: 42));
            worker.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"work-1\",\"lease_epoch\":5}", targetId: 42));
            worker = StartConsumer();

            worker.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope(eventType,
                "{\"task_id\":\"work-1\",\"lease_epoch\":3}", targetId: 42));
            worker.Tick();
            Assert.Contains("\"active_task_id\":\"work-1\"", sent[sent.Count - 1]);
            Assert.Contains("\"active_lease_epoch\":5", sent[sent.Count - 1]);

            worker.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope(eventType,
                "{\"task_id\":\"work-1\",\"lease_epoch\":5}", targetId: 42));
            worker = StartConsumer();
            worker.Tick();
            Assert.DoesNotContain("\"active_task_id\":\"work-1\"", sent[sent.Count - 1]);
        }
    }
}
