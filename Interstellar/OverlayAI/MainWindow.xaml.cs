using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OverlayAI.Ai;
using OverlayAI.Services;
using static OverlayAI.Native.NativeMethods;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using RadioButton = System.Windows.Controls.RadioButton;

namespace OverlayAI;

public partial class MainWindow : Window
{
    private sealed record HotKey(int Id, uint Vk, string Name, bool OnlyWhenExpanded);
    private enum MainPanel { Assistant, Browser, Vpn, Settings }

    private sealed class BrowserTabState(TabItem item, WebView2 view, TextBlock label)
    {
        public TabItem Item { get; } = item;
        public WebView2 View { get; } = view;
        public TextBlock Label { get; } = label;
    }

    private const int ID_TOGGLE = 1, ID_SHOT = 2, ID_CLEAR = 3, ID_LEFT = 4, ID_UP = 5, ID_RIGHT = 6,
                      ID_DOWN = 7, ID_CLICKTHRU = 8, ID_OPUP = 9, ID_OPDOWN = 10, ID_FOCUS = 11, ID_PRIVACY = 12;
    private const double NudgeStep = 40;

    private static readonly HotKey[] Keys =
    {
        new(ID_TOGGLE, 0x20, "Space", false),
        new(ID_SHOT, 0x53, "S", false),
        new(ID_CLEAR, 0x43, "C", true),
        new(ID_LEFT, 0x25, "Left", true),
        new(ID_UP, 0x26, "Up", true),
        new(ID_RIGHT, 0x27, "Right", true),
        new(ID_DOWN, 0x28, "Down", true),
        new(ID_CLICKTHRU, 0x54, "T", true),
        new(ID_OPUP, 0xDD, "]", true),
        new(ID_OPDOWN, 0xDB, "[", true),
        new(ID_FOCUS, 0x0D, "Enter", true),
        new(ID_PRIVACY, 0x48, "H", false),
    };

    private IntPtr _hwnd;
    private readonly AppConfig _cfg = AppConfig.Load();
    private readonly List<ChatMessage> _history = new();
    private readonly HashSet<int> _registered = new();
    private readonly List<BrowserTabState> _browserTabs = new();
    private readonly TaskCompletionSource _chatReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private ScreenClip? _pendingClip;
    private MainPanel _activePanel = MainPanel.Assistant;
    private MainPanel _panelBeforeSettings = MainPanel.Assistant;
    private bool _clickThrough;
    private bool _windowExpanded = true;
    private bool _captureInProgress;
    private string _hotkeyWarning = "";

    private BrowserTabState? ActiveBrowserTab => BrowserTabs.SelectedItem is TabItem selected
        ? _browserTabs.FirstOrDefault(t => ReferenceEquals(t.Item, selected))
        : null;

    public MainWindow()
    {
        InitializeComponent();
        ShowInTaskbar = !_cfg.HideTaskbarButton;
        Deactivated += (_, _) => SetNoActivate(true);
        PreviewMouseDown += (_, _) => BeginInteractive();
        Loaded += async (_, _) =>
        {
            await InitChatAsync();
            await RefreshVpnProfilesAsync();
        };
        StateChanged += (_, _) =>
        {
            _windowExpanded = WindowState != WindowState.Minimized;
            SetVisibleOnlyHotkeys(_windowExpanded);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        // Normal, inspectable Windows app window: taskbar and Alt+Tab presence are intentional.
        SetExStyle(GetExStyle() | WS_EX_TOPMOST | WS_EX_LAYERED);
        ApplyOpacity();
        ApplyCaptureExclusion();
        RegisterHotkeys();
        RefreshStatus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        foreach (var tab in _browserTabs)
        {
            try { tab.View.CoreWebView2?.Stop(); } catch { }
        }
        foreach (var id in _registered.ToArray()) UnregisterHotKey(_hwnd, id);
        _cfg.Save();
        base.OnClosed(e);
    }

    private long GetExStyle() => GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
    private void SetExStyle(long value) => SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(value));

    private void SetNoActivate(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = GetExStyle();
        SetExStyle(on ? ex | WS_EX_NOACTIVATE : ex & ~WS_EX_NOACTIVATE);
    }

