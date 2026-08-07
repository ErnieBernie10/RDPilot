#pragma once
#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#if defined(_WIN32)
#if defined(FREERDP_WRAPPER_EXPORTS)
#define FREERDP_WRAPPER_API __declspec(dllexport)
#else
#define FREERDP_WRAPPER_API __declspec(dllimport)
#endif
#else
#define FREERDP_WRAPPER_API
#endif

typedef struct rdp_session rdp_session;

/* FrameCallback is invoked on the RDP thread when a logical desktop frame becomes presentable.
 * The `data` pointer aliases the live FreeRDP GDI primary framebuffer for the duration of the
 * call only and MUST NOT be retained, dereferenced, or used to copy pixels after the callback
 * returns. The host should only record the dirty rect/dims and post a UI-thread present; the UI
 * thread then calls rdp_session_present, which atomically copies the dirty region out of the
 * live primary buffer under frame_lock. No pixel copy should happen on this RDP thread; doing so
 * would stall the decode loop and (since the buffer lifetime is unrelated to FreeRDP's decode
 * thread) race gdi_resize. See wfreerdp's wf_end_paint for the reference single-threaded model. */
typedef void (*FrameCallback)(rdp_session* session, uint8_t* data, int width, int height, int dirty_x, int dirty_y, int dirty_width, int dirty_height, int source_stride);

/* Which cursor the remote session currently wants shown over the desktop. */
typedef enum {
    RDP_CURSOR_HIDDEN = 0,  /* server asked for no pointer at all */
    RDP_CURSOR_DEFAULT = 1, /* server asked for the platform default arrow */
    RDP_CURSOR_BITMAP = 2   /* server supplied a shape; pull it with rdp_session_copy_cursor_image */
} rdp_cursor_kind;

/* CursorCallback is invoked on the RDP thread whenever the active pointer changes. Unlike
 * FrameCallback it carries no pixel pointer, so there is no aliasing hazard, but it is subject to
 * the same latency constraint: it runs inside the FreeRDP event loop and must not block. The host
 * should record the descriptor, coalesce, and post to its UI thread; the UI thread then pulls the
 * pixels for `cursor_id` with rdp_session_copy_cursor_image. `cursor_id` is unique for the lifetime
 * of the session, so a host-side cache keyed on it never needs invalidating; ids for shapes the
 * server has evicted from its pointer cache simply stop resolving. For RDP_CURSOR_HIDDEN and
 * RDP_CURSOR_DEFAULT every field after `kind` is zero. */
typedef void (*CursorCallback)(rdp_session* session, int kind, uint32_t cursor_id, int width, int height, int hot_x, int hot_y);
typedef void (*ClipboardTextCallback)(rdp_session* session, const char* text);
typedef void (*ClipboardFilesCallback)(rdp_session* session, const char** file_paths, size_t file_count);
typedef void (*StatusCallback)(rdp_session* session, int status, uint32_t error_code, const char* error_name, const char* error_message);
typedef int (*CertificateDecisionCallback)(rdp_session* session, const char* host, uint16_t port, const char* common_name, const char* subject, const char* issuer, const char* fingerprint, int is_changed, const char* previous_subject, const char* previous_issuer, const char* previous_fingerprint);

FREERDP_WRAPPER_API rdp_session* rdp_session_connect(const char* host, const char* connect_host, uint16_t port, const char* domain, const char* user, const char* password,
                                                     const char* gateway_host, const char* gateway_domain, const char* gateway_user, const char* gateway_password,
                                                     int width, int height, int color_depth, bool compression, bool font_smoothing, bool bitmap_cache,
                                                     bool desktop_wallpaper, bool themes, bool menu_animations, bool full_window_drag, int connection_type, bool network_auto_detect,
                                                     uint32_t keyboard_layout, uint32_t dpi_scale_percent,
                                                     FrameCallback frame_callback, ClipboardTextCallback clipboard_text_callback, ClipboardFilesCallback clipboard_files_callback, StatusCallback status_callback, CertificateDecisionCallback certificate_decision_callback,
                                                     CursorCallback cursor_callback);
