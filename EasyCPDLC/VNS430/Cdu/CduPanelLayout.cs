using System.Drawing;

namespace EasyCPDLC.VNS430.Cdu
{
    // Auto-generated from the CDU panel artwork (VNS430/Cdu/extract-cdu-panel.py).
    // Rectangles are normalized (0..1) fractions of the panel image.
    internal static class CduPanelLayout
    {
        // The black LCD glass, as cut in the artwork.
        public static readonly RectangleF Screen = new(0.1587f, 0.0604f, 0.6843f, 0.3824f);

        // Where the 14-row character grid is laid out. It is inset vertically within the
        // glass so the six LSK data rows (grid rows 2,4,6,8,10,12) line up with the physical
        // L1..L6 / R1..R6 key rectangles below, which the even division of the full glass
        // did not. Derived by fitting the data-row centres to the L-key centres.
        public static readonly RectangleF TextArea = new(0.1587f, 0.0808f, 0.6843f, 0.3530f);

        // The EXEC annunciator bar above the EXEC key. Lit (green) only while a
        // network-transmitting action is armed; dark otherwise.
        public static readonly RectangleF ExecLight = new(0.780f, 0.5535f, 0.074f, 0.009f);

        public static readonly (string Name, RectangleF Rect)[] Keys =
        {
            ("L1", new RectangleF(0.01561f, 0.1178f, 0.082f, 0.052f)),
            ("L2", new RectangleF(0.01561f, 0.16865f, 0.082f, 0.052f)),
            ("L3", new RectangleF(0.01561f, 0.21897f, 0.082f, 0.052f)),
            ("L4", new RectangleF(0.01561f, 0.26929f, 0.082f, 0.052f)),
            ("L5", new RectangleF(0.01561f, 0.3196f, 0.082f, 0.052f)),
            ("L6", new RectangleF(0.01561f, 0.36992f, 0.082f, 0.052f)),
            ("R1", new RectangleF(0.90156f, 0.1178f, 0.082f, 0.052f)),
            ("R2", new RectangleF(0.90156f, 0.16865f, 0.082f, 0.052f)),
            ("R3", new RectangleF(0.90156f, 0.21897f, 0.082f, 0.052f)),
            ("R4", new RectangleF(0.90156f, 0.26929f, 0.082f, 0.052f)),
            ("R5", new RectangleF(0.90156f, 0.3196f, 0.082f, 0.052f)),
            ("R6", new RectangleF(0.90156f, 0.36992f, 0.082f, 0.052f)),
            ("INIT_REF", new RectangleF(0.11736f, 0.49151f, 0.1f, 0.062f)),
            ("RTE", new RectangleF(0.24091f, 0.49178f, 0.1f, 0.062f)),
            ("CLB", new RectangleF(0.36364f, 0.49204f, 0.1f, 0.062f)),
            ("CRZ", new RectangleF(0.48636f, 0.49204f, 0.1f, 0.062f)),
            ("DES", new RectangleF(0.60909f, 0.49178f, 0.1f, 0.062f)),
            ("BRT", new RectangleF(0.72535f, 0.48483f, 0.165f, 0.06f)),
            ("MENU", new RectangleF(0.11694f, 0.55163f, 0.1f, 0.062f)),
            ("LEGS", new RectangleF(0.24091f, 0.55163f, 0.1f, 0.062f)),
            ("DEP_ARR", new RectangleF(0.36281f, 0.55136f, 0.1f, 0.062f)),
            ("HOLD", new RectangleF(0.48512f, 0.55163f, 0.1f, 0.062f)),
            ("PROG", new RectangleF(0.60909f, 0.55163f, 0.1f, 0.062f)),
            ("EXEC", new RectangleF(0.76029f, 0.55819f, 0.11f, 0.06f)),
            ("N1_LIMIT", new RectangleF(0.11818f, 0.61174f, 0.1f, 0.062f)),
            ("FIX", new RectangleF(0.2405f, 0.61095f, 0.1f, 0.062f)),
            ("PREV_PAGE", new RectangleF(0.11777f, 0.67107f, 0.1f, 0.062f)),
            ("NEXT_PAGE", new RectangleF(0.24132f, 0.67107f, 0.1f, 0.062f)),
            ("A", new RectangleF(0.41183f, 0.61454f, 0.078f, 0.058f)),
            ("B", new RectangleF(0.50852f, 0.61427f, 0.078f, 0.058f)),
            ("C", new RectangleF(0.60563f, 0.61454f, 0.078f, 0.058f)),
            ("D", new RectangleF(0.70315f, 0.61427f, 0.078f, 0.058f)),
            ("E", new RectangleF(0.80115f, 0.61427f, 0.078f, 0.058f)),
            ("F", new RectangleF(0.41183f, 0.67412f, 0.078f, 0.05801f)),
            ("G", new RectangleF(0.50852f, 0.67386f, 0.078f, 0.058f)),
            ("H", new RectangleF(0.60604f, 0.67412f, 0.078f, 0.05801f)),
            ("I", new RectangleF(0.70274f, 0.67412f, 0.078f, 0.05801f)),
            ("J", new RectangleF(0.80074f, 0.67412f, 0.078f, 0.05801f)),
            ("K", new RectangleF(0.41141f, 0.73345f, 0.078f, 0.058f)),
            ("L", new RectangleF(0.50893f, 0.73345f, 0.078f, 0.058f)),
            ("M", new RectangleF(0.60563f, 0.73371f, 0.078f, 0.058f)),
            ("N", new RectangleF(0.70315f, 0.73398f, 0.078f, 0.058f)),
            ("O", new RectangleF(0.80115f, 0.73398f, 0.078f, 0.058f)),
            ("P", new RectangleF(0.41183f, 0.79277f, 0.078f, 0.058f)),
            ("Q", new RectangleF(0.50893f, 0.79303f, 0.078f, 0.058f)),
            ("R", new RectangleF(0.60563f, 0.79277f, 0.078f, 0.058f)),
            ("S", new RectangleF(0.70315f, 0.79277f, 0.078f, 0.058f)),
            ("T", new RectangleF(0.80115f, 0.79277f, 0.078f, 0.058f)),
            ("U", new RectangleF(0.41183f, 0.85262f, 0.078f, 0.058f)),
            ("V", new RectangleF(0.50893f, 0.85236f, 0.078f, 0.058f)),
            ("W", new RectangleF(0.60604f, 0.85262f, 0.078f, 0.058f)),
            ("X", new RectangleF(0.70315f, 0.85262f, 0.078f, 0.058f)),
            ("Y", new RectangleF(0.80115f, 0.85262f, 0.078f, 0.058f)),
            ("Z", new RectangleF(0.41183f, 0.91221f, 0.078f, 0.058f)),
            ("1", new RectangleF(0.10204f, 0.71945f, 0.086f, 0.086f)),
            ("2", new RectangleF(0.20121f, 0.71945f, 0.086f, 0.086f)),
            ("3", new RectangleF(0.29791f, 0.71971f, 0.086f, 0.086f)),
            ("4", new RectangleF(0.10411f, 0.77903f, 0.086f, 0.086f)),
            ("5", new RectangleF(0.2008f, 0.7793f, 0.086f, 0.086f)),
            ("6", new RectangleF(0.29832f, 0.7793f, 0.086f, 0.086f)),
            ("7", new RectangleF(0.10452f, 0.83862f, 0.086f, 0.086f)),
            ("8", new RectangleF(0.2008f, 0.83889f, 0.086f, 0.086f)),
            ("9", new RectangleF(0.2975f, 0.83862f, 0.086f, 0.086f)),
            ("0", new RectangleF(0.2008f, 0.89821f, 0.086f, 0.086f)),
            ("DOT", new RectangleF(0.10369f, 0.89688f, 0.086f, 0.086f)),
            ("PLUSMINUS", new RectangleF(0.2975f, 0.89794f, 0.086f, 0.086f)),
            ("SP", new RectangleF(0.50893f, 0.91221f, 0.078f, 0.058f)),
            ("DEL", new RectangleF(0.60604f, 0.91274f, 0.078f, 0.058f)),
            ("SLASH", new RectangleF(0.70315f, 0.91247f, 0.078f, 0.058f)),
            ("CLR", new RectangleF(0.80115f, 0.91247f, 0.078f, 0.058f)),
        };
    }
}
