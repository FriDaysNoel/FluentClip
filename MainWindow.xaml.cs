using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT;
using Color = Windows.UI.Color;

namespace FluentClip;

public sealed partial class MainWindow : Window
{
    private const int PopupLogicalWidth = 410;
    private const int PopupLogicalHeight = 570;
    private const int MaxRenderedCards = 80;

    public nint Hwnd { get; private set; }
    public bool IsPopupVisible { get; private set; }
    public FrameworkElement RootElement => RootGrid;

    private AppWindow _appWindow = null!;
    private bool _allowForceClose;
    private bool _isPastingOrTransitioning;
    private string _currentCategory = "All";

    private IReadOnlyList<ClipRecord> _visibleItems = Array.Empty<ClipRecord>();
    private readonly List<Border> _cardBorders = new();
    private int _selectedIndex = -1;
    private ClipRecord? _lightboxItem;

    public MainWindow()
    {
        InitializeComponent();

        SystemBackdrop = new DesktopAcrylicBackdrop();

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowId wndId = Win32Interop.GetWindowIdFromWindow(Hwnd);
        _appWindow = AppWindow.GetFromWindowId(wndId);

        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        _appWindow.SetPresenter(presenter);

        _appWindow.IsShownInSwitchers = false;
        _appWindow.Closing += OnAppWindowClosing;
        NativeMethods.ApplyRoundedCorners(Hwnd);

        Activated += OnWindowActivated;

        App.Storage.HistoryChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (IsPopupVisible)
                RefreshCards();
        });

        App.Monitor.PauseStateChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            PauseToggleBtn.IsChecked = App.Monitor.IsPaused;
        });

        UpdateFilterButtonVisuals();
    }

    public void InitializePrewarmed()
    {
        var bounds = NativeMethods.CalculateClampedPopupBounds(Hwnd, PopupLogicalWidth, PopupLogicalHeight, preferCaret: false);
        _appWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.WidthPx, bounds.HeightPx));
        _appWindow.Hide();
        IsPopupVisible = false;
    }

    public void ShowAtSmartPosition()
    {
        _isPastingOrTransitioning = true;
        try
        {
            CloseLightbox();
            PauseToggleBtn.IsChecked = App.Monitor.IsPaused;

            bool followCaret = App.Storage.Config.FollowCursorOrCaret;
            var bounds = NativeMethods.CalculateClampedPopupBounds(Hwnd, PopupLogicalWidth, PopupLogicalHeight, followCaret);
            _appWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.WidthPx, bounds.HeightPx));

            SearchBox.Text = string.Empty;
            RefreshCards();

            _appWindow.Show();
            NativeMethods.SetForegroundWindow(Hwnd);
            IsPopupVisible = true;

            DispatcherQueue.TryEnqueue(() =>
            {
                SearchBox.Focus(FocusState.Programmatic);
                CardsScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
                _isPastingOrTransitioning = false;
            });
        }
        catch
        {
            _isPastingOrTransitioning = false;
        }
    }

    public void HidePopup()
    {
        if (!IsPopupVisible) return;
        CloseLightbox();
        _appWindow.Hide();
        IsPopupVisible = false;
    }

    public void ForceClose()
    {
        _allowForceClose = true;
        Close();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowForceClose)
        {
            args.Cancel = true;
            HidePopup();
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (!_isPastingOrTransitioning && IsPopupVisible && PinWindowBtn.IsChecked != true)
            {
                HidePopup();
            }
        }
    }

    private void RefreshCards()
    {
        var allMatches = App.Storage.QueryItems(SearchBox.Text, _currentCategory);
        _visibleItems = allMatches;
        CountBadgeText.Text = allMatches.Count.ToString(CultureInfo.InvariantCulture);

        var stats = App.Storage.GetStats();
        CacheSizeFooterBtn.Content = $"缓存 {StorageService.FormatSize(stats.TotalBytes)}";

        CardsStack.Children.Clear();
        _cardBorders.Clear();

        if (allMatches.Count == 0)
        {
            EmptyStatePanel.Visibility = Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                EmptyStateTitle.Text = "未找到匹配的记录";
                EmptyStateSubtitle.Text = "尝试更换关键词或切换分类筛选";
            }
            else if (_currentCategory == "Pinned")
            {
                EmptyStateTitle.Text = "暂无收藏条目";
                EmptyStateSubtitle.Text = "点击卡片右上角的 ★ 即可永久保护常用内容";
            }
            else
            {
                EmptyStateTitle.Text = "剪贴板历史为空";
                EmptyStateSubtitle.Text = "复制任意文字、截图或图片文件即可自动收录";
            }
            _selectedIndex = -1;
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;

        int renderCount = Math.Min(allMatches.Count, MaxRenderedCards);
        for (int i = 0; i < renderCount; i++)
        {
            var item = allMatches[i];
            Border card = BuildClipCard(item, i);
            _cardBorders.Add(card);
            CardsStack.Children.Add(card);
        }

        SelectCard(0, scrollIntoView: false);
    }

    private Border BuildClipCard(ClipRecord item, int index)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11, 9, 11, 9),
            BorderThickness = new Thickness(1.2),
            Background = GetThemeBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = GetThemeBrush("CardStrokeColorDefaultBrush"),
            Tag = index
        };

        var outerStack = new StackPanel { Spacing = 6 };

        // 1. Metadata & Actions Top Row
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftMeta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

        if (index < 9)
        {
            var indexBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Background = GetThemeBrush("SubtleFillColorSecondaryBrush"),
                Child = new TextBlock
                {
                    Text = $"{index + 1}",
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = GetThemeBrush("TextFillColorSecondaryBrush")
                }
            };
            leftMeta.Children.Add(indexBadge);
        }

        // Kind Badge
        string kindLabel = item.Kind switch
        {
            ClipKind.Image => $"图片 {item.ImageWidth}×{item.ImageHeight}",
            ClipKind.Files => "文件",
            _ when !string.IsNullOrEmpty(item.DetectedColorHex) => $"颜色 {item.DetectedColorHex}",
            _ when item.IsUrl => "链接",
            _ => "文本"
        };

        var kindBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Background = item.Kind == ClipKind.Image
                ? new SolidColorBrush(Color.FromArgb(36, 14, 165, 233))
                : new SolidColorBrush(Color.FromArgb(28, 99, 102, 241)),
            Child = new TextBlock
            {
                Text = kindLabel,
                FontSize = 10.5,
                Foreground = item.Kind == ClipKind.Image
                    ? new SolidColorBrush(Color.FromArgb(255, 56, 189, 248))
                    : GetThemeBrush("TextFillColorSecondaryBrush")
            }
        };
        leftMeta.Children.Add(kindBadge);

        string appAndTime = string.IsNullOrEmpty(item.SourceApp)
            ? FormatRelativeTime(item.UpdatedUnixMs)
            : $"{item.SourceApp} · {FormatRelativeTime(item.UpdatedUnixMs)}";

        leftMeta.Children.Add(new TextBlock
        {
            Text = appAndTime,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = GetThemeBrush("TextFillColorTertiaryBrush")
        });

        Grid.SetColumn(leftMeta, 0);
        headerGrid.Children.Add(leftMeta);

        // Action Buttons Row
        var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        if (item.Kind == ClipKind.Image)
        {
            var previewBtn = CreateMiniIconButton("\uE7B3", "查看高清原图", (_, _) => OpenLightbox(item));
            var asFileBtn = CreateMiniIconButton("\uE8B7", "粘贴为 PNG 文件（适用于桌面/文件夹）", async (_, _) => await ExecutePasteAsync(item, pasteAsFile: true));
            actionsPanel.Children.Add(previewBtn);
            actionsPanel.Children.Add(asFileBtn);
        }

        var pinBtn = CreateMiniIconButton(
            item.IsPinned ? "\uE735" : "\uE734",
            item.IsPinned ? "取消收藏" : "收藏并保护（清理缓存时不删除）",
            (_, _) => App.Storage.TogglePin(item.Id),
            item.IsPinned ? new SolidColorBrush(Color.FromArgb(255, 245, 158, 11)) : null);

        var delBtn = CreateMiniIconButton("\uE711", "删除此条记录", (_, _) => App.Storage.DeleteItem(item.Id));

        actionsPanel.Children.Add(pinBtn);
        actionsPanel.Children.Add(delBtn);

        Grid.SetColumn(actionsPanel, 1);
        headerGrid.Children.Add(actionsPanel);
        outerStack.Children.Add(headerGrid);

        // 2. Content Body
        var bodyContainer = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0, 2, 0, 0)
        };

        if (item.Kind == ClipKind.Image)
        {
            var imgFrame = new Border
            {
                CornerRadius = new CornerRadius(7),
                MaxHeight = 138,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(1),
                BorderBrush = GetThemeBrush("SurfaceStrokeColorDefaultBrush"),
                Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)),
                Padding = new Thickness(4)
            };

            var imgControl = new Image
            {
                MaxHeight = 128,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            string thumbPath = App.Storage.GetThumbImagePath(item.ImageHash);
            _ = LoadThumbnailIntoImageAsync(imgControl, thumbPath);

            imgFrame.Child = imgControl;
            bodyContainer.Child = imgFrame;
        }
        else if (!string.IsNullOrEmpty(item.DetectedColorHex) && TryParseHexColor(item.DetectedColorHex, out Color swatchColor))
        {
            var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            colorRow.Children.Add(new Border
            {
                Width = 42,
                Height = 24,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(swatchColor),
                BorderThickness = new Thickness(1),
                BorderBrush = GetThemeBrush("SurfaceStrokeColorDefaultBrush")
            });
            colorRow.Children.Add(new TextBlock
            {
                Text = item.TextContent.Trim(),
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            bodyContainer.Child = colorRow;
        }
        else
        {
            string previewText = item.Kind == ClipKind.Files ? item.Summary + "\n" + item.TextContent : item.TextContent.Trim();
            if (previewText.Length > 360)
                previewText = previewText[..360] + "…";

            bodyContainer.Child = new TextBlock
            {
                Text = previewText,
                FontSize = 12.5,
                MaxLines = 3,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        bodyContainer.Tapped += async (_, e) =>
        {
            e.Handled = true;
            bool shiftHeld = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            await ExecutePasteAsync(item, pasteAsFile: shiftHeld);
        };

        outerStack.Children.Add(bodyContainer);
        card.Child = outerStack;

        card.PointerEntered += (_, _) => SelectCard(index, scrollIntoView: false);

        return card;
    }

    private static async Task LoadThumbnailIntoImageAsync(Image target, string path)
    {
        var bmp = await ClipboardImageHelper.LoadUnlockedBitmapAsync(path);
        if (bmp != null)
        {
            target.Source = bmp;
        }
    }

    private static Button CreateMiniIconButton(string glyph, string tooltip, RoutedEventHandler onClick, Brush? customForeground = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 11.5 };
        if (customForeground != null)
            icon.Foreground = customForeground;

        var btn = new Button
        {
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = icon
        };
        ToolTipService.SetToolTip(btn, tooltip);
        btn.Click += onClick;
        return btn;
    }

    private void SelectCard(int index, bool scrollIntoView)
    {
        if (_cardBorders.Count == 0)
        {
            _selectedIndex = -1;
            return;
        }

        int clamped = Math.Clamp(index, 0, _cardBorders.Count - 1);
        if (_selectedIndex >= 0 && _selectedIndex < _cardBorders.Count)
        {
            var prev = _cardBorders[_selectedIndex];
            prev.BorderBrush = GetThemeBrush("CardStrokeColorDefaultBrush");
            prev.Background = GetThemeBrush("CardBackgroundFillColorDefaultBrush");
        }

        _selectedIndex = clamped;
        var current = _cardBorders[_selectedIndex];
        current.BorderBrush = GetThemeBrush("AccentFillColorDefaultBrush");
        current.Background = GetThemeBrush("CardBackgroundFillColorSecondaryBrush");

        if (scrollIntoView)
        {
            current.StartBringIntoView();
        }
    }

    private async Task ExecutePasteAsync(ClipRecord item, bool pasteAsFile)
    {
        _isPastingOrTransitioning = true;
        try
        {
            HidePopup();
            await App.Monitor.PasteRecordAsync(item, pasteAsFile);
        }
        finally
        {
            _isPastingOrTransitioning = false;
        }
    }

    private async void OpenLightbox(ClipRecord item)
    {
        if (item.Kind != ClipKind.Image) return;
        _lightboxItem = item;
        LightboxMetaText.Text = $"{item.ImageWidth} × {item.ImageHeight} · {StorageService.FormatSize(item.ByteSize)}";
        LightboxImage.Source = null;
        LightboxOverlay.Visibility = Visibility.Visible;

        string rawPath = App.Storage.GetRawImagePath(item.ImageHash);
        var fullBmp = await ClipboardImageHelper.LoadUnlockedBitmapAsync(rawPath);
        if (fullBmp != null && _lightboxItem?.Id == item.Id)
        {
            LightboxImage.Source = fullBmp;
        }
    }

    private void CloseLightbox()
    {
        _lightboxItem = null;
        LightboxImage.Source = null;
        LightboxOverlay.Visibility = Visibility.Collapsed;
    }

    private void CloseLightboxBtn_Click(object sender, RoutedEventArgs e) => CloseLightbox();

    private async void LightboxPasteImageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lightboxItem != null)
            await ExecutePasteAsync(_lightboxItem, pasteAsFile: false);
    }

    private async void LightboxPasteFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lightboxItem != null)
            await ExecutePasteAsync(_lightboxItem, pasteAsFile: true);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshCards();
    }

    private async void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (LightboxOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == VirtualKey.Escape || e.Key == VirtualKey.Space)
            {
                CloseLightbox();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Enter && _lightboxItem != null)
            {
                e.Handled = true;
                await ExecutePasteAsync(_lightboxItem, pasteAsFile: false);
            }
            return;
        }

        if (e.Key == VirtualKey.Down)
        {
            SelectCard(_selectedIndex + 1, scrollIntoView: true);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up)
        {
            SelectCard(_selectedIndex - 1, scrollIntoView: true);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            if (_selectedIndex >= 0 && _selectedIndex < _visibleItems.Count)
            {
                bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                e.Handled = true;
                await ExecutePasteAsync(_visibleItems[_selectedIndex], pasteAsFile: shift);
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            HidePopup();
        }
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (LightboxOverlay.Visibility == Visibility.Visible)
                CloseLightbox();
            else
                HidePopup();
            e.Handled = true;
            return;
        }

        bool altDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool ctrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if ((altDown || ctrlDown) && e.Key >= VirtualKey.Number1 && e.Key <= VirtualKey.Number9)
        {
            int slot = (int)e.Key - (int)VirtualKey.Number1;
            if (slot >= 0 && slot < _visibleItems.Count)
            {
                e.Handled = true;
                await ExecutePasteAsync(_visibleItems[slot], pasteAsFile: false);
            }
        }
    }

    private void FilterTab_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, TabAllBtn)) _currentCategory = "All";
        else if (ReferenceEquals(sender, TabTextBtn)) _currentCategory = "Text";
        else if (ReferenceEquals(sender, TabImageBtn)) _currentCategory = "Image";
        else if (ReferenceEquals(sender, TabPinnedBtn)) _currentCategory = "Pinned";

        UpdateFilterButtonVisuals();
        RefreshCards();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void UpdateFilterButtonVisuals()
    {
        SetTabVisual(TabAllBtn, _currentCategory == "All");
        SetTabVisual(TabTextBtn, _currentCategory == "Text");
        SetTabVisual(TabImageBtn, _currentCategory == "Image");
        SetTabVisual(TabPinnedBtn, _currentCategory == "Pinned");
    }

    private static void SetTabVisual(Button btn, bool active)
    {
        if (active)
        {
            btn.Background = GetThemeBrush("AccentFillColorDefaultBrush");
            btn.Foreground = new SolidColorBrush(Colors.White);
            btn.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }
        else
        {
            btn.ClearValue(Control.BackgroundProperty);
            btn.ClearValue(Control.ForegroundProperty);
            btn.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private static Brush GetThemeBrush(string key)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out object? obj) && obj != null)
            {
                if (obj is Brush brush) return brush;
                if (obj is IWinRTObject winRtObj) return winRtObj.As<Brush>();
            }
        }
        catch
        {
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    private void PauseToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        App.Monitor.IsPaused = PauseToggleBtn.IsChecked == true;
        App.Monitor.UpdateTrayTooltip();
    }

    private void PinWindowBtn_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void ClearUnpinnedBtn_Click(object sender, RoutedEventArgs e)
    {
        App.Storage.ClearUnpinned();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OpenSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PinWindowBtn.IsChecked != true)
            HidePopup();
        App.Instance.ShowSettings();
    }

    private static string FormatRelativeTime(long unixMs)
    {
        TimeSpan diff = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (diff.TotalSeconds < 45) return "刚刚";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} 分钟前";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} 小时前";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays} 天前";
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("MM-dd HH:mm");
    }

    private static bool TryParseHexColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        string s = hex.TrimStart('#');
        try
        {
            if (s.Length == 6)
            {
                byte r = byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                color = Color.FromArgb(255, r, g, b);
                return true;
            }
            if (s.Length == 8)
            {
                byte a = byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte r = byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(s.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
        }
        catch
        {
        }
        return false;
    }
}
