using EasyCPDLC;
using System;
using System.Collections.Generic;
using Xunit;

namespace EasyCPDLC.Tests
{
    public sealed class FmcPositionReportingTests
    {
        private static FmcRouteFix Fix(string ident, double lat, double lon) =>
            new() { Ident = ident, Latitude = lat, Longitude = lon };

        // A short east-bound line of fixes about 60 NM apart at 50N.
        private static List<FmcRouteFix> Route() => new()
        {
            Fix("ALPHA", 50.0, 0.0),
            Fix("BRAVO", 50.0, 1.5),
            Fix("CHARL", 50.0, 3.0),
            Fix("DELTA", 50.0, 4.5)
        };

        // ---- geometry ------------------------------------------------------

        [Fact]
        public void Distance_IsSymmetricAndZeroAtThePoint()
        {
            Assert.Equal(0, FmcGeo.DistanceNm(50, 0, 50, 0), 3);
            Assert.Equal(
                FmcGeo.DistanceNm(50, 0, 51, 1),
                FmcGeo.DistanceNm(51, 1, 50, 0), 6);
        }

        [Fact]
        public void Distance_OneDegreeOfLatitudeIsSixtyMiles()
        {
            Assert.Equal(60.0, FmcGeo.DistanceNm(50, 0, 51, 0), 0);
        }

        // ---- sequencing ----------------------------------------------------

        // The rule that matters: a fix is only "passed" once the range has reopened.
        // Reporting on approach would put the report out before the aircraft got there.
        [Fact]
        public void Sequencer_DoesNotReportWhileStillClosingOnTheFix()
        {
            FmcWaypointSequencer sequencer = new(Route());

            Assert.Null(sequencer.Update(50.0, -0.20));   // ~7.7 NM out, inside capture
            Assert.Null(sequencer.Update(50.0, -0.10));   // ~3.9 NM, still closing
            Assert.Null(sequencer.Update(50.0, -0.02));   // ~0.8 NM, still closing
            Assert.Equal("ALPHA", sequencer.Active.Ident);
        }

        [Fact]
        public void Sequencer_ReportsOnceTheRangeOpensAgain()
        {
            FmcWaypointSequencer sequencer = new(Route());

            sequencer.Update(50.0, -0.10);
            sequencer.Update(50.0, -0.01);
            FmcRouteFix passed = sequencer.Update(50.0, 0.05);

            Assert.NotNull(passed);
            Assert.Equal("ALPHA", passed.Ident);
            Assert.Equal("BRAVO", sequencer.Active.Ident);
            Assert.Equal("CHARL", sequencer.Next.Ident);
        }

        [Fact]
        public void Sequencer_ReportsEachFixExactlyOnceAlongTheRoute()
        {
            FmcWaypointSequencer sequencer = new(Route());
            List<string> reported = new();

            // Fly the line in small steps well past the last fix.
            for (double lon = -0.3; lon <= 5.0; lon += 0.05)
            {
                FmcRouteFix passed = sequencer.Update(50.0, lon);
                if (passed != null)
                {
                    reported.Add(passed.Ident);
                }
            }

            Assert.Equal(new[] { "ALPHA", "BRAVO", "CHARL", "DELTA" }, reported);
            Assert.True(sequencer.Finished);
        }

        [Fact]
        public void Sequencer_StopsCleanlyAtTheEndOfTheRoute()
        {
            FmcWaypointSequencer sequencer = new(new List<FmcRouteFix> { Fix("ONLY", 50, 0) });

            sequencer.Update(50.0, -0.02);
            Assert.NotNull(sequencer.Update(50.0, 0.05));

            Assert.True(sequencer.Finished);
            Assert.Null(sequencer.Active);
            Assert.Null(sequencer.Update(50.0, 1.0));
        }

        // A leg that gets cut - direct-to, or a vector around weather - must not leave
        // the sequencer stuck on a fix the aircraft is never going to reach.
        [Fact]
        public void Sequencer_SkipsAFixTheFlightNeverWentNear()
        {
            FmcWaypointSequencer sequencer = new(Route());

            // Jump straight to the far side of CHARL without ever nearing ALPHA/BRAVO.
            for (int i = 0; i < 6; i++)
            {
                sequencer.Update(50.0, 3.02);
            }

            Assert.NotEqual("ALPHA", sequencer.Active?.Ident);
        }

        [Fact]
        public void Sequencer_HandlesAnEmptyRoute()
        {
            FmcWaypointSequencer sequencer = new(new List<FmcRouteFix>());

            Assert.True(sequencer.Finished);
            Assert.Null(sequencer.Update(50, 0));
        }

        // ---- coordinate formatting ----------------------------------------

        [Theory]
        [InlineData(47.205, "N4712.3")]
        [InlineData(-47.205, "S4712.3")]
        [InlineData(0.0, "N0000.0")]
        [InlineData(51.5, "N5130.0")]
        public void Latitude_IsArincDegreesAndDecimalMinutes(double degrees, string expected)
        {
            Assert.Equal(expected, FmcPositionReport.Latitude(degrees));
        }

