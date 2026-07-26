using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyCPDLC
{
    /// <summary>
    /// Well-known ATSU logon codes, offered as starting points when nothing has been
    /// discovered live.
    /// </summary>
    /// <remarks>
    /// These are ICAO FIR/UIR identifiers, which is what a CPDLC ATSU is addressed by,
    /// plus the oceanic units and the FAA's domestic ATSU. They are SUGGESTIONS only:
    ///
    ///  - On VATSIM there is no registry. Each controller picks a logon code and
    ///    announces it in their controller info, so a live discovered candidate is
    ///    always more authoritative than anything here and is offered first.
    ///  - Whether a given unit is staffed and datalink-capable right now is not
    ///    something a static table can know.
    ///
    /// The value of the table is the flight-start case the discovery path cannot serve:
    /// logging on to a region ATSU (KUSA for the USA) before any controller has been
    /// matched to the flight.
    /// </remarks>
    internal static class CpdlcAtsuDirectory
    {
        internal sealed class Atsu
        {
            internal string Code { get; init; } = string.Empty;
            internal string Name { get; init; } = string.Empty;
            /// <summary>ICAO prefixes this unit serves, longest match wins.</summary>
            internal string[] Prefixes { get; init; } = Array.Empty<string>();
        }

        internal static readonly Atsu[] All =
        {
            // North Atlantic oceanic - the classic CPDLC use case on VATSIM.
            new() { Code = "EGGX", Name = "SHANWICK OCEANIC",  Prefixes = new[] { "EG" } },
            new() { Code = "CZQX", Name = "GANDER OCEANIC",    Prefixes = new[] { "CY", "CZ" } },
            new() { Code = "KZWY", Name = "NEW YORK OCEANIC",  Prefixes = new[] { "K" } },
            new() { Code = "LPPO", Name = "SANTA MARIA OCN",   Prefixes = new[] { "LP" } },
            new() { Code = "BIRD", Name = "REYKJAVIK",         Prefixes = new[] { "BI" } },
            new() { Code = "ENOB", Name = "BODO OCEANIC",      Prefixes = new[] { "EN" } },

            // North America.
            new() { Code = "KUSA", Name = "FAA DOMESTIC",      Prefixes = new[] { "K", "P" } },
            new() { Code = "CZEG", Name = "EDMONTON",          Prefixes = new[] { "CY" } },
            new() { Code = "CZUL", Name = "MONTREAL",          Prefixes = new[] { "CY" } },
            new() { Code = "CZYZ", Name = "TORONTO",           Prefixes = new[] { "CY" } },
            new() { Code = "CZVR", Name = "VANCOUVER",         Prefixes = new[] { "CY" } },

            // Europe - FIR/UIR identifiers.
            new() { Code = "EDYY", Name = "MAASTRICHT UAC",    Prefixes = new[] { "EH", "EB", "EL", "ED" } },
            new() { Code = "EDUU", Name = "KARLSRUHE UAC",     Prefixes = new[] { "ED" } },
            new() { Code = "EGTT", Name = "LONDON",            Prefixes = new[] { "EG" } },
            new() { Code = "EGPX", Name = "SCOTTISH",          Prefixes = new[] { "EG" } },
            new() { Code = "EISN", Name = "SHANNON",           Prefixes = new[] { "EI" } },
            new() { Code = "LFFF", Name = "PARIS",             Prefixes = new[] { "LF" } },
            new() { Code = "LFMM", Name = "MARSEILLE",         Prefixes = new[] { "LF" } },
            new() { Code = "LECM", Name = "MADRID",            Prefixes = new[] { "LE" } },
            new() { Code = "LECB", Name = "BARCELONA",         Prefixes = new[] { "LE" } },
            new() { Code = "LIRR", Name = "ROMA",              Prefixes = new[] { "LI" } },
            new() { Code = "LIMM", Name = "MILANO",            Prefixes = new[] { "LI" } },
            new() { Code = "LSAZ", Name = "SWITZERLAND",       Prefixes = new[] { "LS" } },
            new() { Code = "LOVV", Name = "WIEN",              Prefixes = new[] { "LO" } },
            new() { Code = "EKDK", Name = "KOBENHAVN",         Prefixes = new[] { "EK" } },
            new() { Code = "ESAA", Name = "SWEDEN",            Prefixes = new[] { "ES" } },
            new() { Code = "EFIN", Name = "FINLAND",           Prefixes = new[] { "EF" } },
            new() { Code = "EPWW", Name = "WARSZAWA",          Prefixes = new[] { "EP" } },
            new() { Code = "LKAA", Name = "PRAHA",             Prefixes = new[] { "LK" } },
            new() { Code = "LHCC", Name = "BUDAPEST",          Prefixes = new[] { "LH" } },
            new() { Code = "LGGG", Name = "ATHINAI",           Prefixes = new[] { "LG" } },
            new() { Code = "LTAA", Name = "ANKARA",            Prefixes = new[] { "LT" } },

            // Middle East, Asia and the Pacific.
            new() { Code = "OMAE", Name = "EMIRATES",          Prefixes = new[] { "OM" } },
            new() { Code = "OTDF", Name = "DOHA",              Prefixes = new[] { "OT" } },
            new() { Code = "VABF", Name = "MUMBAI",            Prefixes = new[] { "VA" } },
            new() { Code = "VIDF", Name = "DELHI",             Prefixes = new[] { "VI" } },
            new() { Code = "VHHK", Name = "HONG KONG",         Prefixes = new[] { "VH" } },
            new() { Code = "RJJJ", Name = "FUKUOKA",           Prefixes = new[] { "RJ", "RO" } },
            new() { Code = "WSJC", Name = "SINGAPORE",         Prefixes = new[] { "WS", "WM" } },
            new() { Code = "YBBB", Name = "BRISBANE",          Prefixes = new[] { "Y" } },
            new() { Code = "YMMM", Name = "MELBOURNE",         Prefixes = new[] { "Y" } },
            new() { Code = "NZZO", Name = "AUCKLAND OCEANIC",  Prefixes = new[] { "NZ" } },
            new() { Code = "NFFF", Name = "NADI",              Prefixes = new[] { "NF" } },

            // Africa and South America.
            new() { Code = "FAJO", Name = "JOHANNESBURG",      Prefixes = new[] { "FA" } },
            new() { Code = "SBAO", Name = "ATLANTICO",         Prefixes = new[] { "SB" } },
            new() { Code = "SAEF", Name = "EZEIZA",            Prefixes = new[] { "SA" } },
        };

        internal static Atsu Find(string code)
        {
            string clean = (code ?? string.Empty).Trim().ToUpperInvariant();
            return All.FirstOrDefault(a => a.Code == clean);
        }

        /// <summary>
        /// Units relevant to a flight, most specific first: those serving the departure,
        /// then the destination, then the rest. Duplicates are removed.
        /// </summary>
        internal static IReadOnlyList<Atsu> SuggestFor(string departureIcao, string arrivalIcao)
        {
            List<Atsu> ordered = new();

            foreach (string airport in new[] { departureIcao, arrivalIcao })
            {
                string icao = (airport ?? string.Empty).Trim().ToUpperInvariant();
                if (icao.Length < 2)
                {
                    continue;
                }

                // Longest prefix first, so a two-letter match beats a one-letter one.
                foreach (Atsu unit in All
                    .Where(a => a.Prefixes.Any(p => icao.StartsWith(p, StringComparison.Ordinal)))
                    .OrderByDescending(a => a.Prefixes
                        .Where(p => icao.StartsWith(p, StringComparison.Ordinal))
                        .Max(p => p.Length)))
                {
                    if (!ordered.Contains(unit))
                    {
                        ordered.Add(unit);
                    }
                }
            }

            return ordered;
        }
    }
}
