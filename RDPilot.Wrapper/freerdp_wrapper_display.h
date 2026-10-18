#pragma once
#include "freerdp_wrapper_internal.h"

/* Managed policy chooses targets; this module only sends layouts on the RDP thread. */
void wrapper_display_attach(rdp_session* session, DispClientContext* disp);
void wrapper_display_detach(rdp_session* session);
void wrapper_display_configure(rdp_session* session, rdpSettings* settings, UINT32 width, UINT32 height);
bool wrapper_display_process_pending(rdp_session* session);
