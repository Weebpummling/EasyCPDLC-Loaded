using EasyCPDLC;
using Xunit;

namespace EasyCPDLC.Tests
{
    public class DatalinkRoutingTests
    {
        // On VATSIM nothing ever routes to SayIntentions, whatever the packet looks like.
        [Theory]
        [InlineData("CPDLC", "EDDF")]
        [InlineData("TELEX", "PKGM")]
        [InlineData("poll", "NONE")]
        [InlineData("ping", "SERVER")]
        public void VatsimMode_AlwaysRoutesToHoppie(string type, string recipient)
        {
            Assert.False(DatalinkRouting.RoutesToSayIntentions(AcarsRoute.Auto, type, recipient, sayIntentionsActive: false));
        }

        // On SI, ATC-session traffic follows the ATC network: every CPDLC packet, and
        // telex addressed to the SI ATSU (the PDC request).
        [Theory]
        [InlineData("CPDLC", "PKGM")]
        [InlineData("CPDLC", "EDDF")]   // active session station after logon
        [InlineData("cpdlc", "eddf")]   // case-insensitive
        [InlineData("TELEX", "PKGM")]
        [InlineData("TELEX", "pkgm")]
        public void SiMode_AtcTrafficRoutesToSayIntentions(string type, string recipient)
        {
            Assert.True(DatalinkRouting.RoutesToSayIntentions(AcarsRoute.Auto, type, recipient, sayIntentionsActive: true));
        }

        // On SI, everything that is not ATC-session traffic stays on Hoppie: VA telex,
        // station pings and the Hoppie poll. This is what keeps VA ACARS alive.
        [Theory]
        [InlineData("TELEX", "DLH123")]
        [InlineData("TELEX", "")]
        [InlineData("poll", "NONE")]
        [InlineData("ping", "SERVER")]
        [InlineData("INFOREQ", "EDDF")]
        public void SiMode_NonAtcTrafficStaysOnHoppie(string type, string recipient)
        {
            Assert.False(DatalinkRouting.RoutesToSayIntentions(AcarsRoute.Auto, type, recipient, sayIntentionsActive: true));
        }

        // Explicit routes override the content rule - the SI poll must reach SI even
        // though a poll would otherwise stay on Hoppie, and vice versa.
        [Fact]
        public void ExplicitRoutes_OverrideContentRule()
        {
            Assert.True(DatalinkRouting.RoutesToSayIntentions(AcarsRoute.SayIntentions, "poll", "NONE", sayIntentionsActive: true));
            Assert.False(DatalinkRouting.RoutesToSayIntentions(AcarsRoute.Hoppie, "CPDLC", "PKGM", sayIntentionsActive: true));
            // Forcing SI is honoured even if the mode flag lags a settings change.
            Assert.True(DatalinkRouting.RoutesToSayIntentions(AcarsRoute.SayIntentions, "poll", "NONE", sayIntentionsActive: false));
        }

        [Fact]
        public void NullInputs_DoNotThrow()
        {
            Assert.False(DatalinkRouting.RoutesToSayIntentions(AcarsRoute.Auto, null, null, sayIntentionsActive: true));
        }

        // The wire constants are a contract with the SayIntentions service; a typo here
        // would send credentials to the wrong host or clearances to the wrong station.
        [Fact]
        public void SayIntentionsWireConstants_AreTheDocumentedOnes()
        {
            Assert.Equal("https://acars.sayintentions.ai/acars/system/connect.html", DatalinkRouting.SayIntentionsConnectUrl);
            Assert.Equal("PKGM", DatalinkRouting.SayIntentionsAtsu);
        }
    }
}
