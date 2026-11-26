using NUnit.Framework;
using SpaceEngineers.Libs.RadarCore;

namespace Tests.Radar.Tests
{
    [TestFixture]
    public class RadarConfigTests
    {
        [Test]
        public void RadarConfig_Default_Values()
        {
            var config = new RadarConfig();
            Assert.AreEqual("Fixed", config.Mode);
            Assert.AreEqual("Radar Cameras", config.CameraGroup);
            Assert.AreEqual("[RADAR CAM]", config.CameraPrefix);
            Assert.AreEqual(30000.0, config.MaxRange);
            Assert.AreEqual(1, config.MaxRaycastsPerUpdate);
            Assert.IsFalse(config.ShareContacts);
            Assert.AreEqual("[RADAR AZIMUTH]", config.AzimuthBlock);
            Assert.AreEqual("[RADAR ELEVATION]", config.ElevationBlock);
            Assert.IsFalse(config.InvertAzimuth);
            Assert.IsFalse(config.InvertElevation);
            Assert.AreEqual(10.0, config.PlatformSpeedLimitDegreesPerSecond);
            Assert.AreEqual(-180.0, config.PlatformAzimuthMin);
            Assert.AreEqual(180.0, config.PlatformAzimuthMax);
            Assert.AreEqual(-90.0, config.PlatformElevationMin);
            Assert.AreEqual(90.0, config.PlatformElevationMax);
        }

        [Test]
        public void RadarConfig_TryParse_Basic()
        {
            var config = new RadarConfig();
            var result = RadarConfig.TryParse("[Radar]\nMode=Fixed\nMaxRange=50000\nCameraGroup=MyCameras\nCameraPrefix=[RADAR CAM]", out config, out var err);
            Assert.IsTrue(result);
            Assert.IsNull(err);
            Assert.AreEqual("Fixed", config.Mode);
            Assert.AreEqual(50000.0, config.MaxRange);
            Assert.AreEqual("MyCameras", config.CameraGroup);
            Assert.AreEqual("[RADAR CAM]", config.CameraPrefix);
        }

        [Test]
        public void RadarConfig_TryParse_Empty()
        {
            var config = new RadarConfig();
            var result = RadarConfig.TryParse("", out config, out var err);
            Assert.IsTrue(result);
            Assert.IsNull(err);
            // Should keep defaults
            Assert.AreEqual("Fixed", config.Mode);
        }

        [Test]
        public void RadarConfig_TryParse_Gimbal_Section()
        {
            var config = new RadarConfig();
            var result = RadarConfig.TryParse("[Radar]\nMode=Gimbal\n[Gimbal]\nAzimuthBlock=[RADAR AZIMUTH]\nElevationBlock=[RADAR ELEVATION]\nInvertAzimuth=true", out config, out var err);
            Assert.IsTrue(result);
            Assert.IsNull(err);
            Assert.AreEqual("Gimbal", config.Mode);
            Assert.AreEqual("[RADAR AZIMUTH]", config.AzimuthBlock);
            Assert.AreEqual("[RADAR ELEVATION]", config.ElevationBlock);
            Assert.IsTrue(config.InvertAzimuth);
        }

        [Test]
        public void SearchElevationIsIndependentOfParking()
        {
            Assert.IsTrue(RadarConfig.TryParse("[Gimbal]\nSearchElevationDegrees=12.5\nParkElevationDegrees=-8", out var config, out _));
            Assert.That(config.SearchElevationDegrees, Is.EqualTo(12.5));
            Assert.That(config.ParkElevationDegrees, Is.EqualTo(-8));
            Assert.IsTrue(RadarConfig.TryParse("[Gimbal]\nSearchElevationDegrees=NaN", out config, out _));
            Assert.That(config.SearchElevationDegrees, Is.Zero);
        }

        [Test]
        public void ParkingAnglesAcceptFiniteValuesAndKeepDefaultsForInvalidInput()
        {
            Assert.IsTrue(RadarConfig.TryParse("[Gimbal]\nParkAzimuthDegrees=135.5\nParkElevationDegrees=-12.25",
                out var config, out _));
            Assert.That(config.ParkAzimuthDegrees, Is.EqualTo(135.5));
            Assert.That(config.ParkElevationDegrees, Is.EqualTo(-12.25));

            Assert.IsTrue(RadarConfig.TryParse("[Gimbal]\nParkAzimuthDegrees=NaN\nParkElevationDegrees=Infinity",
                out config, out _));
            Assert.That(config.ParkAzimuthDegrees, Is.Zero);
            Assert.That(config.ParkElevationDegrees, Is.Zero);
        }
    }
}
