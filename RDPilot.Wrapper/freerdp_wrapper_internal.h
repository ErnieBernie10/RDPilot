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
#include <freerdp/utils/cliprdr_utils.h>
#include <freerdp/codec/color.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/gdi/gfx.h>
#include <freerdp/graphics.h>
#include <freerdp/pointer.h>
#include <freerdp/settings.h>
#include <freerdp/update.h>
#include <freerdp/utils/gfx.h>
#include <winpr/clipboard.h>
#include <winpr/sysinfo.h>
#include <winpr/input.h>
#include <winpr/stream.h>
#include <winpr/string.h>
#include <winpr/synch.h>
#include <winpr/user.h>
#include <winpr/wtypes.h>
#include <winpr/thread.h>
#if defined(_WIN32)
#include <winpr/winsock.h>
#endif
#include <stdbool.h>
#include <ctype.h>
#include <inttypes.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#if !defined(_WIN32)
#include <errno.h>
#include <unistd.h>
#include <sys/stat.h>
#endif

#define CONNECT_TIMEOUT_MS 3000
#define INPUT_QUEUE_CAPACITY 256
#define INPUT_LOOP_TIMEOUT_MS 10

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

typedef enum {
    RENDERING_MODE_CLASSIC_GDI,
    RENDERING_MODE_GFX_GDI
} rendering_mode;

/* Host-side cap on how many decoded pointer shapes we keep alive at once. FreeRDP's own pointer
 * cache is bounded by FreeRDP_PointerCacheSize, so this is only a backstop against a server that
 * streams non-cached shapes; 384x384 pointers are ~590 KB each. */
#define CURSOR_CACHE_CAPACITY 64

/* Our rdpPointer subclass. `pointer` MUST stay first: FreeRDP allocates `pointer.size` bytes and
 * hands back the address as an rdpPointer*. Instances live from Pointer_New until Pointer_Free and
 * are reachable from the session's cursor_list so the UI thread can pull pixels by id. */
#define REMOTE_REQUEST_QUEUE_CAPACITY 16

typedef enum {
    REMOTE_REQUEST_TEXT,
    REMOTE_REQUEST_FILE_DESCRIPTOR
} remote_request_kind;

typedef struct {
    remote_request_kind kind;
    UINT32 format_id;
    UINT32 generation;
} remote_request;

#define FILE_CONTENTS_REQUEST_CAPACITY 64

typedef struct {
    UINT32 generation;
    UINT32 stream_id;
    UINT32 list_index;
    bool size_only;
    UINT64 offset;
    UINT32 size;
} file_contents_request;

typedef struct wrapper_pointer {
    rdpPointer pointer;
    UINT32 id;
    UINT32 width;
    UINT32 height;
    UINT32 hot_x;
    UINT32 hot_y;
    BYTE* bgra; /* width*height*4 PIXEL_FORMAT_BGRA32, straight alpha; NULL if conversion failed */
    struct wrapper_pointer* next;
} wrapper_pointer;

struct rdp_session {
    FrameCallback callback;
    ClipboardTextCallback clipboard_text_callback;
    ClipboardFilesCallback clipboard_files_callback;
    ClipboardFileContentsCallback clipboard_file_contents_callback;
    StatusCallback status_callback;
    CertificateDecisionCallback certificate_decision_callback;
    CursorCallback cursor_callback;
    freerdp* instance;
    DispClientContext* disp;
    CliprdrClientContext* cliprdr;
    RdpgfxClientContext* gfx;
    HANDLE thread;
    bool running;
    bool winsock_initialized;
    bool connect_succeeded;
    rendering_mode render_mode;
    bool disp_ready;
    UINT32 last_sent_width;
    UINT32 last_sent_height;
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
    ULONGLONG perf_loop_last_log_tick;
    UINT32 perf_loop_count;
    UINT32 perf_loop_slow_count;
    UINT32 perf_loop_max_total_ms;
    UINT32 perf_loop_max_input_ms;
    UINT32 perf_loop_max_clipboard_ms;
    UINT32 perf_loop_max_check_fds_ms;
    UINT32 perf_loop_max_resize_ms;
    CRITICAL_SECTION input_lock;
    input_event input_queue[INPUT_QUEUE_CAPACITY];
    UINT32 input_queue_count;
    UINT32 input_dropped;
    /* Both pre-normalized by C#: desktop_scale_percent is the true desktop scale (100-500),
     * device_scale_percent is one of 100/140/180. See RdpSessionOptions. */
    UINT32 desktop_scale_percent;
    UINT32 device_scale_percent;
    CRITICAL_SECTION clipboard_lock;
    char* local_clipboard_text;
    bool clipboard_format_pending;
    
    // Advanced clipboard support
    UINT32* supported_local_formats;
    size_t supported_local_formats_count;
    size_t supported_local_formats_capacity;
    
