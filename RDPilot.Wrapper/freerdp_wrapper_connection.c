#include "freerdp_wrapper_internal.h"
#include "freerdp_wrapper_channels.h"
#include "freerdp_wrapper_display.h"
#include "freerdp_wrapper_graphics.h"

static void emit_status(rdp_session* session, int status, const connection_error* error)
{
    if (!session || !session->status_callback) return;
    session->status_callback(session, status, error ? error->code : 0,
                            error ? error->name : NULL, error ? error->message : NULL);
}

static void capture_last_error(rdpContext* context, connection_error* error)
{
    if (!error) return;
    memset(error, 0, sizeof(connection_error));
    if (!context) return;
    error->code = freerdp_get_last_error(context);
    if (error->code != 0)
    {
        error->name = freerdp_get_last_error_name(error->code);
        error->message = freerdp_get_last_error_string(error->code);
    }
}

static void log_loop_stats_if_due(rdp_session* session, UINT32 total_ms, UINT32 input_ms,
                                  UINT32 clipboard_ms, UINT32 check_fds_ms, UINT32 resize_ms)
{
    if (!session) return;
    ULONGLONG now = GetTickCount64();
    if (session->perf_loop_last_log_tick == 0) session->perf_loop_last_log_tick = now;
    session->perf_loop_count++;
    if (total_ms > 50) session->perf_loop_slow_count++;
    if (total_ms > session->perf_loop_max_total_ms) session->perf_loop_max_total_ms = total_ms;
    if (input_ms > session->perf_loop_max_input_ms) session->perf_loop_max_input_ms = input_ms;
    if (clipboard_ms > session->perf_loop_max_clipboard_ms) session->perf_loop_max_clipboard_ms = clipboard_ms;
    if (check_fds_ms > session->perf_loop_max_check_fds_ms) session->perf_loop_max_check_fds_ms = check_fds_ms;
    if (resize_ms > session->perf_loop_max_resize_ms) session->perf_loop_max_resize_ms = resize_ms;
    if (now - session->perf_loop_last_log_tick < 1000) return;
    session->perf_loop_last_log_tick = now;
    session->perf_loop_count = 0;
    session->perf_loop_slow_count = 0;
    session->perf_loop_max_total_ms = 0;
    session->perf_loop_max_input_ms = 0;
    session->perf_loop_max_clipboard_ms = 0;
    session->perf_loop_max_check_fds_ms = 0;
    session->perf_loop_max_resize_ms = 0;
}

static DWORD on_verify_certificate_ex(freerdp* instance, const char* host, UINT16 port,
                                      const char* common_name, const char* subject,
                                      const char* issuer, const char* fingerprint, DWORD flags)
{
    (void)flags;
    rdp_session* session = (instance && instance->context) ? session_from_context(instance->context) : NULL;
    if (!session || !session->certificate_decision_callback) return 0;
    return (DWORD)session->certificate_decision_callback(session, host, port, common_name, subject,
                                                        issuer, fingerprint, 0, NULL, NULL, NULL);
}

static DWORD on_verify_changed_certificate_ex(freerdp* instance, const char* host, UINT16 port,
                                              const char* common_name, const char* subject,
                                              const char* issuer, const char* new_fingerprint,
                                              const char* old_subject, const char* old_issuer,
                                              const char* old_fingerprint, DWORD flags)
{
    (void)flags;
    rdp_session* session = (instance && instance->context) ? session_from_context(instance->context) : NULL;
    if (!session || !session->certificate_decision_callback) return 0;
    return (DWORD)session->certificate_decision_callback(session, host, port, common_name, subject,
                                                        issuer, new_fingerprint, 1, old_subject, old_issuer, old_fingerprint);
}

static void cleanup_instance(rdp_session* session)
{
    if (!session || !session->instance) return;
    if (session->instance->context) freerdp_context_free(session->instance);
    freerdp_free(session->instance);
    session->instance = NULL;
    session->disp = NULL;
    session->cliprdr = NULL;
    session->gfx = NULL;
    session->disp_ready = false;
    EnterCriticalSection(&session->frame_lock);
    session->pending_frame = 0;
    LeaveCriticalSection(&session->frame_lock);
}

