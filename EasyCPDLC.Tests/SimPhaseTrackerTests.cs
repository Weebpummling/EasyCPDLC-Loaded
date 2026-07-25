using EasyCPDLC.VNS430;
using Xunit;

namespace EasyCPDLC.Tests
{
    public class SimPhaseTrackerTests
    {
        [Fact]
        public void FullFlight_CruiseIsRememberedUntilLanding()
        {
            SimPhaseTracker phase = new();

            phase.Update(500, onGround: true);      // at the gate
            Assert.False(phase.ReachedCruise);

            phase.Update(8000, onGround: false);    // climbout
            Assert.False(phase.ReachedCruise);

            phase.Update(35000, onGround: false);   // cruise
            Assert.True(phase.ReachedCruise);

            phase.Update(9000, onGround: false);    // descent - still the same flight
            Assert.True(phase.ReachedCruise);

            phase.Update(600, onGround: true);      // landed: next leg starts fresh
            Assert.False(phase.ReachedCruise);
        }

        [Fact]
        public void TaxiOut_DoesNotResetAnything()
        {
            SimPhaseTracker phase = new();
            phase.Update(500, onGround: true);
            phase.Update(400, onGround: true);
            Assert.False(phase.AirborneSeen);
            Assert.False(phase.ReachedCruise);
        }

        [Fact]
        public void RestartMidCruise_RecoversOnFirstPacket()
        {
            SimPhaseTracker phase = new();
            phase.Update(37000, onGround: false);
            Assert.True(phase.ReachedCruise);
        }

        [Fact]
        public void LowLevelFlight_NeverFlips()
        {
            SimPhaseTracker phase = new();
            phase.Update(500, onGround: true);
            phase.Update(12000, onGround: false);
            phase.Update(15000, onGround: false);
            Assert.False(phase.ReachedCruise);
            phase.Update(300, onGround: true);
            Assert.False(phase.ReachedCruise);
        }

        [Fact]
        public void ThresholdMatchesTheVatsimPhaseEngine()
        {
            Assert.Equal(22000, SimPhaseTracker.CruiseAltitudeFt);
        }
    }
}
