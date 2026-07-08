#include "freerdp_wrapper_internal.h"

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

#define REMOTE_FILE_CHUNK_SIZE (256u * 1024u)

static void clear_remote_file_transfer(rdp_session* session);
static UINT request_remote_file_descriptor(rdp_session* session);
static UINT request_next_remote_file_chunk(rdp_session* session, bool request_size_only);
static void finish_remote_file_transfer(rdp_session* session);

static bool ensure_received_file_paths_capacity(rdp_session* session, size_t needed_capacity)
{
    if (!session)
        return false;

    if (needed_capacity <= session->remote_received_file_paths_capacity)
        return true;

    size_t new_capacity = session->remote_received_file_paths_capacity ? session->remote_received_file_paths_capacity * 2 : 4;
    while (new_capacity < needed_capacity)
        new_capacity *= 2;

    char** new_paths = (char**)realloc(session->remote_received_file_paths, new_capacity * sizeof(char*));
    if (!new_paths)
        return false;

    session->remote_received_file_paths = new_paths;
    session->remote_received_file_paths_capacity = new_capacity;
    return true;
}

static void close_remote_active_file(rdp_session* session)
{
    if (!session)
        return;

    if (session->remote_active_file)
    {
        fclose(session->remote_active_file);
        session->remote_active_file = NULL;
    }

    if (session->remote_active_file_path)
    {
        free(session->remote_active_file_path);
        session->remote_active_file_path = NULL;
    }

    session->remote_active_file_size = 0;
    session->remote_active_file_offset = 0;
}

static void free_remote_received_file_paths(rdp_session* session)
{
    if (!session || !session->remote_received_file_paths)
        return;

    for (size_t i = 0; i < session->remote_received_file_paths_count; i++)
    {
        free(session->remote_received_file_paths[i]);
    }

    free(session->remote_received_file_paths);
    session->remote_received_file_paths = NULL;
    session->remote_received_file_paths_count = 0;
    session->remote_received_file_paths_capacity = 0;
}

static void clear_remote_file_transfer(rdp_session* session)
{
    if (!session)
        return;

    close_remote_active_file(session);
    free_remote_received_file_paths(session);
    session->remote_expected_file_count = 0;
    session->remote_active_file_index = 0;
    session->remote_file_stream_id = 0;
    session->remote_file_transfer_in_progress = false;
    session->pending_remote_format_id = 0;
}

static char get_path_separator(void)
{
#if defined(_WIN32)
    return '\\';
#else
    return '/';
#endif
}

static bool path_exists(const char* path)
{
    if (!path || path[0] == '\0')
        return false;

#if defined(_WIN32)
    DWORD attrs = GetFileAttributesA(path);
    return attrs != INVALID_FILE_ATTRIBUTES;
#else
    struct stat st = {0};
    return stat(path, &st) == 0;
#endif
}

static bool ensure_directory_exists(const char* path)
{
    if (!path || path[0] == '\0')
        return false;

    if (path_exists(path))
        return true;

#if defined(_WIN32)
    return CreateDirectoryA(path, NULL) || GetLastError() == ERROR_ALREADY_EXISTS;
#else
    return mkdir(path, 0700) == 0 || errno == EEXIST;
#endif
}

static char* duplicate_printf(const char* format, ...)
{
    va_list args;
    va_start(args, format);
    int needed = _vscprintf(format, args);
    va_end(args);
    if (needed < 0)
        return NULL;

    char* buffer = (char*)malloc((size_t)needed + 1);
    if (!buffer)
        return NULL;

    va_start(args, format);
    vsnprintf(buffer, (size_t)needed + 1, format, args);
    va_end(args);
    return buffer;
}

static char* sanitize_remote_filename(const WCHAR* wide_name)
{
    if (!wide_name || wide_name[0] == 0)
        return duplicate_string("clipboard-file");

    size_t utf8_len = 0;
    char* utf8 = ConvertWCharToUtf8Alloc(wide_name, &utf8_len);
    if (!utf8 || utf8_len == 0)
    {
        free(utf8);
        return duplicate_string("clipboard-file");
    }

    for (size_t i = 0; utf8[i] != '\0'; i++)
    {
        unsigned char ch = (unsigned char)utf8[i];
        if (ch < 32 || utf8[i] == '/' || utf8[i] == '\\' || utf8[i] == ':' || utf8[i] == '*' || utf8[i] == '?' || utf8[i] == '"' || utf8[i] == '<' || utf8[i] == '>' || utf8[i] == '|')
            utf8[i] = '_';
    }

    while (utf8[0] == '.' || utf8[0] == ' ')
        memmove(utf8, utf8 + 1, strlen(utf8));

    if (utf8[0] == '\0')
    {
        free(utf8);
        return duplicate_string("clipboard-file");
    }

    return utf8;
}

