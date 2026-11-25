using NUnit.Framework;
using VRageMath;
using SpaceEngineers.Libs.RadarCore;

namespace Tests.Radar.Tests
{
    [TestFixture]
    public class PositionPredictorTests
    {
        [Test]
        public void PositionPredictor_Reset()
        {
            var predictor = new PositionPredictor();
            
            // Should work without crashing on reset
            predictor.Reset();
            
            var predicted = predictor.Predict(Vector3D.Zero, Vector3D.Zero, 1.0);
            Assert.AreEqual(Vector3D.Zero, predicted);
        }

        [Test]
        public void PositionPredictor_Basic_Prediction()
        {
            var predictor = new PositionPredictor();
            
            // Update with known position and velocity
            predictor.UpdateState(Vector3D.Zero, Vector3D.UnitX * 10);
            
            // Predict 5 seconds ahead
            var predicted = predictor.Predict(Vector3D.Zero, Vector3D.UnitX * 10, 5.0);
            
            // Should be at x=50 (10 m/s * 5 s)
            Assert.AreEqual(50.0, predicted.X);
        }

        [Test]
        public void PositionPredictor_With_Initial_Position()
        {
            var predictor = new PositionPredictor();
            
            // Start at position 100, move at 20 m/s
            predictor.UpdateState(new Vector3D(100, 0, 0), Vector3D.UnitX * 20);
            
            // Predict 3 seconds ahead
            var predicted = predictor.Predict(new Vector3D(100, 0, 0), Vector3D.UnitX * 20, 3.0);
            
            // Should be at x=160 (100 + 20*3)
            Assert.AreEqual(160.0, predicted.X);
        }
    }
}
