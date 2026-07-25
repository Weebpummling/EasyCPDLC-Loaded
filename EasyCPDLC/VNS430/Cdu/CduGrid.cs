using System;
using System.Collections.Generic;
using System.Drawing;

namespace EasyCPDLC.VNS430.Cdu
{
    // The device-independent "display language" for the LSK-only CDU mode.
    //
    // A CduGrid is a fixed 24-column x 14-row character grid (a real MCDU layout: title +
    // six label/data pairs + scratchpad) where every cell carries a glyph, a colour, a size
    // flag (large/small) and an inverse-video flag. Its 336 cells match the WinWing CDU
    // display, so the same grid that paints the on-screen CDU can be serialised straight to
    // the WinWing websocket (see ToWinwingData). The grid is pure data: it has no dependency
    // on WinForms and is fully unit-testable.

    internal enum CduColor
    {
        White,
        Cyan,
        Green,
        Amber,
        Magenta,
        Red,
        Yellow,
        Blue,
        Grey,
        Khaki
    }

    internal static class CduColorExtensions
    {
        // Single-letter colour codes used by the MobiFlight WinCtrl / WinWing CDU display
        // protocol. Do not change these; they are the wire contract for the hardware.
        public static string WinwingCode(this CduColor color)
        {
            return color switch
            {
                CduColor.White => "w",
                CduColor.Cyan => "c",
                CduColor.Green => "g",
                CduColor.Amber => "a",
                CduColor.Magenta => "m",
                CduColor.Red => "r",
                CduColor.Yellow => "y",
                CduColor.Blue => "o",
                CduColor.Grey => "e",
                CduColor.Khaki => "k",
                _ => "w"
            };
        }

        // On-screen RGB, tuned to the existing VNS430/DCDU palette so the CDU skin matches
        // the rest of the client. Only used for local rendering, never sent to hardware.
        public static Color Rgb(this CduColor color)
        {
            return color switch
            {
                CduColor.White => Color.FromArgb(229, 233, 235),
                CduColor.Cyan => Color.FromArgb(64, 208, 226),
                CduColor.Green => Color.FromArgb(70, 214, 108),
                CduColor.Amber => Color.FromArgb(240, 176, 64),
                CduColor.Magenta => Color.FromArgb(214, 110, 214),
                CduColor.Red => Color.FromArgb(230, 82, 74),
                CduColor.Yellow => Color.FromArgb(226, 214, 70),
                CduColor.Blue => Color.FromArgb(96, 150, 230),
                CduColor.Grey => Color.FromArgb(150, 158, 166),
                CduColor.Khaki => Color.FromArgb(190, 180, 130),
                _ => Color.FromArgb(229, 233, 235)
            };
        }
    }

    // Fixed row layout shared by the renderer, the LSK hotspots and the page tree, matching
    // a real MCDU: row 0 is the title line; each of the 6 LSKs owns a small label row and
    // the large data row beneath it (rows 1..12); row 13 is the scratchpad at the bottom.
    // (left half = left LSK, right half = right LSK.)
    internal static class CduLayout
    {
        public const int TitleRow = 0;
        public const int LskCount = 6;
        public const int ScratchpadRow = 13;

        // Small label row for LSK i (1..6): rows 1,3,5,7,9,11.
        public static int LabelRow(int lsk) => (2 * lsk) - 1;

        // Large data row (the LSK line) for LSK i (1..6): rows 2,4,6,8,10,12.
        public static int DataRow(int lsk) => 2 * lsk;
    }

    internal struct CduCell
    {
        public char Glyph;
        public CduColor Color;
        public bool Small;
        public bool Inverse;

        // A cell is "blank" only when it is a plain space with no inverse block; such a
        // cell serialises to an empty array for the WinWing display.
        public readonly bool IsBlank => Glyph == ' ' && !Inverse;
    }

