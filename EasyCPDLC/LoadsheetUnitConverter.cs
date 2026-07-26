using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EasyCPDLC
{
    /// <summary>
    /// Converts the weights in a loadsheet between kilograms and pounds for display.
    /// </summary>
    /// <remarks>
    /// Non-destructive: the stored message is never altered, the conversion is applied
    /// when the viewer renders it.
    ///
    /// Deliberately conservative about WHAT it converts. A loadsheet is full of numbers
    /// that are not weights - flight numbers, times, dates, seat counts, edition
    /// numbers, registrations - and silently scaling one of those would be worse than
    /// showing the wrong unit. Only two shapes are touched:
    ///
    ///   1. A number immediately followed by a unit token ("62000 KG"). Unambiguous.
    ///   2. A number on a line whose label is a known weight field ("ZFW 62000"), and
    ///      only when the sheet declares its units globally so the source is known.
    ///
    /// Everything else is left exactly as written.
    /// </remarks>
    internal static class LoadsheetUnitConverter
    {
        internal const string Kilograms = "KG";
        internal const string Pounds = "LB";
        private const double PoundsPerKilogram = 2.2046226218;

        // Weight fields that appear on a loadsheet as "LABEL <number>". Fuel and time
        // share labels on some formats, so TIME-suffixed variants are excluded below.
        private static readonly Regex WeightLine = new(
            @"^(?<prefix>\s*(?:ZFW|TOW|LAW|MZFW|MTOW|MLAW|BOW|DOW|OEW|PAYLOAD|CARGO|BAGGAGE|BAG|FREIGHT|MAIL|BLOCK\s*FUEL|TAXI\s*FUEL|TRIP\s*FUEL|TAKEOFF\s*FUEL|T/O\s*FUEL|FUEL|UNDERLOAD|TTL|TOTAL\s*TRAFFIC\s*LOAD)\b[^0-9\-]{0,12})(?<value>\d[\d,]*(?:\.\d+)?)(?<suffix>\s*)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex ValueWithUnit = new(
            @"(?<value>\d[\d,]*(?:\.\d+)?)\s*(?<unit>KGS?|KILOGRAMS?|KILOS?|LBS?|POUNDS?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex GlobalKilograms = new(
            @"\b(KGS?|KILOS?|KILOGRAMS?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex GlobalPounds = new(
            @"\b(LBS?|POUNDS?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The unit a sheet is written in, or empty when it never says.</summary>
        internal static string DetectUnit(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            bool kg = GlobalKilograms.IsMatch(body);
            bool lb = GlobalPounds.IsMatch(body);

            // Mixed or silent: not safe to assume a source unit.
            if (kg == lb)
            {
                return string.Empty;
            }

            return kg ? Kilograms : Pounds;
        }

        internal static string Other(string unit) =>
            string.Equals(unit, Pounds, StringComparison.OrdinalIgnoreCase) ? Kilograms : Pounds;

        /// <summary>
        /// Renders the sheet in <paramref name="targetUnit"/>. Returns the body unchanged
        /// when it is already in that unit or its units cannot be determined.
        /// </summary>
        internal static string Convert(string body, string targetUnit)
        {
            string source = DetectUnit(body);
            if (string.IsNullOrWhiteSpace(source) ||
                string.IsNullOrWhiteSpace(targetUnit) ||
                string.Equals(source, targetUnit, StringComparison.OrdinalIgnoreCase))
            {
                return body;
            }

            bool toPounds = string.Equals(targetUnit, Pounds, StringComparison.OrdinalIgnoreCase);

            // Shape 1: explicit "<number> <unit>" pairs anywhere in the sheet.
            string converted = ValueWithUnit.Replace(body, match =>
            {
                if (!TryParse(match.Groups["value"].Value, out double value))
                {
                    return match.Value;
                }

                bool valueIsPounds = GlobalPounds.IsMatch(match.Groups["unit"].Value);
                if (valueIsPounds == toPounds)
                {
                    return match.Value;   // already in the requested unit
                }

                return Format(Scale(value, toPounds)) + " " + targetUnit;
            });

            // Shape 2: labelled weight lines, safe now that the source unit is known.
            converted = WeightLine.Replace(converted, match =>
            {
                if (!TryParse(match.Groups["value"].Value, out double value))
                {
                    return match.Value;
                }

                return match.Groups["prefix"].Value +
                    Format(Scale(value, toPounds)) +
                    match.Groups["suffix"].Value;
            });

            return converted;
        }

        private static double Scale(double value, bool toPounds) =>
            toPounds ? value * PoundsPerKilogram : value / PoundsPerKilogram;

        private static bool TryParse(string text, out double value) =>
            double.TryParse((text ?? string.Empty).Replace(",", string.Empty),
                NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // Loadsheet weights are whole units; fractions would be noise.
        private static string Format(double value) =>
            Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
    }
}
