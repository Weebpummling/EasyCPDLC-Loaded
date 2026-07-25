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
        AocOceanic
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
        internal string Code { get; init; } = string.Empty;
        internal string Controller { get; init; } = string.Empty;
        internal string Frequency { get; init; } = string.Empty;
        internal string Reason { get; init; } = string.Empty;
        internal bool TunedMatch { get; init; }
    }

    internal sealed class Vns430BackendSnapshot
    {
        internal bool Connected { get; init; }
        internal string Callsign { get; init; } = string.Empty;
        internal string CurrentAtcUnit { get; init; } = string.Empty;
        internal string PendingLogon { get; init; } = string.Empty;
        internal string Departure { get; init; } = string.Empty;
        internal string Arrival { get; init; } = string.Empty;
        internal string Aircraft { get; init; } = string.Empty;
        internal IReadOnlyList<Vns430MessageSnapshot> Messages { get; init; } = new Vns430MessageSnapshot[0];

        // Controller-online / datalink discovery, kept fresh by the backend's 15 s
        // VATSIM + Hoppie refresh loop.
        internal bool AtcUnitOnline { get; init; }
        internal IReadOnlyList<Vns430CpdlcCandidate> CpdlcCandidates { get; init; } = new Vns430CpdlcCandidate[0];
        internal string PdcStatus { get; init; } = string.Empty;
        internal string PdcLogonCode { get; init; } = string.Empty;
        internal string PdcController { get; init; } = string.Empty;
        internal bool PdcAllowReqClr { get; init; }
    }
}
