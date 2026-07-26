using EasyCPDLC;
using Xunit;

namespace EasyCPDLC.Tests
{
    public class LoadsheetUnitConverterTests
    {
        [Theory]
        [InlineData("ZFW 62000 KG", "KG")]
        [InlineData("ALL WEIGHTS IN KILOGRAMS", "KG")]
        [InlineData("ZFW 136000 LB", "LB")]
        [InlineData("WEIGHTS IN POUNDS", "LB")]
        public void DetectUnit_ReadsTheDeclaredUnit(string body, string expected)
        {
            Assert.Equal(expected, LoadsheetUnitConverter.DetectUnit(body));
        }

        // Silent or self-contradictory sheets have no safe source unit.
        [Theory]
        [InlineData("ZFW 62000\nTOW 74000")]
        [InlineData("ZFW 62000 KG\nFUEL 12000 LB")]
        [InlineData("")]
        public void DetectUnit_IsEmptyWhenUnknownOrMixed(string body)
        {
            Assert.Equal(string.Empty, LoadsheetUnitConverter.DetectUnit(body));
        }

        [Fact]
        public void ExplicitPairs_AreConvertedWithTheirUnit()
        {
            string result = LoadsheetUnitConverter.Convert("ZFW 62000 KG", "LB");
            Assert.Equal("ZFW 136687 LB", result);
        }

        [Fact]
        public void PoundsToKilograms_RoundTripsWithinRounding()
        {
            string toLb = LoadsheetUnitConverter.Convert("ZFW 62000 KG", "LB");
            string backToKg = LoadsheetUnitConverter.Convert(toLb, "KG");
            Assert.Equal("ZFW 62000 KG", backToKg);
        }

        // The global declaration makes the source known, so bare labelled weights convert.
        [Fact]
        public void LabelledWeightLines_ConvertWhenUnitsAreDeclared()
        {
            string sheet = "ALL WEIGHTS IN KG\nZFW 62000\nTOW 74000\nCARGO 3000";
            string result = LoadsheetUnitConverter.Convert(sheet, "LB");

            Assert.Contains("ZFW 136687", result);   // 62000 kg
            Assert.Contains("TOW 163142", result);   // 74000 kg
            Assert.Contains("CARGO 6614", result);   //  3000 kg
        }

        // The whole risk of this feature: numbers that are not weights must survive.
        [Fact]
        public void NonWeightNumbers_AreNeverTouched()
        {
            string sheet = string.Join("\n",
                "ALL WEIGHTS IN KG",
                "FLIGHT BA2490",
                "DATE 25JUL26",
                "EDITION 3",
                "STD 1435",
                "REG G-EUUU",
                "PAX 174",
                "ZFW 62000");

            string result = LoadsheetUnitConverter.Convert(sheet, "LB");

            Assert.Contains("FLIGHT BA2490", result);
            Assert.Contains("DATE 25JUL26", result);
            Assert.Contains("EDITION 3", result);
            Assert.Contains("STD 1435", result);
            Assert.Contains("REG G-EUUU", result);
            Assert.Contains("PAX 174", result);
            // ...while the actual weight did convert.
            Assert.Contains("ZFW 136687", result);
        }

        [Fact]
        public void ConvertingToTheUnitAlreadyInUse_ChangesNothing()
        {
            string sheet = "ALL WEIGHTS IN KG\nZFW 62000";
            Assert.Equal(sheet, LoadsheetUnitConverter.Convert(sheet, "KG"));
        }

        // Without a declared unit there is nothing to convert from; leave it alone
        // rather than guess.
        [Fact]
        public void SheetWithoutDeclaredUnits_IsLeftAlone()
        {
            string sheet = "ZFW 62000\nTOW 74000";
            Assert.Equal(sheet, LoadsheetUnitConverter.Convert(sheet, "LB"));
        }

        [Fact]
        public void ThousandsSeparators_AreParsed()
        {
            Assert.Equal("ZFW 136687 LB", LoadsheetUnitConverter.Convert("ZFW 62,000 KG", "LB"));
        }

        [Fact]
        public void Other_TogglesBetweenTheTwoUnits()
        {
            Assert.Equal("LB", LoadsheetUnitConverter.Other("KG"));
            Assert.Equal("KG", LoadsheetUnitConverter.Other("LB"));
        }
    }
}
