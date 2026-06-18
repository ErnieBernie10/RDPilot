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
