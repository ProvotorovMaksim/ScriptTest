using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using VRageMath;
using VRage.Game.ModAPI.Ingame;

namespace SpaceEngineers.Libs.TaskBusConsumer
{
    public sealed class MiningRouteLoader
    {
        private const string RouteControllerName = "[AMN ROUTE]";
        private const string ReturnControllerName = "[AMN RETURN]";
        private const string RouteWaypointPrefix = "AMN approach ";
        private readonly List<MyWaypointInfo> _routeWaypoints = new List<MyWaypointInfo>();
        private readonly List<MyWaypointInfo> _returnWaypoints = new List<MyWaypointInfo>();
        private readonly TaskBusConsumer _worker;
        private readonly IMyCubeGrid _grid;
        private readonly Func<string, IMyTerminalBlock> _findBlock;
        private readonly Action<string> _echo;

        public MiningRouteLoader(TaskBusConsumer worker, IMyCubeGrid grid,
            Func<string, IMyTerminalBlock> findBlock, Action<string> echo)
        {
            _worker = worker;
            _grid = grid;
            _findBlock = findBlock;
            _echo = echo;
        }

        public bool TryHandle(string command)
        {
            if (string.Equals(command, "route approach", StringComparison.OrdinalIgnoreCase))
                LoadMiningApproach();
            else if (string.Equals(command, "route return", StringComparison.OrdinalIgnoreCase))
                LoadReturnRoute();
            else
                return false;
            return true;
        }

        private void LoadMiningApproach()
        {
            string taskId;
            double x;
            double y;
            double z;
            if (!_worker.TryGetMiningApproach(out taskId, out x, out y, out z))
            {
                _echo("A current mining lease with an approach waypoint is required.");
                return;
            }

            try
            {
                var remote = _findBlock(RouteControllerName) as IMyRemoteControl;
                if (remote == null || remote.CubeGrid != _grid || !remote.IsWorking || remote.IsAutoPilotEnabled)
                {
                    _echo("Use a working " + RouteControllerName + " remote with autopilot off.");
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
                    _echo(waypoint.Name == waypointName && waypoint.Coords == position ?
                        "Mining approach already loaded." : "Remove the previous AMN approach before loading this task.");
                    return;
                }
                remote.AddWaypoint(position, waypointName);
                _echo("Mining approach appended. Review the route before enabling autopilot.");
            }
            catch (Exception error)
            {
                _echo("Could not load mining approach: " + error.Message);
            }
        }

        private void LoadReturnRoute()
        {
            string taskId;
            double x;
            double y;
            double z;
            if (!_worker.TryGetMiningApproach(out taskId, out x, out y, out z))
            {
                _echo("A current mining lease with an approach waypoint is required.");
                return;
            }

            IMyRemoteControl inbound = null;
            bool writing = false;
            try
            {
                var outbound = _findBlock(RouteControllerName) as IMyRemoteControl;
                inbound = _findBlock(ReturnControllerName) as IMyRemoteControl;
                if (outbound == null || inbound == null || outbound.CubeGrid != _grid ||
                    inbound.CubeGrid != _grid || !outbound.IsWorking || !inbound.IsWorking ||
                    outbound.IsAutoPilotEnabled || inbound.IsAutoPilotEnabled)
                {
                    _echo("Use local AMN route and return controllers with autopilot off.");
                    return;
                }

                _routeWaypoints.Clear();
                outbound.GetWaypointInfo(_routeWaypoints);
                if (_routeWaypoints.Count < 2)
                {
                    _echo("The outbound route needs at least one return waypoint before the AMN approach.");
                    return;
                }
                var last = _routeWaypoints[_routeWaypoints.Count - 1];
                if (last.Name != RouteWaypointPrefix + taskId || last.Coords != new Vector3D(x, y, z))
                {
                    _echo("The outbound route does not end at this assignment's approach.");
                    return;
                }

                _returnWaypoints.Clear();
                inbound.GetWaypointInfo(_returnWaypoints);
                if (_returnWaypoints.Count != 0)
                {
                    _echo("Clear " + ReturnControllerName + " waypoints before loading a return route.");
                    return;
                }

                writing = true;
                for (int i = _routeWaypoints.Count - 2; i >= 0; i--)
                {
                    var waypoint = _routeWaypoints[i];
                    inbound.AddWaypoint(waypoint.Coords, waypoint.Name);
                }
                _echo("Return route loaded. Review it before enabling autopilot.");
            }
            catch (Exception error)
            {
                if (writing)
                {
                    try { inbound.ClearWaypoints(); }
                    catch (Exception cleanup) { _echo("Could not clear incomplete return route: " + cleanup.Message); }
                }
                _echo("Could not load return route: " + error.Message);
            }
        }
    }
}
