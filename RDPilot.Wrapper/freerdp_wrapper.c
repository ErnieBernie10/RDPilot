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

static void copy_string_field(char* dest, size_t dest_size, const char* src)
{
    if (!dest || dest_size == 0) return;
    if (!src)
    {
        dest[0] = '\0';
        return;
    }
    size_t length = strlen(src);
    if (length >= dest_size) length = dest_size - 1;
    memcpy(dest, src, length);
    dest[length] = '\0';
}

/* Public session allocation/teardown. The connection module owns the RDP thread and instance. */
rdp_session* rdp_session_connect(const char* host, const char* connect_host, uint16_t port, const char* domain, const char* user, const char* password,
                                 const char* gateway_host, const char* gateway_domain, const char* gateway_user, const char* gateway_password,
                                 int width, int height, int color_depth, bool compression, bool font_smoothing, bool bitmap_cache,
                                 bool desktop_wallpaper, bool themes, bool menu_animations, bool full_window_drag, int connection_type, bool network_auto_detect,
                                 bool use_network_level_authentication,
                                 uint32_t keyboard_layout, uint32_t dpi_scale_percent, uint32_t device_scale_percent,
                                 FrameCallback frame_callback, ClipboardTextCallback clipboard_text_callback, ClipboardFilesCallback clipboard_files_callback, StatusCallback status_callback, CertificateDecisionCallback certificate_decision_callback,
                                 CursorCallback cursor_callback, ClipboardFileContentsCallback clipboard_file_contents_callback) {
    rdp_session* session = calloc(1, sizeof(rdp_session));
    if (!session) return NULL;

    session->dpi_scale_percent = dpi_scale_percent == 0 ? 100 : dpi_scale_percent;
    session->device_scale_percent = device_scale_percent == 0 ? 100 : device_scale_percent;
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

    UINT32 initial_width = (UINT32)width;
    UINT32 initial_height = (UINT32)height;
    session->target_width = initial_width;
    session->target_height = initial_height;

    connection_params* params = malloc(sizeof(connection_params));
    if (!params)
    {
        rdp_session_free(session);
        return NULL;
    }
    memset(params, 0, sizeof(connection_params));
    params->session = session;
    copy_string_field(params->host, sizeof(params->host), host);
    copy_string_field(params->connect_host, sizeof(params->connect_host), connect_host && connect_host[0] != '\0' ? connect_host : host);
    params->port = port;
    copy_string_field(params->domain, sizeof(params->domain), domain);
    copy_string_field(params->user, sizeof(params->user), user);
    copy_string_field(params->password, sizeof(params->password), password);
    copy_string_field(params->gateway_host, sizeof(params->gateway_host), gateway_host);
    copy_string_field(params->gateway_domain, sizeof(params->gateway_domain), gateway_domain);
    copy_string_field(params->gateway_user, sizeof(params->gateway_user), gateway_user);
    copy_string_field(params->gateway_password, sizeof(params->gateway_password), gateway_password);
    params->width = (int)initial_width;
    params->height = (int)initial_height;
    params->color_depth = (int)color_depth;
    params->compression = compression;
    params->font_smoothing = font_smoothing;
    params->bitmap_cache = bitmap_cache;
    params->desktop_wallpaper = desktop_wallpaper;
    params->themes = themes;
    params->menu_animations = menu_animations;
    params->full_window_drag = full_window_drag;
    params->connection_type = (int)connection_type;
    params->network_auto_detect = network_auto_detect;
    params->use_network_level_authentication = use_network_level_authentication;
    params->keyboard_layout = keyboard_layout;

#if defined(_WIN32)
    WSADATA wsa_data;
    const int wsa_status = WSAStartup(MAKEWORD(2, 2), &wsa_data);
    if (wsa_status != 0)
    {
        fprintf(stderr, "Failed to initialize Winsock: %d\n", wsa_status);
        free(params);
        rdp_session_free(session);
        return NULL;
    }
    session->winsock_initialized = true;
#endif

    session->running = true;
    session->thread = CreateThread(NULL, 0, wrapper_connection_thread, params, 0, NULL);
    if (!session->thread) {
        free(params);
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
