#ifdef _WIN32

#include "sys/win/window.h"
#include "control_bar.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dwmapi.h>
#include <SDL2/SDL_syswm.h>

static WNDPROC g_old_proc = NULL;

static void
apply_rounded_region(HWND hwnd) {
    if (IsZoomed(hwnd)) {
        SetWindowRgn(hwnd, NULL, TRUE);   // fill work area, square corners
        return;
    }
    RECT rect;
    if (GetClientRect(hwnd, &rect)) {
        int cw = rect.right - rect.left;
        int ch = rect.bottom - rect.top;
        HRGN rgn = CreateRoundRectRgn(0, 0, cw + 1, ch + 1,
                                      SC_WIN_CORNER_RADIUS * 2,
                                      SC_WIN_CORNER_RADIUS * 2);
        if (rgn) {
            SetWindowRgn(hwnd, rgn, TRUE);
        }
    }
}

static LRESULT
hit_resize(HWND hwnd, LPARAM l) {
    if (IsZoomed(hwnd)) return HTNOWHERE;             // no edge-resize while maximized
    POINT pt = { (short) LOWORD(l), (short) HIWORD(l) };   // screen coords
    RECT r;
    GetWindowRect(hwnd, &r);
    int b = SC_WIN_RESIZE_BORDER;
    int left   = pt.x <  r.left   + b;
    int right  = pt.x >= r.right  - b;
    int top    = pt.y <  r.top    + b;
    int bottom = pt.y >= r.bottom - b;
    if (top && left)     return HTTOPLEFT;
    if (top && right)    return HTTOPRIGHT;
    if (bottom && left)  return HTBOTTOMLEFT;
    if (bottom && right) return HTBOTTOMRIGHT;
    if (left)   return HTLEFT;
    if (right)  return HTRIGHT;
    if (top)    return HTTOP;
    if (bottom) return HTBOTTOM;
    return HTNOWHERE;
}

static LRESULT CALLBACK
sc_wndproc(HWND hwnd, UINT msg, WPARAM w, LPARAM l) {
    switch (msg) {
        case WM_NCCALCSIZE:
            if (w == TRUE) {
                return 0;
            }
            break;
        case WM_NCHITTEST: {
            LRESULT rb = hit_resize(hwnd, l);
            if (rb != HTNOWHERE) return rb;

            LRESULT h = CallWindowProc(g_old_proc, hwnd, msg, w, l);
            if (h == HTCLIENT) {
                POINT pt = { (short) LOWORD(l), (short) HIWORD(l) };
                ScreenToClient(hwnd, &pt);
                if (pt.y < SC_WIN_CAPTION_H) {
                    RECT cr; GetClientRect(hwnd, &cr);
                    int cluster_x = sc_control_bar_cluster_x(cr.right - cr.left);
                    if (pt.x >= cluster_x && pt.y < SC_CB_BTN_H) {
                        return HTCLIENT;   // let SDL receive the button clicks
                    }
                    return HTCAPTION;      // rest of the strip drags the window
                }
            }
            return h;
        }
        case WM_GETMINMAXINFO: {
            HMONITOR mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            MONITORINFO mi;
            ZeroMemory(&mi, sizeof(mi));
            mi.cbSize = sizeof(mi);
            if (GetMonitorInfo(mon, &mi)) {
                MINMAXINFO *mmi = (MINMAXINFO *) l;
                RECT wa = mi.rcWork, mr = mi.rcMonitor;
                mmi->ptMaxPosition.x  = wa.left - mr.left;
                mmi->ptMaxPosition.y  = wa.top  - mr.top;
                mmi->ptMaxSize.x      = wa.right  - wa.left;
                mmi->ptMaxSize.y      = wa.bottom - wa.top;
                mmi->ptMaxTrackSize.x = wa.right  - wa.left;
                mmi->ptMaxTrackSize.y = wa.bottom - wa.top;
            }
            return 0;
        }
        case WM_SIZE:
            apply_rounded_region(hwnd);
            return CallWindowProc(g_old_proc, hwnd, msg, w, l);
        default:
            break;
    }
    return CallWindowProc(g_old_proc, hwnd, msg, w, l);
}

void
sc_win_make_frameless(SDL_Window *window) {
    SDL_SysWMinfo wm;
    SDL_VERSION(&wm.version);
    if (!SDL_GetWindowWMInfo(window, &wm)) {
        return;
    }

    HWND hwnd = wm.info.win.window;

    LONG_PTR style = GetWindowLongPtr(hwnd, GWL_STYLE);
    style |= WS_THICKFRAME | WS_MAXIMIZEBOX;   // keep resize borders + maximize/snap
    SetWindowLongPtr(hwnd, GWL_STYLE, style);
    SetWindowPos(hwnd, NULL, 0, 0, 0, 0,
                 SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    g_old_proc = (WNDPROC) SetWindowLongPtr(hwnd, GWLP_WNDPROC, (LONG_PTR) sc_wndproc);

    apply_rounded_region(hwnd);

    MARGINS margins = {0, 0, 0, 1};
    DwmExtendFrameIntoClientArea(hwnd, &margins);
}

#endif
