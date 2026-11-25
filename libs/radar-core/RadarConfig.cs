using System;
using System.Globalization;

namespace SpaceEngineers.Libs.RadarCore
{
    /// <summary>
    /// Конфигурация радара, загружаемая из Custom Data программируемого блока.
    /// </summary>
    public sealed class RadarConfig
    {
        public string Mode = "Fixed";
        public string CameraGroup = "Radar Cameras";
        public string CameraPrefix = "[RADAR CAM]";
        public double MaxRange = 30000.0;
        public int MaxRaycastsPerUpdate = 1;
        public bool ShareContacts = false;
        public string AzimuthBlock = "[RADAR AZIMUTH]";
        public string ElevationBlock = "[RADAR ELEVATION]";
        public bool InvertAzimuth = false;
        public bool InvertElevation = false;
        public double PlatformSpeedLimitDegreesPerSecond = 10.0;
        public double PlatformAzimuthMin = -180.0;
        public double PlatformAzimuthMax = 180.0;
        public double PlatformElevationMin = -90.0;
        public double PlatformElevationMax = 90.0;

        public static bool TryParse(string customData, out RadarConfig config, out string error)
        {
            config = new RadarConfig();
            error = null;

            if (string.IsNullOrEmpty(customData))
            {
                return true;
            }

            string currentSection = null;
            var lines = customData.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                var eqIndex = line.IndexOf('=');
                if (eqIndex < 0)
                {
                    continue;
                }

                var key = line.Substring(0, eqIndex).Trim();
                var value = line.Substring(eqIndex + 1).Trim();

                if (string.Equals(currentSection, "Radar", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyRadarSetting(config, key, value);
                }
                else if (string.Equals(currentSection, "Gimbal", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyGimbalSetting(config, key, value);
                }
            }

            return true;
        }
        private static void ApplyRadarSetting(RadarConfig config, string key, string value)
        {
            if (string.Equals(key, "Mode", StringComparison.OrdinalIgnoreCase))
            {
                config.Mode = value;
            }
            else if (string.Equals(key, "CameraGroup", StringComparison.OrdinalIgnoreCase))
            {
                config.CameraGroup = value;
            }
            else if (string.Equals(key, "CameraPrefix", StringComparison.OrdinalIgnoreCase))
            {
                config.CameraPrefix = value;
            }
            else if (string.Equals(key, "MaxRange", StringComparison.OrdinalIgnoreCase))
            {
                double parsed;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                {
                    config.MaxRange = parsed;
                }
            }
            else if (string.Equals(key, "MaxRaycastsPerUpdate", StringComparison.OrdinalIgnoreCase))
            {
                int parsed;
                if (int.TryParse(value, out parsed) && parsed > 0)
                {
                    config.MaxRaycastsPerUpdate = parsed;
                }
            }
            else if (string.Equals(key, "ShareContacts", StringComparison.OrdinalIgnoreCase))
            {
                config.ShareContacts = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void ApplyGimbalSetting(RadarConfig config, string key, string value)
        {
            if (string.Equals(key, "AzimuthBlock", StringComparison.OrdinalIgnoreCase))
            {
                config.AzimuthBlock = value;
            }
            else if (string.Equals(key, "ElevationBlock", StringComparison.OrdinalIgnoreCase))
            {
                config.ElevationBlock = value;
            }
            else if (string.Equals(key, "InvertAzimuth", StringComparison.OrdinalIgnoreCase))
            {
                config.InvertAzimuth = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (string.Equals(key, "InvertElevation", StringComparison.OrdinalIgnoreCase))
            {
                config.InvertElevation = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (string.Equals(key, "PlatformSpeedLimitDegreesPerSecond", StringComparison.OrdinalIgnoreCase))
            {
                double parsed;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                {
                    config.PlatformSpeedLimitDegreesPerSecond = parsed;
                }
            }
            else if (string.Equals(key, "PlatformAzimuthMin", StringComparison.OrdinalIgnoreCase))
            {
                double parsed;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    config.PlatformAzimuthMin = parsed;
                }
            }
            else if (string.Equals(key, "PlatformAzimuthMax", StringComparison.OrdinalIgnoreCase))
            {
                double parsed;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    config.PlatformAzimuthMax = parsed;
                }
            }
            else if (string.Equals(key, "PlatformElevationMin", StringComparison.OrdinalIgnoreCase))
            {
                double parsed;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    config.PlatformElevationMin = parsed;
                }
            }
            else if (string.Equals(key, "PlatformElevationMax", StringComparison.OrdinalIgnoreCase))
            {
                double parsed;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    config.PlatformElevationMax = parsed;
                }
            }
        }
    }
}