#include "freerdp_wrapper_internal.h"

void queue_input_event(rdp_session* session, input_event event)
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

void process_pending_input(rdp_session* session)
{
    if (!session || !session->instance || !session->instance->context || !session->instance->context->input) return;

    input_event events[INPUT_QUEUE_CAPACITY + 1];
    UINT32 event_count = 0;
    UINT32 dropped = 0;
    UINT32 coalesced = 0;
    bool move_throttled = false;

    EnterCriticalSection(&session->input_lock);
    if (session->pending_mouse_move)
    {
        // Throttle pure mouse moves to ~125 Hz (8ms). RDPGFX frame scheduling on the server gates
        // on input quiescence; flooding >300 Hz (busy-poll style) made the server pace down to
        // 1-12 fps during drag. mstsc/wfreerdp deliver moves at the OS/UI rate (~60-125 Hz).
        ULONGLONG now = GetTickCount64();
        if (session->last_move_send_tick == 0 ||
            now - session->last_move_send_tick >= MIN_MOVE_SEND_INTERVAL_MS)
        {
            events[event_count++] = (input_event){
                .type = INPUT_EVENT_MOUSE,
                .flags = PTR_FLAGS_MOVE,
                .x = session->pending_mouse_x,
                .y = session->pending_mouse_y,
            };
            session->pending_mouse_move = false;
            session->last_move_send_tick = now;
        }
        else
        {
            // Keep the pending move (latest position) for the next iteration; record throttle.
            move_throttled = true;
            session->input_move_throttled++;
        }
    }

    for (UINT32 i = 0; i < session->input_queue_count; i++)
    {
        events[event_count++] = session->input_queue[i];
    }
    session->input_queue_count = 0;

    dropped = session->input_dropped;
    coalesced = session->input_mouse_moves_coalesced;
    UINT32 throttled = session->input_move_throttled;
    session->input_dropped = 0;
    session->input_mouse_moves_coalesced = 0;
    session->input_move_throttled = 0;
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
            if (session->input_keyboard_log_count < 32)
            {
                session->input_keyboard_log_count++;
                printf("[KEYBOARD] phase=native-send flags=0x%04X code=0x%02X extended=%s release=%s\n",
                       event->flags,
                       event->code,
                       (event->flags & 0x0100) ? "true" : "false",
                       (event->flags & 0x8000) ? "true" : "false");
            }
            freerdp_input_send_keyboard_event(session->instance->context->input, event->flags, event->code);
        }
    }

    if (dropped > 0 || coalesced > 1000 || throttled > 1000)
    {
        printf("[PERF_INPUT] sent=%u coalescedMouseMoves=%u throttledMoves=%u dropped=%u\n",
               event_count, coalesced, throttled, dropped);
    }
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
