#include "freerdp_wrapper.h"

#include <freerdp/freerdp.h>
#include <freerdp/addin.h>
#include <freerdp/client.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/cliprdr.h>
#include <freerdp/client/disp.h>
#include <freerdp/client/rdpgfx.h>
#include <freerdp/channels/cliprdr.h>
#include <freerdp/channels/channels.h>
#include <freerdp/channels/rdpgfx.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/gdi/gfx.h>
#include <freerdp/settings.h>
#include <freerdp/update.h>
#include <winpr/sysinfo.h>
#include <winpr/stream.h>
#include <winpr/string.h>
#include <winpr/synch.h>
#include <winpr/user.h>
#include <winpr/wtypes.h>
#include <winpr/thread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define RESIZE_QUIET_DELAY_MS 1000
#define RESIZE_MIN_DELAY_MS 1500
#define INPUT_QUEUE_CAPACITY 256
#define PTR_FLAGS_MOVE 0x0800

typedef enum {
    INPUT_EVENT_MOUSE,
    INPUT_EVENT_KEYBOARD
} input_event_type;

typedef struct {
    input_event_type type;
    uint16_t flags;
    uint16_t x;
    uint16_t y;
    uint16_t code;
} input_event;

static FrameCallback g_callback = NULL;
static ClipboardTextCallback g_clipboard_text_callback = NULL;
static freerdp* g_instance = NULL;
static DispClientContext* g_disp = NULL;
static CliprdrClientContext* g_cliprdr = NULL;
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
static ULONGLONG g_perf_last_log_tick = 0;
static ULONGLONG g_perf_last_frame_tick = 0;
static UINT32 g_perf_frame_count = 0;
static UINT64 g_perf_frame_bytes = 0;
static UINT64 g_perf_frame_gap_total_ms = 0;
static UINT32 g_perf_frame_gap_max_ms = 0;
static CRITICAL_SECTION g_input_lock;
static bool g_input_lock_initialized = false;
static input_event g_input_queue[INPUT_QUEUE_CAPACITY];
static UINT32 g_input_queue_count = 0;
static bool g_pending_mouse_move = false;
static uint16_t g_pending_mouse_x = 0;
static uint16_t g_pending_mouse_y = 0;
static UINT32 g_input_dropped = 0;
static UINT32 g_input_mouse_moves_coalesced = 0;
static CRITICAL_SECTION g_clipboard_lock;
static bool g_clipboard_lock_initialized = false;
static char* g_local_clipboard_text = NULL;
static bool g_clipboard_format_pending = false;

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

static void log_channel_rc(const char* operation, UINT rc)
{
    if (rc != CHANNEL_RC_OK)
    {
        fprintf(stderr, "[CLIPRDR] %s failed rc=%u\n", operation, rc);
    }
}

static void queue_input_event(input_event event)
{
    if (!g_input_lock_initialized) return;

    EnterCriticalSection(&g_input_lock);
    if (g_input_queue_count < INPUT_QUEUE_CAPACITY)
    {
        g_input_queue[g_input_queue_count++] = event;
    }
    else
    {
        g_input_dropped++;
    }
    LeaveCriticalSection(&g_input_lock);
}

static void process_pending_input(void)
{
    if (!g_instance || !g_instance->context || !g_instance->context->input || !g_input_lock_initialized) return;

    input_event events[INPUT_QUEUE_CAPACITY + 1];
    UINT32 event_count = 0;
    UINT32 dropped = 0;
    UINT32 coalesced = 0;

    EnterCriticalSection(&g_input_lock);
    if (g_pending_mouse_move)
    {
        events[event_count++] = (input_event){
            .type = INPUT_EVENT_MOUSE,
            .flags = PTR_FLAGS_MOVE,
            .x = g_pending_mouse_x,
            .y = g_pending_mouse_y,
        };
        g_pending_mouse_move = false;
    }

    for (UINT32 i = 0; i < g_input_queue_count; i++)
    {
        events[event_count++] = g_input_queue[i];
    }
    g_input_queue_count = 0;

    dropped = g_input_dropped;
    coalesced = g_input_mouse_moves_coalesced;
    g_input_dropped = 0;
    g_input_mouse_moves_coalesced = 0;
    LeaveCriticalSection(&g_input_lock);

    for (UINT32 i = 0; i < event_count; i++)
    {
        input_event* event = &events[i];
        if (event->type == INPUT_EVENT_MOUSE)
        {
            freerdp_input_send_mouse_event(g_instance->context->input, event->flags, event->x, event->y);
        }
        else
        {
            freerdp_input_send_keyboard_event(g_instance->context->input, event->flags, event->code);
        }
    }

    if (dropped > 0 || coalesced > 1000)
    {
        printf("[PERF_INPUT] sent=%u coalescedMouseMoves=%u dropped=%u\n", event_count, coalesced, dropped);
    }
}

