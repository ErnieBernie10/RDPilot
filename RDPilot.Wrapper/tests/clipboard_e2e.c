/*
 * End-to-end clipboard tests for libfreerdp_wrapper.
 *
 * The test process hosts a scriptable RDP server (built on FreeRDP's server library and its
 * cliprdr server channel) on 127.0.0.1 and connects to it through the public wrapper API, exactly
 * as RDPilot.Client does. Each scenario drives one side of the clipboard and checks the other side
 * through an independent oracle:
 *
 *   remote -> local : the server announces text; the wrapper's ClipboardTextCallback must report it.
 *   local -> remote : rdp_session_clipboard_set_local_text; the server must fetch and receive it.
 *
 * The server can also misbehave the way a real Windows rdpclip does under load (fail a data
 * request while the source application still holds the clipboard, announce several clipboard
 * changes in a row, offer files and then text), which is what makes the client flaky.
 *
 * Usage: rdpilot_clipboard_e2e [--iterations N] [--scenario NAME] [--verbose]
 * Exit status is non-zero if any scenario's success rate is below 100%.
 */
#include <winpr/wtypes.h>
#include <winpr/synch.h>
#include <winpr/thread.h>
#include <winpr/winsock.h>
#include <winpr/ssl.h>
#include <winpr/wlog.h>
#include <winpr/string.h>
#include <winpr/sysinfo.h>

#include <freerdp/freerdp.h>
#include <freerdp/listener.h>
#include <freerdp/peer.h>
#include <freerdp/crypto/certificate.h>
#include <freerdp/crypto/privatekey.h>
#include <freerdp/server/cliprdr.h>
#include <freerdp/channels/wtsvc.h>
#include <freerdp/channels/channels.h>
#include <freerdp/channels/cliprdr.h>

#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include "../freerdp_wrapper.h"

#ifndef CF_UNICODETEXT
#define CF_UNICODETEXT 13
#endif
#ifndef CF_TEXT
#define CF_TEXT 1
#endif
#ifndef CF_LOCALE
#define CF_LOCALE 16
#endif
#ifndef CF_OEMTEXT
#define CF_OEMTEXT 7
#endif

#define MAX_TEXT (2u * 1024u * 1024u)
#define WAIT_MS 3000

static bool g_verbose = false;

static void vlog(const char* fmt, ...)
{
    if (!g_verbose)
        return;
    va_list ap;
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    va_end(ap);
}

/* ------------------------------------------------------------------------------------------ */
/* Shared state between the test thread, the server's cliprdr thread and the wrapper callbacks */
/* ------------------------------------------------------------------------------------------ */

typedef struct
{
    pthread_mutex_t lock;
    pthread_cond_t changed;

    /* server side */
    CliprdrServerContext* clip;
    bool server_ready;            /* client capabilities + first client format list received */
    char* server_text;            /* what the server's clipboard currently holds (UTF-8) */
    char* server_received_text;   /* last text the server fetched from the client */
    unsigned server_requests_seen;/* data requests the client sent us */
    unsigned fail_next_requests;  /* answer this many upcoming client data requests with FAIL */
    unsigned fail_delay_ms;       /* delay before every answer (simulates a slow source app) */
    bool answer_with_snapshot;    /* answer request k with the text of format list k */
    char* snapshots[64];
    unsigned snapshot_head, snapshot_tail;
    bool offer_files_next;        /* next server format list advertises files + text */
    bool fail_file_descriptors;   /* the file list exists but can never be rendered */
    unsigned file_contents_delay_ms;

    /* wire order as seen by the server's network thread: 'L' client format list, 'V' the V key */
    char wire_log[256];
    size_t wire_len;

    /* client side */
    rdp_session* session;
    int status;
    char* client_received_text;   /* last text the wrapper reported via ClipboardTextCallback */
    unsigned client_text_callbacks;
    unsigned client_files_callbacks;
    char client_last_event;       /* 'T' text or 'F' files: which callback fired last */
} shared_state;

static shared_state g = { .lock = PTHREAD_MUTEX_INITIALIZER, .changed = PTHREAD_COND_INITIALIZER };

static void set_str(char** dst, const char* src)
{
    free(*dst);
    *dst = src ? strdup(src) : NULL;
}

static uint64_t now_ms(void)
{
    return GetTickCount64();
}

/* Wait until pred() holds or timeout; caller must hold g.lock. */
static bool wait_until(bool (*pred)(const void*), const void* arg, unsigned timeout_ms)
{
    const uint64_t deadline = now_ms() + timeout_ms;
    while (!pred(arg))
    {
        const uint64_t now = now_ms();
        if (now >= deadline)
            return false;
        struct timespec ts;
        clock_gettime(CLOCK_REALTIME, &ts);
        uint64_t remaining = deadline - now;
        if (remaining > 50)
            remaining = 50;
        ts.tv_nsec += (long)(remaining * 1000000ull);
        while (ts.tv_nsec >= 1000000000L)
        {
            ts.tv_sec++;
            ts.tv_nsec -= 1000000000L;
        }
        pthread_cond_timedwait(&g.changed, &g.lock, &ts);
    }
    return true;
}

