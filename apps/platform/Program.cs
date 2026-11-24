#region Prelude
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Libs.TaskBusConsumer;
using SpaceEngineers.Libs.IgcCore;
using VRageMath;

namespace SpaceEngineers.Apps.Platform
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string Channel = "SE1-SHIP-LINK";
        private readonly GalnetWorkerBridge _galnet;
        private const string ControllerName = "[AMN PLATFORM]";
        private const string SurveyRemoteName = "[AMN SURVEY ROUTE]";
        private const string SurveyWaypointName = "AMN survey sample";
        private const int MaxSurveyPlanPoints = 9;
        private readonly List<Vector3D> _surveyPlan = new List<Vector3D>();
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
        private readonly List<Vector3D> _surveyViews = new List<Vector3D>();
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
        private int _autoSampleTicks;
        private const string DetectorName = "[AMN ORE]";

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
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
            var camera = FindCamera();
            if (camera != null) camera.EnableRaycast = true;
        }

        public void Main(string argument)
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
            else if (string.Equals(command, "ore report", StringComparison.OrdinalIgnoreCase))
                SubmitOreSurvey();
            else if (command.StartsWith("ore plan ", StringComparison.OrdinalIgnoreCase))
                PlanSurvey(command.Substring(9).Trim());
            else if (string.Equals(command, "ore route", StringComparison.OrdinalIgnoreCase))
                LoadNextSurveyWaypoint();
            else if (string.Equals(command, "ore bounds", StringComparison.OrdinalIgnoreCase))
                ShowOreBounds();
            else if (!_routes.TryHandle(command))
                _worker.ProcessCommand(command);

            UpdateAutomaticSampling();
            _galnet.Flush();
            Echo(_worker.GetStatus());
            Echo(viaGalnet ? (online ? "Local Galnet relay available." : "Galnet relay unavailable or misconfigured.") :
                online ? "Radio available." : "Radio unavailable.");
            Echo(_lastProbe);
            if (_surveyOre != null)
                Echo("Ore survey " + _surveyOre + ": " + _positiveViews + "/" + _surveyViews.Count +
                    " views reported ore, " + _oreHits.Count + " distinct positions.");
            if (_autoSample)
                Echo("Automatic sampling active.");
            if (_sampleDetector != null)
                Echo("Waiting for ore detector refresh.");
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
                if (kind == "view" && _surveyViews.Count < MaxSurveyViews)
                    _surveyViews.Add(new Vector3D(x, y, z));
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
                saved.AppendLine(FormatSurveyGps("view", view));
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
            _autoSample = false;
        }

        private void PlanSurvey(string argument)
        {
            int split = argument.IndexOf(' ');
            double spacing;
            double x;
            double y;
            double z;
            string label;
            if (split < 1 || !double.TryParse(argument.Substring(0, split), NumberStyles.Float,
                CultureInfo.InvariantCulture, out spacing) || !(spacing >= MinimumViewSeparation && spacing <= 500) ||
                !TaskBusIgcHelper.TryReadOreGps(argument.Substring(split + 1), out label, out x, out y, out z))
            {
                Echo("Use ore plan <spacing in metres, 10 to 500> GPS:Center:X:Y:Z:.");
                return;
            }

            if (_surveyLease == null || !SurveyLeaseIsCurrent())
            {
                Echo("Start a confirmed task survey with ore task before planning.");
                return;
            }
            var controller = GridTerminalSystem.GetBlockWithName(ControllerName) as IMyShipController;
            if (controller == null || controller.CubeGrid != Me.CubeGrid || !controller.IsWorking)
            {
                Echo("Planning requires a working local " + ControllerName + ".");
                return;
            }

            var gravity = controller.GetNaturalGravity();
            var frame = controller.WorldMatrix;
            bool inGravity = gravity.LengthSquared() > 0.0001;
            var up = inGravity ? Vector3D.Normalize(-gravity) : frame.Up;
            var right = frame.Right - up * Vector3D.Dot(frame.Right, up);
            if (right.LengthSquared() < 0.01)
                right = frame.Forward - up * Vector3D.Dot(frame.Forward, up);
            right = Vector3D.Normalize(right);
            var across = Vector3D.Cross(up, right);
            var center = new Vector3D(x, y, z);
            Echo(inGravity ? "Survey plan aligned to local gravity." : "Survey plan aligned to ship orientation.");
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

        private void CompletePlannedSurveyPosition()
        {
            if (_surveyPlan.Count == 0)
                return;
            var remote = FindSurveyRemote();
            if (remote != null && !remote.IsAutoPilotEnabled &&
                Vector3D.DistanceSquared(remote.GetPosition(), _surveyPlan[0]) <= 4)
                _surveyPlan.RemoveAt(0);
        }

        private bool IsNewSurveyPosition(Vector3D position)
        {
            foreach (var view in _surveyViews)
            {
                if (Vector3D.DistanceSquared(view, position) < MinimumViewSeparation * MinimumViewSeparation)
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

        private static string FormatSurveyGps(string label, Vector3D position)
        {
            var culture = CultureInfo.InvariantCulture;
            return "GPS:" + label + ":" + position.X.ToString("R", culture) + ":" +
                position.Y.ToString("R", culture) + ":" + position.Z.ToString("R", culture) + ":";
        }

        private void UpdateAutomaticSampling()
        {
            if (!_autoSample || _sampleDetector != null || _surveyViews.Count >= MaxSurveyViews ||
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
            if (_surveyOre == null || _sampleDetector != null || _surveyViews.Count >= MaxSurveyViews)
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
                _surveyViews.Add(_samplePosition);
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
