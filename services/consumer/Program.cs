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
        private const string ReturnControllerName = "[AMN RETURN]";
        private const string RouteWaypointPrefix = "AMN approach ";
        private const string MonitorName = "[AMN STATUS]";
        private int _monitorTicks;

        private readonly List<MyWaypointInfo> _routeWaypoints = new List<MyWaypointInfo>();
        private readonly List<MyWaypointInfo> _returnWaypoints = new List<MyWaypointInfo>();
        private readonly GalnetWorkerBridge _galnet;
        private readonly TaskBusConsumer _consumer;
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

            if (string.Equals(command, "route approach", StringComparison.OrdinalIgnoreCase))
                LoadMiningApproach();
            else if (string.Equals(command, "route return", StringComparison.OrdinalIgnoreCase))
                LoadReturnRoute();
            else
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

        private void LoadReturnRoute()
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

            IMyRemoteControl inbound = null;
            bool writing = false;
            try
            {
                var outbound = GridTerminalSystem.GetBlockWithName(RouteControllerName) as IMyRemoteControl;
                inbound = GridTerminalSystem.GetBlockWithName(ReturnControllerName) as IMyRemoteControl;
                if (outbound == null || inbound == null || outbound.CubeGrid != Me.CubeGrid ||
                    inbound.CubeGrid != Me.CubeGrid || !outbound.IsWorking || !inbound.IsWorking ||
                    outbound.IsAutoPilotEnabled || inbound.IsAutoPilotEnabled)
                {
                    Echo("Use local AMN route and return controllers with autopilot off.");
                    return;
                }

                _routeWaypoints.Clear();
                outbound.GetWaypointInfo(_routeWaypoints);
                if (_routeWaypoints.Count < 2)
                {
                    Echo("The outbound route needs at least one return waypoint before the AMN approach.");
                    return;
                }
                var last = _routeWaypoints[_routeWaypoints.Count - 1];
                if (last.Name != RouteWaypointPrefix + taskId || last.Coords != new Vector3D(x, y, z))
                {
                    Echo("The outbound route does not end at this assignment's approach.");
                    return;
                }

                _returnWaypoints.Clear();
                inbound.GetWaypointInfo(_returnWaypoints);
                if (_returnWaypoints.Count != 0)
                {
                    Echo("Clear " + ReturnControllerName + " waypoints before loading a return route.");
                    return;
                }

                writing = true;
                for (int i = _routeWaypoints.Count - 2; i >= 0; i--)
                {
                    var waypoint = _routeWaypoints[i];
                    inbound.AddWaypoint(waypoint.Coords, waypoint.Name);
                }
                Echo("Return route loaded. Review it before enabling autopilot.");
            }
            catch (Exception error)
            {
                if (writing)
                {
                    try { inbound.ClearWaypoints(); }
                    catch (Exception cleanup) { Echo("Could not clear incomplete return route: " + cleanup.Message); }
                }
                Echo("Could not load return route: " + error.Message);
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
