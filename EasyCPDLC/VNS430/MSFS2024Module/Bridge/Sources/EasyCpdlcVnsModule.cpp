#include "EasyCpdlcVnsProtocol.h"

#include <MSFS/MSFS.h>
#if defined(__wasm__)
typedef long long __int64;
#endif
#include <MSFS/MSFS_WindowsTypes.h>
#include <SimConnect.h>

#include <cstdio>
#include <cstddef>
#include <cstring>

extern "C"
{
    // This legacy ABI is available to standalone modules in both simulator
    // generations. The newer fsVars imports are not exposed by every MSFS 2024
    // standalone-module host.
    // Every name here becomes a hard WASM import: if the host does not export one,
    // the module fails to instantiate and silently never loads at all. Keep this list
    // to the three calls the standalone host is known to provide - check_named_variable
    // in particular is NOT available and must not be reintroduced.
    std::int32_t register_named_variable(const char* name);
    double get_named_variable_value(std::int32_t id);
    void set_named_variable_value(std::int32_t id, double value);
}

namespace
{
    enum ClientDataId : DWORD
    {
        CommandClientDataId = 1,
        StatusClientDataId = 2
    };

    enum DefinitionId : DWORD
    {
        CommandDefinitionId = 1,
        StatusDefinitionId = 2
    };

    enum RequestId : DWORD
    {
        StatusRequestId = 1
    };

    enum EventId : DWORD
    {
        OneSecondEventId = 100,
        FrameEventId = 101,
        SixHzEventId = 102
    };

    HANDLE g_simConnect = 0;
    std::uint32_t g_commandSequence = 0;
    float g_heartbeatSeconds = 0.0f;
    float g_statusAgeSeconds = 1000.0f;
    using LVarId = std::int32_t;
    constexpr LVarId InvalidLVarId = -1;
    LVarId g_command = InvalidLVarId;
    LVarId g_moduleAlive = InvalidLVarId;
    LVarId g_appConnected = InvalidLVarId;
    LVarId g_vatsimConnected = InvalidLVarId;
    LVarId g_unreadCount = InvalidLVarId;
    LVarId g_page = InvalidLVarId;
    LVarId g_cursorActive = InvalidLVarId;
    LVarId g_dcduModeLVar = InvalidLVarId;
    // CDU annunciator lamps and the EXEC light.
    LVarId g_annCall = InvalidLVarId;
    LVarId g_annFail = InvalidLVarId;
    LVarId g_annMsg = InvalidLVarId;
    LVarId g_annOfst = InvalidLVarId;
    LVarId g_execLight = InvalidLVarId;
    bool g_dcduMode = false;
    // Set by the one-second tick; EnsureLVarBindings re-resolves at most once per flag.
    bool g_bindingsStale = true;

    struct DcduInput
    {
        const char* name;
        std::uint32_t command;
        LVarId id;
    };

    DcduInput g_dcduInputs[] =
    {
        { "EASYCPDLC_DCDU_LSK_L1", 19, InvalidLVarId },
        { "EASYCPDLC_DCDU_LSK_L2", 20, InvalidLVarId },
        { "EASYCPDLC_DCDU_LSK_L3", 21, InvalidLVarId },
        { "EASYCPDLC_DCDU_LSK_L4", 22, InvalidLVarId },
        { "EASYCPDLC_DCDU_LSK_L5", 23, InvalidLVarId },
        { "EASYCPDLC_DCDU_LSK_L6", 24, InvalidLVarId },
        { "EASYCPDLC_DCDU_LSK_R1", 25, InvalidLVarId },
        { "EASYCPDLC_DCDU_LSK_R2", 26, InvalidLVarId },
        { "EASYCPDLC_DCDU_LSK_R3", 27, InvalidLVarId },
        { "EASYCPDLC_DCDU_LSK_R4", 28, InvalidLVarId },
        { "EASYCPDLC_DCDU_LSK_R5", 29, InvalidLVarId },
        { "EASYCPDLC_DCDU_LSK_R6", 30, InvalidLVarId },
        { "EASYCPDLC_DCDU_CONNECT", 31, InvalidLVarId },
        { "EASYCPDLC_DCDU_AOC", 32, InvalidLVarId },
        { "EASYCPDLC_DCDU_ATC", 33, InvalidLVarId },
        { "EASYCPDLC_DCDU_SETTINGS", 34, InvalidLVarId },
        { "EASYCPDLC_DCDU_RELOAD", 35, InvalidLVarId },
        { "EASYCPDLC_DCDU_PRINT", 36, InvalidLVarId },
        { "EASYCPDLC_DCDU_REPRINT", 37, InvalidLVarId },
        { "EASYCPDLC_DCDU_HIDE", 38, InvalidLVarId }
    };

