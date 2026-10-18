#include "freerdp_wrapper_display.h"
#include "freerdp_wrapper_graphics.h"

static UINT32 physical_size_mm(UINT32 pixels, UINT32 scale_percent)
{
    UINT32 dpi = 96u * (scale_percent == 0 ? 100 : scale_percent) / 100u;
    if (dpi == 0) dpi = 96;
    UINT32 mm = (pixels * 254u + dpi * 5u) / (dpi * 10u);
    if (mm < DISPLAY_CONTROL_MIN_PHYSICAL_MONITOR_WIDTH) return DISPLAY_CONTROL_MIN_PHYSICAL_MONITOR_WIDTH;
    if (mm > DISPLAY_CONTROL_MAX_PHYSICAL_MONITOR_WIDTH) return DISPLAY_CONTROL_MAX_PHYSICAL_MONITOR_WIDTH;
    return mm;
}

static UINT on_display_control_caps(DispClientContext* context, UINT32 maxNumMonitors,
                                    UINT32 maxMonitorAreaFactorA, UINT32 maxMonitorAreaFactorB)
{
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;
    if (session) session->disp_ready = true;
    (void)maxNumMonitors;
    (void)maxMonitorAreaFactorA;
    (void)maxMonitorAreaFactorB;
    return CHANNEL_RC_OK;
}

void wrapper_display_attach(rdp_session* session, DispClientContext* disp)
{
    if (!session) return;
    session->disp = disp;
    session->disp_ready = false;
    if (disp)
    {
        disp->custom = session;
        disp->DisplayControlCaps = on_display_control_caps;
    }
}

void wrapper_display_detach(rdp_session* session)
{
    if (!session) return;
    session->disp = NULL;
    session->disp_ready = false;
}

void wrapper_display_configure(rdp_session* session, rdpSettings* settings, UINT32 width, UINT32 height)
{
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopScaleFactor, session->dpi_scale_percent);
    freerdp_settings_set_uint32(settings, FreeRDP_DeviceScaleFactor, session->device_scale_percent);
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, width);
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopHeight, height);
    session->last_sent_width = width;
    session->last_sent_height = height;
    freerdp_settings_set_uint32(settings, FreeRDP_MonitorCount, 1);
    freerdp_settings_set_bool(settings, FreeRDP_UseMultimon, FALSE);

    rdpMonitor* monitors = calloc(1, sizeof(rdpMonitor));
    monitors[0].x = 0;
    monitors[0].y = 0;
    monitors[0].width = width;
    monitors[0].height = height;
    monitors[0].attributes.physicalWidth = physical_size_mm(width, session->dpi_scale_percent);
    monitors[0].attributes.physicalHeight = physical_size_mm(height, session->dpi_scale_percent);
    monitors[0].attributes.orientation = ORIENTATION_LANDSCAPE;
    monitors[0].attributes.desktopScaleFactor = session->dpi_scale_percent;
    monitors[0].attributes.deviceScaleFactor = session->device_scale_percent;
    monitors[0].is_primary = TRUE;
    freerdp_settings_set_pointer_len(settings, FreeRDP_MonitorDefArray, monitors, 1);
    freerdp_settings_set_uint32(settings, FreeRDP_MonitorCount, 1);
    free(monitors);
}

bool wrapper_display_process_pending(rdp_session* session)
{
    if (!session || !session->instance || !session->instance->context || !session->disp || !session->disp_ready) return true;
    UINT32 width = 0;
    UINT32 height = 0;
    EnterCriticalSection(&session->resize_lock);
    bool pending = session->resize_pending;
    if (pending) { width = session->target_width; height = session->target_height; }
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
    layout.PhysicalWidth = physical_size_mm(width, session->dpi_scale_percent);
    layout.PhysicalHeight = physical_size_mm(height, session->dpi_scale_percent);
    layout.Orientation = ORIENTATION_LANDSCAPE;
    layout.DesktopScaleFactor = session->dpi_scale_percent;
    layout.DeviceScaleFactor = session->device_scale_percent;
    UINT rc = session->disp->SendMonitorLayout(session->disp, 1, &layout);
    if (rc != CHANNEL_RC_OK)
    {
        fprintf(stderr, "Failed to send monitor layout update %ux%u, rc=%u\n", width, height, rc);
        return true;
    }
    session->last_sent_width = width;
    session->last_sent_height = height;
    if (!wrapper_graphics_uses_pipeline(session->render_mode) && !wrapper_graphics_resize(session->instance->context, width, height)) return true;
    EnterCriticalSection(&session->resize_lock);
    if (session->target_width == width && session->target_height == height) session->resize_pending = false;
    LeaveCriticalSection(&session->resize_lock);
    return true;
}

void rdp_session_update_resolution(rdp_session* session, int width, int height, uint32_t dpi_scale_percent)
{
    if (!session || width <= 0 || height <= 0) return;
    /* DPI stays locked at connect time; managed debounce chooses when this target arrives. */
    (void)dpi_scale_percent;
    EnterCriticalSection(&session->resize_lock);
    if ((UINT32)width != session->target_width || (UINT32)height != session->target_height)
    {
        session->target_width = (UINT32)width;
        session->target_height = (UINT32)height;
        session->resize_pending = true;
    }
    LeaveCriticalSection(&session->resize_lock);
}
