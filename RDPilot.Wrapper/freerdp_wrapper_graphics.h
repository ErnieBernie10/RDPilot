#pragma once
#include "freerdp_wrapper_internal.h"

/* RDP-thread GDI/RDPGFX lifecycle. Present remains in the frame module on the UI thread. */
rendering_mode wrapper_graphics_mode(void);
bool wrapper_graphics_uses_pipeline(rendering_mode mode);
void wrapper_graphics_configure(rdpSettings* settings, rendering_mode mode);
void wrapper_graphics_initialize(rdp_session* session);
void wrapper_graphics_subscribe_reset(rdpContext* context);
void wrapper_graphics_attach(rdpContext* context, RdpgfxClientContext* gfx);
void wrapper_graphics_detach(rdpContext* context);
BOOL wrapper_graphics_resize(rdpContext* context, UINT32 width, UINT32 height);
