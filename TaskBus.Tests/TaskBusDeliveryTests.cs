using System.Collections.Generic;
using System.Linq;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Libs.IgcCore;
using SpaceEngineers.Libs.TaskBusConsumer;
using Xunit;
using Broker = SpaceEngineers.Services.Broker.Program;

namespace TaskBus.Tests
{
    public sealed class TaskBusDeliveryTests
    {
        [Fact]
        public void BrokerPauseSurvivesRestartAndFencesThePreviousWorker()
        {
            var broker = StartBroker("");
            Dispatch(broker, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"ore\"}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            var poll = TaskBusProtocol.CreateEnvelope("TASK_POLL", "{\"group_id\":\"default\"}", targetId: 1);
            Dispatch(broker, 42, poll);
            TakeResponse(broker, "TASK_ASSIGN", 42);

            broker.Main("broker pause");
            Assert.Contains("PAUSED|1", broker.Storage);
            Assert.Contains("|QUEUED|", broker.Storage);
            broker = StartBroker(broker.Storage);
            Dispatch(broker, 43, poll);
            Assert.Empty(broker.IGC.Outbox);

            broker.Main("broker resume");
            Assert.Contains("PAUSED|1", broker.Storage);
            broker = StartBroker(AfterResumeDeadline(broker.Storage));
            broker.Main("broker resume");
            var oldDone = TaskBusProtocol.CreateEnvelope("TASK_DONE",
                "{\"task_id\":\"9-1\",\"lease_epoch\":1}", targetId: 1);
            Dispatch(broker, 42, oldDone);
            TakeResponse(broker, "TASK_REVOKED", 42);
            Dispatch(broker, 43, poll);
            Assert.Contains("\"lease_epoch\":3", TakeResponse(broker, "TASK_ASSIGN", 43).PayloadJson);
        }

        [Fact]
        public void BrokerRestartDoesNotRenewExpiredWallClockLease()
        {
            var broker = StartBroker("");
            Dispatch(broker, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"ore\"}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            var poll = TaskBusProtocol.CreateEnvelope("TASK_POLL", "{\"group_id\":\"default\"}", targetId: 1);
            Dispatch(broker, 42, poll);
            TakeResponse(broker, "TASK_ASSIGN", 42);
            broker = StartBroker(WithExpiredLease(broker.Storage, "9-1"));
            var oldDone = TaskBusProtocol.CreateEnvelope("TASK_DONE",
                "{\"task_id\":\"9-1\",\"lease_epoch\":1}", targetId: 1);
            Dispatch(broker, 42, oldDone);
            TakeResponse(broker, "TASK_REVOKED", 42);
            Dispatch(broker, 43, poll);
            var newLease = TakeResponse(broker, "TASK_ASSIGN", 43);
            Assert.Contains("\"lease_epoch\":3", newLease.PayloadJson);
        }

        [Fact]
        public void ReceiptDoesNotExtendLeaseWithoutATaskAssignment()
        {
            var broker = StartBroker("");
            Dispatch(broker, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"ore\"}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);

            var poll = TaskBusProtocol.CreateEnvelope("TASK_POLL", "{\"group_id\":\"default\"}", targetId: 1);
            Dispatch(broker, 42, poll);
            var assignment = TakeResponse(broker, "TASK_ASSIGN", 42);
            Assert.True(TaskBusIgcHelper.TryGetJsonProperty(assignment.PayloadJson, "lease_expires_utc_ticks", out var deadline));
            var received = TaskBusProtocol.CreateEnvelope("TASK_RECEIVED",
                "{\"task_id\":\"9-1\",\"lease_epoch\":1}", targetId: 1);

            Dispatch(broker, 42, received);
            Assert.Contains("|ACTIVE|", broker.Storage);
            Assert.Contains("|" + deadline + "|", broker.Storage);
            var committedReceipt = broker.Storage;

            broker = StartBroker(committedReceipt);
            Dispatch(broker, 42, received);
            Assert.Equal(committedReceipt, broker.Storage);

            Dispatch(broker, 42, poll);
            var renewal = TakeResponse(broker, "TASK_ASSIGN", 42);
            Assert.True(TaskBusIgcHelper.TryGetJsonProperty(renewal.PayloadJson, "lease_expires_utc_ticks", out var updatedDeadline));
            Assert.True(long.Parse(updatedDeadline) > long.Parse(deadline));
            Assert.Contains("|" + updatedDeadline + "|", broker.Storage);
        }

        [Fact]
        public void LostPublishAndCompletionAcksRecoverAcrossRestarts()
        {
            var broker = StartBroker("");
            string workerStorage = "";
            TaskBusConsumer StartWorker() => new TaskBusConsumer(
                (message, _) => broker.IGC.Inbox.Enqueue((42, message)),
                () => workerStorage, value => workerStorage = value, _ => { },
                42, "Worker", 1);

            var publish = TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"ore-1\",\"group_id\":\"MiningFleet\",\"key\":\"Ore/Iron\",\"steps\":[\"survey\",\"deliver\"]}",
                targetId: 1);
            Dispatch(broker, 9, publish);
            var firstAck = TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            broker = StartBroker(broker.Storage);
            Dispatch(broker, 9, publish);
            Assert.Equal(firstAck.PayloadJson, TakeResponse(broker, "TASK_PUBLISHED_ACK", 9).PayloadJson);
            Assert.Single(broker.Storage.Split('\n').Where(line => line.StartsWith("TASK|")));

            var worker = StartWorker();
            worker.ProcessCommand("broker 1");
            worker.ProcessCommand("group MiningFleet");
            worker.Tick();
            broker.Main("");
            var assignment = TakeResponse(broker, "TASK_ASSIGN", 42);
            DeliverToWorker(worker, assignment);
            worker.ProcessCommand("stage survey");

            worker = StartWorker();
            Assert.Contains("Stage: deliver", worker.GetStatus());
            worker.Tick();
            broker.Main("");
            DeliverToWorker(worker, TakeResponse(broker, "TASK_ASSIGN", 42));
            worker.ProcessCommand("stage deliver");
            broker.Main("");
            TakeResponse(broker, "TASK_DONE_ACK", 42);

            broker = StartBroker(broker.Storage);
            worker = StartWorker();
            Assert.Contains("Pending completion: 9-1", worker.GetStatus());
            var replacement = StartBroker(broker.Storage, 2);
            var source = broker;
            broker = replacement;
            worker.ProcessCommand("broker 2");
            Assert.Contains("Broker: 1", worker.GetStatus());
            worker.ProcessCommand("broker restored 2");
            Assert.Contains("Broker: 2", StartWorker().GetStatus());
            broker.Main("");
            var acknowledgement = TakeResponse(broker, "TASK_DONE_ACK", 42);
            var reply = TaskBusProtocol.CreateEnvelope(acknowledgement.EventType,
                acknowledgement.PayloadJson, targetId: 42);
            Assert.False(worker.ProcessIncomingMessage(1, reply));
            Assert.True(worker.ProcessIncomingMessage(2, reply));
            Assert.Contains("Pending completion: none", StartWorker().GetStatus());
            broker = source;

            Dispatch(broker, 9, publish);
            Assert.Contains("\"state\":\"DONE\"", TakeResponse(broker, "TASK_PUBLISHED_ACK", 9).PayloadJson);
            Assert.DoesNotContain("TASK|", broker.Storage);
        }

        [Fact]
        public void LostCancellationAckDoesNotReopenTheRequestAfterRestart()
        {
            var broker = StartBroker("");
            var publish = TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"ore-2\",\"key\":\"Ore/Iron\"}", targetId: 1);
            var cancel = TaskBusProtocol.CreateEnvelope("TASK_CANCEL",
                "{\"request_id\":\"ore-2\"}", targetId: 1);

            Dispatch(broker, 9, publish);
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            Dispatch(broker, 9, cancel);
            TakeResponse(broker, "TASK_CANCEL_ACK", 9);
            Assert.DoesNotContain("TASK|", broker.Storage);

            broker = StartBroker(broker.Storage);
            Dispatch(broker, 9, cancel);
            TakeResponse(broker, "TASK_CANCEL_ACK", 9);
            Dispatch(broker, 9, publish);
            Assert.Contains("\"state\":\"CANCELLED\"", TakeResponse(broker, "TASK_PUBLISHED_ACK", 9).PayloadJson);
            Assert.DoesNotContain("TASK|", broker.Storage);
        }

        [Fact]
        public void ExpiredWorkerCannotCommitOrResumeLeaseAfterAnotherWorkerTakesOver()
        {
            var broker = StartBroker("");
            string firstStorage = "";
            string secondStorage = "";
            TaskBusConsumer StartFirst() => new TaskBusConsumer(
                (message, _) => broker.IGC.Inbox.Enqueue((42, message)),
                () => firstStorage, value => firstStorage = value, _ => { }, 42, "First", 1);
            var second = new TaskBusConsumer(
                (message, _) => broker.IGC.Inbox.Enqueue((43, message)),
                () => secondStorage, value => secondStorage = value, _ => { }, 43, "Second", 1);

            var publication = TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"ore-3\",\"key\":\"Ore/Iron\"}", targetId: 1);
            Dispatch(broker, 9, publication);
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);

