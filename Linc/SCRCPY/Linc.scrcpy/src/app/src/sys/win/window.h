#ifndef SC_WIN_WINDOW_H
#define SC_WIN_WINDOW_H

#include <SDL2/SDL.h>

#define SC_WIN_CORNER_RADIUS 32   // window-pixel corner radius (owner-tuned)
#define SC_WIN_RESIZE_BORDER 6    // invisible resize-edge thickness in window pixels
#define SC_WIN_CAPTION_H     28   // top strip height that acts as the drag zone

void sc_win_make_frameless(SDL_Window *window);

#endif
