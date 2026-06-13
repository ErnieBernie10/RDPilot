#pragma once

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
#include <stdbool.h>
#include <stdint.h>
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
    CertificateDecisionCallback certificate_decision_callback;
    freerdp* instance;
    DispClientContext* disp;
    CliprdrClientContext* cliprdr;
    RdpgfxClientContext* gfx;
    HANDLE thread;
    bool running;
    bool connect_succeeded;
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

typedef struct {
    UINT32 code;
    const char* name;
    const char* message;
} connection_error;

rdp_session* session_from_context(rdpContext* context);
void log_channel_rc(const char* operation, UINT rc);

void queue_input_event(rdp_session* session, input_event event);
void process_pending_input(rdp_session* session);

void free_local_clipboard_text(rdp_session* session);
void process_pending_clipboard(rdp_session* session);

UINT on_cliprdr_server_capabilities(CliprdrClientContext* context, const CLIPRDR_CAPABILITIES* capabilities);
UINT on_cliprdr_monitor_ready(CliprdrClientContext* context, const CLIPRDR_MONITOR_READY* monitorReady);
UINT on_cliprdr_server_format_list(CliprdrClientContext* context, const CLIPRDR_FORMAT_LIST* formatList);
UINT on_cliprdr_server_format_data_request(CliprdrClientContext* context, const CLIPRDR_FORMAT_DATA_REQUEST* formatDataRequest);
UINT on_cliprdr_server_format_data_response(CliprdrClientContext* context, const CLIPRDR_FORMAT_DATA_RESPONSE* formatDataResponse);
