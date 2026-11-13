// Minimal stand-in for the game: loads radar_launcher.dll, calls init, feeds a fake speed.
// Usage: test_host <dll> <seconds> [crash]   ("crash" = exit without calling shutdown)
#include <Windows.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include "scssdk_telemetry.h"

static scs_telemetry_channel_callback_t g_speed_cb;
static scs_telemetry_event_callback_t g_frame_cb;

SCSAPI_VOID fake_log(const scs_log_type_t type, const scs_string_t msg) { printf("[game log %d] %s\n", type, msg); }

SCSAPI_RESULT fake_reg_event(const scs_event_t ev, const scs_telemetry_event_callback_t cb, const scs_context_t)
{
    if (ev == SCS_TELEMETRY_EVENT_frame_end) g_frame_cb = cb;
    return SCS_RESULT_ok;
}
SCSAPI_RESULT fake_unreg_event(const scs_event_t) { return SCS_RESULT_ok; }
SCSAPI_RESULT fake_reg_channel(const scs_string_t name, const scs_u32_t, const scs_value_type_t,
                               const scs_u32_t, const scs_telemetry_channel_callback_t cb, const scs_context_t)
{
    if (strcmp(name, "truck.speed") == 0) g_speed_cb = cb;
    return SCS_RESULT_ok;
}
SCSAPI_RESULT fake_unreg_channel(const scs_string_t, const scs_u32_t, const scs_value_type_t) { return SCS_RESULT_ok; }

int main(int argc, char **argv)
{
    if (argc < 3) return 2;
    HMODULE dll = LoadLibraryA(argv[1]);
    if (!dll) { printf("LoadLibrary failed %lu\n", GetLastError()); return 1; }
    using init_t = scs_result_t(SCSAPIFUNC *)(scs_u32_t, const scs_telemetry_init_params_t *);
    using shutdown_t = void(SCSAPIFUNC *)();
    auto init = (init_t)GetProcAddress(dll, "scs_telemetry_init");
    auto shutdown = (shutdown_t)GetProcAddress(dll, "scs_telemetry_shutdown");
    if (!init || !shutdown) { printf("exports missing\n"); return 1; }

    scs_telemetry_init_params_v101_t p{};
    p.common.log = fake_log;
    p.common.game_name = "Euro Truck Simulator 2 (fake)";
    p.common.game_id = "eut2";
    p.register_for_event = fake_reg_event;
    p.unregister_from_event = fake_unreg_event;
    p.register_for_channel = fake_reg_channel;
    p.unregister_from_channel = fake_unreg_channel;
    printf("init -> %d\n", init(SCS_TELEMETRY_VERSION_1_01, &p));

    int seconds = atoi(argv[2]);
    for (int i = 0; i < seconds * 20; i++)
    {
        scs_value_t v{};
        v.type = SCS_VALUE_TYPE_float;
        v.value_float.value = 12.5f; // 45 km/h
        if (g_speed_cb) g_speed_cb("truck.speed", SCS_U32_NIL, &v, nullptr);
        if (g_frame_cb) g_frame_cb(SCS_TELEMETRY_EVENT_frame_end, nullptr, nullptr);
        Sleep(50);
    }

    if (argc > 3 && strcmp(argv[3], "crash") == 0)
    {
        printf("simulating crash\n");
        fflush(stdout);
        TerminateProcess(GetCurrentProcess(), 3);
    }
    shutdown();
    printf("shutdown done\n");
    return 0;
}