static void log_native_frame_stats(UINT32 width, UINT32 height, size_t frame_bytes)
{
    ULONGLONG now = GetTickCount64();
    if (g_perf_last_log_tick == 0)
    {
        g_perf_last_log_tick = now;
    }

    if (g_perf_last_frame_tick != 0)
    {
        UINT32 gap = (UINT32)(now - g_perf_last_frame_tick);
        g_perf_frame_gap_total_ms += gap;
        if (gap > g_perf_frame_gap_max_ms) g_perf_frame_gap_max_ms = gap;
    }
    g_perf_last_frame_tick = now;

    g_perf_frame_count++;
    g_perf_frame_bytes += frame_bytes;

    ULONGLONG elapsed = now - g_perf_last_log_tick;
    if (elapsed >= 1000)
    {
        double seconds = elapsed / 1000.0;
        double fps = g_perf_frame_count / seconds;
        double mib_per_sec = (g_perf_frame_bytes / 1048576.0) / seconds;
        double avg_gap = g_perf_frame_count > 1
            ? (double)g_perf_frame_gap_total_ms / (double)(g_perf_frame_count - 1)
            : 0.0;

        printf("[PERF_NATIVE] frames=%.1f/s fullFrame=%.1f MiB/s size=%ux%u avgGap=%.1fms maxGap=%ums\n",
               fps, mib_per_sec, width, height, avg_gap, g_perf_frame_gap_max_ms);

        g_perf_last_log_tick = now;
        g_perf_frame_count = 0;
        g_perf_frame_bytes = 0;
        g_perf_frame_gap_total_ms = 0;
        g_perf_frame_gap_max_ms = 0;
    }
}

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

void set_clipboard_text_callback(ClipboardTextCallback cb) {
    g_clipboard_text_callback = cb;
}

static void free_local_clipboard_text(void)
{
    if (g_local_clipboard_text)
    {
        free(g_local_clipboard_text);
        g_local_clipboard_text = NULL;
    }
}

static bool has_local_clipboard_text(void)
{
    bool has_text = false;
    if (!g_clipboard_lock_initialized) return false;

    EnterCriticalSection(&g_clipboard_lock);
    has_text = g_local_clipboard_text && g_local_clipboard_text[0] != '\0';
    LeaveCriticalSection(&g_clipboard_lock);
    return has_text;
}

static UINT send_clipboard_format_list(void)
{
    if (!g_cliprdr) return CHANNEL_RC_OK;

    CLIPRDR_FORMAT format;
    memset(&format, 0, sizeof(format));
    format.formatId = CF_UNICODETEXT;

    CLIPRDR_FORMAT_LIST format_list;
    memset(&format_list, 0, sizeof(format_list));
    format_list.common.msgType = CB_FORMAT_LIST;
    format_list.common.msgFlags = 0;
    format_list.numFormats = has_local_clipboard_text() ? 1 : 0;
    format_list.formats = format_list.numFormats > 0 ? &format : NULL;

    printf("[CLIPRDR] send local format list unicodeText=%s\n", format_list.numFormats > 0 ? "true" : "false");
    UINT rc = g_cliprdr->ClientFormatList(g_cliprdr, &format_list);
    log_channel_rc("ClientFormatList", rc);
    return rc;
}

