#region Prelude
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using SpaceEngineers.Libs.TaskBusConsumer;

namespace SpaceEngineers.Services.Consumer
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private const int PollIntervalTicks = 30;

        private readonly TaskBusConsumer _consumer;
        private readonly IMyBroadcastListener _listener;
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;

            _consumer = new TaskBusConsumer(
                send: (envelope, targetId) =>
                    IGC.SendBroadcastMessage(CommunicationTag, envelope, TransmissionDistance.TransmissionDistanceMax),
                getStorage: () => Storage,
                setStorage: value => Storage = value,
                echo: message => Echo(message),
                entityId: Me.EntityId,
                gridName: Me.CubeGrid.CustomName,
                pollIntervalTicks: PollIntervalTicks);

            _listener = IGC.RegisterBroadcastListener(CommunicationTag);
        }

        public void Main(string argument)
        {
            _consumer.ProcessCommand(argument);

            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas, antenna => antenna.IsFunctional && antenna.Enabled);
            if (_antennas.Count == 0)
            {
                Echo("Enable a functional radio antenna to connect to the broker.");
                return;
            }

            _consumer.Tick();
            while (_listener.HasPendingMessage)
            {
                var message = _listener.AcceptMessage();
                if (message.Source != Me.EntityId)
                    _consumer.ProcessIncomingMessage(message.Source, message.Data as string);
            }

            Echo("Worker block ID: " + Me.EntityId);
            Echo(_consumer.GetStatus());
        }

        #region PreludeFooter
    }
}
#endregion
