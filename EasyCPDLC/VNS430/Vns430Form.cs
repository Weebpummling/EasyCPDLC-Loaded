using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EasyCPDLC.VNS430
{
    internal sealed class Vns430Form : Form
    {
        private const int LogicalWidth = 960;
        private const int LogicalHeight = 407;
        private const int WmAppSimConnect = 0x8430;

        // WM_SIZING and its wParam edge codes, used to keep the panel's aspect ratio.
        private const int WmSizing = 0x0214;
        private const int WmszLeft = 1;
        private const int WmszRight = 2;
        private const int WmszTop = 3;
        private const int WmszTopLeft = 4;
        private const int WmszTopRight = 5;
        private const int WmszBottom = 6;
        private const int WmszBottomLeft = 7;
        private const int WmszBottomRight = 8;

        // Screen mode collapses the whole non-client frame (title bar and sizing
        // border) via WM_NCCALCSIZE so no chrome is drawn, then does its own edge
        // hit-testing for resize. The window handle is never recreated, so the
        // companion SimConnect registration survives the toggle.
        private const int WmNcHitTest = 0x0084;
        private const int WmNcCalcSize = 0x0083;
        private const int HtClient = 1;
        private const int HtCaption = 2;
        private const int WmNcLButtonDown = 0x00A1;
        private const double DefaultLoadScale = 1.4;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;
        private const int ScreenModeResizeBorder = 6;
        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private static readonly Color BezelDark = Color.FromArgb(105, 108, 103);
        private static readonly Color BezelLight = Color.FromArgb(202, 202, 190);
        private static readonly Color ScreenBlue = Color.FromArgb(0, 61, 128);
        private static readonly Color ScreenBlueTop = Color.FromArgb(0, 78, 148);
        private static readonly Color ScreenWhite = Color.FromArgb(235, 244, 229);
        private static readonly Color ScreenGreen = Color.FromArgb(88, 255, 118);
        private static readonly Color ScreenCyan = Color.FromArgb(90, 226, 255);
        private static readonly Color ScreenAmber = Color.FromArgb(255, 232, 0);
        private static readonly Color ScreenMagenta = Color.FromArgb(255, 82, 215);

        private static readonly RectangleF ScreenBounds = new(235, 55, 518, 277);
        private static readonly RectangleF MainScreenBounds = ScreenBounds;

        private readonly MainForm backend;
        private readonly Vns430Preferences preferences;
        private readonly Vns430CompanionInput companionInput = new();
        private readonly Timer refreshTimer = new() { Interval = 100 };
        private readonly ToolTip toolTip = new();
        private readonly Vns430PanelArtwork artwork = new();
        private readonly List<PanelButton> panelButtons;
        private readonly FontFamily displayFontFamily;

        private Vns430BackendSnapshot snapshot = new();
        private Vns430Page page = Vns430Page.Status;
        private Vns430Page pageBeforeOverlay = Vns430Page.Status;
        private Vns430PageGroup pageGroup = Vns430PageGroup.Nav;
        private Vns430PageGroup groupBeforeOverlay = Vns430PageGroup.Nav;
        private int selectedIndex;
        private int detailScrollLine;
        private int responseIndex;
        private int refreshTick;
        private int zoomLevel = 1;
        private bool cursorActive;
        private string logonCode = "____";
        private int logonCharacter;
        private string transientStatus = string.Empty;
        private DateTime transientStatusUntilUtc = DateTime.MinValue;
        private Bitmap cachedLcd;
        private ulong cachedLcdFingerprint;
        private bool artworkFeedbackPainted;
        private string activeArtworkControl = string.Empty;
        private string activeArtworkState = string.Empty;
        private DateTime activeArtworkUntilUtc = DateTime.MinValue;
        private PanelButton pressedPanelButton;
        private bool pressedCursor;
        private bool pressedScreen;
        private Point screenPressOrigin;
        private bool screenDragging;
        private bool clearAllArmed;   // GNS430 menu CLEAR ALL MESSAGES confirm state
        private int messageFilter;    // 0 = ALL, 1 = RECEIVED, 2 = SENT
        private static readonly string[] MessageFilters = { "ALL", "RECEIVED", "SENT" };
        private Vns430Workflow workflow;
        private int workflowCharacter;
        private Vns430LoadControlSession loadSession;
        private bool operationBusy;
        private string operationStatus = string.Empty;

        private static readonly string[] AtcMenuItems =
        {
            "DIRECT TO", "LEVEL", "SPEED", "WHEN CAN WE", "FREE TEXT", "POSITION REP"
        };

        private static readonly string[] AocMenuItems =
        {
            "AOC TELEX", "METAR", "ATIS", "PREDEP CLEARANCE", "OCEANIC CLEARANCE", "LOAD CONTROL"
        };

        private sealed class PanelButton
        {
            internal RectangleF Bounds { get; init; }
            internal string Label { get; init; } = string.Empty;
            internal string ArtworkControl { get; init; } = string.Empty;
            internal Vns430Command Command { get; init; }
        }

        internal Vns430Form(MainForm backend)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            preferences = Vns430Preferences.Load();

            Text = "EasyCPDLC - GNS430 Datalink";
            StartPosition = FormStartPosition.Manual;
            // Aspect-correct minimum. The form is borderless, so window size == client
            // size; a minimum that does not match the panel aspect would block the
            // normalisation below from ever reaching the right height.
            MinimumSize = new Size(730, (int)Math.Round(730 * (LogicalHeight / (double)LogicalWidth)));
            // A larger default so the GNS430 is comfortably readable on load (only used the
            // first time, before a saved size exists).
            ClientSize = new Size((int)(LogicalWidth * DefaultLoadScale), (int)(LogicalHeight * DefaultLoadScale));
            BackColor = BezelDark;
            DoubleBuffered = true;
            // The whole panel is drawn through a size-dependent ScaleTransform, so a
            // resize that repaints only the newly exposed region leaves the rest at the
            // old scale and visibly corrupts the panel. Force a full repaint on resize.
            ResizeRedraw = true;
            TopMost = true;
            Icon = backend.Icon;
            // Borderless: no title bar. The window is dragged by its bezel (and by the LCD
            // via a small drag threshold) and resized from the edges, handled in WndProc.
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;

            Rectangle saved = new(preferences.Left, preferences.Top, preferences.Width, preferences.Height);
            if (preferences.Left >= 0 && preferences.Top >= 0 && Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(saved)))
            {
                Bounds = saved;
            }
            else
            {
                Rectangle work = Screen.FromControl(backend).WorkingArea;
                Location = new Point(
                    Math.Max(work.Left, work.Right - Width - 24),
                    Math.Max(work.Top, work.Bottom - Height - 24));
            }

            NormalizeClientAspect();

            displayFontFamily = Vns430FontLoader.Family;
            panelButtons = CreatePanelButtons();

            companionInput.CommandReceived += command => BeginInvoke(new Action(() => HandleCompanionCommand(command)));
            refreshTimer.Tick += RefreshTimerTick;
            refreshTimer.Start();

            Paint += PaintPanel;
            MouseDown += PanelMouseDown;
            MouseUp += PanelMouseUp;
            MouseMove += PanelMouseMove;
            MouseWheel += PanelMouseWheel;
            MouseCaptureChanged += PanelMouseCaptureChanged;
            FormClosing += PanelFormClosing;
            VisibleChanged += (_, __) =>
            {
                if (Visible)
                {
                    RefreshSnapshot();
                    Invalidate();
                }
                else if (IsHandleCreated && WindowState == FormWindowState.Normal)
                {
                    // Persist size/position when the panel is hidden (e.g. switching back to
                    // the CDU instrument), not just on close.
                    preferences.Save(Bounds);
                }
            };
        }

        // Force the client box onto the panel's aspect ratio.
        //
        // The form is borderless, so window size == client size. A bounds value saved by
        // an older bordered build (and the legacy 960x455 default) still carries the old
        // title-bar and border thickness inside it, so restoring it verbatim produced a
        // squashed panel until the first manual resize, where WM_SIZING finally applied
        // ConstrainToPanelAspect. Normalising on load fixes the launch size.
        //
        // Screen mode is exempt: it letterboxes the LCD and is free to be any shape.
        private void NormalizeClientAspect()
        {
            if (preferences.ScreenOnlyMode)
            {
                return;
            }

            int width = Math.Max(MinimumSize.Width, ClientSize.Width);
            int height = (int)Math.Round(width * (LogicalHeight / (double)LogicalWidth));
            if (ClientSize.Width != width || ClientSize.Height != height)
            {
                ClientSize = new Size(width, height);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (preferences.CompanionModuleEnabled)
            {
                companionInput.TryEnable(Handle, WmAppSimConnect, out _);
            }

            // Honour a persisted screen-mode choice on startup.
            if (preferences.ScreenOnlyMode)
            {
                ApplyScreenModeChrome();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refreshTimer.Dispose();
                companionInput.Dispose();
                toolTip.Dispose();
                artwork.Dispose();
                cachedLcd?.Dispose();
                cachedLcd = null;
            }

            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmAppSimConnect)
            {
                companionInput.ReceiveWindowMessage();
                return;
            }

            // Panel mode keeps the fixed photographic aspect; screen mode letterboxes the
            // LCD so it can resize freely.
            if (m.Msg == WmSizing && !preferences.ScreenOnlyMode)
            {
                ConstrainToPanelAspect(ref m);
                return;
            }

            // The window is borderless (no title bar) in both modes, so we hit-test the
            // edges ourselves for resizing and treat the body as a caption for dragging.
            if (m.Msg == WmNcHitTest)
            {
                m.Result = (IntPtr)HitTestBorderless();
                return;
            }

            base.WndProc(ref m);
        }

        // Edge = resize, body = drag. In screen mode the whole body drags. In panel mode
        // the interactive controls (buttons, right knob, LCD) stay client so clicks work;
        // the surrounding bezel drags, and the LCD also drags past a small move threshold
        // (handled in PanelMouseMove) so it can still be clicked to select.
        private int HitTestBorderless()
        {
            Point point = PointToClient(Cursor.Position);
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            bool left = point.X <= ScreenModeResizeBorder;
            bool right = point.X >= width - ScreenModeResizeBorder;
            bool top = point.Y <= ScreenModeResizeBorder;
            bool bottom = point.Y >= height - ScreenModeResizeBorder;

            if (top && left) return HtTopLeft;
            if (top && right) return HtTopRight;
            if (bottom && left) return HtBottomLeft;
            if (bottom && right) return HtBottomRight;
            if (left) return HtLeft;
            if (right) return HtRight;
            if (top) return HtTop;
            if (bottom) return HtBottom;

            if (preferences.ScreenOnlyMode)
            {
                return HtCaption;
            }

            // Panel mode: interactive spots stay client so PanelMouseDown/Up handle them.
            PointF logical = ToLogicalPoint(point);
            if (panelButtons.Any(button => button.Bounds.Contains(logical)))
            {
                return HtClient;
            }
            if (KnobPushCommandAt(logical, new PointF(878, 326)) == Vns430Command.CursorPush)
            {
                return HtClient;
            }
            if (ScreenBounds.Contains(logical))
            {
                return HtClient;   // click selects; PanelMouseMove promotes a drag
            }
            return HtCaption;      // bezel drags the window
        }

        // The panel is a fixed-aspect photograph drawn through a ScaleTransform, so a
        // free resize stretches it. Constrain the drag rectangle to the LCD's client
        // aspect while the edge is being dragged, keeping the grabbed edge anchored.
        private void ConstrainToPanelAspect(ref Message m)
        {
            Rect rect = Marshal.PtrToStructure<Rect>(m.LParam);
            int edge = m.WParam.ToInt32();

            // Border and caption thickness, so the aspect applies to the client area.
            Size nonClient = Size - ClientSize;
            double aspect = LogicalWidth / (double)LogicalHeight;

            int clientWidth = Math.Max(1, (rect.Right - rect.Left) - nonClient.Width);
            int clientHeight = Math.Max(1, (rect.Bottom - rect.Top) - nonClient.Height);

            // Dragging a vertical edge lets height drive; anything else lets width drive.
            if (edge == WmszTop || edge == WmszBottom)
            {
                clientWidth = (int)Math.Round(clientHeight * aspect);
            }
            else
            {
                clientHeight = (int)Math.Round(clientWidth / aspect);
            }

            int windowWidth = clientWidth + nonClient.Width;
            int windowHeight = clientHeight + nonClient.Height;

            // Honour MinimumSize without warping: scale both axes up together.
            if (windowWidth < MinimumSize.Width || windowHeight < MinimumSize.Height)
            {
                double scale = Math.Max(
                    MinimumSize.Width / (double)windowWidth,
                    MinimumSize.Height / (double)windowHeight);
                clientWidth = (int)Math.Round(clientWidth * scale);
                clientHeight = (int)Math.Round(clientHeight * scale);
                windowWidth = clientWidth + nonClient.Width;
                windowHeight = clientHeight + nonClient.Height;
            }

            if (edge == WmszLeft || edge == WmszTopLeft || edge == WmszBottomLeft)
            {
                rect.Left = rect.Right - windowWidth;
            }
            else
            {
                rect.Right = rect.Left + windowWidth;
            }

            if (edge == WmszTop || edge == WmszTopLeft || edge == WmszTopRight)
            {
                rect.Top = rect.Bottom - windowHeight;
            }
            else
            {
                rect.Bottom = rect.Top + windowHeight;
            }

            Marshal.StructureToPtr(rect, m.LParam, false);
            m.Result = (IntPtr)1;
        }

        private void RefreshTimerTick(object sender, EventArgs e)
        {
            if (preferences.CompanionModuleEnabled && !companionInput.Enabled && refreshTick % 50 == 0)
            {
                companionInput.TryEnable(Handle, WmAppSimConnect, out _);
            }

            companionInput.UpdateStatus(
                snapshot, page, cursorActive, preferences.DcduCompanionMode,
                backend.CduCompanionStatusFlags(snapshot));

            // Sim-fed flight phase: keeps departure/destination prefills working on SI,
            // where there is no VATSIM position feed to drive the phase engine.
            if (companionInput.TryGetTelemetry(out double altitudeFt, out bool onGround))
            {
                backend.UpdateSimFlightPhase(altitudeFt, onGround);
            }
            refreshTick += 1;

            // Hidden with no hardware bridge attached (e.g. the CDU is the active
            // instrument): nothing consumes the snapshot or the LCD, so skip the per-tick
            // rebuild. Showing the panel refreshes immediately via VisibleChanged.
            if (!Visible && !companionInput.Enabled)
            {
                return;
            }

            if (refreshTick % 3 == 0)
            {
                RefreshSnapshot();
            }

            // Comparing the fingerprint every tick, rather than only when the snapshot
            // is refreshed, means any state change repaints within one tick even if an
            // input path forgets to invalidate. At ~2 us against ~1.7 ms for a render,
            // the hash is roughly 700 times cheaper than what it avoids.
            Vns430LcdState state = CreateLcdState();
            ulong fingerprint = state.Fingerprint();
            bool displayChanged = cachedLcd == null || fingerprint != cachedLcdFingerprint;
            if (refreshTick % 3 == 0)
            {
                companionInput.UpdateDisplay(CurrentLcd(state, fingerprint));
            }

            // Button press artwork is time-limited and sits outside the LCD, so it
            // needs its own repaint, plus one more once it expires to clear it.
            bool artworkFeedbackVisible = DateTime.UtcNow < activeArtworkUntilUtc;
            if (displayChanged || artworkFeedbackVisible || artworkFeedbackPainted)
            {
                artworkFeedbackPainted = artworkFeedbackVisible;
                Invalidate();
            }
        }

        /// <summary>
        /// The rendered LCD, re-rendered only when something it displays has changed.
        /// The returned bitmap is owned by the form and must not be disposed by callers.
        /// </summary>
        private Bitmap CurrentLcd()
        {
            Vns430LcdState state = CreateLcdState();
            return CurrentLcd(state, state.Fingerprint());
        }

        private Bitmap CurrentLcd(Vns430LcdState state, ulong fingerprint)
        {
            if (cachedLcd != null && fingerprint == cachedLcdFingerprint)
            {
                return cachedLcd;
            }

            // Render before disposing, so a failed render cannot leave a disposed
            // bitmap behind for the next paint.
            Bitmap rendered = Vns430LcdRenderer.Render(state);
            cachedLcd?.Dispose();
            cachedLcd = rendered;
            cachedLcdFingerprint = fingerprint;
            return cachedLcd;
        }

        private void RefreshSnapshot()
        {
            snapshot = backend.GetVns430Snapshot();
            if (!ShouldPreserveMessageSelection(page))
            {
                return;
            }

            int visibleCount = VisibleMessages().Count;
            if (visibleCount == 0)
            {
                selectedIndex = 0;
            }
            else
            {
                selectedIndex = Math.Clamp(selectedIndex, 0, visibleCount - 1);
            }
        }

        internal void ExecuteCommand(Vns430Command command)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ExecuteCommand(command)));
                return;
            }

            SetArtworkFeedback(command);

            switch (command)
            {
                case Vns430Command.LargeRightDecrease:
                    RotateLarge(-1);
                    break;
                case Vns430Command.LargeRightIncrease:
                    RotateLarge(1);
                    break;
                case Vns430Command.SmallRightDecrease:
                    RotateSmall(-1);
                    break;
                case Vns430Command.SmallRightIncrease:
                    RotateSmall(1);
                    break;
                case Vns430Command.CursorPush:
                case Vns430Command.Obs:
                    ToggleCursor();
                    break;
                case Vns430Command.Enter:
                    ActivateSelection();
                    break;
                case Vns430Command.Clear:
                    ClearOrBack();
                    break;
                case Vns430Command.Menu:
                    ToggleMenu();
                    break;
                case Vns430Command.Message:
                    // First press opens the inbox; pressing MSG again cycles the
                    // ALL -> RECEIVED -> SENT filter (the CDU's RECEIVED/SENT split).
                    if (page == Vns430Page.Messages)
                    {
                        CycleMessageFilter();
                    }
                    else
                    {
                        OpenMessages(true);
                    }
                    break;
                case Vns430Command.Nearest:
                    OpenMessages(false);
                    break;
                case Vns430Command.FlightPlan:
                    SetPage(Vns430Page.AtcMenu, true, Vns430PageGroup.Wpt);
                    break;
                case Vns430Command.Procedure:
                    SetPage(Vns430Page.AocMenu, true, Vns430PageGroup.Aux);
                    break;
                case Vns430Command.DirectTo:
                    PrefillLogonFromBestCandidate();
                    SetPage(Vns430Page.Logon, true, Vns430PageGroup.Wpt);
                    break;
                case Vns430Command.Cdi:
                    backend.Vns430ToggleVatsimConnection();
                    SetTransient(snapshot.VatsimConnected ? "DISCONNECTING" : "CONNECTING");
                    break;
                case Vns430Command.RangeIn:
                    zoomLevel = Math.Min(2, zoomLevel + 1);
                    break;
                case Vns430Command.RangeOut:
                    zoomLevel = Math.Max(0, zoomLevel - 1);
                    break;
                case Vns430Command.Power:
                    if (Visible)
                    {
                        Hide();
                    }
                    else
                    {
                        Show();
                        BringToFront();
                    }
                    break;
            }

            Invalidate();
        }

        private void RotateLarge(int direction)
        {
            if ((page == Vns430Page.AtcRequest || page == Vns430Page.AocRequest) && workflow != null)
            {
                MoveWorkflowCursor(direction);
                return;
            }

            if (page == Vns430Page.LoadControl && loadSession != null && !operationBusy)
            {
                selectedIndex = Wrap(selectedIndex + direction, loadSession.FieldCount);
                return;
            }

            if (page == Vns430Page.Logon && cursorActive)
            {
                logonCharacter = Wrap(logonCharacter + direction, 4);
                return;
            }

            if (page == Vns430Page.MessageDetail)
            {
                detailScrollLine = Math.Max(0, detailScrollLine + direction);
                return;
            }

            if (page == Vns430Page.Messages)
            {
                int visibleCount = VisibleMessages().Count;
                if (cursorActive && visibleCount > 0)
                {
                    selectedIndex = Wrap(selectedIndex + direction, visibleCount);
                }
                else if (!cursorActive)
                {
                    CyclePageGroup(direction);
                }
                return;
            }

            if (page == Vns430Page.Menu)
            {
                selectedIndex = Wrap(selectedIndex + direction, MenuItems().Count);
                return;
            }

            if (page == Vns430Page.AtcMenu || page == Vns430Page.AocMenu)
            {
                int count = page == Vns430Page.AtcMenu ? AtcMenuItems.Length : AocMenuItems.Length;
                selectedIndex = Wrap(selectedIndex + direction, count);
                return;
            }

            if (page == Vns430Page.Help)
            {
                if (cursorActive)
                {
                    // UTILITY draws a selection bar over a fixed list, so the large knob
                    // moves that selection. It used to step detailScrollLine, which the
                    // page never reads, leaving the bar stuck on the first line.
                    selectedIndex = Wrap(selectedIndex + direction, Vns430LcdRenderer.HelpItems.Length);
                }
                else
                {
                    CyclePageGroup(direction);
                }
                return;
            }

            if (cursorActive)
            {
                selectedIndex = Wrap(selectedIndex + direction, 4);
            }
            else
            {
                CyclePageGroup(direction);
            }
        }

        private void RotateSmall(int direction)
        {
            if ((page == Vns430Page.AtcRequest || page == Vns430Page.AocRequest) && workflow != null)
            {
                workflow.Fields[Math.Clamp(selectedIndex, 0, workflow.Fields.Count - 1)].Step(workflowCharacter, direction);
                return;
            }

            if (page == Vns430Page.LoadControl && loadSession != null && !operationBusy)
            {
                StepLoadControlField(direction);
                return;
            }

            if (page == Vns430Page.Logon && cursorActive)
            {
                char[] characters = "_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();
                char current = logonCode[logonCharacter];
                int characterIndex = Array.IndexOf(characters, current);
                characterIndex = Wrap(characterIndex + direction, characters.Length);
                char[] code = logonCode.ToCharArray();
                code[logonCharacter] = characters[characterIndex];
                logonCode = new string(code);
                return;
            }

            if (page == Vns430Page.MessageDetail)
            {
                Vns430MessageSnapshot message = SelectedMessage();
                if (message?.Responses.Count > 0)
                {
                    responseIndex = Wrap(responseIndex + direction, message.Responses.Count);
                }
                return;
            }

            // On the list pages the cursor is switched on as soon as the page opens and
            // the large knob owns the selection, so the small knob has no editing job to
            // do. Without this, the usual "cursor captures the knobs" rule below left it
            // dead and there was no way to page off the ATC or AOC menu at all.
            // Vns430Page.Menu is deliberately excluded: it is an overlay that belongs to
            // no group, so paging from it would throw the user onto an unrelated page.
            if (page == Vns430Page.AtcMenu || page == Vns430Page.AocMenu || page == Vns430Page.Help)
            {
                CyclePageWithinGroup(direction);
                return;
            }

            if (cursorActive)
            {
                return;
            }

            CyclePageWithinGroup(direction);
        }

        private void ToggleCursor()
        {
            if (page == Vns430Page.Menu)
            {
                cursorActive = true;
                return;
            }

            cursorActive = !cursorActive;
        }

        private async void ActivateSelection()
        {
            switch (page)
            {
                case Vns430Page.Status:
                    if (!cursorActive)
                    {
                        cursorActive = true;
                        return;
                    }

                    switch (selectedIndex)
                    {
                        case 0:
                            SetPage(Vns430Page.Messages, true, Vns430PageGroup.Nrst);
                            break;
                        case 1:
                            SetPage(Vns430Page.Logon, true, Vns430PageGroup.Wpt);
                            break;
                        case 2:
                            backend.Vns430ToggleVatsimConnection();
                            SetTransient(snapshot.VatsimConnected ? "DISCONNECTING" : "CONNECTING");
                            break;
                        case 3:
                            SetPage(Vns430Page.AtcMenu, true, Vns430PageGroup.Wpt);
                            break;
                    }
                    break;

                case Vns430Page.Messages:
                    if (SelectedMessage() != null)
                    {
                        SetPage(Vns430Page.MessageDetail, true);
                        backend.Vns430MarkRead(SelectedMessage());
                    }
                    break;

                case Vns430Page.MessageDetail:
                    Vns430MessageSnapshot message = SelectedMessage();
                    if (message?.Responses.Count > 0)
                    {
                        string response = message.Responses[Math.Clamp(responseIndex, 0, message.Responses.Count - 1)];
                        backend.Vns430Reply(message, response);
                        SetTransient(response + " SENDING");
                    }
                    else if (message != null)
                    {
                        backend.Vns430MarkRead(message);
                        SetTransient("MESSAGE READ");
                    }
                    break;

                case Vns430Page.Logon:
                    string station = logonCode.Replace("_", string.Empty).Trim();
                    if (station.Length < 3)
                    {
                        cursorActive = true;
                        SetTransient("ENTER 3-4 CHAR CODE");
                        return;
                    }

                    // Follows the LOGON VIA selection, like the CDU's manual entry: a
                    // typed code carries no network of its own.
                    await backend.Vns430RequestLogonAsync(station, backend.Vns430LogonRoute());
                    cursorActive = false;
                    SetTransient("LOGON SENT " + station + " VIA " + backend.Vns430PdcViaLabel());
                    break;

                case Vns430Page.AtcMenu:
                    BeginWorkflow((Vns430WorkflowKind)(selectedIndex + (int)Vns430WorkflowKind.AtcDirect));
                    break;

                case Vns430Page.AocMenu:
                    if (selectedIndex == AocMenuItems.Length - 1)
                    {
                        await OpenLoadControlAsync();
                    }
                    else
                    {
                        BeginWorkflow((Vns430WorkflowKind)(selectedIndex + (int)Vns430WorkflowKind.AocTelex));
                    }
                    break;

                case Vns430Page.AtcRequest:
                case Vns430Page.AocRequest:
                    string validation = workflow?.ValidationError() ?? "NO REQUEST";
                    if (!string.IsNullOrWhiteSpace(validation))
                    {
                        SetTransient(validation);
                        return;
                    }
                    SetPage(page == Vns430Page.AtcRequest ? Vns430Page.RequestReview : Vns430Page.AocReview, false);
                    break;

                case Vns430Page.RequestReview:
                case Vns430Page.AocReview:
                    await SendWorkflowAsync();
                    break;

                case Vns430Page.LoadControl:
                    if (loadSession == null)
                    {
                        await OpenLoadControlAsync();
                    }
                    else
                    {
                        SetPage(Vns430Page.LoadReview, false);
                    }
                    break;

                case Vns430Page.LoadReview:
                    await GenerateLoadsheetAsync();
                    break;

                case Vns430Page.Pdc:
                    // First ENT arms the cursor (mirrors the Status page); the second
                    // requests the pre-departure clearance if one is available.
                    if (!cursorActive)
                    {
                        cursorActive = true;
                        return;
                    }
                    if (backend.Vns430CanRequestClearance())
                    {
                        backend.Vns430RequestClearance();
                        SetTransient("REQ CLR SENT");
                    }
                    else
                    {
                        SetTransient("REQ CLR NOT AVAIL");
                    }
                    break;

                case Vns430Page.Menu:
                    ActivateMenuItem(selectedIndex);
                    break;

                case Vns430Page.Help:
                    SetPage(pageBeforeOverlay, false);
                    break;
            }
        }

        private void ActivateMenuItem(int index)
        {
            // Any menu action other than re-selecting CLEAR ALL cancels its armed confirm.
            if (index != 7)
            {
                clearAllArmed = false;
            }

            switch (index)
            {
                case 0:
                    backend.Vns430ToggleVatsimConnection();
                    SetTransient(snapshot.VatsimConnected ? "DISCONNECTING" : "CONNECTING");
                    SetPage(Vns430Page.Status, false, Vns430PageGroup.Nav);
                    break;
                case 1:
                    SetPage(Vns430Page.AtcMenu, true, Vns430PageGroup.Wpt);
                    break;
                case 2:
                    SetPage(Vns430Page.AocMenu, true, Vns430PageGroup.Aux);
                    break;
                case 3:
                    // Cycle the ATC network (mirrors the CDU SETUP); stay on the menu so
                    // the updated label is visible.
                    SetTransient("ATC NETWORK " + backend.Vns430CycleAtcNetwork());
                    break;
                case 4:
                    SetTransient("LOGON VIA " + backend.Vns430CyclePdcVia());
                    break;
                case 5:
                    SetTransient("LOADING SIMBRIEF FP");
                    _ = ReloadSimbriefPlanAsync();
                    break;
                case 6:
                    // Destructive, so require a second press to confirm (the label shows
                    // "CONFIRM CLEAR ALL?" while armed).
                    if (clearAllArmed)
                    {
                        clearAllArmed = false;
                        backend.Vns430ClearAllMessages();
                        SetTransient("MESSAGES CLEARED");
                    }
                    else
                    {
                        clearAllArmed = true;
                        SetTransient("PRESS AGAIN TO CLEAR");
                    }
                    break;
                case 7:
                    backend.Vns430OpenSettings();
                    SetTransient("SETTINGS OPENED");
                    break;
                case 8:
                    ToggleCompanionModule();
                    break;
                case 9:
                    pageBeforeOverlay = Vns430Page.Menu;
                    SetPage(Vns430Page.Help, false);
                    break;
            }
        }

        private async Task ReloadSimbriefPlanAsync()
        {
            await backend.Vns430LoadSimbriefPlanAsync();
            SetTransient(backend.Vns430SimbriefPlanLabel());
            // The load page rebuilds its session from the new plan next time it opens.
            loadSession = null;
        }

        private void ToggleCompanionModule()
        {
            if (companionInput.Enabled)
            {
                companionInput.Disable();
                preferences.CompanionModuleEnabled = false;
                preferences.DcduCompanionMode = false;
                preferences.Save(Bounds);
                SetTransient("MSFS MODULE OFF");
                return;
            }

            if (companionInput.TryEnable(Handle, WmAppSimConnect, out string error))
            {
                preferences.CompanionModuleEnabled = true;
                preferences.Save(Bounds);
                SetTransient("WAITING FOR MODULE");
            }
            else
            {
                preferences.CompanionModuleEnabled = false;
                preferences.DcduCompanionMode = false;
                preferences.Save(Bounds);
                MessageBox.Show(this, error, "Companion module unavailable", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetTransient("MSFS MODULE OFFLINE");
            }
        }

        private void ClearOrBack()
        {
            if ((page == Vns430Page.AtcRequest || page == Vns430Page.AocRequest) && workflow != null && cursorActive)
            {
                Vns430EditField field = workflow.Fields[Math.Clamp(selectedIndex, 0, workflow.Fields.Count - 1)];
                if (!string.IsNullOrWhiteSpace(field.CleanValue) && !field.IsOption)
                {
                    field.ClearCharacter(workflowCharacter);
                    return;
                }
                SetPage(page == Vns430Page.AtcRequest ? Vns430Page.AtcMenu : Vns430Page.AocMenu, true);
                return;
            }

            if (page == Vns430Page.RequestReview)
            {
                SetPage(Vns430Page.AtcRequest, true);
                return;
            }
            if (page == Vns430Page.AocReview)
            {
                SetPage(Vns430Page.AocRequest, true);
                return;
            }
            if (page == Vns430Page.LoadReview)
            {
                SetPage(Vns430Page.LoadControl, true);
                return;
            }
            if (page == Vns430Page.AtcMenu || page == Vns430Page.AocMenu || page == Vns430Page.LoadControl)
            {
                SetPage(Vns430Page.Status, false, Vns430PageGroup.Nav);
                return;
            }

            if (page == Vns430Page.Logon && cursorActive)
            {
                char[] code = logonCode.ToCharArray();
                if (code[logonCharacter] != '_')
                {
                    code[logonCharacter] = '_';
                    logonCode = new string(code);
                    return;
                }

                cursorActive = false;
                return;
            }

            if (page == Vns430Page.MessageDetail)
            {
                SetPage(Vns430Page.Messages, true);
            }
            else if (page == Vns430Page.Menu || page == Vns430Page.Help)
            {
                pageGroup = groupBeforeOverlay;
                SetPage(pageBeforeOverlay, false);
            }
            else
            {
                SetPage(Vns430Page.Status, false, Vns430PageGroup.Nav);
            }
        }

        private void ToggleMenu()
        {
            clearAllArmed = false;   // never carry an armed CLEAR ALL across menu open/close

            if (page == Vns430Page.Menu)
            {
                pageGroup = groupBeforeOverlay;
                SetPage(pageBeforeOverlay, false);
                return;
            }

            pageBeforeOverlay = page;
            groupBeforeOverlay = pageGroup;
            SetPage(Vns430Page.Menu, true);
        }

        private void CyclePageGroup(int direction)
        {
            pageGroup = (Vns430PageGroup)Wrap((int)pageGroup + direction, 4);
            SetPage(PagesForGroup(pageGroup)[0], false);
        }

        private void CyclePageWithinGroup(int direction)
        {
            Vns430Page[] pages = PagesForGroup(pageGroup);
            int current = Array.IndexOf(pages, page);
            if (current < 0)
            {
                current = 0;
            }
            SetPage(pages[Wrap(current + direction, pages.Length)], false);
        }

        internal static Vns430Page[] PagesForGroup(Vns430PageGroup group)
        {
            return group switch
            {
                Vns430PageGroup.Nav => new[] { Vns430Page.Status, Vns430Page.Messages },
                Vns430PageGroup.Wpt => new[] { Vns430Page.Logon, Vns430Page.Pdc, Vns430Page.AtcMenu },
                Vns430PageGroup.Aux => new[] { Vns430Page.AocMenu, Vns430Page.LoadControl, Vns430Page.Help },
                Vns430PageGroup.Nrst => new[] { Vns430Page.Messages },
                _ => new[] { Vns430Page.Status }
            };
        }

        private void SetPage(Vns430Page newPage, bool activateCursor, Vns430PageGroup? group = null)
        {
            if (group.HasValue)
            {
                pageGroup = group.Value;
            }
            page = newPage;
            cursorActive = activateCursor;
            detailScrollLine = 0;
            responseIndex = 0;
            selectedIndex = ShouldPreserveMessageSelection(newPage)
                ? Math.Clamp(selectedIndex, 0, Math.Max(0, VisibleMessages().Count - 1))
                : 0;
            if (newPage != Vns430Page.AtcRequest && newPage != Vns430Page.AocRequest)
            {
                workflowCharacter = 0;
            }
        }

        private string MessageFilterLabel() => MessageFilters[Math.Clamp(messageFilter, 0, MessageFilters.Length - 1)];

        // The messages visible under the current RECEIVED/SENT/ALL filter. Shared with the
        // renderer (Vns430LcdRenderer.FilterMessages) so the selection index lines up.
        private IReadOnlyList<Vns430MessageSnapshot> VisibleMessages() =>
            Vns430LcdRenderer.FilterMessages(snapshot.Messages, MessageFilterLabel());

        private void CycleMessageFilter()
        {
            messageFilter = (messageFilter + 1) % MessageFilters.Length;
            selectedIndex = 0;
            SetTransient("MSG " + MessageFilterLabel());
        }

        private Vns430MessageSnapshot SelectedMessage()
        {
            IReadOnlyList<Vns430MessageSnapshot> messages = VisibleMessages();
            return messages.Count == 0
                ? null
                : messages[Math.Clamp(selectedIndex, 0, messages.Count - 1)];
        }

        private List<string> MenuItems()
        {
            return new List<string>
            {
                snapshot.VatsimConnected ? "DISCONNECT VATSIM" : "CONNECT VATSIM",
                "ATC REQUEST MENU",
                "AOC / TELEX MENU",
                "ATC NETWORK: " + backend.Vns430AtcNetworkLabel(),
                // WX SOURCE lived here; it is now picked per request on the METAR/ATIS
                // forms, where the VIA field sits next to the station it applies to.
                "LOGON VIA: " + backend.Vns430PdcViaLabel(),
                backend.Vns430SimbriefPlanLabel(),
                clearAllArmed ? "CONFIRM CLEAR ALL?" : "CLEAR ALL MESSAGES",
                "EASYCPDLC SETTINGS",
                "MSFS MODULE: " + companionInput.Status,
                "INPUT HELP"
            };
        }

        private void OpenMessages(bool prioritizeUnread)
        {
            int unread = prioritizeUnread
                ? VisibleMessages().ToList().FindIndex(message => message.Unread && !message.Outbound)
                : -1;
            if (unread >= 0)
            {
                selectedIndex = unread;
                SetPage(Vns430Page.MessageDetail, true, Vns430PageGroup.Nrst);
                backend.Vns430MarkRead(SelectedMessage());
                return;
            }

            SetPage(Vns430Page.Messages, true, Vns430PageGroup.Nrst);
        }

        // Seed the logon code with the best online CPDLC facility (a tuned-frequency
        // match wins) so opening the page offers a one-key ENT logon. A code the pilot
        // has already typed is left untouched.
        private void PrefillLogonFromBestCandidate()
        {
            if (!string.IsNullOrEmpty((logonCode ?? string.Empty).Replace("_", string.Empty).Trim()))
            {
                return;
            }

            Vns430CpdlcCandidate best = snapshot?.CpdlcCandidates?
                .OrderByDescending(candidate => candidate.TunedMatch)
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Code));
            if (best != null)
            {
                logonCode = best.Code.PadRight(4, '_').Substring(0, 4);
            }
        }

        private void BeginWorkflow(Vns430WorkflowKind kind)
        {
            workflow = Vns430Workflow.Create(kind, snapshot);
            workflowCharacter = 0;
            operationStatus = string.Empty;
            bool atc = kind >= Vns430WorkflowKind.AtcDirect && kind <= Vns430WorkflowKind.AtcPositionReport;
            SetPage(atc ? Vns430Page.AtcRequest : Vns430Page.AocRequest, true,
                atc ? Vns430PageGroup.Wpt : Vns430PageGroup.Aux);
        }

        private void MoveWorkflowCursor(int direction)
        {
            if (workflow?.Fields.Count > 0)
            {
                Vns430EditField field = workflow.Fields[Math.Clamp(selectedIndex, 0, workflow.Fields.Count - 1)];
                if (field.IsOption)
                {
                    selectedIndex = Wrap(selectedIndex + direction, workflow.Fields.Count);
                    workflowCharacter = direction < 0
                        ? Math.Max(0, workflow.Fields[selectedIndex].MaxLength - 1)
                        : 0;
                    return;
                }

                int next = workflowCharacter + direction;
                if (next >= 0 && next < field.MaxLength)
                {
                    workflowCharacter = next;
                    return;
                }

                selectedIndex = Wrap(selectedIndex + direction, workflow.Fields.Count);
                Vns430EditField nextField = workflow.Fields[selectedIndex];
                workflowCharacter = nextField.IsOption || direction > 0 ? 0 : Math.Max(0, nextField.MaxLength - 1);
            }
        }

        private void StepLoadControlField(int direction)
        {
            if (loadSession == null)
            {
                return;
            }

            if (selectedIndex == 0)
            {
                loadSession.AircraftIndex = Wrap(loadSession.AircraftIndex + direction, loadSession.Reference.Aircraft.Count);
                loadSession.CabinIndex = 0;
                loadSession.RebuildPassengerSplit();
            }
            else if (selectedIndex == 1)
            {
                loadSession.CabinIndex = Wrap(loadSession.CabinIndex + direction, loadSession.Aircraft.CabinConfigurations.Count);
                loadSession.RebuildPassengerSplit();
            }
            else if (selectedIndex == 2)
            {
                loadSession.FormatIndex = Wrap(loadSession.FormatIndex + direction, loadSession.Reference.Formats.Count);
            }
            else if (selectedIndex == 3 + loadSession.PassengerSplit.Count)
            {
                // LOAD TIME sits below the PAX rows: simulated ground-crew loading time.
                loadSession.LoadingTimeIndex = Wrap(
                    loadSession.LoadingTimeIndex + direction,
                    Vns430LoadControlSession.LoadingTimeMinutes.Length);
            }
            else
            {
                int passengerIndex = selectedIndex - 3;
                if (passengerIndex >= 0 && passengerIndex < loadSession.PassengerSplit.Count)
                {
                    PassengerClassAllocation allocation = loadSession.PassengerSplit[passengerIndex];
                    allocation.Passengers = Math.Clamp(allocation.Passengers + direction, 0, 999);
                }
            }
        }

        private async Task OpenLoadControlAsync()
        {
            SetPage(Vns430Page.LoadControl, true, Vns430PageGroup.Aux);
            operationBusy = true;
            operationStatus = "LOADING SIMBRIEF / ELOAD";
            try
            {
                loadSession = await backend.Vns430PrepareLoadControlAsync();
                operationStatus = "REVIEW LOAD DATA";
            }
            catch (Exception ex)
            {
                loadSession = null;
                operationStatus = CollapseWhitespace(ex.Message).ToUpperInvariant();
            }
            finally
            {
                operationBusy = false;
            }
        }

        private async Task SendWorkflowAsync()
        {
            if (operationBusy || workflow == null)
            {
                return;
            }

            operationBusy = true;
            operationStatus = "SENDING REQUEST";
            Vns430OperationResult result = await backend.Vns430SendWorkflowAsync(workflow, snapshot);
            operationBusy = false;
            operationStatus = result.Status;
            SetTransient(result.Status);
            if (result.Success)
            {
                RefreshSnapshot();
                selectedIndex = 0;
                SetPage(Vns430Page.Messages, true, Vns430PageGroup.Nav);
            }
        }

        private async Task GenerateLoadsheetAsync()
        {
            if (operationBusy || loadSession == null)
            {
                return;
            }

            operationBusy = true;
            operationStatus = "GENERATING LOADSHEET";
            Vns430OperationResult result = await backend.Vns430GenerateLoadsheetAsync(loadSession);
            operationBusy = false;
            operationStatus = result.Status;
            SetTransient(result.Status);
            if (result.Success)
            {
                RefreshSnapshot();
                messageFilter = 0;   // show ALL so the freshly generated loadsheet is visible
                int received = VisibleMessages().ToList().FindIndex(message =>
                    message.Unread && string.Equals(message.Type, "LOADSHEET", StringComparison.OrdinalIgnoreCase));
                selectedIndex = Math.Max(0, received);
                SetPage(received >= 0 ? Vns430Page.MessageDetail : Vns430Page.Messages, true, Vns430PageGroup.Nrst);
                if (received >= 0)
                {
                    backend.Vns430MarkRead(SelectedMessage());
                }
            }
        }

        private void SetTransient(string text)
        {
            transientStatus = (text ?? string.Empty).Trim().ToUpperInvariant();
            transientStatusUntilUtc = DateTime.UtcNow.AddSeconds(3);
        }

        private void SetArtworkFeedback(Vns430Command command)
        {
            (string control, string state) = command switch
            {
                Vns430Command.LargeRightDecrease => ("right_encoder", "large-ccw"),
                Vns430Command.LargeRightIncrease => ("right_encoder", "large-cw"),
                Vns430Command.SmallRightDecrease => ("right_encoder", "small-ccw"),
                Vns430Command.SmallRightIncrease => ("right_encoder", "small-cw"),
                Vns430Command.CursorPush => ("right_encoder", "pushed"),
                Vns430Command.Enter => ("enter", "pressed"),
                Vns430Command.Clear => ("clear", "pressed"),
                Vns430Command.Menu => ("menu", "pressed"),
                Vns430Command.Message => ("msg", "pressed"),
                Vns430Command.FlightPlan => ("fpl", "pressed"),
                Vns430Command.Procedure => ("proc", "pressed"),
                Vns430Command.DirectTo => ("direct_to", "pressed"),
                Vns430Command.Cdi => ("cdi", "pressed"),
                Vns430Command.Obs => ("obs", "pressed"),
                Vns430Command.RangeIn => ("range", "increase-pressed"),
                Vns430Command.RangeOut => ("range", "decrease-pressed"),
                Vns430Command.Power => ("left_small_top", "cw"),
                _ => (string.Empty, string.Empty)
            };
            SetArtworkFeedback(control, state);
        }

        private void SetArtworkFeedback(string control, string state, bool held = false)
        {
            activeArtworkControl = control ?? string.Empty;
            activeArtworkState = state ?? string.Empty;
            activeArtworkUntilUtc = string.IsNullOrWhiteSpace(activeArtworkControl)
                ? DateTime.MinValue
                : held ? DateTime.MaxValue : DateTime.UtcNow.AddMilliseconds(220);
        }

        private void ClearArtworkFeedback()
        {
            activeArtworkControl = string.Empty;
            activeArtworkState = string.Empty;
            activeArtworkUntilUtc = DateTime.MinValue;
        }

        private void PaintPanel(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (preferences.ScreenOnlyMode)
            {
                DrawScreenOnly(e.Graphics);
                return;
            }

            float scaleX = ClientSize.Width / (float)LogicalWidth;
            float scaleY = ClientSize.Height / (float)LogicalHeight;
            e.Graphics.ScaleTransform(scaleX, scaleY);

            DrawPanelSurface(e.Graphics);
        }

        // Screen mode: no bezel, no click zones. Draw just the LCD, scaled to fill
        // the window at its native aspect and centred on black, so it can sit
        // behind a physical unit while hardware buttons drive it.
        private void DrawScreenOnly(Graphics graphics)
        {
            graphics.Clear(Color.Black);

            Bitmap lcd = CurrentLcd();
            if (lcd == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            float scale = Math.Min(
                ClientSize.Width / (float)lcd.Width,
                ClientSize.Height / (float)lcd.Height);
            float width = lcd.Width * scale;
            float height = lcd.Height * scale;
            RectangleF target = new(
                (ClientSize.Width - width) / 2f,
                (ClientSize.Height - height) / 2f,
                width,
                height);

            InterpolationMode previousInterpolation = graphics.InterpolationMode;
            PixelOffsetMode previousOffset = graphics.PixelOffsetMode;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(lcd, target);
            graphics.InterpolationMode = previousInterpolation;
            graphics.PixelOffsetMode = previousOffset;
        }

        internal bool ScreenOnlyMode => preferences.ScreenOnlyMode;

        internal void SetScreenOnlyMode(bool enabled)
        {
            if (preferences.ScreenOnlyMode == enabled)
            {
                return;
            }

            preferences.ScreenOnlyMode = enabled;
            preferences.Save(Bounds);
            ApplyScreenModeChrome();
            Invalidate();
        }

        // Force the frame to be recalculated so the WM_NCCALCSIZE handler above
        // adds or drops the chrome for the current mode. The window handle is not
        // recreated, so the companion SimConnect registration is undisturbed.
        private void ApplyScreenModeChrome()
        {
            if (!IsHandleCreated)
            {
                return;
            }

            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }

        private void DrawPanelSurface(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(artwork.PanelBackground, new RectangleF(0, 0, LogicalWidth, LogicalHeight));
            DrawScreen(graphics);
            if (DateTime.UtcNow < activeArtworkUntilUtc)
            {
                artwork.DrawState(graphics, activeArtworkControl, activeArtworkState);
            }
        }

        private void DrawBranding(Graphics graphics)
        {
            using Font brand = new(FontFamily.GenericSansSerif, 8.5f, FontStyle.Bold, GraphicsUnit.Point);
            using Font model = new(FontFamily.GenericSansSerif, 7.2f, FontStyle.Regular, GraphicsUnit.Point);
            using Brush ink = new SolidBrush(Color.FromArgb(22, 24, 22));
            graphics.DrawString("GARMIN", brand, ink, 28, 22);
            graphics.DrawString("VNS430", model, ink, 882, 22);
            graphics.DrawString("COM", model, ink, 136, 220);
            graphics.DrawString("VLOC", model, ink, 169, 220);
            graphics.DrawString("GPS", model, ink, 847, 276);
        }

        private void DrawScreen(Graphics graphics)
        {
            Bitmap lcd = CurrentLcd();
            InterpolationMode previousInterpolation = graphics.InterpolationMode;
            PixelOffsetMode previousOffset = graphics.PixelOffsetMode;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(lcd, ScreenBounds);
            graphics.InterpolationMode = previousInterpolation;
            graphics.PixelOffsetMode = previousOffset;
        }

        private Vns430LcdState CreateLcdState()
        {
            return new Vns430LcdState
            {
                Snapshot = snapshot,
                Page = page,
                PageGroup = pageGroup,
                CursorActive = cursorActive,
                SelectedIndex = selectedIndex,
                DetailScrollLine = detailScrollLine,
                ResponseIndex = responseIndex,
                ZoomLevel = zoomLevel,
                LogonCode = logonCode,
                LogonCharacter = logonCharacter,
                TransientStatus = DateTime.UtcNow < transientStatusUntilUtc ? transientStatus : string.Empty,
                MessageFilter = MessageFilterLabel(),
                // The menu list reads live config for its labels; only build it while the
                // menu overlay is actually displayed. The fingerprint covers Page, so
                // opening the menu re-renders with the real items.
                MenuItems = page == Vns430Page.Menu ? MenuItems() : Array.Empty<string>(),
                Workflow = workflow,
                WorkflowCharacter = workflowCharacter,
                LoadSession = loadSession,
                OperationBusy = operationBusy,
                OperationStatus = operationStatus
            };
        }

        private void DrawRadioStrip(Graphics graphics)
        {
            RectangleF strip = new(ScreenBounds.X, ScreenBounds.Y, MainScreenBounds.X - ScreenBounds.X, ScreenBounds.Height);
            using Brush blue = new SolidBrush(Color.FromArgb(0, 83, 166));
            using Brush black = new SolidBrush(Color.Black);
            using Brush white = new SolidBrush(ScreenWhite);
            using Brush cyan = new SolidBrush(ScreenCyan);
            using Brush green = new SolidBrush(ScreenGreen);
            using Brush amber = new SolidBrush(ScreenAmber);
            using Pen line = new(ScreenWhite, 1);
            using Font label = DisplayFont(6.5f, true);
            using Font value = DisplayFont(10.8f, true);
            using Font mode = DisplayFont(7.2f, true);

            graphics.FillRectangle(blue, strip);
            graphics.DrawString("DLK", label, cyan, strip.X + 5, strip.Y + 4);
            graphics.DrawString(Truncate(string.IsNullOrWhiteSpace(snapshot.Callsign) ? "--------" : snapshot.Callsign, 8), value, white, strip.X + 5, strip.Y + 18);
            graphics.DrawLine(line, strip.X + 3, strip.Y + 48, strip.Right - 3, strip.Y + 48);
            graphics.DrawString("ATC", label, cyan, strip.X + 5, strip.Y + 54);
            graphics.DrawString(Truncate(string.IsNullOrWhiteSpace(snapshot.CurrentAtcUnit) ? "----" : snapshot.CurrentAtcUnit, 8), value, green, strip.X + 5, strip.Y + 68);
            graphics.DrawLine(line, strip.X + 3, strip.Y + 100, strip.Right - 3, strip.Y + 100);
            graphics.DrawString("LOG", label, cyan, strip.X + 5, strip.Y + 106);
            graphics.DrawString(Truncate(string.IsNullOrWhiteSpace(snapshot.PendingLogon) ? "----" : snapshot.PendingLogon, 8), value, snapshot.Connected ? green : amber, strip.X + 5, strip.Y + 120);

            RectangleF modeBox = new(strip.X + 4, strip.Bottom - 71, strip.Width - 8, 27);
            graphics.FillRectangle(black, modeBox);
            graphics.DrawRectangle(line, modeBox.X, modeBox.Y, modeBox.Width, modeBox.Height);
            DrawCentered(graphics, snapshot.Connected ? "ENR" : "STBY", mode, snapshot.Connected ? green : amber, modeBox);

            RectangleF gpsBox = new(strip.X, strip.Bottom - 22, strip.Width, 22);
            graphics.FillRectangle(black, gpsBox);
            graphics.DrawString("GPS", mode, green, gpsBox.X + 6, gpsBox.Y + 4);
        }

        private void DrawStatusPage(Graphics graphics)
        {
            DrawPageHeader(graphics, "DATALINK STATUS", "");
            string connection = snapshot.Connected ? "CONNECTED" : "STANDBY";
            string callsign = string.IsNullOrWhiteSpace(snapshot.Callsign) ? "--------" : snapshot.Callsign;
            string station = string.IsNullOrWhiteSpace(snapshot.CurrentAtcUnit) ? "----" : snapshot.CurrentAtcUnit;

            using Brush black = new SolidBrush(Color.Black);
            using Brush green = new SolidBrush(ScreenGreen);
            using Brush magenta = new SolidBrush(ScreenMagenta);
            using Font route = DisplayFont(10.5f, true);
            RectangleF routeBox = new(MainScreenBounds.X + 6, MainScreenBounds.Y + 34, MainScreenBounds.Width - 12, 34);
            graphics.FillRectangle(black, routeBox);
            graphics.DrawString(Truncate(callsign, 8), route, green, routeBox.X + 8, routeBox.Y + 7);
            DrawCentered(graphics, "→", route, magenta, new RectangleF(routeBox.X + 156, routeBox.Y, 70, routeBox.Height));
            SizeF stationSize = graphics.MeasureString(Truncate(station, 8), route);
            graphics.DrawString(Truncate(station, 8), route, green, routeBox.Right - stationSize.Width - 8, routeBox.Y + 7);

            float cellWidth = (MainScreenBounds.Width - 18) / 2f;
            DrawDataCell(graphics, new RectangleF(MainScreenBounds.X + 6, MainScreenBounds.Y + 76, cellWidth, 61), "MESSAGES", snapshot.Messages.Count.ToString(), cursorActive && selectedIndex == 0, ScreenGreen);
            DrawDataCell(graphics, new RectangleF(MainScreenBounds.X + 12 + cellWidth, MainScreenBounds.Y + 76, cellWidth, 61), "CPDLC LOGON", station, cursorActive && selectedIndex == 1, ScreenGreen);
            DrawDataCell(graphics, new RectangleF(MainScreenBounds.X + 6, MainScreenBounds.Y + 143, cellWidth, 61), "VATSIM", connection, cursorActive && selectedIndex == 2, snapshot.Connected ? ScreenGreen : ScreenAmber);
            DrawDataCell(graphics, new RectangleF(MainScreenBounds.X + 12 + cellWidth, MainScreenBounds.Y + 143, cellWidth, 61), "ATC REQUEST", "SELECT", cursorActive && selectedIndex == 3, ScreenWhite);
            DrawFooter(graphics, cursorActive ? "CRSR" : "", "ENT SELECT");
        }

        private void DrawMessageListPage(Graphics graphics)
        {
            DrawPageHeader(graphics, "DATALINK MESSAGES", snapshot.Messages.Count.ToString());
            if (snapshot.Messages.Count == 0)
            {
                using Font emptyFont = DisplayFont(10.5f, true);
                using Brush emptyBrush = new SolidBrush(ScreenWhite);
                DrawCentered(graphics, "NO MESSAGES", emptyFont, emptyBrush, new RectangleF(MainScreenBounds.X, MainScreenBounds.Y + 100, MainScreenBounds.Width, 35));
                DrawFooter(graphics, "MSG", "CLR RETURN");
                return;
            }

            int first = Math.Max(0, selectedIndex - 5);
            int last = Math.Min(snapshot.Messages.Count, first + 7);
            for (int index = first; index < last; index++)
            {
                Vns430MessageSnapshot message = snapshot.Messages[index];
                int row = index - first;
                string direction = message.Outbound ? ">" : message.Unread ? "*" : "<";
                string title = direction + " " + Truncate(message.Type, 7) + " " + Truncate(message.Station, 8);
                string preview = CollapseWhitespace(message.Text);
                DrawMessageRow(graphics, row, title, preview, index == selectedIndex);
            }

            DrawScrollBar(graphics, first, snapshot.Messages.Count, 7);
            DrawFooter(graphics, cursorActive ? "CRSR" : "MSG", $"{selectedIndex + 1}/{snapshot.Messages.Count}  ENT OPEN");
        }

        private void DrawMessageDetailPage(Graphics graphics)
        {
            Vns430MessageSnapshot message = SelectedMessage();
            if (message == null)
            {
                SetPage(Vns430Page.Messages, true);
                return;
            }

            DrawPageHeader(graphics, message.Outbound ? "UPLINK SENT" : "UPLINK RECEIVED", Truncate(message.Station, 8));
            List<string> lines = WrapText(CollapseWhitespace(message.Text), zoomLevel == 2 ? 30 : zoomLevel == 0 ? 45 : 38);
            int visibleLines = message.Responses.Count > 0 ? 7 : 10;
            detailScrollLine = Math.Clamp(detailScrollLine, 0, Math.Max(0, lines.Count - visibleLines));
            using Font text = DisplayFont(zoomLevel == 2 ? 9.5f : zoomLevel == 0 ? 7f : 8.1f, false);
            using Brush white = new SolidBrush(ScreenWhite);
            using Brush cyan = new SolidBrush(ScreenCyan);
            using Font typeFont = DisplayFont(7f, true);
            graphics.DrawString(Truncate(message.Type, 24), typeFont, cyan, MainScreenBounds.X + 8, MainScreenBounds.Y + 36);
            float y = MainScreenBounds.Y + 55;
            for (int i = detailScrollLine; i < Math.Min(lines.Count, detailScrollLine + visibleLines); i++)
            {
                graphics.DrawString(lines[i], text, white, MainScreenBounds.X + 8, y);
                y += zoomLevel == 2 ? 22 : zoomLevel == 0 ? 15 : 18;
            }

            if (message.Responses.Count > 0)
            {
                string response = message.Responses[Math.Clamp(responseIndex, 0, message.Responses.Count - 1)];
                DrawValueBox(graphics, new RectangleF(MainScreenBounds.X + 92, ScreenBounds.Bottom - 54, MainScreenBounds.Width - 184, 27), response, cursorActive);
            }
            DrawFooter(graphics, "MSG", message.Responses.Count > 0 ? "SMALL RESPONSE  ENT SEND" : "ENT READ  CLR LIST");
        }

        private void DrawLogonPage(Graphics graphics)
        {
            DrawPageHeader(graphics, "CPDLC DIRECT LOGON", "");
            using Font label = DisplayFont(7.5f, true);
            using Brush white = new SolidBrush(ScreenWhite);
            using Brush cyan = new SolidBrush(ScreenCyan);
            graphics.DrawString("LOGON FACILITY", label, cyan, MainScreenBounds.X + 34, MainScreenBounds.Y + 58);

            float startX = MainScreenBounds.X + 123;
            for (int i = 0; i < 4; i++)
            {
                bool selected = cursorActive && i == logonCharacter;
                DrawValueBox(graphics, new RectangleF(startX + (i * 39), MainScreenBounds.Y + 81, 34, 35), logonCode[i].ToString(), selected);
            }

            string current = string.IsNullOrWhiteSpace(snapshot.CurrentAtcUnit) ? "NONE" : snapshot.CurrentAtcUnit;
            string pending = string.IsNullOrWhiteSpace(snapshot.PendingLogon) ? "NONE" : snapshot.PendingLogon;
            DrawDataCell(graphics, new RectangleF(MainScreenBounds.X + 36, MainScreenBounds.Y + 138, MainScreenBounds.Width - 72, 42), "CURRENT FACILITY", current, false, ScreenGreen);
            DrawDataCell(graphics, new RectangleF(MainScreenBounds.X + 36, MainScreenBounds.Y + 185, MainScreenBounds.Width - 72, 42), "PENDING LOGON", pending, false, ScreenAmber);
            DrawFooter(graphics, cursorActive ? "CRSR" : "", "SMALL CHAR  LARGE POS  ENT");
        }

        private void DrawMenuPage(Graphics graphics)
        {
            List<string> items = MenuItems();
            RectangleF menu = new(MainScreenBounds.X + 24, MainScreenBounds.Y + 18, MainScreenBounds.Width - 48, MainScreenBounds.Height - 48);
            using Brush black = new SolidBrush(Color.Black);
            using Pen border = new(ScreenWhite, 1);
            using Font heading = DisplayFont(7.2f, true);
            using Brush green = new SolidBrush(ScreenGreen);
            graphics.FillRectangle(black, menu);
            graphics.DrawRectangle(border, menu.X, menu.Y, menu.Width, menu.Height);
            graphics.DrawString("PAGE MENU", heading, green, menu.X + 8, menu.Y + 6);
            int first = Math.Max(0, selectedIndex - 5);
            for (int index = first; index < Math.Min(items.Count, first + 7); index++)
            {
                int row = index - first;
                DrawMenuRow(graphics, menu, row, items[index], index == selectedIndex);
            }
            DrawScrollBar(graphics, first, items.Count, 7);
            DrawFooter(graphics, "MENU", "LARGE SELECT  ENT");
        }

        private void DrawHelpPage(Graphics graphics)
        {
            DrawPageHeader(graphics, "INPUT / CONTROL SETUP", "");
            string[] lines =
            {
                "PANEL KEYBOARD",
                "LEFT/RIGHT   LARGE RIGHT KNOB",
                "DOWN/UP      SMALL RIGHT KNOB",
                "SPACE        PUSH CRSR",
                "ENTER / ESC  ENT / CLR",
                "",
                "MOBIFLIGHT PROJECT INCLUDED",
                "PRIVATE EASYCPDLC L-VARS ONLY",
                "",
                "NO GARMIN OR AIRCRAFT GPS EVENTS"
            };
            using Font font = DisplayFont(6.9f, false);
            using Brush white = new SolidBrush(ScreenWhite);
            using Brush cyan = new SolidBrush(ScreenCyan);
            float y = MainScreenBounds.Y + 39;
            int lineNumber = 0;
            foreach (string line in lines.Skip(detailScrollLine).Take(12))
            {
                graphics.DrawString(line, font, lineNumber == 0 || line.StartsWith("MOBIFLIGHT") ? cyan : white, MainScreenBounds.X + 9, y);
                y += 16;
                lineNumber++;
            }
            DrawFooter(graphics, cursorActive ? "CRSR" : "", "PUSH CRSR THEN LARGE SCROLL");
        }

        private void DrawPageHeader(Graphics graphics, string title, string pageNumber)
        {
            using Brush black = new SolidBrush(Color.Black);
            using Brush green = new SolidBrush(ScreenGreen);
            using Brush white = new SolidBrush(ScreenWhite);
            using Font font = DisplayFont(7.5f, true);
            RectangleF header = new(MainScreenBounds.X + 5, MainScreenBounds.Y + 4, MainScreenBounds.Width - 10, 25);
            graphics.FillRectangle(black, header);
            graphics.DrawString(title, font, green, header.X + 7, header.Y + 5);
            SizeF size = graphics.MeasureString(pageNumber, font);
            graphics.DrawString(pageNumber, font, white, header.Right - size.Width - 7, header.Y + 5);
        }

        private void DrawDataCell(Graphics graphics, RectangleF bounds, string label, string value, bool selected, Color valueColor)
        {
            using Brush fill = new SolidBrush(selected ? ScreenWhite : Color.Black);
            using Pen border = new(ScreenWhite, 1);
            using Brush labelBrush = new SolidBrush(selected ? Color.Black : ScreenCyan);
            using Brush valueBrush = new SolidBrush(selected ? Color.Black : valueColor);
            using Font labelFont = DisplayFont(6.2f, true);
            using Font valueFont = DisplayFont(8.5f, true);
            graphics.FillRectangle(fill, bounds);
            graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            graphics.DrawString(label, labelFont, labelBrush, bounds.X + 5, bounds.Y + 3);
            DrawCentered(graphics, value, valueFont, valueBrush, new RectangleF(bounds.X + 4, bounds.Y + 18, bounds.Width - 8, bounds.Height - 21));
        }

        private void DrawMessageRow(Graphics graphics, int row, string title, string preview, bool selected)
        {
            float y = MainScreenBounds.Y + 35 + (row * 28);
            RectangleF bounds = new(MainScreenBounds.X + 6, y, MainScreenBounds.Width - 14, 26);
            using Brush fill = new SolidBrush(selected && cursorActive ? ScreenWhite : Color.Black);
            using Pen border = new(ScreenWhite, 0.7f);
            using Font titleFont = DisplayFont(6.5f, true);
            using Font previewFont = DisplayFont(6.2f, false);
            using Brush titleBrush = new SolidBrush(selected && cursorActive ? Color.Black : ScreenGreen);
            using Brush previewBrush = new SolidBrush(selected && cursorActive ? Color.Black : ScreenWhite);
            graphics.FillRectangle(fill, bounds);
            graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            graphics.DrawString(title, titleFont, titleBrush, bounds.X + 4, bounds.Y + 2);
            graphics.DrawString(Truncate(preview, 31), previewFont, previewBrush, bounds.X + 145, bounds.Y + 3);
        }

        private void DrawMenuRow(Graphics graphics, RectangleF menuBounds, int row, string text, bool selected)
        {
            float y = menuBounds.Y + 26 + (row * 27);
            RectangleF bounds = new(menuBounds.X + 5, y, menuBounds.Width - 10, 25);
            using Brush fill = new SolidBrush(selected ? ScreenWhite : Color.Black);
            using Brush brush = new SolidBrush(selected ? Color.Black : ScreenWhite);
            using Font font = DisplayFont(6.8f, true);
            graphics.FillRectangle(fill, bounds);
            graphics.DrawString(text, font, brush, bounds.X + 6, bounds.Y + 5);
        }

        private void DrawValueBox(Graphics graphics, RectangleF bounds, string value, bool selected)
        {
            using Brush fill = new SolidBrush(selected ? ScreenWhite : Color.Black);
            using Pen border = new(ScreenWhite, 1f);
            using Brush text = new SolidBrush(selected ? Color.Black : ScreenGreen);
            using Font font = DisplayFont(bounds.Height > 31 ? 13 : 7.8f, true);
            graphics.FillRectangle(fill, bounds);
            graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            DrawCentered(graphics, value, font, text, bounds);
        }

        private static void DrawScrollBar(Graphics graphics, int firstItem, int totalItems, int visibleItems)
        {
            if (totalItems <= visibleItems || totalItems <= 0)
            {
                return;
            }

            RectangleF track = new(MainScreenBounds.Right - 7, MainScreenBounds.Y + 35, 4, MainScreenBounds.Height - 62);
            float thumbHeight = Math.Max(13, track.Height * visibleItems / totalItems);
            float travel = Math.Max(0, track.Height - thumbHeight);
            float maximumFirst = Math.Max(1, totalItems - visibleItems);
            float thumbY = track.Y + (travel * Math.Min(firstItem, (int)maximumFirst) / maximumFirst);
            using Brush trackBrush = new SolidBrush(Color.Black);
            using Brush thumbBrush = new SolidBrush(ScreenWhite);
            graphics.FillRectangle(trackBrush, track);
            graphics.FillRectangle(thumbBrush, new RectangleF(track.X - 1, thumbY, track.Width + 2, thumbHeight));
        }

        private void DrawFooter(Graphics graphics, string group, string hint)
        {
            using Brush black = new SolidBrush(Color.Black);
            using Brush green = new SolidBrush(ScreenGreen);
            using Brush white = new SolidBrush(ScreenWhite);
            using Brush amber = new SolidBrush(ScreenAmber);
            using Font font = DisplayFont(6.1f, true);
            RectangleF footer = new(MainScreenBounds.X, ScreenBounds.Bottom - 22, MainScreenBounds.Width, 22);
            graphics.FillRectangle(black, footer);
            string annunciator = snapshot.Messages.Any(message => message.Unread) ? "MSG" : group;
            graphics.DrawString(annunciator, font, annunciator == "MSG" ? amber : green, footer.X + 5, footer.Y + 4);
            graphics.DrawString(hint, font, white, footer.X + 62, footer.Y + 4);
            DrawPageIndicator(graphics, footer);
        }

        private void DrawPageIndicator(Graphics graphics, RectangleF footer)
        {
            Vns430PageGroup visibleGroup = page == Vns430Page.Menu ? groupBeforeOverlay : pageGroup;
            Vns430Page[] pages = PagesForGroup(visibleGroup);
            int current = Math.Max(0, Array.IndexOf(pages, page));
            string label = visibleGroup.ToString().ToUpperInvariant();
            using Font font = DisplayFont(6.2f, true);
            using Brush white = new SolidBrush(ScreenWhite);
            SizeF labelSize = graphics.MeasureString(label, font);
            float squaresWidth = (pages.Length * 8) + 3;
            float startX = footer.Right - labelSize.Width - squaresWidth - 8;
            for (int i = 0; i < pages.Length; i++)
            {
                RectangleF square = new(startX + (i * 8), footer.Y + 7, 6, 7);
                using Brush fill = new SolidBrush(i == current ? ScreenWhite : ScreenBlue);
                using Pen outline = new(ScreenWhite, 1);
                graphics.FillRectangle(fill, square);
                graphics.DrawRectangle(outline, square.X, square.Y, square.Width, square.Height);
            }
            graphics.DrawString(label, font, white, footer.Right - labelSize.Width - 4, footer.Y + 4);
        }

        private void DrawPanelButtons(Graphics graphics)
        {
            foreach (PanelButton button in panelButtons)
            {
                bool range = button.Label == "▽" || button.Label == "△";
                using GraphicsPath shape = RoundedRectangle(button.Bounds, Math.Min(8, button.Bounds.Height / 2));
                using LinearGradientBrush face = new(button.Bounds, Color.FromArgb(235, 235, 222), Color.FromArgb(133, 136, 130), LinearGradientMode.Vertical);
                using Pen edge = new(Color.FromArgb(15, 18, 17), 1.3f);
                using Font font = new(FontFamily.GenericSansSerif, range ? 9f : 7.1f, FontStyle.Bold, GraphicsUnit.Point);
                using Brush text = new SolidBrush(Color.FromArgb(16, 18, 16));
                graphics.FillPath(face, shape);
                graphics.DrawPath(edge, shape);
                DrawCentered(graphics, button.Label, font, text, button.Bounds);
            }

            using Font tiny = new(FontFamily.GenericSansSerif, 6.2f, FontStyle.Bold, GraphicsUnit.Point);
            using Brush ink = new SolidBrush(Color.FromArgb(24, 26, 24));
            graphics.DrawString("RNG", tiny, ink, 852, 91);
            graphics.DrawString("DEFAULT", tiny, ink, 764, 174);
            graphics.DrawString("NAV", tiny, ink, 770, 183);
        }

        private void DrawKnob(Graphics graphics, PointF center, string top, string bottom)
        {
            RectangleF outer = new(center.X - 49, center.Y - 49, 98, 98);
            RectangleF middle = new(center.X - 36, center.Y - 36, 72, 72);
            RectangleF inner = new(center.X - 22, center.Y - 22, 44, 44);
            using LinearGradientBrush outerFill = new(outer, Color.FromArgb(214, 215, 205), Color.FromArgb(78, 80, 76), LinearGradientMode.ForwardDiagonal);
            using LinearGradientBrush middleFill = new(middle, Color.FromArgb(182, 184, 176), Color.FromArgb(55, 58, 55), LinearGradientMode.BackwardDiagonal);
            using Brush innerFill = new SolidBrush(Color.FromArgb(207, 209, 198));
            using Pen dark = new(Color.FromArgb(14, 15, 14), 2);
            graphics.FillEllipse(outerFill, outer);
            graphics.DrawEllipse(dark, outer);
            graphics.FillEllipse(middleFill, middle);
            graphics.DrawEllipse(dark, middle);
            graphics.FillEllipse(innerFill, inner);
            graphics.DrawEllipse(dark, inner);

            for (int i = 0; i < 16; i++)
            {
                double angle = i * Math.PI * 2 / 16;
                PointF start = new(center.X + (float)Math.Cos(angle) * 40, center.Y + (float)Math.Sin(angle) * 40);
                PointF end = new(center.X + (float)Math.Cos(angle) * 47, center.Y + (float)Math.Sin(angle) * 47);
                graphics.DrawLine(dark, start, end);
            }

            using Font font = new(FontFamily.GenericSansSerif, 5.5f, FontStyle.Bold, GraphicsUnit.Point);
            using Brush text = new SolidBrush(Color.Black);
            DrawCentered(graphics, top, font, text, new RectangleF(center.X - 22, center.Y - 12, 44, 11));
            DrawCentered(graphics, bottom, font, text, new RectangleF(center.X - 22, center.Y + 1, 44, 11));
        }

        private void DrawSmallVolumeKnob(Graphics graphics, PointF center, string symbol, string label)
        {
            RectangleF body = new(center.X - 18, center.Y - 18, 36, 36);
            using LinearGradientBrush fill = new(body, Color.FromArgb(224, 224, 212), Color.FromArgb(86, 88, 84), LinearGradientMode.ForwardDiagonal);
            using Pen edge = new(Color.FromArgb(25, 27, 25), 1.4f);
            using Brush ink = new SolidBrush(Color.FromArgb(20, 22, 20));
            using Font symbolFont = new(FontFamily.GenericSansSerif, 7f, FontStyle.Bold, GraphicsUnit.Point);
            using Font labelFont = new(FontFamily.GenericSansSerif, 5.5f, FontStyle.Bold, GraphicsUnit.Point);
            graphics.FillEllipse(fill, body);
            graphics.DrawEllipse(edge, body);
            DrawCentered(graphics, symbol, symbolFont, ink, body);
            DrawCentered(graphics, label, labelFont, ink, new RectangleF(center.X - 30, center.Y + 20, 60, 13));
        }

        private static void DrawFastener(Graphics graphics, float x, float y)
        {
            RectangleF screw = new(x - 4, y - 4, 8, 8);
            using Brush fill = new SolidBrush(Color.FromArgb(93, 95, 91));
            using Pen edge = new(Color.FromArgb(40, 42, 40), 1);
            graphics.FillEllipse(fill, screw);
            graphics.DrawEllipse(edge, screw);
            graphics.DrawLine(edge, x - 2.5f, y, x + 2.5f, y);
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = radius * 2;
            GraphicsPath path = new();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private Font DisplayFont(float baseSize, bool bold)
        {
            float adjustment = zoomLevel switch { 0 => -0.3f, 2 => 0.5f, _ => 0f };
            return new Font(displayFontFamily, Math.Max(6, baseSize + adjustment), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
        }

        private static void DrawCentered(Graphics graphics, string text, Font font, Brush brush, RectangleF bounds)
        {
            using StringFormat format = new()
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            graphics.DrawString(text ?? string.Empty, font, brush, bounds, format);
        }

        private void PanelMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || preferences.ScreenOnlyMode)
            {
                return;
            }

            PointF logical = ToLogicalPoint(e.Location);

            foreach (PanelButton button in panelButtons)
            {
                if (button.Bounds.Contains(logical))
                {
                    pressedPanelButton = button;
                    SetArtworkFeedback(
                        button.ArtworkControl,
                        button.Command == Vns430Command.RangeOut ? "decrease-pressed"
                            : button.Command == Vns430Command.RangeIn ? "increase-pressed"
                            : "pressed",
                        held: true);
                    Capture = true;
                    Invalidate();
                    return;
                }
            }

            if (KnobPushCommandAt(logical, new PointF(878, 326)) == Vns430Command.CursorPush)
            {
                pressedCursor = true;
                SetArtworkFeedback("right_encoder", "pushed", held: true);
                Capture = true;
                Invalidate();
                return;
            }

            if (ScreenBounds.Contains(logical))
            {
                pressedScreen = true;
                screenPressOrigin = e.Location;
                screenDragging = false;
                Capture = true;
            }
        }

        // A press-and-drag on the LCD moves the window (the LCD doubles as a grab point);
        // a press-and-release without crossing the threshold is treated as a click.
        private void PanelMouseMove(object sender, MouseEventArgs e)
        {
            if (preferences.ScreenOnlyMode || !pressedScreen || screenDragging)
            {
                return;
            }

            if (Math.Abs(e.X - screenPressOrigin.X) > ScreenModeResizeBorder ||
                Math.Abs(e.Y - screenPressOrigin.Y) > ScreenModeResizeBorder)
            {
                // Promote to a window drag and cancel the pending screen click.
                screenDragging = true;
                pressedScreen = false;
                ReleasePointerPress();
                ReleaseCapture();
                SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
            }
        }

        private void PanelMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || preferences.ScreenOnlyMode)
            {
                return;
            }

            PointF logical = ToLogicalPoint(e.Location);
            PanelButton releasedButton = pressedPanelButton;
            bool releasedCursor = pressedCursor;
            bool releasedScreen = pressedScreen;
            ReleasePointerPress();

            if (releasedButton != null && releasedButton.Bounds.Contains(logical))
            {
                ExecuteCommand(releasedButton.Command);
                ClearArtworkFeedback();
            }
            else if (releasedCursor && KnobPushCommandAt(logical, new PointF(878, 326)) == Vns430Command.CursorPush)
            {
                ExecuteCommand(Vns430Command.CursorPush);
                ClearArtworkFeedback();
            }
            else if (releasedScreen && ScreenBounds.Contains(logical))
            {
                HandleScreenClick(logical);
            }

            Invalidate();
        }

        private void HandleCompanionCommand(Vns430Command command)
        {
            // The DCDU/CDU input set is one contiguous block: line-select keys and panel
            // shortcuts (DcduLeftLsk1..DcduHide, 19..38) followed by the whole 737 keypad
            // (CduAlphaA..CduBrightnessDown, 39..97). It must all route to the backend.
            //
            // This upper bound used to stop at DcduHide, so every keypad command fell
            // through: with hardware keys ON the second branch was skipped and they were
            // dropped, and with it OFF they were handed to ExecuteCommand, which only
            // understands the GNS430's own 1..18. The result was that the LSKs worked from
            // hardware but no letter, digit, EXEC, MENU or PAGE key ever did.
            if (command >= Vns430Command.DcduLeftLsk1 && command <= Vns430Command.CduBrightnessDown)
            {
                if (preferences.DcduCompanionMode)
                {
                    backend.HandleDcduCompanionCommand(command);
                }

                return;
            }

            // GNS430 native commands (1..18) are only live when hardware keys are off.
            if (!preferences.DcduCompanionMode)
            {
                ExecuteCommand(command);
            }
        }

        internal bool DcduCompanionMode => preferences.DcduCompanionMode;

        // True when the MSFS WASM module is connected and recently sending data, i.e. when
        // MobiFlight hardware keybinds will actually drive the panel.
        internal bool CompanionModuleActive => companionInput != null && companionInput.ModuleActive;

        internal bool SetDcduCompanionMode(bool enabled, out string error)
        {
            error = string.Empty;
            preferences.DcduCompanionMode = enabled;
            if (enabled)
            {
                preferences.CompanionModuleEnabled = true;
            }

            if (enabled && !companionInput.Enabled)
            {
                if (!companionInput.TryEnable(Handle, WmAppSimConnect, out error))
                {
                    preferences.Save(Bounds);
                    return true;
                }
            }

            preferences.Save(Bounds);
            Invalidate();
            return true;
        }

        private void PanelMouseWheel(object sender, MouseEventArgs e)
        {
            if (preferences.ScreenOnlyMode)
            {
                return;
            }

            Vns430Command command = KnobWheelCommandAt(ToLogicalPoint(e.Location), new PointF(878, 326), e.Delta);
            if (command == Vns430Command.None)
            {
                return;
            }

            int detents = Math.Max(1, Math.Abs(e.Delta) / Math.Max(1, SystemInformation.MouseWheelScrollDelta));
            for (int index = 0; index < detents; index++)
            {
                ExecuteCommand(command);
            }
        }

        private void PanelMouseCaptureChanged(object sender, EventArgs e)
        {
            if (!Capture && (pressedPanelButton != null || pressedCursor || pressedScreen))
            {
                ReleasePointerPress();
                Invalidate();
            }
        }

        private void ReleasePointerPress()
        {
            pressedPanelButton = null;
            pressedCursor = false;
            pressedScreen = false;
            ClearArtworkFeedback();
            if (Capture)
            {
                Capture = false;
            }
        }

        private PointF ToLogicalPoint(Point point)
        {
            return new PointF(
                point.X * LogicalWidth / (float)Math.Max(1, ClientSize.Width),
                point.Y * LogicalHeight / (float)Math.Max(1, ClientSize.Height));
        }

        internal static Vns430Command KnobPushCommandAt(PointF point, PointF center)
        {
            float dx = point.X - center.X;
            float dy = point.Y - center.Y;
            float distance = (float)Math.Sqrt((dx * dx) + (dy * dy));
            return distance < 27 ? Vns430Command.CursorPush : Vns430Command.None;
        }

        internal static Vns430Command KnobWheelCommandAt(PointF point, PointF center, int delta)
        {
            if (delta == 0)
            {
                return Vns430Command.None;
            }

            float dx = point.X - center.X;
            float dy = point.Y - center.Y;
            float distance = (float)Math.Sqrt((dx * dx) + (dy * dy));
            if (distance > 78)
            {
                return Vns430Command.None;
            }

            if (distance < 49)
            {
                return delta < 0 ? Vns430Command.SmallRightDecrease : Vns430Command.SmallRightIncrease;
            }

            return delta < 0 ? Vns430Command.LargeRightDecrease : Vns430Command.LargeRightIncrease;
        }

        private void HandleScreenClick(PointF point)
        {
            float screenX = (point.X - ScreenBounds.X) * Vns430LcdRenderer.Width / ScreenBounds.Width;
            float screenY = (point.Y - ScreenBounds.Y) * Vns430LcdRenderer.Height / ScreenBounds.Height;
            if (page == Vns430Page.Status && screenY >= 10 && screenY < 100 && screenX >= 59)
            {
                selectedIndex = screenY < 25 ? 0
                    : screenY < 52 ? 2
                    : screenY < 78 ? 1
                    : 3;
                cursorActive = true;
                ActivateSelection();
            }
            else if (page == Vns430Page.Messages && screenY >= 13 && screenY < 111 && screenX >= 61)
            {
                int first = Math.Max(0, selectedIndex - 5);
                selectedIndex = Math.Clamp(first + (int)((screenY - 13) / 14), 0, Math.Max(0, VisibleMessages().Count - 1));
                cursorActive = true;
                ActivateSelection();
            }
            else if (page == Vns430Page.Menu && screenY >= 40 && screenY < 103 && screenX >= 72 && screenX <= 223)
            {
                int first = Math.Max(0, selectedIndex - 5);
                selectedIndex = Math.Clamp(first + (int)((screenY - 40) / 9), 0, MenuItems().Count - 1);
                ActivateSelection();
            }
            else if ((page == Vns430Page.AtcMenu || page == Vns430Page.AocMenu) && screenY >= 12 && screenY < 112 && screenX >= 59)
            {
                int count = page == Vns430Page.AtcMenu ? AtcMenuItems.Length : AocMenuItems.Length;
                int first = Math.Max(0, selectedIndex - 5);
                selectedIndex = Math.Clamp(first + (int)((screenY - 12) / 14), 0, count - 1);
                cursorActive = true;
                ActivateSelection();
            }
            else if ((page == Vns430Page.AtcRequest || page == Vns430Page.AocRequest) && workflow != null && screenY >= 10 && screenY < 113 && screenX >= 59)
            {
                int first = Math.Max(0, selectedIndex - 2);
                selectedIndex = Math.Clamp(first + (int)((screenY - 11) / 22), 0, workflow.Fields.Count - 1);
                workflowCharacter = 0;
                cursorActive = true;
            }
            else if (page == Vns430Page.LoadControl && loadSession != null && screenY >= 10 && screenY < 105 && screenX >= 59)
            {
                int first = Math.Max(0, selectedIndex - 4);
                selectedIndex = Math.Clamp(first + (int)((screenY - 12) / 15), 0, loadSession.FieldCount - 1);
                cursorActive = true;
            }
        }

        // Panel face -> action -> L:EASYCPDLC_VNS_COMMAND value. Every face button sends
        // a different command, and 6 to 18 are each claimed exactly once; 1 to 5 belong
        // to the two encoder rings and the cursor push. The legends are stock GNS 430
        // nomenclature and do not describe the datalink action, so this is the mapping:
        //
        //   VLOC -> message log, all messages      17   MENU -> menu overlay          8
        //   CDI  -> connect/disconnect VATSIM      14   ENT  -> activate selection    6
        //   OBS  -> toggle cursor                  13   CLR  -> back/clear            7
        //   MSG  -> messages, unread first          9   D->  -> Hoppie logon page    12
        //   FPL  -> ATC request menu               10   RNG- -> smaller LCD text     16
        //   PROC -> AOC/telex menu                 11   RNG+ -> larger LCD text      15
        //
        // COM has no hotspot, so 18 (Power) reaches the panel only through the module
        // L-var, where a hardware key showing and hiding the window is still useful.
        // Vns430Tests.PanelButtons_EachBindToADistinctCommand keeps this one-to-one.
        private List<PanelButton> CreatePanelButtons()
        {
            return new List<PanelButton>
            {
                // COM and VLOC used to repeat CDI and MSG, so four faces drove two
                // actions. VLOC now takes the message log, which had no face at all.
                // COM has no hotspot: a flip-flop key has no datalink meaning, and the
                // only command left for it was Power, which is not worth a key when the
                // window closes itself. Label is the legend printed on the artwork, not
                // the function; the mapping is documented above this method.
                new() { Bounds = new RectangleF(153, 132, 54, 72), Label = "VLOC", ArtworkControl = "vloc_flip", Command = Vns430Command.Nearest },
                new() { Bounds = new RectangleF(779, 151, 76, 55), Label = "CLR", ArtworkControl = "clear", Command = Vns430Command.Clear },
                new() { Bounds = new RectangleF(779, 94, 76, 53), Label = "D->", ArtworkControl = "direct_to", Command = Vns430Command.DirectTo },
                new() { Bounds = new RectangleF(781, 40, 75, 54), Label = "RNG-", ArtworkControl = "range", Command = Vns430Command.RangeOut },
                new() { Bounds = new RectangleF(856, 40, 76, 54), Label = "RNG+", ArtworkControl = "range", Command = Vns430Command.RangeIn },
                new() { Bounds = new RectangleF(856, 94, 76, 53), Label = "MENU", ArtworkControl = "menu", Command = Vns430Command.Menu },
                new() { Bounds = new RectangleF(856, 151, 76, 55), Label = "ENT", ArtworkControl = "enter", Command = Vns430Command.Enter },
                new() { Bounds = new RectangleF(243, 343, 82, 50), Label = "CDI", ArtworkControl = "cdi", Command = Vns430Command.Cdi },
                new() { Bounds = new RectangleF(350, 343, 83, 50), Label = "OBS", ArtworkControl = "obs", Command = Vns430Command.Obs },
                new() { Bounds = new RectangleF(457, 343, 84, 50), Label = "MSG", ArtworkControl = "msg", Command = Vns430Command.Message },
                new() { Bounds = new RectangleF(567, 343, 84, 50), Label = "FPL", ArtworkControl = "fpl", Command = Vns430Command.FlightPlan },
                new() { Bounds = new RectangleF(674, 343, 88, 50), Label = "PROC", ArtworkControl = "proc", Command = Vns430Command.Procedure }
            };
        }

        private void PanelFormClosing(object sender, FormClosingEventArgs e)
        {
            preferences.Save(Bounds);
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        }

        internal static bool ShouldPreserveMessageSelection(Vns430Page targetPage)
        {
            return targetPage == Vns430Page.Messages || targetPage == Vns430Page.MessageDetail;
        }

        internal static int Wrap(int value, int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            int result = value % count;
            return result < 0 ? result + count : result;
        }

        internal static string CollapseWhitespace(string value)
        {
            return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        }

        internal static List<string> WrapText(string value, int maxCharacters)
        {
            List<string> lines = new();
            string remaining = CollapseWhitespace(value);
            int width = Math.Max(8, maxCharacters);

            while (remaining.Length > width)
            {
                int split = remaining.LastIndexOf(' ', width);
                if (split < 1)
                {
                    split = width;
                }

                lines.Add(remaining.Substring(0, split).Trim());
                remaining = remaining.Substring(split).TrimStart();
            }

            if (remaining.Length > 0 || lines.Count == 0)
            {
                lines.Add(remaining);
            }
            return lines;
        }

        private static string Truncate(string value, int maxLength)
        {
            string clean = value ?? string.Empty;
            return clean.Length <= maxLength ? clean : clean.Substring(0, Math.Max(1, maxLength - 1)) + "…";
        }
    }

    internal static class Vns430FontLoader
    {
        private static readonly PrivateFontCollection Fonts = LoadFonts();

        internal static FontFamily Family => Fonts.Families.FirstOrDefault()
            ?? FontFamily.GenericMonospace;

        private static PrivateFontCollection LoadFonts()
        {
            PrivateFontCollection collection = new();
            AddFont(collection, Properties.Resources.B612Mono_Regular);
            AddFont(collection, Properties.Resources.B612Mono_Bold);
            return collection;
        }

        private static void AddFont(PrivateFontCollection collection, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            IntPtr memory = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                collection.AddMemoryFont(memory, bytes.Length);
            }
            finally
            {
                Marshal.FreeCoTaskMem(memory);
            }
        }
    }
}
