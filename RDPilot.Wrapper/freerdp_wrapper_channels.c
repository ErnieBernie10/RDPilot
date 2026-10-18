#include "freerdp_wrapper_channels.h"
#include "freerdp_wrapper_graphics.h"
#include "freerdp_wrapper_display.h"

static void on_channel_connected(void* context, const ChannelConnectedEventArgs* e)
{
    rdpContext* rdp_context = (rdpContext*)context;
    rdp_session* session = session_from_context(rdp_context);
    if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
        wrapper_display_attach(session, (DispClientContext*)e->pInterface);
    else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
        wrapper_clipboard_attach(session, (CliprdrClientContext*)e->pInterface);
    else if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0)
        wrapper_graphics_attach(rdp_context, (RdpgfxClientContext*)e->pInterface);
}

static void on_channel_disconnected(void* context, const ChannelDisconnectedEventArgs* e)
{
    rdpContext* rdp_context = (rdpContext*)context;
    rdp_session* session = session_from_context(rdp_context);
    if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0) wrapper_display_detach(session);
    else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0) wrapper_clipboard_detach(session);
    else if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0) wrapper_graphics_detach(rdp_context);
}

void wrapper_channels_subscribe(rdpContext* context)
{
    PubSub_SubscribeChannelConnected(context->pubSub, on_channel_connected);
    PubSub_SubscribeChannelDisconnected(context->pubSub, on_channel_disconnected);
}
