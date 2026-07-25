using System;
using System.Runtime.InteropServices;

namespace EasyCPDLC.VNS430
{
    internal static class Vns430CompanionProtocol
    {
        internal const uint Magic = 0x45435044;
        internal const uint Version = 1;
        internal const string CommandClientDataName = "EasyCPDLC.VNS430.Command.v1";
        internal const string StatusClientDataName = "EasyCPDLC.VNS430.Status.v1";

        internal const string CommandLVar = "EASYCPDLC_VNS_COMMAND";
        internal const string ModuleAliveLVar = "EASYCPDLC_VNS_MODULE_ALIVE";
        internal const string AppConnectedLVar = "EASYCPDLC_VNS_APP_CONNECTED";
        internal const string VatsimConnectedLVar = "EASYCPDLC_VNS_VATSIM_CONNECTED";
        internal const string UnreadCountLVar = "EASYCPDLC_VNS_UNREAD_COUNT";
        internal const string PageLVar = "EASYCPDLC_VNS_PAGE";
        internal const string CursorActiveLVar = "EASYCPDLC_VNS_CURSOR_ACTIVE";
        internal const string DcduModeLVar = "EASYCPDLC_DCDU_MODE";

        // CDU annunciator lamps and the EXEC light, so a hardware CDU can mirror the
        // on-screen unit. Each reads 1 while lit, 0 otherwise.
        internal const string AnnCallLVar = "EASYCPDLC_CDU_ANN_CALL";
        internal const string AnnFailLVar = "EASYCPDLC_CDU_ANN_FAIL";
        internal const string AnnMsgLVar = "EASYCPDLC_CDU_ANN_MSG";
        internal const string AnnOfstLVar = "EASYCPDLC_CDU_ANN_OFST";
        internal const string ExecLightLVar = "EASYCPDLC_CDU_EXEC_LIGHT";

        internal const uint StatusAppOnline = 1 << 0;
        internal const uint StatusVatsimConnected = 1 << 1;
        internal const uint StatusCursorActive = 1 << 2;
        internal const uint StatusDcduMode = 1 << 3;
        // Spare bits in the existing flags word; the packet layout is unchanged.
        internal const uint StatusAnnCall = 1 << 4;
        internal const uint StatusAnnFail = 1 << 5;
        internal const uint StatusAnnMsg = 1 << 6;
        internal const uint StatusAnnOfst = 1 << 7;
        internal const uint StatusExecLight = 1 << 8;
        internal const uint ChecksumSeed = 0x430C0DEC;

        internal static uint CalculateCommandChecksum(uint sequence, uint command)
        {
            return ChecksumSeed ^ Magic ^ Version ^ sequence ^ command;
        }

        /// <summary>
        /// Whether a wire value is a command this build understands.
        /// </summary>
        /// <remarks>
        /// The alpha and digit keys are declared only by their range endpoints
        /// (CduAlphaA/CduAlphaZ and CduDigit0/CduDigit9) because the app maps them
        /// arithmetically rather than by name. Enum.IsDefined alone therefore rejects
        /// B..Y and 1..8, which silently dropped almost the entire CDU keypad coming
        /// from hardware. Accept those two spans by range.
        /// </remarks>
        internal static bool IsKnownCommand(byte value)
        {
            if (value >= (byte)Vns430Command.CduAlphaA && value <= (byte)Vns430Command.CduAlphaZ)
            {
                return true;
            }
            if (value >= (byte)Vns430Command.CduDigit0 && value <= (byte)Vns430Command.CduDigit9)
            {
                return true;
            }
            return Enum.IsDefined(typeof(Vns430Command), value);
        }

        internal static bool TryReadCommand(Vns430CompanionCommandPacket packet, out Vns430Command command)
        {
            command = Vns430Command.None;
            if (packet.Magic != Magic ||
                packet.Version != Version ||
                packet.Checksum != CalculateCommandChecksum(packet.Sequence, packet.Command) ||
                packet.Command > byte.MaxValue ||
                !IsKnownCommand((byte)packet.Command))
            {
                return false;
            }

            command = (Vns430Command)(byte)packet.Command;
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Vns430CompanionCommandPacket
    {
        internal uint Magic;
        internal uint Version;
        internal uint Sequence;
        internal uint Command;
        internal uint Checksum;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Vns430CompanionStatusPacket
    {
        internal uint Magic;
        internal uint Version;
        internal uint Sequence;
        internal uint Flags;
        internal uint UnreadCount;
        internal uint Page;
        internal uint Reserved0;
        internal uint Reserved1;
    }
}
