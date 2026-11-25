using System;
using System.Collections.Generic;
using VRageMath;
using Sandbox.ModAPI.Ingame;

namespace SpaceEngineers.Libs.RadarCore
{
    public sealed class CameraManager
    {
        private readonly RadarConfig _config;
        private readonly List<IMyCameraBlock> _radarCameras = new();
        private int _currentIndex = -1;
        private double _charge = 0.0;

        public void DiscoverCameras(IMyGridTerminalSystem terminalSystem)
        {
            _radarCameras.Clear();
            if (terminalSystem == null) return;

            terminalSystem.GetBlocksOfType(_radarCameras);
            _radarCameras.RemoveAll(c =>
                !customStartsWith(c.CustomData, _config.CameraPrefix) &&
                !customContains(c.CustomData, _config.CameraGroup));

            if (_radarCameras.Count > 0 && _currentIndex >= _radarCameras.Count)
                _currentIndex = 0;
        }

        private static bool customStartsWith(string text, string prefix) =>
            !string.IsNullOrEmpty(text) && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        private static bool customContains(string text, string value) =>
            !string.IsNullOrEmpty(text) && text.Contains(value, StringComparison.OrdinalIgnoreCase);

        public IMyCameraBlock? GetCurrent() =>
            _radarCameras.Count > 0 && _currentIndex >= 0 && _currentIndex < _radarCameras.Count
                ? _radarCameras[_currentIndex]
                : null;

        public IMyCameraBlock? GetNext()
        {
            if (_radarCameras.Count == 0) return null;
            _currentIndex = (_currentIndex + 1) % _radarCameras.Count;
            return _radarCameras[_currentIndex];
        }

        public int Count() => _radarCameras.Count;
        public bool HasCameras() => _radarCameras.Count > 0;
        public double Charge { get => _charge; set => _charge = Math.Clamp(value, 0.0, 1.0); }
        public void ResetCharge() => _charge = 0.0;
    }
}