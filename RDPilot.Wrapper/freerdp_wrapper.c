#include "freerdp_wrapper_internal.h"

static BOOL on_surface_bits(rdpContext* context, const SURFACE_BITS_COMMAND* cmd);
static BOOL on_end_paint(rdpContext* context);
static BOOL on_desktop_resize(rdpContext* context);
static bool is_graphics_pipeline_mode(rendering_mode mode);

rdp_session* session_from_context(rdpContext* context)
{
    if (!context) return NULL;
    return ((wrapper_context*)context)->session;
}

static void emit_status(rdp_session* session, int status, const connection_error* error)
{
    if (!session || !session->status_callback) return;
    session->status_callback(
        session,
        status,
        error ? error->code : 0,
        error ? error->name : NULL,
        error ? error->message : NULL);
}

static void capture_last_error(rdpContext* context, connection_error* error)
{
    if (!error) return;
    memset(error, 0, sizeof(connection_error));

    if (!context) return;

    error->code = freerdp_get_last_error(context);
    if (error->code != 0)
    {
        error->name = freerdp_get_last_error_name(error->code);
        error->message = freerdp_get_last_error_string(error->code);
    }
}

void log_channel_rc(const char* operation, UINT rc)
{
    if (rc != CHANNEL_RC_OK)
    {
        fprintf(stderr, "[CLIPRDR] %s failed rc=%u\n", operation, rc);
    }
}

static void log_native_frame_stats(rdp_session* session, UINT32 width, UINT32 height, size_t frame_bytes)
{
    (void)width;
    (void)height;
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
        session->perf_last_log_tick = now;
        session->perf_frame_count = 0;
        session->perf_frame_bytes = 0;
        session->perf_frame_gap_total_ms = 0;
        session->perf_frame_gap_max_ms = 0;
    }
}

static void notify_pending_frame(rdp_session* session, rdpGdi* gdi, INT32 dirty_x, INT32 dirty_y, INT32 dirty_width, INT32 dirty_height)
{
    if (!session || !session->callback || !gdi || !gdi->primary_buffer) return;
    if (gdi->width <= 0 || gdi->height <= 0) return;

    if (dirty_width <= 0 || dirty_height <= 0)
    {
        dirty_x = 0;
        dirty_y = 0;
        dirty_width = gdi->width;
        dirty_height = gdi->height;
    }

    if (dirty_x < 0)
    {
        dirty_width += dirty_x;
        dirty_x = 0;
    }
    if (dirty_y < 0)
    {
        dirty_height += dirty_y;
        dirty_y = 0;
    }
    if (dirty_x >= gdi->width || dirty_y >= gdi->height || dirty_width <= 0 || dirty_height <= 0) return;
    if (dirty_x + dirty_width > gdi->width) dirty_width = gdi->width - dirty_x;
    if (dirty_y + dirty_height > gdi->height) dirty_height = gdi->height - dirty_y;

    log_native_frame_stats(session, (UINT32)gdi->width, (UINT32)gdi->height, (size_t)dirty_width * (size_t)dirty_height * 4u);

    EnterCriticalSection(&session->frame_lock);
    if (session->pending_frame)
    {
        if (dirty_x < session->pending_dirty_x)
        {
            session->pending_dirty_w += session->pending_dirty_x - dirty_x;
            session->pending_dirty_x = dirty_x;
        }
        if (dirty_y < session->pending_dirty_y)
        {
            session->pending_dirty_h += session->pending_dirty_y - dirty_y;
            session->pending_dirty_y = dirty_y;
        }
        UINT32 right = (UINT32)session->pending_dirty_x + (UINT32)session->pending_dirty_w;
        UINT32 bottom = (UINT32)session->pending_dirty_y + (UINT32)session->pending_dirty_h;
        UINT32 new_right = (UINT32)dirty_x + (UINT32)dirty_width;
        UINT32 new_bottom = (UINT32)dirty_y + (UINT32)dirty_height;
        if (new_right > right) session->pending_dirty_w = (INT32)(new_right - (UINT32)session->pending_dirty_x);
        if (new_bottom > bottom) session->pending_dirty_h = (INT32)(new_bottom - (UINT32)session->pending_dirty_y);
    }
    else
    {
        session->pending_dirty_x = dirty_x;
        session->pending_dirty_y = dirty_y;
        session->pending_dirty_w = dirty_width;
        session->pending_dirty_h = dirty_height;
        session->pending_frame = 1;
    }
    LeaveCriticalSection(&session->frame_lock);

    INT32 source_stride = gdi->width * 4;
    BYTE* data = (BYTE*)gdi->primary_buffer + (size_t)dirty_y * (size_t)source_stride + (size_t)dirty_x * 4u;
    session->callback(session, data, gdi->width, gdi->height, dirty_x, dirty_y, dirty_width, dirty_height, source_stride);
}

bool rdp_session_present(rdp_session* session, uint8_t* dest, int dest_stride, int dest_width, int dest_height,
    int* out_dx, int* out_dy, int* out_dw, int* out_dh, int* out_width, int* out_height)
{
    if (!out_dx || !out_dy || !out_dw || !out_dh || !out_width || !out_height) return false;
    *out_dx = *out_dy = *out_dw = *out_dh = 0;
    *out_width = *out_height = 0;

    if (!session || !session->instance || !session->instance->context) return false;
    rdpGdi* gdi = session->instance->context->gdi;
    if (!gdi || !gdi->primary_buffer)
    {
        *out_width = 0;
        *out_height = 0;
        return false;
    }

    EnterCriticalSection(&session->frame_lock);

    if (!session->pending_frame)
    {
        *out_width = gdi->width;
        *out_height = gdi->height;
        LeaveCriticalSection(&session->frame_lock);
        return false;
    }

    if (gdi->width != dest_width || gdi->height != dest_height)
    {
        *out_width = gdi->width;
        *out_height = gdi->height;
        LeaveCriticalSection(&session->frame_lock);
        return false;
    }

    session->pending_frame = 0;
    INT32 dx = session->pending_dirty_x;
    INT32 dy = session->pending_dirty_y;
    INT32 dw = session->pending_dirty_w;
    INT32 dh = session->pending_dirty_h;

    if (dx < 0) { dw += dx; dx = 0; }
    if (dy < 0) { dh += dy; dy = 0; }
    if (dx >= gdi->width || dy >= gdi->height || dw <= 0 || dh <= 0)
    {
        *out_width = gdi->width;
        *out_height = gdi->height;
        LeaveCriticalSection(&session->frame_lock);
        return false;
    }
    if (dx + dw > gdi->width) dw = gdi->width - dx;
    if (dy + dh > gdi->height) dh = gdi->height - dy;

    INT32 src_stride = gdi->width * 4;
    BYTE* src = (BYTE*)gdi->primary_buffer + (size_t)dy * (size_t)src_stride + (size_t)dx * 4u;
    BYTE* dst = dest + (size_t)dy * (size_t)dest_stride + (size_t)dx * 4u;
    INT32 row_bytes = dw * 4;
    for (INT32 row = 0; row < dh; row++)
    {
        memcpy(dst + (size_t)row * (size_t)dest_stride, src + (size_t)row * (size_t)src_stride, (size_t)row_bytes);
    }

    *out_dx = (int)dx;
    *out_dy = (int)dy;
    *out_dw = (int)dw;
    *out_dh = (int)dh;
    *out_width = gdi->width;
    *out_height = gdi->height;
    LeaveCriticalSection(&session->frame_lock);
    return true;
}

