using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace EasyCPDLC.VNS430.Cdu
{
    internal sealed class CduLskEventArgs : EventArgs
    {
        public CduLskEventArgs(int index, bool rightSide)
        {
            Index = index;
            RightSide = rightSide;
        }

        public int Index { get; }      // 1..6, top to bottom
        public bool RightSide { get; }
    }

    // On-screen renderer for the LSK-only CDU. It paints the Boeing 737NG CDU panel artwork,
    // renders the 24x14 character grid into the artwork's screen rectangle, and hit-tests the
    // artwork's own key rectangles (from CduPanelLayout). A pressed key is shown by darkening
    // its region of the same artwork. It has no knowledge of the datalink backend; MainForm.Cdu
    // populates the grid and reacts to the LskPressed / KeyPressed events.
    internal sealed class CduDisplayPanel : Control
    {
        private static Image panelArt;
        private readonly Dictionary<string, Vns430Command> keyCommands = BuildKeyCommands();
        private RectangleF? pressedRect;

        public CduDisplayPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            BackColor = Color.Black;
            TabStop = true;
            Grid = new CduGrid();
            panelArt ??= LoadPanelArt();
        }

        public CduGrid Grid { get; }

        // Lights the EXEC annunciator when a transmit action is armed.
        public bool ExecArmed { get; set; }

        public ICduDisplaySink Sink { get; set; } = NullCduDisplaySink.Instance;

        public event EventHandler<CduLskEventArgs> LskPressed;
        public event EventHandler<Vns430Command> KeyPressed;
        public event EventHandler<char> CharTyped;
        public event EventHandler ScratchpadBackspace;
        public event EventHandler ScratchpadClear;

        // Raised when the left button is pressed on empty panel area (not on a key),
        // so the host can drag the window. Lets the CDU be moved with no title bar.
        public event EventHandler DragMoveRequested;

        // Raised when the left button is pressed in a corner zone; the int is the
        // Win32 hit-test code (HTTOPLEFT/HTTOPRIGHT/HTBOTTOMLEFT/HTBOTTOMRIGHT) so the
        // host can start a native resize. Lets the CDU be resized with no border.
        public event EventHandler<int> ResizeRequested;

        // Size of the square corner resize zones, in pixels.
        private const int CornerGrip = 28;

        // Aspect ratio (w/h) of the panel artwork, so the host can lock resizing to it.
        public float PanelAspect => panelArt != null && panelArt.Height > 0
            ? (float)panelArt.Width / panelArt.Height
            : 1f;

        public void RefreshDisplay() => Invalidate();

        private static Image LoadPanelArt()
        {
            // Embedded first (single-file publish), then loose developer fallback.
            Image img = EmbeddedAssets.LoadImage("Cdu", "cdu-panel.png");
            return img;
        }

        // Pixel rectangle of the panel artwork within this control (aspect-fit, centred).
        private RectangleF ArtBounds
        {
            get
            {
                if (panelArt == null || panelArt.Width == 0)
                {
                    return new RectangleF(0, 0, Width, Height);
                }
                float ar = panelArt.Width / (float)panelArt.Height;
                float w = Width, h = Width / ar;
                if (h > Height) { h = Height; w = Height * ar; }
                return new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h);
            }
        }

        private RectangleF ToPixels(RectangleF frac)
        {
            RectangleF a = ArtBounds;
            return new RectangleF(a.X + (frac.X * a.Width), a.Y + (frac.Y * a.Height), frac.Width * a.Width, frac.Height * a.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            RectangleF art = ArtBounds;
            if (panelArt != null)
            {
                g.DrawImage(panelArt, art.X, art.Y, art.Width, art.Height);
            }

            DrawScreen(g, ToPixels(CduPanelLayout.Screen));

            // The artwork draws the EXEC annunciator lit. By default mask it with the
            // adjacent bezel colour so it reads off; drop the mask when a transmit action
            // is armed, revealing the artwork's own lit light.
            if (!ExecArmed)
            {
                RectangleF execLight = ToPixels(CduPanelLayout.ExecLight);
                using SolidBrush mask = new(SampleExecBezel());
                using GraphicsPath slot = RoundedRect(execLight, execLight.Height * 0.5f);
                g.FillPath(mask, slot);
            }

            if (pressedRect.HasValue)
            {
                RectangleF pr = ToPixels(pressedRect.Value);
                using SolidBrush press = new(Color.FromArgb(120, 0, 0, 0));
                g.FillRectangle(press, pr);
            }

            DrawCornerHandles(g);

            Sink?.Push(Grid.ToWinwingData());
        }

        private void DrawScreen(Graphics g, RectangleF screen)
        {
            using (SolidBrush black = new(Color.Black))
            {
                g.FillRectangle(black, screen);
            }

            // Lay the character grid in the LSK-aligned text area, not the full glass, so
            // the data rows line up with the physical keys.
            RectangleF grid = ToPixels(CduPanelLayout.TextArea);
            float cellW = grid.Width / CduGrid.Cols;
            float cellH = grid.Height / CduGrid.Rows;
            float large = Math.Max(6f, cellH * 0.82f);
            float small = Math.Max(5f, cellH * 0.64f);

            // B612 Mono is the typeface designed for aircraft cockpit displays and ships
            // with the app; it reads far truer on the CDU than Consolas and, being a real
            // fixed-grid monospace, aligns cleanly to the character cells.
            FontFamily family = EasyCPDLC.VNS430.Vns430FontLoader.Family;
            FontStyle boldStyle = family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
            using Font largeFont = new(family, large, boldStyle, GraphicsUnit.Pixel);
            using Font smallFont = new(family, small, FontStyle.Regular, GraphicsUnit.Pixel);
            using StringFormat fmt = new(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };

            for (int row = 0; row < CduGrid.Rows; row++)
            {
                for (int col = 0; col < CduGrid.Cols; col++)
                {
                    CduCell cell = Grid[row, col];
                    if (cell.IsBlank)
                    {
                        continue;
                    }

                    RectangleF cr = new(grid.X + (col * cellW), grid.Y + (row * cellH), cellW, cellH);
                    Color colour = cell.Color.Rgb();
                    if (cell.Inverse)
                    {
                        using SolidBrush block = new(colour);
                        g.FillRectangle(block, cr.X, cr.Y + 1, cr.Width, cr.Height - 2);
                    }
                    if (cell.Glyph == ' ')
                    {
                        continue;
                    }
                    using SolidBrush text = new(cell.Inverse ? Color.Black : colour);
                    g.DrawString(cell.Glyph.ToString(), cell.Small ? smallFont : largeFont, text, cr, fmt);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            // Corner zones start a native resize (the window is borderless).
            int resizeCode = CornerHitTest(e.Location);
            if (resizeCode != 0)
            {
                ResizeRequested?.Invoke(this, resizeCode);
                return;
            }

            foreach ((string name, RectangleF rect) in CduPanelLayout.Keys)
            {
                if (ToPixels(rect).Contains(e.Location))
                {
                    pressedRect = rect;
                    Invalidate();
                    Activate(name);
                    return;
                }
            }

            // Empty area (bezel or screen background) drags the window.
            DragMoveRequested?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (pressedRect.HasValue)
            {
                pressedRect = null;
                Invalidate();
            }
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath path = new();
            float d = radius * 2f;
            if (d <= 0f || d > r.Width || d > r.Height)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // The bezel colour just below the EXEC annunciator, used to mask it off.
        private static Color SampleExecBezel()
        {
            if (panelArt is Bitmap bmp)
            {
                int x = (int)Math.Clamp((CduPanelLayout.ExecLight.X + (CduPanelLayout.ExecLight.Width * 0.5f)) * bmp.Width, 0, bmp.Width - 1);
                int y = (int)Math.Clamp((CduPanelLayout.ExecLight.Y + (CduPanelLayout.ExecLight.Height * 2.2f)) * bmp.Height, 0, bmp.Height - 1);
                return bmp.GetPixel(x, y);
            }
            return Color.FromArgb(52, 54, 52);
        }

        // Returns the Win32 hit-test code for a corner zone, or 0 for none.
        private int CornerHitTest(Point p)
        {
            bool left = p.X <= CornerGrip;
            bool right = p.X >= Width - CornerGrip;
            bool top = p.Y <= CornerGrip;
            bool bottom = p.Y >= Height - CornerGrip;

            if (top && left) return 13;     // HTTOPLEFT
            if (top && right) return 14;    // HTTOPRIGHT
            if (bottom && left) return 16;  // HTBOTTOMLEFT
            if (bottom && right) return 17; // HTBOTTOMRIGHT
            return 0;
        }

        private void DrawCornerHandles(Graphics g)
        {
            using Pen pen = new(Color.FromArgb(150, 210, 210, 210), 2f);
            int m = 6;               // inset from the edge
            int n = CornerGrip - 12; // bracket arm length
            int r = Width, b = Height;

            g.DrawLines(pen, new[] { new Point(m, m + n), new Point(m, m), new Point(m + n, m) });
            g.DrawLines(pen, new[] { new Point(r - m - n, m), new Point(r - m, m), new Point(r - m, m + n) });
            g.DrawLines(pen, new[] { new Point(m, b - m - n), new Point(m, b - m), new Point(m + n, b - m) });
            g.DrawLines(pen, new[] { new Point(r - m - n, b - m), new Point(r - m, b - m), new Point(r - m, b - m - n) });
        }

        private void Activate(string name)
        {
            if (name.Length >= 2 && (name[0] == 'L' || name[0] == 'R') && int.TryParse(name.Substring(1), out int lsk))
            {
                LskPressed?.Invoke(this, new CduLskEventArgs(lsk, name[0] == 'R'));
                return;
            }
            if (keyCommands.TryGetValue(name, out Vns430Command cmd))
            {
                KeyPressed?.Invoke(this, cmd);
            }
            // Unmapped 737 FMC keys (CLB/CRZ/DES/RTE/LEGS/...) are inert artwork.
        }

        // Keyboard fallback for hardware-free testing.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData >= Keys.F1 && keyData <= Keys.F6)
            {
                LskPressed?.Invoke(this, new CduLskEventArgs((keyData - Keys.F1) + 1, false));
                return true;
            }
            if (keyData >= Keys.F7 && keyData <= Keys.F12)
            {
                LskPressed?.Invoke(this, new CduLskEventArgs((keyData - Keys.F7) + 1, true));
                return true;
            }
            if (keyData == Keys.Back)
            {
                ScratchpadBackspace?.Invoke(this, EventArgs.Empty);
                return true;
            }
            if (keyData == Keys.Delete)
            {
                ScratchpadClear?.Invoke(this, EventArgs.Empty);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            char c = char.ToUpperInvariant(e.KeyChar);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ' || c == '.' || c == '/' || c == '-')
            {
                CharTyped?.Invoke(this, c);
                e.Handled = true;
            }
        }

        private static Dictionary<string, Vns430Command> BuildKeyCommands()
        {
            Dictionary<string, Vns430Command> map = new();
            for (int i = 0; i < 26; i++)
            {
                map[((char)('A' + i)).ToString()] = (Vns430Command)((int)Vns430Command.CduAlphaA + i);
            }
            for (int i = 0; i <= 9; i++)
            {
                map[i.ToString()] = (Vns430Command)((int)Vns430Command.CduDigit0 + i);
            }
            map["SP"] = Vns430Command.CduSpace;
            map["DOT"] = Vns430Command.CduDot;
            map["SLASH"] = Vns430Command.CduSlash;
            map["PLUSMINUS"] = Vns430Command.CduPlusMinus;
            map["CLR"] = Vns430Command.CduClear;
            map["DEL"] = Vns430Command.CduDelete;
            map["MENU"] = Vns430Command.CduMenu;
            map["EXEC"] = Vns430Command.CduExec;
            map["PREV_PAGE"] = Vns430Command.CduPrevPage;
            map["NEXT_PAGE"] = Vns430Command.CduNextPage;
            map["BRT"] = Vns430Command.CduBrightnessUp;
            return map;
        }
    }
}
