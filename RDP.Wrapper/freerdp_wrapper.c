#include "freerdp_wrapper.h"

#include <freerdp/freerdp.h>
#include <freerdp/addin.h>
#include <freerdp/client.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/rdpgfx.h>
#include <freerdp/channels/channels.h>
#include <freerdp/channels/rdpgfx.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/gdi/gfx.h>
#include <freerdp/settings.h>
#include <freerdp/update.h>
#include <winpr/sysinfo.h>
#include <winpr/stream.h>
#include <winpr/synch.h>
#include <winpr/wtypes.h>
#include <winpr/thread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <freerdp/client/disp.h>

#define RESIZE_QUIET_DELAY_MS 1000
#define RESIZE_MIN_DELAY_MS 1500

static FrameCallback g_callback = NULL;
static freerdp* g_instance = NULL;
static DispClientContext* g_disp = NULL;
static RdpgfxClientContext* g_gfx = NULL;
static HANDLE g_thread = NULL;
static bool g_running = false;
static bool g_disp_ready = false;
static UINT32 g_last_sent_width = 0;
static UINT32 g_last_sent_height = 0;
static ULONGLONG g_last_resize_tick = 0;
static ULONGLONG g_resize_queued_tick = 0;
static CRITICAL_SECTION g_resize_lock;
static bool g_resize_lock_initialized = false;
static bool g_resize_pending = false;
static UINT32 g_target_width = 0;
static UINT32 g_target_height = 0;

typedef struct {
    char host[256];
    char domain[256];
    char user[256];
    char password[256];
    char gateway_host[256];
    char gateway_domain[256];
    char gateway_user[256];
    char gateway_password[256];
    int width;
    int height;
} connection_params;

static BOOL on_surface_bits(rdpContext* context, const SURFACE_BITS_COMMAND* cmd);
static BOOL on_end_paint(rdpContext* context);
static BOOL on_desktop_resize(rdpContext* context);

static BOOL resize_local_framebuffer(rdpContext* context, UINT32 width, UINT32 height)
{
    if (!context || !context->gdi) return TRUE;

    if (context->gdi->width == width && context->gdi->height == height) return TRUE;

    printf("[DEBUG] Local framebuffer resize: %ux%u\n", width, height);
    if (!gdi_resize(context->gdi, width, height))
    {
        fprintf(stderr, "Failed to resize local GDI framebuffer to %ux%u\n", width, height);
        return FALSE;
    }

    context->update->SurfaceBits = on_surface_bits;
    context->update->EndPaint = on_end_paint;
    context->update->DesktopResize = on_desktop_resize;

    if (g_callback && context->gdi->primary_buffer)
    {
        g_callback((uint8_t*)context->gdi->primary_buffer, context->gdi->width, context->gdi->height);
    }

    return TRUE;
}

static void init_graphics_pipeline(rdpContext* context)
{
    if (!context || !context->gdi || !g_gfx) return;

    if (!gdi_graphics_pipeline_init(context->gdi, g_gfx))
    {
        fprintf(stderr, "Failed to initialize RDPGFX GDI pipeline\n");
        return;
    }

    printf("[DEBUG] RDPGFX GDI pipeline initialized\n");
}

void set_frame_callback(FrameCallback cb) {
    g_callback = cb;
}

static UINT32 clamp_uint32(UINT32 value, UINT32 min, UINT32 max)
{
    if (value < min) return min;
    if (value > max) return max;
    return value;
}

static UINT32 physical_size_from_pixels(UINT32 pixels)
{
    return clamp_uint32((pixels * 254 + 375) / 750, DISPLAY_CONTROL_MIN_PHYSICAL_MONITOR_WIDTH,
                        DISPLAY_CONTROL_MAX_PHYSICAL_MONITOR_WIDTH);
}

static void normalize_resolution(UINT32* width, UINT32* height)
{
    *width = clamp_uint32(*width, DISPLAY_CONTROL_MIN_MONITOR_WIDTH, DISPLAY_CONTROL_MAX_MONITOR_WIDTH);
    *height = clamp_uint32(*height, DISPLAY_CONTROL_MIN_MONITOR_HEIGHT, DISPLAY_CONTROL_MAX_MONITOR_HEIGHT);
    *width -= *width % 2;
}