// Resets the pending present slot to "full screen dirty" under frame_lock. Used after the GDI
// primary buffer is rebuilt (resize/graphics reset/disconnect) so the next present repaints the
// whole desktop instead of a stale dirty rect.
static void reset_pending_dirty_fullscreen(rdp_session* session, rdpGdi* gdi)
{
    if (!session || !gdi) return;
    EnterCriticalSection(&session->frame_lock);
    session->pending_dirty_x = 0;
    session->pending_dirty_y = 0;
    session->pending_dirty_w = gdi->width;
    session->pending_dirty_h = gdi->height;
    session->pending_frame = 1;
    LeaveCriticalSection(&session->frame_lock);
}

static BOOL resize_local_framebuffer(rdpContext* context, UINT32 width, UINT32 height)
{
    if (!context || !context->gdi) return TRUE;
    rdp_session* session = session_from_context(context);

    if (context->gdi->width == (INT32)width && context->gdi->height == (INT32)height) return TRUE;

    printf("[DEBUG] Local framebuffer resize: %ux%u\n", width, height);

    // Hold frame_lock across gdi_resize so the UI/present thread cannot be mid-copy when the
    // primary buffer is freed/reallocated. The RDP decode loop's surface blits run outside this
    // lock (tearing is benign); only the buffer lifetime is protected here.
    EnterCriticalSection(&session->frame_lock);

    if (!gdi_resize(context->gdi, width, height))
    {
        LeaveCriticalSection(&session->frame_lock);
        fprintf(stderr, "Failed to resize local GDI framebuffer to %ux%u\n", width, height);
        return FALSE;
    }

    context->update->SurfaceBits = on_surface_bits;
    context->update->EndPaint = on_end_paint;
    context->update->DesktopResize = on_desktop_resize;

    reset_pending_dirty_fullscreen(session, context->gdi);
    BYTE* primary = context->gdi->primary_buffer;
    INT32 gdi_w = context->gdi->width;
    INT32 gdi_h = context->gdi->height;
    LeaveCriticalSection(&session->frame_lock);

    if (session && session->callback && primary)
    {
        INT32 stride = gdi_w * 4;
        session->callback(session, (uint8_t*)primary, gdi_w, gdi_h, 0, 0, gdi_w, gdi_h, stride);
    }

    return TRUE;
}

static void init_graphics_pipeline(rdpContext* context)
{
    rdp_session* session = session_from_context(context);
    if (!context || !context->gdi || !session || !session->gfx || !is_graphics_pipeline_mode(session->render_mode)) return;

    if (!gdi_graphics_pipeline_init(context->gdi, session->gfx))
    {
        fprintf(stderr, "Failed to initialize RDPGFX GDI pipeline\n");
        return;
    }

    printf("[DEBUG] RDPGFX GDI pipeline initialized\n");
}

static UINT32 clamp_uint32(UINT32 value, UINT32 min, UINT32 max)
{
    if (value < min) return min;
    if (value > max) return max;
    return value;
}

// Windows RDP server only accepts three values for desktopScaleFactor/deviceScaleFactor:
// 100 (96 DPI), 140 (134 DPI), 180 (173 DPI). Clamp the host's reported percentage to the
// nearest valid step so the server honours it. Sending 200 or 125 silently falls back to 100.
static UINT32 rdp_clamp_scale_percent(uint32_t pct)
{
    if (pct >= 150) return 180;
    if (pct >= 125) return 140;
    return 100;
}

static UINT32 dpi_from_scale_percent(uint32_t pct)
{
    return 96u * rdp_clamp_scale_percent(pct) / 100u;
}

static UINT32 physical_size_mm_with_dpi(UINT32 pixels, UINT32 dpi)
{
    if (dpi == 0) dpi = 96;
    UINT32 mm = (pixels * 254u + dpi * 5u) / (dpi * 10u);
    return clamp_uint32(mm, DISPLAY_CONTROL_MIN_PHYSICAL_MONITOR_WIDTH,
                        DISPLAY_CONTROL_MAX_PHYSICAL_MONITOR_WIDTH);
}

char* duplicate_string(const char* text)
{
    if (!text) return NULL;

    size_t length = strlen(text) + 1;
    char* copy = malloc(length);
    if (copy)
    {
        memcpy(copy, text, length);
    }

    return copy;
}

static void copy_string_field(char* dest, size_t dest_size, const char* src)
{
    if (!dest || dest_size == 0) return;
    if (!src)
    {
        dest[0] = '\0';
        return;
    }

    size_t length = strlen(src);
    if (length >= dest_size) length = dest_size - 1;
    memcpy(dest, src, length);
    dest[length] = '\0';
}

static bool equals_ignore_case(const char* left, const char* right)
{
    if (!left || !right) return false;

    while (*left && *right)
    {
        if (tolower((unsigned char)*left) != tolower((unsigned char)*right)) return false;
        left++;
        right++;
    }

    return *left == '\0' && *right == '\0';
}

