using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EasyCPDLC
{
    /// <summary>
    /// Company position reporting - the AOC service ICAO calls E1, FMC waypoint position
    /// reporting over ACARS.
    ///
    /// The aircraft equipment sends these on its own; this app does not. It tracks the
    /// route passively so the report page opens already filled in with where you are and
    /// which fixes are next, and then the pilot arms and sends it like any other request.
    /// Nothing here transmits: an automatic downlink the pilot never saw is the one thing
    /// a datalink client should not do behind their back.
    /// </summary>
    public partial class MainForm
    {
        private const string AocAddressSettingName = "AocAddress";

        // Fast enough to catch a fix at cruise (8 NM of capture is ~1 minute at 480 kt)
        // without polling the sim harder than the flight-phase feed already does.
        private static readonly TimeSpan FmcSampleInterval = TimeSpan.FromSeconds(5);

        private System.Windows.Forms.Timer fmcTrackTimer;
        private FmcWaypointSequencer fmcSequencer;
        private string fmcRouteSignature = string.Empty;
        private DateTime fmcLastSampleUtc = DateTime.MinValue;

        private string fmcOverflownFix = string.Empty;
        private int fmcReportSequence;

        /// <summary>
        /// Hoppie address of the operator that receives the reports - an ordinary
        /// recipient callsign, not a credential, which is why it is set on the AOC page
        /// next to the report it addresses.
        /// </summary>
        internal static string SavedAocAddress
        {
            get => ReadFixedStringSetting(AocAddressSettingName, string.Empty).Trim().ToUpperInvariant();
            set => SaveFixedStringSetting(AocAddressSettingName, (value ?? string.Empty).Trim().ToUpperInvariant());
        }

        /// <summary>Next report number, consumed when one is actually sent.</summary>
        internal int NextFmcReportSequence => fmcReportSequence >= 99 ? 1 : fmcReportSequence + 1;

        internal void ConsumeFmcReportSequence()
        {
            fmcReportSequence = NextFmcReportSequence;
        }

        /// <summary>
        /// Route fixes from the loaded SimBrief plan, enroute only. SID and STAR legs are
        /// dropped: they sequence every couple of minutes, so tracking them would leave
        /// the report page pointing at a departure fix for the whole cruise.
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

        private static string FmcRouteSignature(IReadOnlyList<FmcRouteFix> route) =>
            route.Count + ":" + string.Join(">", route.Select(fix => fix.Ident));

        /// <summary>
        /// Live position, preferring the SimConnect telemetry the companion bridge
        /// already subscribes to and falling back to FSUIPC when that is what is running.
        /// </summary>
        internal bool TryGetFmcAircraftState(out FmcAircraftState state)
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
        /// Passive route tracking on its own timer: the Hoppie loop only runs while
        /// VATSIM-connected and the SI loop only when an SI key is set, but where the
        /// aircraft is along its route has nothing to do with either.
        /// </summary>
        private void EnsureFmcPositionReportTimer()
        {
            if (fmcTrackTimer != null)
            {
                return;
            }

            fmcTrackTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            fmcTrackTimer.Tick += (_, __) =>
            {
                try
                {
                    TrackFmcRoute();
                }
                catch (Exception ex)
                {
                    Logger.Debug("FMC route tracking failed: " + ex.Message);
                }
            };
            fmcTrackTimer.Start();
        }

        /// <summary>
        /// Advances the sequencer against the current position. Records which fix was
        /// last overflown so the report page can open already filled in. Sends nothing.
        /// </summary>
        private void TrackFmcRoute()
        {
            if (DebugUiPreviewMode || DateTime.UtcNow - fmcLastSampleUtc < FmcSampleInterval)
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
                // A new flight plan starts a new flight: reset the tracker and the report
                // numbering rather than carrying the last leg's state into it.
                fmcRouteSignature = signature;
                fmcSequencer = new FmcWaypointSequencer(route);
                fmcOverflownFix = string.Empty;
                fmcReportSequence = 0;
                Logger.Debug("FMC route tracking armed for " + route.Count + " enroute fixes");
            }

            if (!TryGetFmcAircraftState(out FmcAircraftState state))
            {
                return;
            }

            FmcRouteFix passed = fmcSequencer.Update(state.Latitude, state.Longitude);
            if (passed != null)
            {
                fmcOverflownFix = passed.Ident;
            }
        }

        /// <summary>
        /// Everything the AOC position report page needs, folded into the snapshot the
        /// instruments already read.
        /// </summary>
        private void FillFmcSnapshotFields(
            out string company, out string overflown, out string next, out string following, out string eta,
            out bool positionValid, out double latitude, out double longitude, out double altitudeFt, out double groundSpeedKt)
        {
            company = SavedAocAddress;
            overflown = fmcOverflownFix;
            next = fmcSequencer?.Active?.Ident ?? string.Empty;
            following = fmcSequencer?.Next?.Ident ?? string.Empty;
            eta = string.Empty;

            positionValid = TryGetFmcAircraftState(out FmcAircraftState state);
            latitude = state.Latitude;
            longitude = state.Longitude;
            altitudeFt = state.AltitudeFt;
            groundSpeedKt = state.GroundSpeedKt;

            if (positionValid && fmcSequencer?.Active != null)
            {
                eta = FmcPositionReport.EstimateEta(
                    DateTime.UtcNow, latitude, longitude, groundSpeedKt, fmcSequencer.Active);
            }
        }
    }
}
