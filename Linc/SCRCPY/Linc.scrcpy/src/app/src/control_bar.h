#ifndef SC_CONTROL_BAR_H
#define SC_CONTROL_BAR_H

#include <SDL2/SDL.h>
#include <stdbool.h>

#define SC_CB_BTN_W   46   // per-button width  (physical px)
#define SC_CB_BTN_H   30   // button/strip height
#define SC_CB_BTN_N    3   // minimize, maximize, close

struct sc_control_bar {
    SDL_Window *window;
    bool visible;          // mouse currently over the top strip
    int hovered;           // index of hovered button, or -1
    int pressed;           // index of pressed button, or -1
};

void
sc_control_bar_init(struct sc_control_bar *cb, SDL_Window *window);

// Top-left x of the button cluster for a given drawable width (cluster hugs the right edge).
static inline int
sc_control_bar_cluster_x(int drawable_w) {
    return drawable_w - SC_CB_BTN_N * SC_CB_BTN_W;
}

// Draw the bar+buttons (call between video copy and present). No-op if !visible.
void
sc_control_bar_render(struct sc_control_bar *cb, SDL_Renderer *renderer);

// Handle an SDL mouse event. mx_px/my_px are the event coords already scaled to
// DRAWABLE (physical) pixels, and drawable_w is the renderer output width in pixels.
// Returns true if the overlay consumed it (caller must NOT forward to input manager).
// Sets *repaint = true when visible/hover state changed and a re-render is needed.
bool
sc_control_bar_handle_mouse(struct sc_control_bar *cb, const SDL_Event *event,
                             int mx_px, int my_px, int drawable_w, bool *repaint);

#endif
