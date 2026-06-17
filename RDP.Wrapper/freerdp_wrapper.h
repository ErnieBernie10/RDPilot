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

typedef void (*FrameCallback)(rdp_session* session, uint8_t* data, int width, int height);
typedef void (*ClipboardTextCallback)(rdp_session* session, const char* text);
typedef void (*StatusCallback)(rdp_session* session, int status, uint32_t error_code, const char* error_name, const char* error_message);
typedef int (*CertificateDecisionCallback)(rdp_session* session, const char* host, uint16_t port, const char* common_name, const char* subject, const char* issuer, const char* fingerprint, int is_changed, const char* previous_subject, const char* previous_issuer, const char* previous_fingerprint);

FREERDP_WRAPPER_API rdp_session* rdp_session_connect(const char* host, const char* connect_host, const char* domain, const char* user, const char* password,
                                                     const char* gateway_host, const char* gateway_domain, const char* gateway_user, const char* gateway_password,
                                                     int width, int height, FrameCallback frame_callback, ClipboardTextCallback clipboard_callback, StatusCallback status_callback, CertificateDecisionCallback certificate_decision_callback);
FREERDP_WRAPPER_API void rdp_session_disconnect(rdp_session* session);
FREERDP_WRAPPER_API void rdp_session_free(rdp_session* session);
FREERDP_WRAPPER_API void rdp_session_update_resolution(rdp_session* session, int width, int height);
FREERDP_WRAPPER_API void rdp_session_send_mouse_event(rdp_session* session, uint16_t flags, uint16_t x, uint16_t y);
FREERDP_WRAPPER_API void rdp_session_send_keyboard_event(rdp_session* session, uint16_t flags, uint16_t code);
FREERDP_WRAPPER_API void rdp_session_clipboard_set_local_text(rdp_session* session, const char* text);