/* ------------------------------------------------------------------------------------------ */
/* Server: cliprdr callbacks (run on the cliprdr server thread)                               */
/* ------------------------------------------------------------------------------------------ */

static UINT srv_send_text_response(CliprdrServerContext* clip, const char* text)
{
    CLIPRDR_FORMAT_DATA_RESPONSE rsp = { 0 };
    rsp.common.msgType = CB_FORMAT_DATA_RESPONSE;
    if (!text)
    {
        rsp.common.msgFlags = CB_RESPONSE_FAIL;
        return clip->ServerFormatDataResponse(clip, &rsp);
    }

    size_t wlen = 0;
    WCHAR* w = ConvertUtf8ToWCharAlloc(text, &wlen);
    if (!w)
    {
        rsp.common.msgFlags = CB_RESPONSE_FAIL;
        return clip->ServerFormatDataResponse(clip, &rsp);
    }
    rsp.common.msgFlags = CB_RESPONSE_OK;
    rsp.common.dataLen = (UINT32)((wlen + 1) * sizeof(WCHAR));
    rsp.requestedFormatData = (const BYTE*)w;
    UINT rc = clip->ServerFormatDataResponse(clip, &rsp);
    free(w);
    return rc;
}

static UINT srv_client_capabilities(CliprdrServerContext* clip, const CLIPRDR_CAPABILITIES* caps)
{
    (void)clip;
    vlog("[srv] client capabilities sets=%u\n", caps->cCapabilitiesSets);
    return CHANNEL_RC_OK;
}

static UINT srv_client_format_list(CliprdrServerContext* clip, const CLIPRDR_FORMAT_LIST* list)
{
    CLIPRDR_FORMAT_LIST_RESPONSE rsp = { 0 };
    rsp.common.msgType = CB_FORMAT_LIST_RESPONSE;
    rsp.common.msgFlags = CB_RESPONSE_OK;
    UINT rc = clip->ServerFormatListResponse(clip, &rsp);
    if (rc != CHANNEL_RC_OK)
        return rc;

    bool has_text = false;
    for (UINT32 i = 0; i < list->numFormats; i++)
        if (list->formats[i].formatId == CF_UNICODETEXT)
            has_text = true;

    vlog("[srv] client format list n=%u text=%d\n", list->numFormats, has_text);

    pthread_mutex_lock(&g.lock);
    g.server_ready = true;
    pthread_cond_broadcast(&g.changed);
    pthread_mutex_unlock(&g.lock);

    if (!has_text)
        return CHANNEL_RC_OK;

    /* Windows rdpclip fetches eagerly as well (it renders on demand for local apps, but delayed
     * rendering is still backed by a request); requesting right away is the stricter test. */
    CLIPRDR_FORMAT_DATA_REQUEST req = { 0 };
    req.common.msgType = CB_FORMAT_DATA_REQUEST;
    req.requestedFormatId = CF_UNICODETEXT;
    return clip->ServerFormatDataRequest(clip, &req);
}

static UINT srv_client_format_list_response(CliprdrServerContext* clip, const CLIPRDR_FORMAT_LIST_RESPONSE* rsp)
{
    (void)clip;
    vlog("[srv] client format list response flags=0x%x\n", rsp->common.msgFlags);
    return CHANNEL_RC_OK;
}

static UINT srv_client_format_data_request(CliprdrServerContext* clip, const CLIPRDR_FORMAT_DATA_REQUEST* req)
{
    pthread_mutex_lock(&g.lock);
    g.server_requests_seen++;
    const bool fail = g.fail_next_requests > 0;
    if (fail)
        g.fail_next_requests--;
    const unsigned delay = g.fail_delay_ms;
    char* text = NULL;
    if (g.answer_with_snapshot && g.snapshot_head != g.snapshot_tail)
    {
        text = g.snapshots[g.snapshot_head % 64];
        g.snapshots[g.snapshot_head % 64] = NULL;
        g.snapshot_head++;
    }
    else if (g.server_text)
    {
        text = strdup(g.server_text);
    }
    pthread_cond_broadcast(&g.changed);
    pthread_mutex_unlock(&g.lock);

    vlog("[srv] client requests format=%u -> %s\n", req->requestedFormatId, fail ? "FAIL" : "data");
    if (delay)
        Sleep(delay);

    UINT rc;
    if (req->requestedFormatId == 0xC0F1 && g.fail_file_descriptors)
        rc = srv_send_text_response(clip, NULL);
    else if (!fail && req->requestedFormatId == 0xC0F1)
    {
        /* one-file FILEGROUPDESCRIPTORW, as rdpclip sends for a copied file */
        BYTE buf[sizeof(UINT32) + sizeof(FILEDESCRIPTORW)] = { 0 };
        *(UINT32*)buf = 1;
        FILEDESCRIPTORW* fd = (FILEDESCRIPTORW*)(buf + sizeof(UINT32));
        fd->dwFlags = FD_ATTRIBUTES | FD_FILESIZE;
        fd->dwFileAttributes = 0x80;
        fd->nFileSizeLow = 4;
        (void)ConvertUtf8ToWChar("report.txt", fd->cFileName, ARRAYSIZE(fd->cFileName));
        CLIPRDR_FORMAT_DATA_RESPONSE rsp = { 0 };
        rsp.common.msgType = CB_FORMAT_DATA_RESPONSE;
        rsp.common.msgFlags = CB_RESPONSE_OK;
        rsp.common.dataLen = sizeof(buf);
        rsp.requestedFormatData = buf;
        rc = clip->ServerFormatDataResponse(clip, &rsp);
    }
    else if (fail || req->requestedFormatId != CF_UNICODETEXT)
        rc = srv_send_text_response(clip, NULL);
    else
        rc = srv_send_text_response(clip, text);
    free(text);
    return rc;
}

