using EasyCPDLC;
using Xunit;

namespace EasyCPDLC.Tests
{
    public class ELoadLoadsheetUnitsTests
    {
        // The whole point: a sheet that never names its units gets one, because every
        // weight we send to eLoadControl has been converted to kilograms.
        [Fact]
        public void SheetWithoutUnits_GetsTheUnitsLine()
        {
            string sheet = "LOADSHEET EDITION 1\nZFW 62000\nTOW 74000";
            string result = ELoadLoadsheetUnits.EnsureUnitsLine(sheet);

            Assert.EndsWith(ELoadLoadsheetUnits.UnitsLine, result);
            Assert.StartsWith("LOADSHEET EDITION 1", result);
        }

        // A sheet that already states units must not be annotated twice, in either unit.
        [Theory]
        [InlineData("ZFW 62000 KG\nTOW 74000 KG")]
        [InlineData("ZFW 62000 kg")]
        [InlineData("ALL WEIGHTS IN KILOGRAMS")]
        [InlineData("ZFW 136000 LB")]
        [InlineData("WEIGHTS IN POUNDS")]
        public void SheetThatStatesUnits_IsLeftAlone(string sheet)
        {
            Assert.Equal(sheet.TrimEnd(), ELoadLoadsheetUnits.EnsureUnitsLine(sheet));
        }

        // "KGS" inside a word (e.g. a registration or a packing code) is not a unit
        // statement; the boundary match keeps those from suppressing the line.
        [Fact]
        public void UnitLikeSubstring_DoesNotCountAsStatingUnits()
        {
            Assert.False(ELoadLoadsheetUnits.StatesUnits("REG DKGSA\nZFW 62000"));
            Assert.EndsWith(ELoadLoadsheetUnits.UnitsLine,
                ELoadLoadsheetUnits.EnsureUnitsLine("REG DKGSA\nZFW 62000"));
        }

        [Fact]
        public void EmptyInput_StaysEmpty()
        {
            Assert.Equal(string.Empty, ELoadLoadsheetUnits.EnsureUnitsLine(string.Empty));
            Assert.Equal(string.Empty, ELoadLoadsheetUnits.EnsureUnitsLine(null));
        }
    }
}
