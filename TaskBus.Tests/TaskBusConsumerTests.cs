using System.Collections.Generic;
using SpaceEngineers.Libs.IgcCore;
using SpaceEngineers.Libs.TaskBusConsumer;
using Xunit;

namespace TaskBus.Tests
{
    public sealed class TaskBusConsumerTests
    {
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