    // Boeing CDU (PFP/FMC) keypad. One momentary L-var per key so a WinWing CDU can be
    // bound in MobiFlight the same way as any other aircraft profile. Command values match
    // the Vns430Command enum on the desktop side.
    DcduInput g_cduInputs[] =
    {
        { "EASYCPDLC_CDU_A", 39, InvalidLVarId }, { "EASYCPDLC_CDU_B", 40, InvalidLVarId },
        { "EASYCPDLC_CDU_C", 41, InvalidLVarId }, { "EASYCPDLC_CDU_D", 42, InvalidLVarId },
        { "EASYCPDLC_CDU_E", 43, InvalidLVarId }, { "EASYCPDLC_CDU_F", 44, InvalidLVarId },
        { "EASYCPDLC_CDU_G", 45, InvalidLVarId }, { "EASYCPDLC_CDU_H", 46, InvalidLVarId },
        { "EASYCPDLC_CDU_I", 47, InvalidLVarId }, { "EASYCPDLC_CDU_J", 48, InvalidLVarId },
        { "EASYCPDLC_CDU_K", 49, InvalidLVarId }, { "EASYCPDLC_CDU_L", 50, InvalidLVarId },
        { "EASYCPDLC_CDU_M", 51, InvalidLVarId }, { "EASYCPDLC_CDU_N", 52, InvalidLVarId },
        { "EASYCPDLC_CDU_O", 53, InvalidLVarId }, { "EASYCPDLC_CDU_P", 54, InvalidLVarId },
        { "EASYCPDLC_CDU_Q", 55, InvalidLVarId }, { "EASYCPDLC_CDU_R", 56, InvalidLVarId },
        { "EASYCPDLC_CDU_S", 57, InvalidLVarId }, { "EASYCPDLC_CDU_T", 58, InvalidLVarId },
        { "EASYCPDLC_CDU_U", 59, InvalidLVarId }, { "EASYCPDLC_CDU_V", 60, InvalidLVarId },
        { "EASYCPDLC_CDU_W", 61, InvalidLVarId }, { "EASYCPDLC_CDU_X", 62, InvalidLVarId },
        { "EASYCPDLC_CDU_Y", 63, InvalidLVarId }, { "EASYCPDLC_CDU_Z", 64, InvalidLVarId },
        { "EASYCPDLC_CDU_0", 65, InvalidLVarId }, { "EASYCPDLC_CDU_1", 66, InvalidLVarId },
        { "EASYCPDLC_CDU_2", 67, InvalidLVarId }, { "EASYCPDLC_CDU_3", 68, InvalidLVarId },
        { "EASYCPDLC_CDU_4", 69, InvalidLVarId }, { "EASYCPDLC_CDU_5", 70, InvalidLVarId },
        { "EASYCPDLC_CDU_6", 71, InvalidLVarId }, { "EASYCPDLC_CDU_7", 72, InvalidLVarId },
        { "EASYCPDLC_CDU_8", 73, InvalidLVarId }, { "EASYCPDLC_CDU_9", 74, InvalidLVarId },
        { "EASYCPDLC_CDU_SP", 75, InvalidLVarId }, { "EASYCPDLC_CDU_DOT", 76, InvalidLVarId },
        { "EASYCPDLC_CDU_SLASH", 77, InvalidLVarId }, { "EASYCPDLC_CDU_PLUSMINUS", 78, InvalidLVarId },
        { "EASYCPDLC_CDU_CLR", 79, InvalidLVarId }, { "EASYCPDLC_CDU_DEL", 80, InvalidLVarId },
        { "EASYCPDLC_CDU_INIT_REF", 81, InvalidLVarId }, { "EASYCPDLC_CDU_RTE", 82, InvalidLVarId },
        { "EASYCPDLC_CDU_DEP_ARR", 83, InvalidLVarId }, { "EASYCPDLC_CDU_CLB", 84, InvalidLVarId },
        { "EASYCPDLC_CDU_CRZ", 85, InvalidLVarId }, { "EASYCPDLC_CDU_FIX", 86, InvalidLVarId },
        { "EASYCPDLC_CDU_LEGS", 87, InvalidLVarId }, { "EASYCPDLC_CDU_HOLD", 88, InvalidLVarId },
        { "EASYCPDLC_CDU_DES", 89, InvalidLVarId }, { "EASYCPDLC_CDU_PROG", 90, InvalidLVarId },
        { "EASYCPDLC_CDU_EXEC", 91, InvalidLVarId }, { "EASYCPDLC_CDU_MENU", 92, InvalidLVarId },
        { "EASYCPDLC_CDU_N1_LIMIT", 93, InvalidLVarId }, { "EASYCPDLC_CDU_PREV_PAGE", 94, InvalidLVarId },
        { "EASYCPDLC_CDU_NEXT_PAGE", 95, InvalidLVarId }, { "EASYCPDLC_CDU_BRT_UP", 96, InvalidLVarId },
        { "EASYCPDLC_CDU_BRT_DN", 97, InvalidLVarId }
    };

