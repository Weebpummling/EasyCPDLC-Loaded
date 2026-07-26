using EasyCPDLC;
using Xunit;

namespace EasyCPDLC.Tests
{
    public class CpdlcHandoverParserTests
    {
        // The shapes that already worked must keep working.
        [Theory]
        [InlineData("HANDOVER @KUSA@", "KUSA")]
        [InlineData("HANDOVER KUSA", "KUSA")]
        [InlineData("HANDOVER TO KUSA", "KUSA")]
        [InlineData("HANDOVER TO @KUSA@", "KUSA")]
        [InlineData("HANDOVER @EDYY@", "EDYY")]
        [InlineData("HANDOVER @EURW@", "EURW")]
        public void PlainHandover_YieldsTheStation(string message, string expected)
        {
            Assert.True(CpdlcHandoverParser.TryParseNextUnit(message, out string unit));
            Assert.Equal(expected, unit);
        }

        // Every one of these previously logged the aircraft on to the wrong thing:
        // the last space-delimited token.
        [Theory]
        [InlineData("HANDOVER @KUSA@ AT 1230", "KUSA")]        // was "1230"
        [InlineData("HANDOVER TO KUSA CENTER", "KUSA")]        // was "CENTER"
        [InlineData("HANDOVER @KUSA@ ON 132.500", "KUSA")]     // was "132.500"
        [InlineData("HANDOVER @KUSA@.", "KUSA")]               // was "KUSA@."
        [InlineData("HANDOVER @KUSA@ ", "KUSA")]               // was "" (empty logon!)
        [InlineData("HANDOVER @KUSA@\n", "KUSA")]              // was "KUSA@"
        public void TrailingContent_NoLongerHijacksTheCode(string message, string expected)
        {
            Assert.True(CpdlcHandoverParser.TryParseNextUnit(message, out string unit));
            Assert.Equal(expected, unit);
        }

        // A controller callsign identifies the same ATSU as its prefix, and the prefix
        // is what a logon is addressed to.
        [Theory]
        [InlineData("HANDOVER @EDYY_CTR@", "EDYY")]
        [InlineData("HANDOVER KZDC_CTR", "KZDC")]
        public void ControllerCallsign_ReducesToTheAtsu(string message, string expected)
        {
            Assert.True(CpdlcHandoverParser.TryParseNextUnit(message, out string unit));
            Assert.Equal(expected, unit);
        }

        // The real CPDLC term, and lowercase, both used in the wild.
        [Theory]
        [InlineData("NEXT DATA AUTHORITY KUSA", "KUSA")]
        [InlineData("Handover @kusa@", "KUSA")]
        [InlineData("next data authority @edyy@", "EDYY")]
        public void AlternateWordingAndCase_AreAccepted(string message, string expected)
        {
            Assert.True(CpdlcHandoverParser.TryParseNextUnit(message, out string unit));
            Assert.Equal(expected, unit);
        }

        // The dangerous case: a handover-shaped message with no station in it. Better
        // to report failure than to log on to a word.
        [Theory]
        [InlineData("HANDOVER COMPLETE")]     // previously logged on to "COMPLETE"
        [InlineData("HANDOVER FAILED")]
        [InlineData("HANDOVER")]
        [InlineData("HANDOVER TO")]
        [InlineData("HANDOVER AT 1230")]
        public void HandoverWithoutAStation_Fails(string message)
        {
            Assert.True(CpdlcHandoverParser.IsHandover(message));
            Assert.False(CpdlcHandoverParser.TryParseNextUnit(message, out string unit));
            Assert.Equal(string.Empty, unit);
        }

        // Not a handover at all - must not trigger an automatic logon.
        [Theory]
        [InlineData("CONTACT KUSA CENTER ON 132.500")]
        [InlineData("LOGON ACCEPTED")]
        [InlineData("CURRENT ATC UNIT KUSA")]
        [InlineData("")]
        [InlineData(null)]
        public void NonHandoverMessages_AreIgnored(string message)
        {
            Assert.False(CpdlcHandoverParser.IsHandover(message));
            Assert.False(CpdlcHandoverParser.TryParseNextUnit(message, out _));
        }
    }
}
