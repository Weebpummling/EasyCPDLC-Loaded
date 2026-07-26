using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyCPDLC.VNS430
{
    internal sealed class Vns430LcdState
    {
        internal Vns430BackendSnapshot Snapshot { get; init; } = new();
        internal Vns430Page Page { get; init; }
        internal Vns430PageGroup PageGroup { get; init; }
        internal bool CursorActive { get; init; }
        internal int SelectedIndex { get; init; }
        internal int DetailScrollLine { get; init; }
        internal int ResponseIndex { get; init; }
        internal int ZoomLevel { get; init; }
        internal string LogonCode { get; init; } = "____";
        internal int LogonCharacter { get; init; }
        internal string TransientStatus { get; init; } = string.Empty;
        internal string MessageFilter { get; init; } = "ALL";
        internal IReadOnlyList<string> MenuItems { get; init; } = Array.Empty<string>();
        internal Vns430Workflow Workflow { get; init; }
        internal int WorkflowCharacter { get; init; }
        internal Vns430LoadControlSession LoadSession { get; init; }
        internal bool OperationBusy { get; init; }
        internal string OperationStatus { get; init; } = string.Empty;

        /// <summary>
        /// A value digest of everything <see cref="Vns430LcdRenderer.Render"/> reads.
        /// The panel uses it to skip re-rendering an unchanged display.
        /// </summary>
        /// <remarks>
        /// This must cover every rendered value, including the ones reached through
        /// Snapshot, Workflow and LoadSession. Reference identity is not enough:
        /// Vns430EditField.Value and the LoadSession indices are mutated in place, so
        /// the same object graph produces a different display as the pilot types.
        /// Missing a value here shows a stale display, which is why
        /// Vns430Tests.LcdState_FingerprintCoversEveryRenderedProperty fails when a
        /// property is added without being accounted for.
        /// </remarks>
        internal ulong Fingerprint()
        {
            ulong hash = 14695981039346656037UL;
            Mix(ref hash, (int)Page);
            Mix(ref hash, (int)PageGroup);
            Mix(ref hash, CursorActive);
            Mix(ref hash, SelectedIndex);
            Mix(ref hash, DetailScrollLine);
            Mix(ref hash, ResponseIndex);
            Mix(ref hash, ZoomLevel);
            Mix(ref hash, LogonCode);
            Mix(ref hash, LogonCharacter);
            Mix(ref hash, TransientStatus);
            Mix(ref hash, MessageFilter);
            Mix(ref hash, WorkflowCharacter);
            Mix(ref hash, OperationBusy);
            Mix(ref hash, OperationStatus);

            Mix(ref hash, MenuItems?.Count ?? -1);
            if (MenuItems != null)
            {
                foreach (string item in MenuItems)
                {
                    Mix(ref hash, item);
                }
            }

            if (Snapshot == null)
            {
                Mix(ref hash, "<no snapshot>");
            }
            else
            {
                Mix(ref hash, Snapshot.Connected);
                Mix(ref hash, Snapshot.Callsign);
                Mix(ref hash, Snapshot.CurrentAtcUnit);
                Mix(ref hash, Snapshot.PendingLogon);
                Mix(ref hash, Snapshot.AtcUnitOnline);
                Mix(ref hash, Snapshot.PdcStatus);
                Mix(ref hash, Snapshot.PdcLogonCode);
                Mix(ref hash, Snapshot.PdcController);
                Mix(ref hash, Snapshot.PdcAllowReqClr);
                Mix(ref hash, Snapshot.CpdlcCandidates?.Count ?? -1);
                if (Snapshot.CpdlcCandidates != null)
                {
                    foreach (Vns430CpdlcCandidate candidate in Snapshot.CpdlcCandidates)
                    {
                        Mix(ref hash, candidate.Code);
                        Mix(ref hash, candidate.Controller);
                        Mix(ref hash, candidate.TunedMatch);
                    }
                }
                Mix(ref hash, Snapshot.Departure);
                Mix(ref hash, Snapshot.Arrival);
                Mix(ref hash, Snapshot.Aircraft);
                Mix(ref hash, Snapshot.Messages?.Count ?? -1);
                if (Snapshot.Messages != null)
                {
                    foreach (Vns430MessageSnapshot message in Snapshot.Messages)
                    {
                        Mix(ref hash, message.Type);
                        Mix(ref hash, message.Station);
                        Mix(ref hash, message.Text);
                        Mix(ref hash, message.Outbound);
                        Mix(ref hash, message.Acknowledged);
                        Mix(ref hash, message.Unread);
                        Mix(ref hash, message.Responses?.Count ?? -1);
                        if (message.Responses != null)
                        {
                            foreach (string response in message.Responses)
                            {
                                Mix(ref hash, response);
                            }
                        }
                    }
                }
            }

            if (Workflow == null)
            {
                Mix(ref hash, "<no workflow>");
            }
            else
            {
                Mix(ref hash, (int)Workflow.Kind);
                Mix(ref hash, Workflow.Title);
                Mix(ref hash, Workflow.Fields?.Count ?? -1);
                if (Workflow.Fields != null)
                {
                    foreach (Vns430EditField field in Workflow.Fields)
                    {
                        Mix(ref hash, field.Label);
                        Mix(ref hash, field.Value);
                        Mix(ref hash, field.MaxLength);
                        Mix(ref hash, field.Options?.Count ?? -1);
                    }
                }
            }

            MixLoadSession(ref hash, LoadSession);
            return hash;
        }

        private static void MixLoadSession(ref ulong hash, Vns430LoadControlSession load)
        {
            if (load == null)
            {
                Mix(ref hash, "<no load session>");
                return;
            }

            // Aircraft, Cabin and Format are rendered, but they are derived by indexing
            // into Reference, which throws while that data is still empty. This runs on
            // every tick, so hash the mutable indices and Reference's identity instead:
            // Reference is init-only, so a new set of reference data is a new object.
            Mix(ref hash, load.AircraftIndex);
            Mix(ref hash, load.CabinIndex);
            Mix(ref hash, load.FormatIndex);
            Mix(ref hash, load.LoadingTimeIndex);
            Mix(ref hash, load.Reference == null
                ? 0
                : RuntimeHelpers.GetHashCode(load.Reference));
            Mix(ref hash, load.PassengerSplit?.Count ?? -1);
            if (load.PassengerSplit != null)
            {
                foreach (PassengerClassAllocation allocation in load.PassengerSplit)
                {
                    Mix(ref hash, allocation.Code);
                    Mix(ref hash, allocation.Capacity);
                    Mix(ref hash, allocation.Passengers);
                }
            }

            if (load.Flight == null)
            {
                Mix(ref hash, "<no flight>");
                return;
            }

            Mix(ref hash, load.Flight.Airline);
            Mix(ref hash, load.Flight.FlightNumber);
            Mix(ref hash, load.Flight.AircraftRegistration);
            Mix(ref hash, load.Flight.Departure);
            Mix(ref hash, load.Flight.Destination);
            Mix(ref hash, load.Flight.PassengerCount);
        }

        private static void Mix(ref ulong hash, string value)
        {
            unchecked
            {
                if (value == null)
                {
                    hash = (hash ^ 0xFF) * 1099511628211UL;
                    return;
                }

                foreach (char character in value)
                {
                    hash = (hash ^ (byte)character) * 1099511628211UL;
                    hash = (hash ^ (byte)(character >> 8)) * 1099511628211UL;
                }

                // Length terminator, so "AB"+"C" cannot collide with "A"+"BC".
                hash = (hash ^ 0xFE) * 1099511628211UL;
            }
        }

        private static void Mix(ref ulong hash, int value)
        {
            unchecked
            {
                for (int shift = 0; shift < 32; shift += 8)
                {
                    hash = (hash ^ (byte)(value >> shift)) * 1099511628211UL;
                }
            }
        }

        private static void Mix(ref ulong hash, bool value)
        {
            unchecked
            {
                hash = (hash ^ (byte)(value ? 1 : 2)) * 1099511628211UL;
            }
        }
    }

    internal static class Vns430LcdRenderer
    {
        internal const int Width = 240;
        internal const int Height = 128;

        // Palette sampled from all 444 display captures in the Pilot's Guide.
        internal static readonly Color Blue = Color.FromArgb(56, 83, 164);
        internal static readonly Color Black = Color.FromArgb(4, 7, 7);
        internal static readonly Color Cyan = Color.FromArgb(110, 205, 221);
        internal static readonly Color Green = Color.FromArgb(105, 189, 69);
        internal static readonly Color White = Color.White;
        internal static readonly Color Yellow = Color.FromArgb(243, 236, 25);
        internal static readonly Color Magenta = Color.FromArgb(185, 81, 159);

        private const int MainLeft = 57;
        private const int FooterTop = 119;

        // applyAppearance draws the raw pixel-exact raster when false, which lets the
        // palette be asserted without the older-LCD post-process blending it away.
        internal static Bitmap Render(Vns430LcdState state, bool applyAppearance = true)
        {
            return Render(state, applyAppearance, LcdBloom, LcdSoftness);
        }

        /// <summary>
        /// Renders with explicit appearance weights. For calibrating <see cref="LcdBloom"/>
        /// and <see cref="LcdSoftness"/> against reference photos without rebuilding.
        /// </summary>
        internal static Bitmap Render(Vns430LcdState state, bool applyAppearance, double bloom, double softness)
        {
            Bitmap display = new(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(display))
            {
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.None;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.Clear(Blue);
            }

            DrawRadioStrip(display, state);
            switch (state.Page)
            {
                case Vns430Page.Status:
                    DrawStatus(display, state);
                    break;
                case Vns430Page.Messages:
                    DrawMessages(display, state);
                    break;
                case Vns430Page.MessageDetail:
                    DrawMessageDetail(display, state);
                    break;
                case Vns430Page.Logon:
                    DrawLogon(display, state);
                    break;
                case Vns430Page.AtcMenu:
                    DrawChoiceMenu(display, "ATC REQUESTS", new[] { "DIRECT TO", "LEVEL", "SPEED", "WHEN CAN WE", "FREE TEXT" }, state);
                    break;
                case Vns430Page.AocMenu:
                    DrawChoiceMenu(display, "AOC / COMPANY", new[] { "AOC TELEX", "METAR", "ATIS", "PREDEP CLEARANCE", "OCEANIC CLEARANCE", "LOAD CONTROL" }, state);
                    break;
                case Vns430Page.AtcRequest:
                case Vns430Page.AocRequest:
                    DrawWorkflow(display, state);
                    break;
                case Vns430Page.RequestReview:
                case Vns430Page.AocReview:
                    DrawWorkflowReview(display, state);
                    break;
                case Vns430Page.LoadControl:
                    DrawLoadControl(display, state);
                    break;
                case Vns430Page.LoadReview:
                    DrawLoadReview(display, state);
                    break;
                case Vns430Page.Menu:
                    DrawStatus(display, state, drawFooter: false);
                    DrawMenu(display, state);
                    break;
                case Vns430Page.Help:
                    DrawHelp(display, state);
                    break;
                case Vns430Page.Pdc:
                    DrawPdc(display, state);
                    break;
            }

            DrawFooter(display, state);
            if (!string.IsNullOrWhiteSpace(state.TransientStatus))
            {
                DrawTransient(display, state.TransientStatus);
            }

            if (applyAppearance)
            {
                ApplyLcdAppearance(display, bloom, softness);
            }

            return display;
        }

        private static void DrawRadioStrip(Bitmap display, Vns430LcdState state)
        {
            Vns430BackendSnapshot snapshot = state.Snapshot;
            Fill(display, new Rectangle(0, 0, 56, FooterTop), Blue);
            Line(display, 55, 0, 55, FooterTop - 1, Cyan);

            Text(display, 1, 0, "DLK", Cyan);
            Text(display, 1, 8, Fit(snapshot.Callsign, 8, "--------"), White);
            Line(display, 0, 18, 54, 18, Cyan);

            Text(display, 1, 21, "ATC", Cyan);
            Text(display, 1, 29, Fit(DatalinkRouting.DisplayStation(snapshot.CurrentAtcUnit), 8, "----"), Green);
            Line(display, 0, 39, 54, 39, Cyan);

            Text(display, 1, 42, "LOG", Cyan);
            Text(display, 1, 50, Fit(DatalinkRouting.DisplayStation(snapshot.PendingLogon), 8, "----"), snapshot.Connected ? Green : Yellow);
            Line(display, 0, 61, 54, 61, Cyan);

            Box(display, new Rectangle(1, 91, 53, 19), Cyan, Black);
            Vns430BitmapFont.DrawCentered(display, new Rectangle(1, 91, 53, 19), snapshot.Connected ? "ENR" : "STBY", snapshot.Connected ? Green : Yellow);
        }

        private static void DrawStatus(Bitmap display, Vns430LcdState state, bool drawFooter = true)
        {
            Header(display, "DATALINK STATUS");
            int outbound = state.Snapshot.Messages.Count(message => message.Outbound);
            int inbound = state.Snapshot.Messages.Count - outbound;
            bool selectMessages = state.CursorActive && state.SelectedIndex == 0;
            Text(display, 60, 12, "TX QUEUE", Cyan);
            DrawField(display, new Rectangle(117, 10, 16, 12), outbound.ToString(), selectMessages);
            Text(display, 143, 12, "RX QUEUE", Cyan);
            DrawField(display, new Rectangle(219, 10, 16, 12), inbound.ToString(), selectMessages);

            Text(display, 60, 26, "NETWORK / CONNECTIVITY", Cyan);
            DrawField(
                display,
                new Rectangle(59, 34, 177, 14),
                state.Snapshot.Connected ? "NETWORK CONNECTED" : "NETWORK STANDBY",
                state.CursorActive && state.SelectedIndex == 2);

            Text(display, 60, 51, "CPDLC OPERATION", Cyan);
            string station = Fit(DatalinkRouting.DisplayStation(state.Snapshot.CurrentAtcUnit), 8, "NO LOGON");
            DrawField(
                display,
                new Rectangle(59, 59, 177, 14),
                state.Snapshot.Connected ? "ACTIVE " + station : "LOGON AVAILABLE",
                state.CursorActive && state.SelectedIndex == 1);

            Text(display, 60, 76, "CALLSIGN / ATC REQUEST", Cyan);
            string callsign = Fit(state.Snapshot.Callsign, 8, "--------");
            DrawField(
                display,
                new Rectangle(59, 84, 177, 14),
                callsign + "  SELECT",
                state.CursorActive && state.SelectedIndex == 3);

            Text(display, 60, 103, state.CursorActive ? "ENT TO ACTIVATE" : "PUSH CRSR FOR OPTIONS", White);
        }

        private static void DrawField(Bitmap display, Rectangle bounds, string value, bool selected)
        {
            Box(display, bounds, selected ? White : Cyan, selected ? White : Black);
            Text(display, bounds.X + 3, bounds.Y + 3, Fit(value, Math.Max(1, (bounds.Width - 6) / 6), string.Empty), selected ? Black : Green);
        }

        // Received / sent / all message filter, shared by the renderer and the form so the
        // selection index maps to the same list in both.
        internal static IReadOnlyList<Vns430MessageSnapshot> FilterMessages(
            IReadOnlyList<Vns430MessageSnapshot> messages, string filter)
        {
            if (messages == null)
            {
                return Array.Empty<Vns430MessageSnapshot>();
            }
            return filter switch
            {
                "RECEIVED" => messages.Where(message => !message.Outbound).ToList(),
                "SENT" => messages.Where(message => message.Outbound).ToList(),
                _ => messages
            };
        }

        private static void DrawPdc(Bitmap display, Vns430LcdState state)
        {
            Vns430BackendSnapshot snapshot = state.Snapshot;
            Header(display, "PRE-DEPARTURE CLEARANCE");
            Box(display, new Rectangle(59, 10, 178, 105), Cyan, Black);

            Text(display, 63, 16, "STATUS", Cyan);
            DrawField(display, new Rectangle(61, 24, 174, 14),
                string.IsNullOrWhiteSpace(snapshot.PdcStatus) ? "NONE" : snapshot.PdcStatus, false);

            Text(display, 63, 42, "FACILITY", Cyan);
            string facility = string.IsNullOrWhiteSpace(snapshot.PdcLogonCode) ? "----" : DatalinkRouting.DisplayStation(snapshot.PdcLogonCode);
            if (!string.IsNullOrWhiteSpace(snapshot.PdcController) &&
                !string.Equals(snapshot.PdcController, facility, StringComparison.OrdinalIgnoreCase))
            {
                facility += "  " + snapshot.PdcController;
            }
            DrawField(display, new Rectangle(61, 50, 174, 14), facility, false);

            if (snapshot.PdcAllowReqClr)
            {
                DrawField(display, new Rectangle(61, 78, 174, 14), "REQUEST CLEARANCE", state.CursorActive);
                Text(display, 63, 98, state.CursorActive ? "ENT TO REQUEST" : "PUSH CRSR TO REQUEST", White);
            }
            else
            {
                Vns430BitmapFont.DrawCentered(display, new Rectangle(59, 74, 178, 20), "NO CLEARANCE AVAILABLE", Green);
            }
        }

        private static void DrawMessages(Bitmap display, Vns430LcdState state)
        {
            Header(display, state.MessageFilter == "ALL" ? "DATALINK MESSAGES" : "MESSAGES - " + state.MessageFilter);
            Box(display, new Rectangle(59, 10, 178, 105), Cyan, Black);
            IReadOnlyList<Vns430MessageSnapshot> messages = FilterMessages(state.Snapshot.Messages, state.MessageFilter);
            if (messages.Count == 0)
            {
                Vns430BitmapFont.DrawCentered(display, new Rectangle(59, 10, 178, 105), "NO MESSAGES", Green);
                return;
            }

            int first = Math.Max(0, state.SelectedIndex - 5);
            int visible = 7;
            for (int index = first; index < Math.Min(messages.Count, first + visible); index++)
            {
                int row = index - first;
                int y = 13 + (row * 14);
                bool selected = state.CursorActive && index == state.SelectedIndex;
                Rectangle bounds = new(61, y, 173, 13);
                if (selected)
                {
                    Fill(display, bounds, Green);
                }

                Vns430MessageSnapshot message = messages[index];
                string marker = message.Outbound ? ">" : message.Unread ? "*" : "<";
                string left = marker + " " + Fit(message.Type, 7, "MSG");
                string right = Fit(DatalinkRouting.DisplayStation(message.Station), 6, "----");
                Text(display, 63, y + 3, left, selected ? Black : Green);
                TextRight(display, 231, y + 3, right, selected ? Black : Cyan);
            }
            ScrollBar(display, first, messages.Count, visible);
        }

        private static void DrawMessageDetail(Bitmap display, Vns430LcdState state)
        {
            Header(display, "MESSAGES");
            Box(display, new Rectangle(59, 10, 178, 105), Cyan, Black);
            Vns430MessageSnapshot message = SelectedMessage(state);
            if (message == null)
            {
                Text(display, 64, 18, "NO MESSAGE SELECTED", Green);
                return;
            }

            Text(display, 63, 13, (message.Outbound ? "SENT " : "RECEIVED ") + Fit(DatalinkRouting.DisplayStation(message.Station), 7, "----"), Cyan);
            Line(display, 61, 22, 234, 22, Cyan);
            List<string> lines = Vns430Form.WrapText(Vns430Form.CollapseWhitespace(message.Text), state.ZoomLevel == 2 ? 24 : 28);
            int visibleLines = message.Responses.Count > 0 ? 8 : 10;
            int first = Math.Clamp(state.DetailScrollLine, 0, Math.Max(0, lines.Count - visibleLines));
            for (int index = first; index < Math.Min(lines.Count, first + visibleLines); index++)
            {
                Text(display, 63, 26 + ((index - first) * 9), Fit(lines[index], 28, string.Empty), Green);
            }

            if (message.Responses.Count > 0)
            {
                string response = message.Responses[Math.Clamp(state.ResponseIndex, 0, message.Responses.Count - 1)];
                Rectangle responseBox = new(87, 101, 120, 12);
                Box(display, responseBox, White, state.CursorActive ? White : Black);
                Vns430BitmapFont.DrawCentered(display, responseBox, Fit(response, 18, string.Empty), state.CursorActive ? Black : Green);
            }
        }

        private static void DrawLogon(Bitmap display, Vns430LcdState state)
        {
            Header(display, "SELECT LOGON FACILITY");
            Box(display, new Rectangle(59, 10, 178, 105), Cyan, Black);
            Text(display, 66, 18, "IDENT", Cyan);

            string code = (state.LogonCode ?? "____").PadRight(4, '_').Substring(0, 4);
            for (int index = 0; index < 4; index++)
            {
                Rectangle character = new(105 + (index * 18), 31, 15, 15);
                bool selected = state.CursorActive && state.LogonCharacter == index;
                Box(display, character, White, selected ? White : Black);
                Vns430BitmapFont.DrawCentered(display, character, code[index].ToString(), selected ? Black : Green);
            }

            Line(display, 61, 54, 234, 54, Cyan);

            // Online CPDLC facilities discovered from VATSIM/Hoppie. A tuned-frequency
            // match is shown green; otherwise white. Falls back to the current/pending
            // read-out when nothing is online.
            IReadOnlyList<Vns430CpdlcCandidate> candidates = state.Snapshot?.CpdlcCandidates;
            if (candidates != null && candidates.Count > 0)
            {
                Text(display, 66, 58, "ONLINE CPDLC", Cyan);
                int rows = Math.Min(3, candidates.Count);
                for (int index = 0; index < rows; index++)
                {
                    Vns430CpdlcCandidate candidate = candidates[index];
                    int y = 68 + (index * 12);
                    Color colour = candidate.TunedMatch ? Green : White;
                    Text(display, 66, y, Fit(DatalinkRouting.DisplayStation(candidate.Code), 5, "----"), colour);
                    // The network this logon would leave on, so the row cannot be
                    // mistaken for the other side's.
                    Text(display, 104, y, Fit(candidate.Reason, 11, string.Empty), Cyan);
                    TextRight(display, 230, y, Fit(candidate.Controller, 10, string.Empty), colour);
                }
            }
            else
            {
                Text(display, 66, 61, "CURRENT", Cyan);
                TextRight(display, 230, 61, Fit(state.Snapshot.CurrentAtcUnit, 8, "NONE"), Green);
                Text(display, 66, 77, "PENDING", Cyan);
                TextRight(display, 230, 77, Fit(state.Snapshot.PendingLogon, 8, "NONE"), Yellow);
            }

            Text(display, 66, 106, "ENT TO ACTIVATE", White);
        }

        private static void DrawMenu(Bitmap display, Vns430LcdState state)
        {
            Rectangle shadow = new(77, 28, 151, 82);
            Dither(display, shadow, White, Blue);
            Rectangle menu = new(72, 23, 151, 82);
            Box(display, menu, White, Blue);
            Box(display, new Rectangle(75, 26, 145, 11), Cyan, Blue);
            Vns430BitmapFont.DrawCentered(display, new Rectangle(75, 26, 145, 11), "PAGE MENU", Cyan);

            int first = Math.Max(0, state.SelectedIndex - 5);
            for (int index = first; index < Math.Min(state.MenuItems.Count, first + 7); index++)
            {
                int row = index - first;
                int y = 40 + (row * 9);
                bool selected = index == state.SelectedIndex;
                if (selected)
                {
                    Fill(display, new Rectangle(76, y - 1, 141, 9), White);
                }
                Text(display, 78, y, Fit(state.MenuItems[index], 22, string.Empty), selected ? Black : White);
            }
        }

        private static void DrawChoiceMenu(Bitmap display, string title, IReadOnlyList<string> items, Vns430LcdState state)
        {
            Header(display, title);
            Box(display, new Rectangle(59, 10, 178, 105), Cyan, Black);
            int first = Math.Max(0, state.SelectedIndex - 5);
            for (int index = first; index < Math.Min(items.Count, first + 7); index++)
            {
                int y = 14 + ((index - first) * 14);
                bool selected = state.CursorActive && index == state.SelectedIndex;
                if (selected)
                {
                    Fill(display, new Rectangle(61, y - 2, 173, 12), Green);
                }
                Text(display, 64, y, Fit(items[index], 27, string.Empty), selected ? Black : Green);
                TextRight(display, 230, y, ">", selected ? Black : White);
            }
        }

        private static void DrawWorkflow(Bitmap display, Vns430LcdState state)
        {
            Vns430Workflow workflow = state.Workflow;
            Header(display, workflow?.Title ?? "REQUEST");
            Box(display, new Rectangle(59, 10, 178, 105), Cyan, Black);
            if (workflow == null || workflow.Fields.Count == 0)
            {
                Text(display, 64, 18, "NO REQUEST SELECTED", Yellow);
                return;
            }

            int first = Math.Max(0, state.SelectedIndex - 2);
            for (int index = first; index < Math.Min(workflow.Fields.Count, first + 4); index++)
            {
                Vns430EditField field = workflow.Fields[index];
                int y = 13 + ((index - first) * 22);
                bool selected = state.CursorActive && index == state.SelectedIndex;
                Text(display, 63, y, Fit(field.Label, 13, string.Empty), Cyan);
                string value = field.IsOption
                    ? field.CleanValue
                    : EditWindow(field, selected ? state.WorkflowCharacter : 0, 14);
                Rectangle valueBounds = new(145, y - 2, 88, 12);
                Box(display, valueBounds, selected ? White : Cyan, selected ? White : Black);
                TextRight(display, 230, y, Fit(value, 14, "_"), selected ? Black : Green);
                if (selected && !field.IsOption)
                {
                    int cursor = Math.Clamp(state.WorkflowCharacter, 0, Math.Max(0, field.MaxLength - 1));
                    Text(display, 63, y + 9, "POS " + (cursor + 1) + "/" + field.MaxLength, Yellow);
                }
            }
            Text(display, 63, 103, "ENT REVIEW   CLR ERASE", White);
            ScrollBar(display, first, workflow.Fields.Count, 4);
        }

        private static string EditWindow(Vns430EditField field, int cursor, int width)
        {
            string value = (field.Value ?? string.Empty).PadRight(field.MaxLength, '_').Substring(0, field.MaxLength);
            int first = Math.Clamp(cursor - width + 1, 0, Math.Max(0, value.Length - width));
            return value.Substring(first, Math.Min(width, value.Length - first));
        }

        private static void DrawWorkflowReview(Bitmap display, Vns430LcdState state)
        {
            Header(display, "REVIEW / SEND");
            Box(display, new Rectangle(59, 10, 178, 105), Cyan, Black);
            if (state.Workflow == null)
            {
                Text(display, 64, 18, "NO REQUEST", Yellow);
                return;
            }

            Text(display, 63, 13, Fit(state.Workflow.Title, 28, string.Empty), Cyan);
            string message = state.Workflow.BuildMessage(state.Snapshot);
            List<string> lines = Vns430Form.WrapText(message, 28);
            for (int index = 0; index < Math.Min(8, lines.Count); index++)
            {
                Text(display, 63, 26 + (index * 9), Fit(lines[index], 28, string.Empty), Green);
            }
            Text(display, 63, 102, state.OperationBusy ? "SENDING..." : "ENT SEND   CLR EDIT", state.OperationBusy ? Yellow : White);
        }

        private static void DrawLoadControl(Bitmap display, Vns430LcdState state)
        {
            Header(display, "ELOADCONTROL");
            Box(display, new Rectangle(59, 10, 178, 105), Cyan, Black);
            if (state.LoadSession == null)
            {
                Text(display, 64, 18, Fit(state.OperationStatus, 27, "ENT LOAD SIMBRIEF"), state.OperationBusy ? Yellow : Green);
                Text(display, 64, 34, state.OperationBusy ? "PLEASE WAIT" : "ENT TO RETRY", White);
                return;
            }

            Vns430LoadControlSession load = state.LoadSession;
            string[] labels = new[] { "AIRCRAFT", "CABIN", "FORMAT" }
                .Concat(load.PassengerSplit.Select(item => "PAX " + item.Code + "/" + item.Capacity))
                .Concat(new[] { "LOAD TIME" })
                .ToArray();
            string[] values = new[] { load.Aircraft.Icao, load.Cabin, load.Format.Name }
                .Concat(load.PassengerSplit.Select(item => item.Passengers.ToString()))
                .Concat(new[] { load.LoadingTimeLabel })
                .ToArray();
            int first = Math.Max(0, state.SelectedIndex - 4);
            for (int index = first; index < Math.Min(labels.Length, first + 6); index++)
            {
                int y = 14 + ((index - first) * 15);
                bool selected = state.CursorActive && index == state.SelectedIndex;
                if (selected)
                {
                    Fill(display, new Rectangle(61, y - 2, 173, 12), Green);
                }
                Text(display, 63, y, Fit(labels[index], 15, string.Empty), selected ? Black : Cyan);
                TextRight(display, 231, y, Fit(values[index], 12, string.Empty), selected ? Black : Green);
            }
            int total = load.PassengerSplit.Sum(item => item.Passengers);
            Text(display, 63, 103, "TOTAL " + total + "/" + load.Flight.PassengerCount + "  ENT REVIEW", total == load.Flight.PassengerCount ? White : Yellow);
        }

        private static void DrawLoadReview(Bitmap display, Vns430LcdState state)
        {
            Header(display, "LOADSHEET REVIEW");
            Box(display, new Rectangle(59, 10, 178, 105), Cyan, Black);
            Vns430LoadControlSession load = state.LoadSession;
            if (load == null)
            {
                Text(display, 64, 18, "LOAD DATA NOT READY", Yellow);
                return;
            }
            Text(display, 63, 14, Fit(load.Flight.Airline + load.Flight.FlightNumber, 14, string.Empty), Green);
            TextRight(display, 231, 14, Fit(load.Flight.Departure + "-" + load.Flight.Destination, 11, string.Empty), Cyan);
            Text(display, 63, 30, "TYPE", Cyan);
            TextRight(display, 231, 30, Fit(load.Aircraft.Icao + " " + load.Flight.AircraftRegistration, 18, string.Empty), Green);
            Text(display, 63, 46, "CABIN", Cyan);
            TextRight(display, 231, 46, Fit(load.Cabin, 18, string.Empty), Green);
            Text(display, 63, 62, "PAX", Cyan);
            TextRight(display, 231, 62, load.PassengerSplit.Sum(item => item.Passengers).ToString(), Green);
            Text(display, 63, 78, "FORMAT", Cyan);
            TextRight(display, 231, 78, Fit(load.Format.Name, 18, string.Empty), Green);
            Text(display, 63, 101, state.OperationBusy ? "GENERATING..." : "ENT GENERATE  CLR EDIT", state.OperationBusy ? Yellow : White);
        }

        /// <summary>
        /// The UTILITY page lines. Shared with Vns430Form so the cursor cannot run past
        /// the end of the list when the two get out of step.
        /// </summary>
        internal static readonly string[] HelpItems =
        {
            "INPUT CONFIGURATION",
            "PRIVATE MSFS MODULE",
            "MOBIFLIGHT PROFILE",
            "MOUSE INPUT ONLY",
            "NO GPS EVENTS",
            "RETURN TO DATALINK"
        };

        private static void DrawHelp(Bitmap display, Vns430LcdState state)
        {
            Header(display, "UTILITY");
            Box(display, new Rectangle(59, 10, 178, 105), Cyan, Black);
            string[] items = HelpItems;
            for (int index = 0; index < items.Length; index++)
            {
                bool selected = state.CursorActive && state.SelectedIndex == index;
                int y = 16 + (index * 15);
                if (selected)
                {
                    Fill(display, new Rectangle(62, y - 2, 172, 12), Green);
                }
                Text(display, 64, y, items[index], selected ? Black : Green);
            }
        }

        private static void Header(Bitmap display, string title)
        {
            Fill(display, new Rectangle(MainLeft, 0, Width - MainLeft, 9), Blue);
            Vns430BitmapFont.DrawCentered(display, new Rectangle(MainLeft, 0, Width - MainLeft, 9), title, White);
        }

        private static void DrawFooter(Bitmap display, Vns430LcdState state)
        {
            Fill(display, new Rectangle(0, FooterTop, Width, Height - FooterTop), Black);
            Text(display, 2, 120, "GPS", Green);
            Text(display, 58, 120, state.CursorActive ? "CRSR" : string.Empty, Green);
            bool unread = state.Snapshot.Messages.Any(message => message.Unread && !message.Outbound);
            Text(display, 111, 120, unread ? "MSG" : string.Empty, Yellow);

            string group = PageGroupLabel(state.PageGroup);
            int groupX = 219 - Vns430BitmapFont.Measure(group);
            int pageCount = Vns430Form.PagesForGroup(state.PageGroup).Length;
            Vns430Page[] pages = Vns430Form.PagesForGroup(state.PageGroup);
            int current = Math.Max(0, Array.IndexOf(pages, state.Page));
            int squaresX = groupX - (pageCount * 5) - 3;
            for (int index = 0; index < pageCount; index++)
            {
                Rectangle square = new(squaresX + (index * 5), 121, 4, 6);
                Box(display, square, White, index == current ? White : Blue);
            }
            Text(display, groupX, 120, group, White);
        }

        internal static string PageGroupLabel(Vns430PageGroup group)
        {
            return group switch
            {
                Vns430PageGroup.Nav => "DLK",
                Vns430PageGroup.Wpt => "ATC",
                Vns430PageGroup.Aux => "AOC",
                Vns430PageGroup.Nrst => "MSG",
                _ => "DLK"
            };
        }

        private static void DrawTransient(Bitmap display, string text)
        {
            Rectangle popup = new(76, 94, 144, 18);
            Box(display, popup, White, Black);
            Vns430BitmapFont.DrawCentered(display, popup, Fit(text, 22, string.Empty), Green);
        }

        private static Vns430MessageSnapshot SelectedMessage(Vns430LcdState state)
        {
            IReadOnlyList<Vns430MessageSnapshot> messages = FilterMessages(state.Snapshot.Messages, state.MessageFilter);
            return messages.Count == 0
                ? null
                : messages[Math.Clamp(state.SelectedIndex, 0, messages.Count - 1)];
        }

        private static void ScrollBar(Bitmap display, int first, int total, int visible)
        {
            if (total <= visible)
            {
                return;
            }
            Line(display, 235, 13, 235, 109, White);
            int height = Math.Max(8, (96 * visible) / total);
            int travel = 96 - height;
            int y = 13 + ((travel * first) / Math.Max(1, total - visible));
            Fill(display, new Rectangle(234, y, 3, height), Cyan);
        }

        private static void DrawRightArrow(Bitmap display, int x, int y, Color color)
        {
            Line(display, x, y, x + 9, y, color);
            Line(display, x + 9, y, x + 6, y - 3, color);
            Line(display, x + 9, y, x + 6, y + 3, color);
        }

        private static void Text(Bitmap display, int x, int y, string value, Color color)
        {
            Vns430BitmapFont.Draw(display, x, y, value ?? string.Empty, color);
        }

        private static void TextRight(Bitmap display, int right, int y, string value, Color color)
        {
            string text = value ?? string.Empty;
            Text(display, right - Vns430BitmapFont.Measure(text), y, text, color);
        }

        private static string Fit(string value, int length, string fallback)
        {
            string clean = string.IsNullOrWhiteSpace(value) ? fallback : Vns430Form.CollapseWhitespace(value).ToUpperInvariant();
            return clean.Length <= length ? clean : clean.Substring(0, length);
        }

        private static void Box(Bitmap display, Rectangle bounds, Color outline, Color fill)
        {
            Fill(display, bounds, fill);
            using Graphics graphics = Graphics.FromImage(display);
            graphics.SmoothingMode = SmoothingMode.None;
            using Pen pen = new(outline, 1);
            graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }

        private static void Fill(Bitmap display, Rectangle bounds, Color color)
        {
            using Graphics graphics = Graphics.FromImage(display);
            graphics.SmoothingMode = SmoothingMode.None;
            using Brush brush = new SolidBrush(color);
            graphics.FillRectangle(brush, bounds);
        }

        private static void Line(Bitmap display, int x1, int y1, int x2, int y2, Color color)
        {
            using Graphics graphics = Graphics.FromImage(display);
            graphics.SmoothingMode = SmoothingMode.None;
            using Pen pen = new(color, 1);
            graphics.DrawLine(pen, x1, y1, x2, y2);
        }

        private static void Dither(Bitmap display, Rectangle bounds, Color first, Color second)
        {
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            {
                for (int x = bounds.Left; x < bounds.Right; x++)
                {
                    display.SetPixel(x, y, ((x + y) & 1) == 0 ? first : second);
                }
            }
        }

        // ----- Older-LCD appearance post-process (HANDOFF task 3) -------------
        // The VNS430 glyphs and rules are drawn pixel-exact above. Two optional
        // passes soften that raster:
        //   1. a brightness dilation that bleeds lit pixels into their darker
        //      neighbours, thickening text and line strokes; and
        //   2. a light box blur that adds softness/fuzziness.
        //
        // The dilation is off by default. It thickens strokes as the handoff asked,
        // but because it grows bright pixels into dark ones it also flattens the
        // contrast between them, and at any strength high enough to see it the
        // display reads washed out - the reversed text in a selected row suffers
        // worst. A small blur on its own gives the softness of an aged panel while
        // leaving contrast intact, which is the look we actually wanted.
        //
        // Raise LcdBloom above zero only if reference photos genuinely call for
        // heavier strokes, and check the selected-row text when you do. Render has
        // an overload taking both weights, so they can be previewed without a
        // rebuild.
        private const double LcdBloom = 0.00;    // strength of stroke-thickening bleed (0..1)
        private const double LcdSoftness = 0.15; // strength of final softening blur (0..1)

        // The panel repaints on a 100 ms timer, so this pass runs continuously. Its
        // scratch buffers are reused per thread rather than reallocated each render;
        // at 240x128 a fresh set costs ~364 KB, which dwarfed everything else the
        // renderer allocated. [ThreadStatic] keeps that safe without locking: the
        // UI thread and any test thread each get their own set.
        [ThreadStatic] private static int[] appearanceSource;
        [ThreadStatic] private static int[] appearanceBloomed;
        [ThreadStatic] private static int[] appearanceSoft;
        [ThreadStatic] private static byte[] appearanceLuma;

        private static int[] EnsureBuffer(ref int[] buffer, int count)
        {
            if (buffer == null || buffer.Length < count)
            {
                buffer = new int[count];
            }

            return buffer;
        }

        private static void ApplyLcdAppearance(Bitmap display, double bloom, double softness)
        {
            if (bloom <= 0 && softness <= 0)
            {
                return;
            }

            int w = display.Width;
            int h = display.Height;
            int count = w * h;
            Rectangle bounds = new(0, 0, w, h);
            BitmapData data = display.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int[] src = EnsureBuffer(ref appearanceSource, count);
                Marshal.Copy(data.Scan0, src, 0, count);

                // Pass 1 is skipped entirely when the dilation is off, which is the
                // default, so neither it nor its luma table costs anything.
                int[] bloomed = src;
                if (bloom > 0)
                {
                    bloomed = EnsureBuffer(ref appearanceBloomed, count);
                    if (appearanceLuma == null || appearanceLuma.Length < count)
                    {
                        appearanceLuma = new byte[count];
                    }

                    byte[] luma = appearanceLuma;

                    // Luma is read up to five times per pixel by the dilation below, so
                    // compute it once per pixel instead of per comparison.
                    for (int i = 0; i < count; i++)
                    {
                        luma[i] = (byte)Luma(src[i]);
                    }

                    // 4-neighbour brightness dilation. Each pixel blends toward the
                    // brightest of itself and its neighbours, so bright glyph and rule
                    // pixels grow slightly into the darker background.
                    for (int y = 0; y < h; y++)
                    {
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w) + x;
                        int best = src[i];
                        int bestLuma = luma[i];
                        if (x > 0 && luma[i - 1] > bestLuma) { bestLuma = luma[i - 1]; best = src[i - 1]; }
                        if (x < w - 1 && luma[i + 1] > bestLuma) { bestLuma = luma[i + 1]; best = src[i + 1]; }
                        if (y > 0 && luma[i - w] > bestLuma) { bestLuma = luma[i - w]; best = src[i - w]; }
                        if (y < h - 1 && luma[i + w] > bestLuma) { bestLuma = luma[i + w]; best = src[i + w]; }
                        bloomed[i] = Blend(src[i], best, bloom);
                    }
                    }
                }

                if (softness <= 0)
                {
                    Marshal.Copy(bloomed, 0, data.Scan0, count);
                    return;
                }

                // Pass 2: 3x3 box blur for older-display softness.
                int[] soft = EnsureBuffer(ref appearanceSoft, count);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w) + x;
                        int r = 0, g = 0, b = 0, n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = y + dy;
                            if (ny < 0 || ny >= h) { continue; }
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx;
                                if (nx < 0 || nx >= w) { continue; }
                                int c = bloomed[(ny * w) + nx];
                                r += (c >> 16) & 0xFF;
                                g += (c >> 8) & 0xFF;
                                b += c & 0xFF;
                                n++;
                            }
                        }
                        int blurred = unchecked((int)0xFF000000) | ((r / n) << 16) | ((g / n) << 8) | (b / n);
                        soft[i] = Blend(bloomed[i], blurred, softness);
                    }
                }

                Marshal.Copy(soft, 0, data.Scan0, count);
            }
            finally
            {
                display.UnlockBits(data);
            }
        }

        private static int Luma(int argb)
        {
            int r = (argb >> 16) & 0xFF;
            int g = (argb >> 8) & 0xFF;
            int b = argb & 0xFF;
            return ((r * 299) + (g * 587) + (b * 114)) / 1000;
        }

        private static int Blend(int first, int second, double weight)
        {
            double keep = 1.0 - weight;
            int r = (int)((((first >> 16) & 0xFF) * keep) + (((second >> 16) & 0xFF) * weight));
            int g = (int)((((first >> 8) & 0xFF) * keep) + (((second >> 8) & 0xFF) * weight));
            int b = (int)(((first & 0xFF) * keep) + ((second & 0xFF) * weight));
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
    }
}