            var first = StartFirst();
            first.Tick();
            broker.Main("");
            var earlyAssignment = TakeResponse(broker, "TASK_ASSIGN", 42);
            DeliverToWorker(first, earlyAssignment);
            broker.Main("");
            first.ProcessCommand("stage execute");
            var delayedCompletion = broker.IGC.Inbox.Dequeue();

            for (int tick = 0; tick < 181; tick++)
                broker.Main("");
            second.Tick();
            broker.Main("");
            TakeResponse(broker, "QUEUE_EMPTY", 43);

            broker = StartBroker(WithExpiredLease(broker.Storage, "9-1"));
            second.Tick();
            broker.Main("");
            DeliverToWorker(second, TakeResponse(broker, "TASK_ASSIGN", 43));
            broker.Main("");

            Dispatch(broker, delayedCompletion.Source, (string)delayedCompletion.Data);
            DeliverToWorker(first, TakeResponse(broker, "TASK_REVOKED", 42));
            first = StartFirst();
            Assert.Contains("Pending completion: none", first.GetStatus());
            DeliverToWorker(first, earlyAssignment);
            Assert.False(first.HasActiveTask);
            Assert.True(second.HasActiveTask);
            Assert.Contains("TASK|", broker.Storage);

            second.ProcessCommand("stage execute");
            broker.Main("");
            DeliverToWorker(second, TakeResponse(broker, "TASK_DONE_ACK", 43));
            Assert.Contains("Pending completion: none", second.GetStatus());
            Assert.DoesNotContain("TASK|", broker.Storage);
        }