static UINT send_clipboard_capabilities(void)
{
    if (!g_cliprdr) return CHANNEL_RC_OK;

    CLIPRDR_GENERAL_CAPABILITY_SET general_capability;
    memset(&general_capability, 0, sizeof(general_capability));
    general_capability.capabilitySetType = CB_CAPSTYPE_GENERAL;
    general_capability.capabilitySetLength = CB_CAPSTYPE_GENERAL_LEN;
    general_capability.version = CB_CAPS_VERSION_2;
    general_capability.generalFlags = CB_USE_LONG_FORMAT_NAMES;

    CLIPRDR_CAPABILITIES capabilities;
    memset(&capabilities, 0, sizeof(capabilities));
    capabilities.common.msgType = CB_CLIP_CAPS;
    capabilities.cCapabilitiesSets = 1;
    capabilities.capabilitySets = (CLIPRDR_CAPABILITY_SET*)&general_capability;

    printf("[CLIPRDR] send client capabilities\n");
    UINT rc = g_cliprdr->ClientCapabilities(g_cliprdr, &capabilities);
    log_channel_rc("ClientCapabilities", rc);
    return rc;
}

static UINT send_clipboard_failed_data_response(const char* reason)
{
    if (!g_cliprdr) return CHANNEL_RC_OK;

    CLIPRDR_FORMAT_DATA_RESPONSE response;
    memset(&response, 0, sizeof(response));
    response.common.msgType = CB_FORMAT_DATA_RESPONSE;
    response.common.msgFlags = CB_RESPONSE_FAIL;
    response.common.dataLen = 0;
    response.requestedFormatData = NULL;

    UINT rc = g_cliprdr->ClientFormatDataResponse(g_cliprdr, &response);
    log_channel_rc(reason, rc);
    return rc;
}

static UINT send_clipboard_data_response(UINT32 requested_format_id)
{
    if (!g_cliprdr) return CHANNEL_RC_OK;

    CLIPRDR_FORMAT_DATA_RESPONSE response;
    memset(&response, 0, sizeof(response));
    response.common.msgType = CB_FORMAT_DATA_RESPONSE;

    if (requested_format_id != CF_UNICODETEXT || !g_clipboard_lock_initialized)
    {
        printf("[CLIPRDR] remote requested unsupported local format=%u\n", requested_format_id);
        return send_clipboard_failed_data_response("ClientFormatDataResponse unsupported");
    }

    EnterCriticalSection(&g_clipboard_lock);
    char* text_copy = g_local_clipboard_text ? strdup(g_local_clipboard_text) : NULL;
    LeaveCriticalSection(&g_clipboard_lock);

    if (!text_copy)
    {
        printf("[CLIPRDR] remote requested local text but cache is empty\n");
        return send_clipboard_failed_data_response("ClientFormatDataResponse empty");
    }

    size_t wchar_len = 0;
    WCHAR* wide_text = ConvertUtf8ToWCharAlloc(text_copy, &wchar_len);
    free(text_copy);

    if (!wide_text)
    {
        fprintf(stderr, "[CLIPRDR] failed to convert local UTF-8 clipboard text to UTF-16\n");
        return send_clipboard_failed_data_response("ClientFormatDataResponse conversion");
    }

    response.common.msgFlags = CB_RESPONSE_OK;
    response.common.dataLen = (UINT32)((wchar_len + 1) * sizeof(WCHAR));
    response.requestedFormatData = (const BYTE*)wide_text;
    printf("[CLIPRDR] send local text response bytes=%u chars=%zu\n", response.common.dataLen, wchar_len);
    UINT rc = g_cliprdr->ClientFormatDataResponse(g_cliprdr, &response);
    log_channel_rc("ClientFormatDataResponse text", rc);
    free(wide_text);
    return rc;
}

static UINT on_cliprdr_server_capabilities(CliprdrClientContext* context,
                                           const CLIPRDR_CAPABILITIES* capabilities)
{
    (void)context;
    printf("[CLIPRDR] server capabilities sets=%u\n", capabilities ? capabilities->cCapabilitiesSets : 0);
    return CHANNEL_RC_OK;
}

static UINT on_cliprdr_monitor_ready(CliprdrClientContext* context, const CLIPRDR_MONITOR_READY* monitorReady)
{
    (void)context;
    (void)monitorReady;
    printf("[CLIPRDR] monitor ready\n");
    UINT rc = send_clipboard_capabilities();
    if (rc != CHANNEL_RC_OK) return rc;
    return send_clipboard_format_list();
}

