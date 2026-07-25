#pragma once

#include <cstdint>

namespace easycpdlc
{
    constexpr std::uint32_t kMagic = 0x45435044;
    constexpr std::uint32_t kVersion = 1;
    constexpr std::uint32_t kChecksumSeed = 0x430C0DEC;
    constexpr const char* kCommandClientDataName = "EasyCPDLC.VNS430.Command.v1";
    constexpr const char* kStatusClientDataName = "EasyCPDLC.VNS430.Status.v1";

    constexpr const char* kCommandLVar = "EASYCPDLC_VNS_COMMAND";
    constexpr const char* kModuleAliveLVar = "EASYCPDLC_VNS_MODULE_ALIVE";
    constexpr const char* kAppConnectedLVar = "EASYCPDLC_VNS_APP_CONNECTED";
    constexpr const char* kVatsimConnectedLVar = "EASYCPDLC_VNS_VATSIM_CONNECTED";
    constexpr const char* kUnreadCountLVar = "EASYCPDLC_VNS_UNREAD_COUNT";
    constexpr const char* kPageLVar = "EASYCPDLC_VNS_PAGE";
    constexpr const char* kCursorActiveLVar = "EASYCPDLC_VNS_CURSOR_ACTIVE";
    constexpr const char* kDcduModeLVar = "EASYCPDLC_DCDU_MODE";

    // CDU annunciator lamps and the EXEC light, so a hardware CDU can mirror the
    // on-screen unit. Each reads 1 while lit, 0 otherwise.
    constexpr const char* kAnnCallLVar = "EASYCPDLC_CDU_ANN_CALL";
    constexpr const char* kAnnFailLVar = "EASYCPDLC_CDU_ANN_FAIL";
    constexpr const char* kAnnMsgLVar = "EASYCPDLC_CDU_ANN_MSG";
    constexpr const char* kAnnOfstLVar = "EASYCPDLC_CDU_ANN_OFST";
    constexpr const char* kExecLightLVar = "EASYCPDLC_CDU_EXEC_LIGHT";

    constexpr std::uint32_t kStatusAppOnline = 1u << 0;
    constexpr std::uint32_t kStatusVatsimConnected = 1u << 1;
    constexpr std::uint32_t kStatusCursorActive = 1u << 2;
    constexpr std::uint32_t kStatusDcduMode = 1u << 3;
    // Spare bits in the existing flags word; the packet layout is unchanged.
    constexpr std::uint32_t kStatusAnnCall = 1u << 4;
    constexpr std::uint32_t kStatusAnnFail = 1u << 5;
    constexpr std::uint32_t kStatusAnnMsg = 1u << 6;
    constexpr std::uint32_t kStatusAnnOfst = 1u << 7;
    constexpr std::uint32_t kStatusExecLight = 1u << 8;

#pragma pack(push, 1)
    struct CommandPacket
    {
        std::uint32_t magic;
        std::uint32_t version;
        std::uint32_t sequence;
        std::uint32_t command;
        std::uint32_t checksum;
    };

    struct StatusPacket
    {
        std::uint32_t magic;
        std::uint32_t version;
        std::uint32_t sequence;
        std::uint32_t flags;
        std::uint32_t unreadCount;
        std::uint32_t page;
        std::uint32_t reserved0;
        std::uint32_t reserved1;
    };
#pragma pack(pop)

    static_assert(sizeof(CommandPacket) == 20, "Command protocol layout changed");
    static_assert(sizeof(StatusPacket) == 32, "Status protocol layout changed");

    constexpr std::uint32_t CommandChecksum(std::uint32_t sequence, std::uint32_t command)
    {
        return kChecksumSeed ^ kMagic ^ kVersion ^ sequence ^ command;
    }
}