    private void BeginInteractive()
    {
        if (_clickThrough) return;
        SetNoActivate(false);
        Activate();
    }

    private void ApplyOpacity()
    {
        _cfg.Opacity = Math.Clamp(_cfg.Opacity, 0.2, 1.0);
        if (_hwnd != IntPtr.Zero)
            SetLayeredWindowAttributes(_hwnd, 0, (byte)(_cfg.Opacity * 255), LWA_ALPHA);
    }

    // Global Ctrl+Shift shortcuts. Capture is always available, including while minimized.
    private void RegisterHotkeys()
    {
        var failed = new List<string>();
        foreach (var key in Keys)
        {
            if (key.OnlyWhenExpanded && !_windowExpanded) continue;
            if (RegisterHotKey(_hwnd, key.Id, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, key.Vk)) _registered.Add(key.Id);
            else failed.Add(key.Name);
        }
        _hotkeyWarning = failed.Count > 0 ? "hotkeys in use: " + string.Join(",", failed) : "";
    }

    private void SetVisibleOnlyHotkeys(bool expanded)
    {
        foreach (var key in Keys.Where(k => k.OnlyWhenExpanded))
        {
            if (expanded)
            {
                if (!_registered.Contains(key.Id) && RegisterHotKey(_hwnd, key.Id, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, key.Vk))
                    _registered.Add(key.Id);
            }
            else if (_registered.Remove(key.Id)) UnregisterHotKey(_hwnd, key.Id);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            handled = true;
            OnHotkey(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case ID_TOGGLE: ToggleMinimize(); break;
            case ID_SHOT: _ = CaptureScreenClipAsync(); break;
            case ID_CLEAR: ClearConversation(); break;
            case ID_LEFT: Left -= NudgeStep; break;
            case ID_RIGHT: Left += NudgeStep; break;
            case ID_UP: Top -= NudgeStep; break;
            case ID_DOWN: Top += NudgeStep; break;
            case ID_CLICKTHRU: ToggleClickThrough(); break;
            case ID_OPUP: _cfg.Opacity += 0.05; ApplyOpacity(); break;
            case ID_OPDOWN: _cfg.Opacity -= 0.05; ApplyOpacity(); break;
            case ID_FOCUS: FocusOverlay(); break;
            case ID_PRIVACY: _cfg.HideFromCapture = !_cfg.HideFromCapture; ApplyCaptureExclusion(); break;
        }
    }

    // Asks Windows to leave this window out of screen capture. Best effort: it is honored by OBS display/window
    // capture, Discord, Teams, Game Bar and screenshots, but not by a capture card or camera pointed at the monitor.
    private void ApplyCaptureExclusion()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool ok = SetWindowDisplayAffinity(_hwnd, _cfg.HideFromCapture ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        if (!ok) _cfg.HideFromCapture = false;
        RefreshStatus();
    }

    // With the taskbar button hidden, a minimized window would leave a small stub on the desktop,
    // so the overlay is hidden outright instead and brought back with Ctrl+Shift+Space.
    private bool OverlayHidden => _cfg.HideTaskbarButton ? !IsVisible : WindowState == WindowState.Minimized;

    private void HideOverlay()
    {
        if (_cfg.HideTaskbarButton)
        {
            Hide();
            _windowExpanded = false;
            SetVisibleOnlyHotkeys(false);
        }
        else WindowState = WindowState.Minimized;
    }

    private void ToggleMinimize()
    {
        if (OverlayHidden) FocusOverlay();
        else HideOverlay();
    }

    private void FocusOverlay()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        SetNoActivate(false);
        Show();
        _windowExpanded = true;
        SetVisibleOnlyHotkeys(true);
        Activate();
        if (_activePanel == MainPanel.Assistant)
        {
            PromptBox.Focus();
            Keyboard.Focus(PromptBox);
        }
    }

    private void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        var ex = GetExStyle();
        SetExStyle(_clickThrough ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
        RefreshStatus();
    }

    // One-shot screen clipping. The overlay is briefly hidden so it does not cover the captured work.
    private async Task CaptureScreenClipAsync()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            var originalForeground = GetForegroundWindow();
            bool wasInterstellarActive = originalForeground == _hwnd;
            bool restoreOverlay = WindowState != WindowState.Minimized && IsVisible;
            ScreenClip? clip = null;