static UINT on_cliprdr_server_format_list(CliprdrClientContext* context, const CLIPRDR_FORMAT_LIST* formatList)
{
    bool has_unicode_text = false;
    (void)context;

    for (UINT32 i = 0; i < formatList->numFormats; i++)
    {
        if (formatList->formats[i].formatId == CF_UNICODETEXT)
        {
            has_unicode_text = true;
            break;
        }
    }

    printf("[CLIPRDR] server formats count=%u unicodeText=%s\n",
           formatList->numFormats, has_unicode_text ? "true" : "false");

    CLIPRDR_FORMAT_LIST_RESPONSE list_response;
    memset(&list_response, 0, sizeof(list_response));
    list_response.common.msgType = CB_FORMAT_LIST_RESPONSE;
    list_response.common.msgFlags = CB_RESPONSE_OK;
    if (g_cliprdr)
    {
        UINT rc = g_cliprdr->ClientFormatListResponse(g_cliprdr, &list_response);
        if (rc != CHANNEL_RC_OK)
        {
            log_channel_rc("ClientFormatListResponse", rc);
            return rc;
        }
    }

    if (has_unicode_text && g_cliprdr)
    {
        CLIPRDR_FORMAT_DATA_REQUEST request;
        memset(&request, 0, sizeof(request));
        request.common.msgType = CB_FORMAT_DATA_REQUEST;
        request.requestedFormatId = CF_UNICODETEXT;
        printf("[CLIPRDR] request remote Unicode text\n");
        UINT rc = g_cliprdr->ClientFormatDataRequest(g_cliprdr, &request);
        log_channel_rc("ClientFormatDataRequest", rc);
        return rc;
    }

    return CHANNEL_RC_OK;
}

static UINT on_cliprdr_server_format_data_request(CliprdrClientContext* context,
                                                  const CLIPRDR_FORMAT_DATA_REQUEST* formatDataRequest)
{
    (void)context;
    printf("[CLIPRDR] remote requested local format=%u\n", formatDataRequest->requestedFormatId);
    return send_clipboard_data_response(formatDataRequest->requestedFormatId);
}

static UINT on_cliprdr_server_format_data_response(CliprdrClientContext* context,
                                                   const CLIPRDR_FORMAT_DATA_RESPONSE* formatDataResponse)
{
    (void)context;
    if (!(formatDataResponse->common.msgFlags & CB_RESPONSE_OK) || !formatDataResponse->requestedFormatData)
    {
        printf("[CLIPRDR] remote text response failed flags=0x%04x\n", formatDataResponse->common.msgFlags);
        return CHANNEL_RC_OK;
    }

    size_t wchar_len = formatDataResponse->common.dataLen / sizeof(WCHAR);
    if (wchar_len == 0)
    {
        return CHANNEL_RC_OK;
    }

    size_t utf8_len = 0;
    char* text = ConvertWCharNToUtf8Alloc((const WCHAR*)formatDataResponse->requestedFormatData, wchar_len, &utf8_len);
    if (!text)
    {
        fprintf(stderr, "[CLIPRDR] failed to convert remote UTF-16 clipboard text to UTF-8\n");
        return CHANNEL_RC_OK;
    }

    printf("[CLIPRDR] remote text received bytes=%u chars=%zu\n", formatDataResponse->common.dataLen, utf8_len);
    if (g_clipboard_text_callback)
    {
        g_clipboard_text_callback(text);
    }
    free(text);

    return CHANNEL_RC_OK;
}