static UINT srv_client_file_contents_request(CliprdrServerContext* clip, const CLIPRDR_FILE_CONTENTS_REQUEST* req)
{
    pthread_mutex_lock(&g.lock);
    const unsigned delay = g.file_contents_delay_ms;
    pthread_mutex_unlock(&g.lock);
    if (delay)
        Sleep(delay);
    CLIPRDR_FILE_CONTENTS_RESPONSE rsp = { 0 };
    rsp.common.msgType = CB_FILECONTENTS_RESPONSE;
    rsp.streamId = req->streamId;
    if (req->dwFlags & FILECONTENTS_SIZE)
    {
        static const UINT64 size = 4;
        rsp.common.msgFlags = CB_RESPONSE_OK;
        rsp.cbRequested = sizeof(size);
        rsp.requestedData = (const BYTE*)&size;
    }
    else
    {
        rsp.common.msgFlags = CB_RESPONSE_OK;
        rsp.cbRequested = 4;
        rsp.requestedData = (const BYTE*)"data";
    }
    return clip->ServerFileContentsResponse(clip, &rsp);
}

static UINT srv_client_format_data_response(CliprdrServerContext* clip, const CLIPRDR_FORMAT_DATA_RESPONSE* rsp)
{
    (void)clip;
    if (!(rsp->common.msgFlags & CB_RESPONSE_OK) || !rsp->requestedFormatData)
    {
        vlog("[srv] client data response FAILED\n");
        return CHANNEL_RC_OK;
    }

    char* text = ConvertWCharNToUtf8Alloc((const WCHAR*)rsp->requestedFormatData,
                                          rsp->common.dataLen / sizeof(WCHAR), NULL);
    vlog("[srv] received client text (%u bytes)\n", rsp->common.dataLen);
    pthread_mutex_lock(&g.lock);
    free(g.server_received_text);
    g.server_received_text = text;
    pthread_cond_broadcast(&g.changed);
    pthread_mutex_unlock(&g.lock);
    return CHANNEL_RC_OK;
}

/* Called from the test thread. Announces the server's clipboard like rdpclip does after a copy:
 * CF_UNICODETEXT plus the formats Windows synthesizes, and optionally a file list first. */
static void server_announce(const char* text)
{
    pthread_mutex_lock(&g.lock);
    set_str(&g.server_text, text);
    if (g.answer_with_snapshot)
    {
        g.snapshots[g.snapshot_tail % 64] = strdup(text);
        g.snapshot_tail++;
    }
    const bool files = g.offer_files_next;
    g.offer_files_next = false;
    CliprdrServerContext* clip = g.clip;
    pthread_mutex_unlock(&g.lock);

    CLIPRDR_FORMAT formats[6] = { 0 };
    UINT32 n = 0;
    if (files)
    {
        formats[n++] = (CLIPRDR_FORMAT){ .formatId = 0xC0F1, .formatName = "FileGroupDescriptorW" };
        formats[n++] = (CLIPRDR_FORMAT){ .formatId = 0xC0F2, .formatName = "FileContents" };
    }
    formats[n++] = (CLIPRDR_FORMAT){ .formatId = 0xC00D, .formatName = "HTML Format" };
    formats[n++] = (CLIPRDR_FORMAT){ .formatId = CF_UNICODETEXT };
    formats[n++] = (CLIPRDR_FORMAT){ .formatId = CF_LOCALE };
    formats[n++] = (CLIPRDR_FORMAT){ .formatId = CF_TEXT };

    CLIPRDR_FORMAT_LIST list = { 0 };
    list.common.msgType = CB_FORMAT_LIST;
    list.numFormats = n;
    list.formats = formats;
    UINT rc = clip->ServerFormatList(clip, &list);
    if (rc != CHANNEL_RC_OK)
        fprintf(stderr, "[srv] ServerFormatList failed rc=%u\n", rc);
}

/* ------------------------------------------------------------------------------------------ */
/* Server: peer plumbing                                                                       */
/* ------------------------------------------------------------------------------------------ */

typedef struct
{
    rdpContext base;
    HANDLE vcm;
    CliprdrServerContext* clip;
} test_peer_context;

static psPeerReceiveChannelData g_vcm_receive;
static UINT16 g_cliprdr_channel_id;

static void wire_append(char c)
{
    pthread_mutex_lock(&g.lock);
    if (g.wire_len + 1 < sizeof(g.wire_log))
    {
        g.wire_log[g.wire_len++] = c;
        g.wire_log[g.wire_len] = '\0';
    }
    pthread_mutex_unlock(&g.lock);
}

/* Runs on the peer's network thread in wire order, before the PDU is queued to the cliprdr
 * thread, so it records exactly the order the client sent things in. */