static void shutdown_instance(rdp_session* session)
{
    if (!session || !session->instance) return;
    if (session->connect_succeeded) freerdp_disconnect(session->instance);
    session->connect_succeeded = false;
    cleanup_instance(session);
}

static bool setup_instance(rdp_session* session, const rdp_session_config* config, bool use_gateway)
{
    session->instance = freerdp_new();
    if (!session->instance) {
        fprintf(stderr, "Failed to create FreeRDP instance\n");
        return false;
    }
    session->instance->LoadChannels = freerdp_client_load_channels;
    session->instance->VerifyCertificateEx = on_verify_certificate_ex;
    session->instance->VerifyChangedCertificateEx = on_verify_changed_certificate_ex;
    session->instance->ContextSize = sizeof(wrapper_context);
    if (!freerdp_context_new(session->instance)) {
        fprintf(stderr, "Failed to create FreeRDP context\n");
        cleanup_instance(session);
        return false;
    }
    ((wrapper_context*)session->instance->context)->session = session;
    // Register before connect and after each gdi_init so activation cannot drop pointer updates.
    register_pointer_class(session->instance->context);

    rdpSettings* settings = session->instance->context->settings;
    const char* server_hostname = use_gateway ? config->host : config->connect_host;
    freerdp_settings_set_string(settings, FreeRDP_ServerHostname, server_hostname);
    freerdp_settings_set_string(settings, FreeRDP_UserSpecifiedServerName, config->host);
    freerdp_settings_set_string(settings, FreeRDP_CertificateName, config->host);
    freerdp_settings_set_uint32(settings, FreeRDP_ServerPort, config->port);
    freerdp_settings_set_string(settings, FreeRDP_Domain, config->domain);
    freerdp_settings_set_string(settings, FreeRDP_Username, config->user);
    freerdp_settings_set_string(settings, FreeRDP_Password, config->password);
    freerdp_settings_set_uint32(settings, FreeRDP_TcpConnectTimeout, CONNECT_TIMEOUT_MS);
    freerdp_settings_set_uint32(settings, FreeRDP_AutoReconnectMaxRetries, 0);
    if (use_gateway) {
        freerdp_settings_set_bool(settings, FreeRDP_GatewayEnabled, TRUE);
        freerdp_settings_set_string(settings, FreeRDP_GatewayHostname, config->gateway_host);
        freerdp_settings_set_string(settings, FreeRDP_GatewayDomain, config->gateway_domain);
        freerdp_settings_set_string(settings, FreeRDP_GatewayUsername, config->gateway_user);
        freerdp_settings_set_string(settings, FreeRDP_GatewayPassword, config->gateway_password);
        freerdp_settings_set_bool(settings, FreeRDP_GatewayUseSameCredentials, FALSE);
        freerdp_settings_set_bool(settings, FreeRDP_GatewayHttpTransport, TRUE);
        freerdp_settings_set_bool(settings, FreeRDP_GatewayBypassLocal, FALSE);
    }
    else freerdp_settings_set_bool(settings, FreeRDP_GatewayEnabled, FALSE);

    freerdp_settings_set_bool(settings, FreeRDP_IgnoreCertificate, FALSE);
    freerdp_settings_set_uint32(settings, FreeRDP_OsMajorType, OSMAJORTYPE_WINDOWS);
    freerdp_settings_set_uint32(settings, FreeRDP_OsMinorType, OSMINORTYPE_WINDOWS_NT);
    freerdp_settings_set_bool(settings, FreeRDP_DeactivateClientDecoding, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_SoftwareGdi, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_AutoReconnectionEnabled, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_SupportDynamicChannels, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_SupportDisplayControl, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_SupportMonitorLayoutPdu, FALSE);
    freerdp_settings_set_uint32(settings, FreeRDP_LargePointerFlag, LARGE_POINTER_FLAG_96x96 | LARGE_POINTER_FLAG_384x384);
    wrapper_graphics_configure(settings, session->render_mode);
    freerdp_settings_set_bool(settings, FreeRDP_DynamicResolutionUpdate, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_RedirectClipboard, TRUE);
    freerdp_settings_set_uint32(settings, FreeRDP_ClipboardFeatureMask,
                                CLIPRDR_FLAG_LOCAL_TO_REMOTE | CLIPRDR_FLAG_REMOTE_TO_LOCAL |
                                CLIPRDR_FLAG_LOCAL_TO_REMOTE_FILES | CLIPRDR_FLAG_REMOTE_TO_LOCAL_FILES);
    freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, config->use_network_level_authentication ? TRUE : FALSE);
    /* HYBRID_EX is NLA too. FreeRDP enables it by default, so leaving it on makes a credential-less
     * connect still request CredSSP; a server that supports NLA without requiring it (the setup
     * for logging in on the remote logon screen) then picks HYBRID_EX and the connect fails with
     * SEC_E_NO_CREDENTIALS instead of falling back to TLS. */
    freerdp_settings_set_bool(settings, FreeRDP_ExtSecurity, config->use_network_level_authentication ? TRUE : FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_MstscCookieMode, TRUE);
    if (config->keyboard_layout != 0) freerdp_settings_set_uint32(settings, FreeRDP_KeyboardLayout, config->keyboard_layout);
    freerdp_settings_set_uint32(settings, FreeRDP_KeyboardType, WINPR_KBD_TYPE_IBM_ENHANCED);
    freerdp_settings_set_uint32(settings, FreeRDP_KeyboardSubType, 0);
    freerdp_settings_set_uint32(settings, FreeRDP_KeyboardFunctionKey, 12);
    freerdp_settings_set_bool(settings, FreeRDP_AudioPlayback, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_DeviceRedirection, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_CompressionEnabled, config->compression ? TRUE : FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_BitmapCacheEnabled, config->bitmap_cache ? TRUE : FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_BitmapCachePersistEnabled, FALSE);
    freerdp_settings_set_uint32(settings, FreeRDP_ConnectionType, (UINT32)config->connection_type);
    freerdp_settings_set_bool(settings, FreeRDP_NetworkAutoDetect, config->network_auto_detect ? TRUE : FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableWallpaper, config->desktop_wallpaper ? FALSE : TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableFullWindowDrag, config->full_window_drag ? FALSE : TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableMenuAnims, config->menu_animations ? FALSE : TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableThemes, config->themes ? FALSE : TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_AllowFontSmoothing, config->font_smoothing ? TRUE : FALSE);
    freerdp_performance_flags_make(settings);
    freerdp_settings_set_string(settings, FreeRDP_ClientHostname, "RDPilot");
    freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, (UINT32)config->color_depth);
    wrapper_display_configure(session, settings, (UINT32)config->width, (UINT32)config->height);
    freerdp_settings_set_bool(settings, FreeRDP_Fullscreen, FALSE);
    wrapper_channels_subscribe(session->instance->context);
    wrapper_graphics_subscribe_reset(session->instance->context);
    return true;
}

