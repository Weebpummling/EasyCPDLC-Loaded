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
        private enum CduPageId
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
        private Vns430LoadControlSession cduLoadSession;
        private bool cduLoadBusy;

        // Logon page: LSK index -> candidate logon code.
        private readonly List<string> cduLogonCandidates = new();

        // EXEC arming: a network-transmitting action (send request, logon, REQ CLR,
        // reply, generate loadsheet) is selected first, which highlights it and lights
        // the EXEC annunciator; pressing EXEC then runs it. Local actions stay immediate.
        private Action cduArmedAction;
        private string cduArmedKey = string.Empty;

        // Lamp test: light every side annunciator so their look can be verified/tuned.
        private bool cduAnnunciatorTest;

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
                    grid.WriteRight(CduLayout.DataRow(1), "LOADSHEET>", CduColor.White);
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
                case CduPageId.Load:
                    RenderCduLoad(grid, snapshot);
                    break;
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

        // Shared title row: page name centred, callsign at far left, link state at far right.
        private static void RenderCduHeader(CduGrid grid, string title, Vns430BackendSnapshot snapshot)
        {
            grid.WriteCentered(CduLayout.TitleRow, title, CduColor.White);
            if (!string.IsNullOrWhiteSpace(snapshot.Callsign))
            {
                grid.Write(CduLayout.TitleRow, 0, Truncate(snapshot.Callsign, 7), CduColor.Green, small: true);
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
        }

        // SETUP wants attention when a credential the pilot needs to connect/operate is
        // missing: the Hoppie code and SimBrief are always needed, and the active ATC
        // network needs its own credential (VATSIM -> Hoppie, SI -> SayIntentions key).
        private bool CduSetupNeedsAttention()
        {
            bool hoppieMissing = string.IsNullOrWhiteSpace(SavedHoppieCode);
            bool simbriefMissing = string.IsNullOrWhiteSpace(SimbriefID);
            bool networkMissing = ActiveAtcNetwork == Vns430AtcNetwork.SayIntentions
                ? string.IsNullOrWhiteSpace(SavedSayIntentionsApiKey)
                : string.IsNullOrWhiteSpace(SavedHoppieCode);
            return hoppieMissing || simbriefMissing || networkMissing;
        }

        private void RenderCduDlk(CduGrid grid, Vns430BackendSnapshot snapshot)
        {
            RenderCduHeader(grid, "DLK STATUS", snapshot);

            // Left column: actions on the LSKs.
            grid.WriteLeft(CduLayout.DataRow(1), snapshot.Connected ? "<DISCONNECT" : "<CONNECT",
                snapshot.Connected ? CduColor.Amber : CduColor.Green);
            grid.WriteLeft(CduLayout.DataRow(2), "<RELOAD FP", CduColor.White, inverse: CduArmed("RELOADFP"));
            grid.WriteLeft(CduLayout.DataRow(3), "<PRINT LAST", CduColor.White);
            grid.WriteLeft(CduLayout.DataRow(4), "<REPRINT", CduColor.White);
            grid.WriteLeft(CduLayout.DataRow(5), "<LOGON", CduColor.White);
            grid.WriteLeft(CduLayout.DataRow(6), "<MENU", CduColor.White);

            // Right column: live status read-out (the old top-row info, now in the display).
            RenderCduRightStatus(grid, 1, "VATSIM", snapshot.Connected ? "CONNECTED" : "OFFLINE",
                snapshot.Connected ? CduColor.Green : CduColor.Amber);
            RenderCduRightStatus(grid, 2, "ATS UNIT", string.IsNullOrWhiteSpace(snapshot.CurrentAtcUnit) ? "----" : snapshot.CurrentAtcUnit,
                string.IsNullOrWhiteSpace(snapshot.CurrentAtcUnit) ? CduColor.Grey : (snapshot.AtcUnitOnline ? CduColor.Green : CduColor.Amber));
            RenderCduRightStatus(grid, 3, "ROUTE", BuildRouteText(snapshot), CduColor.White);
            RenderCduRightStatus(grid, 4, "LOGON", string.IsNullOrWhiteSpace(snapshot.PendingLogon) ? "----" : snapshot.PendingLogon, CduColor.Cyan);
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
            grid.WriteLeft(CduLayout.DataRow(3), "<CLEAR ALL MSG", CduColor.White, inverse: CduArmed("CLEARALL"));
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
                string station = string.IsNullOrWhiteSpace(message.Station) ? message.Type : message.Station;

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
            string station = string.IsNullOrWhiteSpace(message.Station) ? message.Type : message.Station;
            grid.WriteCentered(CduLayout.TitleRow, Truncate(station, 18), CduColor.White);
            grid.WriteRight(CduLayout.TitleRow, message.Outbound ? "SENT" : "RCVD", CduColor.Cyan, small: true);

            // Body text wrapped across the upper rows (1..6), clear of the bottom LSKs.
            // Long messages (e.g. an eLoadControl loadsheet) scroll with PREV/NEXT PAGE.
            const int bodyRows = 6;
            List<string> lines = WrapCduText(message.Text, CduGrid.Cols);
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

            // Bottom-right LSKs (4,5,6): print actions + return.
            grid.WriteRight(CduLayout.DataRow(4), "PRINT>", CduColor.White);
            grid.WriteRight(CduLayout.DataRow(5), "REPRINT>", CduColor.White);
            grid.WriteRight(CduLayout.DataRow(6), "RETURN>", CduColor.White);
        }

        // ---- LSK handlers --------------------------------------------------

        private void HandleCduMenuLsk(bool rightSide, int index)
        {
            if (rightSide)
            {
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

        private void HandleCduDlkLsk(bool rightSide, int index)
        {
            if (rightSide)
            {
                return;
            }

            switch (index)
            {
                case 1: Vns430ToggleVatsimConnection(); break;
                case 2:
                    // Reloading the flight plan wipes the inbox for the new leg, so arm it.
                    CduArm("RELOADFP", "RELOAD FP + CLEAR", () =>
                    {
                        ReloadFlightPlanButton_Click(mainReloadFlightPlanButton, EventArgs.Empty);
                        DeleteAllElement(this, EventArgs.Empty);
                        cduStatusLine = "FP RELOADED";
                    });
                    break;
                case 3: PrintButton_Click(refreshButtonVisual, EventArgs.Empty); break;
                case 4: ReprintButton_Click(boeingReprintButton, EventArgs.Empty); break;
                case 5:
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
            string unit = string.IsNullOrWhiteSpace(snapshot.CurrentAtcUnit) ? "----" : snapshot.CurrentAtcUnit;
            grid.WriteRight(CduLayout.LabelRow(1), "LOGGED ON", CduColor.Cyan, small: true);
            grid.WriteRight(CduLayout.DataRow(1), unit,
                string.IsNullOrWhiteSpace(snapshot.CurrentAtcUnit) ? CduColor.Grey : (snapshot.AtcUnitOnline ? CduColor.Green : CduColor.Amber));

            string pdc = string.IsNullOrWhiteSpace(snapshot.PdcStatus) ? "----" : snapshot.PdcStatus;
            if (!string.IsNullOrWhiteSpace(snapshot.PdcLogonCode))
            {
                pdc += " " + snapshot.PdcLogonCode;
            }
            grid.WriteRight(CduLayout.LabelRow(2), "PDC", CduColor.Cyan, small: true);
            grid.WriteRight(CduLayout.DataRow(2), Truncate(pdc, CduGrid.HalfCols),
                snapshot.PdcAllowReqClr ? CduColor.Green : CduColor.White);

            if (snapshot.PdcAllowReqClr)
            {
                grid.WriteRight(CduLayout.DataRow(3), "REQ CLR>", CduColor.Green, inverse: CduArmed("REQCLR"));
            }

            // Left column: online CPDLC logon candidates on LSK 1..4.
            cduLogonCandidates.Clear();
            List<Vns430CpdlcCandidate> candidates = snapshot.CpdlcCandidates.Take(4).ToList();
            for (int i = 0; i < candidates.Count; i++)
            {
                cduLogonCandidates.Add(candidates[i].Code);
                grid.WriteLeft(CduLayout.LabelRow(i + 1), Truncate(candidates[i].Reason, CduGrid.HalfCols), CduColor.Cyan, small: true);
                grid.WriteLeft(CduLayout.DataRow(i + 1),
                    "<" + Truncate(candidates[i].Code + " " + candidates[i].Controller, CduGrid.HalfCols - 1),
                    candidates[i].TunedMatch ? CduColor.Green : CduColor.White, inverse: CduArmed("LOGON:" + i));
            }
            if (candidates.Count == 0)
            {
                grid.WriteLeft(CduLayout.DataRow(2), " NO CPDLC ATC FOUND", CduColor.Grey);
            }

            // Manual code entry via the scratchpad, then return.
            grid.WriteLeft(CduLayout.LabelRow(5), "MANUAL LOGON", CduColor.Cyan, small: true);
            grid.WriteLeft(CduLayout.DataRow(5), "<LOGON", CduColor.White, inverse: CduArmed("LOGON:M"));
            grid.WriteLeft(CduLayout.DataRow(6), "<RETURN", CduColor.White);

            RenderCduScratchpad(grid);
        }

        private void HandleCduLogonLsk(bool rightSide, int index)
        {
            cduStatusLine = string.Empty;
            if (rightSide)
            {
                if (index == 3)
                {
                    // REQ CLR: arm the PDC clearance; EXEC transmits it.
                    if (CanQuickRequestClearance())
                    {
                        CduArm("REQCLR", "REQ CLR", () =>
                        {
                            _ = QuickRequestPredepClearanceAsync();
                            cduStatusLine = "REQ CLR SENT";
                        });
                    }
                    else
                    {
                        cduStatusLine = "REQ CLR NOT AVAIL";
                        cduStatusError = true;
                    }
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
                        CduLogonTo(cduLogonCandidates[position], "LOGON:" + position);
                    }
                    break;
                case 5:
                    CduLogonTo(cduScratchpad, "LOGON:M");
                    break;
                case 6:
                    cduPage = CduPageId.Dlk;
                    break;
            }
        }

        private void CduLogonTo(string code, string armKey)
        {
            string clean = (code ?? string.Empty).Trim().ToUpperInvariant();
            if (clean.Length < 3)
            {
                cduStatusLine = "ENTER 3-4 CHAR CODE";
                cduStatusError = true;
                return;
            }

            cduScratchpad = string.Empty;
            CduArm(armKey, "LOGON " + clean, () =>
            {
                cduStatusLine = "LOGON SENT " + clean;
                _ = Vns430RequestLogonAsync(clean);
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
                case 6: cduPage = CduPageId.MessageList; break;
            }
        }

        // ---- ATC / AOC request pages --------------------------------------

        private static readonly (string Label, Vns430WorkflowKind Kind)[] CduAtcMenuItems =
        {
            ("DIRECT TO", Vns430WorkflowKind.AtcDirect),
            ("LEVEL", Vns430WorkflowKind.AtcLevel),
            ("SPEED", Vns430WorkflowKind.AtcSpeed),
            ("WHEN CAN WE", Vns430WorkflowKind.AtcWhenCanWe),
            ("FREE TEXT", Vns430WorkflowKind.AtcFreeText)
        };

        private static readonly (string Label, Vns430WorkflowKind Kind)[] CduAocMenuItems =
        {
            ("TELEX", Vns430WorkflowKind.AocTelex),
            ("METAR", Vns430WorkflowKind.AocMetar),
            ("ATIS", Vns430WorkflowKind.AocAtis),
            ("PDC", Vns430WorkflowKind.AocPreDeparture),
            ("OCEANIC", Vns430WorkflowKind.AocOceanic)
        };

        private void RenderCduRequestMenu(CduGrid grid, Vns430BackendSnapshot snapshot, string title,
            (string Label, Vns430WorkflowKind Kind)[] items)
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

        private void HandleCduAocLsk(bool rightSide, int index)
        {
            if (rightSide && index == 1)
            {
                CduOpenLoadControl();
                return;
            }
            HandleCduRequestMenuSelection(rightSide, index, CduAocMenuItems);
        }

        private void HandleCduRequestMenuSelection(bool rightSide, int index,
            (string Label, Vns430WorkflowKind Kind)[] items)
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
                cduWorkflow = Vns430Workflow.Create(items[position].Kind, GetVns430Snapshot());
                cduScratchpad = string.Empty;
                cduStatusLine = string.Empty;
                cduPage = CduPageId.Request;
            }
        }

        // Request fields fill left LSK 1..5 then right LSK 1..5 (ten slots). LSK6 is
        // RETURN / SEND; the scratchpad is the dedicated bottom row.
        private static (bool RightSide, int Lsk) CduFieldSlot(int fieldIndex)
        {
            return fieldIndex < 5 ? (false, fieldIndex + 1) : (true, (fieldIndex - 5) + 1);
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

            for (int i = 0; i < cduWorkflow.Fields.Count && i < 8; i++)
            {
                Vns430EditField field = cduWorkflow.Fields[i];
                (bool right, int lsk) = CduFieldSlot(i);
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

            int fieldIndex = rightSide ? 5 + (index - 1) : index - 1;
            if (fieldIndex < 0 || fieldIndex >= cduWorkflow.Fields.Count)
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

        private bool CduScratchpadActive() =>
            cduPage is CduPageId.Request or CduPageId.Logon or CduPageId.SetupAccount or CduPageId.SetupPrinter;

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

            // Right column: instrument selector (CDU <-> GNS430) plus the network/weather
            // cycles. The Airbus/Boeing skins are hidden, so the instrument choices are the
            // CDU and the GNS430 (VNS430) panel.
            RenderCduSetupField(grid, 1, true, "INSTRUMENT", InstrumentText());
            RenderCduSetupField(grid, 2, true, "ATC NETWORK", AtcNetworkText());
            RenderCduSetupField(grid, 3, true, "WX SOURCE", WxSourceText());

            grid.WriteLeft(CduLayout.DataRow(6), "<MENU", CduColor.White);
        }

        private static string AtcNetworkText() =>
            ActiveAtcNetwork == Vns430AtcNetwork.SayIntentions ? "SI" : "VATSIM";

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
            RenderCduAccountField(grid, 5, "SAYINTENTIONS KEY", SavedSayIntentionsApiKey, secret: true);

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
            List<(string Text, CduColor Colour, bool Small)> rows = new();

            void AddEntry(string label, string value)
            {
                rows.Add((label, CduColor.Cyan, true));
                if (string.IsNullOrWhiteSpace(value))
                {
                    rows.Add(("----", CduColor.Grey, false));
                    return;
                }
                foreach (string line in WrapCduText(value, CduGrid.Cols))
                {
                    rows.Add((line, CduColor.Green, false));
                }
            }

            AddEntry("VATSIM CID", SavedCID > 0 ? SavedCID.ToString() : null);
            AddEntry("HOPPIE CODE", SavedHoppieCode);
            AddEntry("SIMBRIEF", SimbriefID);
            AddEntry("ELOAD KEY", SavedELoadControlApiKey);
            AddEntry("SAYINTENTIONS KEY", SavedSayIntentionsApiKey);
            AddEntry("ATC NETWORK", AtcNetworkText());
            AddEntry("WX SOURCE", WxSourceText() + " (" + Vns430WeatherClient.SourceLabel(EffectiveWxSource()) + ")");
            AddEntry("PRINTER", SelectedPrinterName);

            int pageCount = Math.Max(1, (rows.Count + CduTechContentRows - 1) / CduTechContentRows);
            cduTechPage = Math.Clamp(cduTechPage, 0, pageCount - 1);

            string title = pageCount > 1 ? "TECHNICAL " + (cduTechPage + 1) + "/" + pageCount : "TECHNICAL";
            grid.WriteCentered(CduLayout.TitleRow, title, CduColor.White);

            int start = cduTechPage * CduTechContentRows;
            for (int i = 0; i < CduTechContentRows; i++)
            {
                int idx = start + i;
                if (idx >= rows.Count)
                {
                    break;
                }
                (string text, CduColor colour, bool small) = rows[idx];
                grid.Write(i + 1, 0, Truncate(text, CduGrid.Cols), colour, small: small);
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

        private static void RenderCduSetupField(CduGrid grid, int lsk, bool right, string label, string value)
        {
            bool empty = string.IsNullOrWhiteSpace(value);
            CduColor colour = empty ? CduColor.Grey : CduColor.Green;
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
                    case 6: cduPage = CduPageId.Menu; break;
                }
                return;
            }

            switch (index)
            {
                case 1: CduCycleInstrument(); break;
                case 2: CduCycleAtcNetwork(); break;
                case 3: CduCycleWxSource(); break;
            }
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

        private void CduCycleAtcNetwork()
        {
            ActiveAtcNetwork = ActiveAtcNetwork == Vns430AtcNetwork.Vatsim
                ? Vns430AtcNetwork.SayIntentions
                : Vns430AtcNetwork.Vatsim;
            Properties.Settings.Default.Save();
            cduStatusLine = "ATC NETWORK " + AtcNetworkText();
        }

        // AUTO (follow network) -> VATSIM -> REAL WORLD -> SAYINTENTIONS -> AUTO.
        private void CduCycleWxSource()
        {
            string current = SavedWxSourceOverride;
            SavedWxSourceOverride = current switch
            {
                "" or null => "VATSIM",
                "VATSIM" => "REAL WORLD",
                "REAL WORLD" => "SAYINTENTIONS",
                _ => string.Empty
            };
            Properties.Settings.Default.Save();
            cduStatusLine = "WX SOURCE " + WxSourceText();
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
                case 5: CduApplyTextSetting(v => SavedSayIntentionsApiKey = v, "SAYINTENTIONS KEY"); break;
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

            if (index == 6 && cduLoadSession != null)
            {
                // Generating a loadsheet calls the external eLoadControl API, so arm it.
                CduArm("GENERATE", "GENERATE LOADSHEET", CduGenerateLoadsheet);
            }
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

