#region Prelude
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using System;
using VRage.Game.GUI.TextPanel;
using VRageMath;
using SpaceEngineers.Libs.TaskBusConsumer;

namespace SpaceEngineers.Services.Consumer
{
    public sealed class Program : MyGridProgram
    {
        #endregion
        private const string CommunicationTag = "SE1-SHIP-LINK";
        private const int PollIntervalTicks = 30;
        private const string RouteControllerName = "[AMN ROUTE]";
        private const string RouteWaypointPrefix = "AMN approach ";
        private const string MonitorName = "[AMN STATUS]";
        private int _monitorTicks;

        private readonly List<MyWaypointInfo> _routeWaypoints = new List<MyWaypointInfo>();
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
            _antennas.Clear();
            GridTerminalSystem.GetBlocksOfType(_antennas, antenna => antenna.IsFunctional && antenna.Enabled);
            bool connected = _antennas.Count > 0;
            _consumer.Tick(connected);
            while (connected && _listener.HasPendingMessage)
            {
                var message = _listener.AcceptMessage();
                if (message.Source != Me.EntityId)
                    _consumer.ProcessIncomingMessage(message.Source, message.Data as string);
            }

            if (string.Equals((argument ?? "").Trim(), "route approach", StringComparison.OrdinalIgnoreCase))
                LoadMiningApproach();
            else
                _consumer.ProcessCommand(argument);
            string status = "Worker block ID: " + Me.EntityId + "\n" + _consumer.GetStatus();
            if (!connected)
                status = "Radio unavailable.\n" + status;
            Echo(status);

            if (++_monitorTicks >= 6 || !string.IsNullOrWhiteSpace(argument))
            {
                _monitorTicks = 0;
                UpdateMonitor(status);
            }
        }

        private void LoadMiningApproach()
        {
            string taskId;
            double x;
            double y;
            double z;
            if (!_consumer.TryGetMiningApproach(out taskId, out x, out y, out z))
            {
                Echo("A current mining lease with an approach waypoint is required.");
                return;
            }

            try
            {
                var remote = GridTerminalSystem.GetBlockWithName(RouteControllerName) as IMyRemoteControl;
                if (remote == null || remote.CubeGrid != Me.CubeGrid || !remote.IsWorking || remote.IsAutoPilotEnabled)
                {
                    Echo("Use a working " + RouteControllerName + " remote with autopilot off.");
                    return;
                }

                var position = new Vector3D(x, y, z);
                var waypointName = RouteWaypointPrefix + taskId;
                _routeWaypoints.Clear();
                remote.GetWaypointInfo(_routeWaypoints);
                foreach (var waypoint in _routeWaypoints)
                {
                    if (!waypoint.Name.StartsWith(RouteWaypointPrefix, StringComparison.Ordinal))
                        continue;
                    Echo(waypoint.Name == waypointName && waypoint.Coords == position ?
                        "Mining approach already loaded." : "Remove the previous AMN approach before loading this task.");
                    return;
                }
                remote.AddWaypoint(position, waypointName);
                Echo("Mining approach appended. Review the route before enabling autopilot.");
            }
            catch (Exception error)
            {
                Echo("Could not load mining approach: " + error.Message);
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