static void process_pending_clipboard(void)
{
    if (!g_cliprdr || !g_clipboard_lock_initialized) return;

    bool pending = false;
    EnterCriticalSection(&g_clipboard_lock);
    pending = g_clipboard_format_pending;
    g_clipboard_format_pending = false;
    LeaveCriticalSection(&g_clipboard_lock);

    if (pending)
    {
        send_clipboard_format_list();
    }
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
        log_native_frame_stats((UINT32)gdi->width, (UINT32)gdi->height, (size_t)gdi->width * (size_t)gdi->height * 4u);
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
    else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
    {
        g_cliprdr = (CliprdrClientContext*)e->pInterface;
        if (g_cliprdr)
        {
            g_cliprdr->ServerCapabilities = on_cliprdr_server_capabilities;
            g_cliprdr->MonitorReady = on_cliprdr_monitor_ready;
            g_cliprdr->ServerFormatList = on_cliprdr_server_format_list;
            g_cliprdr->ServerFormatDataRequest = on_cliprdr_server_format_data_request;
            g_cliprdr->ServerFormatDataResponse = on_cliprdr_server_format_data_response;
        }
        printf("[CLIPRDR] channel connected\n");
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
    else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
    {
        g_cliprdr = NULL;
        printf("[CLIPRDR] channel disconnected\n");
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
    freerdp_settings_set_bool(settings, FreeRDP_RedirectClipboard, TRUE);
    freerdp_settings_set_uint32(settings, FreeRDP_ClipboardFeatureMask,
                                CLIPRDR_FLAG_LOCAL_TO_REMOTE | CLIPRDR_FLAG_REMOTE_TO_LOCAL);
    freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_MstscCookieMode, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_AudioPlayback, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_DeviceRedirection, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_CompressionEnabled, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_BitmapCacheEnabled, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_BitmapCachePersistEnabled, FALSE);
    freerdp_settings_set_uint32(settings, FreeRDP_ConnectionType, CONNECTION_TYPE_WAN);
    freerdp_settings_set_uint32(settings, FreeRDP_PerformanceFlags,
                                PERF_DISABLE_WALLPAPER |
                                PERF_DISABLE_FULLWINDOWDRAG |
                                PERF_DISABLE_MENUANIMATIONS |
                                PERF_DISABLE_THEMING |
                                PERF_DISABLE_CURSOR_SHADOW |
                                PERF_DISABLE_CURSORSETTINGS);
    freerdp_settings_set_bool(settings, FreeRDP_DisableWallpaper, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableThemes, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_AllowFontSmoothing, FALSE);

    printf("[DEBUG] Channels set up, connecting...\n");
    freerdp_settings_set_string(settings, FreeRDP_ClientHostname, "AvaloniaRDP");

    freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, 16);
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
        process_pending_input();
        process_pending_clipboard();

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

        usleep(2000);
    }


    freerdp_disconnect(g_instance);

    freerdp_context_free(g_instance);
    freerdp_free(g_instance);
    g_instance = NULL;
    g_disp = NULL;
    g_cliprdr = NULL;
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
    if (!g_input_lock_initialized) {
        InitializeCriticalSection(&g_input_lock);
        g_input_lock_initialized = true;
    }
    if (!g_clipboard_lock_initialized) {
        InitializeCriticalSection(&g_clipboard_lock);
        g_clipboard_lock_initialized = true;
    }

    UINT32 initial_width = (UINT32)width;
    UINT32 initial_height = (UINT32)height;
    normalize_resolution(&initial_width, &initial_height);
    g_resize_pending = false;
    g_target_width = initial_width;
    g_target_height = initial_height;
    g_last_resize_tick = 0;
    g_resize_queued_tick = 0;

    EnterCriticalSection(&g_input_lock);
    g_input_queue_count = 0;
    g_pending_mouse_move = false;
    g_input_dropped = 0;
    g_input_mouse_moves_coalesced = 0;
    LeaveCriticalSection(&g_input_lock);

    EnterCriticalSection(&g_clipboard_lock);
    g_clipboard_format_pending = false;
    LeaveCriticalSection(&g_clipboard_lock);

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
    if (!g_input_lock_initialized) return;

    if (flags == PTR_FLAGS_MOVE)
    {
        EnterCriticalSection(&g_input_lock);
        if (g_pending_mouse_move) g_input_mouse_moves_coalesced++;
        g_pending_mouse_move = true;
        g_pending_mouse_x = x;
        g_pending_mouse_y = y;
        LeaveCriticalSection(&g_input_lock);
        return;
    }

    queue_input_event((input_event){
        .type = INPUT_EVENT_MOUSE,
        .flags = flags,
        .x = x,
        .y = y,
    });
}

void send_keyboard_event(uint16_t flags, uint16_t code) {
    queue_input_event((input_event){
        .type = INPUT_EVENT_KEYBOARD,
        .flags = flags,
        .code = code,
    });
}

void clipboard_set_local_text(const char* text) {
    if (!g_clipboard_lock_initialized) return;

    EnterCriticalSection(&g_clipboard_lock);
    free_local_clipboard_text();
    if (text && text[0] != '\0')
    {
        g_local_clipboard_text = strdup(text);
    }
    g_clipboard_format_pending = true;
    size_t text_len = g_local_clipboard_text ? strlen(g_local_clipboard_text) : 0;
    LeaveCriticalSection(&g_clipboard_lock);

    printf("[CLIPRDR] local text changed chars=%zu\n", text_len);
}
