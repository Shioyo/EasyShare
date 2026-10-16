using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace EasyShare;

public partial class MainWindow : Window
{
    private readonly string root = AppContext.BaseDirectory;
    private readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyShare");
    private readonly List<string> logLines = [];
    private MirrorSession? session;
    private string? hdc;
    private string? adb;
    private readonly System.Windows.Threading.DispatcherTimer discoveryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool busy;
    private bool refreshing;
    private bool manualRefresh;
    private bool applyingDevices;
    private string? discoveryErrorStatus;
    private bool closing;
    private bool allowClose;
    private bool fullscreen;
    private WindowState previousWindowState;
    private bool hasFrame;
    private TaskCompletionSource<bool> firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool selfTest = Environment.GetCommandLineArgs().Contains("--self-test");
    private readonly bool usbTest = Environment.GetCommandLineArgs().Contains("--usb-test");
    private readonly bool discoveryTest = Environment.GetCommandLineArgs().Contains("--discovery-test");
    private readonly bool refreshTest = Environment.GetCommandLineArgs().Contains("--refresh-test");
    private readonly bool uiTest = Environment.GetCommandLineArgs().Contains("--ui-test");
    private TaskCompletionSource<JsonElement>? testResult;

    public MainWindow()
    {
        if (selfTest || discoveryTest || refreshTest || uiTest)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }
        InitializeComponent();
        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.5.0");
        discoveryTimer.Tick += async (_, _) => await RefreshAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            ApplySettings(LoadSettings());
            var config = Path.Combine(dataDirectory, "hdc-path.txt");
            hdc = HdcClient.Find(File.Exists(config) ? File.ReadAllText(config).Trim() : null, root);
            adb = HdcClient.FindAdb(root);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDirectory, "WebView2"));
            await Player.EnsureCoreWebView2Async(environment);
            Player.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Player.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Player.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            Player.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            Player.CoreWebView2.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            Player.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (args.Uri != "about:blank" && (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Host != "127.0.0.1"))
                {
                    args.Cancel = true;
                }
            };
            Player.CoreWebView2.WebMessageReceived += Player_MessageReceived;
            Player.CoreWebView2.ProcessFailed += async (_, args) =>
            {
                Log("播放器进程异常：" + args.ProcessFailedKind);
                await StopAsync();
                SetStatus("播放器意外退出，请重新打开 EasyShare。");
            };
            if (selfTest)
            {
                await RunSelfTestAsync();
            }
            else
            {
                await RefreshAsync();
                if (discoveryTest)
                {
                    var settings = ReadSettings();
                    var keyboardMode = (DevicePicker.SelectedItem as DeviceInfo)?.Protocol switch
                    {
                        "ADB" => settings.UseTabletIme ? "uhid" : "sdk",
                        "HDC" => "disabled",
                        _ => null
                    };
                    await CaptureWindowAsync();
                    await File.WriteAllTextAsync(Path.Combine(root, "selftest", "discovery-result.json"), JsonSerializer.Serialize(new
                    {
                        devices = DevicePicker.Items.Cast<DeviceInfo>().ToArray(),
                        startEnabled = StartButton.IsEnabled, status = StatusText.Text,
                        title = PlaceholderTitle.Text, detail = PlaceholderDetail.Text,
                        settings, audioToggleEnabled = AudioEnabledBox.IsEnabled,
                        audioBitrateEnabled = AudioBitratePicker.IsEnabled, videoBitrateEnabled = BitratePicker.IsEnabled,
                        useTabletIme = settings.UseTabletIme, keyboardMode, tabletImeEnabled = TabletImeBox.IsEnabled
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    Close();
                }
                else if (uiTest)
                {
                    await RunUiTestAsync();
                }
                else if (refreshTest)
                {
                    await RunRefreshTestAsync();
                }
                else if (usbTest)
                {
                    await RunUsbTestAsync();
                }
                else
                {
                    discoveryTimer.Start();
                }
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
            if (selfTest || usbTest || discoveryTest || refreshTest || uiTest)
            {
                WriteTestFailure(ex);
                Close();
            }
        }
    }

    private StreamSettings LoadSettings()
    {
        var settings = new StreamSettings();
        try
        {
            var path = Path.Combine(dataDirectory, "stream-settings.json");
            if (File.Exists(path))
            {
                settings = JsonSerializer.Deserialize<StreamSettings>(File.ReadAllText(path))
                    ?? throw new JsonException("设置文件为空。");
            }
            else
            {
                var legacy = Path.Combine(dataDirectory, "quality.txt");
                if (File.Exists(legacy) && int.TryParse(File.ReadAllText(legacy), out var quality))
                {
                    settings = settings with { QualityIndex = quality };
                }
            }
            settings.Validate();
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentOutOfRangeException)
        {
            Log("已恢复默认投屏设置：" + ex.Message);
            settings = new StreamSettings();
        }
        foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
        {
            if (argument == "--quality=clear")
            {
                settings = settings with { QualityIndex = 1 };
            }
            else if (argument == "--no-audio" || argument == "--audio")
            {
                settings = settings with { AudioEnabled = argument == "--audio" };
            }
            else if (argument.StartsWith("--video-bitrate=", StringComparison.Ordinal))
            {
                settings = settings with { VideoBitRateMbps = int.Parse(argument[16..]) };
            }
            else if (argument.StartsWith("--audio-bitrate=", StringComparison.Ordinal))
            {
                settings = settings with { AudioBitRateKbps = int.Parse(argument[16..]) };
            }
            else if (argument.StartsWith("--keyboard=", StringComparison.Ordinal))
            {
                settings = settings with
                {
                    UseTabletIme = argument[11..] switch
                    {
                        "uhid" => true,
                        "sdk" => false,
                        _ => throw new ArgumentException("键盘模式只支持 --keyboard=uhid 或 --keyboard=sdk。")
                    }
                };
            }
        }
        settings.Validate();
        return settings;
    }

    private void ApplySettings(StreamSettings settings)
    {
        QualityPicker.SelectedIndex = settings.QualityIndex;
        BitratePicker.SelectedValue = settings.VideoBitRateMbps.ToString();
        AudioEnabledBox.IsChecked = settings.AudioEnabled;
        AudioBitratePicker.SelectedValue = settings.AudioBitRateKbps.ToString();
        TabletImeBox.IsChecked = settings.UseTabletIme;
    }

    private StreamSettings ReadSettings()
    {
        var settings = new StreamSettings
        {
            QualityIndex = QualityPicker.SelectedIndex,
            VideoBitRateMbps = int.Parse((string)BitratePicker.SelectedValue),
            AudioEnabled = AudioEnabledBox.IsChecked == true,
            AudioBitRateKbps = int.Parse((string)AudioBitratePicker.SelectedValue),
            UseTabletIme = TabletImeBox.IsChecked == true
        };
        settings.Validate();
        return settings;
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateButtons();
        }
    }

    private void Player_MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var uri) || uri.Host != "127.0.0.1")
        {
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var message = document.RootElement;
            switch (message.GetProperty("type").GetString())
            {
                case "frame":
                    hasFrame = true;
                    firstFrame.TrySetResult(true);
                    ShotButton.IsEnabled = true;
                    SetStatus("正在投屏 · 画面通过 USB 在本机传输");
                    break;
                case "stats":
                    StreamInfo.Text = $"{message.GetProperty("width").GetInt32()} × {message.GetProperty("height").GetInt32()}  ·  {message.GetProperty("fps").GetInt32()} FPS  ·  声音在平板播放";
                    break;
                case "status":
                    SetStatus(message.GetProperty("text").GetString() ?? "");
                    break;
                case "error":
                    SetStatus(message.GetProperty("text").GetString() ?? "投屏失败");
                    Log(StatusText.Text);
                    ShotButton.IsEnabled = false;
                    break;
                case "fullscreen":
                    ToggleFullscreen();
                    break;
                case "escape":
                    if (fullscreen)
                    {
                        ToggleFullscreen();
                    }
                    break;
                case "selftest":
                    testResult?.TrySetResult(message.Clone());
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log("播放器消息无效：" + ex.Message);
        }
    }

    private async Task RefreshAsync(bool userInitiated = false)
    {
        if (refreshing || busy || session != null || closing)
        {
            return;
        }
        refreshing = true;
        manualRefresh = userInitiated;
        if (userInitiated)
        {
            RefreshButton.IsEnabled = false;
            SetStatus("正在查找通过 USB 连接的平板…");
        }
        try
        {
            if (hdc == null && adb == null)
            {
                SetStatus("缺少连接工具。请重新解压完整发布包。");
                PlaceholderTitle.Text = "缺少设备连接组件";
                PlaceholderDetail.Text = "请重新解压完整发布包，保留 tools 文件夹中的 ADB 与 HDC 工具。";
                return;
            }
            var currentHdc = hdc;
            var currentAdb = adb;
            var results = await Task.WhenAll(
                DiscoverAsync(currentHdc, "HDC", ["list", "targets", "-v"]),
                DiscoverAsync(currentAdb, "ADB", ["devices", "-l"]));
            if (closing || busy || session != null || hdc != currentHdc || adb != currentAdb)
            {
                return;
            }
            // Keep the last known list on a tool error; a successful empty result still removes disconnected devices.
            var previous = DevicePicker.Items.Cast<DeviceInfo>().ToList();
            var devices = (results[0].Devices ?? previous.Where(d => d.Protocol == "HDC").ToList())
                .Concat(results[1].Devices ?? previous.Where(d => d.Protocol == "ADB").ToList())
                .OrderBy(d => d.Protocol, StringComparer.Ordinal)
                .ThenBy(d => d.Serial, StringComparer.Ordinal)
                .ToList();
            var firstRefresh = DevicePicker.ItemsSource == null;
            var changed = firstRefresh || !previous.SequenceEqual(devices);
            var error = string.Join("；", results.Where(result => result.Error != null).Select(result => result.Error));
            if (changed)
            {
                // Read the selection after awaiting discovery so an intervening user selection is preserved.
                var selected = DevicePicker.SelectedItem as DeviceInfo;
                applyingDevices = true;
                try
                {
                    DevicePicker.ItemsSource = devices;
                    DevicePicker.SelectedItem = devices.FirstOrDefault(d => d.Serial == selected?.Serial && d.Protocol == selected.Protocol)
                        ?? devices.FirstOrDefault(d => d.IsReady) ?? devices.FirstOrDefault();
                }
                finally
                {
                    applyingDevices = false;
                }
                ShowDeviceStatus();
                UpdateButtons();
            }
            else if (userInitiated || (error.Length == 0 && StatusText.Text == discoveryErrorStatus))
            {
                ShowDeviceStatus();
            }
            if ((userInitiated || firstRefresh) && error.Length > 0)
            {
                discoveryErrorStatus = "设备检测失败，已保留上次列表：" + error;
                SetStatus(discoveryErrorStatus);
            }
            else if (error.Length == 0)
            {
                discoveryErrorStatus = null;
            }
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            if (!closing && !busy && session == null && userInitiated)
            {
                discoveryErrorStatus = "设备检测失败：" + ex.Message;
                SetStatus(discoveryErrorStatus);
            }
        }
        finally
        {
            refreshing = false;
            manualRefresh = false;
            if (userInitiated && !closing)
            {
                RefreshButton.IsEnabled = !busy && session == null;
            }
        }
    }

    private async Task<(List<DeviceInfo>? Devices, string? Error)> DiscoverAsync(string? executable, string protocol, string[] arguments)
    {
        if (executable == null)
        {
            Log(protocol + " 工具缺失。");
            return ([], null);
        }
        try
        {
            var output = await HdcClient.RunAsync(executable, arguments);
            Log($"{protocol}: {executable}\n{output.Trim()}");
            return (protocol == "ADB" ? HdcClient.ParseAdbDevices(output) : HdcClient.ParseDevices(output), null);
        }
        catch (Exception ex)
        {
            Log(protocol + " 枚举失败：" + ex.Message);
            return (null, protocol + "：" + ex.Message);
        }
    }

    private void ShowDeviceStatus()
    {
        var device = DevicePicker.SelectedItem as DeviceInfo;
        ConnectionBadge.Text = device == null ? "等待连接" : device.IsReady ? "USB 已连接"
            : device.State == DeviceState.Unauthorized ? "待授权" : "已离线";
        if (device == null)
        {
            PlaceholderTitle.Text = "把设备画面，带到大屏幕";
            PlaceholderDetail.Text = "检查数据线和 USB 调试；每 5 秒自动检测，也可点击刷新。";
            SetStatus("未发现 USB 调试设备。请检查连接；工具错误可在日志中查看。");
        }
        else if (!device.IsReady)
        {
            PlaceholderTitle.Text = device.State == DeviceState.Unauthorized ? "已找到平板 · 等待授权" : "平板暂时离线";
            PlaceholderDetail.Text = device.State == DeviceState.Unauthorized
                ? "请解锁平板，在「允许 USB 调试」弹窗中点允许。授权后会自动刷新。"
                : "请重新插拔 USB 线，保持平板解锁，检查调试授权。";
            SetStatus(device.DisplayName + " · " + PlaceholderDetail.Text);
        }
        else
        {
            PlaceholderTitle.Text = "设备已连接";
            PlaceholderDetail.Text = device.Protocol == "ADB"
                ? "点击「开始投屏」打开独立画面窗口，可用鼠标左键点击、长按和拖动操作平板；右键返回，中键回到主屏幕，也支持电脑键盘输入。"
                : "点击左侧「开始投屏」，平板画面将在这里显示。当前 HDC 连接暂不支持鼠标操作平板。";
            SetStatus($"已找到 {device.Protocol} USB 设备，可以开始投屏。");
        }
    }

    private void Device_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || applyingDevices || busy || session != null)
        {
            return;
        }
        ShowDeviceStatus();
        UpdateButtons();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(true);

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session != null || DevicePicker.SelectedItem is not DeviceInfo { IsReady: true } device)
        {
            return;
        }
        busy = true;
        hasFrame = false;
        UpdateButtons();
        SetStatus("正在启动投屏，请保持平板解锁…");
        try
        {
            var settings = ReadSettings();
            File.WriteAllText(Path.Combine(dataDirectory, "stream-settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            await StartSessionAsync(device, settings, JpegMode.IsChecked == true);
        }
        catch (Exception ex)
        {
            await StopAsync();
            ShowError(ex);
        }
        finally
        {
            busy = false;
            UpdateButtons();
        }
    }

    private async Task StartSessionAsync(DeviceInfo device, StreamSettings settings, bool jpeg)
    {
        firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = device.Protocol == "ADB"
            ? new MirrorSession(root, adb ?? throw new IOException("缺少 ADB 工具。"), device, settings.Scale, settings.FrameRate,
                settings.EffectiveVideoBitRateMbps, settings.AudioEnabled, settings.AudioBitRateKbps, settings.UseTabletIme, Log)
            : new MirrorSession(root, hdc ?? "hdc", device.Serial, settings.Scale, settings.FrameRate,
                settings.EffectiveVideoBitRateMbps, jpeg, Log, selfTest);
        session = current;
        Action updateStream = () => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(session, current) && !closing)
            {
                UpdateExternalStreamInfo(current);
            }
        });
        current.TextureSizeChanged += updateStream;
        current.AudioStateChanged += updateStream;
        current.Exited += () => Dispatcher.BeginInvoke(async () =>
        {
            if (ReferenceEquals(session, current) && !closing)
            {
                await StopAsync();
                SetStatus(current.KeyboardError ?? "投屏已断开。检查平板和 USB 连接后可重新开始；详情见日志。");
            }
        });
        var url = await current.WaitForReadyAsync();
        if (current.AudioEnabled)
        {
            await current.WaitForAudioAsync();
        }
        if (ReferenceEquals(session, current) && !closing)
        {
            if (current.IsExternal)
            {
                ConnectionBadge.Text = "正在投屏";
                PlaceholderTitle.Text = "大屏已就绪，开始操作吧";
                var keyboardHint = current.UseTabletIme
                    ? "电脑输入法保持英文，打拼音、空格选词。若只出现英文，在投屏输入框内按 Ctrl + Shift 将小艺切到中文，再试一次。"
                    : "当前为普通键盘输入；需要平板输入法选词时，结束投屏后开启左侧中文输入选项。";
                PlaceholderDetail.Text = "在「EasyShare · 平板画面」窗口内：左键点击为触摸，按住不动为长按，按住拖动为滑动；右键返回，中键回到主屏幕。"
                    + keyboardHint + "Alt + F 全屏；退出全屏并还原窗口后，左 Alt + G 按 1:1 像素显示。关闭画面窗口或点击「结束投屏」停止。";
                UpdateExternalStreamInfo(current);
                SetStatus("已收到平板视频画面，正在独立窗口播放。");
                return;
            }
            Player.Visibility = Visibility.Visible;
            ConnectionBadge.Text = "正在投屏";
            Placeholder.Visibility = Visibility.Collapsed;
            Player.CoreWebView2.Navigate(url);
        }
    }

    private void UpdateExternalStreamInfo(MirrorSession current)
    {
        var audio = current.AudioEnabled
            ? current.AudioReady ? $"电脑声音 · Opus 目标 {current.AudioBitRateKbps} kbps" : "声音未就绪，详见日志"
            : "声音在平板播放";
        StreamInfo.Text = $"解码尺寸 {current.TextureSize}  ·  视频目标 {current.VideoBitRateMbps} Mbps  ·  {audio}";
    }

    private async Task StopAsync()
    {
        var current = session;
        session = null;
        Player.CoreWebView2?.Navigate("about:blank");
        Player.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        ShotButton.IsEnabled = false;
        hasFrame = false;
        if (current != null)
        {
            await current.DisposeAsync();
        }
        StreamInfo.Text = "Android / HarmonyOS · USB 有线连接";
        ShowDeviceStatus();
        UpdateButtons();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        busy = true;
        UpdateButtons();
        try
        {
            await StopAsync();
            SetStatus("投屏已结束。");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            busy = false;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        RefreshButton.IsEnabled = !busy && !manualRefresh && session == null;
        StartButton.IsEnabled = !busy && session == null && DevicePicker.SelectedItem is DeviceInfo { IsReady: true } && Player.CoreWebView2 != null;
        StopButton.IsEnabled = !busy && session != null;
        DevicePicker.IsEnabled = !busy && session == null;
        QualityPicker.IsEnabled = !busy && session == null;
        JpegMode.IsEnabled = !busy && session == null && (DevicePicker.SelectedItem as DeviceInfo)?.Protocol != "ADB";
        FullscreenButton.IsEnabled = session?.IsExternal != true;
        var editable = !busy && session == null;
        var isHdc = (DevicePicker.SelectedItem as DeviceInfo)?.Protocol == "HDC";
        var jpeg = isHdc && JpegMode.IsChecked == true;
        BitratePicker.IsEnabled = editable && !jpeg;
        BitrateHint.Text = jpeg ? "兼容模式使用 JPEG，视频码率设置不适用。"
            : "自动：清晰 32，其余 12 Mbps。更高码率会增加带宽和编码负担。";
        AudioEnabledBox.IsEnabled = editable && !isHdc;
        AudioBitratePicker.IsEnabled = editable && !isHdc && AudioEnabledBox.IsChecked == true;
        AudioHint.Text = isHdc ? "当前 HDC 连接方式暂不支持声音传输。"
            : AudioEnabledBox.IsChecked == true ? "开启后，平板声音由电脑播放。" : "关闭后，声音在平板播放。";
        TabletImeBox.IsEnabled = editable && !isHdc;
        TabletImeHint.Text = isHdc ? "当前 HDC 连接方式暂不支持电脑键盘输入。"
            : TabletImeBox.IsChecked == true
                ? "电脑输入法保持英文，打拼音、空格选词。若只出现英文，在投屏输入框内按 Ctrl + Shift 将小艺切到中文。更改勾选后重新投屏。"
                : "当前使用普通键盘模式；输入中文请开启此选项。更改后重新投屏。";
    }

    private async void ChooseHdc_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session != null)
        {
            SetStatus("请先结束投屏，再更换连接工具。");
            return;
        }
        var dialog = new OpenFileDialog { Title = "选择鸿蒙 HDC 工具", Filter = "HDC 工具 (hdc.exe)|hdc.exe" };
        if (dialog.ShowDialog() == true)
        {
            hdc = dialog.FileName;
            try
            {
                File.WriteAllText(Path.Combine(dataDirectory, "hdc-path.txt"), hdc);
                await RefreshAsync(true);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (!hasFrame)
        {
            return;
        }
        var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = $"EasyShare-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                var json = await Player.ExecuteScriptAsync("window.snapshot()");
                var dataUrl = JsonSerializer.Deserialize<string>(json) ?? throw new IOException("当前没有可保存的画面。");
                await File.WriteAllBytesAsync(dialog.FileName, Convert.FromBase64String(dataUrl.Split(',')[1]));
                SetStatus("截图已保存：" + dialog.FileName);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }
    }

    private void ToggleFullscreen()
    {
        if (session?.IsExternal == true)
        {
            SetStatus("请在「EasyShare · 平板画面」窗口按 Alt + F 全屏。");
            return;
        }
        fullscreen = !fullscreen;
        if (fullscreen)
        {
            previousWindowState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = previousWindowState;
            Sidebar.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(344);
        }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Escape && fullscreen))
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void Log(string line)
    {
        lock (logLines)
        {
            logLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (logLines.Count > 500)
            {
                logLines.RemoveRange(0, 100);
            }
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void ShowError(Exception exception)
    {
        Log(exception.ToString());
        SetStatus(exception is WebView2RuntimeNotFoundException ? "缺少 Microsoft Edge WebView2 运行时。安装后重新打开 EasyShare。" : exception.Message);
    }

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        string text;
        lock (logLines)
        {
            text = string.Join(Environment.NewLine, logLines);
        }
        var box = new System.Windows.Controls.TextBox { Text = string.IsNullOrEmpty(text) ? "暂无日志。" : text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Margin = new Thickness(15), FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        new Window { Title = "EasyShare 诊断日志", Width = 800, Height = 500, Owner = this, Content = box }.Show();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
        {
            return;
        }
        e.Cancel = true;
        if (closing)
        {
            return;
        }
        closing = true;
        discoveryTimer.Stop();
        try
        {
            await StopAsync();
            Player.Dispose();
        }
        finally
        {
            allowClose = true;
            // StopAsync may finish synchronously when idle; wait until this Closing event has returned.
            _ = Dispatcher.BeginInvoke(() => Close());
        }
    }

    private async Task CaptureWindowAsync(string filename = "window.png")
    {
        Directory.CreateDirectory(Path.Combine(root, "selftest"));
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        UpdateLayout();
        await Task.Delay(300);
        var client = (FrameworkElement)Content;
        var preview = new System.Windows.Media.Imaging.RenderTargetBitmap((int)client.ActualWidth, (int)client.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        preview.Render(client);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
        using (var file = File.Create(Path.Combine(root, "selftest", filename)))
        {
            png.Save(file);
        }
    }

    private async Task RunSelfTestAsync()
    {
        await CaptureWindowAsync();
        testResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartSessionAsync(new DeviceInfo("SELFTEST", "USB"), new StreamSettings { AudioEnabled = false }, false);
        var result = await testResult.Task.WaitAsync(TimeSpan.FromSeconds(40));
        await File.WriteAllTextAsync(Path.Combine(root, "selftest", "result.json"), result.GetRawText());
        using (var file = File.Create(Path.Combine(root, "selftest", "player.png")))
        {
            await Player.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
        }
        await StopAsync();
        Close();
    }

    private async Task RunUsbTestAsync()
    {
        var device = DevicePicker.SelectedItem as DeviceInfo;
        if (device?.IsReady != true)
        {
            throw new IOException("USB 实测需要已授权的设备。当前：" + (device?.DisplayName ?? "无设备"));
        }
        busy = true;
        UpdateButtons();
        var settings = ReadSettings();
        await StartSessionAsync(device, settings, false);
        if (device.Protocol == "HDC")
        {
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        await Task.Delay(8000);
        if (session == null)
        {
            throw new IOException("USB 实测过程中投屏服务退出。");
        }
        var decodedTextureSize = session.TextureSize;
        var targetVideoBitRateMbps = session.VideoBitRateMbps;
        var audioEnabled = session.AudioEnabled;
        var audioOutputReady = session.AudioReady;
        var targetAudioBitRateKbps = session.AudioBitRateKbps;
        var useTabletIme = session.UseTabletIme;
        var keyboardMode = session.KeyboardMode;
        var keyboardError = session.KeyboardError;
        await StopAsync();
        var directory = Path.Combine(root, "selftest");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "usb-result.json"), JsonSerializer.Serialize(new
        {
            device.Protocol, device.Name, videoReady = true, decodedTextureSize, targetVideoBitRateMbps,
            settings, audioEnabled, audioOutputReady, targetAudioBitRateKbps, useTabletIme, keyboardMode, keyboardError, durationAfterReadySeconds = 8,
            stopped = session == null, logs = logLines.ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Close();
    }

    private void WriteTestFailure(Exception exception)
    {
        Directory.CreateDirectory(Path.Combine(root, "selftest"));
        lock (logLines)
        {
            File.WriteAllText(Path.Combine(root, "selftest", "failure.txt"), exception + Environment.NewLine + string.Join(Environment.NewLine, logLines));
        }
    }
}

