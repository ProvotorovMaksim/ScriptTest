#region Prelude
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using VRage.Game.GUI.TextPanel;
using SpaceEngineers.Libs.TaskBusConsumer;

namespace SpaceEngineers.Services.Consumer
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private const int PollIntervalTicks = 30;
        private const string MonitorName = "[AMN STATUS]";
        private int _monitorTicks;

        private readonly GalnetWorkerBridge _galnet;
        private readonly TaskBusConsumer _consumer;
        private readonly MiningRouteLoader _routes;
        private readonly IMyBroadcastListener _listener;
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;

            _galnet = new GalnetWorkerBridge(Me, Me.CustomData,
                () => GridTerminalSystem.GetBlockWithName(GalnetWorkerBridge.RelayName) as IMyProgrammableBlock,
                (envelope, _) => IGC.SendBroadcastMessage(CommunicationTag, envelope, TransmissionDistance.TransmissionDistanceMax),
                message => Echo(message));
            _consumer = new TaskBusConsumer(
                send: _galnet.Send,
                getStorage: () => Storage,
                setStorage: value => Storage = value,
                echo: message => Echo(message),
                entityId: Me.EntityId,
                gridName: Me.CubeGrid.CustomName,
                pollIntervalTicks: PollIntervalTicks);

            _routes = new MiningRouteLoader(_consumer, Me.CubeGrid, GridTerminalSystem.GetBlockWithName, Echo);
            _listener = IGC.RegisterBroadcastListener(CommunicationTag);
        }

        public void Main(string argument)
        {
            var command = (argument ?? "").Trim();
            if (_galnet.TryReceive(command, _consumer))
                return;

            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas, antenna => antenna.IsFunctional && antenna.Enabled);
            bool viaGalnet = _galnet.IsConfigured;
            bool connected = viaGalnet ? _galnet.IsAvailable : _antennas.Count > 0;
            _consumer.Tick(connected);
            while (!viaGalnet && connected && _listener.HasPendingMessage)
            {
                var message = _listener.AcceptMessage();
                if (message.Source != Me.EntityId)
                    _consumer.ProcessIncomingMessage(message.Source, message.Data as string);
            }

            if (!_routes.TryHandle(command))
                _consumer.ProcessCommand(argument);
            _galnet.Flush();
            string status = "Worker block ID: " + Me.EntityId + "\n" + _consumer.GetStatus();
            if (!connected)
                status = (viaGalnet ? "Galnet relay unavailable or misconfigured.\n" : "Radio unavailable.\n") + status;
            Echo(status);

            if (++_monitorTicks >= 6 || !string.IsNullOrWhiteSpace(argument))
            {
                _monitorTicks = 0;
                UpdateMonitor(status);
            }
        }

        private void UpdateMonitor(string status)
        {
            try
            {
                var panel = GridTerminalSystem.GetBlockWithName(MonitorName) as IMyTextPanel;
                if (panel == null || panel.CubeGrid != Me.CubeGrid || !panel.IsWorking)
                    return;
                if (panel.ContentType != ContentType.TEXT_AND_IMAGE)
                    panel.ContentType = ContentType.TEXT_AND_IMAGE;
                panel.WriteText(status);
            }
            catch (Exception error)
            {
                Echo("AMN monitor unavailable: " + error.Message);
            }
        }

        #region PreludeFooter
    }
}
#endregion
