using System.Collections.Generic;

namespace EasyCPDLC.VNS430
{
    internal enum Vns430PageGroup
    {
        Nav,
        Wpt,
        Aux,
        Nrst
    }

    internal enum Vns430Page
    {
        Status,
        Messages,
        MessageDetail,
        Logon,
        AtcMenu,
        AtcRequest,
        RequestReview,
        AocMenu,
        AocRequest,
        AocReview,
        LoadControl,
        LoadReview,
        Menu,
        Help,
        Pdc
    }

    internal enum Vns430WorkflowKind
    {
        None,
        AtcDirect,
        AtcLevel,
        AtcSpeed,
        AtcWhenCanWe,
        AtcFreeText,
        // AtcPositionReport must stay within the contiguous Atc block: the VNS430
        // panel maps its ATC menu to kinds by index arithmetic from AtcDirect.
        AtcPositionReport,
        AocTelex,
        AocMetar,
        AocAtis,
        AocPreDeparture,
        AocOceanic,
        // Company position report (ICAO E1). Prefilled from the route tracker and live
        // position, then armed and sent by the pilot like any other AOC request.
        AocCompanyPosition
    }

    internal sealed class Vns430MessageSnapshot
    {
        internal CPDLCMessage Source { get; init; }
        internal string Type { get; init; } = string.Empty;
        internal string Station { get; init; } = string.Empty;
        internal string Text { get; init; } = string.Empty;
        internal bool Outbound { get; init; }
        internal bool Acknowledged { get; init; }
        internal bool Unread { get; init; }
        internal IReadOnlyList<string> Responses { get; init; } = new string[0];
    }

    // A CPDLC logon candidate discovered from VATSIM/Hoppie online data, projected
    // for the VNS430/CDU front ends from the backend's CpdlcDiscoveryResult.
    internal sealed class Vns430CpdlcCandidate
    {
        /// <summary>
        /// Which ACARS network this logon is sent on. Carried explicitly because the
        /// same ATSU code can be offered on both sides - SayIntentions accepts the
        /// regional codes too - so the row, not the active network mode, decides.
        /// </summary>
        internal AcarsRoute Route { get; init; } = AcarsRoute.Auto;

        internal string Code { get; init; } = string.Empty;
        internal string Controller { get; init; } = string.Empty;
        internal string Frequency { get; init; } = string.Empty;
        internal string Reason { get; init; } = string.Empty;
        internal bool TunedMatch { get; init; }
    }

    internal sealed class Vns430BackendSnapshot
    {
        /// <summary>Whether the datalink is usable at all (SI needs no session).</summary>
        internal bool Connected { get; init; }

        /// <summary>
        /// The real VATSIM connection. Distinct from <see cref="Connected"/>, which is
        /// true on SI without any VATSIM session - so a CONNECT/DISCONNECT control must
        /// use this or it will offer to disconnect something that was never connected.
        /// </summary>
        internal bool VatsimConnected { get; init; }
        internal string Callsign { get; init; } = string.Empty;
        internal string CurrentAtcUnit { get; init; } = string.Empty;
        internal string PendingLogon { get; init; } = string.Empty;
        internal string Departure { get; init; } = string.Empty;
        internal string Arrival { get; init; } = string.Empty;
        internal string Aircraft { get; init; } = string.Empty;

        // True once the flight has reached cruise (the enroute phase marker): weather
        // prefills switch from the departure airport to the destination.
        internal bool PreferArrivalStation { get; init; }

        // Whether the SI network is active, so workflows can default network choices
        // (e.g. the TELEX VIA field) to the side the pilot is flying on.
        internal bool SayIntentionsNetwork { get; init; }

        // The weather source currently in force (VATSIM / REAL WORLD / SI). The METAR
        // and ATIS VIA fields open on this, so the per-request selector starts where the
        // last one left off - it replaced the old SETUP > WX SOURCE switch.
        internal string WeatherSource { get; init; } = "VATSIM";

        // Which network the current CPDLC session runs over, so the ATS UNIT read-out
        // can name it without a separate row.
        internal bool AtcUnitViaSayIntentions { get; init; }

        // Aircraft identity from the loaded SimBrief plan (callsign, else registration),
        // empty when no plan is loaded. The CDU header shows this rather than the live
        // callsign, which outlives the flight it came from.
        internal string SimbriefIdent { get; init; } = string.Empty;
        internal IReadOnlyList<Vns430MessageSnapshot> Messages { get; init; } = new Vns430MessageSnapshot[0];

        // Controller-online / datalink discovery, kept fresh by the backend's 15 s
        // VATSIM + Hoppie refresh loop.
        // Company (AOC) position reporting. The address is a plain Hoppie recipient, so
        // it lives on the AOC page rather than with the credentials; the fix idents come
        // from the passive route tracker and prefill the report page.
        internal string CompanyAddress { get; init; } = string.Empty;
        internal string OverflownFix { get; init; } = string.Empty;
        internal string NextFix { get; init; } = string.Empty;
        internal string FollowingFix { get; init; } = string.Empty;
        internal string NextFixEta { get; init; } = string.Empty;

        /// <summary>Number the next company report will carry (POS01, POS02, ...).</summary>
        internal int CompanyReportSequence { get; init; } = 1;

        // Live aircraft state, when the sim is feeding it. Used to prefill the position
        // report; PositionValid is false when there is nothing trustworthy to report.
        internal bool PositionValid { get; init; }
        internal double Latitude { get; init; }
        internal double Longitude { get; init; }
        internal double AltitudeFt { get; init; }
        internal double GroundSpeedKt { get; init; }

        internal bool AtcUnitOnline { get; init; }
        internal IReadOnlyList<Vns430CpdlcCandidate> CpdlcCandidates { get; init; } = new Vns430CpdlcCandidate[0];
        internal string PdcStatus { get; init; } = string.Empty;
        internal string PdcLogonCode { get; init; } = string.Empty;
        internal string PdcController { get; init; } = string.Empty;
        internal bool PdcAllowReqClr { get; init; }
    }
}
