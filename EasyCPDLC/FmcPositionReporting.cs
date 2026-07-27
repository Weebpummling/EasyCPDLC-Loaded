using System;
using System.Collections.Generic;
using System.Globalization;

namespace EasyCPDLC
{
    /// <summary>
    /// One route fix the sequencer can pass abeam.
    /// </summary>
    internal sealed class FmcRouteFix
    {
        internal string Ident { get; init; } = string.Empty;
        internal double Latitude { get; init; }
        internal double Longitude { get; init; }
    }

    /// <summary>
    /// Live aircraft state a report is built from. Kept as a plain value so the engine
    /// can be fed from SimConnect, FSUIPC, or a test.
    /// </summary>
    internal readonly struct FmcAircraftState
    {
        internal FmcAircraftState(double latitude, double longitude, double altitudeFt, double groundSpeedKt)
        {
            Latitude = latitude;
            Longitude = longitude;
            AltitudeFt = altitudeFt;
            GroundSpeedKt = groundSpeedKt;
        }

        internal double Latitude { get; }
        internal double Longitude { get; }
        internal double AltitudeFt { get; }
        internal double GroundSpeedKt { get; }
    }

    /// <summary>
    /// Great-circle helpers. Small enough to keep local rather than take a dependency.
    /// </summary>
    internal static class FmcGeo
    {
        private const double EarthRadiusNm = 3440.065;

        internal static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
        {
            double dLat = ToRadians(lat2 - lat1);
            double dLon = ToRadians(lon2 - lon1);
            double a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) +
                       (Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                        Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
            return 2 * EarthRadiusNm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
    }

    /// <summary>
    /// Detects waypoint sequencing from position alone.
    ///
    /// A real FMC knows it has sequenced a leg because it flew the leg. We only have
    /// position, so the rule is: come within the capture radius of the active fix, then
    /// start moving away from it again. Waiting for the range to open is what stops a
    /// single noisy sample near the fix from reporting early, and it is why the report
    /// says "overflew" rather than "approaching".
    ///
    /// A fix that is never captured - a shortcut, a direct-to, a vector around weather -
    /// is dropped rather than reported late: if a later fix is clearly closer and the
    /// active one is well behind, the sequencer skips ahead.
    /// </summary>
    internal sealed class FmcWaypointSequencer
    {
        /// <summary>How close counts as overflying a fix.</summary>
        internal const double CaptureRadiusNm = 8.0;

        /// <summary>How far the range must reopen before the fix counts as passed.</summary>
        internal const double DepartureHysteresisNm = 0.5;

        /// <summary>A fix this far off track is treated as skipped, not pending.</summary>
        internal const double SkipDistanceNm = 40.0;

        private readonly IReadOnlyList<FmcRouteFix> fixes;
        private int index;
        private double closestNm = double.MaxValue;
        private bool captured;

        internal FmcWaypointSequencer(IReadOnlyList<FmcRouteFix> fixes)
        {
            this.fixes = fixes ?? Array.Empty<FmcRouteFix>();
        }

        internal FmcRouteFix Active => index < fixes.Count ? fixes[index] : null;
        internal FmcRouteFix Next => index + 1 < fixes.Count ? fixes[index + 1] : null;
        internal bool Finished => index >= fixes.Count;

        /// <summary>
        /// Feeds a position in. Returns the fix just sequenced, or null.
        /// </summary>
        internal FmcRouteFix Update(double latitude, double longitude)
        {
            if (Finished)
            {
                return null;
            }

            FmcRouteFix active = fixes[index];
            double distance = FmcGeo.DistanceNm(latitude, longitude, active.Latitude, active.Longitude);

            if (distance <= CaptureRadiusNm)
            {
                captured = true;
                closestNm = Math.Min(closestNm, distance);

                // Still closing, or holding station on top of it.
                if (distance <= closestNm + DepartureHysteresisNm)
                {
                    return null;
                }

                return Advance();
            }

            if (captured)
            {
                // Captured and now outside the radius: definitely past it.
                return Advance();
            }

            // Never captured. If a later fix is closer and this one is far behind, the
            // leg was cut - drop it silently rather than waiting forever.
            if (distance > SkipDistanceNm && IsLaterFixCloser(latitude, longitude, distance))
            {
                index += 1;
                closestNm = double.MaxValue;
                captured = false;
                return null;
            }

            return null;
        }

