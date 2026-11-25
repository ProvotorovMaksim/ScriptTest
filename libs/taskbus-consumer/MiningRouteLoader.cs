using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using VRageMath;
using VRage.Game.ModAPI.Ingame;

namespace SpaceEngineers.Libs.TaskBusConsumer
{
    using SpaceEngineers.Libs.IgcCore;

    public sealed class MiningRouteLoader
    {
        public const string RouteControllerName = "[AMN ROUTE]";
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
        private readonly Func<long, IMyTerminalBlock> _findBlockById;
        private IMyRemoteControl _cruiseRemote;
        private string _cruiseLease;
        private bool _cruisePickup;
        private long _cruiseGrid;
        private Vector3D _cruiseEndpoint;
        private Vector3D _cruiseProgressPosition;
        private int _cruiseProgressUpdates;
        private string _cruiseStopReason;
        private const int CruiseProgressInterval = 60;
        private const double MinimumCruiseMovementSquared = 0.25;
        private int _arrivalChecks;
        private readonly Action<long> _onCruiseArrival;

        public MiningRouteLoader(TaskBusConsumer worker, IMyCubeGrid grid,
            Func<string, IMyTerminalBlock> findBlock, Action<string> echo, Func<long, string> findStationApproach = null,
            Func<bool> canStartCruise = null, Func<long, IMyTerminalBlock> findBlockById = null,
            Action<long> onCruiseArrival = null)
        {
            _worker = worker;
            _grid = grid;
            _findBlock = findBlock;
            _echo = echo;
            _findStationApproach = findStationApproach;
            _canStartCruise = canStartCruise;
            _findBlockById = findBlockById;
            _onCruiseArrival = onCruiseArrival;
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

        public void LoadMiningApproach(Action<IMyRemoteControl> start = null, bool prepare = false)
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

            var waypoint = RouteWaypointPrefix + taskId;
            var position = new Vector3D(x, y, z);
            if (start == null)
            {
                AppendApproach(RouteControllerName, waypoint, position);
                return;
            }

            try
            {
                if (prepare && !PrepareAssignedApproach(RouteControllerName, waypoint, position, allowNewPosition: true))
                    return;

                var remote = FindPreparedCruise(RouteControllerName, waypoint, position);
                if (remote != null)
                    start(remote);
            }
            catch (Exception error)
            {
                _echo("Could not start mining approach: " + error.Message);
            }
        }

        private bool PrepareAssignedApproach(string controller, string name, Vector3D position, bool allowNewPosition)
        {
            var remote = _findBlock(controller) as IMyRemoteControl;
            if (remote == null || remote.CubeGrid != _grid || !remote.IsWorking ||
                remote.IsAutoPilotEnabled || remote.FlightMode != FlightMode.OneWay)
            {
                _echo("Use a working local " + controller + " in One Way mode with autopilot off.");
                return false;
            }

            _routeWaypoints.Clear();
            remote.GetWaypointInfo(_routeWaypoints);
            int fixedCount = _routeWaypoints.Count;
            if (fixedCount > 0 && _routeWaypoints[fixedCount - 1].Name.StartsWith(RouteWaypointPrefix, StringComparison.Ordinal))
                fixedCount--;
            if (fixedCount == 0)
            {
                if (!allowNewPosition && _routeWaypoints.Count == 1 &&
                    _routeWaypoints[0].Name == name && _routeWaypoints[0].Coords == position)
                    return true;

                _echo("Automatic cruise needs a reviewed corridor before the final approach.");
                return false;
            }
            for (int i = 0; i < fixedCount; i++)
            {
                if (_routeWaypoints[i].Name.StartsWith(RouteWaypointPrefix, StringComparison.Ordinal))
                {
                    _echo("The generated AMN approach must be the final outbound waypoint.");
                    return false;
                }
            }

            if (fixedCount < _routeWaypoints.Count)
            {
                var last = _routeWaypoints[fixedCount];
                if (last.Name == name && last.Coords == position)
                    return true;
                if (!allowNewPosition && last.Coords != position)
                {
                    _echo("The prepared corridor ends at a different station approach. Review this route before using it.");
                    return false;
                }

                remote.ClearWaypoints();
                for (int i = 0; i < fixedCount; i++)
                    remote.AddWaypoint(_routeWaypoints[i].Coords, _routeWaypoints[i].Name);
            }
            remote.AddWaypoint(position, name);
            return true;
        }

        public void LoadStationApproach(bool startCruise, bool prepare = false)
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
                StartPreparedCruise(controller, waypoint, position, pickup, grid, prepare);
            else
                AppendApproach(controller, waypoint, position);
        }

        private void StartPreparedCruise(string controller, string waypoint, Vector3D position, bool pickup, long grid,
            bool prepare)
        {
            try
            {
                if (!_canStartCruise())
                {
                    _echo("Cruise requires an undocked ship and no active pilot or autopilot.");
                    return;
                }
                if (prepare && !PrepareAssignedApproach(controller, waypoint, position, allowNewPosition: false))
                    return;

                var remote = FindPreparedCruise(controller, waypoint, position);
                if (remote == null)
                    return;

                if (_worker.SupervisedCruiseRemote != 0)
                {
                    _echo("Stop the previously supervised cruise before starting another.");
                    return;
                }

                _worker.SetSupervisedCruiseRemote(remote.EntityId);
                _cruiseRemote = remote;
                _cruiseLease = _worker.ConfirmedLeaseIdentity;
                _cruisePickup = pickup;
                _cruiseGrid = grid;
                _cruiseEndpoint = position;
                _cruiseProgressPosition = remote.GetPosition();
                _cruiseProgressUpdates = 0;
                _cruiseStopReason = null;
                _arrivalChecks = 0;
                remote.SetAutoPilotEnabled(true);
                if (remote.IsAutoPilotEnabled)
                    _echo("Cruise autopilot engaged.");
                else
                {
                    ForgetCruise();
                    _echo("Cruise autopilot did not engage.");
                }
            }
            catch (Exception error)
            {
                _echo("Could not start cruise: " + error.Message);
            }
        }

