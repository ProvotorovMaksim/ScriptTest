#region Prelude
using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Libs.TaskBusConsumer;

namespace SpaceEngineers.Apps.Platform
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string Channel = "SE1-SHIP-LINK";
        private const string ControllerName = "[AMN PLATFORM]";
        private const string CameraName = "[AMN SCOUT]";
        private readonly List<IMyRadioAntenna> _antennas = new List<IMyRadioAntenna>();
        private readonly TaskBusConsumer _worker;
        private readonly IMyBroadcastListener _listener;
        private string _lastProbe = "No scan yet.";

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update10;
            _listener = IGC.RegisterBroadcastListener(Channel);
            _worker = new TaskBusConsumer(
                (envelope, _) => IGC.SendBroadcastMessage(Channel, envelope, TransmissionDistance.TransmissionDistanceMax),
                () => Storage, value => Storage = value, message => Echo(message),
                Me.EntityId, Me.CubeGrid.CustomName);
            var camera = FindCamera();
            if (camera != null) camera.EnableRaycast = true;
        }

        public void Main(string argument)
        {
            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas,
                block => block.CubeGrid == Me.CubeGrid && block.IsWorking);
            bool online = _antennas.Count > 0;
            _worker.Tick(online);
            while (online && _listener.HasPendingMessage)
            {
                var message = _listener.AcceptMessage();
                if (message.Source != Me.EntityId)
                    _worker.ProcessIncomingMessage(message.Source, message.Data as string);
            }

            var command = (argument ?? "").Trim();
            if (command.StartsWith("probe ", StringComparison.OrdinalIgnoreCase))
                Probe(command.Substring(6));
            else
                _worker.ProcessCommand(command);

            Echo(_worker.GetStatus());
            Echo(online ? "Radio available." : "Radio unavailable.");
            Echo(_lastProbe);
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
