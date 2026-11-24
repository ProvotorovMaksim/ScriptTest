using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using VRageMath;
using VRage.Game.ModAPI.Ingame;
using SpaceEngineers.Libs.IgcCore;

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
        private readonly Func<long, string> _findStationApproach;
        private readonly Func<bool> _canStartCruise;
        private IMyRemoteControl _cruiseRemote;
        private string _cruiseLease;
        private bool _cruisePickup;
        private long _cruiseGrid;

        public MiningRouteLoader(TaskBusConsumer worker, IMyCubeGrid grid,
            Func<string, IMyTerminalBlock> findBlock, Action<string> echo, Func<long, string> findStationApproach = null,
            Func<bool> canStartCruise = null)
        {
            _worker = worker;
            _grid = grid;
            _findBlock = findBlock;
            _echo = echo;
            _findStationApproach = findStationApproach;
            _canStartCruise = canStartCruise;
        }

        public bool TryHandle(string command)
        {
            if (string.Equals(command, "route approach", StringComparison.OrdinalIgnoreCase))
                LoadMiningApproach();
            else if (string.Equals(command, "route station", StringComparison.OrdinalIgnoreCase) && _findStationApproach != null)
                LoadStationApproach(false);
            else if (string.Equals(command, "route cruise", StringComparison.OrdinalIgnoreCase) &&
                _findStationApproach != null && _canStartCruise != null)
                LoadStationApproach(true);
            else if (string.Equals(command, "route stop", StringComparison.OrdinalIgnoreCase) && _canStartCruise != null)
                StopCruise("Cruise stopped.");
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

            AppendApproach(RouteControllerName, RouteWaypointPrefix + taskId, new Vector3D(x, y, z));
        }

        private void LoadStationApproach(bool startCruise)
        {
            string taskId;
            bool pickup;
            long grid;
            if (!_worker.TryGetHaulerDestination(out taskId, out pickup, out grid))
            {
                _echo("A confirmed hauler lease with an assigned station is required.");
                return;
            }

            string name;
            double x;
            double y;
            double z;
            if (!TaskBusIgcHelper.TryReadOreGps(_findStationApproach(grid), out name, out x, out y, out z))
            {
                _echo("Set HaulerApproach." + grid + "=GPS:Name:X:Y:Z: for the assigned station.");
                return;
            }

            string controller = pickup ? "[AMN PICKUP]" : "[AMN DELIVERY]";
            string waypoint = RouteWaypointPrefix + taskId;
            var position = new Vector3D(x, y, z);
            if (startCruise)
                StartPreparedCruise(controller, waypoint, position, pickup, grid);
            else
                AppendApproach(controller, waypoint, position);
        }

        private void StartPreparedCruise(string controller, string waypoint, Vector3D position, bool pickup, long grid)
        {
            try
            {
                var remote = _findBlock(controller) as IMyRemoteControl;
                if (remote == null || remote.CubeGrid != _grid || !remote.IsWorking ||
                    remote.IsAutoPilotEnabled || !_canStartCruise())
                {
                    _echo("Cruise requires an undocked ship, no active pilot or autopilot, and a working " + controller + ".");
                    return;
                }

                if (remote.FlightMode != FlightMode.OneWay)
                {
                    _echo("Set the route controller to One Way before starting cruise.");
                    return;
                }

                _routeWaypoints.Clear();
                remote.GetWaypointInfo(_routeWaypoints);
                if (_routeWaypoints.Count == 0 ||
                    _routeWaypoints[_routeWaypoints.Count - 1].Name != waypoint ||
                    _routeWaypoints[_routeWaypoints.Count - 1].Coords != position)
                {
                    _echo("Load and review the current station route before starting cruise.");
                    return;
                }
                _cruiseRemote = remote;
                _cruiseLease = _worker.ConfirmedLeaseIdentity;
                _cruisePickup = pickup;
                _cruiseGrid = grid;
                remote.SetAutoPilotEnabled(true);
                _echo(remote.IsAutoPilotEnabled ? "Cruise autopilot engaged." : "Cruise autopilot did not engage.");
            }
            catch (Exception error)
            {
                _echo("Could not start cruise: " + error.Message);
            }
        }

        public void CheckCruiseLease()
        {
            if (_cruiseRemote == null)
                return;

            string taskId;
            bool pickup;
            long grid;
            if (!_worker.TryGetHaulerDestination(out taskId, out pickup, out grid) ||
                _worker.ConfirmedLeaseIdentity != _cruiseLease || pickup != _cruisePickup || grid != _cruiseGrid)
                StopCruise("Cruise stopped: the current leg lost lease confirmation.");
            else if (!_cruiseRemote.IsAutoPilotEnabled)
                _cruiseRemote = null;
        }

        private void StopCruise(string reason)
        {
            if (_cruiseRemote == null)
                return;
            try
            {
                if (_cruiseRemote.IsAutoPilotEnabled)
                    _cruiseRemote.SetAutoPilotEnabled(false);
                if (!_cruiseRemote.IsAutoPilotEnabled)
                {
                    _cruiseRemote = null;
                    _echo(reason);
                }
            }
            catch (Exception error)
            {
                _echo("Could not stop cruise: " + error.Message);
            }
        }

        private void AppendApproach(string controller, string waypointName, Vector3D position)
        {
            try
            {
                var remote = _findBlock(controller) as IMyRemoteControl;
                if (remote == null || remote.CubeGrid != _grid || !remote.IsWorking || remote.IsAutoPilotEnabled)
                {
                    _echo("Use a working " + controller + " remote with autopilot off.");
                    return;
                }

                _routeWaypoints.Clear();
                remote.GetWaypointInfo(_routeWaypoints);
                foreach (var waypoint in _routeWaypoints)
                {
                    if (!waypoint.Name.StartsWith(RouteWaypointPrefix, StringComparison.Ordinal))
                        continue;
                    _echo(waypoint.Name == waypointName && waypoint.Coords == position ?
                        "Approach already loaded." : "Remove the previous AMN approach before loading this task.");
                    return;
                }
                remote.AddWaypoint(position, waypointName);
                _echo("Approach appended. Review the route before enabling autopilot.");
            }
            catch (Exception error)
            {
                _echo("Could not load approach: " + error.Message);
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
