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
#define CONNECT_TIMEOUT_MS 3000
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

struct rdp_session {
    FrameCallback callback;
    ClipboardTextCallback clipboard_text_callback;
    StatusCallback status_callback;
    freerdp* instance;
    DispClientContext* disp;
    CliprdrClientContext* cliprdr;
    RdpgfxClientContext* gfx;
    HANDLE thread;
    bool running;
    bool disp_ready;
    UINT32 last_sent_width;
    UINT32 last_sent_height;
    ULONGLONG last_resize_tick;
    ULONGLONG resize_queued_tick;
    CRITICAL_SECTION resize_lock;
    bool resize_pending;
    UINT32 target_width;
    UINT32 target_height;
    ULONGLONG perf_last_log_tick;
    ULONGLONG perf_last_frame_tick;
    UINT32 perf_frame_count;
    UINT64 perf_frame_bytes;
    UINT64 perf_frame_gap_total_ms;
    UINT32 perf_frame_gap_max_ms;
    CRITICAL_SECTION input_lock;
    input_event input_queue[INPUT_QUEUE_CAPACITY];
    UINT32 input_queue_count;
    bool pending_mouse_move;
    uint16_t pending_mouse_x;
    uint16_t pending_mouse_y;
    UINT32 input_dropped;
    UINT32 input_mouse_moves_coalesced;
    CRITICAL_SECTION clipboard_lock;
    char* local_clipboard_text;
    bool clipboard_format_pending;
};

typedef struct {
    rdpContext context;
    rdp_session* session;
} wrapper_context;