    void EnsureLVarBindings();

    void SetLVar(LVarId id, double value)
    {
        if (id != InvalidLVarId)
        {
            set_named_variable_value(id, value);
        }
    }

    void PublishCommand(std::uint32_t command)
    {
        if (g_simConnect == 0)
        {
            return;
        }

        easycpdlc::CommandPacket packet{};
        packet.magic = easycpdlc::kMagic;
        packet.version = easycpdlc::kVersion;
        packet.sequence = ++g_commandSequence;
        packet.command = command;
        packet.checksum = easycpdlc::CommandChecksum(packet.sequence, packet.command);

        SimConnect_SetClientData(
            g_simConnect,
            CommandClientDataId,
            CommandDefinitionId,
            SIMCONNECT_CLIENT_DATA_SET_FLAG_DEFAULT,
            0,
            sizeof(packet),
            &packet);
    }

    void ClearDcduInputs()
    {
        for (auto& input : g_dcduInputs)
        {
            SetLVar(input.id, 0.0);
        }
        for (auto& input : g_cduInputs)
        {
            SetLVar(input.id, 0.0);
        }
    }

    // Extinguish every CDU lamp. Used at startup and whenever the desktop app goes
    // away, so a hardware annunciator never stays lit on stale state.
    void ClearLamps()
    {
        SetLVar(g_annCall, 0.0);
        SetLVar(g_annFail, 0.0);
        SetLVar(g_annMsg, 0.0);
        SetLVar(g_annOfst, 0.0);
        SetLVar(g_execLight, 0.0);
    }

