using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;

namespace FluentClip;

public sealed partial class SettingsWindow : Window
{
    private static readonly int[] MaxSizeValues = [100, 300, 500, 1024, 2048, 0];
    private static readonly int[] MaxCountValues = [200, 500, 1000, 2000];
    private static readonly string[] ThemeValues = ["System", "Dark", "Light"];
    private static readonly (uint Mods, uint Vk, string Display)[] PresetHotkeys =
    [
        (1, 86, "Alt + V"),
        (6, 86, "Ctrl + Shift + V"),
        (1, 192, "Alt + `"),
        (3, 86, "Ctrl + Alt + V"),
        (1, 67, "Alt + C")
    ];

    public nint Hwnd { get; private set; }
    public bool IsClosed { get; private set; }
    public FrameworkElement RootElement => SettingsRootGrid;

    private AppWindow _appWindow = null!;
    private bool _isLoadingUi = true;
    private bool _isRecordingHotkey;

    public SettingsWindow()
    {
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowId wndId = Win32Interop.GetWindowIdFromWindow(Hwnd);
        _appWindow = AppWindow.GetFromWindowId(wndId);

        CenterWindowOnMonitor(660, 720);
        NativeMethods.ApplyRoundedCorners(Hwnd);

        Closed += (_, _) =>
        {
            IsClosed = true;
            App.Storage.HistoryChanged -= OnStorageHistoryChanged;
        };

        App.Storage.HistoryChanged += OnStorageHistoryChanged;

        LoadCurrentSettingsToUi();
        RefreshStorageMeter();
        _isLoadingUi = false;
    }

    private void OnStorageHistoryChanged()
    {
        if (!IsClosed)
        {
            DispatcherQueue.TryEnqueue(RefreshStorageMeter);
        }
    }

    public void ActivateAndBringToFront()
    {
        RefreshStorageMeter();
        Activate();
        NativeMethods.SetForegroundWindow(Hwnd);
    }

    public void ForceClose()
    {
        if (!IsClosed)
            Close();
    }