        [Theory]
        [InlineData(-8.205, "W00812.3")]
        [InlineData(8.205, "E00812.3")]
        [InlineData(-179.5, "W17930.0")]
        public void Longitude_CarriesThreeDegreeDigits(double degrees, string expected)
        {
            Assert.Equal(expected, FmcPositionReport.Longitude(degrees));
        }

        // 59.97 minutes rounds to 60.0, which is not a valid minutes value - it has to
        // carry into the degrees or the report contains a coordinate that cannot exist.
        [Fact]
        public void Coordinates_CarryRoundedMinutesIntoDegrees()
        {
            string latitude = FmcPositionReport.Latitude(50.99999);

            Assert.Equal("N5100.0", latitude);
            Assert.DoesNotContain("60.0", latitude);
        }

        // ---- report --------------------------------------------------------

        [Fact]
        public void Report_CarriesOverflownFixPositionAndTheNextTwoFixes()
        {
            string report = FmcPositionReport.Format(
                1, "dlh6ym", "ALPHA", "1423", "350",
                positionValid: true, latitude: 47.205, longitude: -8.205,
                nextIdent: "BRAVO", nextEta: "1442", followingIdent: "CHARL", groundSpeedKt: 468);

            Assert.StartsWith("POS01 DLH6YM", report);
            Assert.Contains("/OVR ALPHA 1423 F350", report);
            Assert.Contains("/PSN N4712.3 W00812.3", report);
            Assert.Contains("/NXT BRAVO 1442", report);
            Assert.Contains("/FLW CHARL", report);
            Assert.Contains("/GS 468", report);
        }

        // Every element except the position is a page field the pilot can clear, so the
        // report has to stay well formed when they do rather than emitting empty tags.
        [Fact]
        public void Report_OmitsSectionsTheFieldsLeaveBlank()
        {
            string report = FmcPositionReport.Format(
                9, "DLH6YM", "DELTA", "1500", "350",
                positionValid: true, latitude: 50.0, longitude: 4.5,
                nextIdent: "", nextEta: "", followingIdent: "", groundSpeedKt: 0);

            Assert.Contains("POS09", report);
            Assert.Contains("/OVR DELTA", report);
            Assert.DoesNotContain("/NXT", report);
            Assert.DoesNotContain("/FLW", report);
            Assert.DoesNotContain("/GS", report);
        }

        // No sim feed means no coordinates. Reporting 0N 0E - a point in the Atlantic -
        // would be worse than reporting no position at all.
        [Fact]
        public void Report_OmitsThePositionWhenTheSimIsNotFeedingOne()
        {
            string report = FmcPositionReport.Format(
                1, "DLH6YM", "ALPHA", "1423", "350",
                positionValid: false, latitude: 0, longitude: 0,
                nextIdent: "BRAVO", nextEta: "", followingIdent: "", groundSpeedKt: 0);

            Assert.DoesNotContain("/PSN", report);
            Assert.Contains("/OVR ALPHA", report);
        }

        [Fact]
        public void Report_AcceptsAFlightLevelTypedWithOrWithoutItsPrefix()
        {
            string plain = FmcPositionReport.Format(1, "X", "A", "1200", "350", false, 0, 0, "", "", "", 0);
            string prefixed = FmcPositionReport.Format(1, "X", "A", "1200", "F350", false, 0, 0, "", "", "", 0);

            Assert.Contains(" F350", plain);
            Assert.Equal(plain, prefixed);
        }

        [Fact]
        public void FlightLevel_IsThreeDigitsOfHundredsOfFeet()
        {
            Assert.Equal("350", FmcPositionReport.FlightLevel(35000));
            Assert.Equal("090", FmcPositionReport.FlightLevel(9000));
            Assert.Equal("000", FmcPositionReport.FlightLevel(-500));
        }

        // The ETA is distance-over-ground-speed from the report time. Computed here from
        // the same geometry rather than hard-coded, because a degree of longitude is not
        // 60 NM anywhere but the equator - at 50N these fixes are 57.9 NM apart, and
        // asserting a round number would be testing the wrong thing.
        [Fact]
        public void EstimateEta_IsDistanceOverGroundSpeedFromTheReportTime()
        {
            List<FmcRouteFix> route = Route();
            DateTime utc = new(2026, 7, 26, 14, 0, 0, DateTimeKind.Utc);
            const double groundSpeed = 60;

            double distance = FmcGeo.DistanceNm(50.0, 0.0, route[1].Latitude, route[1].Longitude);
            string expected = utc.AddHours(distance / groundSpeed).ToString("HHmm");

            Assert.Equal(expected, FmcPositionReport.EstimateEta(utc, 50.0, 0.0, groundSpeed, route[1]));
            Assert.InRange(distance, 57.0, 59.0);
        }

        [Fact]
        public void EstimateEta_IsEmptyWhenItCannotBeEstimated()
        {
            List<FmcRouteFix> route = Route();
            DateTime utc = DateTime.UtcNow;

            Assert.Equal(string.Empty, FmcPositionReport.EstimateEta(utc, 50, 0, 0, route[1]));
            Assert.Equal(string.Empty, FmcPositionReport.EstimateEta(utc, 50, 0, 450, null));
        }
    }
}
