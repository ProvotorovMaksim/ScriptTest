using System;
using System.Collections.Generic;

namespace Sandbox.ModAPI.Ingame
{
    public enum UpdateFrequency { Update10 }
    public enum TransmissionDistance { TransmissionDistanceMax }

    public interface IMyRadioAntenna
    {
        bool Enabled { get; }
        bool IsFunctional { get; }
    }

    public interface IMyBroadcastListener
    {
        bool HasPendingMessage { get; }
        (long Source, object Data) AcceptMessage();
    }

    public sealed class TestRadio : IMyBroadcastListener
    {
        public readonly Queue<(long Source, object Data)> Inbox = new();
        public readonly List<string> Outbox = new();

        public bool HasPendingMessage => Inbox.Count != 0;
        public (long Source, object Data) AcceptMessage() => Inbox.Dequeue();
        public IMyBroadcastListener RegisterBroadcastListener(string tag) => this;

        public void SendBroadcastMessage(string tag, string data, TransmissionDistance distance) =>
            Outbox.Add(data);
    }

    public sealed class TestTerminal
    {
        private sealed class Antenna : IMyRadioAntenna
        {
            public bool Enabled => true;
            public bool IsFunctional => true;
        }

        public void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> include)
        {
            if (new Antenna() is T antenna && include(antenna))
                blocks.Add(antenna);
        }
    }

    public abstract class MyGridProgram
    {
        [ThreadStatic]
        public static string StorageAtStartup;

        public TestRadio IGC { get; } = new();
        public TestTerminal GridTerminalSystem { get; } = new();
        public (long EntityId, string Name) Me { get; } = (1, "Broker");
        public TestRuntime Runtime { get; } = new();
        public string Storage { get; set; } = StorageAtStartup;
        public void Echo(string message) { }

        public sealed class TestRuntime
        {
            public UpdateFrequency UpdateFrequency { get; set; }
        }
    }
}
