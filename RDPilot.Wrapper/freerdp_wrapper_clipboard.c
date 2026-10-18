#include "freerdp_wrapper_internal.h"

static void fail_remote_file_contents_requests(rdp_session* session);

void wrapper_clipboard_attach(rdp_session* session, CliprdrClientContext* cliprdr)
{
    if (!session) return;
    session->cliprdr = cliprdr;
    if (!cliprdr) return;
    cliprdr->custom = session;
    cliprdr->ServerCapabilities = on_cliprdr_server_capabilities;
    cliprdr->MonitorReady = on_cliprdr_monitor_ready;
    cliprdr->ServerFormatList = on_cliprdr_server_format_list;
    cliprdr->ServerFormatDataRequest = on_cliprdr_server_format_data_request;
    cliprdr->ServerFileContentsRequest = on_cliprdr_server_file_contents_request;
    cliprdr->ServerFormatDataResponse = on_cliprdr_server_format_data_response;
    cliprdr->ServerFileContentsResponse = on_cliprdr_server_file_contents_response;
}

void wrapper_clipboard_detach(rdp_session* session)
{
    if (!session) return;
    fail_remote_file_contents_requests(session);
    session->cliprdr = NULL;
}

// Standard clipboard format constants (from Winuser.h)
#ifndef CF_TEXT
#define CF_TEXT 1
#endif
#ifndef CF_BITMAP  
#define CF_BITMAP 2
#endif
#ifndef CF_DIB
#define CF_DIB 8
#endif
#ifndef CF_HDROP
#define CF_HDROP 15
#endif
#ifndef CF_DIBV5
#define CF_DIBV5 17
#endif
#ifndef CF_UNICODETEXT
#define CF_UNICODETEXT 13
#endif

// File clipboard format strings — must be narrow (char*) strings, NOT wide (wchar_t*).
// On Windows, shlobj.h defines CFSTR_FILEDESCRIPTOR as TEXT("FileGroupDescriptorW"),
// which becomes a wide string when UNICODE is defined. FreeRDP's CLIPRDR_FORMAT.formatName
// is a char*, so we must use explicit narrow string literals here.
#define RDPILOT_CFSTR_FILEDESCRIPTOR "FileGroupDescriptorW"
#define RDPILOT_CFSTR_FILECONTENTS   "FileContents"

#ifndef S_ISDIR
#define S_ISDIR(m) (((m) & _S_IFMT) == _S_IFDIR)
#endif

typedef struct
{
    BYTE* data;
    size_t size;
} clipboard_buffer;

typedef struct
{
    UINT32 cItems;
    FILEDESCRIPTORW fgd[1];
} RDPILOT_FILEGROUPDESCRIPTORW;

static UINT request_remote_file_descriptor(rdp_session* session);
static void update_supported_local_formats(rdp_session* session);

static const char* basename_from_path(const char* path)
{
    const char* slash = strrchr(path, '/');
    const char* backslash = strrchr(path, '\\');
    const char* base = slash && backslash ? (slash > backslash ? slash : backslash) : (slash ? slash : backslash);
    return base ? base + 1 : path;
}