    void ApplyStatus(const easycpdlc::StatusPacket& packet)
    {
        if (packet.magic != easycpdlc::kMagic || packet.version != easycpdlc::kVersion)
        {
            return;
        }

        g_statusAgeSeconds = 0.0f;
        EnsureLVarBindings();
        SetLVar(g_appConnected, (packet.flags & easycpdlc::kStatusAppOnline) != 0 ? 1.0 : 0.0);
        SetLVar(g_vatsimConnected, (packet.flags & easycpdlc::kStatusVatsimConnected) != 0 ? 1.0 : 0.0);
        SetLVar(g_cursorActive, (packet.flags & easycpdlc::kStatusCursorActive) != 0 ? 1.0 : 0.0);
        const bool dcduMode = (packet.flags & easycpdlc::kStatusDcduMode) != 0;
        if (g_dcduMode && !dcduMode)
        {
            ClearDcduInputs();
        }
        g_dcduMode = dcduMode;
        SetLVar(g_dcduModeLVar, g_dcduMode ? 1.0 : 0.0);
        SetLVar(g_unreadCount, static_cast<double>(packet.unreadCount));
        SetLVar(g_page, static_cast<double>(packet.page));
        SetLVar(g_annCall, (packet.flags & easycpdlc::kStatusAnnCall) != 0 ? 1.0 : 0.0);
        SetLVar(g_annFail, (packet.flags & easycpdlc::kStatusAnnFail) != 0 ? 1.0 : 0.0);
        SetLVar(g_annMsg, (packet.flags & easycpdlc::kStatusAnnMsg) != 0 ? 1.0 : 0.0);
        SetLVar(g_annOfst, (packet.flags & easycpdlc::kStatusAnnOfst) != 0 ? 1.0 : 0.0);
        SetLVar(g_execLight, (packet.flags & easycpdlc::kStatusExecLight) != 0 ? 1.0 : 0.0);
    }

    void ProcessTick(float elapsed);
    void PollInputs();

    void CALLBACK Dispatch(SIMCONNECT_RECV* data, DWORD size, void*)
    {
        if (data == nullptr)
        {
            return;
        }

        if (data->dwID == SIMCONNECT_RECV_ID_CLIENT_DATA)
        {
            const auto* received = reinterpret_cast<const SIMCONNECT_RECV_CLIENT_DATA*>(data);
            constexpr std::size_t payloadOffset = offsetof(SIMCONNECT_RECV_CLIENT_DATA, dwData);
            if (received->dwRequestID != StatusRequestId ||
                size < payloadOffset + sizeof(easycpdlc::StatusPacket))
            {
                return;
            }

            easycpdlc::StatusPacket packet{};
            std::memcpy(&packet, &received->dwData, sizeof(packet));
            ApplyStatus(packet);
            return;
        }

        // "Frame" is delivered as its own receive id with a wider struct, not as a
        // plain SIMCONNECT_RECV_ID_EVENT.
        if (data->dwID == SIMCONNECT_RECV_ID_EVENT_FRAME)
        {
            const auto* frameData = reinterpret_cast<const SIMCONNECT_RECV_EVENT_FRAME*>(data);
            if (frameData->uEventID == FrameEventId)
            {
                PollInputs();
            }
            return;
        }

        if (data->dwID == SIMCONNECT_RECV_ID_EVENT)
        {
            const auto* eventData = reinterpret_cast<const SIMCONNECT_RECV_EVENT*>(data);
            if (eventData->uEventID == OneSecondEventId)
            {
                ProcessTick(1.0f);
            }
            else if (eventData->uEventID == SixHzEventId)
            {
                PollInputs();
            }
        }
    }

