using System.Collections.Generic;

namespace EasyCPDLC.VNS430.Cdu
{
    // Seam for streaming the CDU screen off-box. The grid is already serialised to the
    // WinWing CDU display format (24 cols x 14 rows = 336 [glyph, colour, size, inverse]
    // cells) by CduGrid.ToWinwingData(); a sink just forwards that payload somewhere. The
    // renderer pushes to its sink after every paint, so what is on screen is what a
    // hardware CDU would show.
    //
    // MobiFlight itself hosts the receiving end. With MobiFlight running it serves three
    // websocket endpoints, one per cockpit position:
    //
    //     ws://localhost:8320/winwing/cdu-captain
    //     ws://localhost:8320/winwing/cdu-co-pilot
    //     ws://localhost:8320/winwing/cdu-observer
    //
    // A sink connects to the one matching the position the physical unit is set to in
    // WinWing SimAppPro, optionally sends { "Target": "Font", "Data": "Boeing" }, then
    // sends { "Target": "Display", "Data": <cells> } per refresh. MobiFlight owns the USB
    // device (SimAppPro must be closed), so this is the supported way in - we publish to
    // MobiFlight rather than claiming the hardware.
    //
    // Until a real sink is wired in, NullCduDisplaySink discards the payload.
    internal interface ICduDisplaySink
    {
        void Push(IReadOnlyList<object[]> cells);
    }

    internal sealed class NullCduDisplaySink : ICduDisplaySink
    {
        public static readonly NullCduDisplaySink Instance = new();

        public void Push(IReadOnlyList<object[]> cells)
        {
            // Discarded until a real WinWing/websocket sink is wired in.
        }
    }
}