static BOOL peer_receive_channel_data(freerdp_peer* peer, UINT16 channelId, const BYTE* data, size_t size,
                                      UINT32 flags, size_t totalSize)
{
    if (channelId == g_cliprdr_channel_id && (flags & CHANNEL_FLAG_FIRST) && size >= 2 &&
        data[0] == CB_FORMAT_LIST && data[1] == 0)
        wire_append('L');
    return g_vcm_receive(peer, channelId, data, size, flags, totalSize);
}

static BOOL peer_keyboard_event(rdpInput* input, UINT16 flags, UINT8 code)
{
    (void)input;
    if (code == 0x2F && !(flags & KBD_FLAGS_RELEASE))
        wire_append('V');
    return TRUE;
}

static const char* g_cert_path;
static const char* g_key_path;

static BOOL peer_context_new(freerdp_peer* client, rdpContext* ctx)
{
    test_peer_context* c = (test_peer_context*)ctx;
    c->vcm = WTSOpenServerA((LPSTR)client->context);
    return c->vcm && c->vcm != INVALID_HANDLE_VALUE;
}

static void peer_context_free(freerdp_peer* client, rdpContext* ctx)
{
    (void)client;
    test_peer_context* c = (test_peer_context*)ctx;
    if (!c)
        return;
    if (c->clip)
    {
        c->clip->Stop(c->clip);
        cliprdr_server_context_free(c->clip);
        c->clip = NULL;
    }
    if (c->vcm)
        WTSCloseServer(c->vcm);
}

static BOOL peer_post_connect(freerdp_peer* client)
{
    test_peer_context* c = (test_peer_context*)client->context;
    if (c->clip)
        return TRUE;
    if (!WTSVirtualChannelManagerIsChannelJoined(c->vcm, CLIPRDR_SVC_CHANNEL_NAME))
    {
        fprintf(stderr, "[srv] client did not join cliprdr\n");
        return TRUE;
    }

    g_cliprdr_channel_id = WTSChannelGetId(client, CLIPRDR_SVC_CHANNEL_NAME);
    g_vcm_receive = client->ReceiveChannelData;
    client->ReceiveChannelData = peer_receive_channel_data;

    CliprdrServerContext* clip = cliprdr_server_context_new(c->vcm);
    if (!clip)
        return FALSE;
    clip->rdpcontext = client->context;
    clip->useLongFormatNames = TRUE;
    clip->streamFileClipEnabled = TRUE;
    clip->fileClipNoFilePaths = TRUE;
    clip->canLockClipData = FALSE;
    clip->ClientCapabilities = srv_client_capabilities;
    clip->ClientFormatList = srv_client_format_list;
    clip->ClientFormatListResponse = srv_client_format_list_response;
    clip->ClientFormatDataRequest = srv_client_format_data_request;
    clip->ClientFormatDataResponse = srv_client_format_data_response;
    clip->ClientFileContentsRequest = srv_client_file_contents_request;
    if (clip->Start(clip) != CHANNEL_RC_OK)
    {
        cliprdr_server_context_free(clip);
        return FALSE;
    }
    c->clip = clip;
    pthread_mutex_lock(&g.lock);
    g.clip = clip;
    pthread_cond_broadcast(&g.changed);
    pthread_mutex_unlock(&g.lock);
    vlog("[srv] cliprdr started\n");
    return TRUE;
}

static BOOL peer_activate(freerdp_peer* client)
{
    (void)client;
    return TRUE;
}

static DWORD WINAPI peer_thread(LPVOID arg)
{
    freerdp_peer* client = (freerdp_peer*)arg;
    client->ContextSize = sizeof(test_peer_context);
    client->ContextNew = peer_context_new;
    client->ContextFree = peer_context_free;
    if (!freerdp_peer_context_new(client))
    {
        freerdp_peer_free(client);
        return 0;
    }

    rdpSettings* settings = client->context->settings;
    rdpPrivateKey* key = freerdp_key_new_from_file_enc(g_key_path, NULL);
    rdpCertificate* cert = freerdp_certificate_new_from_file(g_cert_path);
    if (!key || !cert ||
        !freerdp_settings_set_pointer_len(settings, FreeRDP_RdpServerRsaKey, key, 1) ||
        !freerdp_settings_set_pointer_len(settings, FreeRDP_RdpServerCertificate, cert, 1))
    {
        fprintf(stderr, "[srv] failed to load certificate/key\n");
        goto out;
    }
    freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, FALSE);
    freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, 32);
    freerdp_settings_set_uint32(settings, FreeRDP_MultifragMaxRequestSize, 0xFFFFFF);

    client->context->input->KeyboardEvent = peer_keyboard_event;
    client->PostConnect = peer_post_connect;
    client->Activate = peer_activate;
    if (!client->Initialize(client))
        goto out;

    test_peer_context* c = (test_peer_context*)client->context;
    for (;;)
    {
        HANDLE handles[MAXIMUM_WAIT_OBJECTS];
        DWORD count = client->GetEventHandles(client, handles, 32);
        if (count == 0)
            break;
        handles[count++] = WTSVirtualChannelManagerGetEventHandle(c->vcm);
        if (WaitForMultipleObjects(count, handles, FALSE, 100) == WAIT_FAILED)
            break;
        if (!client->CheckFileDescriptor(client))
            break;
        if (!WTSVirtualChannelManagerCheckFileDescriptor(c->vcm))
            break;
    }
    vlog("[srv] peer disconnected\n");

