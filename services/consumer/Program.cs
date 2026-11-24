#region Prelude
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using System.Globalization;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
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
        private readonly string _haulerCargo;
        private readonly string _haulerDock;
        private readonly List<MyInventoryItem> _cargoItems = new List<MyInventoryItem>();

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
            _haulerCargo = ReadSetting("HaulerCargo=");
            _haulerDock = ReadSetting("HaulerDock=");
            _consumer = new TaskBusConsumer(
                send: _galnet.Send,
                getStorage: () => Storage,
                setStorage: value => Storage = value,
                echo: message => Echo(message),
                entityId: Me.EntityId,
                gridName: Me.CubeGrid.CustomName,
                pollIntervalTicks: PollIntervalTicks,
                getGridPositionJson: ReadGridPositionJson,
                readHaulerCargo: _haulerCargo == null ? null : (Func<string, double?>)ReadHaulerCargo,
                gridId: Me.CubeGrid.EntityId,
                readDockedGrid: _haulerDock == null ? null : (Func<long>)ReadDockedGrid,
                autoHauler: string.Equals(ReadSetting("HaulerAuto="), "true", StringComparison.OrdinalIgnoreCase));

            _routes = new MiningRouteLoader(_consumer, Me.CubeGrid, GridTerminalSystem.GetBlockWithName, Echo);
            _listener = IGC.RegisterBroadcastListener(CommunicationTag);
        }

        public void Main(string argument)
        {
            var command = (argument ?? "").Trim();
            if (_galnet.TryReceive(command, (source, envelope) => _consumer.ProcessIncomingMessage(source, envelope)))
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

        private string ReadSetting(string key)
        {
            foreach (var line in Me.CustomData.Split('\n'))
            {
                if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(key.Length).Trim();
            }
            return null;
        }

        private long ReadDockedGrid()
        {
            if (string.IsNullOrEmpty(_haulerDock))
                return 0;
            var connector = GridTerminalSystem.GetBlockWithName(_haulerDock) as IMyShipConnector;
            if (connector == null || connector.CubeGrid != Me.CubeGrid || connector.Status != MyShipConnectorStatus.Connected)
                return 0;
            var partner = connector.OtherConnector;
            return partner == null ? 0 : partner.CubeGrid.EntityId;
        }

        private double? ReadHaulerCargo(string resource)
        {
            if (string.IsNullOrEmpty(_haulerCargo))
                return null;
            var container = GridTerminalSystem.GetBlockWithName(_haulerCargo) as IMyCargoContainer;
            if (container == null || container.CubeGrid != Me.CubeGrid || !container.IsFunctional)
                return null;
            var inventory = container.GetInventory();
            if (inventory == null)
                return null;

            double total = 0;
            _cargoItems.Clear();
            inventory.GetItems(_cargoItems);
            foreach (var item in _cargoItems)
            {
                var type = item.Type.TypeId.ToString().Replace("MyObjectBuilder_", string.Empty);
                if (string.Equals(type + "/" + item.Type.SubtypeId, resource, StringComparison.OrdinalIgnoreCase))
                    total += (double)item.Amount;
            }
            return total;
        }

        private string ReadGridPositionJson()
        {
            var position = Me.CubeGrid.GetPosition();
            var culture = CultureInfo.InvariantCulture;
            return "{\"x\":" + position.X.ToString("R", culture) +
                ",\"y\":" + position.Y.ToString("R", culture) +
                ",\"z\":" + position.Z.ToString("R", culture) + "}";
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
