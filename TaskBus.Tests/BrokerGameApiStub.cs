using System;
using System.Collections.Generic;

namespace Sandbox.ModAPI.Ingame
{
    public enum UpdateType { Update10 = 1, Terminal = 2 }
    public enum UpdateFrequency { Update10 }
    public enum MyLaserAntennaStatus { Idle, Connected }
    public interface IMyLaserAntenna
    {
        long CubeGrid { get; }
        bool IsWorking { get; }
        MyLaserAntennaStatus Status { get; }
        void SetTargetCoords(string gps);
        void Connect();
    }

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

        public readonly List<(long Target, string Tag, string Data)> UnicastOutbox = new();
        public TestUnicastListener UnicastListener { get; } = new();

        public bool SendUnicastMessage(long target, string tag, string data)
        {
            UnicastOutbox.Add((target, tag, data));
            return true;
        }

        public bool HasPendingMessage => Inbox.Count != 0;
        public (long Source, object Data) AcceptMessage() => Inbox.Dequeue();
        public IMyBroadcastListener RegisterBroadcastListener(string tag) => this;

        public void SendBroadcastMessage(string tag, string data, TransmissionDistance distance) =>
            Outbox.Add(data);
    }

    public sealed class TestUnicastListener
    {
        public readonly Queue<(long Source, string Tag, object Data)> Inbox = new();
        public bool HasPendingMessage => Inbox.Count != 0;
        public (long Source, string Tag, object Data) AcceptMessage() => Inbox.Dequeue();
    }

    public interface IMyProgrammableBlock
    {
        long EntityId { get; }
        long CubeGrid { get; }
        bool IsWorking { get; }
        bool TryRun(string command);
    }

    public interface IMyTextPanel
    {
        long CubeGrid { get; }
        bool IsWorking { get; }
        string GetText();
        bool WriteText(string text, bool append);
    }

    public sealed class TestTerminal
    {
        public IMyProgrammableBlock Application;
        public readonly Dictionary<long, IMyProgrammableBlock> ApplicationsById = new();
        public object GetBlockWithId(long id) => ApplicationsById.TryGetValue(id, out var block) ? block : null;
        public IMyProgrammableBlock Relay;
        public IMyLaserAntenna Laser;
        public IMyTextPanel BackupPanel;
        public object GetBlockWithName(string name) =>
            name == "[GALNET APP]" ? Application : name == "[GALNET RELAY]" ? Relay : name == "Laser A" ? Laser : name == "[TASKBUS BACKUP]" ? BackupPanel : null;

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
        [ThreadStatic]
        public static long EntityIdAtStartup;
        [ThreadStatic]
        public static string CustomDataAtStartup;

        public TestRadio IGC { get; } = new();
        public TestTerminal GridTerminalSystem { get; } = new();
        public (long EntityId, string Name, long CubeGrid, string CustomData) Me { get; } =
            (EntityIdAtStartup == 0 ? 1 : EntityIdAtStartup, "Test node", 1, CustomDataAtStartup ?? "");
        public TestRuntime Runtime { get; } = new();
        public string Storage { get; set; } = StorageAtStartup;
        public List<string> Output { get; } = new();
        public void Echo(string message) => Output.Add(message);

        public sealed class TestRuntime
        {
            public UpdateFrequency UpdateFrequency { get; set; }
        }
    }
}