FREERDP_WRAPPER_API void rdp_session_disconnect(rdp_session* session);
FREERDP_WRAPPER_API void rdp_session_free(rdp_session* session);
FREERDP_WRAPPER_API void rdp_session_update_resolution(rdp_session* session, int width, int height, uint32_t dpi_scale_percent);
FREERDP_WRAPPER_API void rdp_session_send_mouse_event(rdp_session* session, uint16_t flags, uint16_t x, uint16_t y);
FREERDP_WRAPPER_API void rdp_session_send_keyboard_event(rdp_session* session, uint16_t flags, uint16_t code);
FREERDP_WRAPPER_API void rdp_session_clipboard_set_local_text(rdp_session* session, const char* text);
FREERDP_WRAPPER_API void rdp_session_clipboard_clear_local_files(rdp_session* session);
FREERDP_WRAPPER_API void rdp_session_clipboard_add_local_file(rdp_session* session, const char* file_path);
FREERDP_WRAPPER_API void rdp_session_clipboard_commit_local_files(rdp_session* session);
FREERDP_WRAPPER_API void rdp_session_clipboard_set_local_files(rdp_session* session, const char** file_paths, size_t file_count);
FREERDP_WRAPPER_API void rdp_session_clipboard_set_local_bitmap(rdp_session* session, const uint8_t* bitmap_data, size_t bitmap_data_size, uint32_t width, uint32_t height);
/* Marks the current GDI primary framebuffer for a full copy on the next present. This is used
 * when a suspended UI presenter is recreated after its bitmap was released. */
FREERDP_WRAPPER_API void rdp_session_request_full_frame(rdp_session* session);

/* Presents the pending desktop frame, copying dirty pixels from the FreeRDP GDI primary
 * framebuffer into the caller-provided destination buffer. Must be called from the UI/present
 * thread with a locked Avalonia WriteableBitmap backing buffer.
 *
 * Returns true when a present was successfully performed: the pending flag was consumed, the
 * dirty rect was copied from the primary buffer into `dest` (row stride `dest_stride`), and the
 * dirty rect/dims are returned in the out_ parameters. Returns false when there was nothing
 * pending, the GDI was not ready, or the GDI dimensions do not match `dest_width`/`dest_height`
 * (resize race): in the resize case out_width / out_height receive the actual GDI dimensions so
 * the caller can recreate its bitmap and retry.
 *
 * The copy happens under `frame_lock`, which is also held around `gdi_resize`, so the primary
 * buffer cannot be freed/reallocatedmid-copy. This is required because the RDP decode thread and
 * the UI present thread run independently (unlike wfreerdp, which serializes both on a single
 * message loop). */
FREERDP_WRAPPER_API bool rdp_session_present(rdp_session* session, uint8_t* dest, int dest_stride, int dest_width, int dest_height, int* out_dx, int* out_dy, int* out_dw, int* out_dh, int* out_width, int* out_height);

/* Copies the decoded BGRA32 image for `cursor_id` (as reported by CursorCallback) into the
 * caller-provided destination buffer. Intended to be called from the UI thread with a locked
 * bitmap backing buffer, mirroring rdp_session_present.
 *
 * The pixels are straight (non-premultiplied) alpha, matching what FreeRDP's
 * freerdp_image_copy_from_pointer_data produces from the AND/XOR masks. Note this differs from
 * the desktop framebuffer, which is opaque BGRX.
 *
 * Returns false when the id is unknown - the RDP thread frees pointers when the server evicts
 * them from its cache, so a shape can disappear between the callback and this call - or when
 * `dest_width`/`dest_height` do not match the reported size. In both cases the caller should keep
 * whatever cursor it is already showing. The lookup and copy happen under the same lock the RDP
 * thread holds while adding/removing pointers, so the source buffer cannot be freed mid-copy. */
FREERDP_WRAPPER_API bool rdp_session_copy_cursor_image(rdp_session* session, uint32_t cursor_id, uint8_t* dest, int dest_stride, int dest_width, int dest_height);
