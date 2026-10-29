#include "freerdp_wrapper_internal.h"
#include "freerdp_wrapper_graphics.h"

rdp_session* session_from_context(rdpContext* context)
{
    if (!context) return NULL;
    return ((wrapper_context*)context)->session;
}

void log_channel_rc(const char* operation, UINT rc)
{
    if (rc != CHANNEL_RC_OK)
    {
        fprintf(stderr, "[CLIPRDR] %s failed rc=%u\n", operation, rc);
    }
}

char* duplicate_string(const char* text)
{
    if (!text) return NULL;
    size_t length = strlen(text) + 1;
    char* copy = malloc(length);
    if (copy) memcpy(copy, text, length);
    return copy;
}

static bool copy_config_string(const char** dest, const char* src)
{
    *dest = NULL;
    if (!src) return true;
    *dest = duplicate_string(src);
    return *dest != NULL;
}

static connection_params* copy_connection_params(rdp_session* session, const rdp_session_config* config)
{
    connection_params* params = calloc(1, sizeof(connection_params));
    if (!params) return NULL;
    params->session = session;
    params->config = *config;
    params->config.size = sizeof(rdp_session_config);
    rdp_session_config* copy = &params->config;
    bool ok = copy_config_string(&copy->host, config->host);
    ok = copy_config_string(&copy->connect_host, config->connect_host) && ok;
    ok = copy_config_string(&copy->domain, config->domain) && ok;
    ok = copy_config_string(&copy->user, config->user) && ok;
    ok = copy_config_string(&copy->password, config->password) && ok;
    ok = copy_config_string(&copy->gateway_host, config->gateway_host) && ok;
    ok = copy_config_string(&copy->gateway_domain, config->gateway_domain) && ok;
    ok = copy_config_string(&copy->gateway_user, config->gateway_user) && ok;
    ok = copy_config_string(&copy->gateway_password, config->gateway_password) && ok;
    if (!ok)
    {
        free_connection_params(params);
        return NULL;
    }
    return params;
}

void free_connection_params(connection_params* params)
{
    if (!params) return;
    rdp_session_config* config = &params->config;
    free((char*)config->host);
    free((char*)config->connect_host);
    free((char*)config->domain);
    free((char*)config->user);
    free((char*)config->password);
    free((char*)config->gateway_host);
    free((char*)config->gateway_domain);
    free((char*)config->gateway_user);
    free((char*)config->gateway_password);
    free(params);
}

/* Public session allocation/teardown. The connection module owns the RDP thread and instance. */
rdp_session* rdp_session_connect(const rdp_session_config* config,
                                 FrameCallback frame_callback, ClipboardTextCallback clipboard_text_callback, ClipboardFilesCallback clipboard_files_callback, StatusCallback status_callback, CertificateDecisionCallback certificate_decision_callback,
                                 CursorCallback cursor_callback, ClipboardFileContentsCallback clipboard_file_contents_callback) {
    if (!config || config->size < sizeof(rdp_session_config)) return NULL;
    rdp_session* session = calloc(1, sizeof(rdp_session));
    if (!session) return NULL;

    session->desktop_scale_percent = config->desktop_scale_percent;
    session->device_scale_percent = config->device_scale_percent;
    session->callback = frame_callback;
    session->clipboard_text_callback = clipboard_text_callback;
    session->clipboard_files_callback = clipboard_files_callback;
    session->clipboard_file_contents_callback = clipboard_file_contents_callback;
    session->status_callback = status_callback;
    session->certificate_decision_callback = certificate_decision_callback;
    session->cursor_callback = cursor_callback;
    session->render_mode = wrapper_graphics_mode();
    InitializeCriticalSection(&session->resize_lock);
    InitializeCriticalSection(&session->input_lock);
    InitializeCriticalSection(&session->clipboard_lock);
    InitializeCriticalSection(&session->frame_lock);
    InitializeCriticalSection(&session->cursor_lock);

#if defined(_WIN32)
    /* Match wfreerdp's registered clipboard format IDs. */
    session->file_group_descriptor_format_id = RegisterClipboardFormatA("FileGroupDescriptorW");
    session->file_contents_format_id = RegisterClipboardFormatA("FileContents");
#else
    session->file_clipboard = ClipboardCreate();
    if (session->file_clipboard)
    {
        session->file_group_descriptor_format_id = ClipboardRegisterFormat(session->file_clipboard, "FileGroupDescriptorW");
        session->file_contents_format_id = ClipboardRegisterFormat(session->file_clipboard, "FileContents");
    }
#endif

    session->target_width = (UINT32)config->width;
    session->target_height = (UINT32)config->height;

    connection_params* params = copy_connection_params(session, config);
    if (!params)
    {
        rdp_session_free(session);
        return NULL;
    }

#if defined(_WIN32)
    WSADATA wsa_data;
    const int wsa_status = WSAStartup(MAKEWORD(2, 2), &wsa_data);
    if (wsa_status != 0)
    {
        fprintf(stderr, "Failed to initialize Winsock: %d\n", wsa_status);
        free_connection_params(params);
        rdp_session_free(session);
        return NULL;
    }
    session->winsock_initialized = true;
#endif

    session->running = true;
    session->thread = CreateThread(NULL, 0, wrapper_connection_thread, params, 0, NULL);
    if (!session->thread) {
        free_connection_params(params);
        session->running = false;
        rdp_session_free(session);
        return NULL;
    }
    return session;
}

void rdp_session_disconnect(rdp_session* session) {
    if (!session) return;
    session->running = false;
    if (session->instance && session->instance->context)
    {
        freerdp_abort_connect_context(session->instance->context);
    }
    if (session->thread) {
        WaitForSingleObject(session->thread, INFINITE);
        CloseHandle(session->thread);
        session->thread = NULL;
    }
}

void rdp_session_free(rdp_session* session) {
    if (!session) return;
    rdp_session_disconnect(session);
    session->callback = NULL;
    session->clipboard_text_callback = NULL;
    session->clipboard_files_callback = NULL;
    session->clipboard_file_contents_callback = NULL;
    session->status_callback = NULL;
    session->certificate_decision_callback = NULL;
    session->cursor_callback = NULL;
    free_cursor_cache(session);
    DeleteCriticalSection(&session->resize_lock);
    DeleteCriticalSection(&session->input_lock);
    DeleteCriticalSection(&session->clipboard_lock);
    DeleteCriticalSection(&session->frame_lock);
    DeleteCriticalSection(&session->cursor_lock);
    if (session->file_clipboard) ClipboardDestroy(session->file_clipboard);
    free_clipboard_data(session);
#if defined(_WIN32)
    if (session->winsock_initialized) WSACleanup();
#endif
    free(session);
}