    bool OpenClientDataChannels()
    {
        if (SimConnect_Open(&g_simConnect, "EasyCPDLC VNS430 bridge", 0, 0, 0, 0) < 0 ||
            g_simConnect == 0)
        {
            g_simConnect = 0;
            return false;
        }

        if (SimConnect_MapClientDataNameToID(g_simConnect, easycpdlc::kCommandClientDataName, CommandClientDataId) < 0 ||
            SimConnect_MapClientDataNameToID(g_simConnect, easycpdlc::kStatusClientDataName, StatusClientDataId) < 0 ||
            SimConnect_AddToClientDataDefinition(g_simConnect, CommandDefinitionId, 0, sizeof(easycpdlc::CommandPacket)) < 0 ||
            SimConnect_AddToClientDataDefinition(g_simConnect, StatusDefinitionId, 0, sizeof(easycpdlc::StatusPacket)) < 0)
        {
            SimConnect_Close(g_simConnect);
            g_simConnect = 0;
            return false;
        }

        // Creation can report an error when the desktop endpoint started first and
        // already created the same named areas. The mapped channels remain usable.
        SimConnect_CreateClientData(g_simConnect, CommandClientDataId, sizeof(easycpdlc::CommandPacket), 0);
        SimConnect_CreateClientData(g_simConnect, StatusClientDataId, sizeof(easycpdlc::StatusPacket), 0);

        if (SimConnect_RequestClientData(
            g_simConnect,
            StatusClientDataId,
            StatusRequestId,
            StatusDefinitionId,
            SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET,
            SIMCONNECT_CLIENT_DATA_REQUEST_FLAG_CHANGED) < 0)
        {
            SimConnect_Close(g_simConnect);
            g_simConnect = 0;
            return false;
        }

        if (SimConnect_SubscribeToSystemEvent(
                g_simConnect,
                OneSecondEventId,
                "1sec") < 0 ||
            SimConnect_CallDispatch(g_simConnect, Dispatch, nullptr) < 0)
        {
            SimConnect_Close(g_simConnect);
            g_simConnect = 0;
            return false;
        }

        // Faster input polling is a latency optimisation, not a requirement: ProcessTick
        // polls once a second regardless. Not every standalone-module host exposes these,
        // so a failure here must NOT tear down the channel - chaining "Frame" into the
        // fatal check above took the whole bridge offline on a host that refuses it.
        // Whichever of the two the host honours simply raises the polling rate.
        SimConnect_SubscribeToSystemEvent(g_simConnect, FrameEventId, "Frame");
        SimConnect_SubscribeToSystemEvent(g_simConnect, SixHzEventId, "6Hz");

        return true;
    }

    // Bind every L-var name to its current id.
    //
    // register_named_variable hands back an index into the simulator's L-var table, and
    // that table is rebuilt whenever a flight or aircraft loads. module_init runs long
    // before the first aircraft exists, so ids captured there go stale the moment the
    // user enters a flight: reads return 0 for ever, and writes land on whatever now
    // occupies the slot. MobiFlight is unaffected because "1 (>L:NAME)" resolves the
    // name at execution time - which is exactly why a hardware key looks perfectly
    // healthy all the way up to this module and then vanishes.
    //
    // Re-binding is idempotent: an existing name keeps its id and its value. So the
    // cheapest correct answer is to simply re-resolve every id once a second.
    void ResolveLVarIds()
    {
        g_command = register_named_variable(easycpdlc::kCommandLVar);
        g_moduleAlive = register_named_variable(easycpdlc::kModuleAliveLVar);
        g_appConnected = register_named_variable(easycpdlc::kAppConnectedLVar);
        g_vatsimConnected = register_named_variable(easycpdlc::kVatsimConnectedLVar);
        g_unreadCount = register_named_variable(easycpdlc::kUnreadCountLVar);
        g_page = register_named_variable(easycpdlc::kPageLVar);
        g_cursorActive = register_named_variable(easycpdlc::kCursorActiveLVar);
        g_dcduModeLVar = register_named_variable(easycpdlc::kDcduModeLVar);
        g_annCall = register_named_variable(easycpdlc::kAnnCallLVar);
        g_annFail = register_named_variable(easycpdlc::kAnnFailLVar);
        g_annMsg = register_named_variable(easycpdlc::kAnnMsgLVar);
        g_annOfst = register_named_variable(easycpdlc::kAnnOfstLVar);
        g_execLight = register_named_variable(easycpdlc::kExecLightLVar);
        for (auto& input : g_dcduInputs)
        {
            input.id = register_named_variable(input.name);
        }
        for (auto& input : g_cduInputs)
        {
            input.id = register_named_variable(input.name);
        }
    }

    // Re-bind unconditionally. There is no way to cheaply detect a rebuilt L-var table
    // without check_named_variable, which this host does not export, and
    // register_named_variable is itself idempotent - an existing name keeps its id and
    // its value. Roughly 115 lookups once a second is far cheaper than being wrong.
    //
    // The one-second tick sets the flag; every caller shares that budget, so status
    // packets arriving twice a second cannot multiply the work.
    void EnsureLVarBindings()
    {
        if (!g_bindingsStale)
        {
            return;
        }

        g_bindingsStale = false;
        ResolveLVarIds();
    }

