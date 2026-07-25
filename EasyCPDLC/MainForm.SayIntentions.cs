using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using EasyCPDLC.VNS430;

namespace EasyCPDLC
{
    /// <summary>
    /// SayIntentions datalink mode: flight identity and prerequisites.
    /// </summary>
    /// <remarks>
    /// On VATSIM the flight identity (callsign, departure, arrival, aircraft) comes from
    /// the VATSIM data feed once connected. SayIntentions has no such feed - its ATC
    /// identifies the flight by the SimBrief plan on file - so in SI mode the same
    /// identity is read from the SimBrief OFP instead, and a VATSIM connection is not
    /// required for the datalink to work.
    /// </remarks>
    public partial class MainForm
    {
        private static readonly TimeSpan SayIntentionsFlightCacheTtl = TimeSpan.FromMinutes(10);

        private string siFlightCallsign = string.Empty;
        private string siFlightDeparture = string.Empty;
        private string siFlightArrival = string.Empty;
        private string siFlightAircraft = string.Empty;
        private DateTime siFlightFetchedUtc = DateTime.MinValue;

        internal bool IsSayIntentionsDatalinkActive =>
            ActiveAtcNetwork == Vns430AtcNetwork.SayIntentions;

        // Whether SI-mode datalink actions can be offered at all. Kept synchronous and
        // cheap so availability gates can call it; the actual OFP fetch happens at
        // action time in EnsureSayIntentionsFlightAsync.
        internal bool SayIntentionsDatalinkPrerequisitesMet =>
            !string.IsNullOrWhiteSpace(SavedSayIntentionsApiKey) &&
            (!string.IsNullOrWhiteSpace(SimbriefID) || Connected);

        /// <summary>
        /// Makes sure the SI flight identity is loaded, refreshing the cached SimBrief
        /// OFP when stale. Returns false (with a SYSTEM message) when it cannot.
        /// </summary>
        internal async Task<bool> EnsureSayIntentionsFlightAsync()
        {
            // A live VATSIM connection is still the best identity source when present.
            if (Connected && !string.IsNullOrWhiteSpace(callsign))
            {
                return true;
            }

            if (DateTime.UtcNow - siFlightFetchedUtc < SayIntentionsFlightCacheTtl &&
                !string.IsNullOrWhiteSpace(siFlightCallsign))
            {
                AdoptSayIntentionsCallsign();
                return true;
            }

            if (string.IsNullOrWhiteSpace(SimbriefID))
            {
                WriteMessage("SET SIMBRIEF ID FOR SAYINTENTIONS DATALINK", "SYSTEM", "SYSTEM");
                return false;
            }

            try
            {
                using HttpClient wc = CreateShortTimeoutHttpClient();
                string json = await wc.GetStringAsync(SimbriefLoadsheetClient.BuildFetchUrl(SimbriefID));
                JObject root = JObject.Parse(json);

                string ofpCallsign = ((string)(root.SelectToken("atc.callsign")
                    ?? root.SelectToken("general.callsign")
                    ?? root.SelectToken("general.flight_number")) ?? string.Empty).Trim().ToUpperInvariant();
                string departure = ((string)root.SelectToken("origin.icao_code") ?? string.Empty).Trim().ToUpperInvariant();
                string arrival = ((string)root.SelectToken("destination.icao_code") ?? string.Empty).Trim().ToUpperInvariant();
                string aircraft = ((string)(root.SelectToken("aircraft.icaocode")
                    ?? root.SelectToken("aircraft.icao_code")) ?? string.Empty).Trim().ToUpperInvariant();

                if (string.IsNullOrWhiteSpace(ofpCallsign))
                {
                    WriteMessage("SIMBRIEF PLAN HAS NO CALLSIGN - FILE AN OFP FIRST", "SYSTEM", "SYSTEM");
                    return false;
                }

                siFlightCallsign = ofpCallsign;
                siFlightDeparture = departure;
                siFlightArrival = arrival;
                siFlightAircraft = aircraft;
                siFlightFetchedUtc = DateTime.UtcNow;

                AdoptSayIntentionsCallsign();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SayIntentions flight identity fetch failed");
                WriteMessage("COULD NOT LOAD SIMBRIEF PLAN FOR SAYINTENTIONS", "SYSTEM", "SYSTEM");
                return false;
            }
        }

        // The ACARS 'from' field and the poll both use the callsign field, so SI mode
        // adopts the OFP callsign - but never overrides a live VATSIM identity, and
        // only while SI is the active network.
        private void AdoptSayIntentionsCallsign()
        {
            if (!Connected &&
                IsSayIntentionsDatalinkActive &&
                string.IsNullOrWhiteSpace(callsign) &&
                !string.IsNullOrWhiteSpace(siFlightCallsign))
            {
                callsign = siFlightCallsign;
                UpdateCallsignDisplay();
            }
        }

        // SI-mode flight data with VATSIM fallback, for building the PDC request.
        internal string SayIntentionsDeparture() =>
            FirstNonBlank(userVATSIMData?.flight_plan?.departure, siFlightDeparture);
        internal string SayIntentionsArrival() =>
            FirstNonBlank(userVATSIMData?.flight_plan?.arrival, siFlightArrival);
        internal string SayIntentionsAircraft() =>
            FirstNonBlank(userVATSIMData?.flight_plan?.aircraft_short, siFlightAircraft);

        private static string FirstNonBlank(string preferred, string fallback)
        {
            string first = (preferred ?? string.Empty).Trim().ToUpperInvariant();
            return first.Length > 0 ? first : (fallback ?? string.Empty).Trim().ToUpperInvariant();
        }
    }
}