static rendering_mode get_configured_rendering_mode(void)
{
    const char* value = getenv("RDPILOT_RENDERING_MODE");
    if (!value || value[0] == '\0') return RENDERING_MODE_GFX_GDI;

    if (equals_ignore_case(value, "gfx-gdi") ||
        equals_ignore_case(value, "rdpgfx") ||
        equals_ignore_case(value, "gfx") ||
        equals_ignore_case(value, "1") ||
        equals_ignore_case(value, "true"))
    {
        return RENDERING_MODE_GFX_GDI;
    }

    if (!equals_ignore_case(value, "classic-gdi") &&
        !equals_ignore_case(value, "gdi") &&
        !equals_ignore_case(value, "0") &&
        !equals_ignore_case(value, "false"))
    {
        fprintf(stderr, "[RENDER] Unknown RDPILOT_RENDERING_MODE='%s'; using gfx-gdi\n", value);
    }

    return RENDERING_MODE_GFX_GDI;
}

static const char* rendering_mode_name(rendering_mode mode)
{
    if (mode == RENDERING_MODE_GFX_GDI) return "gfx-gdi";
    return "classic-gdi";
}

static bool is_graphics_pipeline_mode(rendering_mode mode)
{
    return mode == RENDERING_MODE_GFX_GDI;
}

static const char* get_gfx_codec_policy(void)
{
    const char* value = getenv("RDPILOT_GFX_CODEC_POLICY");
    if (value && value[0] != '\0') return value;
    return "server";
}

static bool env_bool_value(const char* value, bool default_value)
{
    if (!value || value[0] == '\0') return default_value;
    if (equals_ignore_case(value, "1") ||
        equals_ignore_case(value, "true") ||
        equals_ignore_case(value, "yes") ||
        equals_ignore_case(value, "on"))
    {
        return true;
    }

    if (equals_ignore_case(value, "0") ||
        equals_ignore_case(value, "false") ||
        equals_ignore_case(value, "no") ||
        equals_ignore_case(value, "off"))
    {
        return false;
    }

    return default_value;
}

static bool get_gfx_qoe_ack_enabled(rendering_mode mode)
{
    (void)mode;
    const char* value = getenv("RDPILOT_GFX_QOE_ACK");
    return env_bool_value(value, false);
}

static bool get_gfx_frame_ack_enabled(rendering_mode mode)
{
    (void)mode;
    const char* value = getenv("RDPILOT_GFX_FRAME_ACK");
    return env_bool_value(value, true);
}

static void configure_graphics_pipeline_settings(rdpSettings* settings, rendering_mode mode)
{
    if (!settings || !is_graphics_pipeline_mode(mode)) return;

    const char* policy = get_gfx_codec_policy();
    if (equals_ignore_case(policy, "server") ||
        equals_ignore_case(policy, "default") ||
        equals_ignore_case(policy, "auto"))
    {
        return;
    }

    if (equals_ignore_case(policy, "avc") ||
        equals_ignore_case(policy, "video") ||
        equals_ignore_case(policy, "h264"))
    {
        freerdp_settings_set_uint32(settings, FreeRDP_GfxCapsFilter, 0);
        freerdp_settings_set_bool(settings, FreeRDP_GfxProgressive, FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxProgressiveV2, FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxH264, TRUE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444, TRUE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444v2, TRUE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxCodecAV1, FALSE);
        return;
    }

    if (equals_ignore_case(policy, "avc420") ||
        equals_ignore_case(policy, "h264-420") ||
        equals_ignore_case(policy, "h264_420"))
    {
        UINT32 caps_filter = 0;
#if defined(WITH_GFX_AV1)
        caps_filter = (1u << 0) | (1u << 1) | (1u << 3) | (1u << 4) | (1u << 5) |
                      (1u << 6) | (1u << 7) | (1u << 8) | (1u << 9) | (1u << 10) |
                      (1u << 11);
#else
        caps_filter = (1u << 0) | (1u << 2) | (1u << 3) | (1u << 4) | (1u << 5) |
                      (1u << 6) | (1u << 7) | (1u << 8) | (1u << 9) | (1u << 10);
#endif
        freerdp_settings_set_uint32(settings, FreeRDP_GfxCapsFilter, caps_filter);
        freerdp_settings_set_bool(settings, FreeRDP_GfxProgressive, FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxProgressiveV2, FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxH264, TRUE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444, FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444v2, FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxCodecAV1, FALSE);
        return;
    }

    UINT32 caps_filter = 0;
#if defined(WITH_GFX_AV1)
    caps_filter = (1u << 0) | (1u << 3) | (1u << 4) | (1u << 5) | (1u << 6) |
                  (1u << 7) | (1u << 8) | (1u << 9) | (1u << 10) | (1u << 11);
#else
    caps_filter = (1u << 2) | (1u << 3) | (1u << 4) | (1u << 5) | (1u << 6) |
                  (1u << 7) | (1u << 8) | (1u << 9) | (1u << 10);
#endif
    freerdp_settings_set_uint32(settings, FreeRDP_GfxCapsFilter, caps_filter);
    freerdp_settings_set_bool(settings, FreeRDP_GfxProgressive, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxProgressiveV2, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxH264, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444v2, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxCodecAV1, FALSE);
}

static void log_keyboard_settings(const char* phase, rdpSettings* settings)
{
    if (!settings) return;

    printf("[KEYBOARD] phase=%s layout=0x%08X type=%u subtype=%u functionKeys=%u\n",
           phase,
           freerdp_settings_get_uint32(settings, FreeRDP_KeyboardLayout),
           freerdp_settings_get_uint32(settings, FreeRDP_KeyboardType),
           freerdp_settings_get_uint32(settings, FreeRDP_KeyboardSubType),
           freerdp_settings_get_uint32(settings, FreeRDP_KeyboardFunctionKey));
}

static UINT32 get_gdi_pixel_format(rendering_mode mode)
{
    (void)mode;
    return is_graphics_pipeline_mode(mode) ? PIXEL_FORMAT_BGRX32 : PIXEL_FORMAT_BGRA32;
}

static void queue_resolution_update(rdp_session* session, UINT32 width, UINT32 height)
{
    if (!session) return;

    EnterCriticalSection(&session->resize_lock);
    if (width != session->target_width || height != session->target_height)
    {
        session->target_width = width;
        session->target_height = height;
        session->resize_pending = true;
    }
    LeaveCriticalSection(&session->resize_lock);
}

