using EasyCPDLC.VNS430;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace EasyCPDLC
{
    /// <summary>
    /// FMC waypoint position reporting over ACARS - ICAO equipment code E1.
    ///
    /// This is the AOC half of the datalink, not ATC: as each route fix is sequenced the
    /// aircraft downlinks a position report to the operator. It is what an airline's ops
    /// desk watches a flight with, and it is the one datalink service the app previously
    /// could not honestly claim.
    ///
    /// Deliberately separate from the CPDLC position report on the ATC pages: that one is
    /// composed by the pilot and addressed to a controller. This one is automatic and
    /// addressed to the company.
    /// </summary>
    public partial class MainForm
    {
        private const string AocAddressSettingName = "AocAddress";
        private const string FmcReportsEnabledSettingName = "FmcPositionReports";
        private const string FmcReportIntervalSettingName = "FmcPositionReportInterval";

        // Position is sampled often enough to catch a fix at cruise speed without
        // hammering the sim: 8 NM of capture radius is ~1 minute at 480 kt.
        private static readonly TimeSpan FmcSampleInterval = TimeSpan.FromSeconds(10);

        private System.Windows.Forms.Timer fmcReportTimer;
        private bool fmcTickRunning;
        private FmcWaypointSequencer fmcSequencer;
        private string fmcRouteSignature = string.Empty;
        private int fmcReportSequence;
        private DateTime fmcLastSampleUtc = DateTime.MinValue;
        private DateTime fmcLastReportUtc = DateTime.MinValue;

        /// <summary>
        /// Hoppie address of the operator that receives the reports. Blank disables the
        /// whole feature: reports must never go somewhere the pilot did not name.
        /// </summary>
        internal static string SavedAocAddress
        {
            get => ReadFixedStringSetting(AocAddressSettingName, string.Empty).Trim().ToUpperInvariant();
            set => SaveFixedStringSetting(AocAddressSettingName, (value ?? string.Empty).Trim().ToUpperInvariant());
        }

        internal static bool FmcPositionReportsEnabled
        {
            get => string.Equals(ReadFixedStringSetting(FmcReportsEnabledSettingName, "OFF"), "ON", StringComparison.OrdinalIgnoreCase);
            set => SaveFixedStringSetting(FmcReportsEnabledSettingName, value ? "ON" : "OFF");
        }

        /// <summary>
        /// Extra periodic report in minutes; 0 means waypoint sequencing only, which is
        /// what E1 actually describes. The periodic option exists for oceanic legs where
        /// fixes can be an hour apart.
        /// </summary>
        internal static int FmcPositionReportIntervalMinutes
        {
            get
            {
                string value = ReadFixedStringSetting(FmcReportIntervalSettingName, "0");
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
                    ? Math.Clamp(minutes, 0, 120)
                    : 0;
            }
            set => SaveFixedStringSetting(FmcReportIntervalSettingName, Math.Clamp(value, 0, 120).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Whether the feature can run at all: armed, addressed, and with a route.</summary>
        internal bool FmcPositionReportsReady =>
            FmcPositionReportsEnabled &&
            !string.IsNullOrWhiteSpace(SavedAocAddress) &&
            !string.IsNullOrWhiteSpace(logonCode) &&
            BuildFmcRoute().Count > 0;

        /// <summary>
        /// Route fixes from the loaded SimBrief plan, enroute only. SID and STAR legs are
        /// dropped for the same reason the ATC report list drops them: they sequence in
        /// minutes and would bury the ops desk in reports during departure and arrival.
        /// </summary>
        private List<FmcRouteFix> BuildFmcRoute()
        {
            List<FmcRouteFix> route = new();
            IEnumerable<Fix> source = simbriefData?.fix;
            if (source == null)
            {
                return route;
            }

            foreach (Fix fix in source)
            {
                if (fix == null || fix.is_sid_star != "0" || fix.type == "apt")
                {
                    continue;
                }

                if (!double.TryParse(fix.pos_lat, NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) ||
                    !double.TryParse(fix.pos_long, NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude))
                {
                    continue;
                }

                route.Add(new FmcRouteFix
                {
                    Ident = (fix.ident ?? string.Empty).Trim().ToUpperInvariant(),
                    Latitude = latitude,
                    Longitude = longitude
                });
            }

            return route;
        }

        /// <summary>
        /// A cheap identity for the loaded route, so a new flight plan resets the
        /// sequencer instead of continuing from the old flight's waypoint.
        /// </summary>
        private static string FmcRouteSignature(IReadOnlyList<FmcRouteFix> route) =>
            route.Count + ":" + string.Join(">", route.Select(fix => fix.Ident));

        /// <summary>
        /// Live position, preferring the SimConnect telemetry the companion bridge
        /// already subscribes to and falling back to FSUIPC when that is what is running.
        /// </summary>
        private bool TryGetFmcAircraftState(out FmcAircraftState state)
        {
            state = default;

            if (vns430Panel != null && !vns430Panel.IsDisposed &&
                vns430Panel.TryGetCompanionPosition(out double latitude, out double longitude, out double altitude, out double groundSpeed))
            {
                state = new FmcAircraftState(latitude, longitude, altitude, groundSpeed);
                return true;
            }

            try
            {
                if (UseFSUIPC && fsuipc != null)
                {
                    state = new FmcAircraftState(
                        fsuipc.position.Latitude.DecimalDegrees,
                        fsuipc.position.Longitude.DecimalDegrees,
                        fsuipc.altitude.Feet,
                        fsuipc.groundspeed);
                    return Math.Abs(state.Latitude) > 0.0001 || Math.Abs(state.Longitude) > 0.0001;
                }
            }
            catch
            {
                // A sim that disappears mid-flight must not take the app with it.
            }

            return false;
        }

        /// <summary>
        /// Runs on its own timer rather than inside a poll loop: the Hoppie loop only
        /// exists while VATSIM-connected and the SI loop only when an SI key is set, but
        /// position reporting is a property of being in a flight, not of either network.
        /// </summary>
        private void EnsureFmcPositionReportTimer()
        {
            if (fmcReportTimer != null)
            {
                return;
            }

            fmcReportTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            fmcReportTimer.Tick += async (_, __) =>
            {
                // The send is awaited, so a slow network round trip must not stack ticks.
                if (fmcTickRunning)
                {
                    return;
                }

                fmcTickRunning = true;
                try
                {
                    await TickFmcPositionReportsAsync();
                }
                catch (Exception ex)
                {
                    Logger.Debug("FMC position report tick failed: " + ex.Message);
                }
                finally
                {
                    fmcTickRunning = false;
                }
            };
            fmcReportTimer.Start();
        }

        /// <summary>
        /// Called from the periodic tick. Samples position, sequences the route, and
        /// downlinks a report when a fix is passed (or the periodic timer expires).
        /// </summary>
        internal async Task TickFmcPositionReportsAsync()
        {
            if (DebugUiPreviewMode)
            {
                return;
            }

            if (!FmcPositionReportsEnabled || string.IsNullOrWhiteSpace(SavedAocAddress))
            {
                return;
            }

            if (DateTime.UtcNow - fmcLastSampleUtc < FmcSampleInterval)
            {
                return;
            }
            fmcLastSampleUtc = DateTime.UtcNow;

            List<FmcRouteFix> route = BuildFmcRoute();
            if (route.Count == 0)
            {
                return;
            }

            string signature = FmcRouteSignature(route);
            if (fmcSequencer == null || signature != fmcRouteSignature)
            {
                fmcRouteSignature = signature;
                fmcSequencer = new FmcWaypointSequencer(route);
                fmcReportSequence = 0;
                fmcLastReportUtc = DateTime.MinValue;
                Logger.Debug("FMC position reports armed for " + route.Count + " enroute fixes");
            }

            if (!TryGetFmcAircraftState(out FmcAircraftState state))
            {
                return;
            }

            FmcRouteFix passed = fmcSequencer.Update(state.Latitude, state.Longitude);
            bool periodicDue = FmcPositionReportIntervalMinutes > 0 &&
                fmcLastReportUtc != DateTime.MinValue &&
                DateTime.UtcNow - fmcLastReportUtc >= TimeSpan.FromMinutes(FmcPositionReportIntervalMinutes);

            if (passed == null && !periodicDue)
            {
                return;
            }

            // A periodic report has not overflown anything; name the fix being tracked so
            // the ops desk still knows where the aircraft is against the plan.
            FmcRouteFix reference = passed ?? fmcSequencer.Active;
            if (reference == null)
            {
                return;
            }

            await SendFmcPositionReportAsync(reference, state);
        }

        private async Task SendFmcPositionReportAsync(FmcRouteFix reference, FmcAircraftState state)
        {
            string address = SavedAocAddress;
            if (string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            fmcReportSequence = fmcReportSequence >= 99 ? 1 : fmcReportSequence + 1;
            fmcLastReportUtc = DateTime.UtcNow;

            string report = FmcPositionReport.Format(
                fmcReportSequence,
                string.IsNullOrWhiteSpace(callsign) ? SimbriefIdent : callsign,
                reference,
                DateTime.UtcNow,
                state,
                fmcSequencer?.Active,
                fmcSequencer?.Next);

            Logger.Debug("FMC position report " + fmcReportSequence + " over " + reference.Ident);

            // Company traffic, so always Hoppie - the operator's ACARS address lives
            // there whichever network ATC is on.
            await SendCPDLCMessage(address, "TELEX", report, true, AcarsRoute.Hoppie);
        }
    }
}