        private bool IsLaterFixCloser(double latitude, double longitude, double activeDistance)
        {
            for (int i = index + 1; i < fixes.Count; i++)
            {
                double distance = FmcGeo.DistanceNm(latitude, longitude, fixes[i].Latitude, fixes[i].Longitude);
                if (distance < activeDistance)
                {
                    return true;
                }
            }

            return false;
        }

        private FmcRouteFix Advance()
        {
            FmcRouteFix passed = fixes[index];
            index += 1;
            closestNm = double.MaxValue;
            captured = false;
            return passed;
        }
    }

    /// <summary>
    /// Formats an ACARS company position report.
    ///
    /// This is the AOC side of the datalink, not ATC: the report goes to the operator,
    /// which is what ICAO equipment code E1 (FMC waypoint position reporting over ACARS)
    /// actually describes. Airlines each have their own wording, so this follows the
    /// common ARINC-style shape rather than any one carrier's format.
    /// </summary>
    internal static class FmcPositionReport
    {
        internal static string Format(
            int sequence,
            string callsign,
            FmcRouteFix overflown,
            DateTime utc,
            FmcAircraftState state,
            FmcRouteFix next,
            FmcRouteFix following)
        {
            System.Text.StringBuilder report = new();
            report.Append("POS")
                  .Append(Math.Clamp(sequence, 1, 99).ToString("00", CultureInfo.InvariantCulture))
                  .Append(' ')
                  .Append((callsign ?? string.Empty).Trim().ToUpperInvariant());

            report.Append(" /OVR ").Append(Ident(overflown))
                  .Append(' ').Append(utc.ToString("HHmm", CultureInfo.InvariantCulture))
                  .Append(' ').Append(FlightLevel(state.AltitudeFt));

            report.Append(" /PSN ").Append(Latitude(state.Latitude))
                  .Append(' ').Append(Longitude(state.Longitude));

            if (next != null)
            {
                report.Append(" /NXT ").Append(Ident(next));
                string eta = Eta(utc, state, next);
                if (!string.IsNullOrEmpty(eta))
                {
                    report.Append(' ').Append(eta);
                }
            }

            if (following != null)
            {
                report.Append(" /FLW ").Append(Ident(following));
            }

            if (state.GroundSpeedKt > 0)
            {
                report.Append(" /GS ").Append(Math.Round(state.GroundSpeedKt).ToString("0", CultureInfo.InvariantCulture));
            }

            return report.ToString();
        }

        private static string Ident(FmcRouteFix fix) =>
            string.IsNullOrWhiteSpace(fix?.Ident) ? "----" : fix.Ident.Trim().ToUpperInvariant();

        private static string FlightLevel(double altitudeFt) =>
            "F" + Math.Round(Math.Max(0, altitudeFt) / 100.0).ToString("000", CultureInfo.InvariantCulture);

        /// <summary>
        /// ARINC-style latitude: hemisphere, whole degrees, then minutes to one decimal.
        /// </summary>
        internal static string Latitude(double degrees)
        {
            char hemisphere = degrees < 0 ? 'S' : 'N';
            return hemisphere + DegreesMinutes(Math.Abs(degrees), 2);
        }

        internal static string Longitude(double degrees)
        {
            char hemisphere = degrees < 0 ? 'W' : 'E';
            return hemisphere + DegreesMinutes(Math.Abs(degrees), 3);
        }

        private static string DegreesMinutes(double absolute, int degreeDigits)
        {
            int whole = (int)Math.Floor(absolute);
            double minutes = (absolute - whole) * 60.0;

            // Rounding can push minutes to 60.0; carry it into the degrees.
            if (Math.Round(minutes, 1) >= 60.0)
            {
                whole += 1;
                minutes = 0;
            }

            return whole.ToString(new string('0', degreeDigits), CultureInfo.InvariantCulture) +
                   Math.Round(minutes, 1).ToString("00.0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// ETA at the next fix from current ground speed. Omitted when stopped, because
        /// a divide-by-zero estimate is worse than no estimate.
        /// </summary>
        private static string Eta(DateTime utc, FmcAircraftState state, FmcRouteFix next)
        {
            if (state.GroundSpeedKt < 40 || next == null)
            {
                return string.Empty;
            }

            double distance = FmcGeo.DistanceNm(state.Latitude, state.Longitude, next.Latitude, next.Longitude);
            double hours = distance / state.GroundSpeedKt;
            if (double.IsNaN(hours) || double.IsInfinity(hours) || hours > 24)
            {
                return string.Empty;
            }

            return utc.AddHours(hours).ToString("HHmm", CultureInfo.InvariantCulture);
        }
    }
}
