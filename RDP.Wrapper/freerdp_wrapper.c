#include "freerdp_wrapper.h"

#include <freerdp/freerdp.h>
#include <freerdp/client.h>
#include <freerdp/settings.h>
#include <freerdp/update.h>
#include <winpr/stream.h>
#include <winpr/wtypes.h>
#include <winpr/thread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <freerdp/client/disp.h>

static FrameCallback g_callback = NULL;
static freerdp* g_instance = NULL;
static HANDLE g_thread = NULL;
static bool g_running = false;

typedef struct {
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

void set_frame_callback(FrameCallback cb) {
    g_callback = cb;
}

static BOOL on_end_paint(rdpContext* context)
{
    if (!g_callback) return TRUE;

    rdpGdi* gdi = context->gdi;
    if (gdi && gdi->primary_buffer) {
        g_callback((uint8_t*)gdi->primary_buffer, gdi->width, gdi->height);
    }

    return TRUE;
}

static BOOL on_surface_bits(rdpContext* context,
                            const SURFACE_BITS_COMMAND* cmd)
{
    if (!g_callback) return TRUE;

    // Only handle 32bpp for now
    if (cmd->bmp.bpp != 32) return TRUE;

    g_callback((uint8_t*)cmd->bmp.bitmapData, cmd->bmp.width, cmd->bmp.height);
    return TRUE;
}

static DWORD WINAPI rdp_thread_func(LPVOID lpParam) {
    connection_params* params = (connection_params*)lpParam;

    g_instance = freerdp_new();
    if (!g_instance) {
        fprintf(stderr, "Failed to create FreeRDP instance\n");
        free(params);
        g_running = false;
        return 1;
    }

    g_instance->ContextSize = sizeof(rdpContext);
    if (!freerdp_context_new(g_instance)) {
        fprintf(stderr, "Failed to create FreeRDP context\n");
        freerdp_free(g_instance);
        g_instance = NULL;
        free(params);
        g_running = false;
        return 1;
    }

    rdpSettings* settings = g_instance->context->settings;
    freerdp_settings_set_string(settings, FreeRDP_ServerHostname, params->host);
    freerdp_settings_set_string(settings, FreeRDP_Domain, params->domain);
    freerdp_settings_set_string(settings, FreeRDP_Username, params->user);
    freerdp_settings_set_string(settings, FreeRDP_Password, params->password);

    if (params->gateway_host[0] != '\0') {
        freerdp_settings_set_bool(settings, FreeRDP_GatewayEnabled, TRUE);
        freerdp_settings_set_string(settings, FreeRDP_GatewayHostname, params->gateway_host);
        freerdp_settings_set_string(settings, FreeRDP_GatewayDomain, params->gateway_domain);
        freerdp_settings_set_string(settings, FreeRDP_GatewayUsername, params->gateway_user);
        freerdp_settings_set_string(settings, FreeRDP_GatewayPassword, params->gateway_password);
        freerdp_settings_set_bool(settings, FreeRDP_GatewayUseSameCredentials, FALSE);

        // Use the standard HTTP transport flag which is widely supported and for now the only supported method.
        freerdp_settings_set_bool(settings, FreeRDP_GatewayHttpTransport, TRUE);
        freerdp_settings_set_bool(settings, FreeRDP_GatewayBypassLocal, FALSE);
    }

    // For now always ignore certificate. Adding a popup to review the certificate should be implemented for v1
    freerdp_settings_set_bool(settings, FreeRDP_IgnoreCertificate, TRUE);

    // Disable FreeRDP's own window/UI creation
    freerdp_settings_set_bool(settings, FreeRDP_DeactivateClientDecoding, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_SoftwareGdi, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_AutoReconnectionEnabled, TRUE);

    freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_RemoteFxCodec, TRUE);

    // Instead of DisableKerberos, we use the security negotiation flags
    // Setting NLA to TRUE and others to TRUE/FALSE as needed
    freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, TRUE);

    // Set a client hostname as some gateways require it
    freerdp_settings_set_string(settings, FreeRDP_ClientHostname, "AvaloniaRDP");

    freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, 32);
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, params->width);
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopHeight, params->height);

    // Use 1 monitor instead of 0
    // TODO : Multi monitor support
    freerdp_settings_set_uint32(settings, FreeRDP_MonitorCount, 1);
    freerdp_settings_set_bool(settings, FreeRDP_UseMultimon, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_Fullscreen, FALSE);

    // Connect
    if (!freerdp_connect(g_instance)) {
        fprintf(stderr, "Failed to connect\n");
        freerdp_context_free(g_instance);
        freerdp_free(g_instance);
        g_instance = NULL;
        free(params);
        g_running = false;
        return 1;
    }

    gdi_init(g_instance, PIXEL_FORMAT_BGRA32);

    // Hook callbacks after GDI init as it might override them
    g_instance->context->update->SurfaceBits = on_surface_bits;
    g_instance->context->update->EndPaint = on_end_paint;

    free(params);

        while (g_running) {
            if (!freerdp_check_fds(g_instance)) {
                // If check_fds fails, it might be a temporary state or a disconnect
                if (freerdp_get_last_error(g_instance->context) == 0) {
                    usleep(10000);
                    continue;
                }
                break;
            }

            if (freerdp_shall_disconnect(g_instance))
                break;

            usleep(10000); // 10ms is plenty for RDP
        }

    freerdp_disconnect(g_instance);
    freerdp_context_free(g_instance);
    freerdp_free(g_instance);
    g_instance = NULL;
    g_running = false;

    return 0;
}

