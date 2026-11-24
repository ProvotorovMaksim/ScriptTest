#region Prelude
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using System.Linq;
using VRageMath;
using System.Text;
using SpaceEngineers.Libs.IgcCore;

namespace SpaceEngineers.Apps.Sample
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private int _tickCount = 0;
        private Autopilot _autopilot;
        private Logger _logger;
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();
        private IMyBroadcastListener _listener;

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
            _listener = IGC.RegisterBroadcastListener(CommunicationTag);

            var autopilotBlockName = "RemCon";
            var programmingBlockName = "Prog";

            _autopilot = new Autopilot(GridTerminalSystem.GetBlockWithName(autopilotBlockName));
            var programBlock = GridTerminalSystem.GetBlockWithName(programmingBlockName) as IMyProgrammableBlock;
            _logger = new Logger(programBlock);
            _logger.Clear();
            _logger.LogTrace($"Logger initialized");
        }
        public void Main(string args)
        {
            _tickCount++;
            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas, antenna => antenna.IsFunctional && antenna.Enabled);

            if (_antennas.Count == 0)
            {
                Echo("No enabled, functional radio antenna found.");
                return;
            }

            if (string.Equals(args, "ping", StringComparison.OrdinalIgnoreCase))
                SendMessage("PING");
            else if (args.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
                SendMessage("MSG:" + args.Substring(4));

            if (_tickCount % 60 == 0)
                SendMessage("PING");

            while (_listener.HasPendingMessage)
            {
                var message = _listener.AcceptMessage();
                if (message.Source == Me.EntityId)
                    continue;

                var payload = message.Data as string;
                if (string.IsNullOrEmpty(payload))
                    continue;

                Echo($"From PB {message.Source}: {payload}");
                if (payload == "PING")
                    SendMessage("PONG");
            }
        }

        private void SendMessage(string payload)
        {
            IGC.SendBroadcastMessage(CommunicationTag, payload, TransmissionDistance.TransmissionDistanceMax);
            Echo($"Sent: {payload}");
        }

        #region PreludeFooter
    }
}
#endregion