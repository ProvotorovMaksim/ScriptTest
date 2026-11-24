using System.Collections.Generic;
using SpaceEngineers.Libs.IgcCore;
using SpaceEngineers.Libs.TaskBusConsumer;
using Xunit;

namespace TaskBus.Tests
{
    public sealed class TaskBusConsumerTests
    {
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
