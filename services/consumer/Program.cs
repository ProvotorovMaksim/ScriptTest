using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;

namespace SpaceEngineers.Services.Consumer
{
    using SpaceEngineers.Libs.TaskBusConsumer;

    public sealed class Program : MyGridProgram
    {
        // PB BODY START
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private const int PollIntervalTicks = 30;
        private const string HaulerStopPrefix = "HAULER_STOP|";
        private const int DepartStopped = 1;
        private const int CruiseStopped = 2;
        private int _haulerStopFlags;
        private string _consumerStorage;
        private const string MonitorName = "[AMN STATUS]";
        private int _monitorTicks;
        private int _departureTicks;
        private bool _autoDepart;
        private bool _autoCruise;
        private int _autoCruiseTicks;
        private string _startedCruiseLease;
        private long _startedCruiseGrid;
        private bool _startedCruisePickup;
        private string _fcsDockLease;
        private long _fcsDockDeadlineUtc;
        private long _fcsDockRequestId;
        private long _fcsDockLimitUtc;
        private long _fcsDockGrid;
        private bool _cancelFcsDock;
        private readonly string _haulerCargo;
        private readonly string _miningBuffer;
        private readonly string _haulerDock;
        private readonly string _haulerUnloadTo;
        private readonly string _haulerLoadFrom;
        private readonly List<IMyCargoContainer> _stationContainers = new List<IMyCargoContainer>();
        private readonly List<IMyShipController> _cruiseControllers = new List<IMyShipController>();
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
            _miningBuffer = ReadSetting("MiningBuffer=");
            _haulerDock = ReadSetting("HaulerDock=");
            _haulerUnloadTo = ReadSetting("HaulerUnloadTo=");
            _haulerLoadFrom = ReadSetting("HaulerLoadFrom=");
            _consumerStorage = Storage;
            if (Storage.StartsWith(HaulerStopPrefix, StringComparison.Ordinal))
            {
                int end = Storage.IndexOf('\n');
                if (end >= 0)
                {
                    int.TryParse(Storage.Substring(HaulerStopPrefix.Length, end - HaulerStopPrefix.Length),
                        out _haulerStopFlags);
                    _consumerStorage = Storage.Substring(end + 1);
                }
            }
            _autoDepart = (_haulerStopFlags & DepartStopped) == 0 &&
                string.Equals(ReadSetting("HaulerAutoDepart="), "true", StringComparison.OrdinalIgnoreCase);
            _autoCruise = (_haulerStopFlags & CruiseStopped) == 0 &&
                string.Equals(ReadSetting("HaulerAutoCruise="), "true", StringComparison.OrdinalIgnoreCase);
            _consumer = new TaskBusConsumer(
                send: _galnet.Send,
                getStorage: () => _consumerStorage,
                setStorage: value =>
                {
                    _consumerStorage = value;
                    SaveHaulerStorage();
                },
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
                    ? null : (Func<long, string, double, double>)UnloadHaulerCargo,
                loadHaulerCargo: string.IsNullOrEmpty(_haulerLoadFrom)
                    ? null : (Action<long, string, double>)LoadHaulerCargo,
                readMiningBuffer: _miningBuffer == null ? null : (Func<string, double?>)ReadMiningBuffer,
                knownSiteRadius: ReadKnownSiteRadius(),
                autoAcceptSurvey: string.Equals(ReadSetting("PointAutoSurveyAccept="), "true", StringComparison.OrdinalIgnoreCase));

            _routes = new MiningRouteLoader(_consumer, Me.CubeGrid, GridTerminalSystem.GetBlockWithName, Echo,
                gridId => ReadSetting("HaulerApproach." + gridId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "="),
                CanStartHaulerCruise, GridTerminalSystem.GetBlockWithId,
                string.Equals(ReadSetting("HaulerAutoDock="), "true", StringComparison.OrdinalIgnoreCase)
                    ? (Action<long>)RequestHaulerApproach : null);
            _listener = IGC.RegisterBroadcastListener(CommunicationTag);
            _routes.CheckCruiseLease();
        }

        private void SaveHaulerStorage()
        {
            Storage = _haulerStopFlags == 0 ? _consumerStorage :
                HaulerStopPrefix + _haulerStopFlags + "\n" + _consumerStorage;
        }