static char* get_remote_clipboard_temp_directory(rdp_session* session)
{
    if (!session)
        return NULL;

    if (session->temp_directory)
        return session->temp_directory;

#if defined(_WIN32)
    char temp_path[MAX_PATH] = {0};
    DWORD len = GetTempPathA(MAX_PATH, temp_path);
    if (len == 0 || len >= MAX_PATH)
        return NULL;

    char* root = duplicate_printf("%sRDPilot", temp_path);
#else
    const char* base = getenv("XDG_RUNTIME_DIR");
    if (!base || base[0] == '\0')
        base = getenv("TMPDIR");
    if (!base || base[0] == '\0')
        base = "/tmp";

    char* root = duplicate_printf("%s%cRDPilot", base, get_path_separator());
#endif
    if (!root)
        return NULL;

    if (!ensure_directory_exists(root))
    {
        free(root);
        return NULL;
    }

    char* clipboard_dir = duplicate_printf("%s%cClipboard", root, get_path_separator());
    free(root);
    if (!clipboard_dir)
        return NULL;

    if (!ensure_directory_exists(clipboard_dir))
    {
        free(clipboard_dir);
        return NULL;
    }

    session->temp_directory = clipboard_dir;
    return session->temp_directory;
}

static char* build_unique_remote_file_path(rdp_session* session, const WCHAR* wide_name)
{
    char* directory = get_remote_clipboard_temp_directory(session);
    char* sanitized = sanitize_remote_filename(wide_name);
    if (!directory || !sanitized)
    {
        free(sanitized);
        return NULL;
    }

    char separator = get_path_separator();
    char* candidate = duplicate_printf("%s%c%s", directory, separator, sanitized);
    if (!candidate)
    {
        free(sanitized);
        return NULL;
    }

    if (!path_exists(candidate))
    {
        free(sanitized);
        return candidate;
    }

    for (UINT32 suffix = 1; suffix < 10000; suffix++)
    {
        free(candidate);
        candidate = duplicate_printf("%s%c%u-%s", directory, separator, suffix, sanitized);
        if (!candidate)
            break;
        if (!path_exists(candidate))
            break;
    }

    free(sanitized);
    return candidate;
}

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

    struct stat st = {0};
    if (stat(path, &st) != 0)
        return false;
    if (S_ISDIR(st.st_mode))
        return false;
    *size = (UINT64)st.st_size;
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

    FILE* fp = fopen(path, "rb");
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
            WCHAR wide_path[MAX_PATH] = {0};
            MultiByteToWideChar(CP_UTF8, 0, path, -1, wide_path, MAX_PATH);

            HANDLE hFile = CreateFileW(wide_path, GENERIC_READ, FILE_SHARE_READ, NULL,
                                        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_BACKUP_SEMANTICS, NULL);
            if (hFile == INVALID_HANDLE_VALUE)
            {
                free(descriptor);
                LeaveCriticalSection(&session->clipboard_lock);
                return false;
            }

            f->dwFlags = FD_ATTRIBUTES | FD_FILESIZE | FD_WRITESTIME | FD_PROGRESSUI;
            f->dwFileAttributes = GetFileAttributesW(wide_path);
            if (!GetFileTime(hFile, NULL, NULL, &f->ftLastWriteTime))
            {
                f->dwFlags &= ~FD_WRITESTIME;
            }
            f->nFileSizeLow = GetFileSize(hFile, &f->nFileSizeHigh);
            CloseHandle(hFile);

            /* Copy filename manually - wcscpy_s fills remaining buffer with 0xFE in MSVC debug mode */
            WCHAR wide_name[MAX_PATH] = {0};
            MultiByteToWideChar(CP_UTF8, 0, base, -1, wide_name, MAX_PATH);
            size_t nameLen = wcslen(wide_name);
            if (nameLen >= ARRAYSIZE(f->cFileName))
                nameLen = ARRAYSIZE(f->cFileName) - 1;
            memcpy(f->cFileName, wide_name, nameLen * sizeof(WCHAR));
            f->cFileName[nameLen] = 0;
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

