#include "freerdp_wrapper_internal.h"

/* Remote cursor support.
 *
 * FreeRDP does not composite the mouse pointer into the GDI primary framebuffer, so the desktop
 * pixels the host presents never contain a cursor. Instead the core decodes pointer PDUs and drives
 * a client-supplied rdpPointer class. This file is that class: it converts each new shape to BGRA32
 * once, keeps it addressable by a session-unique id, and tells the host (via CursorCallback) which
 * id is currently active. The host pulls the pixels on its own thread with
 * rdp_session_copy_cursor_image and turns them into a real OS cursor.
 *
 * Threading mirrors the frame path: everything here except rdp_session_copy_cursor_image runs on
 * the RDP thread, callbacks are fired outside cursor_lock, and the lock is only ever held around
 * list mutation and the pixel copy.
 */

static wrapper_pointer* find_cursor_locked(rdp_session* session, UINT32 id)
{
    for (wrapper_pointer* entry = session->cursor_list; entry; entry = entry->next)
    {
        if (entry->id == id) return entry;
    }
    return NULL;
}

/* Drops the oldest entries until the list is back within CURSOR_CACHE_CAPACITY. Only reachable if a
 * server streams shapes without ever letting FreeRDP free them; the evicted images simply stop
 * resolving for the host, which then keeps its current cursor. Caller holds cursor_lock. */
static void trim_cursor_list_locked(rdp_session* session)
{
    while (session->cursor_list_count > CURSOR_CACHE_CAPACITY)
    {
        wrapper_pointer** link = &session->cursor_list;
        while ((*link)->next)
        {
            link = &(*link)->next;
        }

        wrapper_pointer* oldest = *link;
        *link = NULL;
        session->cursor_list_count--;
        free(oldest->bgra);
        oldest->bgra = NULL;
    }
}

static void emit_cursor(rdp_session* session, int kind, UINT32 id, UINT32 width, UINT32 height, UINT32 hot_x, UINT32 hot_y)
{
    if (!session || !session->cursor_callback) return;
    session->cursor_callback(session, kind, id, (int)width, (int)height, (int)hot_x, (int)hot_y);
}

static BOOL wrapper_pointer_new(rdpContext* context, rdpPointer* pointer)
{
    rdp_session* session = session_from_context(context);
    wrapper_pointer* entry = (wrapper_pointer*)pointer;
    if (!session || !entry) return FALSE;

    entry->width = pointer->width;
    entry->height = pointer->height;
    entry->hot_x = pointer->xPos;
    entry->hot_y = pointer->yPos;
    entry->bgra = NULL;
    entry->next = NULL;

    if (entry->width == 0 || entry->height == 0) return TRUE;

    size_t stride = (size_t)entry->width * 4u;
    BYTE* bgra = calloc((size_t)entry->height, stride);
    if (!bgra) return TRUE;

    /* Straight (non-premultiplied) alpha; the host bitmap must be created accordingly. */
    if (!freerdp_image_copy_from_pointer_data(bgra, PIXEL_FORMAT_BGRA32, (UINT32)stride, 0, 0,
                                              entry->width, entry->height,
                                              pointer->xorMaskData, pointer->lengthXorMask,
                                              pointer->andMaskData, pointer->lengthAndMask,
                                              pointer->xorBpp,
                                              context->gdi ? &context->gdi->palette : NULL))
    {
        /* A shape we cannot decode is not worth tearing the session down for: leave bgra NULL and
         * let Set fall back to the platform default arrow. */
        free(bgra);
        return TRUE;
    }

    EnterCriticalSection(&session->cursor_lock);
    entry->bgra = bgra;
    entry->id = ++session->next_cursor_id;
    entry->next = session->cursor_list;
    session->cursor_list = entry;
    session->cursor_list_count++;
    trim_cursor_list_locked(session);
    LeaveCriticalSection(&session->cursor_lock);

    return TRUE;
}

static void wrapper_pointer_free(rdpContext* context, rdpPointer* pointer)
{
    rdp_session* session = session_from_context(context);
    wrapper_pointer* entry = (wrapper_pointer*)pointer;
    if (!session || !entry) return;

    EnterCriticalSection(&session->cursor_lock);
    wrapper_pointer** link = &session->cursor_list;
    while (*link)
    {
        if (*link == entry)
        {
            *link = entry->next;
            session->cursor_list_count--;
            break;
        }
        link = &(*link)->next;
    }
    entry->next = NULL;
    free(entry->bgra);
    entry->bgra = NULL;
    LeaveCriticalSection(&session->cursor_lock);
}

