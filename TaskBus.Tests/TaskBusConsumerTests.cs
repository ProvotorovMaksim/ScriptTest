using System.Collections.Generic;
using System.Linq;
using SpaceEngineers.Libs.IgcCore;
using SpaceEngineers.Libs.TaskBusConsumer;
using Xunit;

namespace TaskBus.Tests
{
    public sealed class TaskBusConsumerTests
    {
        [Fact]
        public void RestartedConsumerRequiresANewerLeaseConfirmation()
        {
            string storage = "";
            TaskBusConsumer Start() => new TaskBusConsumer((_, __) => { }, () => storage,
                value => storage = value, _ => { }, 42, "Worker");
            var worker = Start();
            long deadline = System.DateTime.UtcNow.AddSeconds(20).Ticks;
            string Assignment(long expires) => TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"task\",\"lease_epoch\":1,\"lease_expires_utc_ticks\":" + expires +
                ",\"details\":{\"steps\":[\"execute\"]}}", targetId: 42);

            worker.ProcessIncomingMessage(1, Assignment(deadline));
            Assert.Contains("Lease confirmed", worker.GetStatus());

            worker = Start();
            worker.ProcessIncomingMessage(1, Assignment(deadline));
            worker.ProcessCommand("stage execute");
            Assert.Contains("Waiting for lease confirmation", worker.GetStatus());
            Assert.Contains("Pending completion: none", worker.GetStatus());

            worker.ProcessIncomingMessage(1, Assignment(deadline + 1));
            worker.ProcessCommand("stage execute");
            Assert.Contains("Pending completion: task", worker.GetStatus());
        }

        [Fact]
        public void ConsumerIgnoresExpiredAssignmentAndOlderLeaseRefresh()
        {
            var worker = new TaskBusConsumer((_, __) => { }, () => "", _ => { }, _ => { }, 42, "Point", 1);
            worker.ProcessCommand("group MiningPoints");
            worker.ProcessCommand("point auto");
            string Assignment(long deadline, bool receipt) => TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"parent\",\"lease_epoch\":1,\"lease_expires_utc_ticks\":" + deadline +
                ",\"base_stock_confirmed\":" + (receipt ? "true" : "false") +
                ",\"details\":{\"receipt_required\":true,\"steps\":[\"transport\"]}}", targetId: 42);

            worker.ProcessIncomingMessage(1, Assignment(1, true));
            Assert.False(worker.HasActiveTask);