    private void CenterWindowOnMonitor(int logicalW, int logicalH)
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT pt);
        nint hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.MONITORINFO mi = default;
        mi.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>();
        NativeMethods.GetMonitorInfoW(hMon, ref mi);

        // DPI of the monitor the window is centred on, not the one it was created on.
        uint dpi = 0;
        if (NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out uint monDpiX, out _) == 0)
            dpi = monDpiX;
        if (dpi == 0) dpi = NativeMethods.GetDpiForWindow(Hwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;

        int w = (int)Math.Round(logicalW * scale);
        int h = (int)Math.Round(logicalH * scale);
        int x = mi.rcWork.Left + Math.Max(0, (mi.rcWork.Width - w) / 2);
        int y = mi.rcWork.Top + Math.Max(0, (mi.rcWork.Height - h) / 2);

        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private void LoadCurrentSettingsToUi()
    {
        var cfg = App.Storage.Config;
        CacheDirTextBox.Text = cfg.CacheDirectory;

        int sizeIdx = Array.IndexOf(MaxSizeValues, cfg.MaxCacheSizeMb);
        MaxSizeCombo.SelectedIndex = sizeIdx >= 0 ? sizeIdx : 2;

        int countIdx = Array.IndexOf(MaxCountValues, cfg.MaxHistoryCount);
        MaxCountCombo.SelectedIndex = countIdx >= 0 ? countIdx : 1;

        int themeIdx = Array.FindIndex(ThemeValues, t => string.Equals(t, cfg.ThemeMode, StringComparison.OrdinalIgnoreCase));
        ThemeCombo.SelectedIndex = themeIdx >= 0 ? themeIdx : 0;

        int hkIdx = Array.FindIndex(PresetHotkeys, h => h.Mods == cfg.HotkeyModifiers && h.Vk == cfg.HotkeyVirtualKey);
        PresetHotkeyCombo.SelectedIndex = hkIdx;
        CurrentHotkeySubtext.Text = $"当前生效: {cfg.HotkeyDisplay}";

        FollowCaretSwitch.IsOn = cfg.FollowCursorOrCaret;
        AutoStartSwitch.IsOn = cfg.AutoStartOnBoot;
    }

    private void RefreshStorageMeter()
    {
        var stats = App.Storage.GetStats();
        int maxMb = App.Storage.Config.MaxCacheSizeMb;

        StorageStatsText.Text = $"当前总占用: {StorageService.FormatSize(stats.TotalBytes)}";
        StorageQuotaLabel.Text = maxMb > 0 ? $"空间上限 {maxMb} MB" : "不限制空间上限";

        if (maxMb > 0)
        {
            double pct = (stats.TotalBytes / (double)(maxMb * 1024L * 1024L)) * 100.0;
            StorageProgressBar.Value = Math.Clamp(pct, 0, 100);
        }
        else
        {
            StorageProgressBar.Value = Math.Min(100, stats.TotalBytes / (10.0 * 1024 * 1024));
        }

        StorageBreakdownText.Text =
            $"共 {stats.TotalItems} 条记录  ·  文本/文件 {stats.TextItems} 条  ·  去重图片 {stats.ImageItems} 张 ({StorageService.FormatSize(stats.TotalImageBytes)})  ·  ★ 已收藏 {stats.PinnedItems} 条";
    }

    private async void BrowseCacheDirBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && !string.IsNullOrWhiteSpace(folder.Path))
            {
                CacheDirTextBox.Text = folder.Path;
            }
        }
        catch (Exception ex)
        {
            ShowBanner($"无法打开系统文件夹选择器，你可以直接在输入框粘贴目标路径。({ex.Message})", InfoBarSeverity.Warning);
        }
    }

    private void OpenCacheDirBtn_Click(object sender, RoutedEventArgs e)
    {
        App.Monitor.OpenCacheFolderInExplorer();
    }

    private async void ApplyCacheDirBtn_Click(object sender, RoutedEventArgs e)
    {
        string targetPath = CacheDirTextBox.Text.Trim();
        bool migrate = MigrateExistingCheckBox.IsChecked == true;

        ApplyCacheDirBtn.IsEnabled = false;
        try
        {
            var (ok, msg) = await App.Storage.MigrateCacheDirectoryAsync(targetPath, migrate);
            CacheDirTextBox.Text = App.Storage.Config.CacheDirectory;
            RefreshStorageMeter();
            ShowBanner(msg, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        finally
        {
            ApplyCacheDirBtn.IsEnabled = true;
        }
    }

    private void QuotaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi) return;

        if (MaxSizeCombo.SelectedIndex >= 0 && MaxSizeCombo.SelectedIndex < MaxSizeValues.Length)
        {
            App.Storage.Config.MaxCacheSizeMb = MaxSizeValues[MaxSizeCombo.SelectedIndex];
        }

        if (MaxCountCombo.SelectedIndex >= 0 && MaxCountCombo.SelectedIndex < MaxCountValues.Length)
        {
            App.Storage.Config.MaxHistoryCount = MaxCountValues[MaxCountCombo.SelectedIndex];
        }

        App.Storage.SaveBootstrapConfig();
        App.Storage.EnforceQuota();
        RefreshStorageMeter();
    }

    private void CleanUnpinnedCacheBtn_Click(object sender, RoutedEventArgs e)
    {
        App.Storage.ClearUnpinned();
        RefreshStorageMeter();
        ShowBanner("已清理所有未收藏记录与孤立图片缓存！", InfoBarSeverity.Success);
    }

    private void PresetHotkeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi) return;
        int idx = PresetHotkeyCombo.SelectedIndex;
        if (idx >= 0 && idx < PresetHotkeys.Length)
        {
            var hk = PresetHotkeys[idx];
            ApplyNewHotkey(hk.Mods, hk.Vk, hk.Display);
        }
    }

    private void RecordHotkeyBtn_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingHotkey = true;
        RecordHotkeyBtn.Content = "请按下组合键...";
        RecordHotkeyBtn.Focus(FocusState.Programmatic);
    }

    private void RecordHotkeyBtn_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecordingHotkey) return;
        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            _isRecordingHotkey = false;
            RecordHotkeyBtn.Content = "录制自定义键位";
            return;
        }

        // Ignore standalone modifier key presses while waiting for the main key
        if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        uint mods = 0;
        var parts = new List<string>();

        if (IsKeyDown(VirtualKey.Control)) { mods |= NativeMethods.MOD_CONTROL; parts.Add("Ctrl"); }
        if (IsKeyDown(VirtualKey.Menu)) { mods |= NativeMethods.MOD_ALT; parts.Add("Alt"); }
        if (IsKeyDown(VirtualKey.Shift)) { mods |= NativeMethods.MOD_SHIFT; parts.Add("Shift"); }
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) { mods |= NativeMethods.MOD_WIN; parts.Add("Win"); }

        if (mods == 0)
        {
            ShowBanner("请至少包含一个修饰键（Alt / Ctrl / Shift）以免影响正常打字。", InfoBarSeverity.Warning);
            return;
        }

        uint vk = (uint)e.Key;
        string keyLabel = GetKeyLabel(e.Key);
        parts.Add(keyLabel);
        string display = string.Join(" + ", parts);

        _isRecordingHotkey = false;
        RecordHotkeyBtn.Content = "录制自定义键位";

        _isLoadingUi = true;
        PresetHotkeyCombo.SelectedIndex = -1;
        _isLoadingUi = false;

        ApplyNewHotkey(mods, vk, display);
    }

    private void ApplyNewHotkey(uint mods, uint vk, string display)
    {
        uint oldMods = App.Storage.Config.HotkeyModifiers;
        uint oldVk = App.Storage.Config.HotkeyVirtualKey;
        string oldDisplay = App.Storage.Config.HotkeyDisplay;

        App.Storage.Config.HotkeyModifiers = mods;
        App.Storage.Config.HotkeyVirtualKey = vk;
        App.Storage.Config.HotkeyDisplay = display;

        if (App.Monitor.RegisterConfiguredHotkey())
        {
            App.Storage.SaveBootstrapConfig();
            App.Monitor.UpdateTrayTooltip();
            CurrentHotkeySubtext.Text = $"当前生效: {display}";
            ShowBanner($"全局呼出快捷键已更新为 {display}", InfoBarSeverity.Success);
        }
        else
        {
            App.Storage.Config.HotkeyModifiers = oldMods;
            App.Storage.Config.HotkeyVirtualKey = oldVk;
            App.Storage.Config.HotkeyDisplay = oldDisplay;
            App.Monitor.RegisterConfiguredHotkey();
            ShowBanner($"快捷键 {display} 已被其他软件占用，请选择其他组合键。", InfoBarSeverity.Error);
        }
    }

    // VirtualKey has no names for OEM symbol keys, so e.g. Alt+` was displayed as "Alt + 192".
    private static string GetKeyLabel(VirtualKey key)
    {
        switch ((int)key)
        {
            case 0xBA: return ";";
            case 0xBB: return "=";
            case 0xBC: return ",";
            case 0xBD: return "-";
            case 0xBE: return ".";
            case 0xBF: return "/";
            case 0xC0: return "`";
            case 0xDB: return "[";
            case 0xDC: return "\\";
            case 0xDD: return "]";
            case 0xDE: return "'";
        }

        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            return ((int)key - (int)VirtualKey.Number0).ToString(CultureInfo.InvariantCulture);
        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
            return "Num " + ((int)key - (int)VirtualKey.NumberPad0).ToString(CultureInfo.InvariantCulture);

        return key.ToString();
    }

    private void RecordHotkeyBtn_LostFocus(object sender, RoutedEventArgs e)
    {
        // Clicking elsewhere used to leave the button stuck on "请按下组合键..." and still capturing keys.
        if (_isRecordingHotkey)
        {
            _isRecordingHotkey = false;
            RecordHotkeyBtn.Content = "录制自定义键位";
        }
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        return InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private void FollowCaretSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi) return;
        App.Storage.Config.FollowCursorOrCaret = FollowCaretSwitch.IsOn;
        App.Storage.SaveBootstrapConfig();
    }

    private void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi) return;
        App.Storage.Config.AutoStartOnBoot = AutoStartSwitch.IsOn;
        App.Storage.SaveBootstrapConfig();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi) return;
        int idx = ThemeCombo.SelectedIndex;
        if (idx >= 0 && idx < ThemeValues.Length)
        {
            App.Storage.Config.ThemeMode = ThemeValues[idx];
            App.Storage.SaveBootstrapConfig();
        }
    }

    private void ShowBanner(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