        public void Main(string argument, UpdateType updateSource)
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
            else if (string.Equals(command, "route depart auto", StringComparison.OrdinalIgnoreCase))
            {
                _autoDepart = true;
                _haulerStopFlags &= ~DepartStopped;
                SaveHaulerStorage();
            }
            else if (string.Equals(command, "route cruise auto", StringComparison.OrdinalIgnoreCase))
            {
                _autoCruise = true;
                _haulerStopFlags &= ~CruiseStopped;
                SaveHaulerStorage();
            }
            else if (string.Equals(command, "route cruise", StringComparison.OrdinalIgnoreCase))
                StartHaulerCruise(automatic: false);
            else
            {
                if (string.Equals(command, "route stop", StringComparison.OrdinalIgnoreCase))
                {
                    _autoDepart = false;
                    _autoCruise = false;
                    _haulerStopFlags = DepartStopped | CruiseStopped;
                    SaveHaulerStorage();
                    _cancelFcsDock = true;
                }
                if (!_routes.TryHandle(command))
                    _consumer.ProcessCommand(argument);
            }
            if (_autoDepart && (updateSource & UpdateType.Update10) != 0 && ++_departureTicks >= 12)
            {
                _departureTicks = 0;
                TryAutomaticHaulerDeparture();
            }
            CheckLeasedHaulerDock();
            _routes.CheckCruiseLease((updateSource & UpdateType.Update10) != 0);
            if (_autoCruise && (updateSource & UpdateType.Update10) != 0 && ++_autoCruiseTicks >= 12)
            {
                _autoCruiseTicks = 0;
                StartHaulerCruise(automatic: true);
            }
            _galnet.Flush();
            string status = "Worker block ID: " + Me.EntityId + "\n" + _consumer.GetStatus();
            if (!connected)
                status = (viaGalnet ? "Galnet relay unavailable or misconfigured.\n" : "Radio unavailable.\n") + status;
            if (_autoDepart)
                status += "\nAutomatic departure enabled.";
            if (_autoCruise)
                status += "\nAutomatic hauler cruise enabled.";
            Echo(status);