            long later = System.DateTime.UtcNow.AddSeconds(30).Ticks;
            worker.ProcessIncomingMessage(1, Assignment(later, true));
            worker.ProcessIncomingMessage(1, Assignment(later - 1, false));
            Assert.Contains("Base stock confirmed", worker.GetStatus());
            Assert.Contains("Lease confirmed", worker.GetStatus());
        }

        [Fact]
        public void AutomaticPointRemembersMinedSiteAndDispatchesHaulerOnlyWithEnoughBufferStock()
        {
            string storage = "";
            double? stock = 100;
            var sent = new List<string>();
            TaskBusConsumer Start() => new TaskBusConsumer((message, _) => sent.Add(message), () => storage,
                value => storage = value, _ => { }, 42, "Point", 1,
                readMiningBuffer: resource => resource == "Ore/Iron" ? stock : null, autoAcceptSurvey: true);
            var point = Start();
            point.ProcessCommand("group MiningPoints");
            point.ProcessCommand("point auto");
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"parent\",\"lease_epoch\":1,\"details\":{\"resource\":\"Ore/Iron\"," +
                "\"amount\":100,\"steps\":[\"survey\",\"mine\",\"transport\"]}}", targetId: 42);
            point.ProcessIncomingMessage(1, assignment);
            point.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_PUBLISHED_ACK",
                "{\"request_id\":\"parent:1:0\",\"state\":\"DONE\",\"survey_report\":" +
                "\"1 positive sample. GPS:Iron:10:20:30: | GPS:Observation:10:20:60:\"}", targetId: 42));
            point.Tick();
            Assert.Contains("Stage: mine", point.GetStatus());
            Assert.DoesNotContain("KNOWN_SITE|", storage);
            stock = 99;
            point.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_PUBLISHED_ACK",
                "{\"request_id\":\"parent:1:1\",\"state\":\"DONE\"}", targetId: 42));
            point.Tick();
            point.ProcessCommand("stage mine");
            Assert.Contains("Stage: mine", point.GetStatus());
            Assert.DoesNotContain("KNOWN_SITE|", storage);

            stock = 100;
            point.Tick();
            Assert.Contains("KNOWN_SITE|", storage);
            point = Start();
            point.ProcessIncomingMessage(1, assignment);
            Assert.Contains("Stage: transport", point.GetStatus());
            point.Tick();
            Assert.Contains("\"group_id\":\"Haulers\"", sent[sent.Count - 1]);
        }

        [Fact]
        public void MiningDepositIsRestoredOnlyForTheSameConfirmedLease()
        {
            string storage = "";
            TaskBusConsumer Start() => new TaskBusConsumer((_, __) => { }, () => storage,
                value => storage = value, _ => { }, 42, "Platform", miningPlatform: true);
            var platform = Start();
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"mine\",\"lease_epoch\":1,\"group_id\":\"MiningFleet\"," +
                "\"details\":{\"resource\":\"Ore/Iron\",\"amount\":100,\"steps\":[\"mine\"]}}", targetId: 42);
            platform.ProcessIncomingMessage(1, assignment);
            platform.RecordMiningDeposit(60);
            platform = Start();
            Assert.Equal(0, platform.MiningDeposited);
            platform.ProcessIncomingMessage(1, assignment);
            Assert.Equal(60, platform.MiningDeposited);
            platform.RecordMiningDeposit(60);
            Assert.Equal(100, platform.MiningDeposited);
            platform.ProcessIncomingMessage(1, assignment.Replace("\"lease_epoch\":1", "\"lease_epoch\":2"));
            Assert.Equal(0, platform.MiningDeposited);
        }

        [Fact]
        public void PlatformUsesMiningPointOnlyUnderAConfirmedLease()
        {
            var platform = new TaskBusConsumer((_, __) => { }, () => "", _ => { }, _ => { },
                42, "Platform", miningPlatform: true);
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"mine\",\"lease_epoch\":1,\"group_id\":\"MiningFleet\"," +
                "\"details\":{\"mining_point_grid_id\":500,\"steps\":[\"mine\"]}}", targetId: 42);
            platform.ProcessIncomingMessage(1, assignment);
            Assert.True(platform.TryGetMiningPoint(out var grid));
            Assert.Equal(500, grid);
            platform.Tick(false);
            Assert.False(platform.TryGetMiningPoint(out _));
        }

        [Fact]
        public void MiningCompletionRequiresTheFullDepositUnderTheCurrentLease()
        {
            var platform = new TaskBusConsumer((_, __) => { }, () => "", _ => { }, _ => { },
                42, "Platform", miningPlatform: true);
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"mine\",\"lease_epoch\":1,\"group_id\":\"MiningFleet\"," +
                "\"details\":{\"resource\":\"Ore/Iron\",\"amount\":100,\"steps\":[\"mine\"]}}", targetId: 42);
            platform.ProcessIncomingMessage(1, assignment);
            platform.RecordMiningDeposit(99);
            Assert.False(platform.TryCompleteMiningDeposit());
            platform.RecordMiningDeposit(1);
            platform.Tick(false);
            Assert.False(platform.TryCompleteMiningDeposit());
            platform.ProcessIncomingMessage(1, assignment);
            Assert.True(platform.TryCompleteMiningDeposit());
            Assert.Contains("Pending completion: mine", platform.GetStatus());
        }

        [Fact]
        public void SupervisedCruiseRemoteSurvivesRestartEvenWithoutAnActiveTask()
        {
            string storage = "";
            TaskBusConsumer Start() => new TaskBusConsumer((_, __) => { }, () => storage,
                value => storage = value, _ => { }, 42, "Hauler");
            var worker = Start();
            worker.SetSupervisedCruiseRemote(123);
            worker = Start();
            Assert.Equal(123, worker.SupervisedCruiseRemote);
            worker.SetSupervisedCruiseRemote(0);
            Assert.Equal(0, Start().SupervisedCruiseRemote);
        }

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
            Assert.True(hauler.TryGetHaulerDestination(out _, out var pickup, out var grid));
            Assert.True(pickup);
            Assert.Equal(500, grid);
            Assert.False(hauler.CanDepartHaulerPickup());
            Assert.False(hauler.CanDepartHaulerBase());
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
            Assert.True(hauler.CanDepartHaulerBase());
            hauler.ProcessCommand("stage pickup");
            Assert.Contains("Stage: pickup", hauler.GetStatus());
            dockedGrid = 500;
            hauler.ProcessCommand("stage pickup");
            hauler = Start();
            Assert.Contains("Stage: deliver", hauler.GetStatus());
            Assert.False(hauler.TryGetHaulerDestination(out _, out _, out _));
            Assert.False(hauler.CanDepartHaulerPickup());
            Assert.False(hauler.CanDepartHaulerBase());
            hauler.ProcessCommand("stage deliver");
            Assert.Contains("Pending completion: none", hauler.GetStatus());
            hauler.ProcessIncomingMessage(1, assignment);
            Assert.True(hauler.TryGetHaulerDestination(out _, out pickup, out grid));
            Assert.False(pickup);
            Assert.Equal(600, grid);
            Assert.True(hauler.CanDepartHaulerPickup());
            dockedGrid = 600;
            Assert.False(hauler.CanDepartHaulerPickup());
            Assert.False(hauler.CanDepartHaulerBase());
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
            double cargo = 20;
            long dock = 600;
            int unloadCalls = 0;
            int loadCalls = 0;
            string storage = "";
            TaskBusConsumer Start() => new TaskBusConsumer((message, _) => sent.Add(message),
                () => storage, value => storage = value,
                _ => { }, 42, "Hauler", 1, readHaulerCargo: _ => cargo, readDockedGrid: () => dock,
                autoHauler: true, unloadHaulerCargo: (grid, resource, remaining) =>
                {
                    Assert.Equal(600, grid);
                    Assert.Equal("Ore/Iron", resource);
                    bool first = unloadCalls++ == 0;
                    Assert.Equal(first ? 50 : 30, remaining);
                    double transferred = System.Math.Min(cargo, first ? 20 : remaining);
                    cargo -= transferred;
                    return transferred;
                }, loadHaulerCargo: (grid, resource, remaining) =>
                {
                    Assert.Equal(500, grid);
                    Assert.Equal("Ore/Iron", resource);
                    Assert.Equal(30, remaining);
                    loadCalls++;
                    cargo += remaining;
                });
            var hauler = Start();
            hauler.ProcessCommand("group Haulers");
            var assignment = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"haul\",\"lease_epoch\":1,\"details\":{\"resource\":\"Ore/Iron\",\"amount\":50," +
                "\"pickup_grid_id\":500,\"base_grid_id\":600,\"steps\":[\"pickup\",\"deliver\"]}}", targetId: 42);
            hauler.ProcessIncomingMessage(1, assignment);
            hauler.Tick();
            Assert.Contains("Stage: pickup", hauler.GetStatus());
            Assert.Equal(0, loadCalls);

            dock = 500;
            hauler.Tick();
            Assert.Equal(1, loadCalls);
            Assert.Contains("Stage: pickup", hauler.GetStatus());
            hauler.Tick();
            Assert.Equal(1, loadCalls);
            Assert.Contains("Stage: deliver", hauler.GetStatus());
            dock = 600;
            hauler.Tick();
            Assert.Contains("Pending completion: none", hauler.GetStatus());

            Assert.Equal(1, unloadCalls);
            Assert.Contains("HAULER_DELIVERED|20", storage);
            cargo = 0; // Cargo disappeared without reaching the base buffer.
            hauler = Start();
            hauler.Tick();
            Assert.Equal(1, unloadCalls);
            hauler.ProcessIncomingMessage(1, assignment);
            hauler.Tick();
            Assert.Contains("Pending completion: none", hauler.GetStatus());
            Assert.Equal(2, unloadCalls);
            cargo = 30;
            hauler.Tick();
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
                "\"details\":{\"resource\":\"Ore/Iron\",\"ore_candidate_gps\":\"GPS:Iron:10:20:30:\",\"steps\":[\"survey\"]}}", targetId: 42);
            worker.ProcessIncomingMessage(1, assignment);
            Assert.True(worker.TryGetSurveyLease(out var lease, out var ore));
            Assert.Equal("Iron", ore);
            Assert.True(worker.TryGetSurveyCandidate(out var x, out var y, out var z));
            Assert.Equal((10d, 20d, 30d), (x, y, z));

            worker = Start();
            Assert.False(worker.TryGetSurveyLease(out _, out _));
            Assert.False(worker.TryGetSurveyCandidate(out _, out _, out _));
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

        [Theory]
        [InlineData("\n")]
        [InlineData("\r\n")]
        public void SurveyRetrySurvivesRestartAndFencesOldCompletion(string lineEnding)
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
            Assert.Contains("Child request: parent:7:0" + System.Environment.NewLine, point.GetStatus());
            Assert.True(point.ProcessIncomingMessage(1, oldCompletion));
            point.ProcessCommand("survey retry");
            Assert.Contains("\"request_id\":\"parent:7:0:retry:1\"", sent[sent.Count - 1]);

            storage = storage.Replace("\r\n", "\n").Replace("\n", lineEnding);
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
                () => storage, value => storage = value, _ => { }, 42, "Point", 1, gridId: 500);

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
            Assert.Contains("\"mining_point_grid_id\":500", sent[sent.Count - 1]);
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

        [Theory]
        [InlineData(true, "GPS:Iron:10:20:30: | GPS:Observation:10:20:60:", true)]
        [InlineData(false, "GPS:Iron:10:20:30: | GPS:Observation:10:20:60:", false)]
        [InlineData(true, "GPS:Gold:10:20:30: | GPS:Observation:10:20:60:", false)]
        [InlineData(true, "GPS:Iron:10:20:30: | GPS:Observation:10:20:30:", false)]
        [InlineData(true, "GPS:Iron:10:20:30:", false)]
        public void AutomaticPointRequiresACompleteMatchingSurveyReport(bool enabled, string gps, bool advance)
        {
            string storage = "";
            var sent = new List<string>();
            var point = new TaskBusConsumer((message, _) => sent.Add(message),
                () => storage, value => storage = value, _ => { }, 42, "Point", 1,
                gridId: 500, autoAcceptSurvey: enabled);
            point.ProcessCommand("group MiningPoints");
            point.ProcessCommand("point auto");
            point.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"parent\",\"lease_epoch\":7,\"details\":{\"site_required\":true," +
                "\"resource\":\"Ore/Iron\",\"steps\":[\"survey\",\"mine\"]}}", targetId: 42));
            point.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope("TASK_PUBLISHED_ACK",
                "{\"request_id\":\"parent:7:0\",\"state\":\"DONE\",\"survey_report\":" +
                TaskBusIgcHelper.ToJsonString("1 positive sample. " + gps) + "}", targetId: 42));
            point.Tick();
            Assert.Contains(advance ? "Stage: mine" : "Stage: survey", point.GetStatus());
            if (advance)
            {
                point.Tick();
                Assert.Contains("\"approach_position\":{\"x\":10,\"y\":20,\"z\":60}", sent[sent.Count - 1]);
            }
        }

        [Theory]
        [InlineData(65, true)]
        [InlineData(64, false)]
        [InlineData(0, false)]
        public void PointUsesTheClosestStoredEntryOnlyWithinRadius(double radius, bool shouldReuse)
        {
            string storage = "";
            var sent = new List<string>();
            TaskBusConsumer Start() => new TaskBusConsumer((message, _) => sent.Add(message),
                () => storage, value => storage = value, _ => { }, 42, "Point", 1,
                getGridPositionJson: () => "{\"x\":0,\"y\":0,\"z\":0}", gridId: 500, knownSiteRadius: radius);
            var point = Start();
            point.ProcessCommand("group MiningPoints");
            point.ProcessCommand("point auto");
            point.ProcessCommand("site add GPS:Iron:10:20:30: | GPS:Entry:10:20:30:");
            Assert.DoesNotContain("KNOWN_SITE|", storage);
            point.ProcessCommand("site add GPS:Iron:10:20:30:");
            Assert.DoesNotContain("KNOWN_SITE|", storage);
            point.ProcessCommand("site add GPS:Iron:10:20:30: | GPS:Entry:10:20:60:");
            point.ProcessCommand("site add GPS:Iron:10:20:80: | GPS:Entry:10:20:100:");
            point.ProcessCommand("site add GPS:Iron:1:1:1: | GPS:Entry:2:2:2:");
            point.ProcessCommand("site forget GPS:Iron:1:1:1:");
            Assert.Equal(2, storage.Split('\n').Count(line => line.StartsWith("KNOWN_SITE|")));

            point = Start();
            var parent = TaskBusProtocol.CreateEnvelope("TASK_ASSIGN",
                "{\"task_id\":\"imported\",\"lease_epoch\":7,\"details\":{\"site_required\":true," +
                "\"resource\":\"Ore/Iron\",\"steps\":[\"survey\",\"mine\"]}}", targetId: 42);
            Assert.True(point.ProcessIncomingMessage(1, parent));
            Assert.Contains(shouldReuse ? "Stage: mine" : "Stage: survey", point.GetStatus());
            point.Tick();
            var publication = sent[sent.Count - 1];
            Assert.Contains(shouldReuse ? "\"group_id\":\"MiningFleet\"" : "\"group_id\":\"Scouts\"", publication);
            if (shouldReuse)
                Assert.Contains("\"approach_position\":{\"x\":10,\"y\":20,\"z\":60}", publication);
            else
                Assert.DoesNotContain("approach_position", publication);
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
