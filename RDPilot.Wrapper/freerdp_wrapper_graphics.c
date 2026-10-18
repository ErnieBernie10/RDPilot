#include "freerdp_wrapper_graphics.h"

static BOOL on_surface_bits(rdpContext* context, const SURFACE_BITS_COMMAND* cmd);
static BOOL on_end_paint(rdpContext* context);
static BOOL on_desktop_resize(rdpContext* context);

static void hook_updates(rdpContext* context)
{
    context->update->SurfaceBits = on_surface_bits;
    context->update->EndPaint = on_end_paint;
    context->update->DesktopResize = on_desktop_resize;
}

BOOL wrapper_graphics_resize(rdpContext* context, UINT32 width, UINT32 height)
{
    if (!context || !context->gdi) return TRUE;
    rdp_session* session = session_from_context(context);
    if (context->gdi->width == (INT32)width && context->gdi->height == (INT32)height) return TRUE;

    /* Protect primary-buffer lifetime against UI-thread present across gdi_resize. */
    EnterCriticalSection(&session->frame_lock);
    if (!gdi_resize(context->gdi, width, height))
    {
        LeaveCriticalSection(&session->frame_lock);
        fprintf(stderr, "Failed to resize local GDI framebuffer to %ux%u\n", width, height);
        return FALSE;
    }
    hook_updates(context);
    wrapper_frame_reset_dirty(session, context->gdi);
    BYTE* primary = context->gdi->primary_buffer;
    INT32 gdi_w = context->gdi->width;
    INT32 gdi_h = context->gdi->height;
    LeaveCriticalSection(&session->frame_lock);
    if (session && session->callback && primary)
    {
        session->callback(session, (uint8_t*)primary, gdi_w, gdi_h, 0, 0, gdi_w, gdi_h, gdi_w * 4);
    }
    return TRUE;
}

static void init_graphics_pipeline(rdpContext* context)
{
    rdp_session* session = session_from_context(context);
    if (!context || !context->gdi || !session || !session->gfx || !wrapper_graphics_uses_pipeline(session->render_mode)) return;
    if (!gdi_graphics_pipeline_init(context->gdi, session->gfx))
    {
        fprintf(stderr, "Failed to initialize RDPGFX GDI pipeline\n");
    }
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

rendering_mode wrapper_graphics_mode(void)
{
    const char* value = getenv("RDPILOT_RENDERING_MODE");
    if (!value || value[0] == '\0') return RENDERING_MODE_GFX_GDI;
    if (equals_ignore_case(value, "gfx-gdi") || equals_ignore_case(value, "rdpgfx") ||
        equals_ignore_case(value, "gfx") || equals_ignore_case(value, "1") || equals_ignore_case(value, "true"))
    {
        return RENDERING_MODE_GFX_GDI;
    }
    if (!equals_ignore_case(value, "classic-gdi") && !equals_ignore_case(value, "gdi") &&
        !equals_ignore_case(value, "0") && !equals_ignore_case(value, "false"))
    {
        fprintf(stderr, "[RENDER] Unknown RDPILOT_RENDERING_MODE='%s'; using gfx-gdi\n", value);
    }
    return RENDERING_MODE_GFX_GDI;
}

bool wrapper_graphics_uses_pipeline(rendering_mode mode)
{
    return mode == RENDERING_MODE_GFX_GDI;
}

static bool env_bool_value(const char* value, bool default_value)
{
    if (!value || value[0] == '\0') return default_value;
    if (equals_ignore_case(value, "1") || equals_ignore_case(value, "true") ||
        equals_ignore_case(value, "yes") || equals_ignore_case(value, "on")) return true;
    if (equals_ignore_case(value, "0") || equals_ignore_case(value, "false") ||
        equals_ignore_case(value, "no") || equals_ignore_case(value, "off")) return false;
    return default_value;
}

static void configure_codec_settings(rdpSettings* settings)
{
    const char* policy = getenv("RDPILOT_GFX_CODEC_POLICY");
    if (!policy || policy[0] == '\0') policy = "server";
    if (equals_ignore_case(policy, "server") || equals_ignore_case(policy, "default") || equals_ignore_case(policy, "auto")) return;

    if (equals_ignore_case(policy, "avc") || equals_ignore_case(policy, "video") || equals_ignore_case(policy, "h264"))
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
    if (equals_ignore_case(policy, "avc420") || equals_ignore_case(policy, "h264-420") || equals_ignore_case(policy, "h264_420"))
    {
#if defined(WITH_GFX_AV1)
        UINT32 filter = (1u << 0) | (1u << 1) | (1u << 3) | (1u << 4) | (1u << 5) | (1u << 6) |
                        (1u << 7) | (1u << 8) | (1u << 9) | (1u << 10) | (1u << 11);
#else
        UINT32 filter = (1u << 0) | (1u << 2) | (1u << 3) | (1u << 4) | (1u << 5) | (1u << 6) |
                        (1u << 7) | (1u << 8) | (1u << 9) | (1u << 10);
#endif
        freerdp_settings_set_uint32(settings, FreeRDP_GfxCapsFilter, filter);
        freerdp_settings_set_bool(settings, FreeRDP_GfxProgressive, FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxProgressiveV2, FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxH264, TRUE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444, FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444v2, FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GfxCodecAV1, FALSE);
        return;
    }
#if defined(WITH_GFX_AV1)
    UINT32 filter = (1u << 0) | (1u << 3) | (1u << 4) | (1u << 5) | (1u << 6) | (1u << 7) |
                    (1u << 8) | (1u << 9) | (1u << 10) | (1u << 11);
#else
    UINT32 filter = (1u << 2) | (1u << 3) | (1u << 4) | (1u << 5) | (1u << 6) | (1u << 7) |
                    (1u << 8) | (1u << 9) | (1u << 10);
#endif
    freerdp_settings_set_uint32(settings, FreeRDP_GfxCapsFilter, filter);
    freerdp_settings_set_bool(settings, FreeRDP_GfxProgressive, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxProgressiveV2, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxH264, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444v2, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxCodecAV1, FALSE);
}

void wrapper_graphics_configure(rdpSettings* settings, rendering_mode mode)
{
    freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline, wrapper_graphics_uses_pipeline(mode) ? TRUE : FALSE);
    if (!wrapper_graphics_uses_pipeline(mode)) return;
    configure_codec_settings(settings);
    bool frame_ack = env_bool_value(getenv("RDPILOT_GFX_FRAME_ACK"), true);
    bool qoe_ack = env_bool_value(getenv("RDPILOT_GFX_QOE_ACK"), false);
    freerdp_settings_set_bool(settings, FreeRDP_GfxSendQoeAck, qoe_ack ? TRUE : FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxSuspendFrameAck, frame_ack ? FALSE : TRUE);
}