static UINT request_remote_file_descriptor(rdp_session* session)
{
    if (!session || !session->cliprdr || session->remote_file_group_descriptor_format_id == 0)
        return CHANNEL_RC_OK;

    clear_remote_file_transfer(session);
    session->pending_remote_format_id = session->remote_file_group_descriptor_format_id;

    CLIPRDR_FORMAT_DATA_REQUEST request;
    memset(&request, 0, sizeof(request));
    request.common.msgType = CB_FORMAT_DATA_REQUEST;
    request.requestedFormatId = session->remote_file_group_descriptor_format_id;
    printf("[CLIPRDR] request remote file descriptor format=%u\n", request.requestedFormatId);
    UINT rc = session->cliprdr->ClientFormatDataRequest(session->cliprdr, &request);
    log_channel_rc("ClientFormatDataRequest file descriptor", rc);
    return rc;
}

static UINT request_next_remote_file_chunk(rdp_session* session, bool request_size_only)
{
    if (!session || !session->cliprdr || !session->remote_file_transfer_in_progress)
        return CHANNEL_RC_OK;

    if (session->remote_active_file_index >= session->remote_received_file_paths_count)
    {
        finish_remote_file_transfer(session);
        return CHANNEL_RC_OK;
    }

    CLIPRDR_FILE_CONTENTS_REQUEST request;
    memset(&request, 0, sizeof(request));
    request.common.msgType = CB_FILECONTENTS_REQUEST;
    request.streamId = ++session->remote_file_stream_id;
    request.listIndex = (UINT32)session->remote_active_file_index;
    request.dwFlags = request_size_only ? FILECONTENTS_SIZE : FILECONTENTS_RANGE;
    request.nPositionLow = (UINT32)(session->remote_active_file_offset & 0xFFFFFFFFu);
    request.nPositionHigh = (UINT32)(session->remote_active_file_offset >> 32);
    request.cbRequested = request_size_only
        ? sizeof(UINT64)
        : (UINT32)(((session->remote_active_file_size - session->remote_active_file_offset) < REMOTE_FILE_CHUNK_SIZE)
            ? (session->remote_active_file_size - session->remote_active_file_offset)
            : REMOTE_FILE_CHUNK_SIZE);

    printf("[CLIPRDR] request remote file chunk index=%u stream=%u flags=0x%08x offset=%" PRIu64 " size=%u\n",
           request.listIndex,
           request.streamId,
           request.dwFlags,
           session->remote_active_file_offset,
           request.cbRequested);
    UINT rc = session->cliprdr->ClientFileContentsRequest(session->cliprdr, &request);
    log_channel_rc(request_size_only ? "ClientFileContentsRequest size" : "ClientFileContentsRequest data", rc);
    return rc;
}

static bool begin_remote_file_download(rdp_session* session)
{
    if (!session || session->remote_received_file_paths_count == 0)
        return false;

    close_remote_active_file(session);

    session->remote_active_file_index = 0;
    session->remote_active_file_size = 0;
    session->remote_active_file_offset = 0;
    session->remote_file_transfer_in_progress = true;
    return true;
}

static void finish_remote_file_transfer(rdp_session* session)
{
    if (!session)
        return;

    close_remote_active_file(session);
    session->remote_file_transfer_in_progress = false;
    session->pending_remote_format_id = 0;

    if (session->remote_received_file_paths_count > 0 && session->clipboard_files_callback)
    {
        session->clipboard_files_callback(session, (const char**)session->remote_received_file_paths, session->remote_received_file_paths_count);
    }
}

static void free_temp_directory(rdp_session* session)
{
    if (session && session->temp_directory)
    {
        free(session->temp_directory);
        session->temp_directory = NULL;
    }
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
    clear_remote_file_transfer(session);
    free_temp_directory(session);
    free_local_bitmap_data(session);
}