static void queue_resolution_update(UINT32 width, UINT32 height)
{
    normalize_resolution(&width, &height);

    EnterCriticalSection(&g_resize_lock);
    if (width != g_target_width || height != g_target_height)
    {
        g_target_width = width;
        g_target_height = height;
        g_resize_pending = true;
        g_resize_queued_tick = GetTickCount64();
    }
    LeaveCriticalSection(&g_resize_lock);
}

static BOOL on_end_paint(rdpContext* context)
{
    if (!g_callback) return TRUE;

    rdpGdi* gdi = context->gdi;
    if (gdi && gdi->primary_buffer) {
        g_callback((uint8_t*)gdi->primary_buffer, gdi->width, gdi->height);
    }

    return TRUE;
}

static BOOL on_desktop_resize(rdpContext* context)
{
    if (!context || !context->gdi || !context->settings) return TRUE;

    UINT32 width = freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopWidth);
    UINT32 height = freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopHeight);
    printf("[DEBUG] Desktop Resize: %ux%u\n", width, height);

    if (!resize_local_framebuffer(context, width, height))
    {
        return FALSE;
    }

    return TRUE;
}

static UINT on_display_control_caps(DispClientContext* context, UINT32 maxNumMonitors,
                                    UINT32 maxMonitorAreaFactorA, UINT32 maxMonitorAreaFactorB)
{
    (void)context;
    g_disp_ready = true;
    printf("[DEBUG] Display Control caps: maxMonitors=%u areaFactor=%ux%u\n",
           maxNumMonitors, maxMonitorAreaFactorA, maxMonitorAreaFactorB);
    return CHANNEL_RC_OK;
}

static void on_channel_connected(void* context, const ChannelConnectedEventArgs* e)
{
    printf("[DEBUG] Channel connected: %s\n", e->name);
    if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
    {
        g_disp = (DispClientContext*)e->pInterface;
        g_disp_ready = false;
        if (g_disp)
        {
            g_disp->DisplayControlCaps = on_display_control_caps;
        }
        printf("Display Control channel connected\n");
    }
    else if (strcmp(e->name, "drdynvc") == 0)
    {
        printf("DVC manager connected\n");
    }
    else if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0)
    {
        g_gfx = (RdpgfxClientContext*)e->pInterface;
        init_graphics_pipeline((rdpContext*)context);
    }
}

static void on_channel_disconnected(void* context, const ChannelDisconnectedEventArgs* e)
{
    printf("[DEBUG] Channel disconnected: %s\n", e->name);
    if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
    {
        g_disp = NULL;
        g_disp_ready = false;
    }
    else if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0)
    {
        rdpContext* rdp_context = (rdpContext*)context;
        if (rdp_context && rdp_context->gdi && g_gfx)
        {
            gdi_graphics_pipeline_uninit(rdp_context->gdi, g_gfx);
        }
        g_gfx = NULL;
    }
}

static void on_graphics_reset(void* context, const GraphicsResetEventArgs* e)
{
    rdpContext* rdp_context = (rdpContext*)context;
    UINT32 width = freerdp_settings_get_uint32(rdp_context->settings, FreeRDP_DesktopWidth);
    UINT32 height = freerdp_settings_get_uint32(rdp_context->settings, FreeRDP_DesktopHeight);
    printf("[DEBUG] Graphics Reset: %ux%u\n", width, height);

    gdi_free(rdp_context->instance);
    if (!gdi_init(rdp_context->instance, PIXEL_FORMAT_BGRA32))
    {
        fprintf(stderr, "Failed to re-initialize GDI\n");
    }

    // After GDI re-init, we need to re-hook the callbacks
    rdp_context->update->SurfaceBits = on_surface_bits;
    rdp_context->update->EndPaint = on_end_paint;
    rdp_context->update->DesktopResize = on_desktop_resize;
    init_graphics_pipeline(rdp_context);
}

