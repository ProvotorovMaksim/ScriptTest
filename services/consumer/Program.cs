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
        private readonly string _haulerUnloadTo;
        private readonly string _haulerLoadFrom;
        private readonly List<IMyCargoContainer> _stationContainers = new List<IMyCargoContainer>();
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
            _haulerUnloadTo = ReadSetting("HaulerUnloadTo=");
            _haulerLoadFrom = ReadSetting("HaulerLoadFrom=");
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
                autoHauler: string.Equals(ReadSetting("HaulerAuto="), "true", StringComparison.OrdinalIgnoreCase),
                unloadHaulerCargo: string.IsNullOrEmpty(_haulerUnloadTo)
                    ? null : (Action<long, string, double>)UnloadHaulerCargo,
                loadHaulerCargo: string.IsNullOrEmpty(_haulerLoadFrom)
                    ? null : (Action<long, string, double>)LoadHaulerCargo);

            _routes = new MiningRouteLoader(_consumer, Me.CubeGrid, GridTerminalSystem.GetBlockWithName, Echo,
                gridId => ReadSetting("HaulerApproach." + gridId.ToString(CultureInfo.InvariantCulture) + "="));
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

            if (string.Equals(command, "route dock", StringComparison.OrdinalIgnoreCase))
                StartHaulerDock();
            else if (string.Equals(command, "route depart", StringComparison.OrdinalIgnoreCase))
                DepartHaulerPickup();
            else if (!_routes.TryHandle(command))
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

        private void StartHaulerDock()
        {
            string taskId;
            bool pickup;
            long grid;
            if (!_consumer.TryGetHaulerDestination(out taskId, out pickup, out grid))
            {
                Echo("A confirmed hauler lease with an assigned station is required.");
                return;
            }

            if (RequestFcs("dock drone " + grid.ToString(CultureInfo.InvariantCulture)))
                Echo("FCS final approach requested for station grid " + grid + ". Check the FCS status.");
        }

        private void DepartHaulerPickup()
        {
            if (!_consumer.CanDepartHaulerPickup())
            {
                Echo("A confirmed delivery lease, completed pickup and connection to its mining point are required.");
                return;
            }

            if (RequestFcs("dock depart ready"))
                Echo("FCS departure requested. Check the FCS status.");
        }

        private bool RequestFcs(string command)
        {
            var fcs = GridTerminalSystem.GetBlockWithName("[AMN FCS]") as IMyProgrammableBlock;
            if (fcs != null && fcs.CubeGrid == Me.CubeGrid && fcs.EntityId != Me.EntityId && fcs.IsWorking &&
                fcs.TryRun(command))
                return true;

            Echo("Could not send the request to a working local [AMN FCS] block.");
            return false;
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
            var inventory = FindHaulerInventory();
            if (inventory == null)
                return null;

            double total = 0;
            _cargoItems.Clear();
            inventory.GetItems(_cargoItems);
            foreach (var item in _cargoItems)
            {
                if (MatchesResource(item, resource))
                    total += (double)item.Amount;
            }
            return total;
        }

        private IMyInventory FindHaulerInventory()
        {
            if (string.IsNullOrEmpty(_haulerCargo))
                return null;
            var container = GridTerminalSystem.GetBlockWithName(_haulerCargo) as IMyCargoContainer;
            return container != null && container.CubeGrid == Me.CubeGrid && container.IsFunctional
                ? container.GetInventory() : null;
        }

        private static bool MatchesResource(MyInventoryItem item, string resource)
        {
            var type = item.Type.TypeId.ToString().Replace("MyObjectBuilder_", string.Empty);
            return string.Equals(type + "/" + item.Type.SubtypeId, resource, StringComparison.OrdinalIgnoreCase);
        }

        private void LoadHaulerCargo(long gridId, string resource, double amount)
        {
            var source = FindDockedStationInventory(gridId, _haulerLoadFrom);
            TransferHaulerCargo(source, FindHaulerInventory(), resource, amount);
        }

        private void UnloadHaulerCargo(long gridId, string resource, double amount)
        {
            var destination = FindDockedStationInventory(gridId, _haulerUnloadTo);
            TransferHaulerCargo(FindHaulerInventory(), destination, resource, amount);
        }

        private IMyInventory FindDockedStationInventory(long gridId, string containerName)
        {
            if (gridId == 0 || gridId == Me.CubeGrid.EntityId || ReadDockedGrid() != gridId)
                return null;

            _stationContainers.Clear();
            GridTerminalSystem.GetBlocksOfType(_stationContainers, container =>
                container.CubeGrid.EntityId == gridId && container.IsFunctional &&
                string.Equals(container.CustomName, containerName, StringComparison.Ordinal));
            return _stationContainers.Count == 1 ? _stationContainers[0].GetInventory() : null;
        }

        private void TransferHaulerCargo(IMyInventory source, IMyInventory destination, string resource, double amount)
        {
            if (amount <= 0 || source == null || destination == null || !source.IsConnectedTo(destination))
                return;

            _cargoItems.Clear();
            source.GetItems(_cargoItems);
            foreach (var item in _cargoItems)
            {
                if (!MatchesResource(item, resource))
                    continue;

                var quantity = (VRage.MyFixedPoint)Math.Min(amount, (double)item.Amount);
                if ((double)quantity > 0)
                    source.TransferItemTo(destination, item, quantity);
                return;
            }
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
