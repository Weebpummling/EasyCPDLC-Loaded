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

        private System.Threading.CancellationTokenSource siPollCancellationSource;
        private static readonly TimeSpan SayIntentionsPollInterval = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Starts or stops the SayIntentions poll loop to match the active network.
        /// </summary>
        /// <remarks>
        /// The Hoppie poll loop only exists while VATSIM-connected (it is started by a
        /// successful connect), but on SI there is no connect step - the datalink is
        /// live as soon as the key is set. Without a loop of its own, SI sends worked
        /// and the replies were simply never fetched. Call this at startup and whenever
        /// the ATC network selection changes.
        /// </remarks>
        internal void SyncSayIntentionsPolling()
        {
            // Also runs when only the PDC is routed to SI (PDC VIA = SI on the VATSIM
            // network) - the clearance reply still arrives over the SI network.
            bool shouldRun = (IsSayIntentionsDatalinkActive || PdcRoutesToSayIntentions) &&
                !string.IsNullOrWhiteSpace(SavedSayIntentionsApiKey) &&
                !DebugUiPreviewMode;

            if (shouldRun && siPollCancellationSource == null)
            {
                siPollCancellationSource = new System.Threading.CancellationTokenSource();
                _ = PeriodicSayIntentionsPoll(siPollCancellationSource.Token);
            }
            else if (!shouldRun && siPollCancellationSource != null)
            {
                siPollCancellationSource.Cancel();
                siPollCancellationSource.Dispose();
                siPollCancellationSource = null;
            }
        }

        private async Task PeriodicSayIntentionsPoll(System.Threading.CancellationToken token)
        {
            Logger.Debug("SayIntentions poll loop started");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // The poll needs a callsign for its 'from' field. The OFP fetch is
                    // cached, so this settles once and then only refreshes when stale.
                    if (string.IsNullOrWhiteSpace(callsign))
                    {
                        await EnsureSayIntentionsFlightAsync();
                    }

                    if (!string.IsNullOrWhiteSpace(callsign))
                    {
                        await SendCPDLCMessage("NONE", "poll", "", true, AcarsRoute.SayIntentions);

                        // Keep VA traffic alive too: when the VATSIM loop is not running
                        // (not connected) but a Hoppie code exists, poll Hoppie from here.
                        if (!Connected && !string.IsNullOrWhiteSpace(logonCode))
                        {
                            await SendCPDLCMessage("NONE", "poll", "", true, AcarsRoute.Hoppie);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("SayIntentions poll iteration failed: " + ex.Message);
                }

                try
                {
                    await Task.Delay(SayIntentionsPollInterval, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
            Logger.Debug("SayIntentions poll loop stopped");
        }

        internal bool IsSayIntentionsDatalinkActive =>
            ActiveAtcNetwork == Vns430AtcNetwork.SayIntentions;

        // Where the PDC goes: the PDC VIA setting wins, AUTO follows the ATC network.
        // SI hands flights off to VATSIM controllers on their end, so a pilot can be on
        // both at once and legitimately want the clearance from either side.
        internal bool PdcRoutesToSayIntentions => SavedPdcVia switch
        {
            "SI" => true,
            "VATSIM" => false,
            _ => IsSayIntentionsDatalinkActive
        };

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
                WriteMessage("SET SIMBRIEF ID FOR SI DATALINK", "SYSTEM", "SYSTEM");
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
                WriteMessage("COULD NOT LOAD SIMBRIEF PLAN FOR SI", "SYSTEM", "SYSTEM");
                return false;
            }
        }

        /// <summary>
        /// Recognises a SayIntentions CPDLC session from inbound traffic.
        /// </summary>
        /// <remarks>
        /// SI's ATSU does not always answer REQUEST LOGON with a well-formed
        /// /data2/ LOGON ACCEPTED packet the CPDLC parser recognises - the confirmation
        /// can arrive late (observed: only after a PDC request) or as plain telex text.
        /// So: any message from PKGM that says LOGON ACCEPTED logs us on, and while a
        /// logon to PKGM is pending, ANY reply from PKGM counts - their ATSU answering
        /// at all means the session exists on their side.
        /// </remarks>
        internal void MaybeAcceptSayIntentionsLogon(string sender, string payload)
        {
            if (!string.Equals((sender ?? string.Empty).Trim(), DatalinkRouting.SayIntentionsAtsu, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(CurrentATCUnit))
            {
                return;
            }

            bool explicitAccept = (payload ?? string.Empty).IndexOf("LOGON ACCEPTED", StringComparison.OrdinalIgnoreCase) >= 0;
            bool implicitAccept = string.Equals((pendingLogon ?? string.Empty).Trim(), DatalinkRouting.SayIntentionsAtsu, StringComparison.OrdinalIgnoreCase);
            if (explicitAccept || implicitAccept)
            {
                CurrentATCUnit = DatalinkRouting.SayIntentionsAtsu;
                pendingLogon = null;
                ClearNextAtcUnitDisplay();
                Logger.Debug("SI CPDLC session recognised (" + (explicitAccept ? "explicit" : "implicit") + ")");
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
