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
        public static readonly RectangleF ExecLight = new(0.780f, 0.5515f, 0.074f, 0.009f);

        // Side annunciator lights (vertical labels in the recessed edge slots). Lit only
        // when driven; CALL/MSG/OFST illuminate white, FAIL amber. Amber == true marks the
        // amber ones. Standby to bind these to L-vars in the WASM bridge for hardware.
        public static readonly (string Name, RectangleF Rect, bool Amber)[] Annunciators =
        {
            ("CALL", new RectangleF(0.040f, 0.699f, 0.050f, 0.073f), false),
            ("FAIL", new RectangleF(0.040f, 0.780f, 0.050f, 0.073f), true),
            ("MSG",  new RectangleF(0.902f, 0.699f, 0.050f, 0.073f), false),
            ("OFST", new RectangleF(0.902f, 0.780f, 0.050f, 0.073f), false),
        };

        public static readonly (string Name, RectangleF Rect)[] Keys =
        {
            ("L1", new RectangleF(0.02461f, 0.1248f, 0.064f, 0.038f)),
            ("L2", new RectangleF(0.02461f, 0.17565f, 0.064f, 0.038f)),
            ("L3", new RectangleF(0.02461f, 0.22597f, 0.064f, 0.038f)),
            ("L4", new RectangleF(0.02461f, 0.27629f, 0.064f, 0.038f)),
            ("L5", new RectangleF(0.02461f, 0.3266f, 0.064f, 0.038f)),
            ("L6", new RectangleF(0.02461f, 0.37692f, 0.064f, 0.038f)),
            ("R1", new RectangleF(0.91056f, 0.1248f, 0.064f, 0.038f)),
            ("R2", new RectangleF(0.91056f, 0.17565f, 0.064f, 0.038f)),
            ("R3", new RectangleF(0.91056f, 0.22597f, 0.064f, 0.038f)),
            ("R4", new RectangleF(0.91056f, 0.27629f, 0.064f, 0.038f)),
            ("R5", new RectangleF(0.91056f, 0.3266f, 0.064f, 0.038f)),
            ("R6", new RectangleF(0.91056f, 0.37692f, 0.064f, 0.038f)),
            ("INIT_REF", new RectangleF(0.11736f, 0.49418f, 0.1f, 0.04767f)),
            ("RTE", new RectangleF(0.24091f, 0.49418f, 0.1f, 0.04767f)),
            ("CLB", new RectangleF(0.36364f, 0.49418f, 0.1f, 0.04767f)),
            ("CRZ", new RectangleF(0.48636f, 0.49418f, 0.1f, 0.04767f)),
            ("DES", new RectangleF(0.60909f, 0.49418f, 0.1f, 0.04767f)),
            ("BRT", new RectangleF(0.7463f, 0.4915f, 0.125f, 0.0424f)),
            ("MENU", new RectangleF(0.11694f, 0.56145f, 0.1f, 0.04767f)),
            ("LEGS", new RectangleF(0.24091f, 0.56145f, 0.1f, 0.04767f)),
            ("DEP_ARR", new RectangleF(0.36281f, 0.56145f, 0.1f, 0.04767f)),
            ("HOLD", new RectangleF(0.48512f, 0.56145f, 0.1f, 0.04767f)),
            ("PROG", new RectangleF(0.60909f, 0.56145f, 0.1f, 0.04767f)),
            ("EXEC", new RectangleF(0.7657f, 0.5702f, 0.1033f, 0.0392f)),
            // These four sat low against the artwork: N1 LIMIT and FIX by a third of a
            // key height, PREV/NEXT PAGE by half. The rect is both the hit region and the
            // press highlight, so raising it realigns the pushed-in shadow and the click
            // target together.
            ("N1_LIMIT", new RectangleF(0.11818f, 0.61282f, 0.1f, 0.04767f)),
            ("FIX", new RectangleF(0.2405f, 0.61282f, 0.1f, 0.04767f)),
            ("PREV_PAGE", new RectangleF(0.11777f, 0.67215f, 0.1f, 0.04767f)),
            ("NEXT_PAGE", new RectangleF(0.24132f, 0.67215f, 0.1f, 0.04767f)),
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
            ("1", new RectangleF(0.10854f, 0.73895f, 0.073f, 0.047f)),
            ("2", new RectangleF(0.20771f, 0.73895f, 0.073f, 0.047f)),
            ("3", new RectangleF(0.30441f, 0.73921f, 0.073f, 0.047f)),
            ("4", new RectangleF(0.11061f, 0.79853f, 0.073f, 0.047f)),
            ("5", new RectangleF(0.2073f, 0.7988f, 0.073f, 0.047f)),
            ("6", new RectangleF(0.30482f, 0.7988f, 0.073f, 0.047f)),
            ("7", new RectangleF(0.11102f, 0.85812f, 0.073f, 0.047f)),
            ("8", new RectangleF(0.2073f, 0.85839f, 0.073f, 0.047f)),
            ("9", new RectangleF(0.304f, 0.85812f, 0.073f, 0.047f)),
            ("0", new RectangleF(0.2073f, 0.91771f, 0.073f, 0.047f)),
            ("DOT", new RectangleF(0.11019f, 0.91638f, 0.073f, 0.047f)),
            ("PLUSMINUS", new RectangleF(0.304f, 0.91744f, 0.073f, 0.047f)),
            ("SP", new RectangleF(0.50893f, 0.91221f, 0.078f, 0.058f)),
            ("DEL", new RectangleF(0.60604f, 0.91274f, 0.078f, 0.058f)),
            ("SLASH", new RectangleF(0.70315f, 0.91247f, 0.078f, 0.058f)),
            ("CLR", new RectangleF(0.80115f, 0.91247f, 0.078f, 0.058f)),
        };
    }
}
