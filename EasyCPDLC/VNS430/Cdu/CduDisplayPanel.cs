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

        public ICduDisplaySink Sink { get; set; } = NullCduDisplaySink.Instance;

        public event EventHandler<CduLskEventArgs> LskPressed;
        public event EventHandler<Vns430Command> KeyPressed;
        public event EventHandler<char> CharTyped;
        public event EventHandler ScratchpadBackspace;
        public event EventHandler ScratchpadClear;

        // Raised when the left button is pressed on empty panel area (not on a key),
        // so the host can drag the window. Lets the CDU be moved with no title bar.
        public event EventHandler DragMoveRequested;

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

            if (pressedRect.HasValue)
            {
                RectangleF pr = ToPixels(pressedRect.Value);
                using SolidBrush press = new(Color.FromArgb(120, 0, 0, 0));
                g.FillRectangle(press, pr);
            }

            Sink?.Push(Grid.ToWinwingData());
        }

        private void DrawScreen(Graphics g, RectangleF screen)
        {
            using (SolidBrush black = new(Color.Black))
            {
                g.FillRectangle(black, screen);
            }

            float cellW = screen.Width / CduGrid.Cols;
            float cellH = screen.Height / CduGrid.Rows;
            float large = Math.Max(6f, cellH * 0.82f);
            float small = Math.Max(5f, cellH * 0.64f);
            using Font largeFont = new("Consolas", large, FontStyle.Bold, GraphicsUnit.Pixel);
            using Font smallFont = new("Consolas", small, FontStyle.Regular, GraphicsUnit.Pixel);
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

                    RectangleF cr = new(screen.X + (col * cellW), screen.Y + (row * cellH), cellW, cellH);
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