    internal sealed class CduGrid
    {
        public const int Rows = 14;
        public const int Cols = 24;
        public const int HalfCols = Cols / 2; // 12: left half = L-LSKs, right half = R-LSKs
        public const int CellCount = Rows * Cols; // 336 (same total as the WinWing CDU grid)

        private readonly CduCell[,] cells = new CduCell[Rows, Cols];

        public CduGrid()
        {
            Clear();
        }

        public void Clear()
        {
            for (int row = 0; row < Rows; row++)
            {
                for (int col = 0; col < Cols; col++)
                {
                    cells[row, col] = new CduCell
                    {
                        Glyph = ' ',
                        Color = CduColor.White,
                        Small = false,
                        Inverse = false
                    };
                }
            }
        }

        public CduCell this[int row, int col] => cells[row, col];

        // Writes text starting at (row, col). Characters past the right edge are clipped;
        // out-of-range rows/negative columns are ignored rather than throwing.
        public void Write(int row, int col, string text, CduColor color = CduColor.White, bool small = false, bool inverse = false)
        {
            if (string.IsNullOrEmpty(text) || row < 0 || row >= Rows)
            {
                return;
            }

            for (int index = 0; index < text.Length; index++)
            {
                int target = col + index;
                if (target < 0)
                {
                    continue;
                }
                if (target >= Cols)
                {
                    break;
                }

                cells[row, target] = new CduCell
                {
                    Glyph = text[index],
                    Color = color,
                    Small = small,
                    Inverse = inverse
                };
            }
        }

        public void WriteCentered(int row, string text, CduColor color = CduColor.White, bool small = false, bool inverse = false)
        {
            if (text == null)
            {
                return;
            }

            string clipped = Fit(text, Cols);
            int col = (Cols - clipped.Length) / 2;
            Write(row, col, clipped, color, small, inverse);
        }

        // Left half occupies columns 0..HalfCols-1 (the left LSK column).
        public void WriteLeft(int row, string text, CduColor color = CduColor.White, bool small = false, bool inverse = false)
        {
            Write(row, 0, Fit(text, HalfCols), color, small, inverse);
        }

        // Right half occupies columns HalfCols..Cols-1 (the right LSK column), right-aligned.
        public void WriteRight(int row, string text, CduColor color = CduColor.White, bool small = false, bool inverse = false)
        {
            string clipped = Fit(text, HalfCols);
            Write(row, Cols - clipped.Length, clipped, color, small, inverse);
        }

        private static string Fit(string text, int width)
        {
            text ??= string.Empty;
            return text.Length > width ? text.Substring(0, width) : text;
        }

        // Serialises the grid to the WinWing CDU display payload: one quadruplet
        // [glyph, colourCode, size, inverse] per cell in row-major order, 336 total. A blank
        // cell is emitted as an empty array, exactly as the protocol allows. This is the
        // whole contract the "standby" WinWing export mod needs from the display layer.
        public IReadOnlyList<object[]> ToWinwingData()
        {
            var data = new List<object[]>(CellCount);
            for (int row = 0; row < Rows; row++)
            {
                for (int col = 0; col < Cols; col++)
                {
                    CduCell cell = cells[row, col];
                    if (cell.IsBlank)
                    {
                        data.Add(Array.Empty<object>());
                        continue;
                    }

                    // The CDU font has no lowercase glyphs: MobiFlight's own exporters
                    // uppercase every character and express lowercase as *small font*
                    // instead. Sending a raw lowercase glyph renders as nothing on the
                    // device, so follow the same contract here.
                    char glyph = cell.Glyph;
                    bool lower = char.IsLower(glyph);

                    data.Add(new object[]
                    {
                        char.ToUpperInvariant(glyph).ToString(),
                        cell.Color.WinwingCode(),
                        cell.Small || lower ? 1 : 0,
                        cell.Inverse ? 1 : 0
                    });
                }
            }

            return data;
        }
    }
}
