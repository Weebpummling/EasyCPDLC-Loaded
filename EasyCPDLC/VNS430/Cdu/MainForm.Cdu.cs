using EasyCPDLC.VNS430;
using EasyCPDLC.VNS430.Cdu;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace EasyCPDLC
{
    // LSK-only "CDU" display mode. A third DcduStyle alongside Airbus/Boeing that renders a
    // 24x14 MCDU character grid driven entirely by the twelve line-select keys (clickable on
    // screen and driveable by the MobiFlight EASYCPDLC_DCDU_LSK_* L-vars). It reuses the
    // same backend as the DCDU skins through the existing Vns430* wrappers and leaves the
    // Airbus/Boeing code paths untouched.
    public partial class MainForm
    {
        internal enum CduPageId
        {
            Menu,
            Dlk,
            Logon,
            Messages,
            MessageList,
            MessageDetail,
            Atc,
            Aoc,
            Request,
            Setup,
            SetupAccount,
            SetupPrinter,
            SetupTechnical,
            SetupWinwing,
            Load
        }

        private CduDisplayPanel cduDisplayPanel;
        private System.Windows.Forms.Timer cduRefreshTimer;
        private CduPageId cduPage = CduPageId.Menu;
        private CPDLCMessage cduSelectedMessage;
        private int cduDetailScroll;
        private int cduTechPage;      // SETUP > TECHNICAL current page
        private bool cduMsgSent;      // MessageList filter: false = received, true = sent
        private bool cduStatusError;  // lights the FAIL annunciator while an error is shown
        private readonly List<CPDLCMessage> cduVisibleInbox = new();

        // Request-page (ATC/AOC) state and the shared scratchpad.
        private Vns430Workflow cduWorkflow;
        private string cduScratchpad = string.Empty;
        private string cduStatusLine = string.Empty;
        private bool cduRequestSending;

        // eLoadControl loadsheet state.
        // Unit the message viewer is showing weights in, or null to use the sheet's own.
        // Session-only and cleared on leaving a message: it is a view, not a setting.
        private string cduUnitOverride;

        private Vns430LoadControlSession cduLoadSession;
        private bool cduLoadBusy;

        // Logon page: LSK index -> the candidate, which carries its own network route
        // so the row the pilot picked decides where the logon is sent.
        private readonly List<Vns430CpdlcCandidate> cduLogonCandidates = new();

        // The station a manual logon was armed with. The scratchpad is cleared on
        // arming, so the code is kept here to show on the MANUAL LOGON row.
        private string cduManualLogonCode = string.Empty;

        // EXEC arming: a network-transmitting action (send request, logon, REQ CLR,
        // reply, generate loadsheet) is selected first, which highlights it and lights
        // the EXEC annunciator; pressing EXEC then runs it. Local actions stay immediate.
        private Action cduArmedAction;
        private string cduArmedKey = string.Empty;

        // Lamp test: light every side annunciator so their look can be verified/tuned.
        private bool cduAnnunciatorTest;

        // Live WinWing mirror, when a seat is selected. Null while OFF, which also keeps the
        // paint path from serialising frames nobody consumes.
        //
        // Session-only on purpose: the seat is never persisted, so a fresh launch always
        // starts OFF and can never grab a CDU that is already showing a live aircraft
        // display. The pilot picks the seat each session.
        private WinwingCduSink cduWinwingSink;
        private WinwingSeat cduWinwingSeat = WinwingSeat.Off;

        internal bool IsCduAnnunciatorTest() => cduAnnunciatorTest;

        internal void ToggleCduAnnunciatorTest()
        {
            cduAnnunciatorTest = !cduAnnunciatorTest;
            if (IsCduModeActive())
            {
                RefreshCduDisplay();
            }
        }

        private void CduArm(string key, string prompt, Action action)
        {
            cduArmedKey = key;
            cduArmedAction = action;
            cduStatusLine = prompt + " - EXEC";
        }

        private void ClearCduArm()
        {
            cduArmedAction = null;
            cduArmedKey = string.Empty;
            cduManualLogonCode = string.Empty;
        }

        private bool CduArmed(string key) => cduArmedAction != null && cduArmedKey == key;

        internal bool IsCduModeActive() => DcduStyleManager.IsCdu;

        // Entry point from ApplyDisplayStyle when the CDU style is selected.
        private void MountCduMode()
        {
            if (dcduFrame == null)
            {
                return;
            }

            if (cduDisplayPanel == null || cduDisplayPanel.IsDisposed)
            {
                cduDisplayPanel = new CduDisplayPanel { Dock = DockStyle.Fill };
                cduDisplayPanel.LskPressed += CduDisplayPanel_LskPressed;
                cduDisplayPanel.KeyPressed += (_, cmd) => HandleDcduCompanionCommand(cmd);
                cduDisplayPanel.CharTyped += (_, c) => CduScratchpadType(c);
                cduDisplayPanel.ScratchpadBackspace += (_, __) => CduScratchpadBackspace();
                cduDisplayPanel.ScratchpadClear += (_, __) => CduScratchpadClearAll();
                cduDisplayPanel.DragMoveRequested += (_, __) => BeginWindowDrag();
                cduDisplayPanel.ResizeRequested += (_, ht) => BeginWindowResize(ht);
                dcduFrame.Controls.Add(cduDisplayPanel);
            }

            // Hide every Airbus/Boeing element; the CDU panel is the whole screen.
            SetAirbusAocChromeVisible(false);
            if (screenPanel != null) screenPanel.Visible = false;
            if (messageFormatPanel != null) messageFormatPanel.Visible = false;
            dcduFrame.ShowArtwork = false;
            dcduFrame.BackColor = Color.Black;

            cduDisplayPanel.Visible = true;
            cduDisplayPanel.BringToFront();

            if (cduRefreshTimer == null)
            {
                cduRefreshTimer = new System.Windows.Forms.Timer { Interval = 750 };
                cduRefreshTimer.Tick += (_, __) =>
                {
                    if (IsCduModeActive())
                    {
                        RefreshCduDisplay();
                    }
                };
            }
            cduRefreshTimer.Start();

            cduPage = CduPageId.Menu;
            RefreshCduDisplay();
        }

        // Called from ApplyDisplayStyle's non-CDU branch so switching back to Airbus/Boeing
        // restores the normal chrome.
        private void TeardownCduMode()
        {
            cduRefreshTimer?.Stop();

            // Close the WinWing socket; leaving the CDU means nothing is producing frames.
            // The seat stays saved, so re-entering CDU mode reconnects it.
            if (cduWinwingSink != null)
            {
                cduWinwingSink.Dispose();
                cduWinwingSink = null;
                cduWinwingSeat = WinwingSeat.Off;
                if (cduDisplayPanel != null && !cduDisplayPanel.IsDisposed)
                {
                    cduDisplayPanel.Sink = NullCduDisplaySink.Instance;
                }
            }

            if (cduDisplayPanel != null)
            {
                cduDisplayPanel.Visible = false;
            }
            if (screenPanel != null) screenPanel.Visible = true;
            SetAirbusAocChromeVisible(true);
        }

        private void CduDisplayPanel_LskPressed(object sender, CduLskEventArgs e)
        {
            // Route through the single companion hub so on-screen and hardware LSKs share
            // exactly one path.
            Vns430Command command = e.RightSide
                ? (Vns430Command)((int)Vns430Command.DcduRightLsk1 + (e.Index - 1))
                : (Vns430Command)((int)Vns430Command.DcduLeftLsk1 + (e.Index - 1));
            HandleDcduCompanionCommand(command);
        }

        // Invoked from HandleDcduCompanionCommand's CDU arm.
        private void HandleCduLineSelect(bool rightSide, int index)
        {
            // While armed, the bottom-left LSK is ERASE and does nothing but cancel.
            // It must not fall through to the page handler, or it would also trigger
            // whatever that slot normally does (RETURN, MENU) and leave the page.
            if (cduArmedAction != null && !rightSide && index == CduLayout.LskCount)
            {
                ClearCduArm();
                cduStatusError = false;
                cduStatusLine = "ERASED";
                RefreshCduDisplay();
                return;
            }

            // Any line-select changes the selection, so a previously armed transmit is
            // cancelled; the handler below re-arms if this press is itself a transmit.
            ClearCduArm();
            cduStatusError = false;   // an error clears on the next action; sites below re-set it

            switch (cduPage)
            {
                case CduPageId.Menu:
                    HandleCduMenuLsk(rightSide, index);
                    break;
                case CduPageId.Dlk:
                    HandleCduDlkLsk(rightSide, index);
                    break;
                case CduPageId.Logon:
                    HandleCduLogonLsk(rightSide, index);
                    break;
                case CduPageId.Messages:
                    HandleCduMessagesLsk(rightSide, index);
                    break;
                case CduPageId.MessageList:
                    HandleCduMessageListLsk(rightSide, index);
                    break;
                case CduPageId.MessageDetail:
                    HandleCduMessageDetailLsk(rightSide, index);
                    break;
                case CduPageId.Atc:
                    HandleCduAtcLsk(rightSide, index);
                    break;
                case CduPageId.Aoc:
                    HandleCduAocLsk(rightSide, index);
                    break;
                case CduPageId.Request:
                    HandleCduRequestLsk(rightSide, index);
                    break;
                case CduPageId.Setup:
                    HandleCduSetupLsk(rightSide, index);
                    break;
                case CduPageId.SetupAccount:
                    HandleCduSetupAccountLsk(rightSide, index);
                    break;
                case CduPageId.SetupPrinter:
                    HandleCduSetupPrinterLsk(rightSide, index);
                    break;
                case CduPageId.SetupTechnical:
                    HandleCduSetupTechnicalLsk(rightSide, index);
                    break;
                case CduPageId.SetupWinwing:
                    HandleCduSetupWinwingLsk(rightSide, index);
                    break;
                case CduPageId.Load:
                    HandleCduLoadLsk(rightSide, index);
                    break;
            }

            RefreshCduDisplay();
        }

        // ---- Rendering -----------------------------------------------------

        private void RefreshCduDisplay()
        {
            if (cduDisplayPanel == null || cduDisplayPanel.IsDisposed)
            {
                return;
            }

            Vns430BackendSnapshot snapshot = GetVns430Snapshot();
            CduGrid grid = cduDisplayPanel.Grid;
            grid.Clear();

            switch (cduPage)
            {
                case CduPageId.Menu:
                    RenderCduMenu(grid, snapshot);
                    break;
                case CduPageId.Dlk:
                    RenderCduDlk(grid, snapshot);
                    break;
                case CduPageId.Logon:
                    RenderCduLogon(grid, snapshot);
                    break;
                case CduPageId.Messages:
                    RenderCduMessages(grid, snapshot);
                    break;
                case CduPageId.MessageList:
                    RenderCduMessageList(grid, snapshot);
                    break;
                case CduPageId.MessageDetail:
                    RenderCduMessageDetail(grid, snapshot);
                    break;
                case CduPageId.Atc:
                    RenderCduRequestMenu(grid, snapshot, "ATC REQUESTS", CduAtcMenuItems);
                    grid.WriteRight(CduLayout.DataRow(1), "POS REP>", CduColor.White);
                    break;
                case CduPageId.Aoc:
                    RenderCduRequestMenu(grid, snapshot, "AOC / TELEX", CduAocMenuItems);
                    RenderCduAocCompany(grid, snapshot);
                    break;
                case CduPageId.Request:
                    RenderCduRequest(grid, snapshot);
                    break;
                case CduPageId.Setup:
                    RenderCduSetup(grid, snapshot);
                    break;
                case CduPageId.SetupAccount:
                    RenderCduSetupAccount(grid, snapshot);
                    break;
                case CduPageId.SetupPrinter:
                    RenderCduSetupPrinter(grid, snapshot);
                    break;
                case CduPageId.SetupTechnical:
                    RenderCduSetupTechnical(grid, snapshot);
                    break;
                case CduPageId.SetupWinwing:
                    RenderCduSetupWinwing(grid, snapshot);
                    break;
                case CduPageId.Load:
                    RenderCduLoad(grid, snapshot);
                    break;
            }

            // While something is armed, the bottom-left LSK is always ERASE - it
            // replaces whatever that slot would otherwise be, so there is one
            // consistent way out of an armed action on every page.
            if (cduArmedAction != null)
            {
                // Padded across the half row: this overwrites whatever the page put
                // there, and "<ERASE" alone is shorter than "<RETURN", so the tail of
                // the old label was left behind reading "<ERASEN".
                grid.WriteLeft(CduLayout.DataRow(CduLayout.LskCount),
                    "<ERASE?".PadRight(CduGrid.HalfCols), CduColor.Amber);
            }

            cduDisplayPanel.ExecArmed = cduArmedAction != null;
            cduDisplayPanel.LitAnnunciators = CduLitAnnunciators(snapshot);
            cduDisplayPanel.RefreshDisplay();
        }

        // Which side annunciators are lit. A lamp test lights all four; otherwise they
        // follow real state (more drivers arrive when the bridge L-vars are wired).
        private List<string> CduLitAnnunciators(Vns430BackendSnapshot snapshot)
        {
            if (cduAnnunciatorTest)
            {
                return new List<string> { "CALL", "FAIL", "MSG", "OFST" };
            }

            List<string> lit = new();
            if (cduStatusError)
            {
                lit.Add("FAIL");
            }
            if (snapshot.Messages.Any(m => m.Unread && !m.Outbound))
            {
                lit.Add("MSG");
            }
            return lit;
        }

        // The CDU lamp state packed into companion status flags, so a hardware CDU can
        // mirror the on-screen annunciators and EXEC light through the MSFS bridge.
        internal uint CduCompanionStatusFlags(Vns430BackendSnapshot snapshot)
        {
            uint flags = 0;
            foreach (string name in CduLitAnnunciators(snapshot))
            {
                switch (name)
                {
                    case "CALL": flags |= Vns430CompanionProtocol.StatusAnnCall; break;
                    case "FAIL": flags |= Vns430CompanionProtocol.StatusAnnFail; break;
                    case "MSG": flags |= Vns430CompanionProtocol.StatusAnnMsg; break;
                    case "OFST": flags |= Vns430CompanionProtocol.StatusAnnOfst; break;
                }
            }

            if (cduArmedAction != null)
            {
                flags |= Vns430CompanionProtocol.StatusExecLight;
            }
            return flags;
        }

        // Shared title row: page name centred, callsign at far left, link state at far right.
        private static void RenderCduHeader(CduGrid grid, string title, Vns430BackendSnapshot snapshot)
        {
            // The callsign owns cols 0..6 and the link state cols 20..23, so the title
            // centres inside the free band (cols 8..19). Centering it across the whole
            // row let a 7-character callsign overwrite the first letter of long titles.
            const int bandStart = 8;
            const int bandWidth = 12;
            string fitted = Truncate(title ?? string.Empty, bandWidth);
            grid.Write(CduLayout.TitleRow, bandStart + (bandWidth - fitted.Length) / 2, fitted, CduColor.White);

            // Identity comes from the loaded SimBrief plan only - callsign, or the
            // registration when the plan has none - and stays blank until one is
            // loaded, so a callsign never lingers from a previous flight.
            if (!string.IsNullOrWhiteSpace(snapshot.SimbriefIdent))
            {
                grid.Write(CduLayout.TitleRow, 0, Truncate(snapshot.SimbriefIdent, 7), CduColor.Green, small: true);
            }
            grid.WriteRight(CduLayout.TitleRow, snapshot.Connected ? "CONN" : "OFFL",
                snapshot.Connected ? CduColor.Green : CduColor.Amber, small: true);
        }

        private void RenderCduMenu(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            RenderCduHeader(grid, "MCDU MENU", snapshot);

            // Each item carries a small caption above it; the right column is intentionally
            // unused on the menu.
            grid.WriteLeft(CduLayout.LabelRow(1), "STATUS", CduColor.Cyan, small: true);
            grid.WriteLeft(CduLayout.DataRow(1), "<DLK", CduColor.White);
            grid.WriteLeft(CduLayout.LabelRow(2), "REQUESTS", CduColor.Cyan, small: true);
            grid.WriteLeft(CduLayout.DataRow(2), "<ATC", CduColor.White);
            grid.WriteLeft(CduLayout.LabelRow(3), "TELEX/WX", CduColor.Cyan, small: true);
            grid.WriteLeft(CduLayout.DataRow(3), "<AOC", CduColor.White);
            int unread = snapshot.Messages.Count(m => m.Unread && !m.Outbound);
            grid.WriteLeft(CduLayout.LabelRow(4), unread > 0 ? unread + " UNREAD" : "INBOX",
                unread > 0 ? CduColor.Amber : CduColor.Cyan, small: true);
            grid.WriteLeft(CduLayout.DataRow(4), "<MSG", unread > 0 ? CduColor.Amber : CduColor.White, inverse: unread > 0);

            // SETUP is highlighted (like MSG) when a required credential for the active
            // network is missing, so the config need is visible from the menu.
            bool setupAttn = CduSetupNeedsAttention();
            grid.WriteLeft(CduLayout.LabelRow(5), setupAttn ? "CHECK SETUP" : "CONFIG",
                setupAttn ? CduColor.Amber : CduColor.Cyan, small: true);
            grid.WriteLeft(CduLayout.DataRow(5), "<SETUP", setupAttn ? CduColor.Amber : CduColor.White, inverse: setupAttn);

            // Pull the current SimBrief OFP and refresh everything derived from it
            // (navlog fixes, SI identity, eLoadControl source). EXEC-armed because it
            // replaces the loaded plan.
            grid.WriteRight(CduLayout.LabelRow(1),
                SimbriefPlanLoaded ? Truncate(SimbriefPlanRoute, CduGrid.HalfCols) : "SIMBRIEF",
                CduColor.Cyan, small: true);
            grid.WriteRight(CduLayout.DataRow(1), SimbriefPlanLoaded ? "RELOAD FP>" : "LOAD FP>",
                CduColor.White, inverse: CduArmed("SBFP"));
        }

        // SETUP wants attention when a credential the pilot needs to connect/operate is
        // missing: SimBrief is always needed, and the active ATC network needs its own
        // credential (VATSIM -> Hoppie, SI -> SayIntentions key). On SI the Hoppie code
        // is optional - it only adds VA telex - so its absence alone must not flag.
        private bool CduSetupNeedsAttention()
        {
            bool simbriefMissing = string.IsNullOrWhiteSpace(SimbriefID);
            // Follows the LOGON VIA selection rather than the retired ATC NETWORK
            // switch, so the credential flagged is the one the pilot's logons need.
            bool networkMissing = PdcRoutesToSayIntentions
                ? string.IsNullOrWhiteSpace(SavedSayIntentionsApiKey)
                : string.IsNullOrWhiteSpace(SavedHoppieCode);
            return simbriefMissing || networkMissing;
        }

        private void RenderCduDlk(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            RenderCduHeader(grid, "DLK STATUS", snapshot);

            // Left column: actions on the LSKs. LOGON leads - it is the action that
            // actually opens a datalink session. CONNECT/DISCONNECT used to sit here,
            // but it only ever toggled the VATSIM client connection, which is neither
            // required on SI nor the thing a pilot comes to this page to do.
            // Printing lives on the pages where a message is actually on screen, so
            // there is nothing here to print FROM. This page is logon and flight plan.
            grid.WriteLeft(CduLayout.DataRow(1), "<LOGON", CduColor.White);
            grid.WriteLeft(CduLayout.DataRow(6), "<MENU", CduColor.White);

            // Right column: live status read-out (the old top-row info, now in the display).
            // ATS UNIT leads: whether a controller is logged on is the state that
            // actually gates sending CPDLC. The old NETWORK row above it reported
            // "connected" for two different things (a VATSIM session, or merely having
            // SI credentials), so it could not be read as one fact - the network is now
            // named alongside the unit instead, where it means something.
            bool loggedOn = !string.IsNullOrWhiteSpace(snapshot.CurrentAtcUnit);
            string unitText = loggedOn
                ? DatalinkRouting.DisplayStation(snapshot.CurrentAtcUnit) +
                    (snapshot.AtcUnitViaSayIntentions ? " VIA SI" : " VIA VATSIM")
                : "----";
            RenderCduRightStatus(grid, 1, "ATS UNIT", unitText,
                loggedOn ? (snapshot.AtcUnitOnline ? CduColor.Green : CduColor.Amber) : CduColor.Grey);
            RenderCduRightStatus(grid, 2, "ROUTE", BuildRouteText(snapshot), CduColor.White);
            RenderCduRightStatus(grid, 3, "LOGON", string.IsNullOrWhiteSpace(snapshot.PendingLogon) ? "----" : DatalinkRouting.DisplayStation(snapshot.PendingLogon), CduColor.Cyan);

            // Flight plan action on the right at LSK 4. "LOAD/REFRESH>" is 13 columns
            // against the 12 a right entry has, so it is placed by column - safe here
            // because nothing occupies the left of this row.
            grid.WriteRight(CduLayout.LabelRow(4), "VATSIM PLAN", CduColor.Cyan, small: true);
            grid.Write(CduLayout.DataRow(4), CduGrid.Cols - 13, "LOAD/REFRESH>", CduColor.White,
                inverse: CduArmed("RELOADFP"));
        }

        private static void RenderCduRightStatus(CduGrid grid, int lsk, string label, string value, CduColor valueColour)
        {
            grid.WriteRight(CduLayout.LabelRow(lsk), label, CduColor.Cyan, small: true);
            grid.WriteRight(CduLayout.DataRow(lsk), Truncate(value, CduGrid.HalfCols), valueColour);
        }

        // MESSAGES is a submenu splitting received traffic from what was sent.
        private void RenderCduMessages(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            RenderCduHeader(grid, "MESSAGES", snapshot);

            int unread = snapshot.Messages.Count(m => m.Unread && !m.Outbound);
            int sent = snapshot.Messages.Count(m => m.Outbound);
            int received = snapshot.Messages.Count(m => !m.Outbound);

            grid.WriteLeft(CduLayout.LabelRow(1), unread > 0 ? unread + " UNREAD" : received + " MSGS", unread > 0 ? CduColor.Amber : CduColor.Cyan, small: true);
            grid.WriteLeft(CduLayout.DataRow(1), "<RECEIVED", unread > 0 ? CduColor.Amber : CduColor.White);
            grid.WriteLeft(CduLayout.LabelRow(2), sent + " MSGS", CduColor.Cyan, small: true);
            grid.WriteLeft(CduLayout.DataRow(2), "<SENT", CduColor.White);

            // CLEAR ALL MSG lives under SENT; it is destructive so it is EXEC-armed.
            // Written full-width: the label is longer than the 12-column left half and
            // WriteLeft would clip it to "<CLEAR ALL M".
            grid.Write(CduLayout.DataRow(3), 0, "<CLEAR ALL MSG", CduColor.White, inverse: CduArmed("CLEARALL"));
            grid.WriteLeft(CduLayout.DataRow(6), "<MENU", CduColor.White);
        }

        private void RenderCduMessageList(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            RenderCduHeader(grid, cduMsgSent ? "SENT" : "RECEIVED", snapshot);

            cduVisibleInbox.Clear();
            List<Vns430MessageSnapshot> all = snapshot.Messages.Where(m => m.Outbound == cduMsgSent).ToList();
            List<Vns430MessageSnapshot> shown = all.Take(CduLayout.LskCount).ToList();
            for (int i = 0; i < shown.Count; i++)
            {
                Vns430MessageSnapshot message = shown[i];
                cduVisibleInbox.Add(message.Source);
                CduColor colour = cduMsgSent ? CduColor.Cyan : (message.Unread ? CduColor.Amber : CduColor.White);
                string station = string.IsNullOrWhiteSpace(message.Station) ? message.Type : DatalinkRouting.DisplayStation(message.Station);

                // Use the full row width so long senders (e.g. a full facility callsign)
                // are not clipped to the half column. The last row leaves space for the
                // RETURN> label on the right.
                int width = i + 1 == CduLayout.LskCount ? CduGrid.Cols - 9 : CduGrid.Cols - 1;
                string label = (cduMsgSent ? ">" : "<") + Truncate(station + " " + message.Type, width);
                grid.Write(CduLayout.DataRow(i + 1), 0, label, colour);
            }

            if (all.Count == 0)
            {
                grid.WriteCentered(CduLayout.DataRow(3), "NONE", CduColor.Grey);
            }
            else if (all.Count > CduLayout.LskCount)
            {
                grid.WriteRight(CduLayout.LabelRow(1),
                    "+" + (all.Count - CduLayout.LskCount) + " MORE", CduColor.Grey, small: true);
            }

            grid.WriteRight(CduLayout.DataRow(6), "RETURN>", CduColor.White);
        }

        private void RenderCduMessageDetail(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            Vns430MessageSnapshot message = FindCduSelected(snapshot);
            if (message == null)
            {
                cduPage = CduPageId.MessageList;
                RenderCduMessageList(grid, snapshot);
                return;
            }

            // Custom title (no callsign) so a long station name does not collide.
            string station = string.IsNullOrWhiteSpace(message.Station) ? message.Type : DatalinkRouting.DisplayStation(message.Station);
            grid.WriteCentered(CduLayout.TitleRow, Truncate(station, 18), CduColor.White);
            grid.WriteRight(CduLayout.TitleRow, message.Outbound ? "SENT" : "RCVD", CduColor.Cyan, small: true);

            // Body text wrapped across the upper rows (1..6), clear of the bottom LSKs.
            // Long messages (e.g. an eLoadControl loadsheet) scroll with PREV/NEXT PAGE.
            const int bodyRows = 6;
            List<string> lines = WrapCduText(CduDisplayText(message), CduGrid.Cols);
            int maxScroll = Math.Max(0, lines.Count - bodyRows);
            cduDetailScroll = Math.Clamp(cduDetailScroll, 0, maxScroll);
            for (int i = 0; i < bodyRows; i++)
            {
                int lineIndex = cduDetailScroll + i;
                if (lineIndex < lines.Count)
                {
                    grid.Write(i + 1, 0, Truncate(lines[lineIndex], CduGrid.Cols), CduColor.Green, small: true);
                }
            }

            // Scroll prompt at the bottom-right, phrased to point at the PREV/NEXT PAGE
            // keys rather than looking like an LSK label.
            bool canPrev = cduDetailScroll > 0;
            bool canNext = cduDetailScroll + bodyRows < lines.Count;
            if (canPrev || canNext)
            {
                string hint = (canPrev ? "PREV PAGE? " : string.Empty) + (canNext ? "NEXT PAGE?" : string.Empty);
                hint = hint.Trim();
                grid.Write(CduLayout.ScratchpadRow, Math.Max(0, CduGrid.Cols - hint.Length), hint, CduColor.Cyan, small: true);
            }

            // Bottom-left LSKs (4,5,6): available CPDLC replies.
            IReadOnlyList<string> responses = message.Responses ?? Array.Empty<string>();
            for (int i = 0; i < responses.Count && i < 3; i++)
            {
                grid.WriteLeft(CduLayout.DataRow(4 + i), "<" + responses[i], ReplyColour(responses[i]), inverse: CduArmed("REPLY:" + i));
            }

            // Unit toggle on the bottom-left, below any replies: rows 1..6 are the
            // message body, so anything rendered there would sit on top of the text.
            int unitSlot = CduUnitToggleSlot(message);
            if (unitSlot > 0)
            {
                string shown = cduUnitOverride ?? LoadsheetUnitConverter.DetectUnit(message.Text);
                grid.WriteLeft(CduLayout.LabelRow(unitSlot), "UNITS", CduColor.Cyan, small: true);
                grid.WriteLeft(CduLayout.DataRow(unitSlot), "<IN " + LoadsheetUnitConverter.Other(shown),
                    CduColor.White);
            }

            // Bottom-right LSKs (4,5,6): print actions + return.
            grid.WriteRight(CduLayout.DataRow(4), "PRINT>", CduColor.White);
            grid.WriteRight(CduLayout.DataRow(5), "REPRINT>", CduColor.White);
            grid.WriteRight(CduLayout.DataRow(6), "RETURN>", CduColor.White);
        }

        // Which bottom-left LSK carries the unit toggle, or 0 when the message has no
        // determinable units. Sits directly below the reply keys so the two can never
        // overlap, and clear of the body rows (1..6).
        private static int CduUnitToggleSlot(Vns430MessageSnapshot message)
        {
            if (string.IsNullOrWhiteSpace(LoadsheetUnitConverter.DetectUnit(message?.Text)))
            {
                return 0;
            }

            int replies = Math.Min(3, message?.Responses?.Count ?? 0);
            int slot = 4 + replies;
            return slot <= CduLayout.LskCount ? slot : 0;
        }

        // The viewer's text: converted to the pilot's chosen unit when one is selected.
        // The stored message is never modified, so PRINT still emits the original sheet
        // unless the pilot has explicitly switched units.
        private string CduDisplayText(Vns430MessageSnapshot message)
        {
            string text = message?.Text ?? string.Empty;
            return string.IsNullOrWhiteSpace(cduUnitOverride)
                ? text
                : LoadsheetUnitConverter.Convert(text, cduUnitOverride);
        }

        // ---- LSK handlers --------------------------------------------------

        private void HandleCduMenuLsk(bool rightSide, int index)
        {
            if (rightSide)
            {
                if (index == 1)
                {
                    CduArm("SBFP", (SimbriefPlanLoaded ? "RELOAD" : "LOAD") + " SIMBRIEF FP", CduLoadSimbriefPlan);
                }
                return;
            }

            switch (index)
            {
                case 1: cduPage = CduPageId.Dlk; break;
                case 2: cduPage = CduPageId.Atc; break;
                case 3: cduPage = CduPageId.Aoc; break;
                case 4: cduPage = CduPageId.Messages; break;
                case 5: cduPage = CduPageId.Setup; break;
            }
        }

        private async void CduLoadSimbriefPlan()
        {
            cduStatusLine = "LOADING SIMBRIEF FP...";
            RefreshCduDisplay();

            await LoadSimbriefFlightPlanAsync();

            cduStatusLine = SimbriefPlanLoaded ? "FP " + SimbriefPlanRoute : "FP LOAD FAILED";
            cduStatusError = !SimbriefPlanLoaded;
            RefreshCduDisplay();
        }

        private void HandleCduDlkLsk(bool rightSide, int index)
        {
            if (rightSide)
            {
                if (index == 4)
                {
                    // Reloading the flight plan wipes the inbox for the new leg, so arm it.
                    CduArm("RELOADFP", "LOAD/REFRESH FP + CLEAR", () =>
                    {
                        ReloadFlightPlanButton_Click(mainReloadFlightPlanButton, EventArgs.Empty);
                        DeleteAllElement(this, EventArgs.Empty);
                        cduStatusLine = "FP RELOADED";
                    });
                }
                return;
            }

            switch (index)
            {
                case 1:
                    // Freshen discovery from the latest VATSIM/Hoppie data on entry.
                    UpdateCpdlcDiscoveryFromVatsim();
                    cduScratchpad = string.Empty;
                    cduStatusLine = string.Empty;
                    cduPage = CduPageId.Logon;
                    break;
                case 6: cduPage = CduPageId.Menu; break;
            }
        }

        private void RenderCduLogon(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            RenderCduHeader(grid, "CPDLC LOGON", snapshot);

            // Right column: the station we are logged on to, and PDC availability.
            string unit = string.IsNullOrWhiteSpace(snapshot.CurrentAtcUnit) ? "----" : DatalinkRouting.DisplayStation(snapshot.CurrentAtcUnit);
            grid.WriteRight(CduLayout.LabelRow(1), "LOGGED ON", CduColor.Cyan, small: true);
            grid.WriteRight(CduLayout.DataRow(1), unit,
                string.IsNullOrWhiteSpace(snapshot.CurrentAtcUnit) ? CduColor.Grey : (snapshot.AtcUnitOnline ? CduColor.Green : CduColor.Amber));

            // Which network the clearance request goes to, selectable here so it is
            // decided next to the button that acts on it rather than buried in SETUP.
            // Shows the effective side, so AUTO resolves to the network it would use.
            grid.WriteRight(CduLayout.LabelRow(2), "LOGON VIA", CduColor.Cyan, small: true);
            grid.WriteRight(CduLayout.DataRow(2),
                (PdcRoutesToSayIntentions ? "SI" : "VATSIM") + ">", CduColor.White);

            // Always shown, greyed when the selected network has no PDC to offer - on
            // VATSIM that means no online controller advertises one. Hiding it made the
            // page look broken; greyed says "this exists, just not here right now".
            grid.WriteRight(CduLayout.DataRow(3), "REQ CLR>",
                snapshot.PdcAllowReqClr ? CduColor.Green : CduColor.Grey,
                inverse: snapshot.PdcAllowReqClr && CduArmed("REQCLR"));

            // Two choices, one per network, always in the same slot. The facility name
            // goes on the caption row and the selectable line carries only the network
            // and the code - "<VATSIM KZWY" is exactly the 12 columns available, so
            // neither is ever clipped mid-word the way "KZWY NEW YO" was.
            cduLogonCandidates.Clear();
            List<Vns430CpdlcCandidate> candidates = snapshot.CpdlcCandidates.Take(2).ToList();
            for (int i = 0; i < candidates.Count; i++)
            {
                Vns430CpdlcCandidate option = candidates[i];
                cduLogonCandidates.Add(option);

                bool available = !string.IsNullOrWhiteSpace(option.Code);
                string network = option.Route == AcarsRoute.SayIntentions ? "SI" : "VATSIM";

                grid.WriteLeft(CduLayout.LabelRow(i + 1),
                    Truncate(available ? option.Controller : "NONE ONLINE", CduGrid.HalfCols),
                    CduColor.Cyan, small: true);
                grid.WriteLeft(CduLayout.DataRow(i + 1),
                    available ? "<" + network + " " + DatalinkRouting.DisplayStation(option.Code)
                              : "<" + network + " ----",
                    available ? (option.TunedMatch ? CduColor.Green : CduColor.White) : CduColor.Grey,
                    inverse: CduArmed("LOGON:" + i));
            }

            // Manual code entry: an empty field until a code is armed, then the code
            // itself, so the row shows what will actually be sent.
            grid.WriteLeft(CduLayout.LabelRow(5), "MANUAL LOGON", CduColor.Cyan, small: true);
            bool manualArmed = CduArmed("LOGON:M");
            grid.WriteLeft(CduLayout.DataRow(5),
                "<" + (manualArmed && cduManualLogonCode.Length > 0 ? cduManualLogonCode : "----"),
                manualArmed ? CduColor.White : CduColor.Grey, inverse: manualArmed);
            grid.WriteLeft(CduLayout.DataRow(6), "<RETURN", CduColor.White);

            RenderCduScratchpad(grid);
        }

        private void HandleCduLogonLsk(bool rightSide, int index)
        {
            cduStatusLine = string.Empty;
            if (rightSide)
            {
                if (index == 2)
                {
                    // Pick the network the clearance goes to. Writes an explicit side
                    // rather than leaving AUTO, so the choice made here is the choice
                    // that is used.
                    SavedPdcVia = PdcRoutesToSayIntentions ? "VATSIM" : "SI";
                    Properties.Settings.Default.Save();
                    SyncSayIntentionsPolling();
                    UpdateOnlineStatusLabel();
                    // No status message: the row itself shows the new selection, so an
                    // amber line underneath only repeated it.
                }
                else if (index == 3)
                {
                    if (!GetVns430Snapshot().PdcAllowReqClr)
                    {
                        cduStatusLine = PdcRoutesToSayIntentions
                            ? "PDC NOT READY - CHECK SI SETUP"
                            : "NO VATSIM PDC AT THIS FIELD";
                        cduStatusError = true;
                        return;
                    }

                    // REQ CLR opens the PREDEP CLEARANCE request page (same page as the
                    // AOC menu) with the recipient, stand and ATIS prefilled, so the
                    // pilot reviews and completes the request rather than firing a
                    // one-shot message blind. SEND on that page is the EXEC-armed step.
                    OpenCduPredepClearancePage();
                }
                return;
            }

            switch (index)
            {
                case 1:
                case 2:
                case 3:
                case 4:
                    int position = index - 1;
                    if (position < cduLogonCandidates.Count)
                    {
                        Vns430CpdlcCandidate picked = cduLogonCandidates[position];
                        if (string.IsNullOrWhiteSpace(picked.Code))
                        {
                            // The VATSIM slot with nobody online - nothing to log on to.
                            cduStatusLine = "NO VATSIM CPDLC ATC ONLINE";
                            cduStatusError = true;
                            break;
                        }
                        CduLogonTo(picked.Code, "LOGON:" + position, picked.Route);
                    }
                    break;
                case 5:
                    // A typed code carries no network of its own, so it follows the
                    // LOGON VIA selection - otherwise a manual logon fell back to
                    // content routing and could land on the network the pilot had just
                    // selected away from.
                    CduLogonTo(cduScratchpad, "LOGON:M",
                        PdcRoutesToSayIntentions ? AcarsRoute.SayIntentions : AcarsRoute.Hoppie);
                    break;
                case 6:
                    cduPage = CduPageId.Dlk;
                    break;
            }
        }

        // The PREDEP CLEARANCE workflow page, prefilled for the active PDC route:
        // recipient PKGM when PDC goes to SI, the discovered facility on VATSIM. The
        // stand defaults to ---- (matching the quick request) and the ATIS letter to
        // the best-known departure ATIS; both stay editable on the page.
        private void OpenCduPredepClearancePage()
        {
            Vns430BackendSnapshot snapshot = GetVns430Snapshot();
            cduWorkflow = Vns430Workflow.Create(Vns430WorkflowKind.AocPreDeparture, snapshot);

            // On SI, address the regional unit for where the aircraft is rather than
            // their generic ATSU - SayIntentions staffs every station, so the clearance
            // should come from the facility that would actually issue it.
            string recipient = PdcRoutesToSayIntentions
                ? RegionalAtsuCode()
                : (pdcDiscoveryLogonCode ?? string.Empty).Trim().ToUpperInvariant();
            SetCduWorkflowField("RECIPIENT", recipient);
            SetCduWorkflowField("GATE", "----");

            // ATIS is left blank until a real one has been received. Defaulting to "A"
            // put a plausible but usually wrong letter on a clearance request, which is
            // worse than an obviously empty field the pilot must fill.
            SetCduWorkflowField("ATIS", LastReceivedAtisLetter);

            cduScratchpad = string.Empty;
            cduStatusLine = string.Empty;
            cduPage = CduPageId.Request;
        }

        private void SetCduWorkflowField(string key, string value)
        {
            Vns430EditField field = cduWorkflow?.Fields.FirstOrDefault(f => f.Key == key);
            if (field != null && !string.IsNullOrWhiteSpace(value))
            {
                field.Value = value;
            }
        }

        private void CduLogonTo(string code, string armKey, AcarsRoute route = AcarsRoute.Auto)
        {
            string clean = (code ?? string.Empty).Trim().ToUpperInvariant();
            if (clean.Length < 3)
            {
                cduStatusLine = "ENTER 3-4 CHAR CODE";
                cduStatusError = true;
                return;
            }

            // The prompt names the network so the armed action cannot be ambiguous.
            string network = route == AcarsRoute.SayIntentions ? "SI"
                : route == AcarsRoute.Hoppie ? "VATSIM"
                : string.Empty;
            string label = "LOGON " + DatalinkRouting.DisplayStation(clean) +
                (network.Length > 0 ? " VIA " + network : string.Empty);

            cduScratchpad = string.Empty;
            cduManualLogonCode = armKey == "LOGON:M" ? clean : string.Empty;
            CduArm(armKey, label, () =>
            {
                cduStatusLine = label.Replace("LOGON ", "LOGON SENT ");
                _ = Vns430RequestLogonAsync(clean, route);
            });
        }

        private void HandleCduMessagesLsk(bool rightSide, int index)
        {
            if (rightSide)
            {
                return;
            }

            switch (index)
            {
                case 1: cduMsgSent = false; cduPage = CduPageId.MessageList; break;
                case 2: cduMsgSent = true; cduPage = CduPageId.MessageList; break;
                case 3:
                    // Destructive, so arm it; EXEC clears the whole inbox.
                    CduArm("CLEARALL", "CLEAR ALL MSG", () =>
                    {
                        DeleteAllElement(this, EventArgs.Empty);
                        cduStatusLine = "MESSAGES CLEARED";
                    });
                    break;
                case 6: cduPage = CduPageId.Menu; break;
            }
        }

        private void HandleCduMessageListLsk(bool rightSide, int index)
        {
            if (rightSide)
            {
                if (index == 6)
                {
                    cduPage = CduPageId.Messages;
                }
                return;
            }

            int position = index - 1;
            if (position >= 0 && position < cduVisibleInbox.Count)
            {
                cduSelectedMessage = cduVisibleInbox[position];
                cduDetailScroll = 0;
                MarkMessageRead(cduSelectedMessage);
                cduPage = CduPageId.MessageDetail;
            }
        }

        private void HandleCduMessageDetailLsk(bool rightSide, int index)
        {
            Vns430MessageSnapshot message = FindCduSelected(GetVns430Snapshot());
            if (message == null)
            {
                cduPage = CduPageId.MessageList;
                return;
            }

            if (!rightSide)
            {
                // Unit toggle sits immediately below the replies on the bottom left.
                if (index == CduUnitToggleSlot(message))
                {
                    cduUnitOverride = LoadsheetUnitConverter.Other(
                        cduUnitOverride ?? LoadsheetUnitConverter.DetectUnit(message.Text));
                    cduDetailScroll = 0;   // the converted sheet re-wraps
                    cduStatusLine = "UNITS " + cduUnitOverride;
                    return;
                }

                // Replies live on the bottom-left LSKs 4,5,6.
                IReadOnlyList<string> responses = message.Responses ?? Array.Empty<string>();
                int position = index - 4;
                if (position >= 0 && position < responses.Count && position < 3)
                {
                    Vns430MessageSnapshot armMessage = message;
                    string reply = responses[position];
                    CduArm("REPLY:" + position, reply, () => Vns430Reply(armMessage, reply));
                }
                return;
            }

            switch (index)
            {
                case 4: PrintDatalinkMessage(message.Source); break;
                case 5: ReprintButton_Click(boeingReprintButton, EventArgs.Empty); break;
                case 6:
                    cduUnitOverride = null;   // each message opens in its own units
                    cduPage = CduPageId.MessageList;
                    break;
            }
        }

        // ---- ATC / AOC request pages --------------------------------------

        private static readonly (string Label, Vns430WorkflowKind? Kind)[] CduAtcMenuItems =
        {
            ("DIRECT TO", Vns430WorkflowKind.AtcDirect),
            ("LEVEL", Vns430WorkflowKind.AtcLevel),
            ("SPEED", Vns430WorkflowKind.AtcSpeed),
            ("WHEN CAN WE", Vns430WorkflowKind.AtcWhenCanWe),
            ("FREE TEXT", Vns430WorkflowKind.AtcFreeText)
        };

        // AOC has no PDC entry: a clearance request is reached from the LOGON page via
        // REQ CLR, where the network selection and availability that govern it live.
        // Having a second, unguarded route to the same request was only a way to send
        // it to the wrong place. LOADSHEET takes the slot.
        private static readonly (string Label, Vns430WorkflowKind? Kind)[] CduAocMenuItems =
        {
            ("TELEX", Vns430WorkflowKind.AocTelex),
            ("METAR", Vns430WorkflowKind.AocMetar),
            ("ATIS", Vns430WorkflowKind.AocAtis),
            ("LOADSHEET", null),
            ("OCEANIC", Vns430WorkflowKind.AocOceanic)
        };

        private void RenderCduRequestMenu(CduGrid grid, Vns430BackendSnapshot snapshot, string title,
            (string Label, Vns430WorkflowKind? Kind)[] items)
        {
            RenderCduHeader(grid, title, snapshot);
            for (int i = 0; i < items.Length && i < 5; i++)
            {
                grid.WriteLeft(CduLayout.DataRow(i + 1), "<" + items[i].Label, CduColor.White);
            }
            grid.WriteRight(CduLayout.DataRow(6), "MENU>", CduColor.White);
        }

        private void HandleCduAtcLsk(bool rightSide, int index)
        {
            if (rightSide && index == 1)
            {
                cduWorkflow = Vns430Workflow.Create(Vns430WorkflowKind.AtcPositionReport, GetVns430Snapshot());
                cduScratchpad = string.Empty;
                cduStatusLine = string.Empty;
                cduPage = CduPageId.Request;
                return;
            }
            HandleCduRequestMenuSelection(rightSide, index, CduAtcMenuItems);
        }

        // Right column of the AOC page: the company position report, and the address it
        // goes to. The address is an ordinary Hoppie recipient rather than a credential,
        // so it is set here next to the thing that uses it - and shown here so nobody
        // opens the report page only to find it has nowhere to go.
        private void RenderCduAocCompany(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            bool addressed = !string.IsNullOrWhiteSpace(snapshot.CompanyAddress);

            grid.WriteRight(CduLayout.DataRow(1), "POS RPT>", addressed ? CduColor.White : CduColor.Grey);

            grid.WriteRight(CduLayout.LabelRow(2), "COMPANY", CduColor.Cyan, small: true);
            grid.WriteRight(CduLayout.DataRow(2),
                (addressed ? snapshot.CompanyAddress : "NOT SET") + ">",
                addressed ? CduColor.Green : CduColor.Amber);

            // The company address is typed here, so the page needs the scratchpad line -
            // without it the keypad looks dead even once the typing is accepted.
            RenderCduScratchpad(grid);
        }

        private void HandleCduAocLsk(bool rightSide, int index)
        {
            if (rightSide && index == 1)
            {
                if (string.IsNullOrWhiteSpace(SavedAocAddress))
                {
                    cduStatusLine = "SET COMPANY ADDRESS";
                    cduStatusError = true;
                    return;
                }

                cduWorkflow = Vns430Workflow.Create(Vns430WorkflowKind.AocCompanyPosition, GetVns430Snapshot());
                cduScratchpad = string.Empty;
                cduStatusLine = string.Empty;
                cduPage = CduPageId.Request;
                return;
            }

            // Typed address moves into the field; an empty scratchpad clears it, matching
            // how every other CDU field behaves.
            if (rightSide && index == 2)
            {
                SavedAocAddress = cduScratchpad;
                Properties.Settings.Default.Save();
                cduStatusLine = string.IsNullOrWhiteSpace(cduScratchpad)
                    ? "COMPANY ADDRESS CLEARED"
                    : "COMPANY " + SavedAocAddress;
                cduScratchpad = string.Empty;
                return;
            }

            HandleCduRequestMenuSelection(rightSide, index, CduAocMenuItems);
        }

        private void HandleCduRequestMenuSelection(bool rightSide, int index,
            (string Label, Vns430WorkflowKind? Kind)[] items)
        {
            if (rightSide)
            {
                if (index == 6)
                {
                    cduPage = CduPageId.Menu;
                }
                return;
            }

            int position = index - 1;
            if (position >= 0 && position < items.Length && position < 5)
            {
                // A null kind is a page rather than a request workflow (LOADSHEET).
                if (items[position].Kind is not Vns430WorkflowKind kind)
                {
                    CduOpenLoadControl();
                    return;
                }

                cduWorkflow = Vns430Workflow.Create(kind, GetVns430Snapshot());
                cduScratchpad = string.Empty;
                cduStatusLine = string.Empty;
                cduPage = CduPageId.Request;
            }
        }

        // Request fields fill left LSK 1..5 then right LSK 1..5 (ten slots). LSK6 is
        // RETURN / SEND; the scratchpad is the dedicated bottom row.
        private static (bool RightSide, int Lsk) CduFieldSlot(int slot)
        {
            return slot < 5 ? (false, slot + 1) : (true, (slot - 5) + 1);
        }

        // The bottom-right slot, immediately above SEND>.
        private const int CduViaSlot = 9;

        /// <summary>
        /// Maps a display slot to the workflow field that occupies it. A VIA selector is
        /// pinned to the bottom of the page rather than taking its turn in the field
        /// order, so every AOC page carries it in the same place; the remaining fields
        /// close up around it. Returns -1 for an empty slot.
        /// </summary>
        private static int CduSlotField(Vns430Workflow workflow, int slot)
        {
            int via = workflow.Fields.FindIndex(field => field.Key == "VIA");
            if (via < 0)
            {
                return slot < workflow.Fields.Count ? slot : -1;
            }

            if (slot == CduViaSlot)
            {
                return via;
            }

            // Fields declared after VIA shift up one to fill the gap it left.
            int index = slot < via ? slot : slot + 1;
            return index < workflow.Fields.Count ? index : -1;
        }

        private void RenderCduRequest(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            if (cduWorkflow == null)
            {
                cduPage = CduPageId.Menu;
                RenderCduMenu(grid, snapshot);
                return;
            }

            grid.WriteCentered(CduLayout.TitleRow, Truncate(cduWorkflow.Title, 24), CduColor.White);

            for (int slot = 0; slot < 10; slot++)
            {
                int fieldIndex = CduSlotField(cduWorkflow, slot);
                if (fieldIndex < 0)
                {
                    continue;
                }

                Vns430EditField field = cduWorkflow.Fields[fieldIndex];
                (bool right, int lsk) = CduFieldSlot(slot);
                bool empty = string.IsNullOrWhiteSpace(field.CleanValue);
                string value = empty ? (field.IsOption ? "----" : "[   ]") : field.CleanValue;
                CduColor colour = empty ? CduColor.Grey : CduColor.Green;

                if (right)
                {
                    grid.WriteRight(CduLayout.LabelRow(lsk), field.Label, CduColor.Cyan, small: true);
                    grid.WriteRight(CduLayout.DataRow(lsk), Truncate(value, CduGrid.HalfCols - 1) + ">", colour);
                }
                else
                {
                    grid.WriteLeft(CduLayout.LabelRow(lsk), field.Label, CduColor.Cyan, small: true);
                    grid.WriteLeft(CduLayout.DataRow(lsk), "<" + Truncate(value, CduGrid.HalfCols - 1), colour);
                }
            }

            // Scratchpad (or the last status message) sits just above the RETURN/SEND row.
            RenderCduScratchpad(grid);

            grid.WriteLeft(CduLayout.DataRow(6), "<RETURN", CduColor.White);
            grid.WriteRight(CduLayout.DataRow(6), cduRequestSending ? "SENDING" : "SEND>",
                cduRequestSending ? CduColor.Grey : CduColor.Green, inverse: CduArmed("SEND"));
        }

        private void HandleCduRequestLsk(bool rightSide, int index)
        {
            if (cduWorkflow == null)
            {
                cduPage = CduPageId.Menu;
                return;
            }

            if (index == 6)
            {
                if (rightSide)
                {
                    // Arm the transmit; EXEC sends it.
                    CduArm("SEND", "SEND REQUEST", CduSendCurrentWorkflow);
                }
                else
                {
                    cduPage = cduWorkflow.Kind.ToString().StartsWith("Atc", StringComparison.Ordinal)
                        ? CduPageId.Atc
                        : CduPageId.Aoc;
                    cduWorkflow = null;
                }
                return;
            }

            int slot = rightSide ? 5 + (index - 1) : index - 1;
            int fieldIndex = slot < 0 ? -1 : CduSlotField(cduWorkflow, slot);
            if (fieldIndex < 0)
            {
                return;
            }

            Vns430EditField field = cduWorkflow.Fields[fieldIndex];
            cduStatusLine = string.Empty;
            if (field.IsOption)
            {
                field.Step(0, 1); // cycle to the next option
            }
            else if (!string.IsNullOrEmpty(cduScratchpad))
            {
                field.Value = Truncate(cduScratchpad, field.MaxLength);
                cduScratchpad = string.Empty;
            }
            else
            {
                field.Value = string.Empty; // an empty scratchpad clears the field
            }
        }

        private async void CduSendCurrentWorkflow()
        {
            if (cduWorkflow == null || cduRequestSending)
            {
                return;
            }

            cduRequestSending = true;
            cduStatusLine = "SENDING...";
            RefreshCduDisplay();

            Vns430OperationResult result = await Vns430SendWorkflowAsync(cduWorkflow, GetVns430Snapshot());

            cduRequestSending = false;
            cduStatusLine = result.Status;
            cduStatusError = !result.Success;
            if (result.Success)
            {
                cduWorkflow = null;
                cduMsgSent = true;
                cduPage = CduPageId.MessageList;
            }
            RefreshCduDisplay();
        }

        // The scratchpad occupies the bottom row, typed left to right with no brackets.
        // A transient status message (amber) replaces it until the next keystroke.
        private void RenderCduScratchpad(CduGrid grid)
        {
            if (!string.IsNullOrWhiteSpace(cduStatusLine))
            {
                grid.Write(CduLayout.ScratchpadRow, 0, Truncate(cduStatusLine, CduGrid.Cols), CduColor.Amber, small: true);
            }
            else
            {
                grid.Write(CduLayout.ScratchpadRow, 0, Truncate(cduScratchpad, CduGrid.Cols), CduColor.White);
            }
        }

        /// <summary>
        /// Pages that accept typing. Any page with a line key that reads
        /// <c>cduScratchpad</c> must be here, or the keypad is silently dead on it: the
        /// characters go nowhere and the line key consumes an empty buffer. Static and
        /// internal so a test can hold it to that rule.
        /// </summary>
        internal static bool CduPageAcceptsScratchpad(CduPageId page) =>
            page is CduPageId.Request
                 or CduPageId.Logon
                 or CduPageId.Aoc          // company address
                 or CduPageId.SetupAccount
                 or CduPageId.SetupPrinter;

        private bool CduScratchpadActive() => CduPageAcceptsScratchpad(cduPage);

        private void CduScratchpadType(char c)
        {
            if (CduScratchpadActive() && cduScratchpad.Length < CduGrid.Cols)
            {
                cduScratchpad += c;
                cduStatusLine = string.Empty;
                RefreshCduDisplay();
            }
        }

        private void CduScratchpadBackspace()
        {
            if (CduScratchpadActive() && cduScratchpad.Length > 0)
            {
                cduScratchpad = cduScratchpad.Substring(0, cduScratchpad.Length - 1);
                RefreshCduDisplay();
            }
        }

        private void CduScratchpadClearAll()
        {
            if (CduScratchpadActive())
            {
                cduScratchpad = string.Empty;
                cduStatusLine = string.Empty;
                RefreshCduDisplay();
            }
        }

        // ---- SETUP page ----------------------------------------------------

        // SETUP is a menu of sub-pages so no single page is overcrowded.
        private void RenderCduSetup(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            grid.WriteCentered(CduLayout.TitleRow, "SETUP", CduColor.White);

            // Left column: sub-pages. ACCOUNT is highlighted when a required credential
            // is missing so the attention carries through from the menu.
            bool acctAttn = CduSetupNeedsAttention();
            grid.WriteLeft(CduLayout.DataRow(1), "<ACCOUNT", acctAttn ? CduColor.Amber : CduColor.White, inverse: acctAttn);
            grid.WriteLeft(CduLayout.DataRow(2), "<PRINTER", CduColor.White);
            grid.WriteLeft(CduLayout.DataRow(3), "<TECHNICAL", CduColor.White);
            grid.WriteLeft(CduLayout.DataRow(4), "<WINWING", CduColor.White);

            // Right column: instrument selector (CDU <-> GNS430).
            //
            // ATC NETWORK, LOGON VIA and WX SOURCE used to live here too. All three are
            // gone: the network is chosen per logon on the LOGON page and the weather
            // source per request on METAR/ATIS, each next to the thing it affects, so a
            // second global switch could only contradict it.
            RenderCduSetupField(grid, 1, true, "INSTRUMENT", InstrumentText());

            // Hardware keys: gates the MSFS module's CDU/DCDU L-vars so a physical CDU
            // (e.g. WinWing) can drive the LSKs and keypad through MobiFlight.
            RenderCduSetupField(grid, 2, true, "HW KEYS", IsDcduCompanionModeEnabled() ? "ON" : "OFF");

            grid.WriteLeft(CduLayout.DataRow(6), "<MENU", CduColor.White);
        }

        // WX source display: AUTO when following the network, otherwise the chosen source.
        private static string WxSourceText()
        {
            string over = SavedWxSourceOverride;
            if (string.IsNullOrWhiteSpace(over))
            {
                return "AUTO";
            }
            return Vns430WeatherClient.SourceLabel(Vns430WeatherClient.ParseSource(over));
        }

        private void RenderCduSetupAccount(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            grid.WriteCentered(CduLayout.TitleRow, "ACCOUNT / LOGIN", CduColor.White);

            // Identifiers (CID / SimBrief) show their value; secret codes/keys show SET so
            // the actual value is not exposed here (view it on the TECHNICAL page).
            RenderCduAccountField(grid, 1, "VATSIM CID", SavedCID > 0 ? SavedCID.ToString() : null, secret: false);
            RenderCduAccountField(grid, 2, "HOPPIE CODE", SavedHoppieCode, secret: true);
            RenderCduAccountField(grid, 3, "SIMBRIEF", SimbriefID, secret: false);
            RenderCduAccountField(grid, 4, "ELOAD KEY", SavedELoadControlApiKey, secret: true);
            RenderCduAccountField(grid, 5, "SI KEY", SavedSayIntentionsApiKey, secret: true);

            grid.WriteLeft(CduLayout.DataRow(6), "<SETUP", CduColor.White);
            RenderCduScratchpad(grid);
        }

        // A credential field: cyan label on its LSK label row, the value on the full-width
        // data row below it. Secret fields show "SET" instead of the value.
        private void RenderCduAccountField(CduGrid grid, int lsk, string label, string value, bool secret)
        {
            grid.WriteLeft(CduLayout.LabelRow(lsk), label, CduColor.Cyan, small: true);
            bool empty = string.IsNullOrWhiteSpace(value);
            if (empty)
            {
                grid.Write(CduLayout.DataRow(lsk), 0, "<----", CduColor.Grey);
                return;
            }

            if (secret)
            {
                grid.Write(CduLayout.DataRow(lsk), 0, "<SET", CduColor.Green);
                return;
            }

            string shown = "<" + value;
            if (shown.Length > CduGrid.Cols)
            {
                shown = shown.Substring(0, CduGrid.Cols - 1) + ">";
            }
            grid.Write(CduLayout.DataRow(lsk), 0, shown, CduColor.Green);
        }

        // TECHNICAL: a full-screen dump of every stored credential/config value, paged if
        // it does not all fit. This is the one place the actual secret codes are shown.
        private const int CduTechContentRows = 11;   // grid rows 1..11

        private void RenderCduSetupTechnical(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            // Each entry is kept together as a group. Paginating by raw row count split
            // a long value from its own label - the SayIntentions key in particular,
            // which wraps over several rows and straddled the page break.
            List<List<(string Text, CduColor Colour, bool Small)>> entries = new();

            void AddEntry(string label, string value)
            {
                List<(string Text, CduColor Colour, bool Small)> entry = new()
                {
                    (label, CduColor.Cyan, true)
                };

                if (string.IsNullOrWhiteSpace(value))
                {
                    entry.Add(("----", CduColor.Grey, false));
                }
                else
                {
                    foreach (string line in WrapCduText(value, CduGrid.Cols))
                    {
                        entry.Add((line, CduColor.Green, false));
                    }
                }

                entries.Add(entry);
            }

            AddEntry("VATSIM CID", SavedCID > 0 ? SavedCID.ToString() : null);
            AddEntry("HOPPIE CODE", SavedHoppieCode);
            AddEntry("SIMBRIEF", SimbriefID);
            AddEntry("ELOAD KEY", SavedELoadControlApiKey);
            AddEntry("SI KEY", SavedSayIntentionsApiKey);
            AddEntry("LOGON VIA", SavedPdcVia);
            AddEntry("WX SOURCE", WxSourceText() + " (" + Vns430WeatherClient.SourceLabel(EffectiveWxSource()) + ")");
            AddEntry("PRINTER", SelectedPrinterName);

            // Pack entries into pages without ever splitting one.
            List<List<(string Text, CduColor Colour, bool Small)>> pages = new();
            List<(string Text, CduColor Colour, bool Small)> page = new();
            foreach (var entry in entries)
            {
                if (page.Count > 0 && page.Count + entry.Count > CduTechContentRows)
                {
                    pages.Add(page);
                    page = new List<(string Text, CduColor Colour, bool Small)>();
                }
                page.AddRange(entry);
            }
            if (page.Count > 0)
            {
                pages.Add(page);
            }

            int pageCount = Math.Max(1, pages.Count);
            cduTechPage = Math.Clamp(cduTechPage, 0, pageCount - 1);

            string title = pageCount > 1 ? "TECHNICAL " + (cduTechPage + 1) + "/" + pageCount : "TECHNICAL";
            grid.WriteCentered(CduLayout.TitleRow, title, CduColor.White);

            if (pages.Count > 0)
            {
                List<(string Text, CduColor Colour, bool Small)> shown = pages[cduTechPage];
                for (int i = 0; i < shown.Count && i < CduTechContentRows; i++)
                {
                    (string text, CduColor colour, bool small) = shown[i];
                    grid.Write(i + 1, 0, Truncate(text, CduGrid.Cols), colour, small: small);
                }
            }

            grid.WriteLeft(CduLayout.DataRow(6), "<SETUP", CduColor.White);
            if (pageCount > 1)
            {
                bool canPrev = cduTechPage > 0;
                bool canNext = cduTechPage < pageCount - 1;
                string hint = (canPrev ? "PREV PAGE? " : string.Empty) + (canNext ? "NEXT PAGE?" : string.Empty);
                hint = hint.Trim();
                grid.Write(CduLayout.ScratchpadRow, Math.Max(0, CduGrid.Cols - hint.Length), hint, CduColor.Cyan, small: true);
            }
        }

        private void HandleCduSetupTechnicalLsk(bool rightSide, int index)
        {
            if (!rightSide && index == 6)
            {
                cduPage = CduPageId.Setup;
            }
        }

        private void RenderCduSetupPrinter(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            grid.WriteCentered(CduLayout.TitleRow, "PRINTER", CduColor.White);

            // The printer name gets a full-width row so long queue names are not truncated.
            grid.WriteLeft(CduLayout.LabelRow(1), "PRINTER", CduColor.Cyan, small: true);
            bool hasPrinter = !string.IsNullOrWhiteSpace(SelectedPrinterName);
            grid.Write(CduLayout.DataRow(1), 0, "<" + (hasPrinter ? Truncate(SelectedPrinterName, CduGrid.Cols - 1) : "SELECT PRINTER"),
                hasPrinter ? CduColor.Green : CduColor.Amber);

            RenderCduSetupField(grid, 2, false, "MODE", PrinterModeText(PrinterMode));
            RenderCduSetupField(grid, 3, false, "PROFILE", PrinterProfileText(PrinterProfile));
            RenderCduSetupField(grid, 4, false, "CUT", PrinterCutMode.ToString().ToUpperInvariant());

            RenderCduSetupField(grid, 2, true, "FEED LINES", PrinterFeedLines.ToString());
            grid.WriteRight(CduLayout.LabelRow(3), "TEST", CduColor.Cyan, small: true);
            grid.WriteRight(CduLayout.DataRow(3), "PRINT>", CduColor.White);

            grid.WriteLeft(CduLayout.DataRow(6), "<SETUP", CduColor.White);
            RenderCduScratchpad(grid);
        }

        private static string PrinterModeText(DatalinkPrinterMode mode) => mode switch
        {
            DatalinkPrinterMode.RawEscPos => "ESC/POS",
            DatalinkPrinterMode.MockFile => "MOCK FILE",
            _ => "WINDOWS"
        };

        // Short profile names so they fit the half-width field (the full display names
        // truncate to unreadable fragments like "GENERIC 4 I").
        private static string PrinterProfileText(DatalinkPrinterProfile profile) =>
            profile == DatalinkPrinterProfile.CitizenCtS4000_112Mm ? "4 INCH" : "80MM";

        private static void RenderCduSetupField(CduGrid grid, int lsk, bool right, string label, string value,
            CduColor? valueColour = null)
        {
            bool empty = string.IsNullOrWhiteSpace(value);
            CduColor colour = empty ? CduColor.Grey : valueColour ?? CduColor.Green;
            string shown = empty ? "----" : value;
            if (right)
            {
                grid.WriteRight(CduLayout.LabelRow(lsk), label, CduColor.Cyan, small: true);
                grid.WriteRight(CduLayout.DataRow(lsk), shown + ">", colour);
            }
            else
            {
                grid.WriteLeft(CduLayout.LabelRow(lsk), label, CduColor.Cyan, small: true);
                grid.WriteLeft(CduLayout.DataRow(lsk), "<" + shown, colour);
            }
        }

        private void HandleCduSetupLsk(bool rightSide, int index)
        {
            cduStatusLine = string.Empty;
            if (!rightSide)
            {
                switch (index)
                {
                    case 1: cduPage = CduPageId.SetupAccount; break;
                    case 2: cduPage = CduPageId.SetupPrinter; break;
                    case 3: cduTechPage = 0; cduPage = CduPageId.SetupTechnical; break;
                    case 4: cduPage = CduPageId.SetupWinwing; break;
                    case 6: cduPage = CduPageId.Menu; break;
                }
                return;
            }

            switch (index)
            {
                case 1: CduCycleInstrument(); break;
                case 2: CduToggleHardwareKeys(); break;
            }
        }


        // Turn the MSFS module's CDU/DCDU hardware keys on or off. Without this the
        // EASYCPDLC_CDU_* / EASYCPDLC_DCDU_* L-vars are ignored, so a physical CDU cannot
        // drive the panel.
        // WINWING sub-page: the four cockpit positions as explicit choices, so the seat is
        // picked deliberately rather than cycled past. The active one is shown inverse.
        private void RenderCduSetupWinwing(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            grid.WriteCentered(CduLayout.TitleRow, "WINWING CDU", CduColor.White);

            WinwingSeat active = cduWinwingSeat;
            RenderCduWinwingChoice(grid, 1, "OFF", WinwingSeat.Off, active, this);
            RenderCduWinwingChoice(grid, 2, "CAPT", WinwingSeat.Captain, active, this);
            RenderCduWinwingChoice(grid, 3, "FO", WinwingSeat.FirstOfficer, active, this);
            RenderCduWinwingChoice(grid, 4, "OBS", WinwingSeat.Observer, active, this);
            // The 737 only has captain and first-officer CDUs, so the observer unit is
            // never claimed by the aircraft - it is the safe seat to mirror onto. The
            // marker sits directly after <OBS so there is no doubt which line it tags.
            grid.Write(CduLayout.DataRow(4), 5, "RECOMMENDED", CduColor.Cyan, small: true);

            // Live link state, so "selected" and "actually sending" are distinguishable.
            grid.WriteRight(CduLayout.LabelRow(1), "LINK", CduColor.Cyan, small: true);
            if (active == WinwingSeat.Off)
            {
                grid.WriteRight(CduLayout.DataRow(1), "OFF", CduColor.Grey);
            }
            else if (cduWinwingSink?.Connected == true)
            {
                grid.WriteRight(CduLayout.DataRow(1), "SENDING", CduColor.Green);
            }
            else
            {
                grid.WriteRight(CduLayout.DataRow(1), "WAITING", CduColor.Amber);
            }

            // Both lines must fit the 24-column grid or they are clipped mid-word.
            grid.Write(CduLayout.LabelRow(5), 0, "SET SEAT IN SIMAPPPRO,", CduColor.Cyan, small: true);
            grid.Write(CduLayout.DataRow(5), 0, "CLOSE IT, RUN MOBIFLIGHT", CduColor.Cyan, small: true);

            grid.WriteLeft(CduLayout.DataRow(6), "<SETUP", CduColor.White);
            RenderCduScratchpad(grid);
        }

        private static void RenderCduWinwingChoice(CduGrid grid, int lsk, string label, WinwingSeat seat, WinwingSeat active, MainForm form)
        {
            bool selected = seat == active;
            bool armed = form.CduArmed("WINWING:" + seat);
            grid.WriteLeft(CduLayout.DataRow(lsk), "<" + label,
                armed ? CduColor.Amber : selected ? CduColor.Green : CduColor.White,
                inverse: selected || armed);
        }

        private void HandleCduSetupWinwingLsk(bool rightSide, int index)
        {
            if (rightSide)
            {
                return;
            }

            cduStatusLine = string.Empty;
            switch (index)
            {
                case 1: CduSelectWinwingSeat(WinwingSeat.Off); break;
                case 2: CduSelectWinwingSeat(WinwingSeat.Captain); break;
                case 3: CduSelectWinwingSeat(WinwingSeat.FirstOfficer); break;
                case 4: CduSelectWinwingSeat(WinwingSeat.Observer); break;
                case 6: cduPage = CduPageId.Setup; break;
            }
        }

        // Session-only: deliberately not written to settings, so the next launch starts OFF.
        //
        // Taking over a physical CDU is consequential - it claims a panel that may be
        // showing a live aircraft display - so activating a seat is EXEC-armed like any
        // other committing action. Turning it OFF is a stop, not an activation, so it
        // applies immediately.
        private void CduSelectWinwingSeat(WinwingSeat seat)
        {
            if (seat == WinwingSeat.Off)
            {
                cduWinwingSeat = seat;
                ApplyCduWinwingSeat(seat);
                cduStatusLine = "WINWING OFF";
                return;
            }

            CduArm("WINWING:" + seat, "WINWING " + WinwingCduSink.Label(seat), () =>
            {
                cduWinwingSeat = seat;
                ApplyCduWinwingSeat(seat);
                cduStatusLine = "WINWING " + WinwingCduSink.Label(seat);
            });
        }

        // Attach a sink for the chosen seat, or detach entirely when OFF. Detaching restores
        // the null sink so the paint path stops serialising frames.
        private void ApplyCduWinwingSeat(WinwingSeat seat)
        {
            if (cduWinwingSink != null && cduWinwingSink.Seat == seat)
            {
                return;
            }

            cduWinwingSink?.Dispose();
            cduWinwingSink = null;

            if (cduDisplayPanel == null || cduDisplayPanel.IsDisposed)
            {
                return;
            }

            if (seat == WinwingSeat.Off)
            {
                cduDisplayPanel.Sink = NullCduDisplaySink.Instance;
                return;
            }

            cduWinwingSink = new WinwingCduSink(seat);
            cduDisplayPanel.Sink = cduWinwingSink;
        }

        private void CduToggleHardwareKeys()
        {
            bool enable = !IsDcduCompanionModeEnabled();
            if (SetDcduCompanionMode(enable, out string error) && string.IsNullOrWhiteSpace(error))
            {
                cduStatusLine = "HW KEYS " + (enable ? "ON" : "OFF");
                return;
            }

            // Saved either way; the module connects when MSFS/SimConnect becomes available.
            cduStatusLine = enable ? "HW KEYS ON - AWAITING SIM" : "HW KEYS OFF";
        }

        private string InstrumentText() => IsVns430PanelVisibleForInstrument() ? "GNS430" : "CDU";

        // Instruments are mutually-exclusive modes. From the CDU SETUP the CDU is the active
        // instrument, so selecting here switches to the GNS430 (which hides the CDU window).
        private void CduCycleInstrument()
        {
            if (IsVns430PanelVisibleForInstrument())
            {
                SelectCduInstrument();
                cduStatusLine = "INSTRUMENT CDU";
            }
            else
            {
                cduStatusLine = "INSTRUMENT GNS430";
                SelectGns430Instrument();
            }
        }

        private void HandleCduSetupAccountLsk(bool rightSide, int index)
        {
            if (rightSide)
            {
                return;
            }

            cduStatusLine = string.Empty;
            switch (index)
            {
                case 1: CduApplyCidFromScratchpad(); break;
                case 2: CduApplyTextSetting(v => SavedHoppieCode = v.ToUpperInvariant(), "HOPPIE"); break;
                case 3: CduApplyTextSetting(v => SimbriefID = v, "SIMBRIEF"); break;
                case 4: CduApplyTextSetting(v => SavedELoadControlApiKey = v, "ELOAD KEY"); break;
                case 5: CduApplyTextSetting(v => SavedSayIntentionsApiKey = v, "SI KEY"); break;
                case 6: cduPage = CduPageId.Setup; break;
            }
        }

        private void HandleCduSetupPrinterLsk(bool rightSide, int index)
        {
            cduStatusLine = string.Empty;
            if (!rightSide)
            {
                switch (index)
                {
                    case 1: CduCyclePrinter(); break;
                    case 2: PrinterMode = PrinterMode switch
                    {
                        DatalinkPrinterMode.RawEscPos => DatalinkPrinterMode.Windows,
                        DatalinkPrinterMode.Windows => DatalinkPrinterMode.MockFile,
                        _ => DatalinkPrinterMode.RawEscPos
                    }; break;
                    case 3: PrinterProfile = PrinterProfile == DatalinkPrinterProfile.CitizenCtS4000_112Mm
                        ? DatalinkPrinterProfile.GenericEscPos80Mm
                        : DatalinkPrinterProfile.CitizenCtS4000_112Mm; break;
                    case 4: PrinterCutMode = PrinterCutMode switch
                    {
                        DatalinkCutMode.Partial => DatalinkCutMode.Full,
                        DatalinkCutMode.Full => DatalinkCutMode.Off,
                        _ => DatalinkCutMode.Partial
                    }; break;
                    case 6: cduPage = CduPageId.Setup; break;
                }
                return;
            }

            switch (index)
            {
                case 2: PrinterFeedLines = (PrinterFeedLines + 1) % 7; break;
                case 3: RunEmbeddedPrinterTest(); cduStatusLine = "TEST SENT"; break;
            }
        }

        private void CduApplyCidFromScratchpad()
        {
            if (string.IsNullOrWhiteSpace(cduScratchpad))
            {
                SavedCID = 0;
            }
            else if (int.TryParse(cduScratchpad.Trim(), out int cid) && cid > 0)
            {
                SavedCID = cid;
            }
            else
            {
                cduStatusLine = "CID MUST BE NUMERIC";
                cduStatusError = true;
                return;
            }
            cduScratchpad = string.Empty;
            Properties.Settings.Default.Save();
            cduStatusLine = "CID SAVED";
        }

        private void CduApplyTextSetting(Action<string> setter, string name)
        {
            setter(cduScratchpad.Trim());
            cduScratchpad = string.Empty;
            Properties.Settings.Default.Save();
            cduStatusLine = name + " SAVED";
        }

        private void CduCyclePrinter()
        {
            List<string> options = new() { string.Empty };
            options.AddRange(DatalinkPrinter.GetInstalledPrinterNames());
            int current = Math.Max(0, options.FindIndex(p =>
                string.Equals(p, SelectedPrinterName, StringComparison.OrdinalIgnoreCase)));
            SelectedPrinterName = options[(current + 1) % options.Count];
            Properties.Settings.Default.Save();
        }

        // ---- eLoadControl loadsheet ---------------------------------------

        private async void CduOpenLoadControl()
        {
            cduPage = CduPageId.Load;
            cduLoadSession = null;
            cduLoadBusy = true;
            cduStatusLine = "PREPARING...";
            RefreshCduDisplay();

            try
            {
                cduLoadSession = await Vns430PrepareLoadControlAsync();
                cduStatusLine = string.Empty;
            }
            catch (Exception ex)
            {
                cduStatusLine = SafeCduError(ex);
                cduStatusError = true;
            }
            cduLoadBusy = false;
            RefreshCduDisplay();
        }

        private void RenderCduLoad(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            grid.WriteCentered(CduLayout.TitleRow, "LOADSHEET", CduColor.White);

            if (cduLoadSession == null)
            {
                string message = cduLoadBusy
                    ? "PREPARING..."
                    : (string.IsNullOrWhiteSpace(cduStatusLine) ? "NO LOAD DATA" : cduStatusLine);
                grid.WriteCentered(CduLayout.DataRow(3), Truncate(message, CduGrid.Cols),
                    cduLoadBusy ? CduColor.Cyan : CduColor.Amber);
                grid.WriteLeft(CduLayout.DataRow(6), "<RETURN", CduColor.White);
                return;
            }

            Vns430LoadControlSession session = cduLoadSession;
            RenderCduSetupField(grid, 2, false, "AIRCRAFT", Truncate(session.Aircraft.Icao, CduGrid.HalfCols - 1));
            RenderCduSetupField(grid, 3, false, "CABIN", Truncate(session.Cabin, CduGrid.HalfCols - 1));
            RenderCduSetupField(grid, 4, false, "FORMAT",
                Truncate(string.IsNullOrWhiteSpace(session.Format.Name) ? session.Format.TemplateId : session.Format.Name, CduGrid.HalfCols - 1));

            grid.WriteRight(CduLayout.LabelRow(2), "FLIGHT", CduColor.Cyan, small: true);
            grid.WriteRight(CduLayout.DataRow(2), Truncate(snapshot.Callsign, CduGrid.HalfCols), CduColor.White);
            grid.WriteRight(CduLayout.LabelRow(3), "PAX", CduColor.Cyan, small: true);
            grid.WriteRight(CduLayout.DataRow(3), session.Flight.PassengerCount.ToString(), CduColor.White);

            // Simulated ground-crew loading time; the sheet is delivered after this.
            RenderCduSetupField(grid, 4, true, "LOAD TIME", session.LoadingTimeLabel);

            if (!string.IsNullOrWhiteSpace(cduStatusLine))
            {
                grid.WriteCentered(CduLayout.ScratchpadRow, Truncate(cduStatusLine, CduGrid.Cols), CduColor.Amber, small: true);
            }
            grid.WriteLeft(CduLayout.DataRow(6), "<RETURN", CduColor.White);
            grid.WriteRight(CduLayout.DataRow(6), cduLoadBusy ? "WORKING" : "GENERATE>",
                cduLoadBusy ? CduColor.Grey : CduColor.Green, inverse: CduArmed("GENERATE"));
        }

        private void HandleCduLoadLsk(bool rightSide, int index)
        {
            if (cduLoadBusy)
            {
                return;
            }

            if (!rightSide)
            {
                switch (index)
                {
                    case 2: if (cduLoadSession != null) CycleAircraft(cduLoadSession); break;
                    case 3: if (cduLoadSession != null) CycleCabin(cduLoadSession); break;
                    case 4: if (cduLoadSession != null) CycleFormat(cduLoadSession); break;
                    case 6: cduPage = CduPageId.Aoc; break;
                }
                return;
            }

            if (index == 4 && cduLoadSession != null)
            {
                CycleLoadingTime(cduLoadSession);
                return;
            }

            if (index == 6 && cduLoadSession != null)
            {
                // Generating a loadsheet calls the external eLoadControl API, so arm it.
                CduArm("GENERATE", "GENERATE LOADSHEET", CduGenerateLoadsheet);
            }
        }

        private static void CycleLoadingTime(Vns430LoadControlSession s)
        {
            s.LoadingTimeIndex = (s.LoadingTimeIndex + 1) % Vns430LoadControlSession.LoadingTimeMinutes.Length;
        }

        private static void CycleAircraft(Vns430LoadControlSession s)
        {
            int count = s.Reference.Aircraft.Count;
            s.AircraftIndex = ((s.AircraftIndex + 1) % count + count) % count;
            s.CabinIndex = 0;
            s.RebuildPassengerSplit();
        }

        private static void CycleCabin(Vns430LoadControlSession s)
        {
            int count = s.Aircraft.CabinConfigurations.Count;
            s.CabinIndex = ((s.CabinIndex + 1) % count + count) % count;
            s.RebuildPassengerSplit();
        }

        private static void CycleFormat(Vns430LoadControlSession s)
        {
            int count = s.Reference.Formats.Count;
            s.FormatIndex = ((s.FormatIndex + 1) % count + count) % count;
        }

        private async void CduGenerateLoadsheet()
        {
            if (cduLoadSession == null || cduLoadBusy)
            {
                return;
            }

            cduLoadBusy = true;
            cduStatusLine = "GENERATING...";
            RefreshCduDisplay();

            Vns430OperationResult result = await Vns430GenerateLoadsheetAsync(cduLoadSession);

            cduLoadBusy = false;
            cduStatusLine = result.Status;
            cduStatusError = !result.Success;
            if (result.Success)
            {
                cduMsgSent = false;
                cduPage = CduPageId.MessageList;
            }
            RefreshCduDisplay();
        }

        private static string SafeCduError(Exception ex)
        {
            string text = (ex?.Message ?? "FAILED").Trim().ToUpperInvariant();
            return text.Length <= CduGrid.Cols ? text : text.Substring(0, CduGrid.Cols);
        }

        // ---- Boeing CDU keypad --------------------------------------------

        // Handles a physical/on-screen CDU key. Character keys feed the scratchpad; a few
        // Boeing function keys map to our datalink pages and EXEC acts on the current page.
        // The remaining FMC keys are exposed as L-vars for hardware completeness but carry
        // no datalink action.
        private void HandleCduKey(Vns430Command command)
        {
            char ch = CduKeyMap.CharFor(command);
            if (ch != '\0')
            {
                CduScratchpadType(ch);
                return;
            }

            switch (command)
            {
                case Vns430Command.CduClear:
                    // CLR: cancel an armed action, else clear a status/error message (and the
                    // FAIL light), else backspace one scratchpad character.
                    if (cduArmedAction != null)
                    {
                        ClearCduArm();
                        cduStatusLine = string.Empty;
                        cduStatusError = false;
                        RefreshCduDisplay();
                    }
                    else if (!string.IsNullOrEmpty(cduStatusLine))
                    {
                        cduStatusLine = string.Empty;
                        cduStatusError = false;
                        RefreshCduDisplay();
                    }
                    else
                    {
                        CduScratchpadBackspace();
                    }
                    break;
                case Vns430Command.CduDelete:
                    CduScratchpadClearAll();
                    break;
                case Vns430Command.CduPlusMinus:
                    CduScratchpadType('-');
                    break;
                case Vns430Command.CduMenu:
                    ClearCduArm();
                    cduPage = CduPageId.Menu;
                    RefreshCduDisplay();
                    break;
                case Vns430Command.CduPrevPage:
                    if (cduPage == CduPageId.MessageDetail)
                    {
                        cduDetailScroll = Math.Max(0, cduDetailScroll - 6);
                        RefreshCduDisplay();
                    }
                    else if (cduPage == CduPageId.SetupTechnical)
                    {
                        cduTechPage = Math.Max(0, cduTechPage - 1);
                        RefreshCduDisplay();
                    }
                    break;
                case Vns430Command.CduNextPage:
                    if (cduPage == CduPageId.MessageDetail)
                    {
                        cduDetailScroll += 6;   // the render clamps to the last page
                        RefreshCduDisplay();
                    }
                    else if (cduPage == CduPageId.SetupTechnical)
                    {
                        cduTechPage += 1;       // the render clamps to the last page
                        RefreshCduDisplay();
                    }
                    break;
                case Vns430Command.CduExec:
                    // EXEC runs whatever transmit action is armed; nothing happens otherwise.
                    if (cduArmedAction != null)
                    {
                        Action pending = cduArmedAction;
                        ClearCduArm();
                        cduStatusLine = string.Empty;
                        cduStatusError = false;
                        pending();
                        RefreshCduDisplay();
                    }
                    break;
            }
        }

        // ---- Helpers -------------------------------------------------------

        private Vns430MessageSnapshot FindCduSelected(Vns430BackendSnapshot snapshot)
        {
            if (cduSelectedMessage == null)
            {
                return null;
            }
            return snapshot.Messages.FirstOrDefault(m => ReferenceEquals(m.Source, cduSelectedMessage));
        }

        private static CduColor ReplyColour(string response)
        {
            return (response ?? string.Empty).ToUpperInvariant() switch
            {
                "WILCO" or "AFFIRMATIVE" or "ROGER" => CduColor.Green,
                "STANDBY" => CduColor.Amber,
                _ => CduColor.White
            };
        }

        private static string BuildRouteText(Vns430BackendSnapshot snapshot)
        {
            string dep = string.IsNullOrWhiteSpace(snapshot.Departure) ? "----" : snapshot.Departure;
            string arr = string.IsNullOrWhiteSpace(snapshot.Arrival) ? "----" : snapshot.Arrival;
            return dep + "-" + arr;
        }

        private static string Truncate(string value, int width)
        {
            value ??= string.Empty;
            return value.Length <= width ? value : value.Substring(0, width);
        }

        private static List<string> WrapCduText(string text, int width)
        {
            List<string> output = new();
            foreach (string rawLine in (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string remaining = rawLine.Trim();
                if (remaining.Length == 0)
                {
                    output.Add(string.Empty);
                    continue;
                }

                while (remaining.Length > width)
                {
                    int splitAt = remaining.LastIndexOf(' ', Math.Min(width - 1, remaining.Length - 1));
                    if (splitAt < 1)
                    {
                        splitAt = width;
                        output.Add(remaining.Substring(0, splitAt));
                        remaining = remaining.Substring(splitAt);
                        continue;
                    }
                    output.Add(remaining.Substring(0, splitAt).TrimEnd());
                    remaining = remaining.Substring(splitAt + 1);
                }
                output.Add(remaining);
            }
            return output;
        }
    }
}