out:
    pthread_mutex_lock(&g.lock);
    g.clip = NULL;
    g.server_ready = false;
    pthread_cond_broadcast(&g.changed);
    pthread_mutex_unlock(&g.lock);
    client->Disconnect(client);
    freerdp_peer_context_free(client);
    freerdp_peer_free(client);
    return 0;
}

static BOOL peer_accepted(freerdp_listener* listener, freerdp_peer* client)
{
    (void)listener;
    HANDLE t = CreateThread(NULL, 0, peer_thread, client, 0, NULL);
    if (!t)
        return FALSE;
    CloseHandle(t);
    return TRUE;
}

static DWORD WINAPI listener_thread(LPVOID arg)
{
    freerdp_listener* listener = (freerdp_listener*)arg;
    for (;;)
    {
        HANDLE handles[32];
        DWORD count = listener->GetEventHandles(listener, handles, 32);
        if (count == 0)
            break;
        if (WaitForMultipleObjects(count, handles, FALSE, INFINITE) == WAIT_FAILED)
            break;
        if (!listener->CheckFileDescriptor(listener))
            break;
    }
    return 0;
}

/* ------------------------------------------------------------------------------------------ */
/* Client: wrapper callbacks                                                                   */
/* ------------------------------------------------------------------------------------------ */

static void on_frame(rdp_session* s, uint8_t* d, int w, int h, int dx, int dy, int dw, int dh, int stride)
{
    (void)s; (void)d; (void)w; (void)h; (void)dx; (void)dy; (void)dw; (void)dh; (void)stride;
}

static void on_cursor(rdp_session* s, int kind, uint32_t id, int w, int h, int hx, int hy)
{
    (void)s; (void)kind; (void)id; (void)w; (void)h; (void)hx; (void)hy;
}

static void on_clip_text(rdp_session* s, const char* text)
{
    (void)s;
    pthread_mutex_lock(&g.lock);
    set_str(&g.client_received_text, text);
    g.client_text_callbacks++;
    g.client_last_event = 'T';
    pthread_cond_broadcast(&g.changed);
    pthread_mutex_unlock(&g.lock);
}

static void on_clip_files(rdp_session* s, const char** paths, size_t n)
{
    (void)s; (void)paths;
    vlog("[cli] files received n=%zu\n", n);
    pthread_mutex_lock(&g.lock);
    g.client_files_callbacks++;
    g.client_last_event = 'F';
    pthread_cond_broadcast(&g.changed);
    pthread_mutex_unlock(&g.lock);
}

static void on_status(rdp_session* s, int status, uint32_t code, const char* name, const char* msg)
{
    (void)s;
    vlog("[cli] status=%d code=0x%x %s %s\n", status, code, name ? name : "", msg ? msg : "");
    pthread_mutex_lock(&g.lock);
    g.status = status;
    pthread_cond_broadcast(&g.changed);
    pthread_mutex_unlock(&g.lock);
}

static int on_cert(rdp_session* s, const char* host, uint16_t port, const char* cn, const char* subject,
                   const char* issuer, const char* fp, int changed, const char* ps, const char* pi, const char* pf)
{
    (void)s; (void)host; (void)port; (void)cn; (void)subject; (void)issuer; (void)fp; (void)changed; (void)ps; (void)pi; (void)pf;
    return 2; /* accept for this session only */
}

/* ------------------------------------------------------------------------------------------ */
/* Scenarios                                                                                   */
/* ------------------------------------------------------------------------------------------ */

static bool pred_server_ready(const void* a) { (void)a; return g.server_ready && g.clip; }
static bool pred_client_has(const void* a) { return g.client_received_text && strcmp(g.client_received_text, (const char*)a) == 0; }
static bool pred_server_has(const void* a) { return g.server_received_text && strcmp(g.server_received_text, (const char*)a) == 0; }

static char* make_text(const char* tag, unsigned i, size_t pad)
{
    char head[256];
    snprintf(head, sizeof(head), "%s #%u \xC3\xA9\xE6\xBC\xA2\xE5\xAD\x97 \xF0\x9F\x99\x82\r\nline two\t%llu",
             tag, i, (unsigned long long)now_ms());
    size_t len = strlen(head);
    char* t = malloc(len + pad + 1);
    memcpy(t, head, len);
    for (size_t k = 0; k < pad; k++)
        t[len + k] = (char)('a' + (k % 26));
    t[len + pad] = '\0';
    return t;
}

static void reset_server_behaviour(void)
{
    pthread_mutex_lock(&g.lock);
    g.fail_next_requests = 0;
    g.fail_delay_ms = 0;
    g.answer_with_snapshot = false;
    for (unsigned k = 0; k < 64; k++)
    {
        free(g.snapshots[k]);
        g.snapshots[k] = NULL;
    }
    g.snapshot_head = g.snapshot_tail = 0;
    g.offer_files_next = false;
    g.fail_file_descriptors = false;
    g.file_contents_delay_ms = 0;
    pthread_mutex_unlock(&g.lock);
}