typedef struct {
    rdp_session* session;
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

static rdp_session* session_from_context(rdpContext* context)
{
    if (!context) return NULL;
    return ((wrapper_context*)context)->session;
}

static void log_channel_rc(const char* operation, UINT rc)
{
    if (rc != CHANNEL_RC_OK)
    {
        fprintf(stderr, "[CLIPRDR] %s failed rc=%u\n", operation, rc);
    }
}

static void queue_input_event(rdp_session* session, input_event event)
{
    if (!session) return;

    EnterCriticalSection(&session->input_lock);
    if (session->input_queue_count < INPUT_QUEUE_CAPACITY)
    {
        session->input_queue[session->input_queue_count++] = event;
    }
    else
    {
        session->input_dropped++;
    }
    LeaveCriticalSection(&session->input_lock);
}

static void process_pending_input(rdp_session* session)
{
    if (!session || !session->instance || !session->instance->context || !session->instance->context->input) return;

    input_event events[INPUT_QUEUE_CAPACITY + 1];
    UINT32 event_count = 0;
    UINT32 dropped = 0;
    UINT32 coalesced = 0;

    EnterCriticalSection(&session->input_lock);
    if (session->pending_mouse_move)
    {
        events[event_count++] = (input_event){
            .type = INPUT_EVENT_MOUSE,
            .flags = PTR_FLAGS_MOVE,
            .x = session->pending_mouse_x,
            .y = session->pending_mouse_y,
        };
        session->pending_mouse_move = false;
    }

    for (UINT32 i = 0; i < session->input_queue_count; i++)
    {
        events[event_count++] = session->input_queue[i];
    }
    session->input_queue_count = 0;

    dropped = session->input_dropped;
    coalesced = session->input_mouse_moves_coalesced;
    session->input_dropped = 0;
    session->input_mouse_moves_coalesced = 0;
    LeaveCriticalSection(&session->input_lock);

    for (UINT32 i = 0; i < event_count; i++)
    {
        input_event* event = &events[i];
        if (event->type == INPUT_EVENT_MOUSE)
        {
            freerdp_input_send_mouse_event(session->instance->context->input, event->flags, event->x, event->y);
        }
        else
        {
            freerdp_input_send_keyboard_event(session->instance->context->input, event->flags, event->code);
        }
    }

    if (dropped > 0 || coalesced > 1000)
    {
        printf("[PERF_INPUT] sent=%u coalescedMouseMoves=%u dropped=%u\n", event_count, coalesced, dropped);
    }
}

static void log_native_frame_stats(rdp_session* session, UINT32 width, UINT32 height, size_t frame_bytes)
{
    if (!session) return;
    ULONGLONG now = GetTickCount64();
    if (session->perf_last_log_tick == 0)
    {
        session->perf_last_log_tick = now;
    }

    if (session->perf_last_frame_tick != 0)
    {
        UINT32 gap = (UINT32)(now - session->perf_last_frame_tick);
        session->perf_frame_gap_total_ms += gap;
        if (gap > session->perf_frame_gap_max_ms) session->perf_frame_gap_max_ms = gap;
    }
    session->perf_last_frame_tick = now;

    session->perf_frame_count++;
    session->perf_frame_bytes += frame_bytes;

    ULONGLONG elapsed = now - session->perf_last_log_tick;
    if (elapsed >= 1000)
    {
        double seconds = elapsed / 1000.0;
        double fps = session->perf_frame_count / seconds;
        double mib_per_sec = (session->perf_frame_bytes / 1048576.0) / seconds;
        double avg_gap = session->perf_frame_count > 1
            ? (double)session->perf_frame_gap_total_ms / (double)(session->perf_frame_count - 1)
            : 0.0;

        printf("[PERF_NATIVE] frames=%.1f/s fullFrame=%.1f MiB/s size=%ux%u avgGap=%.1fms maxGap=%ums\n",
               fps, mib_per_sec, width, height, avg_gap, session->perf_frame_gap_max_ms);

        session->perf_last_log_tick = now;
        session->perf_frame_count = 0;
        session->perf_frame_bytes = 0;
        session->perf_frame_gap_total_ms = 0;
        session->perf_frame_gap_max_ms = 0;
    }
}

static BOOL resize_local_framebuffer(rdpContext* context, UINT32 width, UINT32 height)
{
    if (!context || !context->gdi) return TRUE;
    rdp_session* session = session_from_context(context);

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

    if (session && session->callback && context->gdi->primary_buffer)
    {
        session->callback(session, (uint8_t*)context->gdi->primary_buffer, context->gdi->width, context->gdi->height);
    }

    return TRUE;
}

static void init_graphics_pipeline(rdpContext* context)
{
    rdp_session* session = session_from_context(context);
    if (!context || !context->gdi || !session || !session->gfx) return;

    if (!gdi_graphics_pipeline_init(context->gdi, session->gfx))
    {
        fprintf(stderr, "Failed to initialize RDPGFX GDI pipeline\n");
        return;
    }

    printf("[DEBUG] RDPGFX GDI pipeline initialized\n");
}

static void free_local_clipboard_text(rdp_session* session)
{
    if (session && session->local_clipboard_text)
    {
        free(session->local_clipboard_text);
        session->local_clipboard_text = NULL;
    }
}

static bool has_local_clipboard_text(rdp_session* session)
{
    bool has_text = false;
    if (!session) return false;

    EnterCriticalSection(&session->clipboard_lock);
    has_text = session->local_clipboard_text && session->local_clipboard_text[0] != '\0';
    LeaveCriticalSection(&session->clipboard_lock);
    return has_text;
}

static UINT send_clipboard_format_list(rdp_session* session)
{
    if (!session || !session->cliprdr) return CHANNEL_RC_OK;

    CLIPRDR_FORMAT format;
    memset(&format, 0, sizeof(format));
    format.formatId = CF_UNICODETEXT;

    CLIPRDR_FORMAT_LIST format_list;
    memset(&format_list, 0, sizeof(format_list));
    format_list.common.msgType = CB_FORMAT_LIST;
    format_list.common.msgFlags = 0;
    format_list.numFormats = has_local_clipboard_text(session) ? 1 : 0;
    format_list.formats = format_list.numFormats > 0 ? &format : NULL;

    printf("[CLIPRDR] send local format list unicodeText=%s\n", format_list.numFormats > 0 ? "true" : "false");
    UINT rc = session->cliprdr->ClientFormatList(session->cliprdr, &format_list);
    log_channel_rc("ClientFormatList", rc);
    return rc;
}

static UINT send_clipboard_capabilities(rdp_session* session)
{
    if (!session || !session->cliprdr) return CHANNEL_RC_OK;

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
    UINT rc = session->cliprdr->ClientCapabilities(session->cliprdr, &capabilities);
    log_channel_rc("ClientCapabilities", rc);
    return rc;
}

static UINT send_clipboard_failed_data_response(rdp_session* session, const char* reason)
{
    if (!session || !session->cliprdr) return CHANNEL_RC_OK;

    CLIPRDR_FORMAT_DATA_RESPONSE response;
    memset(&response, 0, sizeof(response));
    response.common.msgType = CB_FORMAT_DATA_RESPONSE;
    response.common.msgFlags = CB_RESPONSE_FAIL;
    response.common.dataLen = 0;
    response.requestedFormatData = NULL;

    UINT rc = session->cliprdr->ClientFormatDataResponse(session->cliprdr, &response);
    log_channel_rc(reason, rc);
    return rc;
}

static UINT send_clipboard_data_response(rdp_session* session, UINT32 requested_format_id)
{
    if (!session || !session->cliprdr) return CHANNEL_RC_OK;

    CLIPRDR_FORMAT_DATA_RESPONSE response;
    memset(&response, 0, sizeof(response));
    response.common.msgType = CB_FORMAT_DATA_RESPONSE;

    if (requested_format_id != CF_UNICODETEXT)
    {
        printf("[CLIPRDR] remote requested unsupported local format=%u\n", requested_format_id);
        return send_clipboard_failed_data_response(session, "ClientFormatDataResponse unsupported");
    }

    EnterCriticalSection(&session->clipboard_lock);
    char* text_copy = session->local_clipboard_text ? strdup(session->local_clipboard_text) : NULL;
    LeaveCriticalSection(&session->clipboard_lock);

    if (!text_copy)
    {
        printf("[CLIPRDR] remote requested local text but cache is empty\n");
        return send_clipboard_failed_data_response(session, "ClientFormatDataResponse empty");
    }

    size_t wchar_len = 0;
    WCHAR* wide_text = ConvertUtf8ToWCharAlloc(text_copy, &wchar_len);
    free(text_copy);

    if (!wide_text)
    {
        fprintf(stderr, "[CLIPRDR] failed to convert local UTF-8 clipboard text to UTF-16\n");
        return send_clipboard_failed_data_response(session, "ClientFormatDataResponse conversion");
    }

    response.common.msgFlags = CB_RESPONSE_OK;
    response.common.dataLen = (UINT32)((wchar_len + 1) * sizeof(WCHAR));
    response.requestedFormatData = (const BYTE*)wide_text;
    printf("[CLIPRDR] send local text response bytes=%u chars=%zu\n", response.common.dataLen, wchar_len);
    UINT rc = session->cliprdr->ClientFormatDataResponse(session->cliprdr, &response);
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
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;
    (void)monitorReady;
    printf("[CLIPRDR] monitor ready\n");
    UINT rc = send_clipboard_capabilities(session);
    if (rc != CHANNEL_RC_OK) return rc;
    return send_clipboard_format_list(session);
}

static UINT on_cliprdr_server_format_list(CliprdrClientContext* context, const CLIPRDR_FORMAT_LIST* formatList)
{
    bool has_unicode_text = false;
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;

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
    if (session && session->cliprdr)
    {
        UINT rc = session->cliprdr->ClientFormatListResponse(session->cliprdr, &list_response);
        if (rc != CHANNEL_RC_OK)
        {
            log_channel_rc("ClientFormatListResponse", rc);
            return rc;
        }
    }

    if (has_unicode_text && session && session->cliprdr)
    {
        CLIPRDR_FORMAT_DATA_REQUEST request;
        memset(&request, 0, sizeof(request));
        request.common.msgType = CB_FORMAT_DATA_REQUEST;
        request.requestedFormatId = CF_UNICODETEXT;
        printf("[CLIPRDR] request remote Unicode text\n");
        UINT rc = session->cliprdr->ClientFormatDataRequest(session->cliprdr, &request);
        log_channel_rc("ClientFormatDataRequest", rc);
        return rc;
    }

    return CHANNEL_RC_OK;
}

static UINT on_cliprdr_server_format_data_request(CliprdrClientContext* context,
                                                  const CLIPRDR_FORMAT_DATA_REQUEST* formatDataRequest)
{
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;
    printf("[CLIPRDR] remote requested local format=%u\n", formatDataRequest->requestedFormatId);
    return send_clipboard_data_response(session, formatDataRequest->requestedFormatId);
}

static UINT on_cliprdr_server_format_data_response(CliprdrClientContext* context,
                                                   const CLIPRDR_FORMAT_DATA_RESPONSE* formatDataResponse)
{
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;
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
    if (session && session->clipboard_text_callback)
    {
        session->clipboard_text_callback(session, text);
    }
    free(text);

    return CHANNEL_RC_OK;
}

static void process_pending_clipboard(rdp_session* session)
{
    if (!session || !session->cliprdr) return;

    bool pending = false;
    EnterCriticalSection(&session->clipboard_lock);
    pending = session->clipboard_format_pending;
    session->clipboard_format_pending = false;
    LeaveCriticalSection(&session->clipboard_lock);

    if (pending)
    {
        send_clipboard_format_list(session);
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

static void queue_resolution_update(rdp_session* session, UINT32 width, UINT32 height)
{
    if (!session) return;
    normalize_resolution(&width, &height);

    EnterCriticalSection(&session->resize_lock);
    if (width != session->target_width || height != session->target_height)
    {
        session->target_width = width;
        session->target_height = height;
        session->resize_pending = true;
        session->resize_queued_tick = GetTickCount64();
    }
    LeaveCriticalSection(&session->resize_lock);
}

static BOOL on_end_paint(rdpContext* context)
{
    rdp_session* session = session_from_context(context);
    if (!session || !session->callback) return TRUE;

    rdpGdi* gdi = context->gdi;
    if (gdi && gdi->primary_buffer) {
        log_native_frame_stats(session, (UINT32)gdi->width, (UINT32)gdi->height, (size_t)gdi->width * (size_t)gdi->height * 4u);
        session->callback(session, (uint8_t*)gdi->primary_buffer, gdi->width, gdi->height);
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
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;
    if (session) session->disp_ready = true;
    printf("[DEBUG] Display Control caps: maxMonitors=%u areaFactor=%ux%u\n",
           maxNumMonitors, maxMonitorAreaFactorA, maxMonitorAreaFactorB);
    return CHANNEL_RC_OK;
}

static void on_channel_connected(void* context, const ChannelConnectedEventArgs* e)
{
    printf("[DEBUG] Channel connected: %s\n", e->name);
    if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
    {
        rdp_session* session = session_from_context((rdpContext*)context);
        if (session) session->disp = (DispClientContext*)e->pInterface;
        if (session) session->disp_ready = false;
        if (session && session->disp)
        {
            session->disp->custom = session;
            session->disp->DisplayControlCaps = on_display_control_caps;
        }
        printf("Display Control channel connected\n");
    }
    else if (strcmp(e->name, "drdynvc") == 0)
    {
        printf("DVC manager connected\n");
    }
    else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
    {
        rdp_session* session = session_from_context((rdpContext*)context);
        if (session) session->cliprdr = (CliprdrClientContext*)e->pInterface;
        if (session && session->cliprdr)
        {
            session->cliprdr->custom = session;
            session->cliprdr->ServerCapabilities = on_cliprdr_server_capabilities;
            session->cliprdr->MonitorReady = on_cliprdr_monitor_ready;
            session->cliprdr->ServerFormatList = on_cliprdr_server_format_list;
            session->cliprdr->ServerFormatDataRequest = on_cliprdr_server_format_data_request;
            session->cliprdr->ServerFormatDataResponse = on_cliprdr_server_format_data_response;
        }
        printf("[CLIPRDR] channel connected\n");
    }
    else if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0)
    {
        rdp_session* session = session_from_context((rdpContext*)context);
        if (session) session->gfx = (RdpgfxClientContext*)e->pInterface;
        if (session && session->gfx) session->gfx->custom = session;
        init_graphics_pipeline((rdpContext*)context);
    }
}

static void on_channel_disconnected(void* context, const ChannelDisconnectedEventArgs* e)
{
    printf("[DEBUG] Channel disconnected: %s\n", e->name);
    rdp_session* session = session_from_context((rdpContext*)context);
    if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
    {
        if (session) session->disp = NULL;
        if (session) session->disp_ready = false;
    }
    else if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0)
    {
        rdpContext* rdp_context = (rdpContext*)context;
        if (rdp_context && rdp_context->gdi && session && session->gfx)
        {
            gdi_graphics_pipeline_uninit(rdp_context->gdi, session->gfx);
        }
        if (session) session->gfx = NULL;
    }
    else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
    {
        if (session) session->cliprdr = NULL;
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

static bool process_pending_resize(rdp_session* session)
{
    if (!session || !session->instance || !session->instance->context || !session->disp || !session->disp_ready) return true;

    UINT32 width = 0;
    UINT32 height = 0;
    ULONGLONG queued_tick = 0;
    EnterCriticalSection(&session->resize_lock);
    bool pending = session->resize_pending;
    if (pending)
    {
        width = session->target_width;
        height = session->target_height;
        queued_tick = session->resize_queued_tick;
    }
    LeaveCriticalSection(&session->resize_lock);

    if (!pending) return true;
    ULONGLONG now = GetTickCount64();
    if (queued_tick != 0 && now - queued_tick < RESIZE_QUIET_DELAY_MS) return true;

    if (width == session->last_sent_width && height == session->last_sent_height)
    {
        EnterCriticalSection(&session->resize_lock);
        if (session->target_width == width && session->target_height == height) session->resize_pending = false;
        LeaveCriticalSection(&session->resize_lock);
        return true;
    }

    if (session->last_resize_tick != 0 && now - session->last_resize_tick < RESIZE_MIN_DELAY_MS) return true;

    rdpSettings* settings = session->instance->context->settings;
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

    UINT rc = session->disp->SendMonitorLayout(session->disp, 1, &layout);
    if (rc != CHANNEL_RC_OK)
    {
        fprintf(stderr, "Failed to send monitor layout update %ux%u, rc=%u\n", width, height, rc);
        return true;
    }

    session->last_sent_width = width;
    session->last_sent_height = height;
    session->last_resize_tick = now;

    if (!resize_local_framebuffer(session->instance->context, width, height))
    {
        return true;
    }

    EnterCriticalSection(&session->resize_lock);
    if (session->target_width == width && session->target_height == height) session->resize_pending = false;
    LeaveCriticalSection(&session->resize_lock);

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

static void cleanup_instance(rdp_session* session)
{
    if (!session || !session->instance) return;

    if (session->instance->context)
    {
        freerdp_context_free(session->instance);
    }

    freerdp_free(session->instance);
    session->instance = NULL;
    session->disp = NULL;
    session->cliprdr = NULL;
    session->gfx = NULL;
    session->disp_ready = false;
}

static bool setup_instance(rdp_session* session, const connection_params* params, bool use_gateway)
{
    session->instance = freerdp_new();
    if (!session->instance) {
        fprintf(stderr, "Failed to create FreeRDP instance\n");
        return false;
    }

    session->instance->LoadChannels = freerdp_client_load_channels;
    session->instance->ContextSize = sizeof(wrapper_context);
    if (!freerdp_context_new(session->instance)) {
        fprintf(stderr, "Failed to create FreeRDP context\n");
        cleanup_instance(session);
        return false;
    }

    ((wrapper_context*)session->instance->context)->session = session;

    rdpSettings* settings = session->instance->context->settings;
    freerdp_settings_set_string(settings, FreeRDP_ServerHostname, params->host);
    freerdp_settings_set_string(settings, FreeRDP_Domain, params->domain);
    freerdp_settings_set_string(settings, FreeRDP_Username, params->user);
    freerdp_settings_set_string(settings, FreeRDP_Password, params->password);

    freerdp_settings_set_uint32(settings, FreeRDP_TcpConnectTimeout, CONNECT_TIMEOUT_MS);
    freerdp_settings_set_uint32(settings, FreeRDP_AutoReconnectMaxRetries, 0);

    if (use_gateway && params->gateway_host[0] != '\0') {
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
    else
    {
        freerdp_settings_set_bool(settings, FreeRDP_GatewayEnabled, FALSE);
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
    session->last_sent_width = desktop_width;
    session->last_sent_height = desktop_height;

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

    PubSub_SubscribeChannelConnected(session->instance->context->pubSub, on_channel_connected);
    PubSub_SubscribeChannelDisconnected(session->instance->context->pubSub, on_channel_disconnected);
    PubSub_SubscribeGraphicsReset(session->instance->context->pubSub, on_graphics_reset);
    printf("[DEBUG] Subscribed to Channel events\n");

    return true;
}

static bool connect_attempt(rdp_session* session, const connection_params* params, bool use_gateway)
{
    if (!setup_instance(session, params, use_gateway))
    {
        return false;
    }

    printf("[DEBUG] Connecting %s gateway...\n", use_gateway ? "with" : "without");
    if (!freerdp_connect(session->instance)) {
        fprintf(stderr, "Failed to connect %s gateway\n", use_gateway ? "with" : "without");
        cleanup_instance(session);
        return false;
    }

    return true;
}

static DWORD WINAPI rdp_thread_func(LPVOID lpParam) {
    connection_params* params = (connection_params*)lpParam;
    rdp_session* session = params->session;

    if (freerdp_register_addin_provider(freerdp_channels_load_static_addin_entry, 0) != CHANNEL_RC_OK)
    {
        fprintf(stderr, "Failed to register FreeRDP static addin provider\n");
        free(params);
        session->running = false;
        if (session->status_callback) session->status_callback(session, 2);
        return 1;
    }

    bool has_gateway = params->gateway_host[0] != '\0';
    bool connected = connect_attempt(session, params, false);

    if (!connected && has_gateway && session->running)
    {
        connected = connect_attempt(session, params, true);
    }

    if (!connected) {
        fprintf(stderr, "Failed to connect\n");
        free(params);
        session->running = false;
        if (session->status_callback) session->status_callback(session, 2);
        return 1;
    }

    if (session->status_callback) session->status_callback(session, 1);

    gdi_init(session->instance, PIXEL_FORMAT_BGRA32);

    // Hook callbacks after GDI init as it might override them
    session->instance->context->update->SurfaceBits = on_surface_bits;
    session->instance->context->update->EndPaint = on_end_paint;
    session->instance->context->update->DesktopResize = on_desktop_resize;
    init_graphics_pipeline(session->instance->context);

    free(params);

    while (session->running) {
        process_pending_input(session);
        process_pending_clipboard(session);

        if (!freerdp_check_fds(session->instance)) {
            // If check_fds fails, it might be a temporary state or a disconnect
            if (freerdp_get_last_error(session->instance->context) == 0) {
                usleep(10000);
                continue;
            }
            break;
        }

        // Handle channel events
        HANDLE eventHandles[64];
        DWORD eventCount = freerdp_get_event_handles(session->instance->context, eventHandles, 64);
        if (eventCount > 0)
        {
            for (DWORD i = 0; i < eventCount; i++)
            {
                if (WaitForSingleObject(eventHandles[i], 0) == WAIT_OBJECT_0)
                {
                    freerdp_check_event_handles(session->instance->context);
                }
            }
        }

        if (freerdp_shall_disconnect_context(session->instance->context))
            break;

        if (!process_pending_resize(session))
            break;

        usleep(2000);
    }


    freerdp_disconnect(session->instance);

    freerdp_context_free(session->instance);
    freerdp_free(session->instance);
    session->instance = NULL;
    session->disp = NULL;
    session->cliprdr = NULL;
    session->gfx = NULL;
    session->disp_ready = false;
    session->running = false;
    if (session->status_callback) session->status_callback(session, 3);

    return 0;
}

rdp_session* rdp_session_connect(const char* host, const char* domain, const char* user, const char* password,
                                 const char* gateway_host, const char* gateway_domain, const char* gateway_user, const char* gateway_password,
                                 int width, int height, FrameCallback frame_callback, ClipboardTextCallback clipboard_callback, StatusCallback status_callback) {
    rdp_session* session = calloc(1, sizeof(rdp_session));
    if (!session) return NULL;

    session->callback = frame_callback;
    session->clipboard_text_callback = clipboard_callback;
    session->status_callback = status_callback;
    InitializeCriticalSection(&session->resize_lock);
    InitializeCriticalSection(&session->input_lock);
    InitializeCriticalSection(&session->clipboard_lock);

    UINT32 initial_width = (UINT32)width;
    UINT32 initial_height = (UINT32)height;
    normalize_resolution(&initial_width, &initial_height);
    session->target_width = initial_width;
    session->target_height = initial_height;

    connection_params* params = malloc(sizeof(connection_params));
    if (!params)
    {
        rdp_session_free(session);
        return NULL;
    }
    memset(params, 0, sizeof(connection_params));
    params->session = session;
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

    session->running = true;
    session->thread = CreateThread(NULL, 0, rdp_thread_func, params, 0, NULL);

    if (!session->thread) {
        free(params);
        session->running = false;
        rdp_session_free(session);
        return NULL;
    }

    return session;
}

void rdp_session_disconnect(rdp_session* session) {
    if (!session) return;
    session->running = false;

    if (session->instance && session->instance->context)
    {
        freerdp_abort_connect_context(session->instance->context);
    }

    if (session->thread) {
        WaitForSingleObject(session->thread, INFINITE);
        CloseHandle(session->thread);
        session->thread = NULL;
    }
}

void rdp_session_free(rdp_session* session) {
    if (!session) return;
    rdp_session_disconnect(session);
    DeleteCriticalSection(&session->resize_lock);
    DeleteCriticalSection(&session->input_lock);
    DeleteCriticalSection(&session->clipboard_lock);
    free_local_clipboard_text(session);
    free(session);
}

void rdp_session_update_resolution(rdp_session* session, int width, int height) {
    if (!session || width <= 0 || height <= 0) return;
    queue_resolution_update(session, (UINT32)width, (UINT32)height);
}

void rdp_session_send_mouse_event(rdp_session* session, uint16_t flags, uint16_t x, uint16_t y) {
    if (!session) return;
    if (flags == PTR_FLAGS_MOVE)
    {
        EnterCriticalSection(&session->input_lock);
        if (session->pending_mouse_move) session->input_mouse_moves_coalesced++;
        session->pending_mouse_move = true;
        session->pending_mouse_x = x;
        session->pending_mouse_y = y;
        LeaveCriticalSection(&session->input_lock);
        return;
    }

    queue_input_event(session, (input_event){
        .type = INPUT_EVENT_MOUSE,
        .flags = flags,
        .x = x,
        .y = y,
    });
}

void rdp_session_send_keyboard_event(rdp_session* session, uint16_t flags, uint16_t code) {
    queue_input_event(session, (input_event){
        .type = INPUT_EVENT_KEYBOARD,
        .flags = flags,
        .code = code,
    });
}

void rdp_session_clipboard_set_local_text(rdp_session* session, const char* text) {
    if (!session) return;

    EnterCriticalSection(&session->clipboard_lock);
    free_local_clipboard_text(session);
    if (text && text[0] != '\0')
    {
        session->local_clipboard_text = strdup(text);
    }
    session->clipboard_format_pending = true;
    size_t text_len = session->local_clipboard_text ? strlen(session->local_clipboard_text) : 0;
    LeaveCriticalSection(&session->clipboard_lock);

    printf("[CLIPRDR] local text changed chars=%zu\n", text_len);
}