static bool process_pending_resize(void)
{
    if (!g_instance || !g_instance->context || !g_disp || !g_disp_ready) return true;

    UINT32 width = 0;
    UINT32 height = 0;
    ULONGLONG queued_tick = 0;
    EnterCriticalSection(&g_resize_lock);
    bool pending = g_resize_pending;
    if (pending)
    {
        width = g_target_width;
        height = g_target_height;
        queued_tick = g_resize_queued_tick;
    }
    LeaveCriticalSection(&g_resize_lock);

    if (!pending) return true;
    ULONGLONG now = GetTickCount64();
    if (queued_tick != 0 && now - queued_tick < RESIZE_QUIET_DELAY_MS) return true;

    if (width == g_last_sent_width && height == g_last_sent_height)
    {
        EnterCriticalSection(&g_resize_lock);
        if (g_target_width == width && g_target_height == height) g_resize_pending = false;
        LeaveCriticalSection(&g_resize_lock);
        return true;
    }

    if (g_last_resize_tick != 0 && now - g_last_resize_tick < RESIZE_MIN_DELAY_MS) return true;

    rdpSettings* settings = g_instance->context->settings;
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, width);
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopHeight, height);

    DISPLAY_CONTROL_MONITOR_LAYOUT layout;
    memset(&layout, 0, sizeof(layout));
    layout.Flags = DISPLAY_CONTROL_MONITOR_PRIMARY;
    layout.Top = 0;
    layout.Left = 0;
    layout.Width = width;
    layout.Height = height;
    layout.PhysicalWidth = physical_size_from_pixels(width);
    layout.PhysicalHeight = physical_size_from_pixels(height);
    layout.Orientation = ORIENTATION_LANDSCAPE;
    layout.DesktopScaleFactor = freerdp_settings_get_uint32(settings, FreeRDP_DesktopScaleFactor);
    layout.DeviceScaleFactor = freerdp_settings_get_uint32(settings, FreeRDP_DeviceScaleFactor);

    UINT rc = g_disp->SendMonitorLayout(g_disp, 1, &layout);
    if (rc != CHANNEL_RC_OK)
    {
        fprintf(stderr, "Failed to send monitor layout update %ux%u, rc=%u\n", width, height, rc);
        return true;
    }

    g_last_sent_width = width;
    g_last_sent_height = height;
    g_last_resize_tick = now;

    if (!resize_local_framebuffer(g_instance->context, width, height))
    {
        return true;
    }

    EnterCriticalSection(&g_resize_lock);
    if (g_target_width == width && g_target_height == height) g_resize_pending = false;
    LeaveCriticalSection(&g_resize_lock);

    printf("Sent monitor layout update after debounce: %ux%u\n", width, height);
    return true;
}

static BOOL on_surface_bits(rdpContext* context,
                            const SURFACE_BITS_COMMAND* cmd)
{
    (void)context;
    (void)cmd;
    return TRUE;
}