static bool get_file_size_and_attributes(const char* path, UINT64* size, DWORD* attributes)
{
    if (!path || !size || !attributes)
        return false;

    *size = 0;
    *attributes = 0x80; /* FILE_ATTRIBUTE_NORMAL */

#if defined(_WIN32)
    WCHAR* wide_path = ConvertUtf8ToWCharAlloc(path, NULL);
    if (!wide_path)
        return false;
    WIN32_FILE_ATTRIBUTE_DATA data;
    BOOL found = GetFileAttributesExW(wide_path, GetFileExInfoStandard, &data);
    free(wide_path);
    if (!found || (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
        return false;
    *size = ((UINT64)data.nFileSizeHigh << 32) | data.nFileSizeLow;
    *attributes = data.dwFileAttributes;
#else
    struct stat st = {0};
    if (stat(path, &st) != 0)
        return false;
    if (S_ISDIR(st.st_mode))
        return false;
    *size = (UINT64)st.st_size;
#endif
    return true;
}

static FILETIME unix_time_to_filetime(INT64 unix_time)
{
    const UINT64 windows_ticks = ((UINT64)(unix_time + 11644473600LL)) * 10000000ULL;
    FILETIME ft;
    ft.dwLowDateTime = (DWORD)(windows_ticks & 0xFFFFFFFFu);
    ft.dwHighDateTime = (DWORD)(windows_ticks >> 32);
    return ft;
}

static bool read_file_range(const char* path, UINT64 offset, UINT32 requested, clipboard_buffer* buffer)
{
    if (!path || !buffer)
        return false;

    memset(buffer, 0, sizeof(*buffer));

#if defined(_WIN32)
    WCHAR* wide_path = ConvertUtf8ToWCharAlloc(path, NULL);
    if (!wide_path)
        return false;
    FILE* fp = NULL;
    _wfopen_s(&fp, wide_path, L"rb");
    free(wide_path);
#else
    FILE* fp = fopen(path, "rb");
#endif
    if (!fp)
        return false;

#if defined(_WIN32)
    if (_fseeki64(fp, (int64_t)offset, SEEK_SET) != 0)
#else
    if (fseeko(fp, (off_t)offset, SEEK_SET) != 0)
#endif
    {
        fclose(fp);
        return false;
    }

    buffer->data = (BYTE*)malloc(requested);
    if (!buffer->data)
    {
        fclose(fp);
        return false;
    }

    buffer->size = fread(buffer->data, 1, requested, fp);
    fclose(fp);
    if (buffer->size == 0)
    {
        free(buffer->data);
        memset(buffer, 0, sizeof(*buffer));
        return false;
    }

    return true;
}

static bool build_file_group_descriptor_w(rdp_session* session, BYTE** outData, size_t* outSize)
{
    if (!session || !outData || !outSize)
        return false;

    *outData = NULL;
    *outSize = 0;

    EnterCriticalSection(&session->clipboard_lock);
    const size_t fileCount = session->local_file_paths_count;
    if (fileCount == 0)
    {
        LeaveCriticalSection(&session->clipboard_lock);
        return false;
    }

    const size_t allocSize = sizeof(UINT32) + fileCount * sizeof(FILEDESCRIPTORW);
    RDPILOT_FILEGROUPDESCRIPTORW* descriptor = (RDPILOT_FILEGROUPDESCRIPTORW*)calloc(1, allocSize);
    if (!descriptor)
    {
        LeaveCriticalSection(&session->clipboard_lock);
        return false;
    }

    descriptor->cItems = (UINT32)fileCount;
    for (size_t i = 0; i < fileCount; i++)
    {
        const char* path = session->local_file_paths[i];
        FILEDESCRIPTORW* f = &descriptor->fgd[i];
        memset(f, 0, sizeof(*f));

        const char* base = basename_from_path(path);

#if defined(_WIN32)
        /* Match wfreerdp exactly: use Win32 APIs for file metadata */
        {
            WCHAR* wide_path = ConvertUtf8ToWCharAlloc(path, NULL);
            if (!wide_path)
            {
                free(descriptor);
                LeaveCriticalSection(&session->clipboard_lock);
                return false;
            }

            HANDLE hFile = CreateFileW(wide_path, GENERIC_READ, FILE_SHARE_READ, NULL,
                                        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
            if (hFile == INVALID_HANDLE_VALUE)
            {
                free(wide_path);
                free(descriptor);
                LeaveCriticalSection(&session->clipboard_lock);
                return false;
            }

            f->dwFlags = FD_ATTRIBUTES | FD_FILESIZE | FD_WRITESTIME | FD_PROGRESSUI;
            f->dwFileAttributes = GetFileAttributesW(wide_path);
            free(wide_path);
            if (!GetFileTime(hFile, NULL, NULL, &f->ftLastWriteTime))
            {
                f->dwFlags &= ~FD_WRITESTIME;
            }
            f->nFileSizeLow = GetFileSize(hFile, &f->nFileSizeHigh);
            CloseHandle(hFile);

            /* Copy filename manually - wcscpy_s fills remaining buffer with 0xFE in MSVC debug mode */
            WCHAR* wide_name = ConvertUtf8ToWCharAlloc(base, NULL);
            if (!wide_name)
            {
                free(descriptor);
                LeaveCriticalSection(&session->clipboard_lock);
                return false;
            }
            size_t nameLen = wcslen(wide_name);
            if (nameLen >= ARRAYSIZE(f->cFileName))
                nameLen = ARRAYSIZE(f->cFileName) - 1;
            memcpy(f->cFileName, wide_name, nameLen * sizeof(WCHAR));
            f->cFileName[nameLen] = 0;
            free(wide_name);
        }
#else
        UINT64 size = 0;
        DWORD attributes = 0;
        if (!get_file_size_and_attributes(path, &size, &attributes))
        {
            free(descriptor);
            LeaveCriticalSection(&session->clipboard_lock);
            return false;
        }

        f->dwFlags = FD_ATTRIBUTES | FD_FILESIZE | FD_WRITESTIME | FD_PROGRESSUI;
        f->dwFileAttributes = attributes;
        f->nFileSizeHigh = (DWORD)(size >> 32);
        f->nFileSizeLow = (DWORD)(size & 0xFFFFFFFFu);

        struct stat st = {0};
        if (stat(path, &st) == 0)
        {
            f->ftLastWriteTime = unix_time_to_filetime((INT64)st.st_mtime);
        }

        size_t wchar_len = 0;
        WCHAR* wide_name = ConvertUtf8ToWCharAlloc(base, &wchar_len);
        if (!wide_name)
        {
            free(descriptor);
            LeaveCriticalSection(&session->clipboard_lock);
            return false;
        }

        size_t copy_len = wchar_len < 259 ? wchar_len : 259;
        memcpy(f->cFileName, wide_name, copy_len * sizeof(WCHAR));
        f->cFileName[copy_len] = 0;
        free(wide_name);
#endif

        
    }

    LeaveCriticalSection(&session->clipboard_lock);

    /* Send the raw FILEGROUPDESCRIPTORW struct directly, exactly like wfreerdp.
     * cliprdr_serialize_file_list_ex produces identical bytes but adds an unnecessary
     * layer that could theoretically differ on some platforms. */
    *outData = (BYTE*)descriptor;
    *outSize = allocSize;
    return true;
}

static void free_local_bitmap_data(rdp_session* session)
{
    if (session && session->local_bitmap_data)
    {
        free(session->local_bitmap_data);
        session->local_bitmap_data = NULL;
        session->local_bitmap_data_size = 0;
        session->local_bitmap_width = 0;
        session->local_bitmap_height = 0;
    }
}

static void free_supported_local_formats(rdp_session* session)
{
    if (session && session->supported_local_formats)
    {
        free(session->supported_local_formats);
        session->supported_local_formats = NULL;
        session->supported_local_formats_count = 0;
        session->supported_local_formats_capacity = 0;
    }
}

static void free_local_file_paths(rdp_session* session)
{
    if (session && session->local_file_paths)
    {
        for (size_t i = 0; i < session->local_file_paths_count; i++)
        {
            free(session->local_file_paths[i]);
        }
        free(session->local_file_paths);
        session->local_file_paths = NULL;
        session->local_file_paths_count = 0;
        session->local_file_paths_capacity = 0;
    }
}

/* Retry schedule for a remote data request the server answered with CB_RESPONSE_FAIL. Windows'
 * rdpclip fails a request when it cannot open the clipboard, which is common right after a copy
 * because the source application (or a clipboard history/manager) still holds it. We fetch
 * eagerly at that exact moment, unlike mstsc which fetches on paste, so we must retry. */
static const UINT32 k_remote_retry_delays_ms[] = { 25, 50, 100, 200, 400, 800 };
#define REMOTE_RETRY_MAX_ATTEMPTS (sizeof(k_remote_retry_delays_ms) / sizeof(k_remote_retry_delays_ms[0]))

static void reset_remote_requests(rdp_session* session)
{
    if (!session)
        return;
    session->remote_requests_head = 0;
    session->remote_requests_count = 0;
    session->remote_retry_pending = false;
    session->remote_retry_attempt = 0;
}

static UINT send_remote_request(rdp_session* session, remote_request request)
{
    if (!session || !session->cliprdr)
        return CHANNEL_RC_OK;

    if (session->remote_requests_count == REMOTE_REQUEST_QUEUE_CAPACITY)
    {
        /* The server stopped answering; the FIFO can no longer be trusted. Start over. */
        fprintf(stderr, "[CLIPRDR] remote request queue overflow, resetting\n");
        session->remote_requests_head = 0;
        session->remote_requests_count = 0;
    }

    size_t tail = (session->remote_requests_head + session->remote_requests_count) % REMOTE_REQUEST_QUEUE_CAPACITY;
    session->remote_requests[tail] = request;
    session->remote_requests_count++;

    CLIPRDR_FORMAT_DATA_REQUEST pdu;
    memset(&pdu, 0, sizeof(pdu));
    pdu.common.msgType = CB_FORMAT_DATA_REQUEST;
    pdu.requestedFormatId = request.format_id;
    printf("[CLIPRDR] request remote %s format=%u generation=%u\n",
           request.kind == REMOTE_REQUEST_TEXT ? "Unicode text" : "file descriptor",
           request.format_id, request.generation);
    UINT rc = session->cliprdr->ClientFormatDataRequest(session->cliprdr, &pdu);
    log_channel_rc("ClientFormatDataRequest", rc);
    if (rc != CHANNEL_RC_OK)
    {
        /* Nothing went on the wire, so no response will come for it. */
        session->remote_requests_count--;
    }
    return rc;
}

static bool pop_remote_request(rdp_session* session, remote_request* out)
{
    if (!session || session->remote_requests_count == 0)
        return false;
    *out = session->remote_requests[session->remote_requests_head];
    session->remote_requests_head = (session->remote_requests_head + 1) % REMOTE_REQUEST_QUEUE_CAPACITY;
    session->remote_requests_count--;
    return true;
}

static UINT request_remote_text(rdp_session* session)
{
    remote_request request = { REMOTE_REQUEST_TEXT, CF_UNICODETEXT, session->remote_generation };
    return send_remote_request(session, request);
}

static UINT request_remote_file_descriptor(rdp_session* session)
{
    if (!session || !session->cliprdr || session->remote_file_group_descriptor_format_id == 0)
        return CHANNEL_RC_OK;

    remote_request request = { REMOTE_REQUEST_FILE_DESCRIPTOR, session->remote_file_group_descriptor_format_id, session->remote_generation };
    return send_remote_request(session, request);
}

/* A request for the current clipboard generation failed. Retry with backoff; once retries are
 * exhausted for a file list, fall back to the text the server offered alongside it. */
static UINT handle_failed_remote_request(rdp_session* session, remote_request request)
{
    if (request.generation != session->remote_generation)
        return CHANNEL_RC_OK;

    if (session->remote_retry_attempt < REMOTE_RETRY_MAX_ATTEMPTS)
    {
        UINT32 delay = k_remote_retry_delays_ms[session->remote_retry_attempt];
        session->remote_retry_attempt++;
        session->remote_retry = request;
        session->remote_retry_pending = true;
        session->remote_retry_due_ms = GetTickCount64() + delay;
        printf("[CLIPRDR] remote request failed, retry %u in %ums\n", session->remote_retry_attempt, delay);
        return CHANNEL_RC_OK;
    }

    session->remote_retry_attempt = 0;
    if (request.kind == REMOTE_REQUEST_FILE_DESCRIPTOR && session->remote_offers_text)
    {
        printf("[CLIPRDR] remote file list unavailable, falling back to text\n");
        return request_remote_text(session);
    }

    printf("[CLIPRDR] remote request failed, giving up after %u retries\n", (unsigned)REMOTE_RETRY_MAX_ATTEMPTS);
    return CHANNEL_RC_OK;
}

static void process_remote_retry(rdp_session* session)
{
    if (!session->remote_retry_pending || GetTickCount64() < session->remote_retry_due_ms)
        return;

    session->remote_retry_pending = false;
    if (session->remote_retry.generation != session->remote_generation)
        return;

    send_remote_request(session, session->remote_retry);
}

static void notify_file_contents(rdp_session* session, UINT32 stream_id, bool ok, const BYTE* data, UINT32 size)
{
    if (session->clipboard_file_contents_callback)
        session->clipboard_file_contents_callback(session, stream_id, ok, ok ? data : NULL, ok ? size : 0);
}

/* Fails every file contents request the host is still waiting on, so it never waits for an answer
 * that cannot come: the server clipboard they refer to is gone, or the channel is. */
static void fail_remote_file_contents_requests(rdp_session* session)
{
    file_contents_request queued[FILE_CONTENTS_REQUEST_CAPACITY];
    EnterCriticalSection(&session->clipboard_lock);
    const size_t queued_count = session->file_contents_queue_count;
    memcpy(queued, session->file_contents_queue, queued_count * sizeof(file_contents_request));
    session->file_contents_queue_count = 0;
    LeaveCriticalSection(&session->clipboard_lock);

    const size_t outstanding_count = session->file_contents_outstanding_count;
    session->file_contents_outstanding_count = 0;
    for (size_t i = 0; i < outstanding_count; i++)
        notify_file_contents(session, session->file_contents_outstanding[i].stream_id, false, NULL, 0);
    for (size_t i = 0; i < queued_count; i++)
        notify_file_contents(session, queued[i].stream_id, false, NULL, 0);
}

static bool process_pending_file_contents_requests(rdp_session* session)
{
    file_contents_request batch[FILE_CONTENTS_REQUEST_CAPACITY];
    EnterCriticalSection(&session->clipboard_lock);
    const size_t count = session->file_contents_queue_count;
    memcpy(batch, session->file_contents_queue, count * sizeof(file_contents_request));
    session->file_contents_queue_count = 0;
    LeaveCriticalSection(&session->clipboard_lock);

    bool sent = false;
    for (size_t i = 0; i < count; i++)
    {
        const file_contents_request* r = &batch[i];
        if (r->generation != session->remote_generation || session->remote_file_contents_format_id == 0 ||
            session->file_contents_outstanding_count == FILE_CONTENTS_REQUEST_CAPACITY)
        {
            notify_file_contents(session, r->stream_id, false, NULL, 0);
            continue;
        }

        CLIPRDR_FILE_CONTENTS_REQUEST pdu;
        memset(&pdu, 0, sizeof(pdu));
        pdu.common.msgType = CB_FILECONTENTS_REQUEST;
        pdu.streamId = r->stream_id;
        pdu.listIndex = r->list_index;
        pdu.dwFlags = r->size_only ? FILECONTENTS_SIZE : FILECONTENTS_RANGE;
        pdu.nPositionLow = r->size_only ? 0 : (UINT32)(r->offset & 0xFFFFFFFFu);
        pdu.nPositionHigh = r->size_only ? 0 : (UINT32)(r->offset >> 32);
        pdu.cbRequested = r->size_only ? sizeof(UINT64) : r->size;
        UINT rc = session->cliprdr->ClientFileContentsRequest(session->cliprdr, &pdu);
        if (rc != CHANNEL_RC_OK)
        {
            log_channel_rc("ClientFileContentsRequest", rc);
            notify_file_contents(session, r->stream_id, false, NULL, 0);
            continue;
        }

        session->file_contents_outstanding[session->file_contents_outstanding_count++] = *r;
        sent = true;
    }
    return sent;
}

void free_local_clipboard_text(rdp_session* session)
{
    if (session && session->local_clipboard_text)
    {
        free(session->local_clipboard_text);
        session->local_clipboard_text = NULL;
    }
}

void free_clipboard_data(rdp_session* session)
{
    if (!session) return;
    
    free_local_clipboard_text(session);
    free_supported_local_formats(session);
    free_local_file_paths(session);
    free_local_bitmap_data(session);
}

static UINT send_clipboard_format_list(rdp_session* session)
{
    if (!session || !session->cliprdr) return CHANNEL_RC_OK;

    // Count total formats (standard + file)
    UINT32 total_formats = 0;
    bool has_files = false;
    EnterCriticalSection(&session->clipboard_lock);
    total_formats = (UINT32)session->supported_local_formats_count;
    
    // Add file formats if we have files and registered format ids
    if (session->local_file_paths_count > 0 && session->file_group_descriptor_format_id != 0 && session->file_contents_format_id != 0)
    {
        has_files = true;
        total_formats += 2; // CFSTR_FILEDESCRIPTOR + CFSTR_FILECONTENTS
    }
    
    if (total_formats == 0)
    {
        LeaveCriticalSection(&session->clipboard_lock);
        // Send empty format list
        CLIPRDR_FORMAT_LIST format_list;
        memset(&format_list, 0, sizeof(format_list));
        format_list.common.msgType = CB_FORMAT_LIST;
        format_list.common.msgFlags = 0;
        format_list.numFormats = 0;
        format_list.formats = NULL;

        printf("[CLIPRDR] send empty local format list\n");
        UINT rc = session->cliprdr->ClientFormatList(session->cliprdr, &format_list);
        log_channel_rc("ClientFormatList", rc);
        return rc;
    }

    // Build format list with both standard and file formats
    CLIPRDR_FORMAT* formats = (CLIPRDR_FORMAT*)calloc(total_formats, sizeof(CLIPRDR_FORMAT));
    if (!formats)
    {
        LeaveCriticalSection(&session->clipboard_lock);
        fprintf(stderr, "[CLIPRDR] failed to allocate format list\n");
        return ERROR_NOT_ENOUGH_MEMORY;
    }

    UINT32 format_index = 0;
    
    // Add standard formats
    for (UINT32 i = 0; i < session->supported_local_formats_count; i++)
    {
        formats[format_index].formatId = session->supported_local_formats[i];
        formats[format_index].formatName = NULL; // Standard formats use NULL name
        format_index++;
    }
    
    // Add file formats if we have files
    if (has_files)
    {
        // FileGroupDescriptorW format
        formats[format_index].formatId = session->file_group_descriptor_format_id;
        formats[format_index].formatName = duplicate_string(RDPILOT_CFSTR_FILEDESCRIPTOR);
        format_index++;

        // FileContents format  
        formats[format_index].formatId = session->file_contents_format_id;
        formats[format_index].formatName = duplicate_string(RDPILOT_CFSTR_FILECONTENTS);
        format_index++;
    }
    
    LeaveCriticalSection(&session->clipboard_lock);

    CLIPRDR_FORMAT_LIST format_list;
    memset(&format_list, 0, sizeof(format_list));
    format_list.common.msgType = CB_FORMAT_LIST;
    format_list.common.msgFlags = 0;
    format_list.numFormats = total_formats;
    format_list.formats = formats;

printf("[CLIPRDR] send local format list count=%u (files=%s)\n", 
           total_formats, has_files ? "true" : "false");
    UINT rc = session->cliprdr->ClientFormatList(session->cliprdr, &format_list);
    log_channel_rc("ClientFormatList", rc);
    
    // Clean up allocated format names
    for (UINT32 i = 0; i < total_formats; i++)
    {
        if (formats[i].formatName)
        {
            free(formats[i].formatName);
        }
    }
    free(formats);
    
    return rc;
}

static UINT send_clipboard_capabilities(rdp_session* session)
{
    if (!session || !session->cliprdr) return CHANNEL_RC_OK;

    CLIPRDR_GENERAL_CAPABILITY_SET general_capability;
    memset(&general_capability, 0, sizeof(general_capability));
    general_capability.capabilitySetType = CB_CAPSTYPE_GENERAL;
    general_capability.capabilitySetLength = CB_CAPSTYPE_GENERAL_LEN;
    general_capability.version = CB_CAPS_VERSION_2;
    // Match wfreerdp capabilities exactly
    general_capability.generalFlags = CB_USE_LONG_FORMAT_NAMES |
                                     CB_STREAM_FILECLIP_ENABLED |
                                     CB_FILECLIP_NO_FILE_PATHS;

    CLIPRDR_CAPABILITIES capabilities;
    memset(&capabilities, 0, sizeof(capabilities));
    capabilities.common.msgType = CB_CLIP_CAPS;
    capabilities.cCapabilitiesSets = 1;
    capabilities.capabilitySets = (CLIPRDR_CAPABILITY_SET*)&general_capability;

    printf("[CLIPRDR] send client capabilities flags=0x%08x\n", general_capability.generalFlags);
    UINT rc = session->cliprdr->ClientCapabilities(session->cliprdr, &capabilities);
    log_channel_rc("ClientCapabilities", rc);
    return rc;
}

static UINT send_clipboard_failed_data_response(rdp_session* session, const char* reason)
{
    if (!session || !session->cliprdr) return CHANNEL_RC_OK;

    CLIPRDR_FORMAT_DATA_RESPONSE response;
    memset(&response, 0, sizeof(response));
    response.common.msgType = CB_FORMAT_DATA_RESPONSE;
    response.common.msgFlags = CB_RESPONSE_FAIL;
    response.common.dataLen = 0;
    response.requestedFormatData = NULL;

    UINT rc = session->cliprdr->ClientFormatDataResponse(session->cliprdr, &response);
    log_channel_rc(reason, rc);
    return rc;
}

static UINT send_clipboard_data_response(rdp_session* session, UINT32 requested_format_id)
{
    if (!session || !session->cliprdr) return CHANNEL_RC_OK;

    CLIPRDR_FORMAT_DATA_RESPONSE response;
    memset(&response, 0, sizeof(response));
    response.common.msgType = CB_FORMAT_DATA_RESPONSE;

    // Handle text format
    if (requested_format_id == CF_UNICODETEXT)
    {
        EnterCriticalSection(&session->clipboard_lock);
        char* text_copy = session->local_clipboard_text ? duplicate_string(session->local_clipboard_text) : NULL;
        LeaveCriticalSection(&session->clipboard_lock);

        if (!text_copy)
        {
            printf("[CLIPRDR] remote requested local text but cache is empty\n");
            return send_clipboard_failed_data_response(session, "ClientFormatDataResponse empty");
        }

        size_t wchar_len = 0;
        WCHAR* wide_text = ConvertUtf8ToWCharAlloc(text_copy, &wchar_len);
        free(text_copy);

        if (!wide_text)
        {
            fprintf(stderr, "[CLIPRDR] failed to convert local UTF-8 clipboard text to UTF-16\n");
            return send_clipboard_failed_data_response(session, "ClientFormatDataResponse conversion");
        }

        response.common.msgFlags = CB_RESPONSE_OK;
        response.common.dataLen = (UINT32)((wchar_len + 1) * sizeof(WCHAR));
        response.requestedFormatData = (const BYTE*)wide_text;
        printf("[CLIPRDR] send local text response bytes=%u chars=%zu\n", response.common.dataLen, wchar_len);
        UINT rc = session->cliprdr->ClientFormatDataResponse(session->cliprdr, &response);
        log_channel_rc("ClientFormatDataResponse text", rc);
        free(wide_text);
        return rc;
    }
    
    // Handle bitmap formats
    if (requested_format_id == CF_DIB || requested_format_id == CF_BITMAP || requested_format_id == CF_DIBV5)
    {
        EnterCriticalSection(&session->clipboard_lock);
        if (!session->local_bitmap_data || session->local_bitmap_data_size == 0)
        {
            LeaveCriticalSection(&session->clipboard_lock);
            printf("[CLIPRDR] remote requested local bitmap but cache is empty\n");
            return send_clipboard_failed_data_response(session, "ClientFormatDataResponse bitmap empty");
        }
        
        // For now, return the raw bitmap data as-is
        // TODO: Convert to proper DIB format if needed
        response.common.msgFlags = CB_RESPONSE_OK;
        response.common.dataLen = (UINT32)session->local_bitmap_data_size;
        response.requestedFormatData = session->local_bitmap_data;
        printf("[CLIPRDR] send local bitmap response bytes=%u format=%u\n", response.common.dataLen, requested_format_id);
        UINT rc = session->cliprdr->ClientFormatDataResponse(session->cliprdr, &response);
        log_channel_rc("ClientFormatDataResponse bitmap", rc);
        LeaveCriticalSection(&session->clipboard_lock);
        return rc;
    }
    
    if (requested_format_id == session->file_group_descriptor_format_id)
    {
        BYTE* data = NULL;
        size_t data_size = 0;
        if (!build_file_group_descriptor_w(session, &data, &data_size))
        {
            printf("[CLIPRDR] remote requested unicode file descriptor but cache is empty\n");
            return send_clipboard_failed_data_response(session, "ClientFormatDataResponse file descriptor W empty");
        }

        response.common.msgFlags = CB_RESPONSE_OK;
        response.common.dataLen = (UINT32)data_size;
        response.requestedFormatData = data;
printf("[CLIPRDR] send local file descriptor response bytes=%u\n", response.common.dataLen);
        UINT rc = session->cliprdr->ClientFormatDataResponse(session->cliprdr, &response);
        log_channel_rc("ClientFormatDataResponse file descriptor", rc);
        free(data);
        return rc;
    }

    printf("[CLIPRDR] remote requested unsupported local format=%u\n", requested_format_id);
    return send_clipboard_failed_data_response(session, "ClientFormatDataResponse unsupported");
}

static UINT send_clipboard_failed_file_contents_response(rdp_session* session, UINT32 stream_id, const char* reason)
{
    CLIPRDR_FILE_CONTENTS_RESPONSE response;
    memset(&response, 0, sizeof(response));
    response.common.msgType = CB_FILECONTENTS_RESPONSE;
    response.common.msgFlags = CB_RESPONSE_FAIL;
    response.streamId = stream_id;
    UINT rc = session->cliprdr->ClientFileContentsResponse(session->cliprdr, &response);
    log_channel_rc(reason, rc);
    return rc;
}

static UINT send_clipboard_file_contents_response(rdp_session* session,
                                                  const CLIPRDR_FILE_CONTENTS_REQUEST* request)
{
    if (!session || !session->cliprdr || !request)
        return CHANNEL_RC_OK;

    clipboard_buffer buffer = {0};
    UINT64 fileSize = 0;
    DWORD attributes = 0;
    bool ok = false;

    EnterCriticalSection(&session->clipboard_lock);
    const size_t index = request->listIndex;
    const char* stored_path = (index < session->local_file_paths_count) ? session->local_file_paths[index] : NULL;
    char* path = stored_path ? duplicate_string(stored_path) : NULL;
    if (path)
        ok = get_file_size_and_attributes(path, &fileSize, &attributes);
    LeaveCriticalSection(&session->clipboard_lock);

    if (!ok || !path)
    {
        free(path);
        printf("[CLIPRDR] remote requested file contents for invalid index=%u\n", request->listIndex);
        return send_clipboard_failed_file_contents_response(session, request->streamId, "ClientFileContentsResponse invalid index");
    }

    if (request->dwFlags & FILECONTENTS_SIZE)
    {
        CLIPRDR_FILE_CONTENTS_RESPONSE response;
        memset(&response, 0, sizeof(response));
        response.common.msgType = CB_FILECONTENTS_RESPONSE;
        response.common.msgFlags = CB_RESPONSE_OK;
        response.streamId = request->streamId;
        response.cbRequested = sizeof(UINT64);
        response.requestedData = (const BYTE*)&fileSize;
        UINT rc = session->cliprdr->ClientFileContentsResponse(session->cliprdr, &response);
        log_channel_rc("ClientFileContentsResponse size", rc);
        free(path);
        return rc;
    }

    UINT64 offset = ((UINT64)request->nPositionHigh << 32) | request->nPositionLow;
    UINT32 wanted = request->cbRequested;
    if (offset > fileSize)
    {
        free(path);
        return send_clipboard_failed_file_contents_response(session, request->streamId, "ClientFileContentsResponse offset");
    }

    UINT64 remaining = fileSize - offset;
    if (remaining < wanted)
        wanted = (UINT32)remaining;
    if (wanted == 0)
    {
        free(path);
        return send_clipboard_failed_file_contents_response(session, request->streamId, "ClientFileContentsResponse eof");
    }

    if (!read_file_range(path, offset, wanted, &buffer))
    {
        free(path);
        printf("[CLIPRDR] failed to read file contents index=%u offset=%" PRIu64 " wanted=%u\n", request->listIndex, offset, wanted);
        return send_clipboard_failed_file_contents_response(session, request->streamId, "ClientFileContentsResponse read");
    }
    free(path);

    CLIPRDR_FILE_CONTENTS_RESPONSE response;
    memset(&response, 0, sizeof(response));
    response.common.msgType = CB_FILECONTENTS_RESPONSE;
    response.common.msgFlags = CB_RESPONSE_OK;
    response.streamId = request->streamId;
    response.cbRequested = (UINT32)buffer.size;
    response.requestedData = buffer.data;
    UINT rc = session->cliprdr->ClientFileContentsResponse(session->cliprdr, &response);
    log_channel_rc("ClientFileContentsResponse data", rc);
    free(buffer.data);
    return rc;
}

UINT on_cliprdr_server_capabilities(CliprdrClientContext* context,
                                           const CLIPRDR_CAPABILITIES* capabilities)
{
    (void)context;
    printf("[CLIPRDR] server capabilities sets=%u\n", capabilities ? capabilities->cCapabilitiesSets : 0);
    return CHANNEL_RC_OK;
}

UINT on_cliprdr_monitor_ready(CliprdrClientContext* context, const CLIPRDR_MONITOR_READY* monitorReady)
{
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;
    (void)monitorReady;
    printf("[CLIPRDR] monitor ready\n");
    reset_remote_requests(session);
    UINT rc = send_clipboard_capabilities(session);
    if (rc != CHANNEL_RC_OK) return rc;
    return send_clipboard_format_list(session);
}

UINT on_cliprdr_server_format_list(CliprdrClientContext* context, const CLIPRDR_FORMAT_LIST* formatList)
{
    bool has_unicode_text = false;
    bool has_bitmap = false;
    bool has_files = false;
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;

    if (session)
    {
        /* A new server clipboard supersedes everything we were fetching for the previous one:
         * answers still in flight are recognized by their old generation and dropped, a pending
         * retry is abandoned, and a half-finished file download must not land after this. */
        session->remote_generation++;
        session->remote_retry_pending = false;
        session->remote_retry_attempt = 0;
        fail_remote_file_contents_requests(session);
        session->remote_file_group_descriptor_format_id = 0;
        session->remote_file_contents_format_id = 0;
    }

    for (UINT32 i = 0; i < formatList->numFormats; i++)
    {
        UINT32 format_id = formatList->formats[i].formatId;
        if (format_id == CF_UNICODETEXT)
        {
            has_unicode_text = true;
        }
        else if (format_id == CF_DIB || format_id == CF_BITMAP || format_id == CF_DIBV5)
        {
            has_bitmap = true;
        }
        else if (session && formatList->formats[i].formatName)
        {
            if (strcmp(formatList->formats[i].formatName, RDPILOT_CFSTR_FILEDESCRIPTOR) == 0)
            {
                session->remote_file_group_descriptor_format_id = format_id;
                has_files = session->remote_file_contents_format_id != 0;
            }
            else if (strcmp(formatList->formats[i].formatName, RDPILOT_CFSTR_FILECONTENTS) == 0)
            {
                session->remote_file_contents_format_id = format_id;
                has_files = session->remote_file_group_descriptor_format_id != 0;
            }
        }
    }

    has_files = session && session->remote_file_group_descriptor_format_id != 0 && session->remote_file_contents_format_id != 0;
    if (session)
        session->remote_offers_text = has_unicode_text;

    printf("[CLIPRDR] server formats count=%u unicodeText=%s bitmap=%s files=%s\n",
           formatList->numFormats, has_unicode_text ? "true" : "false", 
           has_bitmap ? "true" : "false", has_files ? "true" : "false");

    CLIPRDR_FORMAT_LIST_RESPONSE list_response;
    memset(&list_response, 0, sizeof(list_response));
    list_response.common.msgType = CB_FORMAT_LIST_RESPONSE;
    list_response.common.msgFlags = CB_RESPONSE_OK;
    if (session && session->cliprdr)
    {
        UINT rc = session->cliprdr->ClientFormatListResponse(session->cliprdr, &list_response);
        if (rc != CHANNEL_RC_OK)
        {
            log_channel_rc("ClientFormatListResponse", rc);
            return rc;
        }
    }

    // File copies often advertise text too (the filenames). Prefer the file data.
    if (has_files && session && session->cliprdr)
    {
        return request_remote_file_descriptor(session);
    }

    if (has_unicode_text && session && session->cliprdr)
    {
        return request_remote_text(session);
    }

    return CHANNEL_RC_OK;
}

UINT on_cliprdr_server_format_data_request(CliprdrClientContext* context,
                                                   const CLIPRDR_FORMAT_DATA_REQUEST* formatDataRequest)
{
rdp_session* session = context ? (rdp_session*)context->custom : NULL;
    return send_clipboard_data_response(session, formatDataRequest->requestedFormatId);
}

UINT on_cliprdr_server_file_contents_request(CliprdrClientContext* context,
                                                   const CLIPRDR_FILE_CONTENTS_REQUEST* fileContentsRequest)
{
rdp_session* session = context ? (rdp_session*)context->custom : NULL;
    return send_clipboard_file_contents_response(session, fileContentsRequest);
}

UINT on_cliprdr_server_format_data_response(CliprdrClientContext* context,
                                                     const CLIPRDR_FORMAT_DATA_RESPONSE* formatDataResponse)
{
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;
    if (!session)
        return CHANNEL_RC_OK;

    remote_request request;
    if (!pop_remote_request(session, &request))
    {
        printf("[CLIPRDR] unexpected remote data response (no request outstanding), ignoring\n");
        return CHANNEL_RC_OK;
    }

    if (request.generation != session->remote_generation)
    {
        /* Answer to a request for a clipboard the server has since replaced. A request for the
         * current clipboard is already queued behind it. */
        printf("[CLIPRDR] dropping stale remote response generation=%u current=%u\n", request.generation, session->remote_generation);
        return CHANNEL_RC_OK;
    }

    if (!(formatDataResponse->common.msgFlags & CB_RESPONSE_OK) || !formatDataResponse->requestedFormatData)
    {
        printf("[CLIPRDR] remote data response failed flags=0x%04x format=%u\n", formatDataResponse->common.msgFlags, request.format_id);
        return handle_failed_remote_request(session, request);
    }

    session->remote_retry_attempt = 0;

    if (request.kind == REMOTE_REQUEST_FILE_DESCRIPTOR)
    {
        const UINT32 data_len = formatDataResponse->common.dataLen;
        const BYTE* data = formatDataResponse->requestedFormatData;
        const UINT32 item_count = data_len >= sizeof(UINT32) ? *(const UINT32*)data : 0;
        const size_t expected_len = sizeof(UINT32) + (size_t)item_count * sizeof(FILEDESCRIPTORW);
        if (item_count == 0 || data_len < expected_len)
        {
            printf("[CLIPRDR] remote file descriptor payload invalid items=%u bytes=%u\n", item_count, data_len);
            return CHANNEL_RC_OK;
        }

        printf("[CLIPRDR] remote file descriptor received items=%u generation=%u\n", item_count, request.generation);
        if (session->clipboard_files_callback)
            session->clipboard_files_callback(session, request.generation, data, expected_len);
        return CHANNEL_RC_OK;
    }

    size_t wchar_len = formatDataResponse->common.dataLen / sizeof(WCHAR);
    if (wchar_len == 0)
    {
        return CHANNEL_RC_OK;
    }

    size_t utf8_len = 0;
    char* text = ConvertWCharNToUtf8Alloc((const WCHAR*)formatDataResponse->requestedFormatData, wchar_len, &utf8_len);
    if (!text)
    {
        fprintf(stderr, "[CLIPRDR] failed to convert remote UTF-16 clipboard text to UTF-8\n");
        return CHANNEL_RC_OK;
    }

    printf("[CLIPRDR] remote text received bytes=%u chars=%zu\n", formatDataResponse->common.dataLen, utf8_len);
    if (session->clipboard_text_callback)
    {
        session->clipboard_text_callback(session, text);
    }
    free(text);

    return CHANNEL_RC_OK;
}

UINT on_cliprdr_server_file_contents_response(CliprdrClientContext* context,
                                              const CLIPRDR_FILE_CONTENTS_RESPONSE* fileContentsResponse)
{
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;
    if (!session)
        return CHANNEL_RC_OK;

    size_t index = 0;
    while (index < session->file_contents_outstanding_count &&
           session->file_contents_outstanding[index].stream_id != fileContentsResponse->streamId)
        index++;
    if (index == session->file_contents_outstanding_count)
    {
        printf("[CLIPRDR] dropping file contents for unknown stream=%u\n", fileContentsResponse->streamId);
        return CHANNEL_RC_OK;
    }

    const file_contents_request request = session->file_contents_outstanding[index];
    session->file_contents_outstanding[index] = session->file_contents_outstanding[--session->file_contents_outstanding_count];

    const bool ok = request.generation == session->remote_generation &&
                    (fileContentsResponse->common.msgFlags & CB_RESPONSE_OK) &&
                    fileContentsResponse->requestedData &&
                    (!request.size_only || fileContentsResponse->cbRequested >= sizeof(UINT64));
    if (!ok)
        printf("[CLIPRDR] remote file contents failed stream=%u flags=0x%04x\n", request.stream_id, fileContentsResponse->common.msgFlags);
    notify_file_contents(session, request.stream_id, ok, fileContentsResponse->requestedData, fileContentsResponse->cbRequested);
    return CHANNEL_RC_OK;
}

bool process_pending_clipboard(rdp_session* session)
{
    if (!session || !session->cliprdr) return false;

    const bool retried = session->remote_retry_pending;
    process_remote_retry(session);
    const bool sent_retry = retried && !session->remote_retry_pending;

    bool pending = false;
    EnterCriticalSection(&session->clipboard_lock);
    pending = session->clipboard_format_pending;
    session->clipboard_format_pending = false;
    LeaveCriticalSection(&session->clipboard_lock);

    if (pending)
    {
        send_clipboard_format_list(session);
    }

    const bool sent_file_requests = process_pending_file_contents_requests(session);
    return pending || sent_retry || sent_file_requests;
}

static bool ensure_supported_formats_capacity(rdp_session* session, size_t needed_capacity)
{
    if (!session) return false;
    
    if (needed_capacity <= session->supported_local_formats_capacity) return true;
    
    size_t new_capacity = session->supported_local_formats_capacity ? session->supported_local_formats_capacity * 2 : 4;
    while (new_capacity < needed_capacity) new_capacity *= 2;
    
    UINT32* new_formats = (UINT32*)realloc(session->supported_local_formats, new_capacity * sizeof(UINT32));
    if (!new_formats) return false;
    
    session->supported_local_formats = new_formats;
    session->supported_local_formats_capacity = new_capacity;
    return true;
}

static bool ensure_file_paths_capacity(rdp_session* session, size_t needed_capacity)
{
    if (!session) return false;
    
    if (needed_capacity <= session->local_file_paths_capacity) return true;
    
    size_t new_capacity = session->local_file_paths_capacity ? session->local_file_paths_capacity * 2 : 4;
    while (new_capacity < needed_capacity) new_capacity *= 2;
    
    char** new_paths = (char**)realloc(session->local_file_paths, new_capacity * sizeof(char*));
    if (!new_paths) return false;
    
    session->local_file_paths = new_paths;
    session->local_file_paths_capacity = new_capacity;
    return true;
}

static void mark_local_files_changed(rdp_session* session)
{
    if (!session) return;

    update_supported_local_formats(session);
    session->clipboard_format_pending = true;
    printf("[CLIPRDR] local files changed count=%zu\n", session->local_file_paths_count);
}

static void update_supported_local_formats(rdp_session* session)
{
    if (!session) return;
    
    // Clear existing formats
    session->supported_local_formats_count = 0;
    
    // Add text format if we have text
    if (session->local_clipboard_text && session->local_clipboard_text[0] != '\0')
    {
        if (ensure_supported_formats_capacity(session, 1))
        {
            session->supported_local_formats[session->supported_local_formats_count++] = CF_UNICODETEXT;
        }
    }
    
    // Add bitmap formats if we have bitmap data
    if (session->local_bitmap_data && session->local_bitmap_data_size > 0)
    {
        // Add CF_DIB (Device Independent Bitmap) - most compatible
        if (ensure_supported_formats_capacity(session, session->supported_local_formats_count + 1))
        {
            session->supported_local_formats[session->supported_local_formats_count++] = CF_DIB;
        }
        // TODO: Add CF_BITMAP and CF_DIBV5 for additional compatibility
    }
    
    // TODO: Add file formats when file clipboard is fully implemented
}

void rdp_session_clipboard_set_local_bitmap(rdp_session* session, const BYTE* bitmap_data, size_t bitmap_data_size, UINT32 width, UINT32 height) {
    if (!session) return;

    EnterCriticalSection(&session->clipboard_lock);
    
    // Clear existing bitmap data
    free_local_bitmap_data(session);
    
    // Store new bitmap data
    if (bitmap_data && bitmap_data_size > 0 && width > 0 && height > 0)
    {
        free_local_clipboard_text(session);
        free_local_file_paths(session);
        session->local_bitmap_data = (BYTE*)malloc(bitmap_data_size);
        if (session->local_bitmap_data)
        {
            memcpy(session->local_bitmap_data, bitmap_data, bitmap_data_size);
            session->local_bitmap_data_size = bitmap_data_size;
            session->local_bitmap_width = width;
            session->local_bitmap_height = height;
        }
    }
    
    // Update supported formats
    update_supported_local_formats(session);
    session->clipboard_format_pending = true;
    
    LeaveCriticalSection(&session->clipboard_lock);

    printf("[CLIPRDR] local bitmap changed size=%zu width=%u height=%u\n", 
           session->local_bitmap_data_size, session->local_bitmap_width, session->local_bitmap_height);
}

void rdp_session_clipboard_clear_local_files(rdp_session* session) {
    if (!session) return;

    EnterCriticalSection(&session->clipboard_lock);
    free_local_file_paths(session);
    LeaveCriticalSection(&session->clipboard_lock);
}

void rdp_session_clipboard_add_local_file(rdp_session* session, const char* file_path) {
    if (!session || !file_path || file_path[0] == '\0') return;

    EnterCriticalSection(&session->clipboard_lock);
    if (ensure_file_paths_capacity(session, session->local_file_paths_count + 1))
    {
        session->local_file_paths[session->local_file_paths_count] = duplicate_string(file_path);
        if (session->local_file_paths[session->local_file_paths_count])
        {
            session->local_file_paths_count++;
        }
    }
    LeaveCriticalSection(&session->clipboard_lock);
}

void rdp_session_clipboard_commit_local_files(rdp_session* session) {
    if (!session) return;

    EnterCriticalSection(&session->clipboard_lock);
    if (session->local_file_paths_count > 0)
    {
        free_local_clipboard_text(session);
        free_local_bitmap_data(session);
    }
    mark_local_files_changed(session);
    LeaveCriticalSection(&session->clipboard_lock);
}

void rdp_session_clipboard_set_local_files(rdp_session* session, const char** file_paths, size_t file_count) {
    if (!session) return;

    rdp_session_clipboard_clear_local_files(session);

    if (file_paths && file_count > 0)
    {
        for (size_t i = 0; i < file_count; i++)
        {
            rdp_session_clipboard_add_local_file(session, file_paths[i]);
        }
    }

    rdp_session_clipboard_commit_local_files(session);
}

void rdp_session_clipboard_set_local_text(rdp_session* session, const char* text) {
    if (!session) return;

    EnterCriticalSection(&session->clipboard_lock);
    free_local_clipboard_text(session);
    free_local_file_paths(session);
    free_local_bitmap_data(session);
    if (text && text[0] != '\0')
    {
        session->local_clipboard_text = duplicate_string(text);
    }
    update_supported_local_formats(session);
    session->clipboard_format_pending = true;
    size_t text_len = session->local_clipboard_text ? strlen(session->local_clipboard_text) : 0;
    LeaveCriticalSection(&session->clipboard_lock);

    printf("[CLIPRDR] local text changed chars=%zu\n", text_len);
}

bool rdp_session_clipboard_request_remote_file_contents(rdp_session* session, uint32_t generation, uint32_t stream_id,
                                                       uint32_t list_index, bool size_only, uint64_t offset, uint32_t size)
{
    if (!session || !session->running || (!size_only && size == 0))
        return false;

    bool queued = false;
    EnterCriticalSection(&session->clipboard_lock);
    if (session->file_contents_queue_count < FILE_CONTENTS_REQUEST_CAPACITY)
    {
        file_contents_request request = { generation, stream_id, list_index, size_only, offset, size };
        session->file_contents_queue[session->file_contents_queue_count++] = request;
        queued = true;
    }
    LeaveCriticalSection(&session->clipboard_lock);
    return queued;
}
