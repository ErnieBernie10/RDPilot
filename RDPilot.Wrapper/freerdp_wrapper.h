#pragma once
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
typedef void (*ClipboardTextCallback)(rdp_session* session, const char* text);
typedef void (*StatusCallback)(rdp_session* session, int status, uint32_t error_code, const char* error_name, const char* error_message);
typedef int (*CertificateDecisionCallback)(rdp_session* session, const char* host, uint16_t port, const char* common_name, const char* subject, const char* issuer, const char* fingerprint, int is_changed, const char* previous_subject, const char* previous_issuer, const char* previous_fingerprint);

FREERDP_WRAPPER_API rdp_session* rdp_session_connect(const char* host, const char* connect_host, const char* domain, const char* user, const char* password,
                                                     const char* gateway_host, const char* gateway_domain, const char* gateway_user, const char* gateway_password,
                                                     int width, int height, int color_depth, bool compression, bool font_smoothing, bool bitmap_cache,
                                                     bool desktop_wallpaper, bool themes, bool menu_animations, bool full_window_drag, int connection_type,
                                                     uint32_t keyboard_layout, uint32_t dpi_scale_percent,
                                                     FrameCallback frame_callback, ClipboardTextCallback clipboard_callback, StatusCallback status_callback, CertificateDecisionCallback certificate_decision_callback);
FREERDP_WRAPPER_API void rdp_session_disconnect(rdp_session* session);
FREERDP_WRAPPER_API void rdp_session_free(rdp_session* session);
FREERDP_WRAPPER_API void rdp_session_update_resolution(rdp_session* session, int width, int height, uint32_t dpi_scale_percent);
FREERDP_WRAPPER_API void rdp_session_send_mouse_event(rdp_session* session, uint16_t flags, uint16_t x, uint16_t y);
FREERDP_WRAPPER_API void rdp_session_send_keyboard_event(rdp_session* session, uint16_t flags, uint16_t code);
FREERDP_WRAPPER_API void rdp_session_clipboard_set_local_text(rdp_session* session, const char* text);
FREERDP_WRAPPER_API void rdp_session_clipboard_set_local_files(rdp_session* session, const char** file_paths, size_t file_count);
FREERDP_WRAPPER_API void rdp_session_clipboard_set_local_bitmap(rdp_session* session, const uint8_t* bitmap_data, size_t bitmap_data_size, uint32_t width, uint32_t height);

/* Presents the pending desktop frame, copying dirty pixels from the FreeRDP GDI primary
 * framebuffer into the caller-provided destination buffer. Must be called from the UI/present
 * thread with a locked Avalonia WriteableBitmap backing buffer.
 *
 * Returns true when a present was successfully performed: the pending flag was consumed, the
 * dirty rect was copied from the primary buffer into `dest` (row stride `dest_stride`), and the
 * dirty rect/dims are returned in the out_ parameters. Returns false when there was nothing
 * pending, the GDI was not ready, or the GDI dimensions do not match `dest_width`/`dest_height`
 * (resize race): in the resize case *out_width/*out_height receive the actual GDI dimensions so
 * the caller can recreate its bitmap and retry.
 *
 * The copy happens under `frame_lock`, which is also held around `gdi_resize`, so the primary
 * buffer cannot be freed/reallocatedmid-copy. This is required because the RDP decode thread and
 * the UI present thread run independently (unlike wfreerdp, which serializes both on a single
 * message loop). */
FREERDP_WRAPPER_API bool rdp_session_present(rdp_session* session, uint8_t* dest, int dest_stride, int dest_width, int dest_height, int* out_dx, int* out_dy, int* out_dw, int* out_dh, int* out_width, int* out_height);