static BOOL on_end_paint(rdpContext* context)
{
    rdp_session* session = session_from_context(context);
    if (!session || !session->callback) return TRUE;

    rdpGdi* gdi = context->gdi;
    if (!gdi || !gdi->primary_buffer) return TRUE;

    HGDI_WND hwnd = gdi->primary && gdi->primary->hdc ? gdi->primary->hdc->hwnd : NULL;
    if (!hwnd) return TRUE;

    HGDI_RGN invalid = hwnd->invalid;
    if (invalid && !invalid->null && invalid->w > 0 && invalid->h > 0)
    {
        notify_pending_frame(session, gdi, invalid->x, invalid->y, invalid->w, invalid->h);
        return TRUE;
    }

    const int ninvalid = hwnd->ninvalid;
    const HGDI_RGN cinvalid = hwnd->cinvalid;
    if (ninvalid <= 0 || !cinvalid) return TRUE;

    INT32 min_x = 0, min_y = 0, max_x = 0, max_y = 0;
    for (int i = 0; i < ninvalid; i++)
    {
        INT32 x = cinvalid[i].x;
        INT32 y = cinvalid[i].y;
        INT32 right = x + cinvalid[i].w;
        INT32 bottom = y + cinvalid[i].h;
        if (i == 0)
        {
            min_x = x; min_y = y; max_x = right; max_y = bottom;
        }
        else
        {
            if (x < min_x) min_x = x;
            if (y < min_y) min_y = y;
            if (right > max_x) max_x = right;
            if (bottom > max_y) max_y = bottom;
        }
    }

    if (max_x <= min_x || max_y <= min_y) return TRUE;
    notify_pending_frame(session, gdi, min_x, min_y, max_x - min_x, max_y - min_y);
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

static UINT on_gfx_caps_confirm_log(RdpgfxClientContext* context, const RDPGFX_CAPS_CONFIRM_PDU* caps)
{
    (void)context;
    if (caps && caps->capsSet)
    {
        printf("[RDPGFX] capsConfirm version=0x%08X flags=0x%08X\n",
               caps->capsSet->version,
               caps->capsSet->flags);
    }
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
            session->cliprdr->ServerFileContentsRequest = on_cliprdr_server_file_contents_request;
            session->cliprdr->ServerFormatDataResponse = on_cliprdr_server_format_data_response;
            session->cliprdr->ServerFileContentsResponse = on_cliprdr_server_file_contents_response;
        }
        printf("[CLIPRDR] channel connected\n");
    }
    else if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0)
    {
        rdp_session* session = session_from_context((rdpContext*)context);
        if (session) session->gfx = (RdpgfxClientContext*)e->pInterface;
        if (session && session->gfx) session->gfx->custom = session;
        init_graphics_pipeline((rdpContext*)context);
        if (session && session->gfx)
        {
            // Observe-only CapsConfirm hook (no chained handler; the default FreeRDP behavior is
            // a no-op and we never overrode it with the surface-renderer path). Restores
            // visibility into the negotiated RDPGFX capset so we can confirm which codec family
            // the server picked (e.g. ClearCodec vs AVC).
            session->gfx->CapsConfirm = on_gfx_caps_confirm_log;
        }
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
        if (rdp_context && rdp_context->gdi && session && session->gfx && is_graphics_pipeline_mode(session->render_mode))
        {
            gdi_graphics_pipeline_uninit(rdp_context->gdi, session->gfx);
        }
        if (session)
        {
            session->gfx = NULL;
        }
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
    rdp_session* session = session_from_context(rdp_context);
    UINT32 width = e && e->width != 0 ? e->width : freerdp_settings_get_uint32(rdp_context->settings, FreeRDP_DesktopWidth);
    UINT32 height = e && e->height != 0 ? e->height : freerdp_settings_get_uint32(rdp_context->settings, FreeRDP_DesktopHeight);
    printf("[DEBUG] Graphics Reset: %ux%u\n", width, height);

    if (session && is_graphics_pipeline_mode(session->render_mode))
    {
        if (rdp_context->gdi)
        {
            reset_pending_dirty_fullscreen(session, rdp_context->gdi);
            BYTE* primary = rdp_context->gdi->primary_buffer;
            INT32 gdi_w = rdp_context->gdi->width;
            INT32 gdi_h = rdp_context->gdi->height;
            if (session->callback && primary)
            {
                INT32 stride = gdi_w * 4;
                session->callback(session, (uint8_t*)primary, gdi_w, gdi_h, 0, 0, gdi_w, gdi_h, stride);
            }
        }
        return;
    }

    gdi_free(rdp_context->instance);
    if (!gdi_init(rdp_context->instance, get_gdi_pixel_format(session ? session->render_mode : RENDERING_MODE_CLASSIC_GDI)))
    {
        fprintf(stderr, "Failed to re-initialize GDI\n");
    }

    // After GDI re-init, we need to re-hook the callbacks
    rdp_context->update->SurfaceBits = on_surface_bits;
    rdp_context->update->EndPaint = on_end_paint;
    rdp_context->update->DesktopResize = on_desktop_resize;
    init_graphics_pipeline(rdp_context);
    resize_local_framebuffer(rdp_context, width, height);
}

static bool process_pending_resize(rdp_session* session)
{
    if (!session || !session->instance || !session->instance->context || !session->disp || !session->disp_ready) return true;

    UINT32 width = 0;
    UINT32 height = 0;
    EnterCriticalSection(&session->resize_lock);
    bool pending = session->resize_pending;
    if (pending)
    {
        width = session->target_width;
        height = session->target_height;
    }
    LeaveCriticalSection(&session->resize_lock);

    if (!pending) return true;

    if (width == session->last_sent_width && height == session->last_sent_height)
    {
        EnterCriticalSection(&session->resize_lock);
        if (session->target_width == width && session->target_height == height) session->resize_pending = false;
        LeaveCriticalSection(&session->resize_lock);
        return true;
    }

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
    UINT32 effective_dpi = dpi_from_scale_percent(session->dpi_scale_percent);
    layout.PhysicalWidth = physical_size_mm_with_dpi(width, effective_dpi);
    layout.PhysicalHeight = physical_size_mm_with_dpi(height, effective_dpi);
    layout.Orientation = ORIENTATION_LANDSCAPE;
    layout.DesktopScaleFactor = rdp_clamp_scale_percent(session->dpi_scale_percent);
    layout.DeviceScaleFactor = rdp_clamp_scale_percent(session->dpi_scale_percent);

    UINT rc = session->disp->SendMonitorLayout(session->disp, 1, &layout);
    if (rc != CHANNEL_RC_OK)
    {
        fprintf(stderr, "Failed to send monitor layout update %ux%u, rc=%u\n", width, height, rc);
        return true;
    }

    session->last_sent_width = width;
    session->last_sent_height = height;

    if (!is_graphics_pipeline_mode(session->render_mode) && !resize_local_framebuffer(session->instance->context, width, height))
    {
        return true;
    }

    EnterCriticalSection(&session->resize_lock);
    if (session->target_width == width && session->target_height == height) session->resize_pending = false;
    LeaveCriticalSection(&session->resize_lock);

    printf("Sent monitor layout update: %ux%u\n", width, height);
    return true;
}

