using EasyCPDLC;
using System.Linq;
using Xunit;

namespace EasyCPDLC.Tests
{
    public class CpdlcAtsuDirectoryTests
    {
        // The case that motivated the table: a US flight start, where discovery has
        // nothing because no controller has been matched to the flight yet.
        [Fact]
        public void UsDeparture_SuggestsKusa()
        {
            var codes = CpdlcAtsuDirectory.SuggestFor("KJFK", "KLAX").Select(a => a.Code).ToList();
            Assert.Contains("KUSA", codes);
        }

        [Fact]
        public void TransatlanticFlight_SuggestsBothSidesOceanic()
        {
            var codes = CpdlcAtsuDirectory.SuggestFor("EGLL", "KJFK").Select(a => a.Code).ToList();
            Assert.Contains("EGTT", codes);   // departure side
            Assert.Contains("KUSA", codes);   // destination side
        }

        // Departure units come before destination units - that is the order a pilot
        // needs them in.
        [Fact]
        public void DepartureUnitsRankAheadOfDestination()
        {
            var codes = CpdlcAtsuDirectory.SuggestFor("EDDF", "KJFK").Select(a => a.Code).ToList();
            Assert.True(codes.IndexOf("EDUU") < codes.IndexOf("KUSA"));
        }

        // A two-letter prefix is a better match than a one-letter one.
        [Fact]
        public void LongerPrefixWins()
        {
            var codes = CpdlcAtsuDirectory.SuggestFor("CYYZ", null).Select(a => a.Code).ToList();
            Assert.Contains("CZQX", codes);
            Assert.DoesNotContain("KUSA", codes);   // "K" must not match a CY airport
        }

        [Fact]
        public void UnknownOrBlankAirports_SuggestNothing()
        {
            Assert.Empty(CpdlcAtsuDirectory.SuggestFor("ZZZZ", "QQQQ"));
            Assert.Empty(CpdlcAtsuDirectory.SuggestFor(null, string.Empty));
            Assert.Empty(CpdlcAtsuDirectory.SuggestFor("K", null));   // too short to match
        }

        [Fact]
        public void SuggestionsAreNeverDuplicated()
        {
            var codes = CpdlcAtsuDirectory.SuggestFor("KJFK", "KLAX").Select(a => a.Code).ToList();
            Assert.Equal(codes.Count, codes.Distinct().Count());
        }

        // Every entry must be a valid 4-character ATSU code, or it cannot be logged
        // on to (SendCpdlcLogonRequestAsync requires >= 3, Hoppie codes are 4).
        [Fact]
        public void EveryEntryIsAWellFormedCode()
        {
            foreach (var unit in CpdlcAtsuDirectory.All)
            {
                Assert.Matches("^[A-Z]{4}$", unit.Code);
                Assert.False(string.IsNullOrWhiteSpace(unit.Name));
                Assert.NotEmpty(unit.Prefixes);
            }
            Assert.Equal(CpdlcAtsuDirectory.All.Length,
                CpdlcAtsuDirectory.All.Select(a => a.Code).Distinct().Count());
        }

        [Fact]
        public void Find_LocatesByCodeCaseInsensitively()
        {
            Assert.Equal("FAA DOMESTIC", CpdlcAtsuDirectory.Find("kusa")?.Name);
            Assert.Null(CpdlcAtsuDirectory.Find("ZZZZ"));
        }
    }
}