    // File clipboard support
    wClipboard* file_clipboard;
    UINT32 file_group_descriptor_format_id;
    UINT32 file_contents_format_id;
    char** local_file_paths;
    size_t local_file_paths_count;
    size_t local_file_paths_capacity;
    UINT32 remote_file_group_descriptor_format_id;
    UINT32 remote_file_contents_format_id;
    // Outstanding remote format data requests. CLIPRDR responses carry no request id; the server
    // answers requests in order, so responses are matched FIFO. `generation` is bumped on every
    // server format list so answers to requests made for an older list can be told apart and
    // dropped. RDP-thread only (cliprdr callbacks and the RDP loop), so no lock.
    remote_request remote_requests[REMOTE_REQUEST_QUEUE_CAPACITY];
    size_t remote_requests_head;
    size_t remote_requests_count;
    UINT32 remote_generation;
    bool remote_offers_text;
    bool remote_retry_pending;
    remote_request remote_retry;
    UINT32 remote_retry_attempt;
    ULONGLONG remote_retry_due_ms;
    // Host requests for remote file contents: queued under clipboard_lock from any thread, sent
    // by the RDP thread. Sent requests wait in `outstanding` (RDP thread only) until the server
    // answers; responses are matched by stream id.
    file_contents_request file_contents_queue[FILE_CONTENTS_REQUEST_CAPACITY];
    size_t file_contents_queue_count;
    file_contents_request file_contents_outstanding[FILE_CONTENTS_REQUEST_CAPACITY];
    size_t file_contents_outstanding_count;
    
    // Bitmap clipboard support
    BYTE* local_bitmap_data;
    size_t local_bitmap_data_size;
    UINT32 local_bitmap_width;
    UINT32 local_bitmap_height;
    CRITICAL_SECTION frame_lock;
    volatile LONG pending_frame;
    INT32 pending_dirty_x;
    INT32 pending_dirty_y;
    INT32 pending_dirty_w;
    INT32 pending_dirty_h;

    // Remote cursor support. The RDP thread owns cursor_list; the UI thread only reads it through
    // rdp_session_copy_cursor_image. Both take cursor_lock.
    CRITICAL_SECTION cursor_lock;
    wrapper_pointer* cursor_list;
    UINT32 cursor_list_count;
    UINT32 next_cursor_id;
};

typedef struct {
    rdpContext context;
    rdp_session* session;
} wrapper_context;

/* The RDP thread's own copy of the caller's config; its strings are owned and freed with it. */
typedef struct {
    rdp_session* session;
    rdp_session_config config;
} connection_params;

void free_connection_params(connection_params* params);

typedef struct {
    UINT32 code;
    const char* name;
    const char* message;
} connection_error;

rdp_session* session_from_context(rdpContext* context);
void log_channel_rc(const char* operation, UINT rc);
void wrapper_frame_notify(rdp_session* session, rdpGdi* gdi, INT32 x, INT32 y, INT32 width, INT32 height);
void wrapper_frame_reset_dirty(rdp_session* session, rdpGdi* gdi);

void queue_input_event(rdp_session* session, input_event event);
void process_pending_input(rdp_session* session);

void register_pointer_class(rdpContext* context);
void free_cursor_cache(rdp_session* session);

void free_local_clipboard_text(rdp_session* session);
void free_clipboard_data(rdp_session* session);
/* Returns true when it queued a cliprdr PDU (a local format list or a retried request). */
bool process_pending_clipboard(rdp_session* session);
void wrapper_clipboard_attach(rdp_session* session, CliprdrClientContext* cliprdr);
void wrapper_clipboard_detach(rdp_session* session);
DWORD WINAPI wrapper_connection_thread(LPVOID params);

char* duplicate_string(const char* text);

UINT on_cliprdr_server_capabilities(CliprdrClientContext* context, const CLIPRDR_CAPABILITIES* capabilities);
UINT on_cliprdr_monitor_ready(CliprdrClientContext* context, const CLIPRDR_MONITOR_READY* monitorReady);
UINT on_cliprdr_server_format_list(CliprdrClientContext* context, const CLIPRDR_FORMAT_LIST* formatList);
UINT on_cliprdr_server_format_data_request(CliprdrClientContext* context, const CLIPRDR_FORMAT_DATA_REQUEST* formatDataRequest);
UINT on_cliprdr_server_file_contents_request(CliprdrClientContext* context, const CLIPRDR_FILE_CONTENTS_REQUEST* fileContentsRequest);
UINT on_cliprdr_server_format_data_response(CliprdrClientContext* context, const CLIPRDR_FORMAT_DATA_RESPONSE* formatDataResponse);
UINT on_cliprdr_server_file_contents_response(CliprdrClientContext* context, const CLIPRDR_FILE_CONTENTS_RESPONSE* fileContentsResponse);
