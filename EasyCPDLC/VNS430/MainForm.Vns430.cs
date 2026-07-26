using EasyCPDLC.VNS430;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EasyCPDLC
{
    public partial class MainForm
    {
        private Vns430Form vns430Panel;
        private static readonly Dictionary<string, int> Vns430LoadEditionByFlight = new(StringComparer.OrdinalIgnoreCase);

        internal void ShowVns430Panel()
        {
            EnsureVns430Panel();

            if (!vns430Panel.Visible)
            {
                vns430Panel.Show();
            }

            vns430Panel.BringToFront();
        }

        private void EnsureVns430Panel()
        {
            if (vns430Panel == null || vns430Panel.IsDisposed)
            {
                vns430Panel = new Vns430Form(this);
            }
        }

        internal bool SetDcduCompanionMode(bool enabled, out string error)
        {
            EnsureVns430Panel();
            _ = vns430Panel.Handle;
            return vns430Panel.SetDcduCompanionMode(enabled, out error);
        }

        internal bool IsDcduCompanionModeEnabled()
        {
            if (vns430Panel != null && !vns430Panel.IsDisposed)
            {
                return vns430Panel.DcduCompanionMode;
            }

            return Vns430Preferences.Load().DcduCompanionMode;
        }

        internal void SetVns430ScreenOnlyMode(bool enabled)
        {
            // Do not instantiate the GNS430 just to record the preference: the artwork
            // toggle also routes here, and creating the form starts its refresh timer even
            // though the window is never shown. Apply live if the panel exists; otherwise
            // persist the choice for when it is opened.
            if (vns430Panel != null && !vns430Panel.IsDisposed)
            {
                vns430Panel.SetScreenOnlyMode(enabled);
                return;
            }

            Vns430Preferences preferences = Vns430Preferences.Load();
            if (preferences.ScreenOnlyMode != enabled)
            {
                preferences.ScreenOnlyMode = enabled;
                preferences.Save(new Rectangle(preferences.Left, preferences.Top, preferences.Width, preferences.Height));
            }
        }

        // Whether the MSFS WASM module is currently connected. The companion host runs in
        // the GNS430 panel instance even when it is hidden, so this works regardless of the
        // active instrument.
        internal bool IsCompanionModuleConnected() =>
            vns430Panel != null && !vns430Panel.IsDisposed && vns430Panel.CompanionModuleActive;

        // ---- GNS430 config parity with the CDU SETUP -----------------------

        internal void Vns430ClearAllMessages() => DeleteAllElement(this, EventArgs.Empty);

        // PDC / pre-departure clearance, shared with the CDU's REQ CLR.
        internal bool Vns430CanRequestClearance() => CanQuickRequestClearance();

        internal void Vns430RequestClearance() => _ = QuickRequestPredepClearanceAsync();

        internal string Vns430AtcNetworkLabel() =>
            ActiveAtcNetwork == Vns430AtcNetwork.SayIntentions ? "SI" : "VATSIM";

        internal string Vns430CycleAtcNetwork()
        {
            ActiveAtcNetwork = ActiveAtcNetwork == Vns430AtcNetwork.Vatsim
                ? Vns430AtcNetwork.SayIntentions
                : Vns430AtcNetwork.Vatsim;
            Properties.Settings.Default.Save();
            SyncSayIntentionsPolling();
            UpdateOnlineStatusLabel();
            return Vns430AtcNetworkLabel();
        }

        // Cruise memory fed by SimConnect telemetry (via the GNS430 panel's companion
        // link), so phase-driven behaviour works without a VATSIM position feed.
        private readonly SimPhaseTracker simPhase = new();

        internal void UpdateSimFlightPhase(double altitudeFt, bool onGround) =>
            simPhase.Update(altitudeFt, onGround);

        internal string Vns430PdcViaLabel() => SavedPdcVia;

        internal string Vns430SimbriefPlanLabel() =>
            SimbriefPlanLoaded ? "RELOAD SIMBRIEF FP: " + SimbriefPlanRoute : "LOAD SIMBRIEF FP";

        internal Task Vns430LoadSimbriefPlanAsync() => LoadSimbriefFlightPlanAsync();

        // AUTO -> SI -> VATSIM -> AUTO, mirroring the CDU SETUP cycle.
        internal string Vns430CyclePdcVia()
        {
            SavedPdcVia = SavedPdcVia switch
            {
                "AUTO" => "SI",
                "SI" => "VATSIM",
                _ => "AUTO"
            };
            Properties.Settings.Default.Save();
            SyncSayIntentionsPolling();
            UpdateOnlineStatusLabel();
            return SavedPdcVia;
        }

        // AUTO (follow network) label, or the explicit override source.
        internal string Vns430WxSourceLabel()
        {
            string over = SavedWxSourceOverride;
            return string.IsNullOrWhiteSpace(over)
                ? "AUTO"
                : Vns430WeatherClient.SourceLabel(Vns430WeatherClient.ParseSource(over));
        }

        internal string Vns430CycleWxSource()
        {
            SavedWxSourceOverride = SavedWxSourceOverride switch
            {
                "" or null => "VATSIM",
                "VATSIM" => "REAL WORLD",
                "REAL WORLD" => "SAYINTENTIONS",
                _ => string.Empty
            };
            Properties.Settings.Default.Save();
            return Vns430WxSourceLabel();
        }

        internal bool IsVns430ScreenOnlyMode()
        {
            if (vns430Panel != null && !vns430Panel.IsDisposed)
            {
                return vns430Panel.ScreenOnlyMode;
            }

            return Vns430Preferences.Load().ScreenOnlyMode;
        }

        private void RestoreVns430CompanionHost()
        {
            Vns430Preferences preferences = Vns430Preferences.Load();
            if (!preferences.CompanionModuleEnabled && !preferences.DcduCompanionMode)
            {
                return;
            }

            EnsureVns430Panel();
            _ = vns430Panel.Handle;
            if (preferences.DcduCompanionMode)
            {
                vns430Panel.SetDcduCompanionMode(true, out _);
            }
        }

        internal void HandleDcduCompanionCommand(Vns430Command command)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => HandleDcduCompanionCommand(command)));
                return;
            }

            // LSK-only CDU mode owns the whole screen: route its twelve LSKs to the grid
            // page tree and ignore the (hidden) Airbus/Boeing bezel commands.
            if (IsCduModeActive())
            {
                if (command >= Vns430Command.DcduLeftLsk1 && command <= Vns430Command.DcduRightLsk6)
                {
                    bool cduRight = command >= Vns430Command.DcduRightLsk1;
                    int cduIndex = cduRight
                        ? (int)command - (int)Vns430Command.DcduRightLsk1 + 1
                        : (int)command - (int)Vns430Command.DcduLeftLsk1 + 1;
                    HandleCduLineSelect(cduRight, cduIndex);
                }
                else if (EasyCPDLC.VNS430.Cdu.CduKeyMap.IsCduKeypadCommand(command))
                {
                    HandleCduKey(command);
                }
                else if (command == Vns430Command.DcduHide)
                {
                    Hide();
                }
                return;
            }

            if (command >= Vns430Command.DcduLeftLsk1 && command <= Vns430Command.DcduRightLsk6)
            {
                bool rightSide = command >= Vns430Command.DcduRightLsk1;
                int index = rightSide
                    ? (int)command - (int)Vns430Command.DcduRightLsk1 + 1
                    : (int)command - (int)Vns430Command.DcduLeftLsk1 + 1;

                if (IsEmbeddedSetupActive() && IsEmbeddedSetupLineSelectActionAvailable(rightSide, index))
                {
                    HandleEmbeddedSetupLineSelect(rightSide, index);
                }
                else if (previewMessage != null)
                {
                    HandleStyledMessagePreviewLineSelect(rightSide, index);
                }
                else if (IsAirbusAocActive() && index <= 5 && IsAirbusAocLineSelectActionAvailable(rightSide, index))
                {
                    HandleAirbusAocLineSelect(rightSide, index);
                }
                else if (boeingTelexPage != BoeingTelexPage.None && IsBoeingTelexLineSelectActionAvailable(rightSide, index))
                {
                    HandleBoeingTelexLineSelect(rightSide, index);
                }

                return;
            }

            switch (command)
            {
                case Vns430Command.DcduConnect:
                    RetrieveButton_Click(retrieveButton, EventArgs.Empty);
                    break;
                case Vns430Command.DcduAoc:
                    TelexButton_Click(telexButton, EventArgs.Empty);
                    break;
                case Vns430Command.DcduAtc:
                    RequestButton_Click(atcButton, EventArgs.Empty);
                    break;
                case Vns430Command.DcduSettings:
                    SettingsButton_Click(settingsButton, EventArgs.Empty);
                    break;
                case Vns430Command.DcduReloadFlightPlan:
                    ReloadFlightPlanButton_Click(mainReloadFlightPlanButton, EventArgs.Empty);
                    break;
                case Vns430Command.DcduPrint:
                    PrintButton_Click(refreshButtonVisual, EventArgs.Empty);
                    break;
                case Vns430Command.DcduReprint:
                    ReprintButton_Click(boeingReprintButton, EventArgs.Empty);
                    break;
                case Vns430Command.DcduHide:
                    Hide();
                    break;
            }
        }

        // Per-message cache for the content-based loadsheet classification. The snapshot is
        // rebuilt on every CDU/GNS430 refresh tick and IsELoadControlLoadsheet runs regexes
        // over the whole message body; a message's text never changes after it is written,
        // so classify each one once. Entries are dropped with their message controls.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CPDLCMessage, object> loadsheetClassCache = new();

        private static bool IsLoadsheetCached(CPDLCMessage message)
        {
            if (loadsheetClassCache.TryGetValue(message, out object cached))
            {
                return (bool)cached;
            }

            bool result = DatalinkPrinter.IsELoadControlLoadsheet(message);
            loadsheetClassCache.AddOrUpdate(message, result);
            return result;
        }

        internal Vns430BackendSnapshot GetVns430Snapshot()
        {
            List<Vns430MessageSnapshot> messages = outputTable == null || outputTable.IsDisposed
                ? new List<Vns430MessageSnapshot>()
                : outputTable.Controls
                    .OfType<CPDLCMessage>()
                    .Reverse()
                    .Take(100)
                    .Select(message => new Vns430MessageSnapshot
                    {
                        Source = message,
                        // Tag loadsheets by content so an inbound VA/eLoadControl loadsheet
                        // (which arrives as a plain TELEX over Hoppie) shows as LOADSHEET in
                        // the CDU and GNS430 lists rather than TELEX.
                        Type = IsLoadsheetCached(message)
                            ? "LOADSHEET"
                            : (message.type ?? string.Empty).Trim().ToUpperInvariant(),
                        Station = (message.recipient ?? string.Empty).Trim().ToUpperInvariant(),
                        Text = (message.message ?? string.Empty).Trim(),
                        Outbound = message.outbound,
                        Acknowledged = message.acknowledged,
                        Unread = unreadMessages.Contains(message),
                        Responses = GetVns430Responses(message)
                    })
                    .ToList();

            string currentUnit = (CurrentATCUnit ?? string.Empty).Trim().ToUpperInvariant();

            // Project the backend's live CPDLC/PDC discovery (refreshed by the 15 s
            // VATSIM + Hoppie loop) so the panel and CDU can show who is online and
            // offer a logon without re-implementing any of the discovery logic.
            bool siNetworkActive = IsSayIntentionsDatalinkActive;
            List<Vns430CpdlcCandidate> candidates = cpdlcDiscoveryCandidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Code))
                .Select(candidate => new Vns430CpdlcCandidate
                {
                    Code = candidate.Code,
                    Controller = candidate.Controller,
                    Frequency = candidate.FrequencyText,
                    TunedMatch = candidate.IsTunedFrequencyMatch
                })
                .ToList();

            // The region ATSU for where the aircraft actually is: the departure region
            // until cruise, the destination region after - the same phase marker the
            // weather prefill uses. This is what a pilot logs on to at flight start,
            // before any controller has been matched to the flight.
            bool simbriefBacked = siNetworkActive || PdcRoutesToSayIntentions;
            string departureIcao = simbriefBacked ? SayIntentionsDeparture() : AirbusAocDeparture();
            string arrivalIcao = simbriefBacked ? SayIntentionsArrival() : AirbusAocArrival();
            bool enroute = flightPhaseEnrouteSeen || simPhase.ReachedCruise;
            IReadOnlyList<CpdlcAtsuDirectory.Atsu> regional = CpdlcAtsuDirectory.SuggestFor(
                enroute ? arrivalIcao : departureIcao,
                enroute ? departureIcao : arrivalIcao);
            CpdlcAtsuDirectory.Atsu here = regional.FirstOrDefault();

            // Ordered so the two networks are unmistakable and always in the same
            // place: the VATSIM station first, then SayIntentions, then the rest.
            // Each row carries its own route, so the same regional code can be offered
            // on both sides - SI accepts the regional and local codes too.
            List<Vns430CpdlcCandidate> ordered = new();

            Vns430CpdlcCandidate vatsimBest = candidates.FirstOrDefault();
            if (vatsimBest != null)
            {
                ordered.Add(Route(vatsimBest, AcarsRoute.Hoppie));
            }
            else if (here != null)
            {
                ordered.Add(Route(new Vns430CpdlcCandidate
                {
                    Code = here.Code,
                    Controller = here.Name
                }, AcarsRoute.Hoppie));
            }

            // SayIntentions: the regional unit where the aircraft is when we know it,
            // otherwise their always-on ATSU.
            ordered.Add(Route(new Vns430CpdlcCandidate
            {
                Code = here?.Code ?? DatalinkRouting.SayIntentionsAtsu,
                Controller = here?.Name ?? "ATC"
            }, AcarsRoute.SayIntentions));

            // Remaining discovered stations, then remaining regional suggestions.
            foreach (Vns430CpdlcCandidate discovered in candidates.Skip(vatsimBest == null ? 0 : 1))
            {
                Add(ordered, Route(discovered, AcarsRoute.Hoppie));
            }
            foreach (CpdlcAtsuDirectory.Atsu unit in regional)
            {
                Add(ordered, Route(new Vns430CpdlcCandidate
                {
                    Code = unit.Code,
                    Controller = unit.Name
                }, AcarsRoute.Hoppie));
            }

            candidates = ordered.Take(4).ToList();

            // Caption each row with the network it will actually leave on, so a row can
            // never be mistaken for the other side's.
            static Vns430CpdlcCandidate Route(Vns430CpdlcCandidate candidate, AcarsRoute route) =>
                new()
                {
                    Code = candidate.Code,
                    Controller = candidate.Controller,
                    Frequency = candidate.Frequency,
                    TunedMatch = candidate.TunedMatch,
                    Route = route,
                    Reason = route == AcarsRoute.SayIntentions ? "VIA SI" : "VIA VATSIM"
                };

            // Same code on the same network only once; the same code on the other
            // network is a legitimately different row.
            static void Add(List<Vns430CpdlcCandidate> list, Vns430CpdlcCandidate candidate)
            {
                if (list.Count < 4 &&
                    !list.Any(existing => existing.Route == candidate.Route &&
                        string.Equals(existing.Code, candidate.Code, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(candidate);
                }
            }

            string pdcStatus = (datalinkStatusText ?? string.Empty)
                .Replace("PDC", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

            bool siMode = IsSayIntentionsDatalinkActive;
            return new Vns430BackendSnapshot
            {
                // To the instruments, Connected means "the datalink is usable". On SI
                // that is true as soon as the prerequisites are met - there is no
                // session to establish, the ATSU is always on.
                Connected = Connected || (siMode && SayIntentionsDatalinkPrerequisitesMet),
                VatsimConnected = Connected,
                Callsign = (callsign ?? string.Empty).Trim().ToUpperInvariant(),
                CurrentAtcUnit = currentUnit,
                PendingLogon = (pendingLogon ?? string.Empty).Trim().ToUpperInvariant(),
                // Flight data uses the SimBrief-backed helpers whenever any SI datalink
                // path is in play (SI network, or just the PDC routed there); the
                // helpers still prefer live VATSIM data when connected.
                Departure = siMode || PdcRoutesToSayIntentions ? SayIntentionsDeparture() : AirbusAocDeparture(),
                Arrival = siMode || PdcRoutesToSayIntentions ? SayIntentionsArrival() : AirbusAocArrival(),
                Aircraft = siMode || PdcRoutesToSayIntentions ? SayIntentionsAircraft() : AirbusAocAircraft(),
                // Either phase source can flip the prefill: the VATSIM engine when
                // connected, the SimConnect telemetry tracker otherwise (or both).
                PreferArrivalStation = flightPhaseEnrouteSeen || simPhase.ReachedCruise,
                SayIntentionsNetwork = siMode,
                AtcUnitViaSayIntentions = currentUnit.Length > 0 && DatalinkRouting.RoutesToSayIntentions(
                    AcarsRoute.Auto, "CPDLC", currentUnit, siMode),
                SimbriefIdent = SimbriefIdent,
                Messages = messages,
                AtcUnitOnline = currentUnit.Length > 0 &&
                    (siMode
                        ? string.Equals(currentUnit, DatalinkRouting.SayIntentionsAtsu, StringComparison.OrdinalIgnoreCase) || (Connected && IsHoppieLogonOnline(currentUnit))
                        : Connected && IsHoppieLogonOnline(currentUnit)),
                CpdlcCandidates = candidates,
                PdcStatus = pdcStatus,
                PdcLogonCode = pdcDiscoveryLogonCode ?? string.Empty,
                PdcController = pdcDiscoveryController ?? string.Empty,
                PdcAllowReqClr = pdcDiscoveryAllowReqClr
            };
        }

        private static IReadOnlyList<string> GetVns430Responses(CPDLCMessage message)
        {
            if (message == null || message.outbound || message.acknowledged ||
                !string.Equals(message.type, "CPDLC", StringComparison.OrdinalIgnoreCase))
            {
                return new string[0];
            }

            return (message.header?.Responses ?? string.Empty).Trim().ToUpperInvariant() switch
            {
                "WU" => new[] { "WILCO", "UNABLE", "STANDBY" },
                "AN" => new[] { "AFFIRMATIVE", "NEGATIVE", "STANDBY" },
                "R" => new[] { "ROGER", "STANDBY" },
                _ => new string[0]
            };
        }

        internal void Vns430ToggleVatsimConnection()
        {
            RetrieveButton_Click(retrieveButton, EventArgs.Empty);
        }

        internal async Task Vns430RequestLogonAsync(string station, AcarsRoute route = AcarsRoute.Auto)
        {
            string cleanStation = (station ?? string.Empty).Trim().ToUpperInvariant();

            // An SI-routed logon follows the SI rules whatever the active network is:
            // the pilot picked the SayIntentions row deliberately.
            if (route == AcarsRoute.SayIntentions || IsSayIntentionsDatalinkActive)
            {
                if (!SayIntentionsDatalinkPrerequisitesMet)
                {
                    WriteMessage("CPDLC LOGON NOT READY: SET SI KEY AND SIMBRIEF ID", "SYSTEM", "SYSTEM");
                    return;
                }
                if (!await EnsureSayIntentionsFlightAsync())
                {
                    return;
                }
                if (string.IsNullOrWhiteSpace(cleanStation))
                {
                    cleanStation = DatalinkRouting.SayIntentionsAtsu;
                }

                await SendCpdlcLogonRequestAsync(cleanStation, false);
                return;
            }

            if (!Connected)
            {
                WriteMessage("CPDLC LOGON NOT READY: CONNECT TO VATSIM FIRST", "SYSTEM", "SYSTEM");
                return;
            }

            await SendCpdlcLogonRequestAsync(cleanStation, false);
        }

        internal void Vns430Reply(Vns430MessageSnapshot message, string response)
        {
            if (message?.Source == null || message.Source.IsDisposed || string.IsNullOrWhiteSpace(response))
            {
                return;
            }

            MarkMessageRead(message.Source);
            ReplyMessage(EventArgs.Empty, message.Source, response.Trim().ToUpperInvariant());
        }

        internal void Vns430MarkRead(Vns430MessageSnapshot message)
        {
            if (message?.Source != null && !message.Source.IsDisposed)
            {
                MarkMessageRead(message.Source);
            }
        }

        internal async Task<Vns430OperationResult> Vns430SendWorkflowAsync(
            Vns430Workflow workflow,
            Vns430BackendSnapshot snapshot)
        {
            if (workflow == null)
            {
                return new Vns430OperationResult { Status = "NO REQUEST SELECTED" };
            }

            string error = workflow.ValidationError();
            if (!string.IsNullOrWhiteSpace(error))
            {
                return new Vns430OperationResult { Status = error };
            }

            // REAL WORLD / SAYINTENTIONS weather is fetched directly over HTTP and does
            // not need a VATSIM datalink connection; only the VATSIM (INFOREQ) path does.
            // The source follows the global SETUP selection (ATC network / WX override).
            Vns430WeatherSource wxSource = EffectiveWxSource();
            bool directWeather =
                (workflow.Kind == Vns430WorkflowKind.AocMetar || workflow.Kind == Vns430WorkflowKind.AocAtis) &&
                wxSource != Vns430WeatherSource.Vatsim;

            // SI datalink: either the whole network is SI, or just this PDC is routed
            // there (PDC VIA = SI on VATSIM). Either way no VATSIM connection is
            // required - the SI prerequisites are.
            bool viaSiDatalink = IsSayIntentionsDatalinkActive ||
                (workflow.Kind == Vns430WorkflowKind.AocPreDeparture && PdcRoutesToSayIntentions);
            if (viaSiDatalink && !directWeather)
            {
                if (!SayIntentionsDatalinkPrerequisitesMet)
                {
                    return new Vns430OperationResult { Status = "SET SI KEY + SIMBRIEF ID" };
                }
                if (!Connected && !await EnsureSayIntentionsFlightAsync())
                {
                    return new Vns430OperationResult { Status = "LOAD SIMBRIEF PLAN FIRST" };
                }
            }
            else if (!Connected && !directWeather)
            {
                return new Vns430OperationResult { Status = "CONNECT VATSIM FIRST" };
            }

            if (workflow.Kind == Vns430WorkflowKind.AocPreDeparture &&
                new[] { snapshot.Callsign, snapshot.Aircraft, snapshot.Departure, snapshot.Arrival }
                    .Any(string.IsNullOrWhiteSpace))
            {
                return new Vns430OperationResult { Status = "LOAD FLIGHT PLAN FOR PDC" };
            }
            if (workflow.Kind == Vns430WorkflowKind.AocOceanic && string.IsNullOrWhiteSpace(snapshot.Callsign))
            {
                return new Vns430OperationResult { Status = "CALLSIGN REQUIRED" };
            }

            string recipient = workflow.Value("RECIPIENT");
            string message = workflow.BuildMessage(snapshot);
            try
            {
                switch (workflow.Kind)
                {
                    case Vns430WorkflowKind.AtcDirect:
                    case Vns430WorkflowKind.AtcLevel:
                    case Vns430WorkflowKind.AtcSpeed:
                    case Vns430WorkflowKind.AtcWhenCanWe:
                    case Vns430WorkflowKind.AtcFreeText:
                        string packet = string.Format("/data2/{0}//Y/{1}", messageOutCounter, message);
                        messageOutCounter += 1;
                        await SendCPDLCMessage(recipient, "CPDLC", packet);
                        break;

                    case Vns430WorkflowKind.AtcPositionReport:
                        // A position report expects no clearance reply, so it is sent
                        // with the "N" response flag, matching the original RequestForm.
                        string reportPacket = string.Format("/data2/{0}//N/{1}", messageOutCounter, message);
                        messageOutCounter += 1;
                        await SendCPDLCMessage(recipient, "CPDLC", reportPacket);
                        break;

                    case Vns430WorkflowKind.AocTelex:
                        // The VIA field picks the ACARS network explicitly; content
                        // routing cannot know which side a free-text telex belongs to.
                        AcarsRoute telexRoute = workflow.Value("VIA") == "SI"
                            ? AcarsRoute.SayIntentions
                            : AcarsRoute.Hoppie;
                        await SendCPDLCMessage(recipient, "TELEX", message, true, telexRoute);
                        break;

                    case Vns430WorkflowKind.AocPreDeparture:
                        await SendCPDLCMessage(recipient, "TELEX", message);
                        break;

                    case Vns430WorkflowKind.AocOceanic:
                        await SendCPDLCMessage(recipient, "CPDLC", message);
                        break;

                    case Vns430WorkflowKind.AocMetar:
                        recipient = workflow.Value("STATION");
                        if (directWeather)
                        {
                            await FetchDirectWeatherAsync(
                                wxSource, Vns430WorkflowKind.AocMetar, recipient, string.Empty).ConfigureAwait(false);
                            break;
                        }
                        WriteMessage("METAR REQUEST", "METAR", recipient, true);
                        ArtificialDelay("METAR " + recipient, "INFOREQ", "REQUEST");
                        break;

                    case Vns430WorkflowKind.AocAtis:
                        string station = workflow.Value("STATION");
                        if (directWeather)
                        {
                            await FetchDirectWeatherAsync(
                                wxSource, Vns430WorkflowKind.AocAtis, station, workflow.Value("TYPE")).ConfigureAwait(false);
                            break;
                        }
                        if (!TryResolveAtisRequestTarget(station, workflow.Value("TYPE"), out recipient, out string warning))
                        {
                            return new Vns430OperationResult { Status = warning };
                        }
                        SetAtisAutoRefresh(recipient, workflow.Value("AUTO") == "ON");
                        WriteMessage("ATIS REQUEST", "ATIS", recipient, true);
                        ArtificialDelay("VATATIS " + recipient, "INFOREQ", "REQUEST");
                        _ = RefreshVatsimForAtisHoverAsync();
                        break;
                }

                return new Vns430OperationResult { Success = true, Status = "REQUEST SENT" };
            }
            catch (Exception ex)
            {
                Logger.Debug("VNS430 request failed: " + ex.Message);
                return new Vns430OperationResult { Status = SafeVns430Error(ex) };
            }
        }

        /// <summary>
        /// Fetches REAL WORLD / SAYINTENTIONS weather over HTTP and writes the reply
        /// straight into the inbox, bypassing the Hoppie datalink. Marshals the
        /// WriteMessage calls back onto the UI thread. Throws on failure so the caller
        /// reports the reason on the CDU status line and lights FAIL.
        /// </summary>
        private async Task FetchDirectWeatherAsync(
            Vns430WeatherSource source,
            Vns430WorkflowKind kind,
            string station,
            string type)
        {
            string label = kind == Vns430WorkflowKind.AocAtis ? "ATIS" : "METAR";
            string cleanStation = (station ?? string.Empty).Trim().ToUpperInvariant();
            string sourceLabel = Vns430WeatherClient.SourceLabel(source);

            void OnUi(Action action)
            {
                if (InvokeRequired)
                {
                    Invoke(action);
                }
                else
                {
                    action();
                }
            }

            // Record the outbound request on the SENT list before the fetch.
            OnUi(() => WriteMessage(label + " REQUEST " + sourceLabel, label, cleanStation, true));

            try
            {
                string apiKey = SavedSayIntentionsApiKey;
                DateTime requestedUtc = DateTime.UtcNow;
                Vns430WeatherClient weather = new();
                string body = kind == Vns430WorkflowKind.AocAtis
                    ? await weather.FetchAtisAsync(source, cleanStation, type, apiKey, CancellationToken.None).ConfigureAwait(false)
                    : await weather.FetchMetarAsync(source, cleanStation, apiKey, CancellationToken.None).ConfigureAwait(false);

                // Same minimum reply latency as the datalink: a weather answer landing
                // the same instant as the request reads as fake.
                TimeSpan elapsed = DateTime.UtcNow - requestedUtc;
                if (elapsed < MinimumReplyLatency)
                {
                    await Task.Delay(MinimumReplyLatency - elapsed).ConfigureAwait(false);
                }

                OnUi(() => WriteMessage(body, label, cleanStation, false));
            }
            catch (Exception ex)
            {
                string reason = ex is Vns430WeatherException ? ex.Message : SafeVns430Error(ex);
                throw new InvalidOperationException(reason);
            }
        }

        internal async Task<Vns430LoadControlSession> Vns430PrepareLoadControlAsync()
        {
            string key = SavedELoadControlApiKey;
            string simbriefUser = SimbriefID;
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("SET ELOAD KEY FIRST");
            }
            if (string.IsNullOrWhiteSpace(simbriefUser))
            {
                throw new InvalidOperationException("SET SIMBRIEF USER FIRST");
            }

            SimbriefLoadsheetData flight = await new SimbriefLoadsheetClient()
                .FetchAsync(simbriefUser, callsign ?? string.Empty, CancellationToken.None);
            ELoadReferenceData reference = await new ELoadControlClient()
                .GetReferenceDataAsync(key, flight.AircraftIcao, CancellationToken.None);

            List<ELoadAircraft> usableAircraft = reference.Aircraft
                .Where(item => item.CabinConfigurations.Count > 0)
                .ToList();
            if (usableAircraft.Count == 0)
            {
                throw new InvalidOperationException("NO AIRCRAFT MATCH");
            }
            if (reference.Formats.Count == 0)
            {
                throw new InvalidOperationException("NO LOADSHEET FORMATS");
            }

            ELoadReferenceData usable = new() { Aircraft = usableAircraft, Formats = reference.Formats };
            int aircraftIndex = usableAircraft.FindIndex(item =>
                string.Equals(item.Icao, flight.AircraftIcao, StringComparison.OrdinalIgnoreCase));
            Vns430LoadControlSession session = new()
            {
                Flight = flight,
                Reference = usable,
                AircraftIndex = Math.Max(0, aircraftIndex)
            };
            session.RebuildPassengerSplit();
            return session;
        }

        internal async Task<Vns430OperationResult> Vns430GenerateLoadsheetAsync(Vns430LoadControlSession session)
        {
            if (session?.Flight == null)
            {
                return new Vns430OperationResult { Status = "LOAD DATA NOT READY" };
            }
            if (session.PassengerSplit.Sum(item => item.Passengers) != session.Flight.PassengerCount)
            {
                return new Vns430OperationResult { Status = "PAX TOTAL MUST BE " + session.Flight.PassengerCount };
            }

            try
            {
                int edition = Vns430LoadEditionByFlight.TryGetValue(session.Flight.FlightKey, out int last)
                    ? Math.Max(1, last + 1)
                    : 1;
                Newtonsoft.Json.Linq.JObject request = session.Flight.BuildGenerateRequest(
                    session.Cabin,
                    session.Format.TemplateId,
                    session.PassengerSplit,
                    edition,
                    session.Aircraft.Icao);
                DateTime requestedUtc = DateTime.UtcNow;
                ELoadLoadsheetResult result = await new ELoadControlClient()
                    .GenerateLoadsheetAsync(SavedELoadControlApiKey, request, CancellationToken.None);
                Vns430LoadEditionByFlight[session.Flight.FlightKey] = Math.Max(edition, result.EditionNumber);

                // Simulated loading time: the sheet is generated now but delivered when
                // the ground crew "finishes". Queued on disk rather than held in a
                // Task.Delay, so closing the app cannot swallow it. INSTANT falls
                // through to the immediate path below.
                int loadingMinutes = session.LoadingMinutes;
                if (loadingMinutes > 0)
                {
                    string pendingBody = ELoadLoadsheetUnits.EnsureUnitsLine(
                        string.IsNullOrWhiteSpace(result?.AcarsMessage) ? result?.Loadsheet : result.AcarsMessage);
                    if (string.IsNullOrWhiteSpace(pendingBody))
                    {
                        return new Vns430OperationResult { Status = "NO LOADSHEET RETURNED" };
                    }

                    string pendingCallsign = string.IsNullOrWhiteSpace(callsign)
                        ? ((session.Flight.Airline ?? string.Empty) + (session.Flight.FlightNumber ?? string.Empty)).Trim()
                        : callsign.Trim();

                    SchedulePendingLoadsheet(pendingBody, pendingCallsign, loadingMinutes);
                    return new Vns430OperationResult { Success = true, Status = "LOADSHEET IN " + loadingMinutes + " MIN" };
                }

                // Same minimum reply latency as the datalink: a loadsheet generated in
                // a few hundred milliseconds reads as fake when it appears instantly.
                TimeSpan elapsed = DateTime.UtcNow - requestedUtc;
                if (elapsed < MinimumReplyLatency)
                {
                    await Task.Delay(MinimumReplyLatency - elapsed);
                }

                ReceiveELoadControlLoadsheet(result, session.Flight, false);
                return new Vns430OperationResult { Success = true, Status = "LOADSHEET RECEIVED" };
            }
            catch (Exception ex)
            {
                Logger.Debug("VNS430 eLoadControl failed: " + ex.Message);
                return new Vns430OperationResult { Status = SafeVns430Error(ex) };
            }
        }

        private static string SafeVns430Error(Exception ex)
        {
            string text = (ex?.Message ?? "REQUEST FAILED").Trim().ToUpperInvariant();
            return text.Length <= 80 ? text : text.Substring(0, 80);
        }

        internal void Vns430OpenAtcRequests()
        {
            BringEasyCpdlcWindowToFront();
            BeginInvoke(new Action(() => RequestButton_Click(atcButton, EventArgs.Empty)));
        }

        internal void Vns430OpenAocTelex()
        {
            BringEasyCpdlcWindowToFront();
            BeginInvoke(new Action(() => TelexButton_Click(telexButton, EventArgs.Empty)));
        }

        internal void Vns430OpenSettings()
        {
            BringEasyCpdlcWindowToFront();
            BeginInvoke(new Action(() => SettingsButton_Click(settingsButton, EventArgs.Empty)));
        }
    }
}
