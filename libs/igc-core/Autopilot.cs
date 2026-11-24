using System;
using Sandbox.ModAPI.Ingame;
using VRageMath;

namespace SpaceEngineers.Libs.IgcCore
{
    /// <summary>
    /// Wraps physical information from an <see cref="IMyRemoteControl"/> block.
    /// </summary>
    public class RemoteControlPhysicalInfo
    {
        private readonly IMyRemoteControl _remoteControl;

        /// <summary>The remote control's world matrix.</summary>
        public MatrixD WorldMatrix { get { return _remoteControl.WorldMatrix; } }
        /// <summary>The remote control's position in world space.</summary>
        public Vector3D Position { get { return _remoteControl.GetPosition(); } }
        /// <summary>The natural gravity vector at the remote control.</summary>
        public Vector3D GravityVector { get { return _remoteControl.GetNaturalGravity(); } }
        /// <summary>The magnitude of the gravity vector.</summary>
        public double GravityModule { get { return GravityVector.Length(); } }

        /// <summary>
        /// Creates a physical info wrapper for the given remote control.
        /// </summary>
        /// <param name="remoteControl">The remote control block (must not be null).</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="remoteControl"/> is null.</exception>
        public RemoteControlPhysicalInfo(IMyRemoteControl remoteControl)
        {
            if (remoteControl == null) throw new ArgumentNullException(nameof(remoteControl));
            _remoteControl = remoteControl;
        }
    }

    /// <summary>
    /// Simple autopilot helper for controlling a remote control block's flight.
    /// </summary>
    public class Autopilot
    {
        private readonly IMyRemoteControl _remoteControl;

        /// <summary>Arrival accuracy threshold in meters.</summary>
        public int Accuracy { get; set; }

        /// <summary>Physical information about the remote control's current position.</summary>
        public RemoteControlPhysicalInfo PointPhysicalInfo { get; private set; }

        /// <summary>
        /// Creates an autopilot for the given terminal block (must be an <see cref="IMyRemoteControl"/>).
        /// </summary>
        /// <param name="remoteControl">Terminal block to control (must be an IMyRemoteControl).</param>
        /// <exception cref="ArgumentNullException">Thrown when the block is not an IMyRemoteControl or is null.</exception>
        public Autopilot(IMyTerminalBlock remoteControl)
        {
            _remoteControl = remoteControl as IMyRemoteControl;
            if (_remoteControl == null)
                throw new ArgumentNullException(nameof(remoteControl), "Autopilot requires an IMyRemoteControl block.");
            _remoteControl.FlightMode = FlightMode.OneWay;
            PointPhysicalInfo = new RemoteControlPhysicalInfo(_remoteControl);
        }

        /// <summary>Placeholder update method called each tick.</summary>
        public void OnUpdate()
        {
        }

        /// <summary>
        /// Checks if the remote control has arrived at its current waypoint.
        /// </summary>
        /// <returns>True if within accuracy threshold; autopilot is disabled on arrival.</returns>
        public bool IsArrived()
        {
            if (Vector3D.Distance(_remoteControl.CurrentWaypoint.Coords, PointPhysicalInfo.Position) < Accuracy)
            {
                _remoteControl.SetAutoPilotEnabled(false);
                return true;
            }
            return false;
        }

        /// <summary>Enables autopilot.</summary>
        public void Go()
        {
            _remoteControl.SetAutoPilotEnabled(true);
        }

        /// <summary>Clears waypoints, adds a destination, and enables autopilot.</summary>
        /// <param name="destination">Destination coordinates in world space.</param>
        public void Go(Vector3D destination)
        {
            AddDestination(destination);
            Go();
        }

        /// <summary>Clears waypoints and adds a single destination.</summary>
        /// <param name="destination">Destination coordinates in world space.</param>
        public void AddDestination(Vector3D destination)
        {
            _remoteControl.ClearWaypoints();
            _remoteControl.AddWaypoint(destination, "destination");
        }

        /// <summary>Re-enables autopilot after being stopped.</summary>
        public void Resume()
        {
            _remoteControl.SetAutoPilotEnabled(true);
        }

        /// <summary>Disables autopilot.</summary>
        public void Stop() => _remoteControl.SetAutoPilotEnabled(false);

        /// <summary>Sets the speed limit for autopilot.</summary>
        /// <param name="speed">Speed limit in m/s.</param>
        public void SetSpeedLimit(int speed) => _remoteControl.SpeedLimit = speed;
    }
}