/* Each scenario returns true on success for one iteration. */
typedef bool (*scenario_fn)(unsigned i);

/* The server copies text; the client must receive it. */
static bool sc_remote_to_local(unsigned i)
{
    char* t = make_text("remote", i, 0);
    server_announce(t);
    pthread_mutex_lock(&g.lock);
    bool ok = wait_until(pred_client_has, t, WAIT_MS);
    pthread_mutex_unlock(&g.lock);
    free(t);
    return ok;
}

/* The client copies text; the server must be able to paste it. */
static bool sc_local_to_remote(unsigned i)
{
    char* t = make_text("local", i, 0);
    rdp_session_clipboard_set_local_text(g.session, t);
    pthread_mutex_lock(&g.lock);
    bool ok = wait_until(pred_server_has, t, WAIT_MS);
    pthread_mutex_unlock(&g.lock);
    free(t);
    return ok;
}

/* Like the above, but with a 1 MiB payload (large log excerpts, CSV pasted from Excel...). */
static bool sc_large_both_ways(unsigned i)
{
    char* t = make_text("large-remote", i, 1024 * 1024);
    server_announce(t);
    pthread_mutex_lock(&g.lock);
    bool ok = wait_until(pred_client_has, t, WAIT_MS * 2);
    pthread_mutex_unlock(&g.lock);
    free(t);
    if (!ok)
        return false;
    t = make_text("large-local", i, 1024 * 1024);
    rdp_session_clipboard_set_local_text(g.session, t);
    pthread_mutex_lock(&g.lock);
    ok = wait_until(pred_server_has, t, WAIT_MS * 2);
    pthread_mutex_unlock(&g.lock);
    free(t);
    return ok;
}

/* The source application still holds the server clipboard when the client fetches, so rdpclip
 * answers the first request with CB_RESPONSE_FAIL. mstsc fetches lazily on paste and rarely hits
 * this; an eager fetch has to retry. */
static bool sc_remote_busy_once(unsigned i)
{
    pthread_mutex_lock(&g.lock);
    g.fail_next_requests = 1;
    pthread_mutex_unlock(&g.lock);
    bool ok = sc_remote_to_local(i);
    reset_server_behaviour();
    return ok;
}

/* Two clipboard changes in quick succession (Office and browsers commonly set the clipboard more
 * than once per copy). The client must end up with the latest text. Responses describe the
 * clipboard as it was announced, so the second response is the one that matters. */
static bool sc_remote_burst(unsigned i)
{
    pthread_mutex_lock(&g.lock);
    g.answer_with_snapshot = true;
    g.fail_delay_ms = 5;
    pthread_mutex_unlock(&g.lock);
    char* a = make_text("burst-a", i, 0);
    char* b = make_text("burst-b", i, 0);
    server_announce(a);
    server_announce(b);
    pthread_mutex_lock(&g.lock);
    bool ok = wait_until(pred_client_has, b, WAIT_MS);
    /* also make sure nothing else arrives after b and overwrites it */
    if (ok)
    {
        pthread_mutex_unlock(&g.lock);
        Sleep(150);
        pthread_mutex_lock(&g.lock);
        ok = pred_client_has(b);
    }
    pthread_mutex_unlock(&g.lock);
    free(a);
    free(b);
    reset_server_behaviour();
    return ok;
}

/* Copy a file on the server, then immediately copy text. The client starts fetching the file
 * descriptor and must not misinterpret it as text, nor drop the text that follows. */
static bool sc_remote_files_then_text(unsigned i)
{
    pthread_mutex_lock(&g.lock);
    g.offer_files_next = true;
    g.fail_delay_ms = 5;
    pthread_mutex_unlock(&g.lock);
    char* f = make_text("files", i, 0);
    char* t = make_text("after-files", i, 0);
    server_announce(f); /* advertises files + text; the descriptor answer arrives after the next list */
    server_announce(t);
    pthread_mutex_lock(&g.lock);
    bool ok = wait_until(pred_client_has, t, WAIT_MS);
    if (ok)
    {
        pthread_mutex_unlock(&g.lock);
        Sleep(150);
        pthread_mutex_lock(&g.lock);
        ok = pred_client_has(t);
    }
    pthread_mutex_unlock(&g.lock);
    free(f);
    free(t);
    reset_server_behaviour();
    return ok;
}

/* rdpclip stays busy for a while (three failed answers in a row). */
static bool sc_remote_busy_long(unsigned i)
{
    pthread_mutex_lock(&g.lock);
    g.fail_next_requests = 3;
    pthread_mutex_unlock(&g.lock);
    bool ok = sc_remote_to_local(i);
    reset_server_behaviour();
    return ok;
}

/* A copied item advertises a file list that can't be rendered (e.g. a virtual item from a shell
 * extension) along with text. The client must fall back to the text. */
static bool sc_remote_files_unavailable(unsigned i)
{
    pthread_mutex_lock(&g.lock);
    g.offer_files_next = true;
    g.fail_file_descriptors = true;
    pthread_mutex_unlock(&g.lock);
    bool ok = sc_remote_to_local(i);
    reset_server_behaviour();
    return ok;
}

static bool pred_text_last(const void* a)
{
    return g.client_last_event == 'T' && pred_client_has(a);
}

