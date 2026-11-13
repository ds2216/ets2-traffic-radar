// ETS2 Verkehrsradar – game-side starter plugin.
//
//  * starts traffic_radar\TrafficRadar.exe (next to this DLL) hidden when the game loads plugins
//    and stops it with the game – via a job object, so it also dies if the game crashes;
//  * publishes the exact truck speed from the official telemetry SDK in shared memory
//    "Local\TrafficRadarSpeed" (see SpeedMem);
//  * opens the radar page in the default browser unless a page (open tab, tablet) connects
//    within kBrowserWaitMs – switch off with open_browser=0 in radar_launcher.ini.
//
// Uses only the official SCS telemetry API, so it should survive game updates.

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <shellapi.h>
#include <objbase.h>

#include <cstdint>
#include <string>

#include "scssdk_telemetry.h"
#include "common/scssdk_telemetry_common_channels.h"
#include "common/scssdk_telemetry_truck_common_channels.h"

namespace
{
#pragma pack(push, 1)
    struct SpeedMem
    {
        uint32_t magic;   // 0  'TRSP'
        uint32_t frame;   // 4  incremented every frame_end
        float speed;      // 8  truck.speed in m/s (negative when reversing)
        uint8_t paused;   // 12 1 while the game is paused
                          // 13
    };
#pragma pack(pop)
    static_assert(sizeof(SpeedMem) == 13);

    constexpr uint32_t kMagic = 0x50535254; // "TRSP" little-endian

    scs_log_t g_log = nullptr;
    HANDLE g_mapping = nullptr;
    volatile SpeedMem *g_mem = nullptr;
    HANDLE g_job = nullptr;

    // An already open tab reconnects on its own; Edge took 10-15 s in tests, so wait generously.
    constexpr DWORD kBrowserWaitMs = 30000;
    constexpr const wchar_t *kRadarUrl = L"http://localhost:8350/";
    HANDLE g_clientEvent = nullptr; // set by the server when a radar page connects
    HANDLE g_stopEvent = nullptr;
    HANDLE g_browserThread = nullptr;

