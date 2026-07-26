using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace EasyCPDLC
{
    /// <summary>
    /// LOAD / RELOAD SIMBRIEF FP: pulls the current SimBrief OFP and refreshes every
    /// consumer of it in one action.
    /// </summary>
    /// <remarks>
    /// SimBrief data feeds several subsystems that each cached it independently: the
    /// navlog fixes for position reports, the SI flight identity (ten-minute cache) and
    /// the eLoadControl loadsheet source. Re-planning in SimBrief left the app on the
    /// old plan with no obvious way to refresh - notably eLoadControl, whose session is
    /// only rebuilt when its page is re-opened. This clears all of them together.
    /// </remarks>
    public partial class MainForm
    {
        private string simbriefPlanRoute = string.Empty;
        private string simbriefCallsign = string.Empty;
        private string simbriefRegistration = string.Empty;
        private string simbriefDeparture = string.Empty;
        private string simbriefArrival = string.Empty;

        /// <summary>Departure from the loaded SimBrief plan, or empty.</summary>
        internal string SimbriefDeparture => simbriefDeparture;
        internal string SimbriefArrival => simbriefArrival;

        /// <summary>
        /// The information letter from the most recent ATIS actually received, or empty.
        /// </summary>
        /// <remarks>
        /// Used to prefill the PDC request. Deliberately blank until a real ATIS has
        /// arrived: the old behaviour defaulted to "A", which is a plausible-looking
        /// value that is usually wrong, and a wrong ATIS letter on a clearance request
        /// is worse than an obviously empty field.
        /// </remarks>
        internal string LastReceivedAtisLetter { get; private set; } = string.Empty;

        private static readonly System.Text.RegularExpressions.Regex AtisLetterPattern = new(
            @"\b(?:INFORMATION|INFO|ATIS)\s+([A-Z])\b(?!\w)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Records the information letter from an inbound ATIS message.</summary>
        internal void RecordAtisInformationLetter(string atisText)
        {
            System.Text.RegularExpressions.Match match =
                AtisLetterPattern.Match(atisText ?? string.Empty);
            if (match.Success)
            {
                LastReceivedAtisLetter = match.Groups[1].Value.ToUpperInvariant();
                Logger.Debug("Recorded ATIS information " + LastReceivedAtisLetter);
            }
        }

        /// <summary>Route of the currently loaded SimBrief plan, or empty.</summary>
        internal string SimbriefPlanRoute => simbriefPlanRoute;

        internal bool SimbriefPlanLoaded => !string.IsNullOrWhiteSpace(simbriefPlanRoute);

        /// <summary>
        /// What the CDU shows as the aircraft identity: the SimBrief plan's callsign,
        /// its registration when the plan has no callsign, and nothing at all when no
        /// plan is loaded.
        /// </summary>
        /// <remarks>
        /// Deliberately not the app's live callsign field. That is set from a VATSIM
        /// connection or an adopted OFP and survives the flight it came from, so the
        /// header kept displaying a stale callsign from a previous session.
        /// </remarks>
        internal string SimbriefIdent =>
            !string.IsNullOrWhiteSpace(simbriefCallsign) ? simbriefCallsign : simbriefRegistration;

        // Records the identity from a parsed SimBrief OFP. Shared by every path that
        // reads one, so the header cannot disagree with the loaded plan.
        private void CaptureSimbriefIdent(JObject root)
        {
            simbriefCallsign = ((string)(root.SelectToken("atc.callsign")
                ?? root.SelectToken("general.callsign")
                ?? root.SelectToken("general.flight_number")) ?? string.Empty).Trim().ToUpperInvariant();
            simbriefRegistration = ((string)(root.SelectToken("aircraft.reg")
                ?? root.SelectToken("aircraft.registration")) ?? string.Empty).Trim().ToUpperInvariant();
            simbriefDeparture = ((string)root.SelectToken("origin.icao_code") ?? string.Empty).Trim().ToUpperInvariant();
            simbriefArrival = ((string)root.SelectToken("destination.icao_code") ?? string.Empty).Trim().ToUpperInvariant();
        }

        internal async Task LoadSimbriefFlightPlanAsync()
        {
            if (string.IsNullOrWhiteSpace(SimbriefID))
            {
                WriteMessage("SET SIMBRIEF ID IN SETUP / ACCOUNT FIRST", "SYSTEM", "SYSTEM");
                return;
            }

            try
            {
                using HttpClient wc = CreateShortTimeoutHttpClient();

                // Cache-bust: the OFP is fetched over plain GET and a cached response
                // would defeat the entire point of a manual reload.
                string url = SimbriefLoadsheetClient.BuildFetchUrl(SimbriefID) +
                    "&_=" + DateTime.UtcNow.Ticks.ToString();
                string json = await wc.GetStringAsync(url);
                JObject root = JObject.Parse(json);

                string origin = ((string)root.SelectToken("origin.icao_code") ?? string.Empty).Trim().ToUpperInvariant();
                string destination = ((string)root.SelectToken("destination.icao_code") ?? string.Empty).Trim().ToUpperInvariant();
                if (origin.Length == 0 || destination.Length == 0)
                {
                    WriteMessage("SIMBRIEF PLAN INCOMPLETE - GENERATE AN OFP FIRST", "SYSTEM", "SYSTEM");
                    return;
                }

                // Navlog fixes, used by the position-report workflow.
                string navlog = root["navlog"]?.ToString();
                if (!string.IsNullOrWhiteSpace(navlog))
                {
                    simbriefData = JsonConvert.DeserializeObject<Navlog>(navlog);
                    reportFixes = simbriefData?.fix?
                        .Where(x => x.is_sid_star == "0" && !new string[] { "apt" }.Contains(x.type))
                        .Select(x => x.ident)
                        .ToArray();
                }

                // Drop every cached derivative so the new plan is picked up everywhere.
                siFlightFetchedUtc = DateTime.MinValue;
                cduLoadSession = null;

                CaptureSimbriefIdent(root);
                simbriefPlanRoute = origin + "-" + destination;
                WriteMessage("SIMBRIEF FP LOADED: " + simbriefPlanRoute, "SYSTEM", "SYSTEM");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "LOAD SIMBRIEF FP failed");
                WriteMessage("SIMBRIEF FP LOAD FAILED", "SYSTEM", "SYSTEM");
            }
        }
    }
}