        private IMyRemoteControl FindPreparedCruise(string controller, string waypoint, Vector3D position)
        {
            var remote = _findBlock(controller) as IMyRemoteControl;
            if (remote == null || remote.CubeGrid != _grid || !remote.IsWorking ||
                remote.IsAutoPilotEnabled || remote.FlightMode != FlightMode.OneWay)
            {
                _echo("Use a working local " + controller + " in One Way mode with autopilot off.");
                return null;
            }

            _routeWaypoints.Clear();
            remote.GetWaypointInfo(_routeWaypoints);
            if (_routeWaypoints.Count == 0 ||
                _routeWaypoints[_routeWaypoints.Count - 1].Name != waypoint ||
                _routeWaypoints[_routeWaypoints.Count - 1].Coords != position)
            {
                _echo("Load and review the current assignment's approach before starting cruise.");
                return null;
            }
            return remote;
        }

        public void CheckCruiseLease(bool periodic = false)
        {
            if (_cruiseRemote == null && _worker.SupervisedCruiseRemote != 0 && _findBlockById != null)
            {
                var saved = _findBlockById(_worker.SupervisedCruiseRemote) as IMyRemoteControl;
                if (saved == null || saved.CubeGrid != _grid)
                {
                    _worker.SetSupervisedCruiseRemote(0);
                    return;
                }
                _cruiseRemote = saved;
                _cruiseLease = null;
            }
            if (_cruiseRemote == null)
                return;

            if (_cruiseRemote.CubeGrid != _grid)
            {
                ForgetCruise();
                return;
            }

            if (_cruiseStopReason != null)
            {
                StopCruise(_cruiseStopReason);
                return;
            }

            string taskId;
            bool pickup;
            long grid;
            if (!_worker.TryGetHaulerDestination(out taskId, out pickup, out grid) ||
                _worker.ConfirmedLeaseIdentity != _cruiseLease || pickup != _cruisePickup || grid != _cruiseGrid)
                StopCruise("Cruise stopped: the current leg lost lease confirmation.");
            else if (!_cruiseRemote.IsAutoPilotEnabled)
                CheckCruiseArrival();
            else if (periodic && ++_cruiseProgressUpdates >= CruiseProgressInterval)
            {
                _cruiseProgressUpdates = 0;
                var position = _cruiseRemote.GetPosition();
                if (Vector3D.DistanceSquared(position, _cruiseProgressPosition) < MinimumCruiseMovementSquared)
                    StopCruise("Cruise stopped: the ship did not move along the supervised route.");
                else
                    _cruiseProgressPosition = position;
            }
        }

        private void CheckCruiseArrival()
        {
            if (_onCruiseArrival == null || !_cruiseRemote.IsWorking ||
                Vector3D.DistanceSquared(_cruiseRemote.GetPosition(), _cruiseEndpoint) > 4)
            {
                ForgetCruise();
                return;
            }

            if (_cruiseRemote.GetShipSpeed() > 0.35 || !_canStartCruise())
            {
                if (++_arrivalChecks >= 18)
                {
                    ForgetCruise();
                    _echo("Automatic docking handoff timed out. Check the ship and request route dock.");
                }
                return;
            }

            long grid = _cruiseGrid;
            ForgetCruise();
            _onCruiseArrival(grid);
        }

        private void ForgetCruise()
        {
            _cruiseRemote = null;
            _cruiseStopReason = null;
            _worker.SetSupervisedCruiseRemote(0);
        }

        private void StopCruise(string reason)
        {
            if (_cruiseRemote == null)
                return;
            _cruiseStopReason = reason;
            try
            {
                if (_cruiseRemote.IsAutoPilotEnabled)
                    _cruiseRemote.SetAutoPilotEnabled(false);
                if (!_cruiseRemote.IsAutoPilotEnabled)
                {
                    ForgetCruise();
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

        public void LoadReturnRoute(Action<IMyRemoteControl> start = null, bool prepare = false)
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
                bool loadWaypoints = _returnWaypoints.Count == 0 && (start == null || prepare);
                if (start != null)
                {
                    if (inbound.FlightMode != FlightMode.OneWay ||
                        Vector3D.DistanceSquared(outbound.GetPosition(), last.Coords) > 100 ||
                        (!loadWaypoints && _returnWaypoints.Count != _routeWaypoints.Count - 1))
                    {
                        _echo("Return requires a prepared One Way controller and the ship within 10 m of the ore approach.");
                        return;
                    }
                    for (int i = 0; i < _returnWaypoints.Count; i++)
                    {
                        var expected = _routeWaypoints[_routeWaypoints.Count - 2 - i];
                        var actual = _returnWaypoints[i];
                        if (actual.Name != expected.Name || actual.Coords != expected.Coords)
                        {
                            _echo("The return route differs from the prepared outbound route.");
                            return;
                        }
                    }
                }
                else if (!loadWaypoints)
                {
                    _echo("Clear " + ReturnControllerName + " waypoints before loading a return route.");
                    return;
                }

                if (loadWaypoints)
                {
                    writing = true;
                    for (int i = _routeWaypoints.Count - 2; i >= 0; i--)
                    {
                        var waypoint = _routeWaypoints[i];
                        inbound.AddWaypoint(waypoint.Coords, waypoint.Name);
                    }
                    writing = false;
                }

                if (start != null)
                    start(inbound);
                else
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
