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
#include <freerdp/gdi/gdi.h>
#include <freerdp/gdi/gfx.h>
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

#define RESIZE_QUIET_DELAY_MS 1000
#define RESIZE_MIN_DELAY_MS 1500
#define CONNECT_TIMEOUT_MS 3000
#define DEFAULT_RDP_PORT 3389
#define INPUT_QUEUE_CAPACITY 256
#define PTR_FLAGS_MOVE 0x0800
#define INPUT_LOOP_TIMEOUT_MS 10
#define MIN_MOVE_SEND_INTERVAL_MS 8

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

struct rdp_session {
    FrameCallback callback;
    ClipboardTextCallback clipboard_text_callback;
    ClipboardFilesCallback clipboard_files_callback;
    StatusCallback status_callback;
    CertificateDecisionCallback certificate_decision_callback;
    freerdp* instance;
    DispClientContext* disp;
    CliprdrClientContext* cliprdr;
    RdpgfxClientContext* gfx;
    HANDLE thread;
    bool running;
    bool connect_succeeded;
    rendering_mode render_mode;
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
    bool pending_mouse_move;
    uint16_t pending_mouse_x;
    uint16_t pending_mouse_y;
    UINT32 input_keyboard_log_count;
    UINT32 input_dropped;
    UINT32 input_mouse_moves_coalesced;
    ULONGLONG last_move_send_tick;
    UINT32 input_move_throttled;
    UINT32 dpi_scale_percent;
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
    char* temp_directory;
    UINT32 remote_file_group_descriptor_format_id;
    UINT32 remote_file_contents_format_id;
    UINT32 pending_remote_format_id;
    UINT32 remote_file_stream_id;
    char** remote_received_file_paths;
    size_t remote_received_file_paths_count;
    size_t remote_received_file_paths_capacity;
    size_t remote_expected_file_count;
    size_t remote_active_file_index;
    UINT64 remote_active_file_size;
    UINT64 remote_active_file_offset;
    char* remote_active_file_path;
    FILE* remote_active_file;
    bool remote_file_transfer_in_progress;
    
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
};

typedef struct {
    rdpContext context;
    rdp_session* session;
} wrapper_context;

typedef struct {
    rdp_session* session;
    char host[256];
    char connect_host[256];
    char domain[256];
    char user[256];
    char password[256];
    char gateway_host[256];
    char gateway_domain[256];
    char gateway_user[256];
    char gateway_password[256];
    int width;
    int height;
    int color_depth;
    bool compression;
    bool font_smoothing;
    bool bitmap_cache;
    bool desktop_wallpaper;
    bool themes;
    bool menu_animations;
    bool full_window_drag;
    int connection_type;
    UINT32 keyboard_layout;
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
void free_clipboard_data(rdp_session* session);
void process_pending_clipboard(rdp_session* session);

char* duplicate_string(const char* text);

UINT on_cliprdr_server_capabilities(CliprdrClientContext* context, const CLIPRDR_CAPABILITIES* capabilities);
UINT on_cliprdr_monitor_ready(CliprdrClientContext* context, const CLIPRDR_MONITOR_READY* monitorReady);
UINT on_cliprdr_server_format_list(CliprdrClientContext* context, const CLIPRDR_FORMAT_LIST* formatList);
UINT on_cliprdr_server_format_data_request(CliprdrClientContext* context, const CLIPRDR_FORMAT_DATA_REQUEST* formatDataRequest);
UINT on_cliprdr_server_file_contents_request(CliprdrClientContext* context, const CLIPRDR_FILE_CONTENTS_REQUEST* fileContentsRequest);
UINT on_cliprdr_server_format_data_response(CliprdrClientContext* context, const CLIPRDR_FORMAT_DATA_RESPONSE* formatDataResponse);
UINT on_cliprdr_server_file_contents_response(CliprdrClientContext* context, const CLIPRDR_FILE_CONTENTS_RESPONSE* fileContentsResponse);