static bool has_local_clipboard_text(rdp_session* session)
{
    bool has_text = false;
    if (!session) return false;

    EnterCriticalSection(&session->clipboard_lock);
    has_text = session->local_clipboard_text && session->local_clipboard_text[0] != '\0';
    LeaveCriticalSection(&session->clipboard_lock);
    return has_text;
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
    
    LeaveCriticalSection(&session->clipboard_lock);
    
    if (total_formats == 0)
    {
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
        fprintf(stderr, "[CLIPRDR] failed to allocate format list\n");
        return ERROR_NOT_ENOUGH_MEMORY;
    }

    UINT32 format_index = 0;
    
    // Add standard formats
    EnterCriticalSection(&session->clipboard_lock);
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
    const char* path = (index < session->local_file_paths_count) ? session->local_file_paths[index] : NULL;
    if (path)
        ok = get_file_size_and_attributes(path, &fileSize, &attributes);
    LeaveCriticalSection(&session->clipboard_lock);

    if (!ok || !path)
    {
        printf("[CLIPRDR] remote requested file contents for invalid index=%u\n", request->listIndex);
        return send_clipboard_failed_data_response(session, "ClientFileContentsResponse invalid index");
    }

    if (request->dwFlags & FILECONTENTS_SIZE)
    {
        UINT64* sizeResponse = (UINT64*)malloc(sizeof(UINT64));
        if (!sizeResponse)
            return send_clipboard_failed_data_response(session, "ClientFileContentsResponse size oom");

        *sizeResponse = fileSize;
        CLIPRDR_FILE_CONTENTS_RESPONSE response;
        memset(&response, 0, sizeof(response));
        response.common.msgType = CB_FILECONTENTS_RESPONSE;
        response.common.msgFlags = CB_RESPONSE_OK;
        response.streamId = request->streamId;
        response.cbRequested = sizeof(UINT64);
        response.requestedData = (const BYTE*)sizeResponse;
        UINT rc = session->cliprdr->ClientFileContentsResponse(session->cliprdr, &response);
        log_channel_rc("ClientFileContentsResponse size", rc);
        free(sizeResponse);
        return rc;
    }

    UINT64 offset = ((UINT64)request->nPositionHigh << 32) | request->nPositionLow;
    UINT32 wanted = request->cbRequested;
    if (offset > fileSize)
        return send_clipboard_failed_data_response(session, "ClientFileContentsResponse offset");

    UINT64 remaining = fileSize - offset;
    if (remaining < wanted)
        wanted = (UINT32)remaining;
    if (wanted == 0)
        return send_clipboard_failed_data_response(session, "ClientFileContentsResponse eof");

    if (!read_file_range(path, offset, wanted, &buffer))
    {
        printf("[CLIPRDR] failed to read file contents index=%u offset=%" PRIu64 " wanted=%u\n", request->listIndex, offset, wanted);
        return send_clipboard_failed_data_response(session, "ClientFileContentsResponse read");
    }

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

    // Request the highest priority format available
    if (has_unicode_text && session && session->cliprdr)
    {
        session->pending_remote_format_id = CF_UNICODETEXT;
        CLIPRDR_FORMAT_DATA_REQUEST request;
        memset(&request, 0, sizeof(request));
        request.common.msgType = CB_FORMAT_DATA_REQUEST;
        request.requestedFormatId = CF_UNICODETEXT;
        printf("[CLIPRDR] request remote Unicode text\n");
        UINT rc = session->cliprdr->ClientFormatDataRequest(session->cliprdr, &request);
        log_channel_rc("ClientFormatDataRequest", rc);
        return rc;
    }

    if (has_files && session && session->cliprdr)
    {
        return request_remote_file_descriptor(session);
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
    if (!(formatDataResponse->common.msgFlags & CB_RESPONSE_OK) || !formatDataResponse->requestedFormatData)
    {
        printf("[CLIPRDR] remote text response failed flags=0x%04x\n", formatDataResponse->common.msgFlags);
        return CHANNEL_RC_OK;
    }

    if (session && session->pending_remote_format_id == session->remote_file_group_descriptor_format_id)
    {
        if (formatDataResponse->common.dataLen < sizeof(UINT32))
        {
            printf("[CLIPRDR] remote file descriptor response too small bytes=%u\n", formatDataResponse->common.dataLen);
            clear_remote_file_transfer(session);
            return CHANNEL_RC_OK;
        }

        const BYTE* data = formatDataResponse->requestedFormatData;
        UINT32 item_count = *(const UINT32*)data;
        size_t expected_len = sizeof(UINT32) + (size_t)item_count * sizeof(FILEDESCRIPTORW);
        if (item_count == 0 || formatDataResponse->common.dataLen < expected_len)
        {
            printf("[CLIPRDR] remote file descriptor payload invalid items=%u bytes=%u\n", item_count, formatDataResponse->common.dataLen);
            clear_remote_file_transfer(session);
            return CHANNEL_RC_OK;
        }

        clear_remote_file_transfer(session);
        session->remote_expected_file_count = item_count;
        if (!ensure_received_file_paths_capacity(session, item_count))
        {
            clear_remote_file_transfer(session);
            return CHANNEL_RC_OK;
        }

        const FILEDESCRIPTORW* descriptors = (const FILEDESCRIPTORW*)(data + sizeof(UINT32));
        for (UINT32 i = 0; i < item_count; i++)
        {
            char* path = build_unique_remote_file_path(session, descriptors[i].cFileName);
            if (!path)
            {
                clear_remote_file_transfer(session);
                return CHANNEL_RC_OK;
            }

            session->remote_received_file_paths[session->remote_received_file_paths_count++] = path;
        }

        if (!begin_remote_file_download(session))
        {
            clear_remote_file_transfer(session);
            return CHANNEL_RC_OK;
        }

        printf("[CLIPRDR] remote file descriptor received items=%u\n", item_count);
        return request_next_remote_file_chunk(session, true);
    }

    if (session)
        session->pending_remote_format_id = 0;

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
    if (session && session->clipboard_text_callback)
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
    if (!session || !session->remote_file_transfer_in_progress)
        return CHANNEL_RC_OK;

    if (!(fileContentsResponse->common.msgFlags & CB_RESPONSE_OK) || !fileContentsResponse->requestedData)
    {
        printf("[CLIPRDR] remote file contents response failed flags=0x%04x\n", fileContentsResponse->common.msgFlags);
        clear_remote_file_transfer(session);
        return CHANNEL_RC_OK;
    }

    if (session->remote_active_file_index >= session->remote_received_file_paths_count)
    {
        clear_remote_file_transfer(session);
        return CHANNEL_RC_OK;
    }

    if (!session->remote_active_file)
    {
        if (fileContentsResponse->cbRequested < sizeof(UINT64))
        {
            clear_remote_file_transfer(session);
            return CHANNEL_RC_OK;
        }

        session->remote_active_file_size = *(const UINT64*)fileContentsResponse->requestedData;
        session->remote_active_file_offset = 0;
        session->remote_active_file_path = duplicate_string(session->remote_received_file_paths[session->remote_active_file_index]);
        if (!session->remote_active_file_path)
        {
            clear_remote_file_transfer(session);
            return CHANNEL_RC_OK;
        }

        session->remote_active_file = fopen(session->remote_active_file_path, "wb");
        if (!session->remote_active_file)
        {
            printf("[CLIPRDR] failed to open remote temp file path=%s\n", session->remote_active_file_path);
            clear_remote_file_transfer(session);
            return CHANNEL_RC_OK;
        }

        if (session->remote_active_file_size == 0)
        {
            close_remote_active_file(session);
            session->remote_active_file_index++;
            return request_next_remote_file_chunk(session, true);
        }

        return request_next_remote_file_chunk(session, false);
    }

    size_t written = fwrite(fileContentsResponse->requestedData, 1, fileContentsResponse->cbRequested, session->remote_active_file);
    if (written != fileContentsResponse->cbRequested)
    {
        printf("[CLIPRDR] failed to write remote temp file path=%s\n", session->remote_active_file_path ? session->remote_active_file_path : "<null>");
        clear_remote_file_transfer(session);
        return CHANNEL_RC_OK;
    }

    session->remote_active_file_offset += fileContentsResponse->cbRequested;
    if (session->remote_active_file_offset < session->remote_active_file_size)
        return request_next_remote_file_chunk(session, false);

    close_remote_active_file(session);
    session->remote_active_file_index++;
    if (session->remote_active_file_index >= session->remote_received_file_paths_count)
    {
        finish_remote_file_transfer(session);
        return CHANNEL_RC_OK;
    }

    return request_next_remote_file_chunk(session, true);
}

void process_pending_clipboard(rdp_session* session)
{
    if (!session || !session->cliprdr) return;

    bool pending = false;
    EnterCriticalSection(&session->clipboard_lock);
    pending = session->clipboard_format_pending;
    session->clipboard_format_pending = false;
    LeaveCriticalSection(&session->clipboard_lock);

    if (pending)
    {
        send_clipboard_format_list(session);
    }
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

void rdp_session_clipboard_set_local_files(rdp_session* session, const char** file_paths, size_t file_count) {
    if (!session) return;

    EnterCriticalSection(&session->clipboard_lock);
    
    // Clear existing file paths
    free_local_file_paths(session);
    
    // Add new file paths
    if (file_paths && file_count > 0)
    {
        if (ensure_file_paths_capacity(session, file_count))
        {
            for (size_t i = 0; i < file_count; i++)
            {
                if (file_paths[i] && file_paths[i][0] != '\0')
                {
                    session->local_file_paths[session->local_file_paths_count] = duplicate_string(file_paths[i]);
                    if (session->local_file_paths[session->local_file_paths_count])
                    {
                        session->local_file_paths_count++;
                    }
                }
            }
        }
    }
    
    // Update supported formats (file formats will be handled separately in format list)
    update_supported_local_formats(session);
    session->clipboard_format_pending = true;
    
    LeaveCriticalSection(&session->clipboard_lock);

    printf("[CLIPRDR] local files changed count=%zu\n", session->local_file_paths_count);
}

void rdp_session_clipboard_set_local_text(rdp_session* session, const char* text) {
    if (!session) return;

    EnterCriticalSection(&session->clipboard_lock);
    free_local_clipboard_text(session);
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
