#pragma once
#include <stdint.h>
#include <stdbool.h>

typedef void (*FrameCallback)(uint8_t* data, int width, int height);

void set_frame_callback(FrameCallback cb);
bool connect_rdp(const char* host, const char* domain, const char* user, const char* password,
                 const char* gateway_host, const char* gateway_domain, const char* gateway_user, const char* gateway_password,
                 int width, int height);
void disconnect_rdp(void);
void send_mouse_event(uint16_t flags, uint16_t x, uint16_t y);
void send_keyboard_event(uint16_t flags, uint16_t code);