static UINT32 gdi_pixel_format(rendering_mode mode)
{
    return wrapper_graphics_uses_pipeline(mode) ? PIXEL_FORMAT_BGRX32 : PIXEL_FORMAT_BGRA32;
}

void wrapper_graphics_initialize(rdp_session* session)
{
    gdi_init(session->instance, gdi_pixel_format(session->render_mode));
    rdpContext* context = session->instance->context;
    hook_updates(context);
    register_pointer_class(context);
    init_graphics_pipeline(context);
}

void wrapper_graphics_attach(rdpContext* context, RdpgfxClientContext* gfx)
{
    rdp_session* session = session_from_context(context);
    if (session) session->gfx = gfx;
    if (session && gfx) gfx->custom = session;
    init_graphics_pipeline(context);
}

void wrapper_graphics_detach(rdpContext* context)
{
    rdp_session* session = session_from_context(context);
    if (context && context->gdi && session && session->gfx && wrapper_graphics_uses_pipeline(session->render_mode))
    {
        gdi_graphics_pipeline_uninit(context->gdi, session->gfx);
    }
    if (session) session->gfx = NULL;
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
        wrapper_frame_notify(session, gdi, invalid->x, invalid->y, invalid->w, invalid->h);
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
        if (i == 0) { min_x = x; min_y = y; max_x = right; max_y = bottom; }
        else
        {
            if (x < min_x) min_x = x;
            if (y < min_y) min_y = y;
            if (right > max_x) max_x = right;
            if (bottom > max_y) max_y = bottom;
        }
    }
    if (max_x <= min_x || max_y <= min_y) return TRUE;
    wrapper_frame_notify(session, gdi, min_x, min_y, max_x - min_x, max_y - min_y);
    return TRUE;
}

static BOOL on_desktop_resize(rdpContext* context)
{
    if (!context || !context->gdi || !context->settings) return TRUE;
    UINT32 width = freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopWidth);
    UINT32 height = freerdp_settings_get_uint32(context->settings, FreeRDP_DesktopHeight);
    return wrapper_graphics_resize(context, width, height);
}

static void on_graphics_reset(void* context, const GraphicsResetEventArgs* e)
{
    rdpContext* rdp_context = (rdpContext*)context;
    rdp_session* session = session_from_context(rdp_context);
    UINT32 width = e && e->width != 0 ? e->width : freerdp_settings_get_uint32(rdp_context->settings, FreeRDP_DesktopWidth);
    UINT32 height = e && e->height != 0 ? e->height : freerdp_settings_get_uint32(rdp_context->settings, FreeRDP_DesktopHeight);
    if (session && wrapper_graphics_uses_pipeline(session->render_mode))
    {
        if (rdp_context->gdi)
        {
            wrapper_frame_reset_dirty(session, rdp_context->gdi);
            BYTE* primary = rdp_context->gdi->primary_buffer;
            INT32 gdi_w = rdp_context->gdi->width;
            INT32 gdi_h = rdp_context->gdi->height;
            if (session->callback && primary)
            {
                session->callback(session, (uint8_t*)primary, gdi_w, gdi_h, 0, 0, gdi_w, gdi_h, gdi_w * 4);
            }
        }
        return;
    }
    gdi_free(rdp_context->instance);
    if (!gdi_init(rdp_context->instance, gdi_pixel_format(session ? session->render_mode : RENDERING_MODE_CLASSIC_GDI)))
    {
        fprintf(stderr, "Failed to re-initialize GDI\n");
    }
    hook_updates(rdp_context);
    register_pointer_class(rdp_context);
    init_graphics_pipeline(rdp_context);
    wrapper_graphics_resize(rdp_context, width, height);
}

void wrapper_graphics_subscribe_reset(rdpContext* context)
{
    PubSub_SubscribeGraphicsReset(context->pubSub, on_graphics_reset);
}

static BOOL on_surface_bits(rdpContext* context, const SURFACE_BITS_COMMAND* cmd)
{
    (void)context;
    (void)cmd;
    return TRUE;
}