void rdp_session_request_full_frame(rdp_session* session)
{
    if (!session) return;

    EnterCriticalSection(&session->frame_lock);
    if (session->instance && session->instance->context)
    {
        rdpGdi* gdi = session->instance->context->gdi;
        if (gdi && gdi->primary_buffer && gdi->width > 0 && gdi->height > 0)
        {
            session->pending_frame = 1;
            session->pending_dirty_x = 0;
            session->pending_dirty_y = 0;
            session->pending_dirty_w = gdi->width;
            session->pending_dirty_h = gdi->height;
        }
    }
    LeaveCriticalSection(&session->frame_lock);
}

static BOOL on_surface_bits(rdpContext* context,
                            const SURFACE_BITS_COMMAND* cmd)
{
    (void)context;
    (void)cmd;
    return TRUE;
}

static void log_loop_stats_if_due(rdp_session* session,
                                  UINT32 total_ms,
                                  UINT32 input_ms,
                                  UINT32 clipboard_ms,
                                  UINT32 check_fds_ms,
                                  UINT32 resize_ms)
{
    if (!session) return;

    ULONGLONG now = GetTickCount64();
    if (session->perf_loop_last_log_tick == 0) session->perf_loop_last_log_tick = now;

    session->perf_loop_count++;
    if (total_ms > 50) session->perf_loop_slow_count++;
    if (total_ms > session->perf_loop_max_total_ms) session->perf_loop_max_total_ms = total_ms;
    if (input_ms > session->perf_loop_max_input_ms) session->perf_loop_max_input_ms = input_ms;
    if (clipboard_ms > session->perf_loop_max_clipboard_ms) session->perf_loop_max_clipboard_ms = clipboard_ms;
    if (check_fds_ms > session->perf_loop_max_check_fds_ms) session->perf_loop_max_check_fds_ms = check_fds_ms;
    if (resize_ms > session->perf_loop_max_resize_ms) session->perf_loop_max_resize_ms = resize_ms;

    ULONGLONG elapsed = now - session->perf_loop_last_log_tick;
    if (elapsed < 1000) return;

    //printf("[PERF_LOOP] loops=%u slow=%u maxTotal=%ums inputMax=%ums clipboardMax=%ums checkFdsMax=%ums resizeMax=%ums\n",
    //       session->perf_loop_count,
    //       session->perf_loop_slow_count,
    //       session->perf_loop_max_total_ms,
    //       session->perf_loop_max_input_ms,
    //       session->perf_loop_max_clipboard_ms,
    //       session->perf_loop_max_check_fds_ms,
    //       session->perf_loop_max_resize_ms);

    session->perf_loop_last_log_tick = now;
    session->perf_loop_count = 0;
    session->perf_loop_slow_count = 0;
    session->perf_loop_max_total_ms = 0;
    session->perf_loop_max_input_ms = 0;
    session->perf_loop_max_clipboard_ms = 0;
    session->perf_loop_max_check_fds_ms = 0;
    session->perf_loop_max_resize_ms = 0;
}

static DWORD on_verify_certificate_ex(freerdp* instance, const char* host, UINT16 port,
                                      const char* common_name, const char* subject,
                                      const char* issuer, const char* fingerprint, DWORD flags)
{
    (void)flags;
    rdp_session* session = (instance && instance->context) ? session_from_context(instance->context) : NULL;
    if (!session || !session->certificate_decision_callback)
    {
        return 0;
    }

    return (DWORD)session->certificate_decision_callback(
        session,
        host,
        port,
        common_name,
        subject,
        issuer,
        fingerprint,
        0,
        NULL,
        NULL,
        NULL);
}

static DWORD on_verify_changed_certificate_ex(freerdp* instance, const char* host, UINT16 port,
                                              const char* common_name, const char* subject,
                                              const char* issuer, const char* new_fingerprint,
                                              const char* old_subject, const char* old_issuer,
                                              const char* old_fingerprint, DWORD flags)
{
    (void)flags;
    rdp_session* session = (instance && instance->context) ? session_from_context(instance->context) : NULL;
    if (!session || !session->certificate_decision_callback)
    {
        return 0;
    }

    return (DWORD)session->certificate_decision_callback(
        session,
        host,
        port,
        common_name,
        subject,
        issuer,
        new_fingerprint,
        1,
        old_subject,
        old_issuer,
        old_fingerprint);
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
    EnterCriticalSection(&session->frame_lock);
    session->pending_frame = 0;
    LeaveCriticalSection(&session->frame_lock);
}

static void shutdown_instance(rdp_session* session)
{
    if (!session || !session->instance) return;

    if (session->connect_succeeded)
    {
        freerdp_disconnect(session->instance);
    }

    session->connect_succeeded = false;
    cleanup_instance(session);
}