/* Copy a file on the server, and while it is still downloading, copy text. The finished file
 * download must not replace the newer text on the local clipboard. */
static bool sc_text_during_file_download(unsigned i)
{
    pthread_mutex_lock(&g.lock);
    g.offer_files_next = true;
    g.file_contents_delay_ms = 30;
    pthread_mutex_unlock(&g.lock);
    char* f = make_text("download", i, 0);
    char* t = make_text("during-download", i, 0);
    server_announce(f);
    Sleep(10); /* descriptor answered, contents still pending */
    server_announce(t);
    pthread_mutex_lock(&g.lock);
    bool ok = wait_until(pred_text_last, t, WAIT_MS);
    if (ok)
    {
        pthread_mutex_unlock(&g.lock);
        Sleep(250);
        pthread_mutex_lock(&g.lock);
        ok = pred_text_last(t);
    }
    pthread_mutex_unlock(&g.lock);
    free(f);
    free(t);
    reset_server_behaviour();
    return ok;
}

static bool pred_wire_has_v(const void* a) { (void)a; return strchr(g.wire_log, 'V') != NULL; }

/* Copy locally and press Ctrl+V in the session right away (alt-tab, paste). The server must see
 * the new clipboard announcement before the keystroke, or the remote app pastes stale content. */
static bool sc_paste_right_after_copy(unsigned i)
{
    pthread_mutex_lock(&g.lock);
    g.wire_len = 0;
    g.wire_log[0] = '\0';
    pthread_mutex_unlock(&g.lock);

    char* t = make_text("copy-then-paste", i, 0);
    rdp_session_clipboard_set_local_text(g.session, t);
    rdp_session_send_keyboard_event(g.session, 0, 0x1D);              /* Ctrl down */
    rdp_session_send_keyboard_event(g.session, 0, 0x2F);              /* V down */
    rdp_session_send_keyboard_event(g.session, KBD_FLAGS_RELEASE, 0x2F);
    rdp_session_send_keyboard_event(g.session, KBD_FLAGS_RELEASE, 0x1D);

    pthread_mutex_lock(&g.lock);
    bool ok = wait_until(pred_server_has, t, WAIT_MS) && wait_until(pred_wire_has_v, NULL, WAIT_MS);
    const char* l = strchr(g.wire_log, 'L');
    const char* v = strchr(g.wire_log, 'V');
    ok = ok && l && v && l < v;
    if (!ok)
        vlog("[test] wire order: %s\n", g.wire_log);
    pthread_mutex_unlock(&g.lock);
    free(t);
    return ok;
}

/* Client copies three times faster than the server can fetch; the server must end with the last. */
static bool sc_local_burst(unsigned i)
{
    pthread_mutex_lock(&g.lock);
    g.fail_delay_ms = 0;
    pthread_mutex_unlock(&g.lock);
    char* a = make_text("lburst-a", i, 0);
    char* b = make_text("lburst-b", i, 0);
    char* c = make_text("lburst-c", i, 0);
    rdp_session_clipboard_set_local_text(g.session, a);
    rdp_session_clipboard_set_local_text(g.session, b);
    Sleep(3);
    rdp_session_clipboard_set_local_text(g.session, c);
    pthread_mutex_lock(&g.lock);
    bool ok = wait_until(pred_server_has, c, WAIT_MS);
    pthread_mutex_unlock(&g.lock);
    free(a);
    free(b);
    free(c);
    return ok;
}

/* Copy on one side, then immediately on the other (user copies on the server, alt-tabs and copies
 * locally). Both directions must still be delivered. */
static bool sc_crossing(unsigned i)
{
    char* r = make_text("cross-remote", i, 0);
    char* l = make_text("cross-local", i, 0);
    server_announce(r);
    rdp_session_clipboard_set_local_text(g.session, l);
    pthread_mutex_lock(&g.lock);
    bool ok = wait_until(pred_server_has, l, WAIT_MS) && wait_until(pred_client_has, r, WAIT_MS);
    pthread_mutex_unlock(&g.lock);
    free(r);
    free(l);
    return ok;
}

typedef struct
{
    const char* name;
    scenario_fn fn;
    unsigned iterations_divisor;
} scenario;

static const scenario k_scenarios[] = {
    { "remote_to_local", sc_remote_to_local, 1 },
    { "local_to_remote", sc_local_to_remote, 1 },
    { "large_both_ways", sc_large_both_ways, 10 },
    { "remote_busy_once", sc_remote_busy_once, 1 },
    { "remote_busy_long", sc_remote_busy_long, 1 },
    { "remote_files_unavailable", sc_remote_files_unavailable, 1 },
    { "remote_burst", sc_remote_burst, 1 },
    { "text_during_file_download", sc_text_during_file_download, 1 },
    { "remote_files_then_text", sc_remote_files_then_text, 1 },
    { "local_burst", sc_local_burst, 1 },
    { "paste_right_after_copy", sc_paste_right_after_copy, 1 },
    { "crossing", sc_crossing, 1 },
};