            try
            {
                if (restoreOverlay) ShowWindow(_hwnd, SW_HIDE);
                await Task.Delay(65);
                clip = await Task.Run(ScreenClipService.CapturePrimaryScreen);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Screen clip failed: " + ex.Message;
                return;
            }
            finally
            {
                if (restoreOverlay) ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            }

            if (clip is null) return;

            try
            {
                Clipboard.SetImage(clip.Preview);
                Clipboard.Flush();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Clipboard unavailable: " + ex.Message;
                return;
            }

            if (wasInterstellarActive)
            {
                _activePanel = MainPanel.Assistant;
                TabAssistant.IsChecked = true;
                UpdatePanels();
                _pendingClip = clip;
                Thumb.Source = clip.Preview;
                ThumbTitle.Text = "SCREEN CLIP ATTACHED";
                ThumbSubtitle.Text = $"{clip.Width} × {clip.Height} · ready to send with your message";
                ThumbBar.Visibility = Visibility.Visible;
                SetNoActivate(false);
                Activate();
                PromptBox.Focus();
                Keyboard.Focus(PromptBox);
                StatusText.Text = "Screen clip attached to the assistant input";
            }
            else
            {
                // The image remains on the Windows clipboard. Send Ctrl+V to the control the user
                // was already typing in; image insertion depends on that application's paste support.
                await Task.Delay(100); // Allow the physical Ctrl+Shift+S chord to be released first.
                bool sameTarget = GetForegroundWindow() == originalForeground;
                bool pasted = sameTarget && PasteClipboardImageToForeground();
                StatusText.Text = pasted
                    ? "Screen clip pasted · also available on clipboard"
                    : "Screen clip copied · paste with Ctrl+V in an image-capable field";
            }
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    // Chat (WebView2-rendered Markdown)
    private async Task InitChatAsync()
    {
        try
        {
            ChatView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0C, 0x15, 0x22);
            await ChatView.EnsureCoreWebView2Async();
            var core = ChatView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, _) => _chatReady.TrySetResult();
            core.NavigateToString(LoadHtml());
        }
        catch (Exception ex)
        {
            StatusText.Text = "WebView2 unavailable: " + ex.Message;
        }
    }

    private static string LoadHtml()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("chat.html")
            ?? throw new InvalidOperationException("The chat interface resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd()
            .Replace("{{ART_MONO}}", LoadArtworkDataUri("interstellar-monochrome.png"), StringComparison.Ordinal)
            .Replace("{{ART_ORBIT}}", LoadArtworkDataUri("interstellar-signal.png"), StringComparison.Ordinal)
            .Replace("{{ART_POSTER}}", LoadArtworkDataUri("interstellar-reminder.png"), StringComparison.Ordinal);
    }

    private static string LoadArtworkDataUri(string fileName)
    {
        var uri = new Uri($"pack://application:,,,/Assets/Branding/{fileName}", UriKind.Absolute);
        var resource = Application.GetResourceStream(uri)
            ?? throw new FileNotFoundException($"The Interstellar artwork '{fileName}' could not be loaded.");
        using var source = resource.Stream;
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray());
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var type = document.RootElement.GetProperty("type").GetString();
            switch (type)
            {
                case "copy": Clipboard.SetDataObject(document.RootElement.GetProperty("text").GetString() ?? "", true); break;
                case "capture": _ = CaptureScreenClipAsync(); break;
                case "focus": FocusOverlay(); break;
            }
        }
        catch { }
    }

    private static string J(string value) => JsonSerializer.Serialize(value);

    private async Task Js(string script)
    {
        await _chatReady.Task;
        if (ChatView.CoreWebView2 is not null)
            await ChatView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void ClearConversation()
    {
        _cts?.Cancel();
        _history.Clear();
        _pendingClip = null;
        Thumb.Source = null;
        ThumbBar.Visibility = Visibility.Collapsed;
        _ = Js("chat.clear()");
    }

    private IReadOnlyList<ChatMessage> BuildContext()
    {
        // Fast mode sends only recent turns and never re-sends more than the newest image.
        int keep = _cfg.FastMode ? 12 : 40;
        var recent = _history.Skip(Math.Max(0, _history.Count - keep)).ToList();
        int lastImage = recent.FindLastIndex(m => m.ImageBase64 is not null);
        for (int i = 0; i < recent.Count; i++)
            if (recent[i].ImageBase64 is not null && i != lastImage)
                recent[i] = recent[i] with { ImageBase64 = null, Text = recent[i].Text + " [older screen clip omitted]" };
        return recent;
    }

    private async Task SendAsync(string text, string? imageBase64)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var cancellationToken = _cts.Token;

        // Only the newest screenshot is retained in the working conversation context.
        if (imageBase64 is not null)
        {
            for (int i = 0; i < _history.Count; i++)
            {
                var old = _history[i];
                if (old.ImageBase64 is not null)
                    _history[i] = old with { ImageBase64 = null, Text = old.Text + " [screen clip replaced by a newer clip]" };
            }
        }

        _history.Add(new ChatMessage("user", text, imageBase64));
        await Js($"chat.addUser({J(text)},{(imageBase64 is not null ? "true" : "false")})");
        await Js("chat.startAssistant()");

        var full = new StringBuilder();
        var pending = new StringBuilder();
        var clock = Stopwatch.StartNew();
        try
        {
            var provider = ProviderFactory.Create(_cfg);
            await foreach (var chunk in provider.StreamAsync(_cfg.SystemPrompt, BuildContext(), cancellationToken))
            {
                full.Append(chunk);
                pending.Append(chunk);
                if (clock.ElapsedMilliseconds >= 25)
                {
                    await Js($"chat.append({J(pending.ToString())})");
                    pending.Clear();
                    clock.Restart();
                }
            }
            if (pending.Length > 0) await Js($"chat.append({J(pending.ToString())})");
            _history.Add(new ChatMessage("assistant", full.ToString()));
            await Js("chat.finish()");
        }
        catch (OperationCanceledException) { /* A newer request or user action cancelled this stream. */ }
        catch (Exception ex)
        {
            var message = ex is HttpRequestException && _cfg.P == "ollama"
                ? ex.Message + "\n\nIs Ollama running, and is the model pulled? (ollama pull " + _cfg.EffectiveModel + ")"
                : ex.Message;
            if (!cancellationToken.IsCancellationRequested) await Js($"chat.error({J(message)})");
        }
    }

    // Composer actions
    private void Capture_Click(object sender, RoutedEventArgs e) => _ = CaptureScreenClipAsync();

    private void ThumbClose_Click(object sender, RoutedEventArgs e)
    {
        _pendingClip = null;
        Thumb.Source = null;
        ThumbBar.Visibility = Visibility.Collapsed;
    }

    private void Prompt_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendFromPrompt();
        }
        else if (e.Key == Key.Escape)
        {
            _cts?.Cancel();
            _ = Js("chat.finish()");
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendFromPrompt();

    private void SendFromPrompt()
    {
        var text = PromptBox.Text.Trim();
        if (text.Length == 0 && _pendingClip is null) return;
        var image = _pendingClip is null ? null : Convert.ToBase64String(_pendingClip.Jpeg);
        if (text.Length == 0 && image is not null) text = _cfg.ScreenshotPrompt;
        PromptBox.Clear();
        _pendingClip = null;
        if (image is not null)
        {
            ThumbTitle.Text = "SCREEN CLIP SENT";
            ThumbSubtitle.Text = "Included with this message";
        }
        _ = SendAsync(text, image);
    }

    // Main navigation and panels
    private void UpdatePanels()
    {
        ChatPanel.Visibility = _activePanel == MainPanel.Assistant ? Visibility.Visible : Visibility.Collapsed;
        BrowserPanel.Visibility = _activePanel == MainPanel.Browser ? Visibility.Visible : Visibility.Collapsed;
        VpnPanel.Visibility = _activePanel == MainPanel.Vpn ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = _activePanel == MainPanel.Settings ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton tab || !Enum.TryParse<MainPanel>(tab.Tag?.ToString(), true, out var panel)) return;
        _activePanel = panel;
        UpdatePanels();
        if (panel == MainPanel.Browser) _ = EnsureBrowserAsync();
        else if (panel == MainPanel.Vpn) _ = RefreshVpnProfilesAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_activePanel != MainPanel.Settings) _panelBeforeSettings = _activePanel;
        _activePanel = _activePanel == MainPanel.Settings ? _panelBeforeSettings : MainPanel.Settings;
        if (_activePanel == MainPanel.Settings)
        {
            foreach (var rb in new[] { RbOpenAi, RbAnthropic, RbGemini, RbOllama }) rb.IsChecked = (string)rb.Tag == _cfg.P;
            BaseUrlBox.Text = _cfg.EffectiveBaseUrl;
            ModelBox.Text = _cfg.EffectiveModel;
            KeyBox.Password = _cfg.ApiKey;
            FastModeToggle.IsChecked = _cfg.FastMode;
        }
        UpdatePanels();
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => HideOverlay();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not Button) DragMove();
    }

    private void RefreshStatus()
    {
        var status = $"{_cfg.P} · {_cfg.EffectiveModel}";
        if (_cfg.FastMode) status += " · fast";
        if (_clickThrough) status += " · click-through";
        status += _cfg.HideFromCapture ? " · hidden from capture" : " · visible to capture";
        if (_hotkeyWarning.Length > 0) status += " · " + _hotkeyWarning;
        StatusText.Text = status;
        StatusText.ToolTip = status;
    }

    // Browser tabs
    private async Task EnsureBrowserAsync()
    {
        if (_browserTabs.Count == 0) await AddBrowserTabAsync("https://duckduckgo.com/");
    }

    private async void NewBrowserTab_Click(object sender, RoutedEventArgs e) => await AddBrowserTabAsync("https://duckduckgo.com/");

    private async Task AddBrowserTabAsync(string startUrl)
    {
        var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0C, 0x15, 0x22) };
        var title = new TextBlock { Text = "New tab", Foreground = new SolidColorBrush(Color.FromRgb(205, 225, 240)), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "×", Width = 20, Height = 20, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(0), FontSize = 13, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(Color.FromRgb(160, 185, 205)), Cursor = Cursors.Hand };
        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title);
        header.Children.Add(close);
        var item = new TabItem { Header = header, Content = view, Padding = new Thickness(9, 5, 7, 5) };
        var state = new BrowserTabState(item, view, title);
        _browserTabs.Add(state);
        close.Click += (_, _) => CloseBrowserTab(state);
        BrowserTabs.Items.Add(item);
        BrowserTabs.SelectedItem = item;
        try
        {
            await view.EnsureCoreWebView2Async();
            var core = view.CoreWebView2;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.SourceChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(ActiveBrowserTab, state)) UrlBox.Text = core.Source;
            });
            core.DocumentTitleChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                state.Label.Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "New tab" : core.DocumentTitle;
            });
            core.Navigate(startUrl);
            UpdateActiveBrowserTab();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Browser tab failed: " + ex.Message;
        }
    }

    private void CloseBrowserTab(BrowserTabState state)
    {
        try { state.View.CoreWebView2?.Stop(); } catch { }
        BrowserTabs.Items.Remove(state.Item);
        _browserTabs.Remove(state);
        if (_browserTabs.Count == 0) _ = AddBrowserTabAsync("https://duckduckgo.com/");
        else UpdateActiveBrowserTab();
    }

    private void BrowserTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActiveBrowserTab();

    private void UpdateActiveBrowserTab()
    {
        var tab = ActiveBrowserTab;
        UrlBox.Text = tab?.View.Source?.ToString() ?? "";
    }

    private WebView2? ActiveBrowser => ActiveBrowserTab?.View;
    private void Back_Click(object sender, RoutedEventArgs e) { if (ActiveBrowser?.CanGoBack == true) ActiveBrowser.GoBack(); }
    private void Fwd_Click(object sender, RoutedEventArgs e) { if (ActiveBrowser?.CanGoForward == true) ActiveBrowser.GoForward(); }
    private void Go_Click(object sender, RoutedEventArgs e) => Navigate();
    private void Url_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Navigate(); }

    private void Navigate()
    {
        var text = UrlBox.Text.Trim();
        var browser = ActiveBrowser;
        if (text.Length == 0 || browser?.CoreWebView2 == null) return;
        string url = Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https") ? text
                   : !text.Contains(' ') && text.Contains('.') ? "https://" + text
                   : "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);
        browser.CoreWebView2.Navigate(url);
    }

    // Windows built-in VPN profiles
    private async Task RefreshVpnProfilesAsync()
    {
        try
        {
            var previous = VpnProfileBox.SelectedItem as string;
            var profiles = WindowsVpnService.GetProfiles();
            VpnProfileBox.ItemsSource = profiles;
            if (previous is not null && profiles.Contains(previous, StringComparer.OrdinalIgnoreCase)) VpnProfileBox.SelectedItem = previous;
            else if (profiles.Count > 0) VpnProfileBox.SelectedIndex = 0;

            var summary = await WindowsVpnService.GetConnectionSummaryAsync();
            var selected = VpnProfileBox.SelectedItem as string;
            if (profiles.Count == 0)
                VpnStatusText.Text = "No Windows VPN profiles found. Add a VPN in Windows Settings, then select Refresh.";
            else if (selected is not null && summary.Contains(selected, StringComparison.OrdinalIgnoreCase) && !summary.Contains("No connections", StringComparison.OrdinalIgnoreCase))
                VpnStatusText.Text = $"{selected} appears in the active Windows VPN connections.\n\n{summary}";
            else
                VpnStatusText.Text = "No selected profile appears connected.\n\n" + summary;
        }
        catch (Exception ex) { VpnStatusText.Text = "Could not read Windows VPN profiles: " + ex.Message; }
    }

    private async void VpnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshVpnProfilesAsync();

    private async void VpnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (VpnProfileBox.SelectedItem is not string profile)
        {
            VpnStatusText.Text = "Choose a configured Windows VPN profile first.";
            return;
        }
        VpnStatusText.Text = $"Connecting to {profile}…";
        try
        {
            var output = await WindowsVpnService.ConnectAsync(profile);
            VpnStatusText.Text = output;
            await RefreshVpnProfilesAsync();
            if (!output.Contains("success", StringComparison.OrdinalIgnoreCase)) VpnStatusText.Text = output;
        }
        catch (Exception ex) { VpnStatusText.Text = "VPN connection failed: " + ex.Message; }
    }

    private async void VpnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (VpnProfileBox.SelectedItem is not string profile)
        {
            VpnStatusText.Text = "Choose a configured Windows VPN profile first.";
            return;
        }
        VpnStatusText.Text = $"Disconnecting {profile}…";
        try
        {
            var output = await WindowsVpnService.DisconnectAsync(profile);
            VpnStatusText.Text = output;
            await RefreshVpnProfilesAsync();
            if (!output.Contains("success", StringComparison.OrdinalIgnoreCase)) VpnStatusText.Text = output;
        }
        catch (Exception ex) { VpnStatusText.Text = "VPN disconnect failed: " + ex.Message; }
    }

    private void VpnOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network-vpn") { UseShellExecute = true }); }
        catch (Exception ex) { VpnStatusText.Text = "Could not open Windows VPN settings: " + ex.Message; }
    }

    // AI configuration
    private void Provider_Click(object sender, RoutedEventArgs e)
    {
        var provider = (string)((RadioButton)sender).Tag;
        BaseUrlBox.Text = AppConfig.Defaults[provider].Url;
        ModelBox.Text = AppConfig.Defaults[provider].Model;
        KeyBox.Password = "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var selected = new[] { RbOpenAi, RbAnthropic, RbGemini, RbOllama }.FirstOrDefault(rb => rb.IsChecked == true);
        _cfg.Provider = selected is not null ? (string)selected.Tag : _cfg.Provider;
        _cfg.BaseUrl = BaseUrlBox.Text.Trim();
        _cfg.Model = ModelBox.Text.Trim();
        _cfg.ApiKey = KeyBox.Password.Trim();
        _cfg.FastMode = FastModeToggle.IsChecked == true;
        _cfg.Save();
        _activePanel = MainPanel.Assistant;
        TabAssistant.IsChecked = true;
        UpdatePanels();
        RefreshStatus();
    }
}
