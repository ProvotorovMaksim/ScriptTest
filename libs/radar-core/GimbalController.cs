using System;
using VRageMath;
using Sandbox.ModAPI.Ingame;

namespace SpaceEngineers.Libs.RadarCore
{
    /// <summary>
    /// Управляет механической платформой радара с двумя осями (азимут и элевация).
    /// Поддерживает Fixed (неподвижный) и Gimbal (ротор+шарнир) режимы.
    /// </summary>
    public sealed class GimbalController
    {
        private readonly RadarConfig _config;
        private double _azimuth; // в градусах
        private double _elevation; // в градусах
        private bool _enabled;
        private bool _calibrated;
        private double _speedDegPerSec;
        private double _azimuthMin, _azimuthMax;
        private double _elevationMin, _elevationMax;

        public GimbalController(RadarConfig config)
        {
            _config = config;
            _azimuth = 0.0;
            _elevation = 0.0;
            _enabled = false;
            _calibrated = false;

            // Initialize limits from config
            _azimuthMin = config.PlatformAzimuthMin;
            _azimuthMax = config.PlatformAzimuthMax;
            _elevationMin = config.PlatformElevationMin;
            _elevationMax = config.PlatformElevationMax;
            _speedDegPerSec = config.PlatformSpeedLimitDegreesPerSecond;
        }

        /// <summary>
        /// Включает gimbal платформу.
        /// </summary>
        public void Enable()
        {
            _enabled = true;
        }

        /// <summary>
        /// Выключает gimbal платформу и паркует её в парковочном положении.
        /// </summary>
        public void Disable()
        {
            _enabled = false;
            Park();
        }

        /// <summary>
        /// Калибрирует платформу в парковочное положение (0 азимут, 0 элевация).
        /// </summary>
        public void Calibrate()
        {
            _calibrated = true;
            _azimuth = 0.0;
            _elevation = 0.0;
        }

        /// <summary>
        /// Возвращает платформу в парковочное положение.
        /// </summary>
        public void Park()
        {
            if (_calibrated)
            {
                _azimuth = 0.0;
                _elevation = 0.0;
            }
            else
            {
                // Если не калибРирован, возвращаем в безопасную зону
                _azimuth = MathHelper.Clamp(_azimuth, _azimuthMin, _azimuthMax);
                _elevation = MathHelper.Clamp(_elevation, _elevationMin, _elevationMax);
            }
        }

        /// <summary>
        /// Устанавливает азимутную угол.
        /// </summary>
        /// <param name="azimuthDeg">Угол в градусах</param>
        public void SetAzimuth(double azimuthDeg)
        {
            if (!_enabled) return;

            var azimuth = azimuthDeg;
            // Применение инверсии
            if (_config.InvertAzimuth) azimuth = -azimuth;

            // Ограничение диапазона
            azimuth = MathHelper.Clamp(azimuth, (float)_azimuthMin, (float)_azimuthMax);

            // Плавное движение с учетом скорости
            var delta = azimuth - _azimuth;
            // Нормализация угла delta
            delta = ((delta + 180.0) % 360.0) - 180.0;

            var step = _speedDegPerSec; // за один update (100ms)
            _azimuth = MathHelper.Clamp(_azimuth + Math.Sign(delta) * Math.Min(step, Math.Abs(delta)), (float)_azimuthMin, (float)_azimuthMax);
        }

        /// <summary>
        /// Устанавливает угол элевации.
        /// </summary>
        /// <param name="elevationDeg">Угол в градусах</param>
        public void SetElevation(double elevationDeg)
        {
            if (!_enabled) return;

            var elevation = elevationDeg;
            // Применение инверсии
            if (_config.InvertElevation) elevation = -elevation;

            // Ограничение диапазона
            elevation = MathHelper.Clamp(elevation, (float)_elevationMin, (float)_elevationMax);

            // Плавное движение
            var delta = elevation - _elevation;
            delta = ((delta + 180.0) % 360.0) - 180.0;

            var step = _speedDegPerSec;
            _elevation = MathHelper.Clamp(_elevation + Math.Sign(delta) * Math.Min(step, Math.Abs(delta)), (float)_elevationMin, (float)_elevationMax);
        }

        /// <summary>
        /// Получает текущий азимут.
        /// </summary>
        public double GetAzimuth() => _azimuth;

        /// <summary>
        /// Получает текущую элевацию.
        /// </summary>
        public double GetElevation() => _elevation;

        /// <summary>
        /// Проверяет, включен ли gimbal.
        /// </summary>
        public bool IsEnabled() => _enabled;

        /// <summary>
        /// Проверяет, калибРирована ли платформа.
        /// </summary>
        public bool IsCalibrated() => _calibrated;
    }
}