#include "control_bar.h"

void
sc_control_bar_init(struct sc_control_bar *cb, SDL_Window *window) {
    cb->window = window;
    cb->visible = false;
    cb->hovered = -1;
    cb->pressed = -1;
}

void
sc_control_bar_render(struct sc_control_bar *cb, SDL_Renderer *renderer) {
    if (!cb->visible) {
        return;
    }

    // Draw in real drawable pixels: save + neutralize any logical size, restore below.
    int lw = 0, lh = 0;
    SDL_RenderGetLogicalSize(renderer, &lw, &lh);
    if (lw || lh) {
        SDL_RenderSetLogicalSize(renderer, 0, 0);
    }

    int w = 0, h = 0;
    SDL_GetRendererOutputSize(renderer, &w, &h);

    int cluster_x = sc_control_bar_cluster_x(w);

    SDL_SetRenderDrawBlendMode(renderer, SDL_BLENDMODE_BLEND);

    for (int i = 0; i < SC_CB_BTN_N; ++i) {
        SDL_Rect rect = {
            .x = cluster_x + i * SC_CB_BTN_W,
            .y = 0,
            .w = SC_CB_BTN_W,
            .h = SC_CB_BTN_H,
        };

        if (i == cb->hovered) {
            if (i == 2) {
                // Close button hovered: red
                SDL_SetRenderDrawColor(renderer, 232, 17, 35, 200);
            } else {
                // Min/Max button hovered: lighter grey
                SDL_SetRenderDrawColor(renderer, 60, 60, 60, 180);
            }
        } else {
            // Translucent panel
            SDL_SetRenderDrawColor(renderer, 32, 32, 32, 150);
        }

        SDL_RenderFillRect(renderer, &rect);

        // Draw glyph centered in button
        SDL_SetRenderDrawColor(renderer, 230, 230, 230, 255);
        int cx = rect.x + rect.w / 2;
        int cy = rect.y + rect.h / 2;

        if (i == 0) {
            // Minimize: horizontal line
            SDL_RenderDrawLine(renderer, cx - 5, cy, cx + 5, cy);
        } else if (i == 1) {
            // Maximize / Restore
            uint32_t flags = SDL_GetWindowFlags(cb->window);
            bool is_maximized = (flags & SDL_WINDOW_MAXIMIZED) != 0;
            if (is_maximized) {
                // Restore glyph (two offset squares)
                SDL_RenderDrawLine(renderer, cx - 3, cy - 6, cx + 5, cy - 6);
                SDL_RenderDrawLine(renderer, cx + 5, cy - 6, cx + 5, cy + 2);
                SDL_RenderDrawLine(renderer, cx + 1, cy + 2, cx + 5, cy + 2);
                SDL_RenderDrawLine(renderer, cx - 3, cy - 6, cx - 3, cy - 2);

                SDL_Rect front = { cx - 6, cy - 3, 8, 8 };
                SDL_RenderDrawRect(renderer, &front);
            } else {
                // Maximize glyph (single square outline ~10x10)
                SDL_Rect square = { cx - 5, cy - 5, 10, 10 };
                SDL_RenderDrawRect(renderer, &square);
            }
        } else if (i == 2) {
            // Close: X
            SDL_RenderDrawLine(renderer, cx - 5, cy - 5, cx + 5, cy + 5);
            SDL_RenderDrawLine(renderer, cx - 5, cy + 5, cx + 5, cy - 5);
        }
    }

    // Restore the previous logical size
    if (lw || lh) {
        SDL_RenderSetLogicalSize(renderer, lw, lh);
    }
}

bool
sc_control_bar_handle_mouse(struct sc_control_bar *cb, const SDL_Event *event,
                            int mx_px, int my_px, int drawable_w, bool *repaint) {
    if (event->type != SDL_MOUSEMOTION
            && event->type != SDL_MOUSEBUTTONDOWN
            && event->type != SDL_MOUSEBUTTONUP) {
        return false;
    }
    int mx = mx_px;
    int my = my_px;

    bool in_strip = (my >= 0 && my < SC_CB_BTN_H && mx >= 0 && mx < drawable_w);

    int hover_idx = -1;
    if (in_strip) {
        int cluster_x = sc_control_bar_cluster_x(drawable_w);
        if (mx >= cluster_x && mx < drawable_w) {
            hover_idx = (mx - cluster_x) / SC_CB_BTN_W;
            if (hover_idx < 0 || hover_idx >= SC_CB_BTN_N) {
                hover_idx = -1;
            }
        }
    }

    bool visible = in_strip;
    if (visible != cb->visible || hover_idx != cb->hovered) {
        cb->visible = visible;
        cb->hovered = hover_idx;
        if (repaint) {
            *repaint = true;
        }
    }

    if (event->type == SDL_MOUSEBUTTONDOWN && event->button.button == SDL_BUTTON_LEFT) {
        if (hover_idx >= 0) {
            cb->pressed = hover_idx;
        }
    } else if (event->type == SDL_MOUSEBUTTONUP && event->button.button == SDL_BUTTON_LEFT) {
        if (cb->pressed >= 0 && cb->pressed == hover_idx) {
            int btn = cb->pressed;
            cb->pressed = -1;
            if (btn == 0) {
                SDL_MinimizeWindow(cb->window);
            } else if (btn == 1) {
                uint32_t flags = SDL_GetWindowFlags(cb->window);
                if (flags & SDL_WINDOW_MAXIMIZED) {
                    SDL_RestoreWindow(cb->window);
                } else {
                    SDL_MaximizeWindow(cb->window);
                }
            } else if (btn == 2) {
                SDL_Event quit_event;
                quit_event.type = SDL_QUIT;
                quit_event.quit.timestamp = SDL_GetTicks();
                SDL_PushEvent(&quit_event);
            }
        } else {
            cb->pressed = -1;
        }
    }

    return in_strip;
}
