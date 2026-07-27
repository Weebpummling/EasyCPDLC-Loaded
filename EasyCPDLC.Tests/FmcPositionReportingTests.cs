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
            List<FmcRouteFix> route = Route();
            FmcAircraftState state = new(47.205, -8.205, 35000, 468);

            string report = FmcPositionReport.Format(
                1, "dlh6ym", route[0], new DateTime(2026, 7, 26, 14, 23, 0, DateTimeKind.Utc),
                state, route[1], route[2]);

            Assert.StartsWith("POS01 DLH6YM", report);
            Assert.Contains("/OVR ALPHA 1423 F350", report);
            Assert.Contains("/PSN N4712.3 W00812.3", report);
            Assert.Contains("/NXT BRAVO", report);
            Assert.Contains("/FLW CHARL", report);
            Assert.Contains("/GS 468", report);
        }

        [Fact]
        public void Report_OmitsTheEtaWhenStopped()
        {
            List<FmcRouteFix> route = Route();
            FmcAircraftState stopped = new(50.0, 0.0, 0, 0);

            string report = FmcPositionReport.Format(
                1, "DLH6YM", route[0], DateTime.UtcNow, stopped, route[1], route[2]);

            Assert.Contains("/NXT BRAVO", report);
            Assert.DoesNotContain("/GS", report);
        }

        [Fact]
        public void Report_HandlesTheLastFixWithNothingAhead()
        {
            List<FmcRouteFix> route = Route();
            FmcAircraftState state = new(50.0, 4.5, 35000, 450);

            string report = FmcPositionReport.Format(
                9, "DLH6YM", route[3], DateTime.UtcNow, state, null, null);

            Assert.Contains("POS09", report);
            Assert.Contains("/OVR DELTA", report);
            Assert.DoesNotContain("/NXT", report);
            Assert.DoesNotContain("/FLW", report);
        }

        // The ETA is distance-over-ground-speed from the report time. Computed here from
        // the same geometry rather than hard-coded, because a degree of longitude is not
        // 60 NM anywhere but the equator - at 50N these fixes are 57.9 NM apart, and
        // asserting a round number would be testing the wrong thing.
        [Fact]
        public void Report_EtaIsDistanceOverGroundSpeedFromTheReportTime()
        {
            List<FmcRouteFix> route = Route();
            DateTime utc = new(2026, 7, 26, 14, 0, 0, DateTimeKind.Utc);
            const double groundSpeed = 60;
            FmcAircraftState state = new(50.0, 0.0, 35000, groundSpeed);

            double distance = FmcGeo.DistanceNm(50.0, 0.0, route[1].Latitude, route[1].Longitude);
            string expected = utc.AddHours(distance / groundSpeed).ToString("HHmm");

            string report = FmcPositionReport.Format(1, "DLH6YM", route[0], utc, state, route[1], null);

            Assert.Contains("/NXT BRAVO " + expected, report);
            Assert.InRange(distance, 57.0, 59.0);
        }
    }
}
