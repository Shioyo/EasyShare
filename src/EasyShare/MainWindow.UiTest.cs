using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace EasyShare;

public partial class MainWindow
{
    private async Task RunUiTestAsync()
    {
        var originalSettings = ReadSettings();
        var checks = new List<object>();
        var screenshots = new List<string>();
        var supportsInputAndAudio = (DevicePicker.SelectedItem as DeviceInfo)?.Protocol != "HDC";
        discoveryTimer.Stop();

        void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("界面检查失败：" + message);
            }
        }

        async Task SelectTabAsync(int index)
        {
            SettingsTabs.SelectedIndex = index;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
        }

        object CheckButtonBounds(FrameworkElement button, FrameworkElement client)
        {
            var origin = button.TranslatePoint(new Point(0, 0), client);
            var bounds = new Rect(origin, button.RenderSize);
            Require(button.IsVisible && bounds.Width > 0 && bounds.Height > 0,
                button.Name + " 未显示或没有有效尺寸。");
            Require(bounds.Left >= -0.5 && bounds.Top >= -0.5
                && bounds.Right <= client.ActualWidth + 0.5 && bounds.Bottom <= client.ActualHeight + 0.5,
                button.Name + " 超出窗口内容边界。");
            return new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height };
        }

        Require(SettingsTabs.Items.Count == 3, "应有画面、声音、键盘三个设置页。");
        try
        {
            WindowState = WindowState.Normal;
            foreach (var size in new[] { (Name: "default", Width: 1280d, Height: 900d), (Name: "minimum", Width: 1060d, Height: 700d) })
            {
                Width = size.Width;
                Height = size.Height;
                for (var tab = 0; tab < 3; tab++)
                {
                    await SelectTabAsync(tab);
                    var filename = $"ui-{size.Name}-{tab}.png";
                    await CaptureWindowAsync(filename);
                    screenshots.Add(filename);
                    var client = (FrameworkElement)Content;
                    var startBounds = CheckButtonBounds(StartButton, client);
                    var stopBounds = CheckButtonBounds(StopButton, client);
                    Require(ReadSettings() == originalSettings, "切换设置页或窗口尺寸后设置值改变。");
                    checks.Add(new
                    {
                        name = "layout_and_tab_persistence", size = size.Name, tab,
                        windowWidth = ActualWidth, windowHeight = ActualHeight,
                        clientWidth = client.ActualWidth, clientHeight = client.ActualHeight,
                        startBounds, stopBounds, settingsPreserved = true
                    });
                }
            }

            await SelectTabAsync(1);
            Require(AudioEnabledBox.IsVisible && AudioBitratePicker.IsVisible, "声音设置页未加载。");
            Require(AudioEnabledBox.IsEnabled == supportsInputAndAudio, "声音开关可用状态与当前连接方式不符。");
            AudioBitratePicker.SelectedValue = "256";
            AudioEnabledBox.IsChecked = false;
            Require(!AudioBitratePicker.IsEnabled, "关闭声音后音频码率仍可编辑。");
            Require(ReadSettings() == (originalSettings with { AudioEnabled = false, AudioBitRateKbps = 256 }),
                "关闭声音时没有保留音频码率或覆盖了其他设置。");
            AudioEnabledBox.IsChecked = true;
            Require(AudioBitratePicker.IsEnabled == supportsInputAndAudio, "恢复声音后音频码率可用状态不正确。");
            Require(ReadSettings() == (originalSettings with { AudioEnabled = true, AudioBitRateKbps = 256 }),
                "恢复声音时丢失了音频码率或覆盖了其他设置。");
            checks.Add(new { name = "audio_toggle_and_bitrate", editingSupported = supportsInputAndAudio, passed = true });
            ApplySettings(originalSettings);

            await SelectTabAsync(2);
            Require(TabletImeBox.IsVisible, "键盘设置页未加载。");
            Require(TabletImeBox.IsEnabled == supportsInputAndAudio, "平板输入法选项可用状态与当前连接方式不符。");
            TabletImeBox.IsChecked = false;
            Require(ReadSettings() == (originalSettings with { UseTabletIme = false }), "关闭平板输入法时设置值未更新或其他设置被覆盖。");
            TabletImeBox.IsChecked = true;
            Require(ReadSettings() == (originalSettings with { UseTabletIme = true }), "恢复平板输入法时设置值未更新或其他设置被覆盖。");
            checks.Add(new { name = "keyboard_toggle_persistence", editingSupported = supportsInputAndAudio, passed = true });
            ApplySettings(originalSettings);

            await SelectTabAsync(0);
            Require(QualityPicker.IsVisible && BitratePicker.IsVisible, "画面设置页未加载。");
            foreach (var choice in new[] { (Quality: 0, Bitrate: 0), (Quality: 1, Bitrate: 50), (Quality: 2, Bitrate: 8) })
            {
                QualityPicker.SelectedIndex = choice.Quality;
                BitratePicker.SelectedValue = choice.Bitrate.ToString();
                Require(ReadSettings() == (originalSettings with { QualityIndex = choice.Quality, VideoBitRateMbps = choice.Bitrate }),
                    "画质或视频码率选择没有正确读回，或覆盖了其他设置。");
            }
            ApplySettings(originalSettings);
            Require(ReadSettings() == originalSettings, "恢复原有设置失败。");
            checks.Add(new { name = "quality_and_bitrate_roundtrip", passed = true });

            Width = 1280;
            Height = 900;
            await SelectTabAsync(0);
            await CaptureWindowAsync();
            screenshots.Add("window.png");
            var finalClient = (FrameworkElement)Content;
            CheckButtonBounds(StartButton, finalClient);
            CheckButtonBounds(StopButton, finalClient);
            Require(ReadSettings() == originalSettings, "最终界面没有保留原有设置。");
            var directory = Path.Combine(root, "selftest");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "ui-result.json"), JsonSerializer.Serialize(new
            {
                success = true,
                scope = "实际 WPF 布局与程序化控件检查，不代表鼠标交互或真机投屏验证。",
                checks, screenshots, originalSettings, finalSettings = ReadSettings(),
                selectedProtocol = (DevicePicker.SelectedItem as DeviceInfo)?.Protocol
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            ApplySettings(originalSettings);
        }
        Close();
    }
}
