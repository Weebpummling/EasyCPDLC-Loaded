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

        /// <summary>Route of the currently loaded SimBrief plan, or empty.</summary>
        internal string SimbriefPlanRoute => simbriefPlanRoute;

        internal bool SimbriefPlanLoaded => !string.IsNullOrWhiteSpace(simbriefPlanRoute);

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
