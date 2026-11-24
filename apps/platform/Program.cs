#region Prelude
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Libs.TaskBusConsumer;
using SpaceEngineers.Libs.IgcCore;
using VRageMath;
using VRage.Game.ModAPI.Ingame;

namespace SpaceEngineers.Apps.Platform
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string Channel = "SE1-SHIP-LINK";
        private readonly GalnetWorkerBridge _galnet;
        private const string DrillPrefix = "[AMN DRILL]";
        private readonly List<IMyShipDrill> _managedDrills = new List<IMyShipDrill>();
        private string _drillingLease;
        private readonly bool _unloadOnDock;
        private string _pendingUnloadDockLease;
        private long _pendingUnloadDockGrid;
        private int _unloadDockUpdates;
        private bool _autoMiningUnload;
        private string _autoMiningUnloadLease;
        private int _miningUnloadTicks;
        private readonly string _miningCargoName;
        private readonly double? _miningLaneRadius;
        private readonly string _miningUnloadTo;
        private readonly List<IMyCargoContainer> _miningBuffers = new List<IMyCargoContainer>();
        private readonly List<MyInventoryItem> _miningItems = new List<MyInventoryItem>();
        private const string ControllerName = "[AMN PLATFORM]";
        private const string SurveyRemoteName = "[AMN SURVEY ROUTE]";
        private const string SurveyWaypointName = "AMN survey sample";
        private const int MaxSurveyPlanPoints = 9;
        private readonly List<Vector3D> _surveyPlan = new List<Vector3D>();
        private readonly List<IMyShipController> _surveyControllers = new List<IMyShipController>();
        private readonly List<IMyShipConnector> _surveyConnectors = new List<IMyShipConnector>();
        private string _platformFlightLease;
        private bool _platformSurveyFlight;
        private bool _platformMiningPass;
        private bool _platformMiningRetreat;
        private readonly bool _autoMiningRetreat;
        private readonly bool _autoMiningReturn;
        private readonly bool _autoMiningDock;
        private bool _platformMiningReturn;
        private Vector3D _miningReturnEndpoint;
        private string _pendingMiningDockLease;
        private string _fcsDockLease;
        private bool _cancelFcsDock;
        private long _fcsDockDeadlineUtc;
        private long _fcsDockRequestId;
        private long _fcsDockLimitUtc;
        private long _fcsDockGrid;
        private long _miningDockReturnController;
        private int _dockSettleUpdates;
        private string _pendingReturnLease;
        private int _returnSettleUpdates;
        private string _pendingRetreatLease;
        private int _retreatSettleUpdates;
        private const string MiningRetreatRemoteName = "[AMN MINE RETREAT]";
        private const string MiningRetreatWaypointName = "AMN assigned retreat";
        private const int FlightProgressCheckUpdates = 60;
        private const double MinimumFlightProgress = 0.5;
        private int _flightProgressTicks;
        private Vector3D _flightProgressPosition;
        private Vector3D _miningPassEndpoint;
        private double _miningPassDistance;
        private const string MiningPassRemoteName = "[AMN MINE PASS]";
        private const string MiningEndpointName = "AMN assigned ore";
        private readonly List<MyWaypointInfo> _surveyWaypoints = new List<MyWaypointInfo>();
        private const string CameraName = "[AMN SCOUT]";
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();
        private readonly TaskBusConsumer _worker;
        private readonly MiningRouteLoader _routes;
        private readonly IMyBroadcastListener _listener;
        private string _lastProbe = "No scan yet.";

        private const double OreSampleWaitSeconds = 4;
        private const double MinimumViewSeparation = 10;
        private const int MaxSurveyViews = 64;
        private const int MaxOreHits = 256;
        private const string SurveyStorageMarker = "\nAMN_SURVEY_V1\n";
        private string _workerStorage;
        private string _surveyStorage = "";
        private readonly List<SurveyView> _surveyViews = new List<SurveyView>();

        private sealed class SurveyView
        {
            public readonly Vector3D Position;
            public readonly bool? FoundOre;

            public SurveyView(Vector3D position, bool? foundOre)
            {
                Position = position;
                FoundOre = foundOre;
            }
        }
        private readonly HashSet<Vector3D> _oreHits = new HashSet<Vector3D>();
        private IMyOreDetector _sampleDetector;
        private string _surveyOre;
        private string _surveyLease;
        private Vector3D _samplePosition;
        private Vector3D? _reportOre;
        private Vector3D? _reportView;
        private double _sampleWait;
        private int _positiveViews;
        private bool _autoSample;
        private bool _surveySweep;
        private bool _reportOnSweepCompletion;
        private bool _bracketOnSweepCompletion;
        private readonly double _automaticBracketGap;
        private readonly double _automaticSurveySpacing;
        private readonly Vector3D _automaticSurveyCenter;
        private readonly double _asteroidScanRange;
        private readonly double _asteroidStandOff;
        private readonly double _asteroidSurveySpacing;
        private int _asteroidScanTicks;
        private string _suppressedAutomaticSurveyLease;
        private bool _sweepLegStarted;
        private bool _sweepSampleStarted;
        private int _sweepSettleTicks;
        private int _sweepScanTicks;
        private readonly double _surveyClearanceRadius;
        private int _sweepClearanceIndex;
        private Vector3D _sweepClearanceOrigin;
        private Vector3D _sweepClearanceRight;
        private Vector3D _sweepClearanceUp;
        private readonly List<IMyCameraBlock> _surveyCameras = new List<IMyCameraBlock>();
        private int _autoSampleTicks;
        private const string DetectorName = "[AMN ORE]";

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
            var automaticSurvey = ReadSetting("SurveyAutoSweep=");
            if (automaticSurvey != null &&
                !TryReadSurveyPlan(automaticSurvey, out _automaticSurveySpacing, out _automaticSurveyCenter))
                Echo("Use SurveyAutoSweep=<10 to 500 metres> GPS:Center:X:Y:Z:.");
            var automaticBracket = ReadSetting("SurveyAutoBracketGap=");
            if (automaticBracket != null && !TryReadBracketGap(automaticBracket, out _automaticBracketGap))
                Echo("SurveyAutoBracketGap must be between 20 and 500 m.");
            var clearanceSetting = ReadSetting("SurveyClearanceRadius=");
            if (clearanceSetting != null)
            {
                double radius;
                if (double.TryParse(clearanceSetting, NumberStyles.Float, CultureInfo.InvariantCulture, out radius) &&
                    radius >= 0 && radius <= 100)
                    _surveyClearanceRadius = radius;
                else
                    Echo("SurveyClearanceRadius must be between 0 and 100 m.");
            }
            var asteroidSurvey = ReadSetting("SurveyAutoAsteroid=");
            if (asteroidSurvey != null &&
                !TryReadAsteroidSurvey(asteroidSurvey, out _asteroidScanRange, out _asteroidStandOff, out _asteroidSurveySpacing))
                Echo("Use SurveyAutoAsteroid=<scan metres> <stand-off metres> <spacing metres>.");
            _miningCargoName = ReadSetting("MiningCargo=");
            _autoMiningRetreat = string.Equals(ReadSetting("MiningAutoRetreat="), "true", StringComparison.OrdinalIgnoreCase);
            _autoMiningReturn = string.Equals(ReadSetting("MiningAutoReturn="), "true", StringComparison.OrdinalIgnoreCase);
            _autoMiningDock = string.Equals(ReadSetting("MiningAutoDock="), "true", StringComparison.OrdinalIgnoreCase);
            var laneSetting = ReadSetting("MiningLaneRadius=");
            if (laneSetting != null)
            {
                double radius;
                _miningLaneRadius = double.TryParse(laneSetting, NumberStyles.Float, CultureInfo.InvariantCulture, out radius)
                    && radius >= 1 && radius <= 100 ? radius : double.NaN;
            }
            _miningUnloadTo = ReadSetting("MiningUnloadTo=");
            _unloadOnDock = string.Equals(ReadSetting("MiningAutoUnloadOnDock="), "true", StringComparison.OrdinalIgnoreCase);
            _listener = IGC.RegisterBroadcastListener(Channel);
            _galnet = new GalnetWorkerBridge(Me, Me.CustomData,
                () => GridTerminalSystem.GetBlockWithName(GalnetWorkerBridge.RelayName) as IMyProgrammableBlock,
                (envelope, _) => IGC.SendBroadcastMessage(Channel, envelope, TransmissionDistance.TransmissionDistanceMax),
                message => Echo(message));
            int surveyStart = Storage.IndexOf(SurveyStorageMarker, StringComparison.Ordinal);
            _workerStorage = surveyStart < 0 ? Storage : Storage.Substring(0, surveyStart);
            if (surveyStart >= 0)
            {
                _surveyStorage = Storage.Substring(surveyStart);
                LoadSurvey(_surveyStorage.Substring(SurveyStorageMarker.Length));
            }

            _worker = new TaskBusConsumer(
                _galnet.Send,
                () => _workerStorage, value =>
                {
                    _workerStorage = value;
                    Storage = _workerStorage + _surveyStorage;
                }, message => Echo(message),
                Me.EntityId, Me.CubeGrid.CustomName, miningPlatform: true);
            _routes = new MiningRouteLoader(_worker, Me.CubeGrid, GridTerminalSystem.GetBlockWithName, Echo);
            StopManagedDrills();
            CheckPlatformFlight();
            var camera = FindCamera();
            if (camera != null) camera.EnableRaycast = true;
        }

        public void Main(string argument, UpdateType updateSource)
        {
            var command = (argument ?? "").Trim();
            if (_galnet.TryReceive(command, (source, envelope) => _worker.ProcessIncomingMessage(source, envelope)))
                return;

            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas,
                block => block.CubeGrid == Me.CubeGrid && block.IsWorking);
            bool viaGalnet = _galnet.IsConfigured;
            bool online = viaGalnet ? _galnet.IsAvailable : _antennas.Count > 0;
            _worker.Tick(online);
            while (!viaGalnet && online && _listener.HasPendingMessage)
            {
                var message = _listener.AcceptMessage();
                if (message.Source != Me.EntityId)
                    _worker.ProcessIncomingMessage(message.Source, message.Data as string);
            }

            if ((updateSource & UpdateType.Update10) != 0)
                CheckPlatformFlightProgress();
            CheckPlatformFlight();
            CheckMiningLease();
            UpdateOreSample();
            if (command.StartsWith("probe ", StringComparison.OrdinalIgnoreCase))
                Probe(command.Substring(6));
            else if (string.Equals(command, "ore task", StringComparison.OrdinalIgnoreCase))
                BeginTaskSurvey();
            else if (command.StartsWith("ore survey ", StringComparison.OrdinalIgnoreCase))
                BeginOreSurvey(command.Substring(11).Trim());
            else if (string.Equals(command, "ore auto on", StringComparison.OrdinalIgnoreCase))
            {
                _autoSample = _surveyLease != null && SurveyLeaseIsCurrent();
                _autoSampleTicks = 0;
                _lastProbe = _autoSample ? "Automatic sampling enabled for this survey." :
                    "Start the confirmed survey with ore task before enabling automatic sampling.";
            }
            else if (string.Equals(command, "ore auto off", StringComparison.OrdinalIgnoreCase))
            {
                if (_autoSample)
                    _sampleDetector = null;
                _autoSample = false;
                _lastProbe = "Automatic sampling disabled.";
            }
            else if (string.Equals(command, "ore sample", StringComparison.OrdinalIgnoreCase))
                ScheduleOreSample();
            else if (string.Equals(command, "ore samples", StringComparison.OrdinalIgnoreCase))
                ShowSurveySamples();
            else if (string.Equals(command, "ore report", StringComparison.OrdinalIgnoreCase))
                SubmitOreSurvey();
            else if (command.StartsWith("ore plan ", StringComparison.OrdinalIgnoreCase))
                PlanSurvey(command.Substring(9).Trim());
            else if (command.StartsWith("ore candidate ", StringComparison.OrdinalIgnoreCase))
                PlanSurveyCandidate(command.Substring(14));
            else if (command.StartsWith("ore refine ", StringComparison.OrdinalIgnoreCase))
                PlanPerimeterSurvey(command.Substring(11).Trim());
            else if (command.StartsWith("ore bracket ", StringComparison.OrdinalIgnoreCase))
                PlanSurveyBrackets(command.Substring(12).Trim());
            else if (string.Equals(command, "ore route", StringComparison.OrdinalIgnoreCase))
                LoadNextSurveyWaypoint();
            else if (string.Equals(command, "ore fly", StringComparison.OrdinalIgnoreCase))
                StartSurveyFlight();
            else if (string.Equals(command, "ore sweep", StringComparison.OrdinalIgnoreCase))
                BeginSurveySweep(reportOnCompletion: false);
            else if (string.Equals(command, "ore sweep report", StringComparison.OrdinalIgnoreCase))
                BeginSurveySweep(reportOnCompletion: true);
            else if (string.Equals(command, "ore stop", StringComparison.OrdinalIgnoreCase))
            {
                _autoSample = false;
                _surveySweep = false;
                _sampleDetector = null;
                _platformFlightLease = null;
                _pendingRetreatLease = null;
                _pendingReturnLease = null;
                _pendingMiningDockLease = null;
                _pendingUnloadDockLease = null;
                string surveyOre;
                _worker.TryGetSurveyLease(out _suppressedAutomaticSurveyLease, out surveyOre);
                CheckLeasedMiningDock(cancel: true);
                CheckPlatformFlight();
            }
            else if (string.Equals(command, "mine drills on", StringComparison.OrdinalIgnoreCase))
                StartManagedDrills();
            else if (string.Equals(command, "mine plan", StringComparison.OrdinalIgnoreCase))
                LoadAssignedMiningEndpoint();
            else if (string.Equals(command, "mine pass", StringComparison.OrdinalIgnoreCase))
                StartMiningPass();
            else if (string.Equals(command, "mine retreat plan", StringComparison.OrdinalIgnoreCase))
                LoadAssignedMiningRetreat();
            else if (string.Equals(command, "mine retreat", StringComparison.OrdinalIgnoreCase))
            {
                _pendingRetreatLease = null;
                StartMiningRetreat();
            }
            else if (string.Equals(command, "mine dock", StringComparison.OrdinalIgnoreCase))
            {
                _pendingMiningDockLease = null;
                RequestMiningPointDock();
            }
            else if (string.Equals(command, "mine return", StringComparison.OrdinalIgnoreCase))
            {
                _pendingReturnLease = null;
                StartMiningCruise(returning: true);
            }
            else if (string.Equals(command, "mine cruise", StringComparison.OrdinalIgnoreCase))
                StartMiningCruise(returning: false);
            else if (string.Equals(command, "mine unload", StringComparison.OrdinalIgnoreCase))
                UnloadMiningCargo();
            else if (string.Equals(command, "mine unload auto", StringComparison.OrdinalIgnoreCase))
            {
                _pendingUnloadDockLease = null;
                BeginAutomaticMiningUnload();
            }
            else if (string.Equals(command, "mine unload stop", StringComparison.OrdinalIgnoreCase))
            {
                _autoMiningUnload = false;
                _pendingUnloadDockLease = null;
                Echo("Automatic mining unload stopped.");
            }
            else if (string.Equals(command, "mine finish", StringComparison.OrdinalIgnoreCase) ||
                (_miningUnloadTo != null && command.StartsWith("stage ", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(command.Substring(6).Trim(), "mine", StringComparison.OrdinalIgnoreCase)))
                FinishMiningDeposit();
            else if (string.Equals(command, "mine drills off", StringComparison.OrdinalIgnoreCase))
            {
                _pendingRetreatLease = null;
                StopManagedDrills();
            }
            else if (string.Equals(command, "ore bounds", StringComparison.OrdinalIgnoreCase))
                ShowOreBounds();
            else if (string.Equals(command, "ore perimeter", StringComparison.OrdinalIgnoreCase))
                ShowOrePerimeter();
            else if (!_routes.TryHandle(command))
                _worker.ProcessCommand(command);

            if (_autoMiningUnload && _autoMiningUnloadLease != _worker.ConfirmedLeaseIdentity)
            {
                _autoMiningUnload = false;
                Echo("Automatic mining unload stopped: the lease changed.");
            }
            if (_autoMiningUnload && (updateSource & UpdateType.Update10) != 0 && ++_miningUnloadTicks >= 6)
            {
                _miningUnloadTicks = 0;
                _autoMiningUnload = UnloadMiningCargo(completeWhenReady: true);
            }
            if ((updateSource & UpdateType.Update10) != 0)
            {
                UpdateAutomaticRetreat();
                UpdateAutomaticMiningReturn();
                CheckLeasedMiningDock();
                UpdateAutomaticMiningDock();
                UpdateUnloadAfterMiningDock();
                if (command.Length == 0)
                    TryStartAutomaticSurvey();
            }
            UpdateSurveySweep();
            UpdateAutomaticSampling();
            CheckPlatformFlight();
            CheckMiningLease();
            _galnet.Flush();
            Echo(_worker.GetStatus());
            Echo(viaGalnet ? (online ? "Local Galnet relay available." : "Galnet relay unavailable or misconfigured.") :
                online ? "Radio available." : "Radio unavailable.");
            Echo(_lastProbe);
            if (_autoMiningUnload)
                Echo("Automatic mining unload active.");
            else if (_pendingUnloadDockLease != null)
                Echo("Automatic unload waiting for docking at grid " + _pendingUnloadDockGrid + ".");
            if (_drillingLease != null)
                Echo("Managed drills enabled for the current mining lease.");
            if (_surveyOre != null)
                Echo("Ore survey " + _surveyOre + ": " + _positiveViews + "/" + _surveyViews.Count +
                    " views reported ore, " + _oreHits.Count + " distinct positions.");
            if (_surveySweep)
                Echo("Survey sweep active. Remaining points: " + _surveyPlan.Count + ".");
            if (_autoSample)
                Echo("Automatic sampling active.");
            if (_sampleDetector != null)
                Echo("Waiting for ore detector refresh.");
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

        private void BeginAutomaticMiningUnload()
        {
            _miningUnloadTicks = 0;
            _autoMiningUnloadLease = _worker.ConfirmedLeaseIdentity;
            _autoMiningUnload = UnloadMiningCargo(completeWhenReady: true);
        }

        private void UpdateUnloadAfterMiningDock()
        {
            if (_pendingUnloadDockLease == null)
                return;
            if (_pendingUnloadDockLease != _worker.ConfirmedLeaseIdentity ||
                _worker.SupervisedCruiseRemote != 0 || _drillingLease != null)
            {
                _pendingUnloadDockLease = null;
                Echo("Automatic unload cancelled: mining lease or flight state changed.");
                return;
            }
            if (IsDockedAtMiningPoint(_pendingUnloadDockGrid))
            {
                _pendingUnloadDockLease = null;
                BeginAutomaticMiningUnload();
            }
            else if (++_unloadDockUpdates >= 180)
            {
                _pendingUnloadDockLease = null;
                Echo("Automatic unload cancelled: docking was not confirmed at the assigned mining point.");
            }
        }

        private bool UnloadMiningCargo(bool completeWhenReady = false)
        {
            long grid;
            string resource;
            double amount;
            if (_drillingLease != null || !_worker.TryGetMiningPoint(out grid) ||
                !_worker.TryGetMiningCargoTarget(out resource, out amount))
            {
                Echo("Stop the drills and confirm the mining assignment before unloading.");
                return false;
            }

            if (!IsDockedAtMiningPoint(grid))
            {
                Echo("Dock at the assigned mining point before unloading.");
                return false;
            }

            var source = FindMiningInventory();
            if (source == null || string.IsNullOrEmpty(_miningUnloadTo))
            {
                Echo("Configure MiningCargo and MiningUnloadTo before unloading.");
                return false;
            }
            _miningBuffers.Clear();
            GridTerminalSystem.GetBlocksOfType(_miningBuffers, container =>
                container.CubeGrid.EntityId == grid && container.IsFunctional &&
                string.Equals(container.CustomName, _miningUnloadTo, StringComparison.Ordinal));
            var destination = _miningBuffers.Count == 1 ? _miningBuffers[0].GetInventory() : null;
            if (destination == null || !source.IsConnectedTo(destination))
            {
                Echo("The assigned mining point needs a unique, conveyor-connected unload container.");
                return false;
            }

            double remaining = amount - _worker.MiningDeposited;
            if (remaining <= 0.0000001)
            {
                if (completeWhenReady)
                    FinishMiningDeposit();
                else
                    Echo("The requested mining quantity has already been unloaded.");
                return false;
            }

            double bufferBefore = MeasureMiningCargo(destination, resource);
            double before = MeasureMiningCargo(source, resource);
            double attempted = 0;
            foreach (var item in _miningItems)
            {
                if (!MatchesMiningResource(item, resource))
                    continue;

                var quantity = (VRage.MyFixedPoint)Math.Min(remaining, (double)item.Amount);
                if ((double)quantity > 0)
                {
                    source.TransferItemTo(destination, item, quantity);
                    attempted = (double)quantity;
                }
                break;
            }
            double transferred = attempted > 0
                ? Math.Min(attempted, Math.Min(before - MeasureMiningCargo(source, resource),
                    MeasureMiningCargo(destination, resource) - bufferBefore))
                : 0;
            if (transferred > 0)
            {
                _worker.RecordMiningDeposit(transferred);
                Echo("Mining deposit: " + _worker.MiningDeposited.ToString("G", CultureInfo.InvariantCulture) +
                    " / " + amount.ToString("G", CultureInfo.InvariantCulture) + " " + resource + ".");
                if (completeWhenReady && _worker.MiningDeposited + 0.0000001 >= amount)
                {
                    FinishMiningDeposit();
                    return false;
                }
                return true;
            }

            Echo("No requested ore transferred. Check cargo, available space and conveyor permissions.");
            return false;
        }

        private bool IsDockedAtMiningPoint(long grid)
        {
            if (grid == Me.CubeGrid.EntityId)
                return false;
            var connector = GridTerminalSystem.GetBlockWithName("[FCS DOCK]") as IMyShipConnector;
            var partner = connector == null || connector.CubeGrid != Me.CubeGrid ||
                connector.Status != MyShipConnectorStatus.Connected ? null : connector.OtherConnector;
            return partner != null && partner.CubeGrid.EntityId == grid;
        }

        private void FinishMiningDeposit()
        {
            long grid;
            if (_drillingLease != null || !_worker.TryGetMiningPoint(out grid) || !IsDockedAtMiningPoint(grid))
            {
                Echo("Stop the drills and dock at the assigned mining point under a confirmed lease.");
                return;
            }
            if (!_worker.TryCompleteMiningDeposit())
                Echo("Unload the requested quantity with mine unload before completing mining.");
        }

        private void RequestMiningPointDock()
        {
            long grid;
            if (_drillingLease != null || !_worker.TryGetMiningPoint(out grid))
            {
                Echo("Stop the drills and confirm an assignment with a mining point before docking.");
                return;
            }

            if (_fcsDockLease != null)
            {
                Echo("A leased docking request is already pending.");
                return;
            }
            long deadline = _worker.ConfirmedLeaseDeadlineUtc;
            if (deadline <= DateTime.UtcNow.Ticks)
            {
                Echo("Docking requires an active broker lease with a UTC deadline.");
                return;
            }
            if (!TryRunMiningFcs("dock leased " + grid.ToString(CultureInfo.InvariantCulture) + " " +
                deadline.ToString(CultureInfo.InvariantCulture)))
            {
                Echo("Could not send docking to the local [AMN FCS] block.");
                return;
            }
            _cancelFcsDock = false;
            _fcsDockLease = _worker.ConfirmedLeaseIdentity;
            _fcsDockDeadlineUtc = deadline;
            _fcsDockRequestId = deadline;
            _fcsDockLimitUtc = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(30).Ticks;
            _fcsDockGrid = grid;
            if (_unloadOnDock && _miningUnloadTo != null && _miningCargoName != null)
            {
                _pendingUnloadDockLease = _worker.ConfirmedLeaseIdentity;
                _pendingUnloadDockGrid = grid;
                _unloadDockUpdates = 0;
            }
            Echo("FCS docking requested at mining point grid " + grid + ". Check the FCS status.");
        }

        private bool TryRunMiningFcs(string command)
        {
            var fcs = GridTerminalSystem.GetBlockWithName("[AMN FCS]") as IMyProgrammableBlock;
            return fcs != null && fcs.CubeGrid == Me.CubeGrid && fcs.EntityId != Me.EntityId &&
                fcs.IsWorking && fcs.TryRun(command);
        }

        private void CheckLeasedMiningDock(bool cancel = false)
        {
            if (_fcsDockLease == null)
                return;
            if (DateTime.UtcNow.Ticks >= _fcsDockDeadlineUtc || IsDockedAtMiningPoint(_fcsDockGrid))
            {
                _fcsDockLease = null;
                return;
            }
            _cancelFcsDock |= cancel || _fcsDockLease != _worker.ConfirmedLeaseIdentity;
            string requestId = _fcsDockRequestId.ToString(CultureInfo.InvariantCulture);
            if (_cancelFcsDock)
            {
                if (TryRunMiningFcs("dock lease stop " + requestId))
                    _fcsDockLease = null;
                return;
            }

            long deadline = Math.Min(_fcsDockLimitUtc, _worker.ConfirmedLeaseDeadlineUtc);
            if (deadline > _fcsDockDeadlineUtc &&
                TryRunMiningFcs("dock lease refresh " + requestId + " " + deadline.ToString(CultureInfo.InvariantCulture)))
                _fcsDockDeadlineUtc = deadline;
        }

        private void FindManagedDrills()
        {
            _managedDrills.Clear();
            GridTerminalSystem.GetBlocksOfType(_managedDrills, drill =>
                drill.CubeGrid == Me.CubeGrid && drill.CustomName.StartsWith(DrillPrefix, StringComparison.Ordinal));
        }

        private bool StartManagedDrills()
        {
            string taskId;
            double x;
            double y;
            double z;
            if (_worker.SupervisedCruiseRemote != 0 || !_worker.TryGetMiningApproach(out taskId, out x, out y, out z))
            {
                Echo("A confirmed mining lease with a verified approach is required.");
                return false;
            }
            _surveyConnectors.Clear();
            GridTerminalSystem.GetBlocksOfType(_surveyConnectors,
                connector => connector.CubeGrid == Me.CubeGrid && connector.Status == MyShipConnectorStatus.Connected);
            if (_surveyConnectors.Count != 0)
            {
                Echo("Undock before enabling the managed drills.");
                return false;
            }

            if (!CheckMiningLane(x, y, z) || !CheckMiningCargo())
            {
                Echo(_lastProbe);
                return false;
            }
            FindManagedDrills();
            if (_managedDrills.Count == 0)
            {
                Echo("Name the managed local drills with the " + DrillPrefix + " prefix.");
                return false;
            }
            _drillingLease = _worker.ConfirmedLeaseIdentity;
            foreach (var drill in _managedDrills)
                drill.Enabled = true;
            return true;
        }

        private void CheckMiningLease()
        {
            if (_drillingLease == null)
                return;
            string taskId;
            double x;
            double y;
            double z;
            if (_drillingLease != _worker.ConfirmedLeaseIdentity ||
                !_worker.TryGetMiningApproach(out taskId, out x, out y, out z))
            {
                StopManagedDrills();
                _lastProbe = "Managed drills stopped: the mining lease or stage changed.";
            }
            else if (!CheckMiningLane(x, y, z) || !CheckMiningCargo())
                StopManagedDrills();
        }

        private bool CheckMiningLane(double approachX, double approachY, double approachZ, Vector3D? position = null)
        {
            if (!_miningLaneRadius.HasValue)
                return true;
            if (double.IsNaN(_miningLaneRadius.Value))
            {
                _lastProbe = "Set MiningLaneRadius to a value between 1 and 100 metres.";
                return false;
            }

            double oreX;
            double oreY;
            double oreZ;
            if (!_worker.TryGetMiningOrePosition(out oreX, out oreY, out oreZ))
            {
                _lastProbe = "Mining lane requires a confirmed ore position.";
                return false;
            }
            var start = new Vector3D(approachX, approachY, approachZ);
            var axis = new Vector3D(oreX, oreY, oreZ) - start;
            double lengthSquared = axis.LengthSquared();
            if (lengthSquared < 0.01 || double.IsInfinity(lengthSquared))
            {
                _lastProbe = "Mining lane needs distinct approach and ore positions.";
                return false;
            }
            var offset = (position ?? Me.CubeGrid.GetPosition()) - start;
            double progress = Math.Max(0, Math.Min(1, Vector3D.Dot(offset, axis) / lengthSquared));
            double radius = _miningLaneRadius.Value;
            if ((offset - progress * axis).LengthSquared() <= radius * radius)
                return true;

            _lastProbe = "Managed drills halted: platform outside the configured mining lane.";
            return false;
        }

        private bool CheckMiningCargo()
        {
            if (_miningCargoName == null)
                return true;

            var inventory = FindMiningInventory();
            string resource;
            double target;
            if (inventory == null || !_worker.TryGetMiningCargoTarget(out resource, out target))
            {
                _lastProbe = "Managed drills halted: cargo inventory or mining target unavailable.";
                return false;
            }
            double collected = MeasureMiningCargo(inventory, resource);
            double deposited = _worker.MiningDeposited;
            _lastProbe = "Mining cargo " + resource + ": " + collected.ToString("G", CultureInfo.InvariantCulture) +
                " aboard, " + deposited.ToString("G", CultureInfo.InvariantCulture) +
                " deposited / " + target.ToString("G", CultureInfo.InvariantCulture);
            if (collected + deposited >= target)
            {
                _lastProbe += ". Target reached; drills halted.";
                return false;
            }
            if ((double)inventory.CurrentVolume >= 0.95 * (double)inventory.MaxVolume)
            {
                _lastProbe += ". Container at least 95% full; drills halted.";
                return false;
            }
            return true;
        }

        private double MeasureMiningCargo(IMyInventory inventory, string resource)
        {
            double total = 0;
            _miningItems.Clear();
            inventory.GetItems(_miningItems);
            foreach (var item in _miningItems)
            {
                if (MatchesMiningResource(item, resource))
                    total += (double)item.Amount;
            }
            return total;
        }

        private IMyInventory FindMiningInventory()
        {
            var container = string.IsNullOrEmpty(_miningCargoName) ? null :
                GridTerminalSystem.GetBlockWithName(_miningCargoName) as IMyCargoContainer;
            return container != null && container.CubeGrid == Me.CubeGrid && container.IsFunctional
                ? container.GetInventory() : null;
        }

        private static bool MatchesMiningResource(MyInventoryItem item, string resource)
        {
            var type = item.Type.TypeId.ToString().Replace("MyObjectBuilder_", string.Empty);
            return string.Equals(type + "/" + item.Type.SubtypeId, resource, StringComparison.OrdinalIgnoreCase);
        }

        private void StopManagedDrills()
        {
            FindManagedDrills();
            foreach (var drill in _managedDrills)
                drill.Enabled = false;
            _drillingLease = null;
        }

        private void BeginTaskSurvey()
        {
            string lease;
            string ore;
            if (!_worker.TryGetSurveyLease(out lease, out ore))
            {
                _lastProbe = "A confirmed survey lease for ore is required.";
                return;
            }
            if (_surveyLease == lease && string.Equals(_surveyOre, ore, StringComparison.OrdinalIgnoreCase))
            {
                _lastProbe = "Survey already active for this lease. Move to a new sampling position.";
                return;
            }
            BeginOreSurvey(ore, lease);
        }

        private bool SurveyLeaseIsCurrent()
        {
            string lease;
            string ore;
            return _surveyLease == null || (_worker.TryGetSurveyLease(out lease, out ore) &&
                lease == _surveyLease && string.Equals(ore, _surveyOre, StringComparison.OrdinalIgnoreCase));
        }

        private void BeginOreSurvey(string ore, string lease = null)
        {
            if (string.IsNullOrWhiteSpace(ore) || ore.Length > 64)
            {
                _lastProbe = "Use ore survey <ore name>.";
                return;
            }
            _surveyOre = ore;
            _surveyLease = lease;
            _autoSample = false;
            _surveySweep = false;
            _surveyViews.Clear();
            _surveyPlan.Clear();
            _oreHits.Clear();
            _reportOre = null;
            _reportView = null;
            _positiveViews = 0;
            _sampleDetector = null;
            SaveSurvey();
            _lastProbe = "Survey started. Move to a survey position and run ore sample.";
        }

        private void LoadSurvey(string saved)
        {
            var rows = saved.Split('\n');
            try
            {
                var ore = TaskBusIgcHelper.DecodeStorage(rows[0]);
                if (string.IsNullOrWhiteSpace(ore) || ore.Length > 64)
                    return;
                _surveyOre = ore;
            }
            catch (FormatException) { return; }

            for (int i = 2; i < rows.Length; i++)
            {
                if (rows[i].StartsWith("lease|", StringComparison.Ordinal))
                {
                    try { _surveyLease = TaskBusIgcHelper.DecodeStorage(rows[i].Substring(6)); }
                    catch (FormatException) { _surveyLease = ""; }
                    continue;
                }
                string kind;
                double x;
                double y;
                double z;
                if (!TaskBusIgcHelper.TryReadOreGps(rows[i], out kind, out x, out y, out z))
                    continue;
                if ((kind == "view" || kind == "viewOre" || kind == "viewNoReport") &&
                    _surveyViews.Count < MaxSurveyViews)
                {
                    bool? foundOre = kind == "view" ? (bool?)null : kind == "viewOre";
                    _surveyViews.Add(new SurveyView(new Vector3D(x, y, z), foundOre));
                }
                else if (kind == "hit" && _oreHits.Count < MaxOreHits)
                    _oreHits.Add(new Vector3D(x, y, z));
                else if (kind == "plan" && _surveyPlan.Count < MaxSurveyPlanPoints)
                    _surveyPlan.Add(new Vector3D(x, y, z));
                else if (kind == "reportOre")
                    _reportOre = new Vector3D(x, y, z);
                else if (kind == "reportView")
                    _reportView = new Vector3D(x, y, z);
            }
            int positives;
            if (rows.Length > 1 && int.TryParse(rows[1].TrimEnd('\r'), NumberStyles.None, CultureInfo.InvariantCulture, out positives))
                _positiveViews = Math.Min(positives, _surveyViews.Count);
            _lastProbe = "Saved survey restored. Move to a new position to resume sampling.";
        }

        private void SaveSurvey()
        {
            var saved = new StringBuilder(SurveyStorageMarker);
            saved.AppendLine(TaskBusIgcHelper.EncodeStorage(_surveyOre));
            saved.AppendLine(_positiveViews.ToString(CultureInfo.InvariantCulture));
            if (_surveyLease != null)
                saved.AppendLine("lease|" + TaskBusIgcHelper.EncodeStorage(_surveyLease));
            foreach (var point in _surveyPlan)
                saved.AppendLine(FormatSurveyGps("plan", point));
            foreach (var view in _surveyViews)
                saved.AppendLine(FormatSurveyGps(
                    !view.FoundOre.HasValue ? "view" : view.FoundOre.Value ? "viewOre" : "viewNoReport",
                    view.Position));
            foreach (var hit in _oreHits)
                saved.AppendLine(FormatSurveyGps("hit", hit));
            if (_reportOre.HasValue && _reportView.HasValue)
            {
                saved.AppendLine(FormatSurveyGps("reportOre", _reportOre.Value));
                saved.AppendLine(FormatSurveyGps("reportView", _reportView.Value));
            }
            _surveyStorage = saved.ToString();
            Storage = _workerStorage + _surveyStorage;
        }

        private void ShowSurveySamples()
        {
            if (_surveyViews.Count == 0)
            {
                Echo("No completed ore detector samples.");
                return;
            }

            Echo(_surveyOre + " detector samples (" + _positiveViews + "/" + _surveyViews.Count + " reported ore):");
            for (int i = 0; i < _surveyViews.Count; i++)
            {
                var view = _surveyViews[i];
                var result = !view.FoundOre.HasValue ? "unknown" :
                    view.FoundOre.Value ? "reported" : "no report";
                Echo(FormatSurveyGps("Sample " + (i + 1) + " " + result, view.Position));
            }
        }

        private void SubmitOreSurvey()
        {
            if (_surveyLease == null || !SurveyLeaseIsCurrent() || _surveyViews.Count == 0 || _sampleDetector != null)
            {
                _lastProbe = "A confirmed task survey and a completed sample are required.";
                return;
            }

            var report = _surveyOre + ": " + _positiveViews + "/" + _surveyViews.Count +
                " positive samples, " + _oreHits.Count + " distinct reports. Coverage unknown.";
            if (_reportOre.HasValue && _reportView.HasValue)
            {
                report += " " + FormatSurveyGps(_surveyOre, _reportOre.Value);
                var observation = " | " + FormatSurveyGps("Observation", _reportView.Value);
                if (report.Length + observation.Length <= 320)
                    report += observation;
            }
            else
            {
                foreach (var hit in _oreHits)
                {
                    report += " " + FormatSurveyGps(_surveyOre, hit);
                    break;
                }
            }
            _worker.CompleteSurvey(report);
            _lastProbe = "Survey report queued for delivery to the broker.";
            _autoSample = false;
            _surveySweep = false;
        }

        private static bool TryReadSurveyPlan(string argument, out double spacing, out Vector3D center)
        {
            spacing = 0;
            center = Vector3D.Zero;
            int split = argument.IndexOf(' ');
            double parsedSpacing;
            double x;
            double y;
            double z;
            string label;
            if (split < 1 || !double.TryParse(argument.Substring(0, split), NumberStyles.Float,
                CultureInfo.InvariantCulture, out parsedSpacing) ||
                !(parsedSpacing >= MinimumViewSeparation && parsedSpacing <= 500) ||
                !TaskBusIgcHelper.TryReadOreGps(argument.Substring(split + 1), out label, out x, out y, out z))
                return false;
            spacing = parsedSpacing;
            center = new Vector3D(x, y, z);
            return true;
        }

        private void PlanSurvey(string argument)
        {
            double spacing;
            Vector3D center;
            if (!TryReadSurveyPlan(argument, out spacing, out center))
            {
                Echo("Use ore plan <spacing in metres, 10 to 500> GPS:Center:X:Y:Z:.");
                return;
            }

            var controller = FindSurveyPlanningController();
            if (controller != null)
                PlanSurveyGrid(controller, spacing, center);
        }

        private void TryStartAutomaticSurvey()
        {
            string lease;
            string ore;
            if ((_automaticSurveySpacing == 0 && _asteroidScanRange == 0) || _surveySweep || _sampleDetector != null ||
                _worker.SupervisedCruiseRemote != 0 || !_worker.TryGetSurveyLease(out lease, out ore) ||
                lease == _surveyLease || lease == _suppressedAutomaticSurveyLease)
                return;

            var controller = GridTerminalSystem.GetBlockWithName(ControllerName) as IMyShipController;
            var remote = FindSurveyRemote();
            var detector = GridTerminalSystem.GetBlockWithName(DetectorName) as IMyOreDetector;
            if (controller == null || controller.CubeGrid != Me.CubeGrid || !controller.IsWorking ||
                remote == null || remote.FlightMode != FlightMode.OneWay ||
                detector == null || detector.CubeGrid != Me.CubeGrid || !detector.IsWorking ||
                !CanStartPlatformFlight())
                return;

            if (_automaticSurveySpacing != 0)
            {
                BeginOreSurvey(ore, lease);
                PlanSurveyGrid(controller, _automaticSurveySpacing, _automaticSurveyCenter);
                _lastProbe = "Automatic survey started for the configured area.";
            }
            else
            {
                if (controller.GetNaturalGravity().LengthSquared() > 0.0001 || controller.GetShipSpeed() > 0.25)
                {
                    _lastProbe = "Automatic asteroid survey waits for a stationary platform outside natural gravity.";
                    return;
                }
                if (++_asteroidScanTicks < 6)
                    return;
                _asteroidScanTicks = 0;
                if (!PlanAsteroidSurvey(ore, lease))
                    return;
            }
            BeginSurveySweep(reportOnCompletion: true, bracketOnCompletion: _automaticBracketGap > 0);
        }

        private void PlanSurveyGrid(IMyShipController controller, double spacing, Vector3D center)
        {
            Vector3D up;
            Vector3D right;
            Vector3D across;
            bool inGravity = GetSurveyAxes(controller, out up, out right, out across);
            Echo(inGravity ? "Survey plan aligned to local gravity." : "Survey plan aligned to ship orientation.");
            SaveSurveyGrid(spacing, center, right, across);
        }

        private static bool TryReadAsteroidSurvey(string setting, out double range, out double standOff, out double spacing)
        {
            range = standOff = spacing = 0;
            var parts = setting.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            double parsedRange;
            double parsedStandOff;
            double parsedSpacing;
            if (parts.Length != 3 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out parsedRange) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out parsedStandOff) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out parsedSpacing) ||
                !(parsedRange <= 5000 && parsedStandOff >= 10 && parsedStandOff < parsedRange &&
                    parsedSpacing >= MinimumViewSeparation && parsedSpacing <= 500))
                return false;
            range = parsedRange;
            standOff = parsedStandOff;
            spacing = parsedSpacing;
            return true;
        }

        private bool PlanAsteroidSurvey(string ore, string lease)
        {
            _surveyCameras.Clear();
            GridTerminalSystem.GetBlocksOfType(_surveyCameras, camera =>
                camera.CubeGrid == Me.CubeGrid && camera.IsWorking &&
                camera.CustomName.StartsWith(CameraName, StringComparison.Ordinal));
            foreach (var camera in _surveyCameras)
            {
                camera.EnableRaycast = true;
                if (!camera.CanScan(_asteroidScanRange))
                    continue;
                var hit = camera.Raycast(_asteroidScanRange);
                if (hit.Type != MyDetectedEntityType.Asteroid || !hit.HitPosition.HasValue ||
                    Vector3D.DistanceSquared(hit.HitPosition.Value, camera.GetPosition()) <= _asteroidStandOff * _asteroidStandOff)
                    continue;

                var frame = camera.WorldMatrix;
                var center = hit.HitPosition.Value - frame.Forward * _asteroidStandOff;
                BeginOreSurvey(ore, lease);
                SaveSurveyGrid(_asteroidSurveySpacing, center, frame.Right, frame.Up);
                _lastProbe = "Automatic asteroid survey started from a camera hit.";
                return true;
            }
            _lastProbe = "Automatic asteroid survey waiting for a camera to detect a surface beyond the stand-off distance.";
            return false;
        }

        private void PlanSurveyCandidate(string argument)
        {
            var parts = argument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            double standOff;
            double spacing;
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out standOff) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out spacing) ||
                !(standOff >= 10 && standOff <= 500 && spacing >= MinimumViewSeparation && spacing <= 500))
            {
                Echo("Use ore candidate <stand-off> <spacing>, both in metres from 10 to 500.");
                return;
            }
            var controller = FindSurveyPlanningController();
            if (controller == null)
                return;
            double x;
            double y;
            double z;
            if (controller.GetNaturalGravity().LengthSquared() > 0.0001 || controller.GetShipSpeed() > 0.25 ||
                !_worker.TryGetSurveyCandidate(out x, out y, out z))
            {
                Echo("Candidate planning needs a matching ore GPS and a stationary platform outside natural gravity.");
                return;
            }

            var candidate = new Vector3D(x, y, z);
            _surveyCameras.Clear();
            GridTerminalSystem.GetBlocksOfType(_surveyCameras, camera =>
                camera.CubeGrid == Me.CubeGrid && camera.IsWorking &&
                camera.CustomName.StartsWith(CameraName, StringComparison.Ordinal));
            foreach (var camera in _surveyCameras)
            {
                camera.EnableRaycast = true;
                var origin = camera.GetPosition();
                if (Vector3D.DistanceSquared(candidate, origin) > 5000.0 * 5000 || !camera.CanScan(candidate))
                    continue;
                var hit = camera.Raycast(candidate);
                if (hit.Type != MyDetectedEntityType.Asteroid || !hit.HitPosition.HasValue ||
                    Vector3D.DistanceSquared(hit.HitPosition.Value, candidate) > 150.0 * 150)
                    continue;

                var offset = hit.HitPosition.Value - origin;
                if (offset.LengthSquared() <= standOff * standOff)
                    continue;
                var direction = Vector3D.Normalize(offset);
                var reference = Math.Abs(Vector3D.Dot(direction, Vector3D.Up)) < 0.9
                    ? Vector3D.Up : Vector3D.Right;
                var right = Vector3D.Normalize(Vector3D.Cross(direction, reference));
                var up = Vector3D.Cross(right, direction);
                SaveSurveyGrid(spacing, hit.HitPosition.Value - direction * standOff, right, up);
                return;
            }
            Echo("No camera confirmed a nearby asteroid surface along the candidate ray. Check alignment and raycast charge.");
        }

        private void SaveSurveyGrid(double spacing, Vector3D center, Vector3D right, Vector3D across)
        {
            int index = 0;
            _surveyPlan.Clear();
            for (int row = -1; row <= 1; row++)
            {
                for (int column = -1; column <= 1; column++)
                {
                    int sweep = row == 0 ? -column : column;
                    var point = center + spacing * (sweep * right + row * across);
                    index++;
                    if (!IsNewSurveyPosition(point))
                        continue;
                    Echo(FormatSurveyGps("Survey " + index, point));
                    _surveyPlan.Add(point);
                }
            }
            SaveSurvey();
            Echo("Unvisited points: " + _surveyPlan.Count + ". Review terrain and approach clearance.");
        }

        private IMyShipController FindSurveyPlanningController()
        {
            if (_surveySweep || _sampleDetector != null ||
                _worker.SupervisedCruiseRemote != 0 || _surveyLease == null || !SurveyLeaseIsCurrent())
            {
                Echo("Stop the current flight or sample and start a confirmed task survey before planning.");
                return null;
            }
            var controller = GridTerminalSystem.GetBlockWithName(ControllerName) as IMyShipController;
            if (controller == null || controller.CubeGrid != Me.CubeGrid || !controller.IsWorking)
            {
                Echo("Planning requires a working local " + ControllerName + ".");
                return null;
            }
            return controller;
        }

        private void PlanPerimeterSurvey(string argument)
        {
            double offset;
            if (!double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out offset) ||
                !(offset >= MinimumViewSeparation && offset <= 500))
            {
                Echo("Use ore refine <outward offset in metres, 10 to 500>.");
                return;
            }
            var controller = FindSurveyPlanningController();
            if (controller == null)
                return;

            Vector3D up;
            Vector3D right;
            Vector3D across;
            GetSurveyAxes(controller, out up, out right, out across);
            var origin = controller.GetPosition();
            double lowest;
            double highest;
            var hull = BuildOrePerimeter(origin, up, right, across, out lowest, out highest);
            if (hull.Count < 3)
            {
                Echo("Collect at least three reports enclosing an area before refining.");
                return;
            }

            var lengths = new List<double>(hull.Count);
            double perimeter = 0;
            for (int i = 0; i < hull.Count; i++)
            {
                perimeter += (hull[(i + 1) % hull.Count] - hull[i]).Length();
                lengths.Add(perimeter);
            }

            _surveyPlan.Clear();
            int edgeIndex = 0;
            const int samples = MaxSurveyPlanPoints;
            for (int i = 0; i < samples && _surveyPlan.Count < MaxSurveyPlanPoints; i++)
            {
                double distance = perimeter * (i + 0.5) / samples;
                while (edgeIndex < hull.Count - 1 && lengths[edgeIndex] < distance)
                    edgeIndex++;
                var edgeStart = hull[edgeIndex];
                var edge = hull[(edgeIndex + 1) % hull.Count] - edgeStart;
                var direction = Vector3D.Normalize(edge);
                double along = distance - (edgeIndex == 0 ? 0 : lengths[edgeIndex - 1]);
                var point = edgeStart + direction * along +
                    new Vector3D(direction.Y, -direction.X, 0) * offset;
                var waypoint = origin + right * point.X + across * point.Y;
                if (!IsNewSurveyPosition(waypoint) || IsNearPlannedSurveyPosition(waypoint, _surveyPlan))
                    continue;
                _surveyPlan.Add(waypoint);
            }

            SaveSurvey();
            for (int i = 0; i < _surveyPlan.Count; i++)
                Echo(FormatSurveyGps("Perimeter survey " + (i + 1), _surveyPlan[i]));
            Echo("Unvisited points: " + _surveyPlan.Count + ". Review terrain and approach clearance.");
        }

        private static bool TryReadBracketGap(string argument, out double maxGap)
        {
            double gap;
            maxGap = 0;
            if (!double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out gap) ||
                !(gap >= 2 * MinimumViewSeparation && gap <= 500))
                return false;
            maxGap = gap;
            return true;
        }

        private void PlanSurveyBrackets(string argument)
        {
            double maxGap;
            if (!TryReadBracketGap(argument, out maxGap))
            {
                Echo("Use ore bracket <maximum sample gap in metres, 20 to 500>.");
                return;
            }
            if (FindSurveyPlanningController() != null && TryPlanSurveyBrackets(maxGap))
                Echo("Review terrain and approach clearance before running ore sweep.");
        }

        private bool TryPlanSurveyBrackets(double maxGap)
        {
            int maxPoints = Math.Min(MaxSurveyPlanPoints, MaxSurveyViews - _surveyViews.Count);
            if (maxPoints <= 0)
                return false;
            var remote = FindSurveyRemote();
            var detector = GridTerminalSystem.GetBlockWithName(DetectorName) as IMyOreDetector;
            if (remote == null || remote.IsAutoPilotEnabled || detector == null ||
                detector.CubeGrid != Me.CubeGrid || !detector.IsWorking)
            {
                Echo("Bracketing requires a working local detector and an idle survey remote.");
                return false;
            }

            var noReports = new List<Vector3D>();
            foreach (var view in _surveyViews)
            {
                if (view.FoundOre == false)
                    noReports.Add(view.Position);
            }
            var planned = new List<Vector3D>();
            var remoteOffset = remote.GetPosition() - detector.GetPosition();
            double minimumGap = 2 * MinimumViewSeparation;
            foreach (var view in _surveyViews)
            {
                if (view.FoundOre != true)
                    continue;

                double bestGapSquared = maxGap * maxGap;
                Vector3D? next = null;
                foreach (var noReport in noReports)
                {
                    double gapSquared = Vector3D.DistanceSquared(view.Position, noReport);
                    if (gapSquared > bestGapSquared)
                        continue;
                    next = view.Position + (noReport - view.Position) * 0.5;
                    bestGapSquared = gapSquared;
                }
                if (!next.HasValue || bestGapSquared < minimumGap * minimumGap || !IsNewSurveyPosition(next.Value))
                    continue;
                var waypoint = next.Value + remoteOffset;
                if (IsNearPlannedSurveyPosition(waypoint, planned))
                    continue;
                planned.Add(waypoint);
                if (planned.Count == maxPoints)
                    break;
            }

            if (planned.Count == 0)
            {
                Echo("No new midpoint between opposite detector readings within the selected gap.");
                return false;
            }
            _surveyPlan.Clear();
            _surveyPlan.AddRange(planned);
            SaveSurvey();
            for (int i = 0; i < _surveyPlan.Count; i++)
                Echo(FormatSurveyGps("Ore bracket " + (i + 1), _surveyPlan[i]));
            return true;
        }

        private static bool IsNearPlannedSurveyPosition(Vector3D position, List<Vector3D> plan)
        {
            foreach (var point in plan)
            {
                if (Vector3D.DistanceSquared(point, position) < MinimumViewSeparation * MinimumViewSeparation)
                    return true;
            }
            return false;
        }

        private IMyRemoteControl FindSurveyRemote()
        {
            var remote = GridTerminalSystem.GetBlockWithName(SurveyRemoteName) as IMyRemoteControl;
            return remote != null && remote.CubeGrid == Me.CubeGrid && remote.IsWorking ? remote : null;
        }

        private void LoadNextSurveyWaypoint()
        {
            if (_surveyLease == null || !SurveyLeaseIsCurrent() || _surveyPlan.Count == 0)
            {
                Echo("A confirmed survey lease and a pending survey plan are required.");
                return;
            }

            var remote = FindSurveyRemote();
            if (remote == null || remote.IsAutoPilotEnabled || remote.FlightMode != FlightMode.OneWay)
            {
                Echo("Use a working " + SurveyRemoteName + " in One Way mode with autopilot off.");
                return;
            }

            _surveyWaypoints.Clear();
            remote.GetWaypointInfo(_surveyWaypoints);
            if (_surveyWaypoints.Count > 1 ||
                (_surveyWaypoints.Count == 1 && _surveyWaypoints[0].Name != SurveyWaypointName))
            {
                Echo("Clear other waypoints from the dedicated survey controller first.");
                return;
            }

            if (_surveyWaypoints.Count == 0 || _surveyWaypoints[0].Coords != _surveyPlan[0])
            {
                remote.ClearWaypoints();
                remote.AddWaypoint(_surveyPlan[0], SurveyWaypointName);
            }
            Echo("Survey waypoint ready. Review clearance before starting the one-way flight.");
        }

        private void StartSurveyFlight()
        {
            var remote = FindSurveyRemote();
            if (_worker.SupervisedCruiseRemote != 0 || _surveyPlan.Count == 0 || _surveyLease == null ||
                !SurveyLeaseIsCurrent() || _sampleDetector != null || remote == null || remote.FlightMode != FlightMode.OneWay)
            {
                Echo("A confirmed survey plan, a One Way survey controller and no active sample or leg are required.");
                return;
            }

            if (!CanStartPlatformFlight())
                return;

            _surveyWaypoints.Clear();
            remote.GetWaypointInfo(_surveyWaypoints);
            if (_surveyWaypoints.Count != 1 || _surveyWaypoints[0].Name != SurveyWaypointName ||
                _surveyWaypoints[0].Coords != _surveyPlan[0])
            {
                Echo("Load and review the next waypoint with ore route.");
                return;
            }

            StartPlatformFlight(remote, "Survey leg", true);
        }

        private bool CanStartPlatformFlight()
        {
            _surveyControllers.Clear();
            GridTerminalSystem.GetBlocksOfType(_surveyControllers, controller => controller.CubeGrid == Me.CubeGrid);
            foreach (var controller in _surveyControllers)
            {
                var otherRemote = controller as IMyRemoteControl;
                if (controller.IsUnderControl || (otherRemote != null && otherRemote.IsAutoPilotEnabled))
                {
                    Echo("Release pilot control and stop other autopilots before starting autopilot.");
                    return false;
                }
            }
            _surveyConnectors.Clear();
            GridTerminalSystem.GetBlocksOfType(_surveyConnectors,
                connector => connector.CubeGrid == Me.CubeGrid && connector.Status == MyShipConnectorStatus.Connected);
            if (_surveyConnectors.Count != 0)
            {
                Echo("Undock before starting autopilot.");
                return false;
            }

            return true;
        }

        private void StartMiningCruise(bool returning)
        {
            if (_drillingLease != null || _worker.SupervisedCruiseRemote != 0 || !CanStartPlatformFlight())
            {
                Echo("Stop drilling and release flight controls before starting the mining flight.");
                return;
            }

            if (returning)
                _routes.LoadReturnRoute(StartMiningReturnFlight);
            else
                _routes.LoadMiningApproach(remote => StartPlatformFlight(remote, "Mining approach", false));
        }

        private void StartMiningReturnFlight(IMyRemoteControl remote)
        {
            _pendingMiningDockLease = null;
            _surveyWaypoints.Clear();
            remote.GetWaypointInfo(_surveyWaypoints);
            if (_surveyWaypoints.Count == 0)
                return;

            _miningReturnEndpoint = _surveyWaypoints[_surveyWaypoints.Count - 1].Coords;
            StartPlatformFlight(remote, "Mining return", false);
            _platformMiningReturn = _worker.SupervisedCruiseRemote == remote.EntityId;
        }

        private void LoadAssignedMiningEndpoint()
        {
            string taskId;
            double approachX;
            double approachY;
            double approachZ;
            double oreX;
            double oreY;
            double oreZ;
            if (!_miningLaneRadius.HasValue || _miningCargoName == null || _drillingLease != null ||
                _worker.SupervisedCruiseRemote != 0 ||
                !_worker.TryGetMiningApproach(out taskId, out approachX, out approachY, out approachZ) ||
                !_worker.TryGetMiningOrePosition(out oreX, out oreY, out oreZ))
            {
                Echo("Planning needs a confirmed mining assignment, a configured lane and cargo, and idle drills.");
                return;
            }

            var remote = GridTerminalSystem.GetBlockWithName(MiningPassRemoteName) as IMyRemoteControl;
            var approach = new Vector3D(approachX, approachY, approachZ);
            if (remote == null || remote.CubeGrid != Me.CubeGrid || !remote.IsWorking || remote.IsAutoPilotEnabled ||
                remote.FlightMode != FlightMode.OneWay || !(remote.SpeedLimit > 0 && remote.SpeedLimit <= 1) ||
                remote.GetShipSpeed() > 0.25 || Vector3D.DistanceSquared(remote.GetPosition(), approach) > 25)
            {
                Echo("Settle at the assigned approach with an idle One Way " + MiningPassRemoteName + " at 1 m/s or less.");
                return;
            }

            var ore = new Vector3D(oreX, oreY, oreZ);
            var gridOrigin = Me.CubeGrid.GetPosition();
            if (!CheckMiningLane(approachX, approachY, approachZ, gridOrigin))
            {
                Echo(_lastProbe);
                return;
            }

            // The lane constrains the grid origin, but the Remote Control flies its own position.
            var endpoint = ore + (remote.GetPosition() - gridOrigin);
            if (Vector3D.DistanceSquared(remote.GetPosition(), endpoint) <= 4)
            {
                Echo("The platform is already within 2 m of the assigned ore endpoint.");
                return;
            }

            _surveyWaypoints.Clear();
            remote.GetWaypointInfo(_surveyWaypoints);
            if (_surveyWaypoints.Count > 1 ||
                (_surveyWaypoints.Count == 1 && _surveyWaypoints[0].Name != MiningEndpointName))
            {
                Echo("The mining controller has a manually prepared route. Clear it before generating an endpoint.");
                return;
            }
            remote.ClearWaypoints();
            remote.AddWaypoint(endpoint, MiningEndpointName);
            Echo(FormatSurveyGps(MiningEndpointName, endpoint));
            Echo("Review the loaded endpoint, ship alignment and drill clearance before running mine pass.");
        }

        private void StartMiningPass()
        {
            string taskId;
            double x;
            double y;
            double z;
            if (!_miningLaneRadius.HasValue || _miningCargoName == null || _drillingLease != null ||
                _worker.SupervisedCruiseRemote != 0 || !_worker.TryGetMiningApproach(out taskId, out x, out y, out z) ||
                !CanStartPlatformFlight())
            {
                Echo("A confirmed mining approach, configured lane and cargo, and idle flight controls are required.");
                return;
            }

            var remote = GridTerminalSystem.GetBlockWithName(MiningPassRemoteName) as IMyRemoteControl;
            var approach = new Vector3D(x, y, z);
            if (remote == null || remote.CubeGrid != Me.CubeGrid || !remote.IsWorking ||
                remote.FlightMode != FlightMode.OneWay || !(remote.SpeedLimit > 0 && remote.SpeedLimit <= 1) ||
                remote.GetShipSpeed() > 0.25 || Vector3D.DistanceSquared(remote.GetPosition(), approach) > 25)
            {
                Echo("Use a local One Way " + MiningPassRemoteName +
                    " at no more than 1 m/s. Settle within 5 m of the ore approach.");
                return;
            }

            _surveyWaypoints.Clear();
            remote.GetWaypointInfo(_surveyWaypoints);
            if (_surveyWaypoints.Count != 1 ||
                !CheckMiningLane(x, y, z, _surveyWaypoints[0].Coords + Me.CubeGrid.GetPosition() - remote.GetPosition()))
            {
                Echo("Load one reviewed mining endpoint inside the assigned lane.");
                return;
            }

            if (_surveyWaypoints[0].Name == MiningEndpointName)
            {
                double oreX;
                double oreY;
                double oreZ;
                var plannedGridPosition = _surveyWaypoints[0].Coords + Me.CubeGrid.GetPosition() - remote.GetPosition();
                if (!_worker.TryGetMiningOrePosition(out oreX, out oreY, out oreZ) ||
                    Vector3D.DistanceSquared(plannedGridPosition, new Vector3D(oreX, oreY, oreZ)) > 4)
                {
                    Echo("The generated endpoint no longer matches the assignment or ship alignment. Run mine plan again.");
                    return;
                }
            }

            _platformMiningPass = true;
            _miningPassEndpoint = _surveyWaypoints[0].Coords;
            _miningPassDistance = Vector3D.Distance(remote.GetPosition(), _miningPassEndpoint);
            try
            {
                if (StartManagedDrills())
                    StartPlatformFlight(remote, "Mining pass", false);
            }
            finally
            {
                if (_worker.SupervisedCruiseRemote == 0 || !remote.IsAutoPilotEnabled)
                    FinishMiningPass();
            }
        }

        private IMyRemoteControl FindMiningRetreatController()
        {
            var remote = GridTerminalSystem.GetBlockWithName(MiningRetreatRemoteName) as IMyRemoteControl;
            if (remote != null && remote.CubeGrid == Me.CubeGrid && remote.IsWorking && !remote.IsAutoPilotEnabled &&
                remote.Direction == Base6Directions.Direction.Backward && remote.FlightMode == FlightMode.OneWay &&
                remote.SpeedLimit > 0 && remote.SpeedLimit <= 1 && remote.GetShipSpeed() <= 0.25)
                return remote;

            Echo("Use an idle local One Way " + MiningRetreatRemoteName + " facing Backward at no more than 1 m/s.");
            return null;
        }

        private bool TryGetMiningRetreatEndpoint(IMyRemoteControl retreat, Vector3D approach, out Vector3D endpoint)
        {
            endpoint = approach;
            var pass = GridTerminalSystem.GetBlockWithName(MiningPassRemoteName) as IMyRemoteControl;
            if (pass == null || pass.CubeGrid != Me.CubeGrid || !pass.IsWorking)
            {
                Echo("The generated retreat requires a working local " + MiningPassRemoteName + ".");
                return false;
            }
            endpoint += retreat.GetPosition() - pass.GetPosition();
            return true;
        }

        private void LoadAssignedMiningRetreat()
        {
            string taskId;
            double x;
            double y;
            double z;
            if (!_miningLaneRadius.HasValue || _drillingLease != null || _worker.SupervisedCruiseRemote != 0 ||
                !_worker.TryGetMiningApproach(out taskId, out x, out y, out z) || !CheckMiningLane(x, y, z))
            {
                Echo("Stop the drills and settle inside the assigned mining lane under a confirmed lease.");
                return;
            }

            var remote = FindMiningRetreatController();
            Vector3D endpoint;
            if (remote == null || !TryGetMiningRetreatEndpoint(remote, new Vector3D(x, y, z), out endpoint))
                return;
            _surveyWaypoints.Clear();
            remote.GetWaypointInfo(_surveyWaypoints);
            if (_surveyWaypoints.Count > 1 ||
                (_surveyWaypoints.Count == 1 && _surveyWaypoints[0].Name != MiningRetreatWaypointName))
            {
                Echo("The retreat controller has a manually prepared route. Clear it before generating an endpoint.");
                return;
            }
            remote.ClearWaypoints();
            remote.AddWaypoint(endpoint, MiningRetreatWaypointName);
            Echo(FormatSurveyGps(MiningRetreatWaypointName, endpoint));
            Echo("Review the retreat path and alignment before running mine retreat.");
        }

        private void StartMiningRetreat()
        {
            string taskId;
            double x;
            double y;
            double z;
            if (!_miningLaneRadius.HasValue || _drillingLease != null || _worker.SupervisedCruiseRemote != 0 ||
                !_worker.TryGetMiningApproach(out taskId, out x, out y, out z) ||
                !CheckMiningLane(x, y, z) || !CanStartPlatformFlight())
            {
                Echo("Stop the drills and settle inside the assigned mining lane under a confirmed lease.");
                return;
            }

            var remote = FindMiningRetreatController();
            if (remote == null)
                return;

            _surveyWaypoints.Clear();
            remote.GetWaypointInfo(_surveyWaypoints);
            if (_surveyWaypoints.Count != 1)
            {
                Echo("Configure exactly one reviewed retreat waypoint.");
                return;
            }
            var waypoint = _surveyWaypoints[0];
            var approach = new Vector3D(x, y, z);
            var expected = approach;
            if (waypoint.Name == MiningRetreatWaypointName && !TryGetMiningRetreatEndpoint(remote, approach, out expected))
                return;
            if (Vector3D.DistanceSquared(waypoint.Coords, expected) > 1)
            {
                Echo("The retreat waypoint does not match the assigned approach and current alignment.");
                return;
            }
            var toApproach = waypoint.Coords - remote.GetPosition();
            if (toApproach.LengthSquared() <= 4 ||
                Vector3D.Dot(Vector3D.Normalize(toApproach), remote.WorldMatrix.Backward) < 0.996)
            {
                Echo("Align the rear of the retreat controller with the retreat waypoint before starting.");
                return;
            }

            _platformMiningRetreat = true;
            _miningPassEndpoint = _surveyWaypoints[0].Coords;
            _miningPassDistance = Vector3D.Distance(remote.GetPosition(), _miningPassEndpoint);
            StartPlatformFlight(remote, "Mining retreat", false);
            if (_worker.SupervisedCruiseRemote == 0)
                _platformMiningRetreat = false;
        }

        private void CheckPlatformFlightProgress()
        {
            if (_worker.SupervisedCruiseRemote == 0 || _platformFlightLease == null ||
                ++_flightProgressTicks < FlightProgressCheckUpdates)
                return;

            _flightProgressTicks = 0;
            var remote = GridTerminalSystem.GetBlockWithId(_worker.SupervisedCruiseRemote) as IMyRemoteControl;
            if (remote == null || remote.CubeGrid != Me.CubeGrid || !remote.IsAutoPilotEnabled)
                return;

            var position = remote.GetPosition();
            if (_platformMiningPass || _platformMiningRetreat)
            {
                double distance = Vector3D.Distance(position, _miningPassEndpoint);
                if (_miningPassDistance - distance >= MinimumFlightProgress)
                {
                    _miningPassDistance = distance;
                    return;
                }
            }
            else if (Vector3D.DistanceSquared(position, _flightProgressPosition) >=
                MinimumFlightProgress * MinimumFlightProgress)
            {
                // Multi-waypoint routes may temporarily move away from their final destination.
                _flightProgressPosition = position;
                return;
            }

            _platformFlightLease = null;
            if (_platformMiningPass)
                StopManagedDrills();
            _lastProbe = "Platform movement halted: no progress on the supervised leg.";
        }

        private void FinishMiningPass()
        {
            if (!_platformMiningPass)
                return;
            _platformMiningPass = false;
            StopManagedDrills();
        }

        private void StartPlatformFlight(IMyRemoteControl remote, string leg, bool survey)
        {
            _worker.SetSupervisedCruiseRemote(remote.EntityId);
            _platformFlightLease = _worker.ConfirmedLeaseIdentity;
            _platformSurveyFlight = survey;
            _platformMiningReturn = false;
            _flightProgressTicks = 0;
            _flightProgressPosition = remote.GetPosition();
            remote.SetAutoPilotEnabled(true);
            if (!remote.IsAutoPilotEnabled)
                _worker.SetSupervisedCruiseRemote(0);
            Echo(remote.IsAutoPilotEnabled ? leg + " started." : leg + " autopilot did not engage.");
        }

        private void CheckPlatformFlight()
        {
            long id = _worker.SupervisedCruiseRemote;
            if (id == 0)
                return;

            var remote = GridTerminalSystem.GetBlockWithId(id) as IMyRemoteControl;
            if (remote == null || remote.CubeGrid != Me.CubeGrid || !remote.IsAutoPilotEnabled)
            {
                if (_autoMiningRetreat && _platformMiningPass && _platformFlightLease != null && remote != null &&
                    remote.CubeGrid == Me.CubeGrid && remote.IsWorking &&
                    _drillingLease != null && _drillingLease == _worker.ConfirmedLeaseIdentity &&
                    Vector3D.DistanceSquared(remote.GetPosition(), _miningPassEndpoint) <= 4)
                {
                    _pendingRetreatLease = _drillingLease;
                    _retreatSettleUpdates = 0;
                }
                if (_autoMiningReturn && _platformMiningRetreat && _platformFlightLease != null &&
                    _platformFlightLease == _worker.ConfirmedLeaseIdentity && remote != null &&
                    remote.CubeGrid == Me.CubeGrid && remote.IsWorking && CheckMiningFlightPosition(remote) &&
                    Vector3D.DistanceSquared(remote.GetPosition(), _miningPassEndpoint) <= 4)
                {
                    _pendingReturnLease = _platformFlightLease;
                    _returnSettleUpdates = 0;
                }
                if (_autoMiningDock && _platformMiningReturn && _platformFlightLease != null &&
                    _platformFlightLease == _worker.ConfirmedLeaseIdentity && remote != null &&
                    remote.CubeGrid == Me.CubeGrid && remote.IsWorking &&
                    Vector3D.DistanceSquared(remote.GetPosition(), _miningReturnEndpoint) <= 4)
                {
                    _pendingMiningDockLease = _platformFlightLease;
                    _miningDockReturnController = id;
                    _dockSettleUpdates = 0;
                }
                _worker.SetSupervisedCruiseRemote(0);
                FinishMiningPass();
                _platformMiningRetreat = false;
                _platformMiningReturn = false;
                return;
            }
            if (_platformMiningPass && (_drillingLease == null || remote.GetShipSpeed() > 1.5))
            {
                _platformFlightLease = null;
                remote.SetAutoPilotEnabled(false);
                if (!remote.IsAutoPilotEnabled)
                    _worker.SetSupervisedCruiseRemote(0);
                FinishMiningPass();
                return;
            }
            if (_platformFlightLease == null || _platformFlightLease != _worker.ConfirmedLeaseIdentity ||
                (_platformSurveyFlight ? !SurveyLeaseIsCurrent() : !CheckMiningFlightPosition(remote)))
            {
                remote.SetAutoPilotEnabled(false);
                if (!remote.IsAutoPilotEnabled)
                    _worker.SetSupervisedCruiseRemote(0);
                FinishMiningPass();
                _platformMiningRetreat = false;
                _platformMiningReturn = false;
                if (!_lastProbe.StartsWith("Platform movement halted", StringComparison.Ordinal))
                    _lastProbe = "Platform flight stopped. Check the lease and flight conditions.";
            }
        }

        private void UpdateAutomaticRetreat()
        {
            if (_pendingRetreatLease == null)
                return;
            if (_pendingRetreatLease != _worker.ConfirmedLeaseIdentity || _worker.SupervisedCruiseRemote != 0)
            {
                _pendingRetreatLease = null;
                return;
            }

            var remote = GridTerminalSystem.GetBlockWithName(MiningRetreatRemoteName) as IMyRemoteControl;
            if (remote != null && remote.CubeGrid == Me.CubeGrid && remote.IsWorking && remote.GetShipSpeed() <= 0.25)
            {
                _pendingRetreatLease = null;
                StartMiningRetreat();
            }
            else if (++_retreatSettleUpdates >= 30)
            {
                _pendingRetreatLease = null;
                _lastProbe = "Automatic retreat cancelled: the platform did not settle or the retreat controller is unavailable.";
            }
        }

        private void UpdateAutomaticMiningReturn()
        {
            if (_pendingReturnLease == null)
                return;
            if (_pendingReturnLease != _worker.ConfirmedLeaseIdentity || _worker.SupervisedCruiseRemote != 0)
            {
                _pendingReturnLease = null;
                return;
            }

            var remote = GridTerminalSystem.GetBlockWithName(MiningRetreatRemoteName) as IMyRemoteControl;
            if (remote != null && remote.CubeGrid == Me.CubeGrid && remote.IsWorking &&
                !remote.IsAutoPilotEnabled && remote.GetShipSpeed() <= 0.25 &&
                Vector3D.DistanceSquared(remote.GetPosition(), _miningPassEndpoint) <= 4)
            {
                _pendingReturnLease = null;
                StartMiningCruise(returning: true);
            }
            else if (++_returnSettleUpdates >= 30)
            {
                _pendingReturnLease = null;
                _lastProbe = "Automatic return cancelled: the platform did not settle at the retreat waypoint.";
            }
        }

        private void UpdateAutomaticMiningDock()
        {
            if (_pendingMiningDockLease == null)
                return;
            if (_pendingMiningDockLease != _worker.ConfirmedLeaseIdentity || _worker.SupervisedCruiseRemote != 0)
            {
                _pendingMiningDockLease = null;
                return;
            }

            var remote = GridTerminalSystem.GetBlockWithId(_miningDockReturnController) as IMyRemoteControl;
            if (remote != null && remote.CubeGrid == Me.CubeGrid && remote.IsWorking &&
                !remote.IsAutoPilotEnabled && remote.GetShipSpeed() <= 0.25 &&
                Vector3D.DistanceSquared(remote.GetPosition(), _miningReturnEndpoint) <= 4)
            {
                _pendingMiningDockLease = null;
                if (CanStartPlatformFlight())
                    RequestMiningPointDock();
            }
            else if (++_dockSettleUpdates >= 30)
            {
                _pendingMiningDockLease = null;
                _lastProbe = "Automatic docking cancelled: the platform did not settle at the return waypoint.";
            }
        }

        private bool CheckMiningFlightPosition(IMyRemoteControl remote)
        {
            string taskId;
            double x;
            double y;
            double z;
            return _worker.TryGetMiningApproach(out taskId, out x, out y, out z) &&
                (!_platformMiningRetreat || (remote.GetShipSpeed() <= 1.5 && CheckMiningLane(x, y, z)));
        }

        private void BeginSurveySweep(bool reportOnCompletion, bool bracketOnCompletion = false)
        {
            if (_surveySweep || _surveyLease == null || !SurveyLeaseIsCurrent() ||
                _surveyPlan.Count == 0 || _worker.SupervisedCruiseRemote != 0 || _sampleDetector != null)
            {
                Echo("A confirmed, reviewed survey plan and an idle platform are required.");
                return;
            }

            _autoSample = false;
            _sweepLegStarted = false;
            _sweepSampleStarted = false;
            _sweepSettleTicks = 0;
            _sweepScanTicks = 0;
            _sweepClearanceIndex = 0;
            _reportOnSweepCompletion = reportOnCompletion;
            _bracketOnSweepCompletion = bracketOnCompletion;
            _surveySweep = true;
        }

        private void UpdateSurveySweep()
        {
            if (!_surveySweep)
                return;
            if (!SurveyLeaseIsCurrent())
            {
                _surveySweep = false;
                _lastProbe = "Survey sweep halted: the survey lease is no longer confirmed.";
                return;
            }
            if (_surveyPlan.Count == 0)
            {
                _surveySweep = false;
                if (_bracketOnSweepCompletion && TryPlanSurveyBrackets(_automaticBracketGap))
                {
                    BeginSurveySweep(reportOnCompletion: true);
                    _lastProbe = "Automatic survey is refining the boundary between detector observations.";
                    return;
                }
                if (_reportOnSweepCompletion)
                    SubmitOreSurvey();
                else
                    _lastProbe = "Survey sweep complete. Review the samples or submit an ore report.";
                return;
            }
            if (_worker.SupervisedCruiseRemote != 0 || _sampleDetector != null)
                return;
            if (_sweepSampleStarted)
            {
                _surveySweep = false;
                Echo("Survey sweep halted after an incomplete sample.");
                return;
            }

            var remote = FindSurveyRemote();
            if (remote == null || remote.IsAutoPilotEnabled)
            {
                _surveySweep = false;
                Echo("Survey sweep halted: the survey controller is unavailable or already flying.");
                return;
            }
            if (Vector3D.DistanceSquared(remote.GetPosition(), _surveyPlan[0]) <= 4)
            {
                if (remote.GetShipSpeed() > 0.25)
                {
                    if (++_sweepSettleTicks >= 30)
                    {
                        _surveySweep = false;
                        Echo("Survey sweep halted: platform did not settle at the waypoint.");
                    }
                    return;
                }

                ScheduleOreSample();
                _sweepSampleStarted = true;
                return;
            }

            if (_sweepLegStarted)
            {
                _surveySweep = false;
                Echo("Survey sweep halted outside the waypoint tolerance.");
                return;
            }

            if (!CheckSurveyLegClearance(remote, _surveyPlan[0]))
                return;

            LoadNextSurveyWaypoint();
            StartSurveyFlight();
            _sweepLegStarted = true;
            if (_worker.SupervisedCruiseRemote == 0)
                _surveySweep = false;
        }

        private bool CheckSurveyLegClearance(IMyRemoteControl remote, Vector3D target)
        {
            if (_surveyClearanceRadius > 0 && remote.GetShipSpeed() > 0.25)
            {
                _sweepClearanceIndex = 0;
                if (++_sweepScanTicks >= 60)
                {
                    _surveySweep = false;
                    _lastProbe = "Survey sweep halted: platform did not settle for corridor scans.";
                }
                return false;
            }

            var origin = remote.GetPosition();
            if (_sweepClearanceIndex == 0 || Vector3D.DistanceSquared(origin, _sweepClearanceOrigin) > 0.25)
            {
                _sweepClearanceIndex = 0;
                _sweepClearanceOrigin = origin;
                var direction = Vector3D.Normalize(target - origin);
                var reference = Math.Abs(Vector3D.Dot(direction, Vector3D.Up)) < 0.9
                    ? Vector3D.Up : Vector3D.Right;
                _sweepClearanceRight = Vector3D.Normalize(Vector3D.Cross(direction, reference)) * _surveyClearanceRadius;
                _sweepClearanceUp = Vector3D.Cross(_sweepClearanceRight, direction);
            }

            int requiredScans = _surveyClearanceRadius > 0 ? 5 : 1;
            var scanTarget = target;
            switch (_sweepClearanceIndex)
            {
                case 1: scanTarget += _sweepClearanceRight; break;
                case 2: scanTarget -= _sweepClearanceRight; break;
                case 3: scanTarget += _sweepClearanceUp; break;
                case 4: scanTarget -= _sweepClearanceUp; break;
            }

            _surveyCameras.Clear();
            GridTerminalSystem.GetBlocksOfType(_surveyCameras, camera =>
                camera.CubeGrid == Me.CubeGrid && camera.IsWorking &&
                camera.CustomName.StartsWith(CameraName, StringComparison.Ordinal));

            foreach (var camera in _surveyCameras)
            {
                camera.EnableRaycast = true;
                if (!camera.CanScan(scanTarget))
                    continue;

                var hit = camera.Raycast(scanTarget);
                if (hit.IsEmpty())
                {
                    if (++_sweepClearanceIndex == requiredScans)
                        return true;
                    break;
                }

                _surveySweep = false;
                _lastProbe = "Survey sweep halted: camera detected " + hit.Type + " before the next waypoint.";
                return false;
            }

            if (++_sweepScanTicks >= 60 || _surveyCameras.Count == 0)
            {
                _surveySweep = false;
                _lastProbe = "Survey sweep halted: no survey camera could scan the next waypoint.";
            }
            else
                _lastProbe = "Survey clearance scans " + _sweepClearanceIndex + "/" + requiredScans +
                    ". Waiting for camera range and alignment.";
            return false;
        }

        private void CompletePlannedSurveyPosition()
        {
            if (_surveyPlan.Count == 0)
                return;
            var remote = FindSurveyRemote();
            if (remote != null && !remote.IsAutoPilotEnabled &&
                Vector3D.DistanceSquared(remote.GetPosition(), _surveyPlan[0]) <= 4)
            {
                _surveyPlan.RemoveAt(0);
                _sweepLegStarted = false;
                _sweepSampleStarted = false;
                _sweepSettleTicks = 0;
                _sweepScanTicks = 0;
                _sweepClearanceIndex = 0;
            }
        }

        private bool IsNewSurveyPosition(Vector3D position)
        {
            foreach (var view in _surveyViews)
            {
                if (Vector3D.DistanceSquared(view.Position, position) < MinimumViewSeparation * MinimumViewSeparation)
                    return false;
            }
            return true;
        }

        private void ShowOreBounds()
        {
            if (_oreHits.Count == 0)
            {
                Echo("No ore positions reported. Survey coverage unknown.");
                return;
            }

            var min = new Vector3D(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
            var max = new Vector3D(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
            foreach (var hit in _oreHits)
            {
                min = Vector3D.Min(min, hit);
                max = Vector3D.Max(max, hit);
            }

            Echo("Observed " + _surveyOre + " coordinate bounds (" + _oreHits.Count + " distinct positions):");
            Echo(FormatSurveyGps("Observed minimum", min));
            Echo(FormatSurveyGps("Observed maximum", max));
            Echo("Box corners are reference points. Coverage between and beyond reports is unknown.");
            if (_oreHits.Count >= MaxOreHits)
                Echo("Observation limit reached. The box may omit additional reports.");
        }

        private static bool GetSurveyAxes(IMyShipController controller, out Vector3D up,
            out Vector3D right, out Vector3D across)
        {
            var gravity = controller.GetNaturalGravity();
            var frame = controller.WorldMatrix;
            bool inGravity = gravity.LengthSquared() > 0.0001;
            up = inGravity ? Vector3D.Normalize(-gravity) : frame.Up;
            right = frame.Right - up * Vector3D.Dot(frame.Right, up);
            if (right.LengthSquared() < 0.01)
                right = frame.Forward - up * Vector3D.Dot(frame.Forward, up);
            right = Vector3D.Normalize(right);
            across = Vector3D.Cross(up, right);
            return inGravity;
        }

        private void ShowOrePerimeter()
        {
            var controller = GridTerminalSystem.GetBlockWithName(ControllerName) as IMyShipController;
            if (controller == null || controller.CubeGrid != Me.CubeGrid || !controller.IsWorking)
            {
                Echo("Ore perimeter requires a working local " + ControllerName + ".");
                return;
            }
            if (_oreHits.Count < 3)
            {
                Echo("Ore perimeter needs at least three distinct reported positions.");
                return;
            }

            Vector3D up;
            Vector3D right;
            Vector3D across;
            bool inGravity = GetSurveyAxes(controller, out up, out right, out across);
            var origin = controller.GetPosition();
            double lowest;
            double highest;
            var hull = BuildOrePerimeter(origin, up, right, across, out lowest, out highest);
            if (hull.Count < 3)
            {
                Echo("Reported positions do not enclose an area in the survey plane.");
                return;
            }

            double heightMiddle = (lowest + highest) * 0.5;
            Echo("Observed " + _surveyOre + " perimeter (" + hull.Count + " vertices, " +
                (inGravity ? "gravity plane" : "ship plane") + ").");
            Echo("Reported vertical span: " + (highest - lowest).ToString("0.0", CultureInfo.InvariantCulture) + " m.");
            for (int i = 0; i < hull.Count; i++)
            {
                var point = hull[i];
                var vertex = origin + point.X * right + point.Y * across + heightMiddle * up;
                Echo(FormatSurveyGps("Ore perimeter " + (i + 1), vertex));
            }
            Echo("Interior coverage is unknown. Use these GPS points for survey planning.");
        }

        private List<Vector3D> BuildOrePerimeter(Vector3D origin, Vector3D up, Vector3D right,
            Vector3D across, out double lowest, out double highest)
        {
            var points = new List<Vector3D>(_oreHits.Count);
            lowest = double.PositiveInfinity;
            highest = double.NegativeInfinity;
            foreach (var hit in _oreHits)
            {
                var offset = hit - origin;
                double height = Vector3D.Dot(offset, up);
                lowest = Math.Min(lowest, height);
                highest = Math.Max(highest, height);
                points.Add(new Vector3D(Vector3D.Dot(offset, right), Vector3D.Dot(offset, across), 0));
            }
            points.Sort((first, second) =>
                first.X == second.X ? first.Y.CompareTo(second.Y) : first.X.CompareTo(second.X));
            var hull = new List<Vector3D>();
            foreach (var point in points)
                AppendPerimeterPoint(hull, point, 0);
            int upperStart = hull.Count - 1;
            for (int i = points.Count - 2; i >= 0; i--)
                AppendPerimeterPoint(hull, points[i], upperStart);
            if (hull.Count != 0)
                hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        private static void AppendPerimeterPoint(List<Vector3D> hull, Vector3D point, int chainStart)
        {
            while (hull.Count >= chainStart + 2)
            {
                var corner = hull[hull.Count - 1];
                var previous = hull[hull.Count - 2];
                var first = corner - previous;
                var second = point - corner;
                if (first.X * second.Y - first.Y * second.X > 0.000001)
                    break;
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(point);
        }

        private static string FormatSurveyGps(string label, Vector3D position)
        {
            var culture = CultureInfo.InvariantCulture;
            return "GPS:" + label + ":" + position.X.ToString("R", culture) + ":" +
                position.Y.ToString("R", culture) + ":" + position.Z.ToString("R", culture) + ":";
        }

        private void UpdateAutomaticSampling()
        {
            if (_surveySweep || !_autoSample || _worker.SupervisedCruiseRemote != 0 || _sampleDetector != null || _surveyViews.Count >= MaxSurveyViews ||
                !SurveyLeaseIsCurrent() || ++_autoSampleTicks < 6)
                return;

            _autoSampleTicks = 0;
            var controller = GridTerminalSystem.GetBlockWithName(ControllerName) as IMyShipController;
            if (controller == null || controller.CubeGrid != Me.CubeGrid || !controller.IsWorking)
            {
                _lastProbe = "Automatic sampling requires a working local " + ControllerName + ".";
                return;
            }
            if (controller.GetShipSpeed() <= 0.25)
                ScheduleOreSample(automatic: true);
        }

        private void ScheduleOreSample(bool automatic = false)
        {
            if (_surveyOre == null || _sampleDetector != null || _surveyViews.Count >= MaxSurveyViews ||
                _worker.SupervisedCruiseRemote != 0)
            {
                _lastProbe = "Start an ore survey, or wait for the current sample.";
                return;
            }

            if (!SurveyLeaseIsCurrent())
            {
                _lastProbe = "Wait for the survey lease to be confirmed or start the current task with ore task.";
                return;
            }

            var detector = GridTerminalSystem.GetBlockWithName(DetectorName) as IMyOreDetector;
            if (detector == null || detector.CubeGrid != Me.CubeGrid || !detector.IsWorking)
            {
                _lastProbe = "A working local " + DetectorName + " detector is required.";
                return;
            }

            var position = detector.GetPosition();
            if (!IsNewSurveyPosition(position))
            {
                if (!automatic)
                    _lastProbe = "Move at least 10 m from previous survey positions.";
                return;
            }
            _sampleDetector = detector;
            _samplePosition = position;
            _sampleWait = 0;
        }

        private void UpdateOreSample()
        {
            if (_sampleDetector == null)
                return;
            if (!SurveyLeaseIsCurrent())
            {
                _sampleDetector = null;
                _lastProbe = "Sample cancelled: survey lease is no longer confirmed.";
                return;
            }
            try
            {
                if (!_sampleDetector.IsWorking || _sampleDetector.CubeGrid != Me.CubeGrid ||
                    Vector3D.DistanceSquared(_sampleDetector.GetPosition(), _samplePosition) > 4)
                {
                    _lastProbe = "Sample cancelled: detector unavailable or moved more than 2 m.";
                    _sampleDetector = null;
                    return;
                }

                _sampleWait += Runtime.TimeSinceLastRun.TotalSeconds;
                if (_sampleWait < OreSampleWaitSeconds)
                    return;

                var property = _sampleDetector.GetProperty("Survey.DetectedOre")
                    as Sandbox.ModAPI.Interfaces.ITerminalProperty<string>;
                var reports = property == null ? null : property.GetValue(_sampleDetector);
                _sampleDetector = null;
                if (property == null)
                {
                    _lastProbe = "Ore detector survey API is unavailable.";
                    return;
                }

                bool hit = false;
                foreach (var report in (reports ?? "").Split('\n'))
                {
                    string ore;
                    double x;
                    double y;
                    double z;
                    if (TaskBusIgcHelper.TryReadOreGps(report, out ore, out x, out y, out z) &&
                        string.Equals(ore, _surveyOre, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = true;
                        var orePosition = new Vector3D(x, y, z);
                        if (_oreHits.Count < MaxOreHits) _oreHits.Add(orePosition);
                        if (!_reportOre.HasValue || !_reportView.HasValue)
                        {
                            _reportOre = orePosition;
                            _reportView = Me.CubeGrid.GetPosition();
                        }
                    }
                }
                _surveyViews.Add(new SurveyView(_samplePosition, hit));
                CompletePlannedSurveyPosition();
                if (hit)
                    _positiveViews++;
                SaveSurvey();
                _lastProbe = hit ? "Detector reported the requested ore." : "No matching report. Coverage unknown.";
            }
            catch (Exception error)
            {
                _sampleDetector = null;
                _lastProbe = "Ore sample failed: " + error.Message;
            }
        }

        private IMyCameraBlock FindCamera()
        {
            var camera = GridTerminalSystem.GetBlockWithName(CameraName) as IMyCameraBlock;
            return camera != null && camera.CubeGrid == Me.CubeGrid && camera.IsFunctional ? camera : null;
        }

        private void Probe(string argument)
        {
            double distance;
            if (!double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out distance) ||
                !(distance >= 1 && distance <= 5000))
            {
                _lastProbe = "Use probe <1 to 5000 metres>.";
                return;
            }

            var controller = GridTerminalSystem.GetBlockWithName(ControllerName) as IMyShipController;
            var camera = FindCamera();
            if (controller == null || controller.CubeGrid != Me.CubeGrid || !controller.IsWorking ||
                camera == null || !camera.IsWorking)
            {
                _lastProbe = "Probe requires a working " + ControllerName + " and " + CameraName + ".";
                return;
            }

            camera.EnableRaycast = true;
            if (!camera.CanScan(distance))
            {
                _lastProbe = "Probe unavailable at the requested range.";
                return;
            }

            bool inGravity = controller.GetNaturalGravity().LengthSquared() > 0.0001;
            var hit = camera.Raycast(distance);
            var environment = inGravity ? "Gravity" : "Space";
            if (hit.IsEmpty() || !hit.HitPosition.HasValue)
            {
                _lastProbe = environment + ": no hit on this ray.";
                return;
            }

            var point = hit.HitPosition.Value;
            _lastProbe = environment + ": " + hit.Type + " " + hit.EntityId + "\n" +
                "Hit X/Y/Z: " + point.X.ToString("0.00", CultureInfo.InvariantCulture) + " / " +
                point.Y.ToString("0.00", CultureInfo.InvariantCulture) + " / " +
                point.Z.ToString("0.00", CultureInfo.InvariantCulture);
        }

        #region PreludeFooter
    }
}
#endregion