static int make_cert(const char* dir, char* cert, char* key, size_t n)
{
    snprintf(cert, n, "%s/server.crt", dir);
    snprintf(key, n, "%s/server.key", dir);
    char cmd[1024];
    snprintf(cmd, sizeof(cmd),
             "openssl req -x509 -newkey rsa:2048 -nodes -keyout '%s' -out '%s' -days 2 "
             "-subj '/CN=rdpilot-e2e' >/dev/null 2>&1",
             key, cert);
    return system(cmd);
}

static bool pred_connected(const void* a) { (void)a; return g.status == 1 || g.status == 2 || g.status == 3; }

int main(int argc, char** argv)
{
    unsigned iterations = 50;
    const char* only = NULL;
    for (int i = 1; i < argc; i++)
    {
        if (strcmp(argv[i], "--iterations") == 0 && i + 1 < argc)
            iterations = (unsigned)atoi(argv[++i]);
        else if (strcmp(argv[i], "--scenario") == 0 && i + 1 < argc)
            only = argv[++i];
        else if (strcmp(argv[i], "--verbose") == 0)
            g_verbose = true;
        else
        {
            fprintf(stderr, "usage: %s [--iterations N] [--scenario NAME] [--verbose]\n", argv[0]);
            return 2;
        }
    }

    if (!g_verbose)
    {
        /* the wrapper logs every clipboard step to stdout; keep the report readable */
        if (!freopen("/dev/null", "w", stdout))
            return 2;
        WLog_SetLogLevel(WLog_GetRoot(), WLOG_FATAL);
    }

    char dir[] = "/tmp/rdpilot-e2e-XXXXXX";
    if (!mkdtemp(dir))
        return 2;
    char cert[512], key[512];
    if (make_cert(dir, cert, key, sizeof(cert)) != 0)
    {
        fprintf(stderr, "openssl failed to create a test certificate\n");
        return 2;
    }
    g_cert_path = cert;
    g_key_path = key;

    WTSRegisterWtsApiFunctionTable(FreeRDP_InitWtsApi());
    winpr_InitializeSSL(WINPR_SSL_INIT_DEFAULT);
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);

    freerdp_listener* listener = freerdp_listener_new();
    listener->PeerAccepted = peer_accepted;
    const UINT16 port = (UINT16)(40000 + (getpid() % 20000));
    if (!listener->Open(listener, "127.0.0.1", port))
    {
        fprintf(stderr, "failed to listen on 127.0.0.1:%u\n", port);
        return 2;
    }
    HANDLE lt = CreateThread(NULL, 0, listener_thread, listener, 0, NULL);
    (void)lt;

    g.session = rdp_session_connect("127.0.0.1", "127.0.0.1", port, "", "", "", NULL, NULL, NULL, NULL,
                                    1024, 768, 32, true, false, true, false, false, false, false, 0, false,
                                    false, 0, 100, 100,
                                    on_frame, on_clip_text, on_clip_files, on_status, on_cert, on_cursor);
    if (!g.session)
    {
        fprintf(stderr, "rdp_session_connect returned NULL\n");
        return 2;
    }

    /* Copy locally before the clipboard channel is up: the server must still get it. */
    static const char k_early[] = "copied before the session was ready \xE2\x9C\x93";
    rdp_session_clipboard_set_local_text(g.session, k_early);

    pthread_mutex_lock(&g.lock);
    bool ready = wait_until(pred_connected, NULL, 15000) && g.status == 1 && wait_until(pred_server_ready, NULL, 15000);
    const bool early_ok = ready && wait_until(pred_server_has, k_early, WAIT_MS);
    pthread_mutex_unlock(&g.lock);
    if (!ready)
    {
        fprintf(stderr, "session did not become clipboard-ready (status=%d)\n", g.status);
        return 2;
    }

    int failures = 0;
    fprintf(stderr, "%-26s %10s %8s\n", "scenario", "passed", "avg ms");
    if (!only || strcmp(only, "local_before_ready") == 0)
    {
        fprintf(stderr, "%-26s %6u/%-4u %8s\n", "local_before_ready", early_ok ? 1u : 0u, 1u, "-");
        if (!early_ok)
            failures++;
    }
    for (size_t s = 0; s < sizeof(k_scenarios) / sizeof(k_scenarios[0]); s++)
    {
        const scenario* sc = &k_scenarios[s];
        if (only && strcmp(only, sc->name) != 0)
            continue;
        unsigned n = iterations / sc->iterations_divisor;
        if (n == 0)
            n = 1;
        unsigned passed = 0;
        const uint64_t start = now_ms();
        for (unsigned i = 0; i < n; i++)
        {
            if (sc->fn(i))
                passed++;
            else
                vlog("[test] %s iteration %u FAILED\n", sc->name, i);
            /* let in-flight PDUs settle so one iteration cannot poison the next */
            Sleep(20);
            reset_server_behaviour();
        }
        const double avg = (double)(now_ms() - start) / n;
        fprintf(stderr, "%-26s %6u/%-4u %8.1f\n", sc->name, passed, n, avg);
        if (passed != n)
            failures++;
    }

    rdp_session_disconnect(g.session);
    rdp_session_free(g.session);
    char cmd[600];
    snprintf(cmd, sizeof(cmd), "rm -rf '%s'", dir);
    if (system(cmd) != 0)
        fprintf(stderr, "could not remove %s\n", dir);
    return failures ? 1 : 0;
}
