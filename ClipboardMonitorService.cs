using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace FluentClip;

public sealed class ClipboardMonitorService : IDisposable
{
    private const int HotkeyId = 0x2026;
    private const uint TrayUid = 1001;

    private const uint CmdOpenPopup = 4001;
    private const uint CmdOpenSettings = 4002;
    private const uint CmdTogglePause = 4003;
    private const uint CmdClearUnpinned = 4004;
    private const uint CmdOpenCacheFolder = 4005;
    private const uint CmdExitApp = 4006;

    private readonly StorageService _storage;
    private readonly DispatcherQueue _dispatcher;
    private readonly NativeMethods.WndProcDelegate _wndProcDelegate;

    private nint _hwnd;
    private nint _trayIcon;
    private bool _trayAdded;
    private bool _disposed;

    private readonly uint _fmtExcludeMonitor;
    private readonly uint _fmtCanIncludeHistory;
    private readonly uint _fmtInternalWrite;
    public readonly uint MsgShowPopupFromSecondInstance;
    private readonly uint _msgTaskbarCreated;

    public bool IsPaused { get; set; }
    public nint LastExternalForegroundWindow { get; private set; }

    public event Action? TogglePopupRequested;
    public event Action? TrayIconClicked;
    public event Action? ShowPopupRequested;
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;
    public event Action? PauseStateChanged;

    public ClipboardMonitorService(StorageService storage, DispatcherQueue dispatcher)
    {
        _storage = storage;
        _dispatcher = dispatcher;
        _wndProcDelegate = WndProc;

        _fmtExcludeMonitor = NativeMethods.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        _fmtCanIncludeHistory = NativeMethods.RegisterClipboardFormatW("CanIncludeInClipboardHistory");
        _fmtInternalWrite = NativeMethods.RegisterClipboardFormatW("FluentClip_InternalWrite");
        MsgShowPopupFromSecondInstance = NativeMethods.RegisterWindowMessageW("FluentClip_ShowPopup_IPC");
        // Broadcast by Explorer when the taskbar is (re)created; tray icons must be re-added or they vanish.
        _msgTaskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
    }

    public unsafe void Start()
    {
        string className = "FluentClip_HiddenHostWindow";
        nint hInstance = NativeMethods.GetModuleHandleW(null);

        fixed (char* pClassName = className)
        {
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = hInstance,
                lpszClassName = (nint)pClassName
            };
            NativeMethods.RegisterClassExW(ref wc);
        }

        // Hidden top-level tool window (never shown): receives WM_CLIPBOARDUPDATE, WM_HOTKEY, WM_TRAYICON, and HWND_BROADCAST
        _hwnd = NativeMethods.CreateWindowExW(
            (uint)NativeMethods.WS_EX_TOOLWINDOW,
            className,
            "FluentClipHost",
            0,
            0, 0, 0, 0,
            0, 0, hInstance, 0);

