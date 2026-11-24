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

void take_pending_input(rdp_session* session, input_batch* batch)
{
    batch->count = 0;
    batch->dropped = 0;
    if (!session) return;

    EnterCriticalSection(&session->input_lock);
    for (UINT32 i = 0; i < session->input_queue_count; i++)
    {
        batch->events[batch->count++] = session->input_queue[i];
    }
    session->input_queue_count = 0;

    batch->dropped = session->input_dropped;
    session->input_dropped = 0;
    LeaveCriticalSection(&session->input_lock);
}

void send_input_batch(rdp_session* session, const input_batch* batch)
{
    if (!session || !session->instance || !session->instance->context || !session->instance->context->input) return;

    for (UINT32 i = 0; i < batch->count; i++)
    {
        const input_event* event = &batch->events[i];
        if (event->type == INPUT_EVENT_MOUSE)
        {
            freerdp_input_send_mouse_event(session->instance->context->input, event->flags, event->x, event->y);
        }
        else
        {
            freerdp_input_send_keyboard_event(session->instance->context->input, event->flags, (UINT8)event->code);
        }
    }

    if (batch->dropped > 0)
    {
        printf("[PERF_INPUT] sent=%u dropped=%u\n", batch->count, batch->dropped);
    }
}

void rdp_session_send_mouse_event(rdp_session* session, uint16_t flags, uint16_t x, uint16_t y) {
    if (!session) return;

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
