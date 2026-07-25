using System;

namespace EasyCPDLC
{
    /// <summary>
    /// Which ACARS network an outbound packet is sent through.
    /// </summary>
    public enum AcarsRoute
    {
        /// <summary>Route by content: CPDLC and ATSU-addressed traffic follow the active
        /// ATC network, everything else stays on Hoppie.</summary>
        Auto,
        /// <summary>Force Hoppie (VA telex, station discovery, the Hoppie poll).</summary>
        Hoppie,
        /// <summary>Force SayIntentions (the SI poll).</summary>
        SayIntentions
    }

    /// <summary>
    /// Decides which ACARS network a packet belongs to.
    /// </summary>
    /// <remarks>
    /// SayIntentions operates a Hoppie-protocol-compatible ACARS service: the same
    /// connect.html request shape (logon/from/to/type/packet) and the same poll response
    /// format, hosted at acars.sayintentions.ai, authenticated with the user's SI API key
    /// as the logon value. Its ATSU answers on the fixed station PKGM.
    ///
    /// The routing rule keeps both networks live at once: ATC-session traffic (all CPDLC
    /// packets, and telex addressed to the SI ATSU - i.e. a PDC request) follows the
    /// active ATC network, while everything else - VA telex, loadsheets, station pings,
    /// the Hoppie poll - stays on Hoppie. That is what lets a VA's ACARS keep working
    /// while SayIntentions handles the controlling.
    /// </remarks>
    internal static class DatalinkRouting
    {
        internal const string SayIntentionsConnectUrl = "https://acars.sayintentions.ai/acars/system/connect.html";

        // The one station SayIntentions' ATC answers on, for logon, CPDLC and PDC alike.
        internal const string SayIntentionsAtsu = "PKGM";

        /// <summary>
        /// User-facing name for a station. The SI ATSU's wire code (PKGM) means nothing
        /// to a pilot, so every display shows it as "SI"; the wire keeps using PKGM.
        /// </summary>
        internal static string DisplayStation(string station)
        {
            string clean = (station ?? string.Empty).Trim();
            return clean.Equals(SayIntentionsAtsu, StringComparison.OrdinalIgnoreCase) ? "SI" : clean;
        }

        internal static bool RoutesToSayIntentions(AcarsRoute route, string messageType, string recipient, bool sayIntentionsActive)
        {
            if (route == AcarsRoute.Hoppie)
            {
                return false;
            }
            if (route == AcarsRoute.SayIntentions)
            {
                return true;
            }

            // Anything addressed to the SI ATSU can only be meant for SayIntentions,
            // whatever the active network - PKGM is their station, not a Hoppie one.
            // This is what makes PDC VIA = SI work while flying on VATSIM.
            string to = (recipient ?? string.Empty).Trim().ToUpperInvariant();
            if (to == SayIntentionsAtsu)
            {
                return true;
            }

            if (!sayIntentionsActive)
            {
                return false;
            }

            string type = (messageType ?? string.Empty).Trim().ToUpperInvariant();
            return type == "CPDLC";
        }
    }
}
