#include "freerdp_wrapper_internal.h"

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

/* Frame notification and presentation share the same session-owned lock. The RDP thread
 * publishes dirty geometry; the UI thread copies pixels only while that buffer is alive. */
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

void wrapper_frame_notify(rdp_session* session, rdpGdi* gdi, INT32 dirty_x, INT32 dirty_y, INT32 dirty_width, INT32 dirty_height)
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

/* Called after GDI buffer rebuilds. The lock is recursive on WinPR and callers can already
 * hold frame_lock across gdi_resize: the UI thread must never see freed primary storage. */
void wrapper_frame_reset_dirty(rdp_session* session, rdpGdi* gdi)
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
