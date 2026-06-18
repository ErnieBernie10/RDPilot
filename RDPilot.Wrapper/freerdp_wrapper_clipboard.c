#include "freerdp_wrapper_internal.h"

void free_local_clipboard_text(rdp_session* session)
{
    if (session && session->local_clipboard_text)
    {
        free(session->local_clipboard_text);
        session->local_clipboard_text = NULL;
    }
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

    CLIPRDR_FORMAT format;
    memset(&format, 0, sizeof(format));
    format.formatId = CF_UNICODETEXT;

    CLIPRDR_FORMAT_LIST format_list;
    memset(&format_list, 0, sizeof(format_list));
    format_list.common.msgType = CB_FORMAT_LIST;
    format_list.common.msgFlags = 0;
    format_list.numFormats = has_local_clipboard_text(session) ? 1 : 0;
    format_list.formats = format_list.numFormats > 0 ? &format : NULL;

    printf("[CLIPRDR] send local format list unicodeText=%s\n", format_list.numFormats > 0 ? "true" : "false");
    UINT rc = session->cliprdr->ClientFormatList(session->cliprdr, &format_list);
    log_channel_rc("ClientFormatList", rc);
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
    general_capability.generalFlags = CB_USE_LONG_FORMAT_NAMES;

    CLIPRDR_CAPABILITIES capabilities;
    memset(&capabilities, 0, sizeof(capabilities));
    capabilities.common.msgType = CB_CLIP_CAPS;
    capabilities.cCapabilitiesSets = 1;
    capabilities.capabilitySets = (CLIPRDR_CAPABILITY_SET*)&general_capability;

    printf("[CLIPRDR] send client capabilities\n");
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

    if (requested_format_id != CF_UNICODETEXT)
    {
        printf("[CLIPRDR] remote requested unsupported local format=%u\n", requested_format_id);
        return send_clipboard_failed_data_response(session, "ClientFormatDataResponse unsupported");
    }

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
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;

    for (UINT32 i = 0; i < formatList->numFormats; i++)
    {
        if (formatList->formats[i].formatId == CF_UNICODETEXT)
        {
            has_unicode_text = true;
            break;
        }
    }

    printf("[CLIPRDR] server formats count=%u unicodeText=%s\n",
           formatList->numFormats, has_unicode_text ? "true" : "false");

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

    if (has_unicode_text && session && session->cliprdr)
    {
        CLIPRDR_FORMAT_DATA_REQUEST request;
        memset(&request, 0, sizeof(request));
        request.common.msgType = CB_FORMAT_DATA_REQUEST;
        request.requestedFormatId = CF_UNICODETEXT;
        printf("[CLIPRDR] request remote Unicode text\n");
        UINT rc = session->cliprdr->ClientFormatDataRequest(session->cliprdr, &request);
        log_channel_rc("ClientFormatDataRequest", rc);
        return rc;
    }

    return CHANNEL_RC_OK;
}

UINT on_cliprdr_server_format_data_request(CliprdrClientContext* context,
                                                  const CLIPRDR_FORMAT_DATA_REQUEST* formatDataRequest)
{
    rdp_session* session = context ? (rdp_session*)context->custom : NULL;
    printf("[CLIPRDR] remote requested local format=%u\n", formatDataRequest->requestedFormatId);
    return send_clipboard_data_response(session, formatDataRequest->requestedFormatId);
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

void rdp_session_clipboard_set_local_text(rdp_session* session, const char* text) {
    if (!session) return;

    EnterCriticalSection(&session->clipboard_lock);
    free_local_clipboard_text(session);
    if (text && text[0] != '\0')
    {
        session->local_clipboard_text = duplicate_string(text);
    }
    session->clipboard_format_pending = true;
    size_t text_len = session->local_clipboard_text ? strlen(session->local_clipboard_text) : 0;
    LeaveCriticalSection(&session->clipboard_lock);

    printf("[CLIPRDR] local text changed chars=%zu\n", text_len);
}
