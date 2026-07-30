using EasyCPDLC;
using Xunit;

namespace EasyCPDLC.Tests
{
    /// <summary>
    /// A generated loadsheet is written into the inbox with type "LOADSHEET", while one
    /// a VA sends over Hoppie arrives as a "TELEX". Both are the same thing to the pilot
    /// and both must print.
    ///
    /// "LOADSHEET" used to fall past every branch of ClassifyMessage to Other, which made
    /// IsPrintableMessage false - so PRINT fell back to the last job and reported "NO
    /// PRINTABLE ACARS MESSAGE IS AVAILABLE" on a sheet that was plainly on screen. It
    /// also broke IsELoadControlLoadsheet, which tests printability first.
    /// </summary>
    public sealed class LoadsheetPrintingTests
    {
        private const string Body =
            "LOADSHEET EDDF/EDDP\nZFW 61234\nTOW 74000\nLDW 66500\nPAX 148";

        private static CPDLCMessage Generated() =>
            new("LOADSHEET", "ELOADCONTROL", Body, false) { aircraftCallsign = "DLH6YM" };

        private static CPDLCMessage FromVirtualAirline() =>
            new("TELEX", "DISPATCH", Body, false) { aircraftCallsign = "DLH6YM" };

        [Fact]
        public void GeneratedLoadsheetIsPrintable()
        {
            Assert.True(DatalinkPrinter.IsPrintableMessage(Generated()));
            Assert.Equal(DatalinkMessageCategory.TelexAoc, DatalinkPrinter.ClassifyMessage(Generated()));
        }

        [Fact]
        public void VirtualAirlineLoadsheetIsPrintableToo()
        {
            Assert.True(DatalinkPrinter.IsPrintableMessage(FromVirtualAirline()));
            Assert.Equal(DatalinkMessageCategory.TelexAoc, DatalinkPrinter.ClassifyMessage(FromVirtualAirline()));
        }

        // Both routes must also be recognised as loadsheets, or they lose the LOADSHEET
        // tag in the inbox and the units stop offering the right handling.
        [Fact]
        public void BothRoutesAreRecognisedAsLoadsheets()
        {
            Assert.True(DatalinkPrinter.IsELoadControlLoadsheet(Generated()));
            Assert.True(DatalinkPrinter.IsELoadControlLoadsheet(FromVirtualAirline()));
        }

        // The failure the user actually saw: a print job built from the message must
        // carry the body, because an empty Message is what triggers the error text.
        [Fact]
        public void PrintJobFromAGeneratedLoadsheetCarriesItsBody()
        {
            DatalinkPrintJob job = DatalinkPrintJob.FromMessage(Generated(), "DLH6YM");

            Assert.False(string.IsNullOrWhiteSpace(job.Message));
            Assert.Contains("ZFW 61234", job.Message);
            Assert.Equal("DLH6YM", job.AircraftCallsign);
        }

        [Fact]
        public void PrintingAJobWithNoBodyIsStillRefused()
        {
            DatalinkPrintResult result = DatalinkPrinter.Print(null, null);

            Assert.False(result.Success);
            Assert.Contains("NO PRINTABLE ACARS MESSAGE", result.Message);
        }

        // Outbound copies are never printed, whatever their type - printing your own
        // request back is noise, and a generated sheet is inbound by construction.
        [Fact]
        public void OutboundMessagesAreNotPrintable()
        {
            CPDLCMessage sent = new("LOADSHEET", "ELOADCONTROL", Body, true);

            Assert.False(DatalinkPrinter.IsPrintableMessage(sent));
        }
    }
}