        [Fact]
        public void MiningPointCanPublishChildOnlyWithCurrentParentLease()
        {
            var broker = StartBroker("");
            Dispatch(broker, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"parent\",\"group_id\":\"MiningPoints\"}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);

            Dispatch(broker, 42, TaskBusProtocol.CreateEnvelope("TASK_POLL",
                "{\"group_id\":\"MiningPoints\"}", targetId: 1));
            var parent = TakeResponse(broker, "TASK_ASSIGN", 42);
            Assert.Contains("\"task_id\":\"9-1\"", parent.PayloadJson);
            Assert.Contains("\"lease_epoch\":1", parent.PayloadJson);

            string Child(string id, int epoch) => TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"" + id + "\",\"group_id\":\"Scouts\",\"parent_task_id\":\"9-1\"," +
                "\"parent_lease_epoch\":" + epoch + "}", targetId: 1);

            Dispatch(broker, 43, Child("wrong-worker", 1));
            Assert.Contains("PARENT_LEASE_INVALID", TakeResponse(broker, "TASK_REJECTED", 43).PayloadJson);
            Dispatch(broker, 42, Child("wrong-epoch", 0));
            Assert.Contains("PARENT_LEASE_INVALID", TakeResponse(broker, "TASK_REJECTED", 42).PayloadJson);
            Dispatch(broker, 42, Child("survey", 1));
            Assert.Contains("\"state\":\"QUEUED\"", TakeResponse(broker, "TASK_PUBLISHED_ACK", 42).PayloadJson);

            var query = TaskBusProtocol.CreateEnvelope("TASK_QUERY", "{\"request_id\":\"parent\"}", targetId: 1);
            Dispatch(broker, 9, query);
            var status = TakeResponse(broker, "TASK_PUBLISHED_ACK", 9).PayloadJson;
            Assert.Contains("\"phase\":\"survey\"", status);
            Assert.Contains("\"child_state\":\"QUEUED\"", status);
            Dispatch(broker, 43, query);
            Assert.DoesNotContain("\"phase\"", TakeResponse(broker, "TASK_PUBLISHED_ACK", 43).PayloadJson);

            Dispatch(broker, 44, TaskBusProtocol.CreateEnvelope("TASK_POLL",
                "{\"group_id\":\"Scouts\"}", targetId: 1));
            TakeResponse(broker, "TASK_ASSIGN", 44);
            Dispatch(broker, 44, TaskBusProtocol.CreateEnvelope("TASK_DONE",
                "{\"task_id\":\"42-2\",\"lease_epoch\":1}", targetId: 1));
            TakeResponse(broker, "TASK_DONE_ACK", 44);
            broker = StartBroker(broker.Storage);
            Dispatch(broker, 9, query);
            var completed = TakeResponse(broker, "TASK_PUBLISHED_ACK", 9).PayloadJson;
            Assert.Contains("\"phase\":\"survey\"", completed);
            Assert.Contains("\"child_state\":\"DONE\"", completed);

            Dispatch(broker, 42, Child("survey-retry", 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 42);
            Dispatch(broker, 9, query);
            Assert.Contains("\"child_state\":\"QUEUED\"", TakeResponse(broker, "TASK_PUBLISHED_ACK", 9).PayloadJson);
        }

        [Fact]
        public void ExpiredParentRevokesActiveChildAndPersistsCancellation()
        {
            var broker = StartBroker("");
            Dispatch(broker, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"parent\",\"group_id\":\"MiningPoints\"}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            Dispatch(broker, 42, TaskBusProtocol.CreateEnvelope("TASK_POLL",
                "{\"group_id\":\"MiningPoints\"}", targetId: 1));
            var parent = TakeResponse(broker, "TASK_ASSIGN", 42);

            var child = TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"scout\",\"group_id\":\"Scouts\",\"parent_task_id\":\"9-1\"," +
                "\"parent_lease_epoch\":1}", targetId: 1);
            Dispatch(broker, 42, child);
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 42);
            var scoutPoll = TaskBusProtocol.CreateEnvelope("TASK_POLL", "{\"group_id\":\"Scouts\"}", targetId: 1);
            Dispatch(broker, 43, scoutPoll);
            var assignment = TakeResponse(broker, "TASK_ASSIGN", 43);
            Assert.Contains("\"task_id\":\"42-2\"", assignment.PayloadJson);

            Assert.True(TaskBusProtocol.TryGetProperty(parent.PayloadJson, "lease_expires_utc_ticks", out var parentDeadline));
            Assert.True(TaskBusProtocol.TryGetProperty(assignment.PayloadJson, "lease_expires_utc_ticks", out var childDeadline));
            Assert.Equal(long.Parse(parentDeadline), long.Parse(childDeadline));

            broker = StartBroker(WithExpiredLease(broker.Storage, "9-1"));
            Assert.Contains("\"task_id\":\"42-2\"", TakeResponse(broker, "TASK_REVOKED", 43).PayloadJson);

            broker = StartBroker(broker.Storage);
            Dispatch(broker, 42, child);
            Assert.Contains("\"state\":\"CANCELLED\"", TakeResponse(broker, "TASK_PUBLISHED_ACK", 42).PayloadJson);
            Dispatch(broker, 43, scoutPoll);
            TakeResponse(broker, "QUEUE_EMPTY", 43);
        }

        [Fact]
        public void CommittedMiningChildSharesOreCandidateWithAnotherPointAfterRestart()
        {
            var broker = StartBroker("");
            string Request(string id) => TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"" + id + "\",\"group_id\":\"MiningPoints\",\"resource\":\"Ore/Iron\"}", targetId: 1);
            var poll = TaskBusProtocol.CreateEnvelope("TASK_POLL",
                "{\"group_id\":\"MiningPoints\",\"point_position\":{\"x\":0,\"y\":0,\"z\":0}}", targetId: 1);
            Dispatch(broker, 9, Request("first"));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            Dispatch(broker, 40, poll);
            TakeResponse(broker, "TASK_ASSIGN", 40);
            Dispatch(broker, 40, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"mine\",\"group_id\":\"MiningFleet\",\"parent_task_id\":\"9-1\"," +
                "\"parent_lease_epoch\":1,\"resource\":\"Ore/Iron\",\"ore_position\":{\"x\":10,\"y\":20,\"z\":30}}",
                targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 40);
            Assert.DoesNotContain("DEPOSIT|", broker.Storage);
            Dispatch(broker, 44, TaskBusProtocol.CreateEnvelope("TASK_POLL",
                "{\"group_id\":\"MiningFleet\"}", targetId: 1));
            TakeResponse(broker, "TASK_ASSIGN", 44);
            Dispatch(broker, 44, TaskBusProtocol.CreateEnvelope("TASK_DONE",
                "{\"task_id\":\"40-2\",\"lease_epoch\":1}", targetId: 1));
            TakeResponse(broker, "TASK_DONE_ACK", 44);

            broker = StartBroker(broker.Storage);
            Dispatch(broker, 9, Request("second"));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            Dispatch(broker, 41, poll);
            Assert.Contains("\"shared_ore_gps\":\"GPS:Iron:10:20:30:\"",
                TakeResponse(broker, "TASK_ASSIGN", 41).PayloadJson);
        }

        [Fact]
        public void RequestStatusQueryIsReadOnlyAndScopedToRequester()
        {
            var broker = StartBroker("");
            var query = TaskBusProtocol.CreateEnvelope("TASK_QUERY",
                "{\"request_id\":\"parent\"}", targetId: 1);
            Dispatch(broker, 9, query);
            Assert.Contains("\"state\":\"UNKNOWN\"", TakeResponse(broker, "TASK_PUBLISHED_ACK", 9).PayloadJson);
            Assert.DoesNotContain("TASK|", broker.Storage);

            Dispatch(broker, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"parent\",\"group_id\":\"MiningPoints\"}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            Dispatch(broker, 10, query);
            Assert.Contains("\"state\":\"UNKNOWN\"", TakeResponse(broker, "TASK_PUBLISHED_ACK", 10).PayloadJson);
            Dispatch(broker, 9, query);
            Assert.Contains("\"state\":\"QUEUED\"", TakeResponse(broker, "TASK_PUBLISHED_ACK", 9).PayloadJson);
            Assert.Single(broker.Storage.Split('\n').Where(line => line.StartsWith("TASK|")));
        }

        [Fact]
        public void TransportClosesOnlyAfterBaseStockAndActualHaulerCompletion()
        {
            var broker = StartBroker("");
            var pointPackets = new Queue<string>();
            var haulerPackets = new Queue<string>();
            var point = new TaskBusConsumer((message, _) => pointPackets.Enqueue(message),
                () => "", _ => { }, _ => { }, 40, "Point", 1, gridId: 500);
            double cargo = 50;
            long dock = 500;
            var hauler = new TaskBusConsumer((message, _) => haulerPackets.Enqueue(message),
                () => "", _ => { }, _ => { }, 42, "Hauler", 1, readHaulerCargo: _ => cargo,
                readDockedGrid: () => dock, autoHauler: true);
            point.ProcessCommand("broker 1");
            point.ProcessCommand("group MiningPoints");
            point.ProcessCommand("point auto");
            hauler.ProcessCommand("broker 1");
            hauler.ProcessCommand("group Haulers");

            Dispatch(broker, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"parent\",\"resource\":\"Ore/Iron\",\"amount\":50,\"minimum\":100," +
                "\"group_id\":\"MiningPoints\",\"base_grid_id\":600,\"receipt_required\":true," +
                "\"steps\":[\"transport\"]}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            point.Tick();
            DeliverToWorker(point, Exchange(40, pointPackets, "TASK_ASSIGN"));
            Dispatch(broker, 40, pointPackets.Dequeue());

            point.Tick();
            DeliverToWorker(point, Exchange(40, pointPackets, "TASK_ASSIGN"));
            DeliverToWorker(point, Exchange(40, pointPackets, "TASK_PUBLISHED_ACK"));
            Dispatch(broker, 40, pointPackets.Dequeue());

            hauler.Tick();
            DeliverToWorker(hauler, Exchange(42, haulerPackets, "TASK_ASSIGN"));
            Dispatch(broker, 42, haulerPackets.Dequeue());
            hauler.Tick();
            DeliverToWorker(hauler, Exchange(42, haulerPackets, "TASK_ASSIGN"));
            Assert.Empty(haulerPackets);
            Assert.Contains("Stage: deliver", hauler.GetStatus());

            var stock = TaskBusProtocol.CreateEnvelope("TASK_QUERY",
                "{\"request_id\":\"parent\",\"stock\":100}", targetId: 1);
            Dispatch(broker, 9, stock);
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            point.Tick();
            DeliverToWorker(point, Exchange(40, pointPackets, "TASK_ASSIGN"));
            DeliverToWorker(point, Exchange(40, pointPackets, "TASK_PUBLISHED_ACK"));
            Dispatch(broker, 40, pointPackets.Dequeue());
            Assert.Contains("Base stock confirmed", point.GetStatus());
            Assert.Contains("Pending completion: none", point.GetStatus());

            dock = 600;
            cargo = 0;
            hauler.Tick();
            Exchange(42, haulerPackets, "TASK_ASSIGN");
            DeliverToWorker(hauler, Exchange(42, haulerPackets, "TASK_DONE_ACK"));

            point.Tick();
            DeliverToWorker(point, Exchange(40, pointPackets, "TASK_ASSIGN"));
            DeliverToWorker(point, Exchange(40, pointPackets, "TASK_PUBLISHED_ACK"));
            Dispatch(broker, 40, pointPackets.Dequeue());
            point.Tick();
            Exchange(40, pointPackets, "TASK_ASSIGN");
            DeliverToWorker(point, Exchange(40, pointPackets, "TASK_DONE_ACK"));

            Dispatch(broker, 9, stock);
            Assert.Contains("\"state\":\"DONE\"", TakeResponse(broker, "TASK_PUBLISHED_ACK", 9).PayloadJson);
            Assert.DoesNotContain("TASK|", broker.Storage);

            TaskBusEnvelope Exchange(long sender, Queue<string> packets, string response)
            {
                Dispatch(broker, sender, packets.Dequeue());
                return TakeResponse(broker, response, sender);
            }
        }

        [Fact]
        public void BaseStockConfirmationSurvivesReloadAndRequiresOriginalRequester()
        {
            var broker = StartBroker("");
            Dispatch(broker, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"parent\",\"group_id\":\"MiningPoints\",\"minimum\":100}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);

            var report = TaskBusProtocol.CreateEnvelope("TASK_QUERY",
                "{\"request_id\":\"parent\",\"stock\":100}", targetId: 1);
            var poll = TaskBusProtocol.CreateEnvelope("TASK_POLL",
                "{\"group_id\":\"MiningPoints\"}", targetId: 1);
            Dispatch(broker, 10, report);
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 10);
            Dispatch(broker, 42, poll);
            Assert.Contains("\"base_stock_confirmed\":false", TakeResponse(broker, "TASK_ASSIGN", 42).PayloadJson);

            Dispatch(broker, 9, report);
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            broker = StartBroker(broker.Storage);
            Dispatch(broker, 42, poll);
            Assert.Contains("\"base_stock_confirmed\":true", TakeResponse(broker, "TASK_ASSIGN", 42).PayloadJson);
        }

        [Fact]
        public void SurveyReportSurvivesLostCompletionAndBrokerRestart()
        {
            var broker = StartBroker("");
            Dispatch(broker, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"parent\",\"group_id\":\"MiningPoints\"}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            Dispatch(broker, 40, TaskBusProtocol.CreateEnvelope("TASK_POLL",
                "{\"group_id\":\"MiningPoints\"}", targetId: 1));
            TakeResponse(broker, "TASK_ASSIGN", 40);
            Dispatch(broker, 40, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"survey\",\"group_id\":\"Scouts\",\"parent_task_id\":\"9-1\"," +
                "\"parent_lease_epoch\":1,\"resource\":\"Ore/Iron\",\"steps\":[\"survey\"]}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 40);

            string storage = "";
            var requests = new List<string>();
            TaskBusConsumer StartPlatform() => new TaskBusConsumer((message, _) => requests.Add(message),
                () => storage, value => storage = value, _ => { }, 42, "Platform", 1, miningPlatform: true);
            var platform = StartPlatform();
            platform.Tick();
            Dispatch(broker, 42, Assert.Single(requests));
            requests.Clear();
            DeliverToWorker(platform, TakeResponse(broker, "TASK_ASSIGN", 42));

            const string report = "Iron observed at GPS:Iron:10:20:30:";
            platform.CompleteSurvey(report);
            requests.Clear();
            platform = StartPlatform();
            platform.Tick();
            Dispatch(broker, 42, Assert.Single(requests));
            TakeResponse(broker, "TASK_DONE_ACK", 42);

            broker = StartBroker(broker.Storage);
            Dispatch(broker, 40, TaskBusProtocol.CreateEnvelope("TASK_QUERY",
                "{\"request_id\":\"survey\"}", targetId: 1));
            var status = TakeResponse(broker, "TASK_PUBLISHED_ACK", 40);
            Assert.Contains("\"state\":\"DONE\"", status.PayloadJson);
            Assert.Contains("\"survey_report\":\"" + report + "\"", status.PayloadJson);
        }

        [Fact]
        public void MiningPlatformCanFinishSurveyThenLeaseMiningWork()
        {
            var broker = StartBroker("");
            string storage = "";
            var platform = new TaskBusConsumer((message, _) => broker.IGC.Inbox.Enqueue((42, message)),
                () => storage, value => storage = value, _ => { }, 42, "Platform", 1, miningPlatform: true);

            Dispatch(broker, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"survey\",\"group_id\":\"Scouts\",\"steps\":[\"survey\"]}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);
            Dispatch(broker, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"mine\",\"group_id\":\"MiningFleet\",\"steps\":[\"mine\"]}", targetId: 1));
            TakeResponse(broker, "TASK_PUBLISHED_ACK", 9);

            platform.Tick();
            broker.Main("");
            DeliverToWorker(platform, TakeResponse(broker, "TASK_ASSIGN", 42));
            Assert.Contains("Stage: survey", platform.GetStatus());

            platform.Tick();
            broker.Main("");
            Assert.Contains("\"group_id\":\"Scouts\"", TakeResponse(broker, "TASK_ASSIGN", 42).PayloadJson);
            platform.ProcessCommand("stage survey");
            broker.Main("");
            DeliverToWorker(platform, TakeResponse(broker, "TASK_DONE_ACK", 42));

            platform.Tick();
            broker.Main("");
            DeliverToWorker(platform, TakeResponse(broker, "TASK_ASSIGN", 42));
            Assert.Contains("Stage: mine", platform.GetStatus());
        }

        [Fact]
        public void BrokerDefersRelayReplyUntilAfterTheIncomingInvocation()
        {
            var broker = StartBroker("");
            var relay = new RecordingRelay();
            broker.GridTerminalSystem.Relay = relay;
            var poll = TaskBusProtocol.CreateEnvelope("TASK_POLL", "{\"group_id\":\"MiningFleet\"}", targetId: 1);

            broker.Main("galnet recv 73 1:1 taskbus 42 " + poll);
            Assert.Empty(relay.Commands);
            Assert.Empty(broker.IGC.Outbox);

            broker.Main("");
            var reply = Assert.Single(relay.Commands);
            Assert.StartsWith("app 73 taskbus 1 ", reply);
            Assert.True(TaskBusProtocol.TryParseEnvelope(reply.Substring("app 73 taskbus 1 ".Length), out var envelope));
            Assert.Equal("QUEUE_EMPTY", envelope.EventType);
            Assert.Equal(42L, envelope.TargetId);
        }

        private sealed class RecordingRelay : IMyProgrammableBlock
        {
            public long EntityId => 100;
            public long CubeGrid => 1;
            public bool IsWorking => true;
            public readonly System.Collections.Generic.List<string> Commands = new();

            public bool TryRun(string command)
            {
                Commands.Add(command);
                return true;
            }
        }

        [Fact]
        public void PausedBrokerTransfersQueueAndRejectsCorruptedBackup()
        {
            var source = StartBroker("");
            var panel = new BackupPanel();
            source.GridTerminalSystem.BackupPanel = panel;
            Dispatch(source, 9, TaskBusProtocol.CreateEnvelope("TASK_PUBLISH",
                "{\"request_id\":\"handoff\",\"group_id\":\"MiningPoints\"}", targetId: 1));
            TakeResponse(source, "TASK_PUBLISHED_ACK", 9);
            source.Main("broker pause");
            source.Main("broker export");
            Assert.DoesNotContain("RETIRED|1", source.Storage);
            var regularBackup = panel.Text;
            Assert.StartsWith("TASKBUS-BACKUP-1|", regularBackup);
            panel.RejectWrites = true;
            source.Main("broker handoff 2");
            Assert.Equal(regularBackup, panel.Text);
            panel.RejectWrites = false;
            Assert.Contains("HANDOFF_TARGET|2", source.Storage);
            Assert.Contains("RETIRED|1", source.Storage);
            var retiredAfterDeadline = StartBroker(AfterResumeDeadline(source.Storage));
            retiredAfterDeadline.Main("broker resume");
            Assert.Contains("PAUSED|1", retiredAfterDeadline.Storage);
            source = StartBroker(source.Storage);

            source.GridTerminalSystem.BackupPanel = panel;
            source.Main("broker export");
            Assert.StartsWith("TASKBUS-BACKUP-2|", panel.Text);
            Assert.Contains("HANDOFF_TARGET|2", panel.Text);
            var exported = panel.Text;
            source.Main("broker handoff 3");
            Assert.Equal(exported, panel.Text);

            var wrongDestination = StartBroker("", 3);
            wrongDestination.GridTerminalSystem.BackupPanel = panel;
            wrongDestination.Main("broker pause");
            var wrongStorage = wrongDestination.Storage;
            wrongDestination.Main("broker import");
            Assert.Equal(wrongStorage, wrongDestination.Storage);

            var destination = StartBroker("", 2);
            destination.GridTerminalSystem.BackupPanel = panel;
            destination.Main("broker pause");
            var empty = destination.Storage;
            var backup = panel.Text;
            panel.Text += "truncated";
            destination.Main("broker import");
            Assert.Equal(empty, destination.Storage);
            panel.Text = backup.Replace("\n", "\r\n");

            destination.Main("broker import");
            Assert.DoesNotContain("RETIRED|1", destination.Storage);
            Assert.DoesNotContain("HANDOFF_TARGET|", destination.Storage);
            Assert.Contains("PAUSED|1", destination.Storage);
            Assert.Contains("TASK|9-1|", destination.Storage);
            var poll = TaskBusProtocol.CreateEnvelope("TASK_POLL",
                "{\"group_id\":\"MiningPoints\"}", targetId: 2);
            Dispatch(destination, 42, poll);
            Assert.Empty(destination.IGC.Outbox);

            destination.Main("broker resume");
            Assert.Contains("PAUSED|1", destination.Storage);
            destination = StartBroker(AfterResumeDeadline(destination.Storage), 2);
            destination.Main("broker resume");
            Dispatch(destination, 42, poll);
            Assert.Contains("9-1", TakeResponse(destination, "TASK_ASSIGN", 42).PayloadJson);
        }

        private sealed class BackupPanel : IMyTextPanel
        {
            public long CubeGrid => 1;
            public bool IsWorking => true;
            public string Text = "";
            public bool RejectWrites;
            public string GetText() => Text;

            public bool WriteText(string text, bool append)
            {
                if (RejectWrites)
                    return false;
                Text = append ? Text + text : text;
                return true;
            }
        }

        private static string WithExpiredLease(string storage, string taskId)
        {
            var rows = storage.Split('\n');
            int index = System.Array.FindIndex(rows, row => row.StartsWith("TASK|" + taskId + "|"));
            Assert.True(index >= 0);
            var fields = rows[index].Split('|');
            Assert.True(long.Parse(fields[12]) > System.DateTime.UtcNow.Ticks);
            fields[12] = "1";
            rows[index] = string.Join("|", fields);
            return string.Join("\n", rows);
        }

        private static string AfterResumeDeadline(string storage)
        {
            const string key = "RESUME_AFTER_UTC|";
            var lines = storage.Split('\n');
            var deadline = Assert.Single(lines.Where(line => line.StartsWith(key, System.StringComparison.Ordinal)));
            Assert.True(long.Parse(deadline.Substring(key.Length)) > System.DateTime.UtcNow.Ticks);
            return storage.Replace(deadline, key + "1");
        }

        private static Broker StartBroker(string storage, long entityId = 1)
        {
            MyGridProgram.StorageAtStartup = storage;
            MyGridProgram.EntityIdAtStartup = entityId;
            try { return new Broker(); }
            finally
            {
                MyGridProgram.StorageAtStartup = null;
                MyGridProgram.EntityIdAtStartup = 0;
            }
        }

        private static void Dispatch(Broker broker, long sender, string envelope)
        {
            broker.IGC.Inbox.Enqueue((sender, envelope));
            broker.Main("");
        }

        private static TaskBusEnvelope TakeResponse(Broker broker, string type, long target)
        {
            Assert.True(TaskBusProtocol.TryParseEnvelope(Assert.Single(broker.IGC.Outbox), out var envelope));
            broker.IGC.Outbox.Clear();
            Assert.Equal(type, envelope.EventType);
            Assert.Equal(target, envelope.TargetId);
            return envelope;
        }

        private static void DeliverToWorker(TaskBusConsumer worker, TaskBusEnvelope envelope)
        {
            Assert.True(worker.ProcessIncomingMessage(1, TaskBusProtocol.CreateEnvelope(
                envelope.EventType, envelope.PayloadJson, envelope.MessageId, envelope.TargetId)));
        }
    }
}
