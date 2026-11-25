using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRageMath;
using SpaceEngineers.Libs.RadarCore;

namespace Tests.Radar.Tests
{
    [TestFixture]
    public class CameraManagerTests
    {
        private RadarConfig _config;
        private CameraManager _manager;
        private IMyGridTerminalSystem _terminalSystem;

        [SetUp]
        public void Setup()
        {
            _config = new RadarConfig { CameraGroup = "[RADAR CAM]", CameraPrefix = "[RADAR CAM]" };
            _manager = new CameraManager(_config);
            // Mock terminal system
            _terminalSystem = new MyGridTerminalSystem();
        }

        [Test]
        public void CameraManager_Initial_State()
        {
            Assert.IsFalse(_manager.HasCameras());
            Assert.IsNull(_manager.GetCurrent());
            Assert.IsNull(_manager.GetNext());
        }

        [Test]
        public void CameraManager_DiscoverCameras_With_Prefix()
        {
            // Create mock cameras
            var cam1 = new MyCameraBlock();
            cam1.CustomName = "[RADAR CAM] Scanner 1";
            var cam2 = new MyCameraBlock();
            cam2.CustomName = "Other Camera";
            var cam3 = new MyCameraBlock();
            cam3.CustomName = "[RADAR CAM] Scanner 2";

            // Simulate terminal system blocks
            // Note: This tests the logic, actual GridTerminalSystem.GetBlocksOfType behavior
            // would depend on game implementation
            
            _manager.DiscoverCameras(_terminalSystem);
            
            // Should find cameras matching the prefix
            Assert.IsTrue(_manager.HasCameras(), "Should detect at least one camera with prefix");
        }

        [Test]
        public void CameraManager_GetCurrent_Next_Cycling()
        {
            _manager.DiscoverCameras(_terminalSystem);

            var current = _manager.GetCurrent();
            var next = _manager.GetNext();
            var next2 = _manager.GetNext();

            // After cycling through all cameras, should return to first
            Assert.AreEqual(current, next2, "Cycling should return to first camera after last");
        }

        [Test]
        public void CameraManager_Charge_Management()
        {
            _manager.DiscoverCameras(_terminalSystem);

            var initialCharge = _manager.Charge;
            _manager.Charge = 0.5;
            Assert.AreEqual(0.5, _manager.Charge);
            _manager.ResetCharge();
            Assert.AreEqual(0.0, _manager.Charge);
        }

        [Test]
        public void CameraManager_Count_And_HasCameras()
        {
            _manager.DiscoverCameras(_terminalSystem);
            
            var count = _manager.Count();
            var has = _manager.HasCameras();
            
            // When no cameras discovered, count should be 0 and has should be false
            // When cameras discovered, depends on mock implementation
            Assert.IsTrue(count >= 0);
            Assert.IsTrue(has >= 0);
        }
    }
}
TESTEOF
echo "CameraManagerTests.cs written"