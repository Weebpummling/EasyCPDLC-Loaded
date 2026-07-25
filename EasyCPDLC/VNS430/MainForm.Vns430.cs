using EasyCPDLC.VNS430;
using System;
using System.Collections.Generic;
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
            EnsureVns430Panel();
            vns430Panel.SetScreenOnlyMode(enabled);
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
                        Type = (message.type ?? string.Empty).Trim().ToUpperInvariant(),
                        Station = (message.recipient ?? string.Empty).Trim().ToUpperInvariant(),
                        Text = (message.message ?? string.Empty).Trim(),
                        Outbound = message.outbound,
                        Acknowledged = message.acknowledged,
                        Unread = unreadMessages.Contains(message),
                        Responses = GetVns430Responses(message)
                    })
                    .ToList();

            return new Vns430BackendSnapshot
            {
                Connected = Connected,
                Callsign = (callsign ?? string.Empty).Trim().ToUpperInvariant(),
                CurrentAtcUnit = (CurrentATCUnit ?? string.Empty).Trim().ToUpperInvariant(),
                PendingLogon = (pendingLogon ?? string.Empty).Trim().ToUpperInvariant(),
                Departure = AirbusAocDeparture(),
                Arrival = AirbusAocArrival(),
                Aircraft = AirbusAocAircraft(),
                Messages = messages
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

        internal async Task Vns430RequestLogonAsync(string station)
        {
            string cleanStation = (station ?? string.Empty).Trim().ToUpperInvariant();
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

            if (!Connected)
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
                    case Vns430WorkflowKind.AocPreDeparture:
                        await SendCPDLCMessage(recipient, "TELEX", message);
                        break;

                    case Vns430WorkflowKind.AocOceanic:
                        await SendCPDLCMessage(recipient, "CPDLC", message);
                        break;

                    case Vns430WorkflowKind.AocMetar:
                        recipient = workflow.Value("STATION");
                        WriteMessage("METAR REQUEST", "METAR", recipient, true);
                        ArtificialDelay("METAR " + recipient, "INFOREQ", "REQUEST");
                        break;

                    case Vns430WorkflowKind.AocAtis:
                        string station = workflow.Value("STATION");
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

        internal async Task<Vns430LoadControlSession> Vns430PrepareLoadControlAsync()
        {
            string key = SavedELoadControlApiKey;
            string simbriefUser = SimbriefID;
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("SAVE ELOADCONTROL API KEY IN SETUP FIRST.");
            }
            if (string.IsNullOrWhiteSpace(simbriefUser))
            {
                throw new InvalidOperationException("SAVE SIMBRIEF USER IN SETUP FIRST.");
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
                throw new InvalidOperationException("ELOADCONTROL HAS NO USABLE AIRCRAFT MATCH.");
            }
            if (reference.Formats.Count == 0)
            {
                throw new InvalidOperationException("ELOADCONTROL RETURNED NO LOADSHEET FORMATS.");
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
                ELoadLoadsheetResult result = await new ELoadControlClient()
                    .GenerateLoadsheetAsync(SavedELoadControlApiKey, request, CancellationToken.None);
                Vns430LoadEditionByFlight[session.Flight.FlightKey] = Math.Max(edition, result.EditionNumber);
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