        if (_hwnd != 0)
        {
            NativeMethods.AddClipboardFormatListener(_hwnd);
            RegisterConfiguredHotkey();
            CreateTrayIcon();
        }
    }

    public bool RegisterConfiguredHotkey()
    {
        if (_hwnd == 0) return false;
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);

        uint mods = _storage.Config.HotkeyModifiers | NativeMethods.MOD_NOREPEAT;
        uint vk = _storage.Config.HotkeyVirtualKey;
        _hotkeyRegistered = NativeMethods.RegisterHotKey(_hwnd, HotkeyId, mods, vk);
        return _hotkeyRegistered;
    }

    private bool _hotkeyRegistered;

    // A hotkey already taken by another app used to fail silently at start-up: the popup just never opened.
    private string BuildTrayTip()
    {
        string hotkey = _storage.Config.HotkeyDisplay;
        string status = IsPaused ? "已暂停"
            : _hotkeyRegistered ? hotkey
            : $"快捷键 {hotkey} 被占用，请在设置中更换";
        return $"FluentClip 剪贴板 ({status})";
    }

    public void SnapshotForegroundBeforePopup(nint ownPopupHwnd, nint ownSettingsHwnd)
    {
        nint fg = NativeMethods.GetForegroundWindow();
        // Opening from the tray makes the taskbar the foreground window; remembering it sent the paste
        // (Ctrl+V) to the taskbar. Keep the previously remembered app window instead.
        if (fg != 0 && fg != _hwnd && fg != ownPopupHwnd && fg != ownSettingsHwnd && !NativeMethods.IsShellTrayWindow(fg))
        {
            NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
            if (pid != (uint)Environment.ProcessId)
            {
                LastExternalForegroundWindow = fg;
            }
        }
    }

    private unsafe void CreateTrayIcon()
    {
        if (_trayIcon == 0)
            _trayIcon = NativeMethods.CreateFluentTrayIcon();

        NativeMethods.NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NativeMethods.NOTIFYICONDATAW);
        nid.hWnd = _hwnd;
        nid.uID = TrayUid;
        nid.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
        nid.uCallbackMessage = NativeMethods.WM_TRAYICON;
        nid.hIcon = _trayIcon;

        WriteFixedString(nid.szTip, 128, BuildTrayTip());

        _trayAdded = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref nid);
    }

    private unsafe void RemoveTrayIcon()
    {
        NativeMethods.NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NativeMethods.NOTIFYICONDATAW);
        nid.hWnd = _hwnd;
        nid.uID = TrayUid;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref nid);
        _trayAdded = false;
    }

    public unsafe void UpdateTrayTooltip()
    {
        if (!_trayAdded || _hwnd == 0) return;

        NativeMethods.NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NativeMethods.NOTIFYICONDATAW);
        nid.hWnd = _hwnd;
        nid.uID = TrayUid;
        nid.uFlags = NativeMethods.NIF_TIP;

        WriteFixedString(nid.szTip, 128, BuildTrayTip());
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);
    }

    private static unsafe void WriteFixedString(char* dest, int capacity, string text)
    {
        int len = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, len).CopyTo(new Span<char>(dest, capacity));
        dest[len] = '\0';
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // Exceptions cannot unwind through the native window-procedure frame; they would terminate the process.
        try
        {
            return WndProcCore(hWnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            App.LogError($"WndProc 0x{msg:X4}", ex);
            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private nint WndProcCore(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == MsgShowPopupFromSecondInstance && MsgShowPopupFromSecondInstance != 0)
        {
            _dispatcher.TryEnqueue(() => ShowPopupRequested?.Invoke());
            return 0;
        }

        if (msg == _msgTaskbarCreated && _msgTaskbarCreated != 0)
        {
            // Normally the old icon died with the old taskbar, but delete first in case it survived
            // (otherwise NIM_ADD fails and the icon is left un-updatable and un-removable).
            RemoveTrayIcon();
            CreateTrayIcon();
            return 0;
        }

        switch (msg)
        {
            case NativeMethods.WM_HOTKEY:
                if ((int)wParam == HotkeyId)
                {
                    _dispatcher.TryEnqueue(() => TogglePopupRequested?.Invoke());
                    return 0;
                }
                break;

            case NativeMethods.WM_CLIPBOARDUPDATE:
                if (!IsPaused)
                {
                    _dispatcher.TryEnqueue(async () =>
                    {
                        try
                        {
                            await CaptureCurrentClipboardAsync();
                        }
                        catch (Exception ex)
                        {
                            // async-void lambda: an escaping exception would take down the whole app
                            App.LogError("CaptureClipboard", ex);
                        }
                    });
                }
                return 0;

            case NativeMethods.WM_TRAYICON:
                uint mouseMsg = (uint)(lParam & 0xFFFF);
                // Only WM_LBUTTONUP: a double-click delivers UP, DBLCLK, UP, so also handling DBLCLK
                // toggled the popup three times (open → close → open flicker).
                if (mouseMsg == NativeMethods.WM_LBUTTONUP)
                {
                    _dispatcher.TryEnqueue(() => TrayIconClicked?.Invoke());
                    return 0;
                }
                if (mouseMsg == NativeMethods.WM_RBUTTONUP || mouseMsg == NativeMethods.WM_CONTEXTMENU)
                {
                    ShowTrayContextMenu();
                    return 0;
                }
                break;
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowTrayContextMenu()
    {
        nint hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == 0) return;

        try
        {
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, CmdOpenPopup, $"打开剪贴板面板 ({_storage.Config.HotkeyDisplay})");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, CmdOpenSettings, "设置与缓存目录...");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, CmdOpenCacheFolder, "打开当前缓存文件夹");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, 0, null);

            uint pauseFlags = NativeMethods.MF_STRING | (IsPaused ? NativeMethods.MF_CHECKED : 0);
            NativeMethods.AppendMenuW(hMenu, pauseFlags, CmdTogglePause, "暂停记录剪贴板");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, CmdClearUnpinned, "清空未收藏历史");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, CmdExitApp, "退出 FluentClip");

            NativeMethods.GetCursorPos(out NativeMethods.POINT pt);
            NativeMethods.SetForegroundWindow(_hwnd);

            int cmd = NativeMethods.TrackPopupMenu(
                hMenu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_BOTTOMALIGN | NativeMethods.TPM_RETURNCMD,
                pt.X,
                pt.Y,
                0,
                _hwnd,
                0);

            // Documented TrackPopupMenu quirk: without this the menu may not close when clicking elsewhere.
            NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, 0, 0);

            switch ((uint)cmd)
            {
                case CmdOpenPopup:
                    _dispatcher.TryEnqueue(() => ShowPopupRequested?.Invoke());
                    break;
                case CmdOpenSettings:
                    _dispatcher.TryEnqueue(() => OpenSettingsRequested?.Invoke());
                    break;
                case CmdOpenCacheFolder:
                    OpenCacheFolderInExplorer();
                    break;
                case CmdTogglePause:
                    IsPaused = !IsPaused;
                    UpdateTrayTooltip();
                    _dispatcher.TryEnqueue(() => PauseStateChanged?.Invoke());
                    break;
                case CmdClearUnpinned:
                    _dispatcher.TryEnqueue(() => _storage.ClearUnpinned());
                    break;
                case CmdExitApp:
                    _dispatcher.TryEnqueue(() => ExitRequested?.Invoke());
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    public void OpenCacheFolderInExplorer()
    {
        try
        {
            Directory.CreateDirectory(_storage.CacheRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_storage.CacheRoot}\"",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private async Task CaptureCurrentClipboardAsync()
    {
        nint owner = NativeMethods.GetClipboardOwner();
        string sourceApp = NativeMethods.GetProcessNameFromWindow(owner);
        if (string.IsNullOrEmpty(sourceApp))
        {
            sourceApp = NativeMethods.GetProcessNameFromWindow(NativeMethods.GetForegroundWindow());
        }

        // Retry briefly if another app still holds OpenClipboard
        bool opened = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (NativeMethods.OpenClipboard(_hwnd))
            {
                opened = true;
                break;
            }
            await Task.Delay(20);
        }

        if (!opened) return;

        string? capturedText = null;
        byte[]? rawPng = null;
        byte[]? rawBgra = null;
        int imgW = 0;
        int imgH = 0;
        List<string>? droppedFiles = null;

        try
        {
            // 1. Skip our own clipboard writes
            if (_fmtInternalWrite != 0 && NativeMethods.IsClipboardFormatAvailable(_fmtInternalWrite))
                return;

            // 2. Respect password managers (1Password, Bitwarden, KeePass, Windows Cloud Clipboard exclusions)
            if (_fmtExcludeMonitor != 0 && NativeMethods.IsClipboardFormatAvailable(_fmtExcludeMonitor))
                return;

            if (_fmtCanIncludeHistory != 0 && NativeMethods.IsClipboardFormatAvailable(_fmtCanIncludeHistory))
            {
                byte[]? flagBytes = ClipboardImageHelper.ReadClipboardGlobalBytes(_fmtCanIncludeHistory);
                if (flagBytes is { Length: >= 4 } && BitConverter.ToInt32(flagBytes, 0) == 0)
                    return;
            }

            // 3. Check Image formats first (PNG / CF_DIBV5 / CF_DIB)
            (rawPng, rawBgra, imgW, imgH) = ClipboardImageHelper.ReadRawImageFromOpenClipboard();

            // 4. Else check File Drop (CF_HDROP)
            if (rawPng == null && rawBgra == null && NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP))
            {
                droppedFiles = ClipboardImageHelper.ReadFileDropListFromOpenClipboard();
            }

            // 5. Else check Unicode Text
            if (rawPng == null && rawBgra == null && (droppedFiles == null || droppedFiles.Count == 0) &&
                NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
            {
                nint hText = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                if (hText != 0)
                {
                    nint pText = NativeMethods.GlobalLock(hText);
                    if (pText != 0)
                    {
                        try
                        {
                            capturedText = Marshal.PtrToStringUni(pText);
                        }
                        finally
                        {
                            NativeMethods.GlobalUnlock(hText);
                        }
                    }
                }
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        // Process & persist outside clipboard lock so system clipboard is never blocked!
        if (rawPng != null || rawBgra != null)
        {
            var normalized = await ClipboardImageHelper.NormalizeAndCreateThumbnailAsync(rawPng, rawBgra, imgW, imgH);
            if (normalized != null)
            {
                await _storage.AddImageClipAsync(normalized, sourceApp);
            }
            return;
        }

        if (droppedFiles is { Count: > 0 })
        {
            // If user copied a single image file in Explorer (< 35MB), store it as a rich previewable Image card!
            if (droppedFiles.Count == 1 && ClipboardImageHelper.IsImageFile(droppedFiles[0]))
            {
                try
                {
                    var fi = new FileInfo(droppedFiles[0]);
                    if (fi.Exists && fi.Length <= 35 * 1024 * 1024)
                    {
                        var normalized = await ClipboardImageHelper.NormalizeAndCreateThumbnailAsync(
                            null, null, 0, 0, droppedFiles[0]);
                        if (normalized != null)
                        {
                            await _storage.AddImageClipAsync(normalized, sourceApp);
                            return;
                        }
                    }
                }
                catch
                {
                }
            }

            await _storage.AddFileClipAsync(droppedFiles, sourceApp);
            return;
        }

        if (!string.IsNullOrWhiteSpace(capturedText))
        {
            await _storage.AddTextClipAsync(capturedText, sourceApp);
        }
    }

    public async Task<bool> PasteRecordAsync(ClipRecord record, bool pasteAsFile)
    {
        byte[]? pngBytes = null;
        byte[]? dibBytes = null;
        List<string>? fileDropPaths = null;

        if (record.Kind == ClipKind.Image)
        {
            string rawPath = _storage.GetRawImagePath(record.ImageHash);
            if (!File.Exists(rawPath)) return false;

            if (pasteAsFile)
            {
                fileDropPaths = new List<string> { GetPasteableImageFilePath(record, rawPath) };
            }
            else
            {
                pngBytes = await File.ReadAllBytesAsync(rawPath);
                dibBytes = await ClipboardImageHelper.ConvertPngToDibBytesAsync(pngBytes);
            }
        }
        else if (record.Kind == ClipKind.Files)
        {
            fileDropPaths = record.FilePaths;
        }

        bool opened = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (NativeMethods.OpenClipboard(_hwnd))
            {
                opened = true;
                break;
            }
            await Task.Delay(15);
        }
        if (!opened) return false;

        try
        {
            NativeMethods.EmptyClipboard();

            // Tag clipboard with our internal marker so WM_CLIPBOARDUPDATE ignores our own write
            if (_fmtInternalWrite != 0)
            {
                ClipboardImageHelper.SetClipboardBytes(_fmtInternalWrite, new byte[] { 1, 0, 0, 0 });
            }

            if (fileDropPaths is { Count: > 0 })
            {
                ClipboardImageHelper.WriteFileDropListToOpenClipboard(fileDropPaths);
            }
            else if (pngBytes != null)
            {
                ClipboardImageHelper.WriteDualImageToOpenClipboard(pngBytes, dibBytes);
            }
            else
            {
                WriteUnicodeTextToOpenClipboard(record.TextContent);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        _storage.TouchItem(record.Id);

        // Restore focus to target window & synthesize Ctrl+V seamlessly
        if (LastExternalForegroundWindow != 0 && NativeMethods.IsWindow(LastExternalForegroundWindow))
        {
            NativeMethods.SetForegroundWindow(LastExternalForegroundWindow);
        }

        await Task.Delay(40);
        NativeMethods.SendCtrlV();
        return true;
    }

    /// <summary>
    /// Pasting the cache file itself dropped a "3fa9c0…e1.png" (SHA-256 name) onto the desktop/folder.
    /// Prefer the original file the image was copied from; otherwise hand out a readably named temp copy.
    /// </summary>
    private static string GetPasteableImageFilePath(ClipRecord record, string rawPath)
    {
        foreach (string original in record.FilePaths)
        {
            if (File.Exists(original))
                return original;
        }

        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "FluentClip");
            Directory.CreateDirectory(dir);
            var created = DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedUnixMs).ToLocalTime();
            string shortHash = record.ImageHash.Length >= 6 ? record.ImageHash[..6] : record.ImageHash;
            string friendly = Path.Combine(dir, $"FluentClip_{created:yyyyMMdd_HHmmss}_{shortHash}.png");
            if (!File.Exists(friendly))
                File.Copy(rawPath, friendly);
            return friendly;
        }
        catch (Exception ex)
        {
            App.LogError("GetPasteableImageFilePath", ex);
            return rawPath;
        }
    }

    private static unsafe void WriteUnicodeTextToOpenClipboard(string text)
    {
        int byteLen = (text.Length + 1) * 2;
        nint hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (nuint)byteLen);
        if (hGlobal == 0) return;

        nint ptr = NativeMethods.GlobalLock(hGlobal);
        if (ptr == 0)
        {
            NativeMethods.GlobalFree(hGlobal);
            return;
        }

        try
        {
            char* dst = (char*)ptr;
            text.AsSpan().CopyTo(new Span<char>(dst, text.Length));
            dst[text.Length] = '\0';
        }
        finally
        {
            NativeMethods.GlobalUnlock(hGlobal);
        }

        if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hGlobal) == 0)
        {
            NativeMethods.GlobalFree(hGlobal);
        }
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != 0)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            NativeMethods.RemoveClipboardFormatListener(_hwnd);

            if (_trayAdded)
                RemoveTrayIcon();

            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        if (_trayIcon != 0)
        {
            NativeMethods.DestroyIcon(_trayIcon);
            _trayIcon = 0;
        }
    }
}