static DWORD WINAPI rdp_thread_func(LPVOID lpParam) {
    connection_params* params = (connection_params*)lpParam;

    g_instance = freerdp_new();
    if (!g_instance) {
        fprintf(stderr, "Failed to create FreeRDP instance\n");
        free(params);
        g_running = false;
        return 1;
    }

    g_instance->LoadChannels = freerdp_client_load_channels;
    if (freerdp_register_addin_provider(freerdp_channels_load_static_addin_entry, 0) != CHANNEL_RC_OK)
    {
        fprintf(stderr, "Failed to register FreeRDP static addin provider\n");
        freerdp_free(g_instance);
        g_instance = NULL;
        free(params);
        g_running = false;
        return 1;
    }

    g_instance->ContextSize = sizeof(rdpContext);
    if (!freerdp_context_new(g_instance)) {
        fprintf(stderr, "Failed to create FreeRDP context\n");
        freerdp_free(g_instance);
        g_instance = NULL;
        free(params);
        g_running = false;
        return 1;
    }

    rdpSettings* settings = g_instance->context->settings;
    freerdp_settings_set_string(settings, FreeRDP_ServerHostname, params->host);
    freerdp_settings_set_string(settings, FreeRDP_Domain, params->domain);
    freerdp_settings_set_string(settings, FreeRDP_Username, params->user);
    freerdp_settings_set_string(settings, FreeRDP_Password, params->password);

    if (params->gateway_host[0] != '\0') {
        freerdp_settings_set_bool(settings, FreeRDP_GatewayEnabled, TRUE);
        freerdp_settings_set_string(settings, FreeRDP_GatewayHostname, params->gateway_host);
        freerdp_settings_set_string(settings, FreeRDP_GatewayDomain, params->gateway_domain);
        freerdp_settings_set_string(settings, FreeRDP_GatewayUsername, params->gateway_user);
        freerdp_settings_set_string(settings, FreeRDP_GatewayPassword, params->gateway_password);
        freerdp_settings_set_bool(settings, FreeRDP_GatewayUseSameCredentials, FALSE);

        // Use the standard HTTP transport flag which is widely supported and for now the only supported method.
        freerdp_settings_set_bool(settings, FreeRDP_GatewayHttpTransport, TRUE);
        freerdp_settings_set_bool(settings, FreeRDP_GatewayBypassLocal, FALSE);
    }

    // For now always ignore certificate. Adding a popup to review the certificate should be implemented for v1
    freerdp_settings_set_bool(settings, FreeRDP_IgnoreCertificate, TRUE);

    // Disable FreeRDP's own window/UI creation
    freerdp_settings_set_bool(settings, FreeRDP_DeactivateClientDecoding, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_SoftwareGdi, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_AutoReconnectionEnabled, TRUE);

    freerdp_settings_set_bool(settings, FreeRDP_SupportDynamicChannels, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_SupportDisplayControl, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_SupportMonitorLayoutPdu, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_DynamicResolutionUpdate, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_MstscCookieMode, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_AudioPlayback, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DeviceRedirection, TRUE);

    printf("[DEBUG] Channels set up, connecting...\n");
    freerdp_settings_set_string(settings, FreeRDP_ClientHostname, "AvaloniaRDP");

    freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, 32);
    UINT32 desktop_width = (UINT32)params->width;
    UINT32 desktop_height = (UINT32)params->height;
    normalize_resolution(&desktop_width, &desktop_height);
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, desktop_width);
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopHeight, desktop_height);
    g_last_sent_width = desktop_width;
    g_last_sent_height = desktop_height;

    freerdp_settings_set_uint32(settings, FreeRDP_MonitorCount, 1);
    freerdp_settings_set_bool(settings, FreeRDP_UseMultimon, TRUE);

    rdpMonitor* monitors = calloc(1, sizeof(rdpMonitor));
    monitors[0].x = 0;
    monitors[0].y = 0;
    monitors[0].width = desktop_width;
    monitors[0].height = desktop_height;
    monitors[0].attributes.physicalWidth = physical_size_from_pixels(desktop_width);
    monitors[0].attributes.physicalHeight = physical_size_from_pixels(desktop_height);
    monitors[0].attributes.orientation = ORIENTATION_LANDSCAPE;
    monitors[0].attributes.desktopScaleFactor = 100;
    monitors[0].attributes.deviceScaleFactor = 100;
    monitors[0].is_primary = TRUE;

    freerdp_settings_set_pointer_len(settings, FreeRDP_MonitorDefArray, monitors, 1);
    freerdp_settings_set_uint32(settings, FreeRDP_MonitorCount, 1);
    free(monitors);

    freerdp_settings_set_bool(settings, FreeRDP_Fullscreen, FALSE);

    PubSub_SubscribeChannelConnected(g_instance->context->pubSub, on_channel_connected);
    PubSub_SubscribeChannelDisconnected(g_instance->context->pubSub, on_channel_disconnected);
    PubSub_SubscribeGraphicsReset(g_instance->context->pubSub, on_graphics_reset);
    printf("[DEBUG] Subscribed to Channel events\n");

    // Connect
    if (!freerdp_connect(g_instance)) {
        fprintf(stderr, "Failed to connect\n");
        freerdp_context_free(g_instance);
        freerdp_free(g_instance);
        g_instance = NULL;
        free(params);
        g_running = false;
        return 1;
    }

    gdi_init(g_instance, PIXEL_FORMAT_BGRA32);

    // Hook callbacks after GDI init as it might override them
    g_instance->context->update->SurfaceBits = on_surface_bits;
    g_instance->context->update->EndPaint = on_end_paint;
    g_instance->context->update->DesktopResize = on_desktop_resize;
    init_graphics_pipeline(g_instance->context);

    free(params);

    while (g_running) {
        if (!freerdp_check_fds(g_instance)) {
            // If check_fds fails, it might be a temporary state or a disconnect
            if (freerdp_get_last_error(g_instance->context) == 0) {
                usleep(10000);
                continue;
            }
            break;
        }

        // Handle channel events
        HANDLE eventHandles[64];
        DWORD eventCount = freerdp_get_event_handles(g_instance->context, eventHandles, 64);
        if (eventCount > 0)
        {
            for (DWORD i = 0; i < eventCount; i++)
            {
                if (WaitForSingleObject(eventHandles[i], 0) == WAIT_OBJECT_0)
                {
                    freerdp_check_event_handles(g_instance->context);
                }
            }
        }

        if (freerdp_shall_disconnect_context(g_instance->context))
            break;

        if (!process_pending_resize())
            break;

        usleep(10000); // 10ms is plenty for RDP
    }


    freerdp_disconnect(g_instance);

    freerdp_context_free(g_instance);
    freerdp_free(g_instance);
    g_instance = NULL;
    g_disp = NULL;
    g_gfx = NULL;
    g_disp_ready = false;
    g_running = false;

    return 0;
}

