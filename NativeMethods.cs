using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FluentClip;

internal static unsafe class NativeMethods
{
    public const int WM_DESTROY = 0x0002;
    public const int WM_ACTIVATE = 0x0006;
    public const int WM_CLOSE = 0x0010;
    public const int WM_COMMAND = 0x0111;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 42;

    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_CONTEXTMENU = 0x007B;

    public const int WA_INACTIVE = 0;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const nint WS_EX_DLGMODALFRAME = 0x00000001;
    public const nint WS_EX_TOPMOST = 0x00000008;
    public const nint WS_EX_TOOLWINDOW = 0x00000080;
    public const nint WS_EX_APPWINDOW = 0x00040000;
    public const nint WS_EX_NOACTIVATE = 0x08000000;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_MESSAGE = -3;
    public static readonly nint HWND_BROADCAST = 0xFFFF;

    // Hotkey Modifiers
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // Clipboard Formats
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_HDROP = 15;
    public const uint CF_DIBV5 = 17;

    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;

    // DWM Attributes for Win11 Rounded Corners & Shadow
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    // Monitor
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // Shell_NotifyIcon
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_INFO = 0x00000010;

    // Menu Flags
    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_CHECKED = 0x00000008;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_BOTTOMALIGN = 0x0020;

    // SendInput
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12; // Alt
    public const ushort VK_V = 0x56;

    // Process Query
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint ERROR_ALREADY_EXISTS = 183;