    void RegisterLVars()
    {
        ResolveLVarIds();

        SetLVar(g_command, 0.0);
        SetLVar(g_moduleAlive, 0.0);
        SetLVar(g_appConnected, 0.0);
        SetLVar(g_vatsimConnected, 0.0);
        SetLVar(g_unreadCount, 0.0);
        SetLVar(g_page, 0.0);
        SetLVar(g_cursorActive, 0.0);
        SetLVar(g_dcduModeLVar, 0.0);
        ClearLamps();
        ClearDcduInputs();
    }

    // Hardware keys are polled every visual frame. The one-second system event used to
    // do this, which meant a key press could sit unseen for a whole second and two
    // presses inside that second collapsed into one - unusable for a CDU keypad.
    void PollInputs()
    {
        if (g_simConnect == 0)
        {
            return;
        }

        // Bindings are refreshed by the one-second tick, not here: this runs every
        // frame and re-resolving the whole set that often would be pure waste.
        const double commandValue = get_named_variable_value(g_command);
        if (commandValue != 0.0)
        {
            SetLVar(g_command, 0.0);
            const auto command = static_cast<std::uint32_t>(commandValue + 0.5);
            if (!g_dcduMode && command >= 1 && command <= 18)
            {
                PublishCommand(command);
            }
        }

        if (!g_dcduMode)
        {
            return;
        }

        for (auto& input : g_dcduInputs)
        {
            const double value = get_named_variable_value(input.id);
            if (value != 0.0)
            {
                SetLVar(input.id, 0.0);
                PublishCommand(input.command);
            }
        }
        for (auto& input : g_cduInputs)
        {
            const double value = get_named_variable_value(input.id);
            if (value != 0.0)
            {
                SetLVar(input.id, 0.0);
                PublishCommand(input.command);
            }
        }
    }

    void ProcessTick(float elapsed)
    {
        if (g_simConnect == 0)
        {
            return;
        }

        // Loading a flight or swapping aircraft rebuilds the L-var table, so every id
        // has to be re-bound or both reads and writes silently address the wrong slot.
        g_bindingsStale = true;
        EnsureLVarBindings();

        g_heartbeatSeconds += elapsed;
        g_statusAgeSeconds += elapsed;

        // Safety net: the Frame event does not fire outside an active flight, and a
        // host that never sends it at all would otherwise leave the hardware dead.
        // Polling here too costs nothing and degrades to the old one-second cadence.
        PollInputs();

        // Deliberately no periodic clear while hardware keys are off. ApplyStatus
        // already clears once on the transition out of the mode, and now that the
        // bindings actually resolve, zeroing every second would fight MobiFlight for
        // ownership of the L-vars and make its writes look like they never landed.

        if (g_heartbeatSeconds >= 1.0f)
        {
            g_heartbeatSeconds = 0.0f;
            PublishCommand(0);
            SetLVar(g_moduleAlive, 1.0);
        }

        if (g_statusAgeSeconds >= 3.0f)
        {
            SetLVar(g_appConnected, 0.0);
            SetLVar(g_vatsimConnected, 0.0);
            SetLVar(g_dcduModeLVar, 0.0);
            g_dcduMode = false;
            ClearLamps();
            ClearDcduInputs();
        }
    }
}

extern "C" MSFS_CALLBACK void module_init(void)
{
    RegisterLVars();
    const bool opened = OpenClientDataChannels();
    SetLVar(g_moduleAlive, opened ? 1.0 : 0.0);
    std::printf("[EasyCPDLC] VNS430 bridge %s\n", opened ? "ready" : "could not open SimConnect");
}

extern "C" MSFS_CALLBACK void module_deinit(void)
{
    SetLVar(g_moduleAlive, 0.0);
    SetLVar(g_appConnected, 0.0);
    SetLVar(g_dcduModeLVar, 0.0);
    ClearDcduInputs();
    if (g_simConnect != 0)
    {
        SimConnect_Close(g_simConnect);
        g_simConnect = 0;
    }
    std::printf("[EasyCPDLC] VNS430 bridge stopped\n");
}
