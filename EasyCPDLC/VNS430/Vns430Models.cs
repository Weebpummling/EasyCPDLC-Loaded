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
        Help
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
    }
}