static bool setup_instance(rdp_session* session, const connection_params* params, bool use_gateway)
{
    session->instance = freerdp_new();
    if (!session->instance) {
        fprintf(stderr, "Failed to create FreeRDP instance\n");
        return false;
    }

    session->instance->LoadChannels = freerdp_client_load_channels;
    session->instance->VerifyCertificateEx = on_verify_certificate_ex;
    session->instance->VerifyChangedCertificateEx = on_verify_changed_certificate_ex;
    session->instance->ContextSize = sizeof(wrapper_context);
    if (!freerdp_context_new(session->instance)) {
        fprintf(stderr, "Failed to create FreeRDP context\n");
        cleanup_instance(session);
        return false;
    }

    ((wrapper_context*)session->instance->context)->session = session;

    rdpSettings* settings = session->instance->context->settings;
    const char* server_hostname = use_gateway ? params->host : params->connect_host;
    printf("[DEBUG] Target host='%s' connectHost='%s' serverHostname='%s' port=%u gateway=%s\n",
           params->host,
           params->connect_host,
           server_hostname,
           DEFAULT_RDP_PORT,
           use_gateway ? "true" : "false");
    freerdp_settings_set_string(settings, FreeRDP_ServerHostname, server_hostname);
    freerdp_settings_set_string(settings, FreeRDP_UserSpecifiedServerName, params->host);
    freerdp_settings_set_string(settings, FreeRDP_CertificateName, params->host);
    freerdp_settings_set_uint32(settings, FreeRDP_ServerPort, DEFAULT_RDP_PORT);
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

    freerdp_settings_set_bool(settings, FreeRDP_IgnoreCertificate, FALSE);

    // Disable FreeRDP's own window/UI creation
    freerdp_settings_set_uint32(settings, FreeRDP_OsMajorType, OSMAJORTYPE_WINDOWS);
    freerdp_settings_set_uint32(settings, FreeRDP_OsMinorType, OSMINORTYPE_WINDOWS_NT);
    freerdp_settings_set_bool(settings, FreeRDP_DeactivateClientDecoding, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_SoftwareGdi, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_AutoReconnectionEnabled, TRUE);

    freerdp_settings_set_bool(settings, FreeRDP_SupportDynamicChannels, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_SupportDisplayControl, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_SupportMonitorLayoutPdu, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline,
                              is_graphics_pipeline_mode(session->render_mode) ? TRUE : FALSE);
    configure_graphics_pipeline_settings(settings, session->render_mode);
    if (is_graphics_pipeline_mode(session->render_mode))
    {
        bool frame_ack = get_gfx_frame_ack_enabled(session->render_mode);
        bool qoe_ack = get_gfx_qoe_ack_enabled(session->render_mode);
        freerdp_settings_set_bool(settings, FreeRDP_GfxSendQoeAck, qoe_ack ? TRUE : FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxSuspendFrameAck, frame_ack ? FALSE : TRUE);
    }
    freerdp_settings_set_bool(settings, FreeRDP_DynamicResolutionUpdate, TRUE);
    UINT32 clamped_scale = rdp_clamp_scale_percent(session->dpi_scale_percent);
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopScaleFactor, clamped_scale);
    freerdp_settings_set_uint32(settings, FreeRDP_DeviceScaleFactor, clamped_scale);
    freerdp_settings_set_bool(settings, FreeRDP_RedirectClipboard, TRUE);
    freerdp_settings_set_uint32(settings, FreeRDP_ClipboardFeatureMask,
                                CLIPRDR_FLAG_LOCAL_TO_REMOTE |
                                    CLIPRDR_FLAG_REMOTE_TO_LOCAL |
                                    CLIPRDR_FLAG_LOCAL_TO_REMOTE_FILES |
                                    CLIPRDR_FLAG_REMOTE_TO_LOCAL_FILES);
    freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_MstscCookieMode, TRUE);
    printf("[KEYBOARD] phase=requested layout=0x%08X\n", params->keyboard_layout);
    if (params->keyboard_layout != 0) freerdp_settings_set_uint32(settings, FreeRDP_KeyboardLayout, params->keyboard_layout);
    freerdp_settings_set_uint32(settings, FreeRDP_KeyboardType, WINPR_KBD_TYPE_IBM_ENHANCED);
    freerdp_settings_set_uint32(settings, FreeRDP_KeyboardSubType, 0);
    freerdp_settings_set_uint32(settings, FreeRDP_KeyboardFunctionKey, 12);
    log_keyboard_settings("configured", settings);
    freerdp_settings_set_bool(settings, FreeRDP_AudioPlayback, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_DeviceRedirection, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_CompressionEnabled, params->compression ? TRUE : FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_BitmapCacheEnabled, params->bitmap_cache ? TRUE : FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_BitmapCachePersistEnabled, FALSE);
    freerdp_settings_set_uint32(settings, FreeRDP_ConnectionType, (UINT32)params->connection_type);
    freerdp_settings_set_bool(settings, FreeRDP_NetworkAutoDetect, params->network_auto_detect ? TRUE : FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableWallpaper, params->desktop_wallpaper ? FALSE : TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableFullWindowDrag, params->full_window_drag ? FALSE : TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableMenuAnims, params->menu_animations ? FALSE : TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableThemes, params->themes ? FALSE : TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_AllowFontSmoothing, params->font_smoothing ? TRUE : FALSE);
    freerdp_performance_flags_make(settings);

    printf("[RENDER] mode=%s supportGraphicsPipeline=%s\n",
           rendering_mode_name(session->render_mode),
           is_graphics_pipeline_mode(session->render_mode) ? "true" : "false");
    if (is_graphics_pipeline_mode(session->render_mode))
    {
        printf("[RENDER] gfxCodecPolicy=%s\n", get_gfx_codec_policy());
        printf("[RENDER] gfxFrameAck=%s gfxQoeAck=%s\n",
               get_gfx_frame_ack_enabled(session->render_mode) ? "on" : "off",
               get_gfx_qoe_ack_enabled(session->render_mode) ? "on" : "off");
        printf("[RENDER] gfxCapsFilter=0x%08X\n",
               freerdp_settings_get_uint32(settings, FreeRDP_GfxCapsFilter));
    }
    printf("[RENDER] connectionType=%u performanceFlags=0x%08X\n",
           freerdp_settings_get_uint32(settings, FreeRDP_ConnectionType),
           freerdp_settings_get_uint32(settings, FreeRDP_PerformanceFlags));
    printf("[DEBUG] Channels set up, connecting...\n");
    freerdp_settings_set_string(settings, FreeRDP_ClientHostname, "RDPilot");

    UINT32 color_depth = (UINT32)params->color_depth;
    freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, color_depth);
    fprintf(stderr,
            "[QUALITY] colorDepth=%u connectionType=%u networkAutoDetect=%s compression=%s bitmapCache=%s "
            "fontSmoothing=%s wallpaper=%s themes=%s menuAnimations=%s fullWindowDrag=%s "
            "performanceFlags=0x%08X\n",
            freerdp_settings_get_uint32(settings, FreeRDP_ColorDepth),
            freerdp_settings_get_uint32(settings, FreeRDP_ConnectionType),
            freerdp_settings_get_bool(settings, FreeRDP_NetworkAutoDetect) ? "on" : "off",
            freerdp_settings_get_bool(settings, FreeRDP_CompressionEnabled) ? "on" : "off",
            freerdp_settings_get_bool(settings, FreeRDP_BitmapCacheEnabled) ? "on" : "off",
            freerdp_settings_get_bool(settings, FreeRDP_AllowFontSmoothing) ? "on" : "off",
            freerdp_settings_get_bool(settings, FreeRDP_DisableWallpaper) ? "off" : "on",
            freerdp_settings_get_bool(settings, FreeRDP_DisableThemes) ? "off" : "on",
            freerdp_settings_get_bool(settings, FreeRDP_DisableMenuAnims) ? "off" : "on",
            freerdp_settings_get_bool(settings, FreeRDP_DisableFullWindowDrag) ? "off" : "on",
            freerdp_settings_get_uint32(settings, FreeRDP_PerformanceFlags));
    fflush(stderr);

    UINT32 desktop_width = (UINT32)params->width;
    UINT32 desktop_height = (UINT32)params->height;
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
    UINT32 monitor_scale = rdp_clamp_scale_percent(session->dpi_scale_percent);
    UINT32 effective_dpi = dpi_from_scale_percent(session->dpi_scale_percent);
    monitors[0].attributes.physicalWidth = physical_size_mm_with_dpi(desktop_width, effective_dpi);
    monitors[0].attributes.physicalHeight = physical_size_mm_with_dpi(desktop_height, effective_dpi);
    monitors[0].attributes.orientation = ORIENTATION_LANDSCAPE;
    monitors[0].attributes.desktopScaleFactor = monitor_scale;
    monitors[0].attributes.deviceScaleFactor = monitor_scale;
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

