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
        private Vector3D _samplePosition;
        private double _sampleWait;
        private int _positiveViews;
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
            if (_galnet.TryReceive(command, _worker))
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
            else if (command.StartsWith("ore survey ", StringComparison.OrdinalIgnoreCase))
                BeginOreSurvey(command.Substring(11).Trim());
            else if (string.Equals(command, "ore sample", StringComparison.OrdinalIgnoreCase))
                ScheduleOreSample();
            else if (string.Equals(command, "ore bounds", StringComparison.OrdinalIgnoreCase))
                ShowOreBounds();
            else if (!_routes.TryHandle(command))
                _worker.ProcessCommand(command);

            _galnet.Flush();
            Echo(_worker.GetStatus());
            Echo(viaGalnet ? (online ? "Local Galnet relay available." : "Galnet relay unavailable or misconfigured.") :
                online ? "Radio available." : "Radio unavailable.");
            Echo(_lastProbe);
            if (_surveyOre != null)
                Echo("Ore survey " + _surveyOre + ": " + _positiveViews + "/" + _surveyViews.Count +
                    " views reported ore, " + _oreHits.Count + " distinct positions.");
            if (_sampleDetector != null)
                Echo("Waiting for ore detector refresh.");
        }

        private void BeginOreSurvey(string ore)
        {
            if (string.IsNullOrWhiteSpace(ore) || ore.Length > 64)
            {
                _lastProbe = "Use ore survey <ore name>.";
                return;
            }
            _surveyOre = ore;
            _surveyViews.Clear();
            _oreHits.Clear();
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
            foreach (var view in _surveyViews)
                saved.AppendLine(FormatSurveyGps("view", view));
            foreach (var hit in _oreHits)
                saved.AppendLine(FormatSurveyGps("hit", hit));
            _surveyStorage = saved.ToString();
            Storage = _workerStorage + _surveyStorage;
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

        private void ScheduleOreSample()
        {
            if (_surveyOre == null || _sampleDetector != null || _surveyViews.Count >= MaxSurveyViews)
            {
                _lastProbe = "Start an ore survey, or wait for the current sample.";
                return;
            }

            var detector = GridTerminalSystem.GetBlockWithName(DetectorName) as IMyOreDetector;
            if (detector == null || detector.CubeGrid != Me.CubeGrid || !detector.IsWorking)
            {
                _lastProbe = "A working local " + DetectorName + " detector is required.";
                return;
            }

            var position = detector.GetPosition();
            foreach (var view in _surveyViews)
            {
                if (Vector3D.DistanceSquared(view, position) < MinimumViewSeparation * MinimumViewSeparation)
                {
                    _lastProbe = "Move at least 10 m from previous survey positions.";
                    return;
                }
            }
            _sampleDetector = detector;
            _samplePosition = position;
            _sampleWait = 0;
        }

        private void UpdateOreSample()
        {
            if (_sampleDetector == null)
                return;
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
                        if (_oreHits.Count < MaxOreHits) _oreHits.Add(new Vector3D(x, y, z));
                    }
                }
                _surveyViews.Add(_samplePosition);
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