    public delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DROPFILES
    {
        public uint pFiles;
        public POINT pt;
        public int fNC;
        public int fWide;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPV5HEADER
    {
        public uint bV5Size;
        public int bV5Width;
        public int bV5Height;
        public ushort bV5Planes;
        public ushort bV5BitCount;
        public uint bV5Compression;
        public uint bV5SizeImage;
        public int bV5XPelsPerMeter;
        public int bV5YPelsPerMeter;
        public uint bV5ClrUsed;
        public uint bV5ClrImportant;
        public uint bV5RedMask;
        public uint bV5GreenMask;
        public uint bV5BlueMask;
        public uint bV5AlphaMask;
        public uint bV5CSType;
        public fixed byte bV5Endpoints[36];
        public uint bV5GammaRed;
        public uint bV5GammaGreen;
        public uint bV5GammaBlue;
        public uint bV5Intent;
        public uint bV5ProfileData;
        public uint bV5ProfileSize;
        public uint bV5Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public fixed byte padding[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    // Kernel32
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalFree(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nuint GlobalSize(nint hMem);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateMutexW(nint lpMutexAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll")]
    public static extern uint GetLastError();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint hObject);

    // User32
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProcW(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll")]
    public static extern nint GetClipboardOwner();

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDC);

    // Gdi32
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern nint CreateDIBSection(nint hdc, BITMAPV5HEADER* pbmi, uint usage, out void* ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern nint CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, nint lpBits);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint ho);

    // Shell32
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileW(nint hDrop, uint iFile, char* lpszFile, uint cch);

    // Dwmapi
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public static void ApplyRoundedCorners(nint hwnd)
    {
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    public static string GetProcessNameFromWindow(nint hwnd)
    {
        if (hwnd == 0) return string.Empty;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return string.Empty;

        nint hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == 0) return string.Empty;

        try
        {
            char* buffer = stackalloc char[512];
            uint size = 512;
            if (QueryFullProcessImageNameW(hProc, 0, buffer, ref size) && size > 0)
            {
                string fullPath = new string(buffer, 0, (int)size);
                return Path.GetFileNameWithoutExtension(fullPath);
            }
        }
        finally
        {
            CloseHandle(hProc);
        }
        return string.Empty;
    }

    public static POINT GetSmartAnchorPoint(bool preferCaret)
    {
        if (preferCaret)
        {
            nint fg = GetForegroundWindow();
            if (fg != 0)
            {
                uint tid = GetWindowThreadProcessId(fg, out _);
                GUITHREADINFO gti = default;
                gti.cbSize = (uint)sizeof(GUITHREADINFO);
                if (GetGUIThreadInfo(tid, ref gti) && gti.hwndCaret != 0)
                {
                    if (gti.rcCaret.Right != 0 || gti.rcCaret.Bottom != 0)
                    {
                        POINT caretPt = new() { X = gti.rcCaret.Left, Y = gti.rcCaret.Bottom + 6 };
                        if (ClientToScreen(gti.hwndCaret, ref caretPt))
                        {
                            return caretPt;
                        }
                    }
                }
            }
        }

        GetCursorPos(out POINT mousePt);
        return new POINT { X = mousePt.X + 8, Y = mousePt.Y + 12 };
    }

    public static (int X, int Y, int WidthPx, int HeightPx) CalculateClampedPopupBounds(
        nint popupHwnd,
        int logicalWidth,
        int logicalHeight,
        bool preferCaret)
    {
        POINT anchor = GetSmartAnchorPoint(preferCaret);
        nint hMon = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
        MONITORINFO mi = default;
        mi.cbSize = (uint)sizeof(MONITORINFO);
        GetMonitorInfoW(hMon, ref mi);

        uint dpi = popupHwnd != 0 ? GetDpiForWindow(popupHwnd) : 96;
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;

        int w = (int)Math.Round(logicalWidth * scale);
        int h = (int)Math.Round(logicalHeight * scale);

        RECT work = mi.rcWork;
        int margin = (int)Math.Round(10 * scale);

        int x = anchor.X;
        int y = anchor.Y;

        if (x + w > work.Right - margin)
            x = work.Right - w - margin;
        if (x < work.Left + margin)
            x = work.Left + margin;

        if (y + h > work.Bottom - margin)
            y = Math.Max(work.Top + margin, anchor.Y - h - (int)Math.Round(24 * scale));
        if (y + h > work.Bottom - margin)
            y = work.Bottom - h - margin;
        if (y < work.Top + margin)
            y = work.Top + margin;

        return (x, y, w, h);
    }

    public static void SendCtrlV()
    {
        INPUT* inputs = stackalloc INPUT[6];
        int count = 0;

        // Release Alt or Shift if user still holds them from hotkey
        if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
        {
            inputs[count].type = INPUT_KEYBOARD;
            inputs[count].u.ki.wVk = VK_MENU;
            inputs[count].u.ki.dwFlags = KEYEVENTF_KEYUP;
            count++;
        }
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0)
        {
            inputs[count].type = INPUT_KEYBOARD;
            inputs[count].u.ki.wVk = VK_SHIFT;
            inputs[count].u.ki.dwFlags = KEYEVENTF_KEYUP;
            count++;
        }

        // Ctrl Down
        inputs[count].type = INPUT_KEYBOARD;
        inputs[count].u.ki.wVk = VK_CONTROL;
        count++;

        // V Down
        inputs[count].type = INPUT_KEYBOARD;
        inputs[count].u.ki.wVk = VK_V;
        count++;

        // V Up
        inputs[count].type = INPUT_KEYBOARD;
        inputs[count].u.ki.wVk = VK_V;
        inputs[count].u.ki.dwFlags = KEYEVENTF_KEYUP;
        count++;

        // Ctrl Up
        inputs[count].type = INPUT_KEYBOARD;
        inputs[count].u.ki.wVk = VK_CONTROL;
        inputs[count].u.ki.dwFlags = KEYEVENTF_KEYUP;
        count++;

        SendInput((uint)count, inputs, sizeof(INPUT));
    }

    /// <summary>
    /// Renders a crisp 32x32 ARGB rounded-square Fluent clipboard icon directly in memory (zero external .ico files needed).
    /// </summary>
    public static nint CreateFluentTrayIcon()
    {
        const int size = 32;
        BITMAPV5HEADER bmi = default;
        bmi.bV5Size = (uint)sizeof(BITMAPV5HEADER);
        bmi.bV5Width = size;
        bmi.bV5Height = -size; // top-down
        bmi.bV5Planes = 1;
        bmi.bV5BitCount = 32;
        bmi.bV5Compression = 0; // BI_RGB

        nint hdc = GetDC(0);
        nint hBitmap = CreateDIBSection(hdc, &bmi, 0, out void* bits, 0, 0);
        ReleaseDC(0, hdc);
        if (hBitmap == 0 || bits == null) return 0;

        uint* px = (uint*)bits;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Rounded squircle background (2..29)
                bool insideBg = IsInsideRoundedRect(x, y, 2, 3, 29, 30, 7);
                if (!insideBg)
                {
                    px[y * size + x] = 0x00000000;
                    continue;
                }

                // Gradient teal -> accent blue (#0EA5E9 -> #2563EB)
                float t = (x + y) / (float)(size * 2);
                byte r = (byte)(14 + (37 - 14) * t);
                byte g = (byte)(165 + (99 - 165) * t);
                byte b = (byte)(233 + (235 - 233) * t);
                uint color = (0xFFu << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

                // White clipboard board inner frame
                if (IsInsideRoundedRect(x, y, 8, 8, 23, 25, 3))
                {
                    color = 0xF5FFFFFF;
                    // Lines on board
                    if ((y == 14 || y == 18 || y == 22) && x >= 11 && x <= 20)
                    {
                        color = 0xFF2563EB;
                    }
                }

                // Top clip clasp
                if (IsInsideRoundedRect(x, y, 11, 5, 20, 9, 2))
                {
                    color = 0xFFFFFFFF;
                }

                px[y * size + x] = color;
            }
        }

        nint hMask = CreateBitmap(size, size, 1, 1, 0);
        ICONINFO ii = new()
        {
            fIcon = 1,
            hbmColor = hBitmap,
            hbmMask = hMask
        };
        nint hIcon = CreateIconIndirect(ref ii);
        DeleteObject(hBitmap);
        DeleteObject(hMask);
        return hIcon;
    }

    private static bool IsInsideRoundedRect(int x, int y, int left, int top, int right, int bottom, int radius)
    {
        if (x < left || x > right || y < top || y > bottom) return false;
        int cx = x < left + radius ? left + radius : (x > right - radius ? right - radius : x);
        int cy = y < top + radius ? top + radius : (y > bottom - radius ? bottom - radius : y);
        int dx = x - cx;
        int dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }
}