bool connect_rdp(const char* host, const char* domain, const char* user, const char* password,
                 const char* gateway_host, const char* gateway_domain, const char* gateway_user, const char* gateway_password,
                 int width, int height) {
    if (g_running) return false;

    connection_params* params = malloc(sizeof(connection_params));
    memset(params, 0, sizeof(connection_params));
    strncpy(params->host, host, 255);
    strncpy(params->domain, domain, 255);
    strncpy(params->user, user, 255);
    strncpy(params->password, password, 255);
    if (gateway_host) strncpy(params->gateway_host, gateway_host, 255);
    if (gateway_domain) strncpy(params->gateway_domain, gateway_domain, 255);
    if (gateway_user) strncpy(params->gateway_user, gateway_user, 255);
    if (gateway_password) strncpy(params->gateway_password, gateway_password, 255);
    params->width = width;
    params->height = height;

    g_running = true;
    g_thread = CreateThread(NULL, 0, rdp_thread_func, params, 0, NULL);

    if (!g_thread) {
        free(params);
        g_running = false;
        return false;
    }

    return true;
}

void disconnect_rdp(void) {
    g_running = false;
    if (g_thread) {
        WaitForSingleObject(g_thread, INFINITE);
        CloseHandle(g_thread);
        g_thread = NULL;
    }
}

void send_mouse_event(uint16_t flags, uint16_t x, uint16_t y) {
    if (g_instance && g_instance->context && g_instance->context->input) {
        freerdp_input_send_mouse_event(g_instance->context->input, flags, x, y);
    }
}

void send_keyboard_event(uint16_t flags, uint16_t code) {
    if (g_instance && g_instance->context && g_instance->context->input) {
        freerdp_input_send_keyboard_event(g_instance->context->input, flags, code);
    }
}

void update_resolution(int width, int height) {
    if (g_instance && g_instance->context && g_instance->context->settings) {
        freerdp_settings_set_uint32(g_instance->context->settings, FreeRDP_DesktopWidth, width);
        freerdp_settings_set_uint32(g_instance->context->settings, FreeRDP_DesktopHeight, height);

        // Send a dynamic resolution update request
        if (g_instance->context->update) {
            g_instance->context->update->DesktopResize(g_instance->context);
        }
    }
}