static bool connect_attempt(rdp_session* session, const rdp_session_config* config, bool use_gateway, connection_error* error)
{
    if (error) memset(error, 0, sizeof(connection_error));
    if (!setup_instance(session, config, use_gateway))
    {
        if (error)
        {
            error->name = "WRAPPER_SETUP_FAILED";
            error->message = "Failed to initialize the RDP client before connecting.";
        }
        return false;
    }
    if (!freerdp_connect(session->instance)) {
        fprintf(stderr, "Failed to connect %s gateway\n", use_gateway ? "with" : "without");
        capture_last_error(session->instance->context, error);
        session->connect_succeeded = false;
        cleanup_instance(session);
        return false;
    }
    session->connect_succeeded = true;
    return true;
}

DWORD WINAPI wrapper_connection_thread(LPVOID lpParam) {
    connection_params* params = (connection_params*)lpParam;
    rdp_session* session = params->session;
    if (freerdp_register_addin_provider(freerdp_channels_load_static_addin_entry, 0) != CHANNEL_RC_OK)
    {
        fprintf(stderr, "Failed to register FreeRDP static addin provider\n");
        free_connection_params(params);
        session->running = false;
        connection_error error = { 0, "WRAPPER_ADDIN_PROVIDER_FAILED", "Failed to register FreeRDP static addin provider." };
        emit_status(session, 2, &error);
        return 1;
    }
    const rdp_session_config* config = &params->config;
    bool has_gateway = config->gateway_host && config->gateway_host[0] != '\0';
    connection_error error;
    connection_error direct_error = { 0 };
    bool connected = connect_attempt(session, config, false, &error);
    if (!connected) direct_error = error;
    if (!connected && has_gateway && session->running) connected = connect_attempt(session, config, true, &error);
    if (!connected) {
        fprintf(stderr, "Failed to connect\n");
        free_connection_params(params);
        session->running = false;
        if (has_gateway && direct_error.name && error.name)
        {
            char combined_message[1024] = { 0 };
            const char* direct_message = direct_error.message ? direct_error.message : "Unknown direct connection error.";
            const char* gateway_message = error.message ? error.message : "Unknown gateway connection error.";
            snprintf(combined_message, sizeof(combined_message),
                     "Direct connection failed: %s Gateway connection failed: %s", direct_message, gateway_message);
            connection_error combined_error = { error.code, "WRAPPER_DIRECT_AND_GATEWAY_FAILED", combined_message };
            emit_status(session, 2, &combined_error);
            return 1;
        }
        emit_status(session, 2, &error);
        return 1;
    }
    emit_status(session, 1, NULL);
    wrapper_graphics_initialize(session);
    free_connection_params(params);

    while (session->running) {
        ULONGLONG loop_start = GetTickCount64();
        // Canonical FreeRDP loop: wait on transport/channel events, then check once.
        HANDLE eventHandles[MAXIMUM_WAIT_OBJECTS];
        DWORD eventCount = freerdp_get_event_handles(session->instance->context, eventHandles, MAXIMUM_WAIT_OBJECTS);
        if (eventCount == 0)
        {
            fprintf(stderr, "freerdp_get_event_handles failed\n");
            break;
        }
        DWORD waitStatus = WaitForMultipleObjects(eventCount, eventHandles, FALSE, INPUT_LOOP_TIMEOUT_MS);
        if (waitStatus == WAIT_FAILED)
        {
            fprintf(stderr, "RDP loop WaitForMultipleObjects failed: 0x%08X\n", (unsigned)GetLastError());
            break;
        }
        // Announce a local clipboard change first, then drain input, then process channel data,
        // then handle resize. Clipboard goes before input so that "copy locally, switch to the
        // session, Ctrl+V" reaches the server as format list then keystroke; the other way round
        // the remote app pastes the previous clipboard.
        ULONGLONG phase_start = GetTickCount64();
        if (process_pending_clipboard(session))
        {
            // Channel writes are only queued by the cliprdr plugin; flush them now, otherwise
            // they leave in freerdp_check_event_handles below, after the input we send next.
            freerdp_channels_check_fds(session->instance->context->channels, session->instance);
        }
        UINT32 clipboard_ms = (UINT32)(GetTickCount64() - phase_start);
        phase_start = GetTickCount64();
        process_pending_input(session);
        UINT32 input_ms = (UINT32)(GetTickCount64() - phase_start);
        phase_start = GetTickCount64();
        BOOL fdsOk = freerdp_check_event_handles(session->instance->context);
        UINT32 check_fds_ms = (UINT32)(GetTickCount64() - phase_start);
        if (!fdsOk)
        {
            log_loop_stats_if_due(session, (UINT32)(GetTickCount64() - loop_start), input_ms, clipboard_ms, check_fds_ms, 0);
            // No recorded FreeRDP error: retry transient failures instead of tearing down.
            if (freerdp_get_last_error(session->instance->context) == 0) continue;
            break;
        }
        if (freerdp_shall_disconnect_context(session->instance->context)) break;
        phase_start = GetTickCount64();
        if (!wrapper_display_process_pending(session)) break;
        UINT32 resize_ms = (UINT32)(GetTickCount64() - phase_start);
        log_loop_stats_if_due(session, (UINT32)(GetTickCount64() - loop_start), input_ms, clipboard_ms, check_fds_ms, resize_ms);
    }
    shutdown_instance(session);
    session->running = false;
    emit_status(session, 3, NULL);
    return 0;
}
