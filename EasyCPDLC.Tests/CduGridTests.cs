using System.Collections.Generic;
using EasyCPDLC.VNS430.Cdu;
using Xunit;

namespace EasyCPDLC.Tests
{
    public sealed class CduGridTests
    {
        [Fact]
        public void Grid_Is24x14_And336Cells()
        {
            Assert.Equal(24, CduGrid.Cols);
            Assert.Equal(14, CduGrid.Rows);
            Assert.Equal(336, CduGrid.CellCount);
            Assert.Equal(336, new CduGrid().ToWinwingData().Count);
        }

        [Fact]
        public void BlankGrid_SerialisesToAllEmptyArrays()
        {
            IReadOnlyList<object[]> data = new CduGrid().ToWinwingData();

            Assert.Equal(336, data.Count);
            Assert.All(data, cell => Assert.Empty(cell));
        }

        [Fact]
        public void Write_PlacesGlyphsWithColourSizeAndInverse()
        {
            CduGrid grid = new();
            grid.Write(1, 2, "MENU", CduColor.Green, small: true, inverse: true);

            Assert.Equal('M', grid[1, 2].Glyph);
            Assert.Equal(CduColor.Green, grid[1, 2].Color);
            Assert.True(grid[1, 2].Small);
            Assert.True(grid[1, 2].Inverse);
            Assert.Equal('U', grid[1, 5].Glyph);
            // Untouched neighbour stays blank.
            Assert.True(grid[1, 6].IsBlank);
        }

        [Fact]
        public void Write_ClipsAtRightEdgeAndIgnoresOutOfRange()
        {
            CduGrid grid = new();
            grid.Write(0, 22, "ABCD", CduColor.White); // only A,B fit (cols 22,23)
            grid.Write(-1, 0, "X", CduColor.White);     // ignored, no throw
            grid.Write(99, 0, "X", CduColor.White);     // ignored, no throw

            Assert.Equal('A', grid[0, 22].Glyph);
            Assert.Equal('B', grid[0, 23].Glyph);
        }

        [Fact]
        public void WriteCentered_CentresWithinTheGrid()
        {
            CduGrid grid = new();
            grid.WriteCentered(0, "CDU"); // (24-3)/2 = 10

            Assert.Equal('C', grid[0, 10].Glyph);
            Assert.Equal('D', grid[0, 11].Glyph);
            Assert.Equal('U', grid[0, 12].Glyph);
        }

        [Fact]
        public void WriteRight_RightAlignsInTheRightHalf()
        {
            CduGrid grid = new();
            grid.WriteRight(3, "ATC");

            Assert.Equal('A', grid[3, 21].Glyph);
            Assert.Equal('T', grid[3, 22].Glyph);
            Assert.Equal('C', grid[3, 23].Glyph);
            // Left half untouched.
            Assert.True(grid[3, 0].IsBlank);
        }

        // The WinWing CDU font has no lowercase glyphs. MobiFlight's own exporters
        // uppercase every character and express lowercase as small font instead; a raw
        // lowercase glyph renders as nothing on the device.
        [Fact]
        public void ToWinwingData_UppercasesLowercaseAndMarksItSmall()
        {
            CduGrid grid = new();
            grid.Write(0, 0, "a", CduColor.Green, small: false, inverse: false);
            grid.Write(0, 1, "B", CduColor.Green, small: false, inverse: false);

            IReadOnlyList<object[]> data = grid.ToWinwingData();

            object[] wasLower = data[0];
            Assert.Equal("A", wasLower[0]);
            Assert.Equal(1, wasLower[2]);   // lowercase -> small font

            object[] wasUpper = data[1];
            Assert.Equal("B", wasUpper[0]);
            Assert.Equal(0, wasUpper[2]);   // already uppercase -> large, unchanged
        }

        [Fact]
        public void ToWinwingData_EmitsQuadrupletForAWrittenCell()
        {
            CduGrid grid = new();
            grid.Write(0, 0, "M", CduColor.White, small: false, inverse: false);
            grid.Write(0, 1, "S", CduColor.Amber, small: true, inverse: true);

            IReadOnlyList<object[]> data = grid.ToWinwingData();

            object[] large = data[0];
            Assert.Equal("M", large[0]);
            Assert.Equal("w", large[1]);
            Assert.Equal(0, large[2]);
            Assert.Equal(0, large[3]);

            object[] smallInverse = data[1];
            Assert.Equal("S", smallInverse[0]);
            Assert.Equal("a", smallInverse[1]);
            Assert.Equal(1, smallInverse[2]);
            Assert.Equal(1, smallInverse[3]);
        }

        [Fact]
        public void WinwingColourCodes_MatchTheProtocol()
        {
            Assert.Equal("w", CduColor.White.WinwingCode());
            Assert.Equal("c", CduColor.Cyan.WinwingCode());
            Assert.Equal("g", CduColor.Green.WinwingCode());
            Assert.Equal("a", CduColor.Amber.WinwingCode());
            Assert.Equal("m", CduColor.Magenta.WinwingCode());
            Assert.Equal("r", CduColor.Red.WinwingCode());
            Assert.Equal("y", CduColor.Yellow.WinwingCode());
            Assert.Equal("o", CduColor.Blue.WinwingCode());
            Assert.Equal("e", CduColor.Grey.WinwingCode());
            Assert.Equal("k", CduColor.Khaki.WinwingCode());
        }

        [Fact]
        public void Layout_MapsEachLskToALabelAndDataRow()
        {
            // MCDU layout: title row 0, then six label/data pairs, scratchpad row 13.
            for (int lsk = 1; lsk <= CduLayout.LskCount; lsk++)
            {
                Assert.Equal((2 * lsk) - 1, CduLayout.LabelRow(lsk));
                Assert.Equal(2 * lsk, CduLayout.DataRow(lsk));
                Assert.InRange(CduLayout.DataRow(lsk), 0, CduGrid.Rows - 1);
            }
            Assert.Equal(0, CduLayout.TitleRow);
            Assert.Equal(12, CduLayout.DataRow(6));
            Assert.Equal(13, CduLayout.ScratchpadRow);
        }

        [Fact]
        public void RealisticPage_StillSerialisesToExactly336Cells()
        {
            // Use space-free tokens: a space serialises to a blank (empty) cell.
            CduGrid grid = new();
            grid.WriteCentered(CduLayout.TitleRow, "DLKSTATUS", CduColor.White);
            grid.WriteLeft(CduLayout.DataRow(1), "<DISCONNECT", CduColor.Amber);
            grid.WriteRight(CduLayout.DataRow(1), "CONNECTED", CduColor.Green);

            System.Collections.Generic.IReadOnlyList<object[]> data = grid.ToWinwingData();
            Assert.Equal(336, data.Count);
            int nonBlank = 0;
            foreach (object[] cell in data)
            {
                if (cell.Length > 0)
                {
                    nonBlank++;
                }
            }
            Assert.Equal("DLKSTATUS".Length + "<DISCONNECT".Length + "CONNECTED".Length, nonBlank);
        }

        [Fact]
        public void Clear_ResetsEveryCellToBlank()
        {
            CduGrid grid = new();
            grid.Write(5, 5, "FILLED", CduColor.Red);
            grid.Clear();

            Assert.All(grid.ToWinwingData(), cell => Assert.Empty(cell));
        }
    }
}
