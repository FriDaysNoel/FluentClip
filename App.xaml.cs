using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FluentClip;

public sealed partial class App : Application
{
    private nint _singleInstanceMutex;

    public static App Instance { get; private set; } = null!;
    public static StorageService Storage { get; private set; } = null!;
    public static ClipboardMonitorService Monitor { get; private set; } = null!;

    private MainWindow? _popupWindow;
    private SettingsWindow? _settingsWindow;

    public App()
    {
        Instance = this;

        // Registered before InitializeComponent so XAML start-up failures are logged too.
        // A background tray app must not die from one bad clipboard payload or a locked file.
        UnhandledException += (_, e) =>
        {
            LogError($"UnhandledException: {e.Message}", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        InitializeComponent();
    }

    private static readonly object LogLock = new();

    public static void LogError(string context, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluentClip");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "error.log");
            lock (LogLock)
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                    fi.Delete();
                File.AppendAllText(path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single-instance check: if already running, tell the existing tray instance to pop open the HUD!
        // Read the error via the marshaller (SetLastError=true); a separate GetLastError P/Invoke may see a clobbered value.
        _singleInstanceMutex = NativeMethods.CreateMutexW(0, true, @"Local\FluentClip_SingleInstance_Mutex_v1");
        if (Marshal.GetLastPInvokeError() == NativeMethods.ERROR_ALREADY_EXISTS)
        {
            uint showMsg = NativeMethods.RegisterWindowMessageW("FluentClip_ShowPopup_IPC");
            if (showMsg != 0)
            {
                NativeMethods.PostMessageW(NativeMethods.HWND_BROADCAST, showMsg, 0, 0);
            }
            Environment.Exit(0);
            return;
        }

        Storage = new StorageService();
        Storage.Initialize();

        Monitor = new ClipboardMonitorService(Storage, DispatcherQueue.GetForCurrentThread());
        Monitor.TogglePopupRequested += TogglePopup;
        Monitor.ShowPopupRequested += ShowPopup;
        Monitor.OpenSettingsRequested += ShowSettings;
        Monitor.ExitRequested += ExitApplication;
        Monitor.Start();

        _popupWindow = new MainWindow();
        _popupWindow.InitializePrewarmed();

        Storage.ConfigChanged += ApplyThemeToAllWindows;
        ApplyThemeToAllWindows();

        // Unless launched silently via --tray on boot, pop open the sleek HUD once so the user sees it immediately!
        string[] cmdArgs = Environment.GetCommandLineArgs();
        bool silentTray = cmdArgs.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (!silentTray)
        {
            ShowPopup();
        }
    }

    public void TogglePopup()
    {
        if (_popupWindow == null) return;

        if (_popupWindow.IsPopupVisible)
        {
            _popupWindow.HidePopup();
        }
        else
        {
            ShowPopup();
        }
    }

    public void ShowPopup()
    {
        if (_popupWindow == null) return;
        nint settingsHwnd = _settingsWindow != null ? _settingsWindow.Hwnd : 0;
        Monitor.SnapshotForegroundBeforePopup(_popupWindow.Hwnd, settingsHwnd);
        _popupWindow.ShowAtSmartPosition();
    }

    public void ShowSettings()
    {
        if (_settingsWindow == null || _settingsWindow.IsClosed)
        {
            _settingsWindow = new SettingsWindow();
            ApplyThemeToWindow(_settingsWindow.RootElement);
        }
        _settingsWindow.ActivateAndBringToFront();
    }

    public void ApplyThemeToAllWindows()
    {
        if (_popupWindow != null)
            ApplyThemeToWindow(_popupWindow.RootElement);
        if (_settingsWindow is { IsClosed: false })
            ApplyThemeToWindow(_settingsWindow.RootElement);
    }

    private static void ApplyThemeToWindow(FrameworkElement? root)
    {
        if (root == null) return;
        root.RequestedTheme = Storage.Config.ThemeMode switch
        {
            "Dark" => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _ => ElementTheme.Default
        };
    }

    public void ExitApplication()
    {
        Monitor.Dispose();
        if (_settingsWindow is { IsClosed: false })
            _settingsWindow.ForceClose();
        _popupWindow?.ForceClose();

        if (_singleInstanceMutex != 0)
        {
            NativeMethods.CloseHandle(_singleInstanceMutex);
            _singleInstanceMutex = 0;
        }

        Exit();
        Environment.Exit(0);
    }
}