static bool connect_attempt(rdp_session* session, const connection_params* params, bool use_gateway, connection_error* error)
{
    if (error) memset(error, 0, sizeof(connection_error));

    if (!setup_instance(session, params, use_gateway))
    {
        if (error)
        {
            error->name = "WRAPPER_SETUP_FAILED";
            error->message = "Failed to initialize the RDP client before connecting.";
        }
        return false;
    }

    printf("[DEBUG] Connecting %s gateway...\n", use_gateway ? "with" : "without");

    if (!freerdp_connect(session->instance)) {
        fprintf(stderr, "Failed to connect %s gateway\n", use_gateway ? "with" : "without");
        capture_last_error(session->instance->context, error);
        session->connect_succeeded = false;
        cleanup_instance(session);
        return false;
    }

    session->connect_succeeded = true;
    if (session->instance && session->instance->context)
    {
        log_keyboard_settings("connected", session->instance->context->settings);
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
        connection_error error = { 0, "WRAPPER_ADDIN_PROVIDER_FAILED", "Failed to register FreeRDP static addin provider." };
        emit_status(session, 2, &error);
        return 1;
    }

    bool has_gateway = params->gateway_host[0] != '\0';
    connection_error error;
    connection_error direct_error = { 0 };
    bool connected = connect_attempt(session, params, false, &error);
    if (!connected)
    {
        direct_error = error;
    }

    if (!connected && has_gateway && session->running)
    {
        connected = connect_attempt(session, params, true, &error);
    }

    if (!connected) {
        fprintf(stderr, "Failed to connect\n");
        free(params);
        session->running = false;
        if (has_gateway && direct_error.name && error.name)
        {
            char combined_message[1024] = { 0 };
            const char* direct_message = direct_error.message ? direct_error.message : "Unknown direct connection error.";
            const char* gateway_message = error.message ? error.message : "Unknown gateway connection error.";
            snprintf(combined_message, sizeof(combined_message),
                     "Direct connection failed: %s Gateway connection failed: %s",
                     direct_message, gateway_message);
            connection_error combined_error = {
                error.code,
                "WRAPPER_DIRECT_AND_GATEWAY_FAILED",
                combined_message,
            };
            emit_status(session, 2, &combined_error);
            return 1;
        }

        emit_status(session, 2, &error);
        return 1;
    }

    emit_status(session, 1, NULL);

    gdi_init(session->instance, get_gdi_pixel_format(session->render_mode));

    // Hook callbacks after GDI init as it might override them
    session->instance->context->update->SurfaceBits = on_surface_bits;
    session->instance->context->update->EndPaint = on_end_paint;
    session->instance->context->update->DesktopResize = on_desktop_resize;
    init_graphics_pipeline(session->instance->context);

    free(params);

    while (session->running) {
        ULONGLONG loop_start = GetTickCount64();

        // Canonical FreeRDP client loop: wait on all transport/channel event handles, then drain
        // them in a single freerdp_check_event_handles call (which itself invokes
        // freerdp_check_fds + freerdp_channels_check_fds + error-event check). This matches
        // wfreerdp/xfreerdp and avoids the previous busy-poll + redundant per-handle
        // WaitForSingleObject(0) + Sleep(2) which spammed mouse moves at ~500 Hz during drag and
        // made the server pace RDPGFX frames down to 1-12 fps.
        HANDLE eventHandles[MAXIMUM_WAIT_OBJECTS];
        DWORD eventCount = freerdp_get_event_handles(session->instance->context, eventHandles, MAXIMUM_WAIT_OBJECTS);
        if (eventCount == 0)
        {
            fprintf(stderr, "freerdp_get_event_handles failed\n");
            break;
        }

        DWORD waitStatus = WaitForMultipleObjects(eventCount, eventHandles, FALSE, INPUT_LOOP_TIMEOUT_MS);
        if (waitStatus == WAIT_FAILED)
        {
            fprintf(stderr, "RDP loop WaitForMultipleObjects failed: 0x%08X\n", (unsigned)GetLastError());
            break;
        }

        // Wake: data ready (waitStatus in [0, eventCount)) or timeout expired (WAIT_TIMEOUT).
        // Either way, drain input first so the server sees the latest local mouse/key state,
        // then process channel data, then handle resize.
        ULONGLONG phase_start = GetTickCount64();
        process_pending_input(session);
        UINT32 input_ms = (UINT32)(GetTickCount64() - phase_start);

        phase_start = GetTickCount64();
        process_pending_clipboard(session);
        UINT32 clipboard_ms = (UINT32)(GetTickCount64() - phase_start);

        phase_start = GetTickCount64();
        BOOL fdsOk = freerdp_check_event_handles(session->instance->context);
        UINT32 check_fds_ms = (UINT32)(GetTickCount64() - phase_start);

        if (!fdsOk)
        {
            // If check_event_handles fails it might be transient or a real disconnect. When no
            // FreeRDP error was recorded, retry the loop instead of tearing down the session.
            log_loop_stats_if_due(session,
                                  (UINT32)(GetTickCount64() - loop_start),
                                  input_ms,
                                  clipboard_ms,
                                  check_fds_ms,
                                  0);
            if (freerdp_get_last_error(session->instance->context) == 0)
            {
                continue;
            }
            break;
        }

        if (freerdp_shall_disconnect_context(session->instance->context))
            break;

        phase_start = GetTickCount64();
        if (!process_pending_resize(session))
            break;
        UINT32 resize_ms = (UINT32)(GetTickCount64() - phase_start);

        log_loop_stats_if_due(session,
                              (UINT32)(GetTickCount64() - loop_start),
                              input_ms,
                              clipboard_ms,
                              check_fds_ms,
                              resize_ms);
    }


    shutdown_instance(session);
    session->running = false;
    emit_status(session, 3, NULL);

    return 0;
}