bool connect_rdp(const char* host, const char* domain, const char* user, const char* password,
                 const char* gateway_host, const char* gateway_domain, const char* gateway_user, const char* gateway_password,
                 int width, int height) {
    if (g_running) return false;
    if (!g_resize_lock_initialized) {
        InitializeCriticalSection(&g_resize_lock);
        g_resize_lock_initialized = true;
    }

    UINT32 initial_width = (UINT32)width;
    UINT32 initial_height = (UINT32)height;
    normalize_resolution(&initial_width, &initial_height);
    g_resize_pending = false;
    g_target_width = initial_width;
    g_target_height = initial_height;
    g_last_resize_tick = 0;
    g_resize_queued_tick = 0;

    connection_params* params = malloc(sizeof(connection_params));
    memset(params, 0, sizeof(connection_params));
    strncpy(params->host, host, 255);
    strncpy(params->domain, domain, 255);
    strncpy(params->user, user, 255);
    strncpy(params->password, password, 255);
    if (gateway_host) strncpy(params->gateway_host, gateway_host, 255);
    if (gateway_domain) strncpy(params->gateway_domain, gateway_domain, 255);
    if (gateway_user) strncpy(params->gateway_user, gateway_user, 255);
    if (gateway_password) strncpy(params->gateway_password, gateway_password, 255);
    params->width = (int)initial_width;
    params->height = (int)initial_height;

    g_running = true;
    g_thread = CreateThread(NULL, 0, rdp_thread_func, params, 0, NULL);

    if (!g_thread) {
        free(params);
        g_running = false;
        return false;
    }

    return true;
}

void disconnect_rdp(void) {
    g_running = false;
    if (g_thread) {
        WaitForSingleObject(g_thread, INFINITE);
        CloseHandle(g_thread);
        g_thread = NULL;
    }
}

void update_resolution(int width, int height) {
    if (!g_resize_lock_initialized || width <= 0 || height <= 0) return;
    queue_resolution_update((UINT32)width, (UINT32)height);
}

void send_mouse_event(uint16_t flags, uint16_t x, uint16_t y) {
    if (g_instance && g_instance->context && g_instance->context->input) {
        freerdp_input_send_mouse_event(g_instance->context->input, flags, x, y);
    }
}

void send_keyboard_event(uint16_t flags, uint16_t code) {
    if (g_instance && g_instance->context && g_instance->context->input) {
        freerdp_input_send_keyboard_event(g_instance->context->input, flags, code);
    }
}
