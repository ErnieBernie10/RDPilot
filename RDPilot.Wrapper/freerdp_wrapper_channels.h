#pragma once
#include "freerdp_wrapper_internal.h"

/* PubSub channel routing. Channel-specific initialization is owned by its module. */
void wrapper_channels_subscribe(rdpContext* context);