rdp_session* rdp_session_connect(const char* host, const char* connect_host, const char* domain, const char* user, const char* password,
                                 const char* gateway_host, const char* gateway_domain, const char* gateway_user, const char* gateway_password,
                                 int width, int height, int color_depth, bool compression, bool font_smoothing, bool bitmap_cache,
                                 bool desktop_wallpaper, bool themes, bool menu_animations, bool full_window_drag, int connection_type, bool network_auto_detect,
                                 uint32_t keyboard_layout, uint32_t dpi_scale_percent,
                                 FrameCallback frame_callback, ClipboardTextCallback clipboard_text_callback, ClipboardFilesCallback clipboard_files_callback, StatusCallback status_callback, CertificateDecisionCallback certificate_decision_callback) {
    rdp_session* session = calloc(1, sizeof(rdp_session));
    if (!session) return NULL;

    session->dpi_scale_percent = dpi_scale_percent == 0 ? 100 : dpi_scale_percent;
    session->callback = frame_callback;
    session->clipboard_text_callback = clipboard_text_callback;
    session->clipboard_files_callback = clipboard_files_callback;
    session->status_callback = status_callback;
    session->certificate_decision_callback = certificate_decision_callback;
    session->render_mode = get_configured_rendering_mode();
    InitializeCriticalSection(&session->resize_lock);
    InitializeCriticalSection(&session->input_lock);
    InitializeCriticalSection(&session->clipboard_lock);
    InitializeCriticalSection(&session->frame_lock);

#if defined(_WIN32)
    /* On Windows, use the actual Win32 registered clipboard format IDs.
     * This matches wfreerdp, which calls RegisterClipboardFormat(CFSTR_FILEDESCRIPTORW).
     * The Windows RDP server uses these IDs to identify file clipboard formats. */
    session->file_group_descriptor_format_id = RegisterClipboardFormatA("FileGroupDescriptorW");
    session->file_contents_format_id = RegisterClipboardFormatA("FileContents");
#else
    session->file_clipboard = ClipboardCreate();
    if (session->file_clipboard)
    {
        session->file_group_descriptor_format_id = ClipboardRegisterFormat(session->file_clipboard, "FileGroupDescriptorW");
        session->file_contents_format_id = ClipboardRegisterFormat(session->file_clipboard, "FileContents");
    }
#endif

    UINT32 initial_width = (UINT32)width;
    UINT32 initial_height = (UINT32)height;
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
    copy_string_field(params->host, sizeof(params->host), host);
    copy_string_field(params->connect_host, sizeof(params->connect_host), connect_host && connect_host[0] != '\0' ? connect_host : host);
    copy_string_field(params->domain, sizeof(params->domain), domain);
    copy_string_field(params->user, sizeof(params->user), user);
    copy_string_field(params->password, sizeof(params->password), password);
    copy_string_field(params->gateway_host, sizeof(params->gateway_host), gateway_host);
    copy_string_field(params->gateway_domain, sizeof(params->gateway_domain), gateway_domain);
    copy_string_field(params->gateway_user, sizeof(params->gateway_user), gateway_user);
    copy_string_field(params->gateway_password, sizeof(params->gateway_password), gateway_password);
    params->width = (int)initial_width;
    params->height = (int)initial_height;
    params->color_depth = (int)color_depth;
    params->compression = compression;
    params->font_smoothing = font_smoothing;
    params->bitmap_cache = bitmap_cache;
    params->desktop_wallpaper = desktop_wallpaper;
    params->themes = themes;
    params->menu_animations = menu_animations;
    params->full_window_drag = full_window_drag;
    params->connection_type = (int)connection_type;
    params->network_auto_detect = network_auto_detect;
    params->keyboard_layout = keyboard_layout;

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
    session->callback = NULL;
    session->clipboard_text_callback = NULL;
    session->clipboard_files_callback = NULL;
    session->status_callback = NULL;
    DeleteCriticalSection(&session->resize_lock);
    DeleteCriticalSection(&session->input_lock);
    DeleteCriticalSection(&session->clipboard_lock);
    DeleteCriticalSection(&session->frame_lock);
    if (session->file_clipboard)
        ClipboardDestroy(session->file_clipboard);
    free_clipboard_data(session);
    free(session);
}

void rdp_session_update_resolution(rdp_session* session, int width, int height, uint32_t dpi_scale_percent) {
    if (!session || width <= 0 || height <= 0) return;
    // DPI scale is locked at connect time. The Windows RDP server does not reliably handle
    // mid-session desktopScaleFactor changes (UWP processes like the Start Menu don't reflow).
    // We only change the resolution; the server keeps the DPI from the initial connection.
    (void)dpi_scale_percent;
    queue_resolution_update(session, (UINT32)width, (UINT32)height);
}