    DWORD WINAPI browser_thread(LPVOID)
    {
        HANDLE waits[] = {g_stopEvent, g_clientEvent};
        if (WaitForMultipleObjects(2, waits, FALSE, kBrowserWaitMs) != WAIT_TIMEOUT)
            return 0; // game shutting down, or a page is already connected
        if (SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE)))
        {
            ShellExecuteW(nullptr, L"open", kRadarUrl, nullptr, nullptr, SW_SHOWNOACTIVATE);
            CoUninitialize();
        }
        return 0;
    }

    void schedule_browser(const std::wstring &dir)
    {
        const std::wstring ini = dir + L"\\radar_launcher.ini";
        if (GetPrivateProfileIntW(L"radar", L"open_browser", 1, ini.c_str()) == 0)
            return;
        g_clientEvent = CreateEventW(nullptr, TRUE, FALSE, L"Local\\TrafficRadarClient");
        g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (g_clientEvent && g_stopEvent)
            g_browserThread = CreateThread(nullptr, 0, browser_thread, nullptr, 0, nullptr);
    }

    void cancel_browser()
    {
        if (g_browserThread)
        {
            SetEvent(g_stopEvent);
            WaitForSingleObject(g_browserThread, 5000);
            CloseHandle(g_browserThread);
            g_browserThread = nullptr;
        }
        if (g_stopEvent) { CloseHandle(g_stopEvent); g_stopEvent = nullptr; }
        if (g_clientEvent) { CloseHandle(g_clientEvent); g_clientEvent = nullptr; }
    }

    void log(scs_log_type_t type, const std::string &msg)
    {
        if (g_log)
            g_log(type, ("[radar_launcher] " + msg).c_str());
    }

    std::wstring module_dir()
    {
        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&module_dir), &self);
        wchar_t path[MAX_PATH * 2];
        DWORD n = GetModuleFileNameW(self, path, static_cast<DWORD>(std::size(path)));
        std::wstring p(path, n);
        return p.substr(0, p.find_last_of(L"\\/"));
    }

    std::string narrow(const std::wstring &w)
    {
        int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
        std::string s(n > 0 ? n - 1 : 0, '\0');
        WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, s.data(), n, nullptr, nullptr);
        return s;
    }

    bool open_speed_mem()
    {
        g_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(SpeedMem),
                                       L"Local\\TrafficRadarSpeed");
        if (!g_mapping)
            return false;
        g_mem = static_cast<SpeedMem *>(MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SpeedMem)));
        if (!g_mem)
            return false;
        g_mem->frame = 0;
        g_mem->speed = 0;
        g_mem->paused = 1;
        g_mem->magic = kMagic;
        return true;
    }

    void start_server()
    {
        const std::wstring dir = module_dir() + L"\\traffic_radar";
        const std::wstring exe = dir + L"\\TrafficRadar.exe";
        if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            log(SCS_LOG_TYPE_warning, "server not found: " + narrow(exe));
            return;
        }

        g_job = CreateJobObjectW(nullptr, nullptr);
        if (g_job)
        {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info{};
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            SetInformationJobObject(g_job, JobObjectExtendedLimitInformation, &info, sizeof(info));
        }

        // The server writes its own server.log; no handles are inherited from the game.
        STARTUPINFOW si{};
        si.cb = sizeof(si);
        PROCESS_INFORMATION pi{};
        std::wstring cmd = L"\"" + exe + L"\" --log server.log";
        const BOOL ok = CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE,
                                       CREATE_NO_WINDOW | CREATE_SUSPENDED, nullptr, dir.c_str(), &si, &pi);
        if (!ok)
        {
            log(SCS_LOG_TYPE_error, "could not start server, error " + std::to_string(GetLastError()));
            return;
        }

        if (g_job && !AssignProcessToJobObject(g_job, pi.hProcess))
            log(SCS_LOG_TYPE_warning, "could not attach server to job object, error " + std::to_string(GetLastError()));
        schedule_browser(module_dir()); // before the server runs, so its client event exists
        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        log(SCS_LOG_TYPE_message, "server started (pid " + std::to_string(pi.dwProcessId) + ")");
    }

    void stop_server()
    {
        if (g_job)
        {
            TerminateJobObject(g_job, 0);
            CloseHandle(g_job);
            g_job = nullptr;
        }
    }

    SCSAPI_VOID on_speed(const scs_string_t, const scs_u32_t, const scs_value_t *const value, const scs_context_t)
    {
        if (g_mem && value && value->type == SCS_VALUE_TYPE_float)
            g_mem->speed = value->value_float.value;
    }

    SCSAPI_VOID on_frame_end(const scs_event_t, const void *const, const scs_context_t)
    {
        if (g_mem)
            g_mem->frame = g_mem->frame + 1;
    }

    SCSAPI_VOID on_pause(const scs_event_t event, const void *const, const scs_context_t)
    {
        if (g_mem)
            g_mem->paused = event == SCS_TELEMETRY_EVENT_paused ? 1 : 0;
    }
}

SCSAPI_RESULT scs_telemetry_init(const scs_u32_t version, const scs_telemetry_init_params_t *const params)
{
    if (version != SCS_TELEMETRY_VERSION_1_00 && version != SCS_TELEMETRY_VERSION_1_01)
        return SCS_RESULT_unsupported;

    const auto *p = static_cast<const scs_telemetry_init_params_v100_t *>(params);
    g_log = p->common.log;

    if (open_speed_mem())
    {
        p->register_for_channel(SCS_TELEMETRY_TRUCK_CHANNEL_speed, SCS_U32_NIL, SCS_VALUE_TYPE_float,
                                SCS_TELEMETRY_CHANNEL_FLAG_none, on_speed, nullptr);
        p->register_for_event(SCS_TELEMETRY_EVENT_frame_end, on_frame_end, nullptr);
        p->register_for_event(SCS_TELEMETRY_EVENT_paused, on_pause, nullptr);
        p->register_for_event(SCS_TELEMETRY_EVENT_started, on_pause, nullptr);
    }
    else
    {
        log(SCS_LOG_TYPE_warning, "could not create shared memory, error " + std::to_string(GetLastError()));
    }

    start_server();
    return SCS_RESULT_ok;
}

SCSAPI_VOID scs_telemetry_shutdown(void)
{
    cancel_browser();
    stop_server();
    if (g_mem)
    {
        UnmapViewOfFile(const_cast<SpeedMem *>(g_mem));
        g_mem = nullptr;
    }
    if (g_mapping)
    {
        CloseHandle(g_mapping);
        g_mapping = nullptr;
    }
    g_log = nullptr;
}

BOOL WINAPI DllMain(HMODULE, DWORD, LPVOID) { return TRUE; }
