using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace EasyCPDLC
{
    /// <summary>
    /// Extracts the next ATSU from a handover / next-data-authority uplink.
    /// </summary>
    /// <remarks>
    /// The previous rule was `messageString.Split(' ').Last().Trim('@')` — the last
    /// space-delimited token, with no validation. That works only when the code is the
    /// very last thing in the message, which is not something the sender guarantees:
    ///
    ///   HANDOVER @KUSA@ AT 1230      -> "1230"      logged on to a time
    ///   HANDOVER TO KUSA CENTER      -> "CENTER"    logged on to a word
    ///   HANDOVER @KUSA@.             -> "KUSA@."    malformed recipient
    ///   HANDOVER COMPLETE            -> "COMPLETE"  logged on to nothing real
    ///   "HANDOVER @KUSA@ "           -> ""          logon sent to an empty station
    ///
    /// Every one of those was then used verbatim as the Hoppie recipient of an
    /// automatic REQUEST LOGON and shown to the pilot as the NEXT ATS UNIT. This parser
    /// instead looks for a station code, and reports failure rather than guessing.
    /// </remarks>
    internal static class CpdlcHandoverParser
    {
        // The real CPDLC term is NEXT DATA AUTHORITY; Hoppie-network controllers
        // commonly send HANDOVER. Both mean "log on to this unit next".
        private static readonly Regex HandoverPrefix = new(
            @"^\s*(HANDOVER|NEXT\s+DATA\s+AUTHORITY|NDA)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A code wrapped in the @...@ highlight markers is unambiguous, so it wins.
        private static readonly Regex Delimited = new(
            @"@\s*([A-Z0-9]{3,4}(?:_[A-Z0-9]{1,4})?)\s*@",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Otherwise: a bare ATSU code (KUSA, EDYY, EURW) or a controller callsign
        // (EDYY_CTR). Anchored to word boundaries so it cannot match inside a number.
        private static readonly Regex Bare = new(
            @"\b([A-Z]{2}[A-Z0-9]{1,2}(?:_[A-Z0-9]{1,4})?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Words that appear in handover phrasing and are not station codes.
        private static readonly string[] NotACode =
        {
            "HANDOVER", "NEXT", "DATA", "AUTHORITY", "NDA", "TO", "AT", "ON", "VIA",
            "WITH", "CONTACT", "COMPLETE", "COMPLETED", "FAILED", "END", "NONE",
            "CENTER", "CENTRE", "CTR", "APP", "DEP", "TWR", "GND", "DEL", "FSS",
            "RADIO", "UNICOM", "AND", "THEN", "FOR", "FREQ", "UTC", "ZULU"
        };

        internal static bool IsHandover(string messageText) =>
            HandoverPrefix.IsMatch(messageText ?? string.Empty);

        /// <summary>
        /// Pulls the next ATSU code out of a handover uplink.
        /// </summary>
        /// <returns>False when the message carries no usable station code, in which
        /// case no logon should be attempted and the text belongs in front of the
        /// pilot instead.</returns>
        internal static bool TryParseNextUnit(string messageText, out string nextUnit)
        {
            nextUnit = string.Empty;
            string text = (messageText ?? string.Empty).Trim();
            if (!HandoverPrefix.IsMatch(text))
            {
                return false;
            }

            // Everything after the keyword; the keyword itself must never be the code.
            string tail = HandoverPrefix.Replace(text, string.Empty);

            Match delimited = Delimited.Match(tail);
            if (delimited.Success && Accept(delimited.Groups[1].Value, out nextUnit))
            {
                return true;
            }

            foreach (Match candidate in Bare.Matches(tail))
            {
                if (Accept(candidate.Groups[1].Value, out nextUnit))
                {
                    return true;
                }
            }

            nextUnit = string.Empty;
            return false;
        }

        private static bool Accept(string raw, out string code)
        {
            code = (raw ?? string.Empty).Trim().Trim('@').ToUpperInvariant();
            if (code.Length < 3)
            {
                code = string.Empty;
                return false;
            }

            // A controller callsign (EDYY_CTR) identifies the same ATSU as its prefix,
            // and the prefix is what the network expects a logon addressed to.
            string atsu = code.Split('_')[0];
            if (NotACode.Contains(atsu, StringComparer.OrdinalIgnoreCase))
            {
                code = string.Empty;
                return false;
            }

            code = atsu;
            return code.Length is >= 3 and <= 4;
        }
    }
}