            if (++_monitorTicks >= 6 || !string.IsNullOrWhiteSpace(argument))
            {
                _monitorTicks = 0;
                UpdateMonitor(status);
            }
        }

        private void StartHaulerCruise(bool automatic)
        {
            var lease = _consumer.ConfirmedLeaseIdentity;
            if (_consumer.SupervisedCruiseRemote != 0)
                return;

            string taskId;
            bool pickup;
            long grid;
            bool hasDestination = _consumer.TryGetHaulerDestination(out taskId, out pickup, out grid);
            if (automatic && (lease == null || !hasDestination || _fcsDockLease != null ||
                (lease == _startedCruiseLease && grid == _startedCruiseGrid && pickup == _startedCruisePickup) ||
                !CanStartHaulerCruise()))
                return;

            _routes.LoadStationApproach(startCruise: true, prepare: automatic);
            if (_consumer.SupervisedCruiseRemote != 0)
            {
                _startedCruiseLease = lease;
                _startedCruiseGrid = grid;
                _startedCruisePickup = pickup;
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

            RequestHaulerApproach(grid);
        }

        private void RequestHaulerApproach(long grid)
        {
            CheckLeasedHaulerDock();
            if (_fcsDockLease != null)
            {
                Echo("A leased docking request is already pending.");
                return;
            }
            long deadline = _consumer.ConfirmedLeaseDeadlineUtc;
            if (deadline <= DateTime.UtcNow.Ticks)
            {
                Echo("Docking requires an active broker lease with a UTC deadline.");
                return;
            }
            if (!RequestFcs("dock leased " + grid.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
                deadline.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                return;

            _fcsDockLease = _consumer.ConfirmedLeaseIdentity;
            _fcsDockDeadlineUtc = deadline;
            _fcsDockRequestId = deadline;
            _fcsDockLimitUtc = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(30).Ticks;
            _fcsDockGrid = grid;
            _cancelFcsDock = false;
            Echo("FCS final approach requested for station grid " + grid + ". Check the FCS status.");
        }

        private void CheckLeasedHaulerDock()
        {
            if (_fcsDockLease == null)
                return;
            if (DateTime.UtcNow.Ticks >= _fcsDockDeadlineUtc || ReadDockedGrid() == _fcsDockGrid)
            {
                _fcsDockLease = null;
                return;
            }
            _cancelFcsDock |= _fcsDockLease != _consumer.ConfirmedLeaseIdentity;
            string requestId = _fcsDockRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_cancelFcsDock)
            {
                if (RequestFcs("dock lease stop " + requestId))
                    _fcsDockLease = null;
                return;
            }

            long deadline = Math.Min(_fcsDockLimitUtc, _consumer.ConfirmedLeaseDeadlineUtc);
            if (deadline > _fcsDockDeadlineUtc &&
                RequestFcs("dock lease refresh " + requestId + " " + deadline.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                _fcsDockDeadlineUtc = deadline;
        }

        private void TryAutomaticHaulerDeparture()
        {
            if (_consumer.SupervisedCruiseRemote != 0 || _fcsDockLease != null)
                return;

            bool atPickup = _consumer.CanDepartHaulerPickup();
            if (!atPickup && (!_autoCruise || !_consumer.CanDepartHaulerBase()))
                return;

            if (!HaulerFlightControlsIdle())
                return;
            if (_autoCruise && !_routes.LoadStationApproach(startCruise: false, prepare: true))
                return;

            RequestFcs("dock depart ready");
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

        private double ReadKnownSiteRadius()
        {
            const double defaultRadius = 5000;
            var setting = ReadSetting("KnownSiteRadius=");
            double radius;
            if (setting == null)
                return defaultRadius;
            if (double.TryParse(setting, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out radius) &&
                radius >= 0 && radius <= 1000000)
                return radius;
            Echo("KnownSiteRadius must be between 0 and 1000000 m. Using 5000 m.");
            return defaultRadius;
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

        private bool CanStartHaulerCruise()
        {
            if (string.IsNullOrEmpty(_haulerDock))
                return false;
            var connector = GridTerminalSystem.GetBlockWithName(_haulerDock) as IMyShipConnector;
            if (connector == null || connector.CubeGrid != Me.CubeGrid || !connector.IsWorking ||
                connector.Status == MyShipConnectorStatus.Connected)
                return false;

            return HaulerFlightControlsIdle();
        }

        private bool HaulerFlightControlsIdle()
        {
            _cruiseControllers.Clear();
            GridTerminalSystem.GetBlocksOfType(_cruiseControllers, controller => controller.CubeGrid == Me.CubeGrid);
            foreach (var controller in _cruiseControllers)
            {
                var remote = controller as IMyRemoteControl;
                if (controller.IsUnderControl || (remote != null && remote.IsAutoPilotEnabled))
                    return false;
            }
            return true;
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
            return ReadCargo(FindHaulerInventory(), resource);
        }

        private double? ReadMiningBuffer(string resource)
        {
            return ReadCargo(FindLocalInventory(_miningBuffer), resource);
        }

        private double? ReadCargo(IMyInventory inventory, string resource)
        {
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
            return FindLocalInventory(_haulerCargo);
        }

        private IMyInventory FindLocalInventory(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            var container = GridTerminalSystem.GetBlockWithName(name) as IMyCargoContainer;
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

        private double UnloadHaulerCargo(long gridId, string resource, double amount)
        {
            var source = FindHaulerInventory();
            var destination = FindDockedStationInventory(gridId, _haulerUnloadTo);
            var before = ReadCargo(source, resource);
            var bufferBefore = ReadCargo(destination, resource);
            if (!before.HasValue || !bufferBefore.HasValue)
                return 0;

            double attempted = TransferHaulerCargo(source, destination, resource, amount);
            if (attempted <= 0)
                return 0;

            double removed = before.Value - ReadCargo(source, resource).Value;
            double added = ReadCargo(destination, resource).Value - bufferBefore.Value;
            return Math.Max(0, Math.Min(amount, Math.Min(attempted, Math.Min(removed, added))));
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

        private double TransferHaulerCargo(IMyInventory source, IMyInventory destination, string resource, double amount)
        {
            if (amount <= 0 || source == null || destination == null || !source.IsConnectedTo(destination))
                return 0;

            _cargoItems.Clear();
            source.GetItems(_cargoItems);
            foreach (var item in _cargoItems)
            {
                if (!MatchesResource(item, resource))
                    continue;

                var quantity = InventoryTransfer.AmountThatFits(destination, item, amount);
                return (double)quantity > 0 && source.TransferItemTo(destination, item, quantity)
                    ? (double)quantity : 0;
            }
            return 0;
        }

        private string ReadGridPositionJson()
        {
            var position = Me.CubeGrid.GetPosition();
            var culture = System.Globalization.CultureInfo.InvariantCulture;
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

        // PB BODY END
    }
}
