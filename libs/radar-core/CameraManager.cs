using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;

namespace SpaceEngineers.Libs.RadarCore
{
    public sealed class CameraManager
    {
        private readonly RadarConfig _config;
        private readonly List<IMyCameraBlock> _cameras = new List<IMyCameraBlock>();
        private int _next;

        public CameraManager(RadarConfig config)
        {
            _config = config;
        }

        public void DiscoverCameras(IMyGridTerminalSystem terminal, IMyTerminalBlock owner)
        {
            _cameras.Clear();
            var group = terminal.GetBlockGroupWithName(_config.CameraGroup);
            if (group != null)
                group.GetBlocksOfType(_cameras, camera => camera.IsSameConstructAs(owner));
            if (_cameras.Count == 0 && !string.IsNullOrWhiteSpace(_config.CameraPrefix))
                terminal.GetBlocksOfType(_cameras, camera => camera.IsSameConstructAs(owner) &&
                    camera.CustomName.StartsWith(_config.CameraPrefix, StringComparison.OrdinalIgnoreCase));
            foreach (var camera in _cameras)
                camera.EnableRaycast = true;
            _next = 0;
        }

        public IMyCameraBlock GetNext()
        {
            if (_cameras.Count == 0)
                return null;
            var camera = _cameras[_next];
            _next = (_next + 1) % _cameras.Count;
            return camera;
        }

        public int Count() => _cameras.Count;
    }
}
