using System.Collections.Generic;
using SpaceEngineers.Libs.IgcCore;
using SpaceEngineers.Libs.TaskBusConsumer;
using Xunit;

namespace TaskBus.Tests
{
    public sealed class TaskBusConsumerTests
    {
        [Fact]
        public void AutomaticPointClosesTransportAfterHaulerAndBaseConfirm()
        {
            string storage = "";
            var sent = new List<string>();
            TaskBusConsumer Start() => new TaskBusConsumer((message, _) => sent.Add(message), () => storage,
                value => storage = value, _ => { }, 42, "Mining point", 1);
            var point = Start();
            point.ProcessCommand("group MiningPoints");
            point.ProcessCommand("point auto");
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"parent\",\"lease_epoch\":1,\"base_stock_confirmed\":false," +
                "\"details\":{\"receipt_required\":true,\"steps\":[\"transport\"]}}", targetId: 42);
            point.ProcessIncomingMessage(1, assignment);
            point.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_PUBLISHED_ACK",
                "{\"request_id\":\"parent:1:0\",\"state\":\"DONE\"}", targetId: 42));
            point.Tick();
            Assert.Contains("Waiting for base stock confirmation", point.GetStatus());

            point = Start();
            point.Tick();
            Assert.Contains("Pending completion: none", point.GetStatus());
            point.ProcessIncomingMessage(1, assignment.Replace("false", "true"));
            point.Tick();
            Assert.Contains("Pending completion: parent", point.GetStatus());
            Assert.Contains("TASK_DONE", sent[sent.Count - 1]);
        }

        [Fact]
        public void TransportAssignmentIncludesPickupAndDeliveryPositions()
        {
            var sent = new List<string>();
            var point = new TaskBusConsumer((message, _) => sent.Add(message), () => "", _ => { }, _ => { },
                42, "Mining point", 1, getGridPositionJson: () => "{\"x\":10,\"y\":20,\"z\":30}", gridId: 500);
            point.ProcessCommand("group MiningPoints");
            point.ProcessCommand("point auto");
            var parent = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"parent\",\"lease_epoch\":1,\"details\":{\"steps\":[\"transport\"]," +
                "\"base_position\":{\"x\":40,\"y\":50,\"z\":60}}}", targetId: 42);
            point.ProcessIncomingMessage(1, parent);
            point.Tick();

            Assert.Contains("\"group_id\":\"Haulers\"", sent[sent.Count - 1]);
            Assert.Contains("\"steps\":[\"pickup\",\"deliver\"]", sent[sent.Count - 1]);
            Assert.Contains("\"pickup_grid_id\":500", sent[sent.Count - 1]);
            Assert.Contains("\"pickup_name\":\"Mining point\"", sent[sent.Count - 1]);
            Assert.Contains("\"pickup_position\":{\"x\":10,\"y\":20,\"z\":30}", sent[sent.Count - 1]);
            Assert.Contains("\"base_position\":{\"x\":40,\"y\":50,\"z\":60}", sent[sent.Count - 1]);
        }

        [Fact]
        public void HaulerMustConfirmPickupBeforeDeliveryAndResumesAfterRestart()
        {
            string storage = "";
            double? cargo = null;
            long dockedGrid = 0;
            var sent = new List<string>();
            TaskBusConsumer Start() => new TaskBusConsumer((message, _) => sent.Add(message),
                () => storage, value => storage = value, _ => { }, 42, "Hauler", 1, readHaulerCargo: resource => resource == "Ore/Iron" ? cargo : null,
                readDockedGrid: () => dockedGrid);
            var hauler = Start();
            hauler.ProcessCommand("group Haulers");
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"haul\",\"lease_epoch\":1,\"details\":{\"resource\":\"Ore/Iron\",\"amount\":50,\"pickup_grid_id\":500,\"base_grid_id\":600,\"steps\":[\"pickup\",\"deliver\"]}}", targetId: 42);
            hauler.ProcessIncomingMessage(1, assignment);
            hauler.ProcessCommand("stage deliver");
            Assert.Contains("Stage: pickup", hauler.GetStatus());

            hauler.ProcessCommand("stage pickup");
            Assert.Contains("Stage: pickup", hauler.GetStatus());
            dockedGrid = 500;
            cargo = 49;
            hauler.ProcessCommand("stage pickup");
            Assert.Contains("Stage: pickup", hauler.GetStatus());
            cargo = 75;
            dockedGrid = 600;
            hauler.ProcessCommand("stage pickup");
            Assert.Contains("Stage: pickup", hauler.GetStatus());
            dockedGrid = 500;
            hauler.ProcessCommand("stage pickup");
            hauler = Start();
            Assert.Contains("Stage: deliver", hauler.GetStatus());
            hauler.ProcessCommand("stage deliver");
            Assert.Contains("Pending completion: none", hauler.GetStatus());
            hauler.ProcessIncomingMessage(1, assignment);
            dockedGrid = 600;
            cargo = null;
            hauler.ProcessCommand("stage deliver");
            Assert.Contains("Pending completion: none", hauler.GetStatus());
            cargo = 26;
            hauler.ProcessCommand("stage deliver");
            Assert.Contains("Pending completion: none", hauler.GetStatus());
            dockedGrid = 500;
            cargo = 25;
            hauler.ProcessCommand("stage deliver");
            Assert.Contains("Pending completion: none", hauler.GetStatus());
            dockedGrid = 600;
            hauler.ProcessCommand("stage deliver");
            Assert.Contains("Pending completion: haul", hauler.GetStatus());
            Assert.Contains("TASK_DONE", sent[sent.Count - 1]);
        }

        [Fact]
        public void AutomaticHaulerAdvancesOnlyWithDockAndCargoEvidence()
        {
            var sent = new List<string>();
            double cargo = 50;
            long dock = 600;
            int unloadCalls = 0;
            var hauler = new TaskBusConsumer((message, _) => sent.Add(message), () => "", _ => { },
                _ => { }, 42, "Hauler", 1, readHaulerCargo: _ => cargo, readDockedGrid: () => dock,
                autoHauler: true, unloadHaulerCargo: (grid, resource, remaining) =>
                {
                    Assert.Equal(600, grid);
                    Assert.Equal("Ore/Iron", resource);
                    Assert.Equal(50, remaining);
                    unloadCalls++;
                    cargo -= remaining;
                });
            hauler.ProcessCommand("group Haulers");
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"haul\",\"lease_epoch\":1,\"details\":{\"resource\":\"Ore/Iron\",\"amount\":50," +
                "\"pickup_grid_id\":500,\"base_grid_id\":600,\"steps\":[\"pickup\",\"deliver\"]}}", targetId: 42);
            hauler.ProcessIncomingMessage(1, assignment);
            hauler.Tick();
            Assert.Contains("Stage: pickup", hauler.GetStatus());

            dock = 500;
            hauler.Tick();
            Assert.Contains("Stage: deliver", hauler.GetStatus());
            dock = 600;
            hauler.Tick();
            Assert.Contains("Pending completion: none", hauler.GetStatus());

            Assert.Equal(1, unloadCalls);
            hauler.Tick();
            Assert.Contains("Pending completion: haul", hauler.GetStatus());
            Assert.Contains("TASK_DONE", sent[sent.Count - 1]);
        }

        [Fact]
        public void SurveyLeaseRequiresConfirmationAfterRestart()
        {
            string storage = "";
            TaskBusConsumer Start() => new TaskBusConsumer((_, __) => { }, () => storage,
                value => storage = value, _ => { }, 42, "Platform", miningPlatform: true);
            var worker = Start();
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"survey-1\",\"lease_epoch\":1,\"group_id\":\"Scouts\"," +
                "\"details\":{\"resource\":\"Ore/Iron\",\"steps\":[\"survey\"]}}", targetId: 42);
            worker.ProcessIncomingMessage(1, assignment);
            Assert.True(worker.TryGetSurveyLease(out var lease, out var ore));
            Assert.Equal("Iron", ore);

            worker = Start();
            Assert.False(worker.TryGetSurveyLease(out _, out _));
            worker.ProcessIncomingMessage(1, assignment);
            Assert.True(worker.TryGetSurveyLease(out var restored, out _));
            Assert.Equal(lease, restored);
        }

        [Fact]
        public void SurveyLeaseDistinguishesBroadcastBrokersWithIdenticalAssignments()
        {
            var worker = new TaskBusConsumer((_, __) => { }, () => "", _ => { }, _ => { },
                42, "Platform", miningPlatform: true);
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"survey-1\",\"lease_epoch\":1,\"group_id\":\"Scouts\"," +
                "\"details\":{\"resource\":\"Ore/Iron\",\"steps\":[\"survey\"]}}", targetId: 42);

            worker.ProcessIncomingMessage(1, assignment);
            Assert.True(worker.TryGetSurveyLease(out var first, out _));
            worker.ProcessIncomingMessage(2, assignment);
            Assert.True(worker.TryGetSurveyLease(out var second, out _));

            Assert.NotEqual(first, second);
        }

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
        public void SurveyRetrySurvivesRestartAndFencesOldCompletion()
        {
            string storage = "";
            var sent = new List<string>();
            TaskBusConsumer Start() => new TaskBusConsumer((message, _) => sent.Add(message),
                () => storage, value => storage = value, _ => { }, 42, "Point", 1);
            var point = Start();
            point.ProcessCommand("group MiningPoints");
            point.ProcessCommand("point auto");
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"parent\",\"lease_epoch\":7,\"details\":{\"steps\":[\"survey\",\"mine\"]}}", targetId: 42);
            point.ProcessIncomingMessage(1, assignment);
            var oldCompletion = TaskBusProtocol.CreateEnvelope("TASK_PUBLISHED_ACK",
                "{\"request_id\":\"parent:7:0\",\"state\":\"DONE\"}", targetId: 42);
            point.ProcessCommand("survey retry");
            Assert.Contains("Child request: parent:7:0\n", point.GetStatus());
            Assert.True(point.ProcessIncomingMessage(1, oldCompletion));
            point.ProcessCommand("survey retry");
            Assert.Contains("\"request_id\":\"parent:7:0:retry:1\"", sent[sent.Count - 1]);

            point = Start();
            point.ProcessIncomingMessage(1, assignment);
            Assert.False(point.ProcessIncomingMessage(1, oldCompletion));
            point.Tick();
            Assert.Contains("\"request_id\":\"parent:7:0:retry:1\"", sent[sent.Count - 1]);
            Assert.Contains("Stage: survey", point.GetStatus());
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
                "{\"task_id\":\"parent\",\"lease_epoch\":7,\"details\":{\"site_required\":true,\"resource\":\"Ore/Iron\",\"ore_candidate_gps\":\"GPS:Iron:10:20:30:\",\"steps\":[\"survey\",\"mine\"]}}", targetId: 42);
            point.ProcessIncomingMessage(1, parent);
            point.Tick();
            var firstPublication = sent[sent.Count - 1];
            Assert.Contains("\"group_id\":\"Scouts\"", firstPublication);
            Assert.Contains("\"parent_lease_epoch\":7", firstPublication);
            Assert.Contains("\"ore_candidate_gps\":\"GPS:Iron:10:20:30:\"", firstPublication);
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
            point.ProcessCommand("site report");
            point.ProcessCommand("stage survey");
            Assert.Contains("Stage: survey", point.GetStatus());
            point = Start();
            point.ProcessIncomingMessage(1, parent);
            point.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_PUBLISHED_ACK",
                "{\"request_id\":\"parent:7:0\",\"state\":\"DONE\"," +
                "\"survey_report\":\"1 positive sample. GPS:Iron:10:20:30: | GPS:Observation:10:20:60:\"}", targetId: 42));
            point = Start();
            Assert.Contains("Child status: DONE", point.GetStatus());
            point.ProcessIncomingMessage(1, parent);
            point.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_PUBLISHED_ACK",
                "{\"request_id\":\"parent:7:0\",\"state\":\"ACTIVE\"}", targetId: 42));
            Assert.Contains("Child status: DONE", point.GetStatus());
            point.ProcessCommand("site report");
            point.ProcessCommand("approach report");
            point.ProcessCommand("site remember");
            point.ProcessCommand("stage survey");
            Assert.Contains("Stage: mine", point.GetStatus());
            point.Tick();
            Assert.Contains("\"group_id\":\"MiningFleet\"", sent[sent.Count - 1]);
            Assert.DoesNotContain("ore_candidate_gps", sent[sent.Count - 1]);
            Assert.Contains("\"ore_position\":{\"x\":10,\"y\":20,\"z\":30}", sent[sent.Count - 1]);
            Assert.Contains("\"approach_position\":{\"x\":10,\"y\":20,\"z\":60}", sent[sent.Count - 1]);
            point.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_PUBLISHED_ACK",
                "{\"request_id\":\"parent:7:1\",\"state\":\"DONE\"}", targetId: 42));
            point.ProcessCommand("stage mine");
            point.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_DONE_ACK",
                "{\"task_id\":\"parent\",\"lease_epoch\":7}", targetId: 42));

            point = Start();
            point.ProcessIncomingMessage(1, parent.Replace("\"parent\"", "\"next\""));
            Assert.Contains("Stage: mine", point.GetStatus());
            point.Tick();
            Assert.Contains("\"group_id\":\"MiningFleet\"", sent[sent.Count - 1]);
            Assert.Contains("\"request_id\":\"next:7:1\"", sent[sent.Count - 1]);
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