static BOOL wrapper_pointer_set(rdpContext* context, rdpPointer* pointer)
{
    rdp_session* session = session_from_context(context);
    wrapper_pointer* entry = (wrapper_pointer*)pointer;
    if (!session || !entry) return FALSE;

    EnterCriticalSection(&session->cursor_lock);
    BOOL has_image = entry->bgra != NULL;
    UINT32 id = entry->id;
    UINT32 width = entry->width;
    UINT32 height = entry->height;
    UINT32 hot_x = entry->hot_x;
    UINT32 hot_y = entry->hot_y;
    LeaveCriticalSection(&session->cursor_lock);

    if (has_image)
    {
        emit_cursor(session, RDP_CURSOR_BITMAP, id, width, height, hot_x, hot_y);
    }
    else
    {
        emit_cursor(session, RDP_CURSOR_DEFAULT, 0, 0, 0, 0, 0);
    }

    return TRUE;
}

static BOOL wrapper_pointer_set_null(rdpContext* context)
{
    emit_cursor(session_from_context(context), RDP_CURSOR_HIDDEN, 0, 0, 0, 0, 0);
    return TRUE;
}

static BOOL wrapper_pointer_set_default(rdpContext* context)
{
    emit_cursor(session_from_context(context), RDP_CURSOR_DEFAULT, 0, 0, 0, 0, 0);
    return TRUE;
}

/* Intentional no-op. This fires when a remote app calls SetCursorPos, and honouring it would mean
 * warping the user's physical mouse out from under them - there is no cross-platform Avalonia API
 * for that, it fights local mouse movement, and it has to be suppressed whenever the window is not
 * focused. RDPilot renders remote cursor shape and visibility only; do not "fix" this. */
static BOOL wrapper_pointer_set_position(rdpContext* context, UINT32 x, UINT32 y)
{
    (void)context;
    (void)x;
    (void)y;
    return TRUE;
}

void register_pointer_class(rdpContext* context)
{
    if (!context || !context->graphics) return;

    rdpPointer prototype = { 0 };
    /* FreeRDP allocates `size` bytes per pointer, so this is what makes the extra wrapper_pointer
     * fields legal to touch in the callbacks above. */
    prototype.size = sizeof(wrapper_pointer);
    prototype.New = wrapper_pointer_new;
    prototype.Free = wrapper_pointer_free;
    prototype.Set = wrapper_pointer_set;
    prototype.SetNull = wrapper_pointer_set_null;
    prototype.SetDefault = wrapper_pointer_set_default;
    prototype.SetPosition = wrapper_pointer_set_position;

    graphics_register_pointer(context->graphics, &prototype);
}

/* Called from rdp_session_free after the RDP thread has stopped. FreeRDP frees the wrapper_pointer
 * allocations themselves during context teardown; this only releases the decoded images for any
 * entries still listed. */
void free_cursor_cache(rdp_session* session)
{
    if (!session) return;

    EnterCriticalSection(&session->cursor_lock);
    wrapper_pointer* entry = session->cursor_list;
    while (entry)
    {
        wrapper_pointer* next = entry->next;
        free(entry->bgra);
        entry->bgra = NULL;
        entry->next = NULL;
        entry = next;
    }
    session->cursor_list = NULL;
    session->cursor_list_count = 0;
    LeaveCriticalSection(&session->cursor_lock);
}

bool rdp_session_copy_cursor_image(rdp_session* session, uint32_t cursor_id, uint8_t* dest, int dest_stride, int dest_width, int dest_height)
{
    if (!session || !dest || dest_stride <= 0 || dest_width <= 0 || dest_height <= 0) return false;

    EnterCriticalSection(&session->cursor_lock);

    wrapper_pointer* entry = find_cursor_locked(session, cursor_id);
    if (!entry || !entry->bgra || entry->width != (UINT32)dest_width || entry->height != (UINT32)dest_height)
    {
        LeaveCriticalSection(&session->cursor_lock);
        return false;
    }

    size_t src_stride = (size_t)entry->width * 4u;
    if ((size_t)dest_stride < src_stride)
    {
        LeaveCriticalSection(&session->cursor_lock);
        return false;
    }

    for (UINT32 row = 0; row < entry->height; row++)
    {
        memcpy(dest + (size_t)row * (size_t)dest_stride, entry->bgra + (size_t)row * src_stride, src_stride);
    }

    LeaveCriticalSection(&session->cursor_lock);
    return true;
}
