using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace EasyShare;

public partial class MainWindow
{
    private async Task RunRefreshTestAsync()
    {
        const string sentinel = "刷新稳定性检查：保留当前提示";
        var controls = new Control[]
        {
            DevicePicker, QualityPicker, BitratePicker, AudioEnabledBox, AudioBitratePicker,
            TabletImeBox, JpegMode, StartButton, StopButton, RefreshButton, FullscreenButton
        };
        var itemsDescriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ComboBox));
        var textDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        var unexpectedChanges = new List<string>();
        var rounds = new List<object>();
        var manualButtonChanges = 0;
        var manual = false;
        var allowStatusChange = false;
        var watching = false;
        var manualCompleted = false;
        var errorRecoveryCompleted = false;
        string? discoveryFailureStatus = null;
        string? discoveryRecoveryStatus = null;
        StreamSettings? expectedSettings = null;
        Exception? failure = null;

        void OnEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (manual && ReferenceEquals(sender, RefreshButton))
            {
                manualButtonChanges++;
                return;
            }
            unexpectedChanges.Add($"{((Control)sender).Name}.IsEnabled: {e.OldValue} -> {e.NewValue}");
        }
        void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            unexpectedChanges.Add("设备选择发生变化。");
        }
        void OnItemsChanged(object? sender, EventArgs e)
        {
            unexpectedChanges.Add("设备 ItemsSource 被替换。");
        }
        void OnTextChanged(object? sender, EventArgs e)
        {
            if (!allowStatusChange)
            {
                unexpectedChanges.Add("后台刷新覆盖了状态提示：" + StatusText.Text);
            }
        }
        void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        try
        {
            discoveryTimer.Stop();
            await RefreshAsync(true);
            var device = DevicePicker.Items.Cast<DeviceInfo>().FirstOrDefault(d => d.IsReady && d.Protocol == "ADB")
                ?? DevicePicker.Items.Cast<DeviceInfo>().FirstOrDefault(d => d.IsReady);
            Require(device != null, "刷新检查需要至少一台已就绪、连接稳定的 USB 设备。");
            DevicePicker.SelectedItem = device;
            JpegMode.IsChecked = false;
            ApplySettings(new StreamSettings { QualityIndex = 1, VideoBitRateMbps = 20, AudioEnabled = true, AudioBitRateKbps = 192 });
            UpdateButtons();
            var originalItems = DevicePicker.ItemsSource;
            var originalSelection = DevicePicker.SelectedItem;
            var expectsAudioControls = device!.Protocol == "ADB";
            StatusText.Text = sentinel;

            watching = true;
            foreach (var control in controls)
            {
                control.IsEnabledChanged += OnEnabledChanged;
            }
            DevicePicker.SelectionChanged += OnSelectionChanged;
            itemsDescriptor.AddValueChanged(DevicePicker, OnItemsChanged);
            textDescriptor.AddValueChanged(StatusText, OnTextChanged);
            for (var round = 1; round <= 3; round++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                var elapsed = Stopwatch.StartNew();
                var refresh = RefreshAsync();
                Require(refreshing, "后台刷新没有进入异步枚举，无法验证等待期间的控件状态。");
                Require(DevicePicker.IsEnabled && QualityPicker.IsEnabled && BitratePicker.IsEnabled && StartButton.IsEnabled,
                    "后台枚举等待期间禁用了设备、画质、视频码率或开始按钮。");
                Require(!expectsAudioControls || (AudioEnabledBox.IsEnabled && AudioBitratePicker.IsEnabled),
                    "后台枚举等待期间禁用了音频设置。");
                QualityPicker.SelectedIndex = round - 1;
                BitratePicker.SelectedValue = round == 2 ? "8" : "20";
                if (expectsAudioControls)
                {
                    AudioBitratePicker.SelectedValue = round == 2 ? "256" : "192";
                }
                expectedSettings = ReadSettings();
                await refresh;
                elapsed.Stop();
                Require(ReadSettings() == expectedSettings, "后台刷新覆盖了等待期间选择的音视频设置。");
                Require(ReferenceEquals(originalItems, DevicePicker.ItemsSource), "稳定设备列表被替换。");
                Require(ReferenceEquals(originalSelection, DevicePicker.SelectedItem), "后台刷新改变了所选设备。");
                Require(StatusText.Text == sentinel, "后台刷新覆盖了诊断哨兵提示。");
                Require(unexpectedChanges.Count == 0, string.Join(Environment.NewLine, unexpectedChanges));
                rounds.Add(new { round, waitIntervalSeconds = 5, enumerationMilliseconds = elapsed.ElapsedMilliseconds, settings = expectedSettings });
            }

            manual = true;
            allowStatusChange = true;
            var manualTask = RefreshAsync(true);
            Require(manualTask.IsCompleted || !RefreshButton.IsEnabled, "手动枚举等待期间未禁用刷新按钮。");
            Require(QualityPicker.IsEnabled && BitratePicker.IsEnabled && DevicePicker.IsEnabled,
                "手动刷新禁用了整个设置面板。");
            await manualTask;
            Require(!refreshing && !manualRefresh && RefreshButton.IsEnabled, "手动刷新结束后按钮或刷新标志未恢复。");
            Require(StatusText.Text != sentinel && !StatusText.Text.Contains("正在查找", StringComparison.Ordinal),
                "手动刷新结束后未显示最终设备状态。");
            Require(ReadSettings() == expectedSettings, "手动刷新覆盖了音视频设置。");
            Require(unexpectedChanges.Count == 0, string.Join(Environment.NewLine, unexpectedChanges));
            manualCompleted = true;

            var savedAdb = adb;
            Require(savedAdb != null && device.Protocol == "ADB", "检测失败恢复检查需要可用的 ADB 工具和已就绪设备。");
            var missingAdb = Path.Combine(root, "selftest", "missing-adb-" + Guid.NewGuid().ToString("N") + ".exe");
            Require(!File.Exists(missingAdb), "故障检查使用的 ADB 路径必须不存在。");
            try
            {
                adb = missingAdb;
                await RefreshAsync(true);
                discoveryFailureStatus = StatusText.Text;
                Require(discoveryFailureStatus.StartsWith("设备检测失败", StringComparison.Ordinal),
                    "工具枚举失败没有明确显示检测错误。");
                Require(ReferenceEquals(originalItems, DevicePicker.ItemsSource), "检测失败重建了上次设备列表。");
                Require(ReferenceEquals(originalSelection, DevicePicker.SelectedItem), "检测失败改变了所选设备。");
            }
            finally
            {
                adb = savedAdb;
            }
            manual = false;
            await RefreshAsync();
            discoveryRecoveryStatus = StatusText.Text;
            Require(discoveryRecoveryStatus != discoveryFailureStatus
                && !discoveryRecoveryStatus.StartsWith("设备检测失败", StringComparison.Ordinal)
                && !discoveryRecoveryStatus.Contains("正在查找", StringComparison.Ordinal),
                "同一设备列表恢复成功后仍保留了检测失败提示。");
            Require(ReferenceEquals(originalItems, DevicePicker.ItemsSource), "错误恢复重建了稳定设备列表。");
            Require(ReferenceEquals(originalSelection, DevicePicker.SelectedItem), "错误恢复改变了所选设备。");
            Require(ReadSettings() == expectedSettings, "检测失败或恢复覆盖了音视频设置。");
            Require(unexpectedChanges.Count == 0, string.Join(Environment.NewLine, unexpectedChanges));
            errorRecoveryCompleted = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (watching)
            {
                foreach (var control in controls)
                {
                    control.IsEnabledChanged -= OnEnabledChanged;
                }
                DevicePicker.SelectionChanged -= OnSelectionChanged;
                itemsDescriptor.RemoveValueChanged(DevicePicker, OnItemsChanged);
                textDescriptor.RemoveValueChanged(StatusText, OnTextChanged);
            }
        }

        var directory = Path.Combine(root, "selftest");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "refresh-result.json"), JsonSerializer.Serialize(new
        {
            success = failure == null,
            automaticRefreshCount = rounds.Count,
            rounds,
            unexpectedChanges,
            manualCompleted,
            manualButtonChanges,
            errorRecoveryCompleted,
            discoveryFailureStatus,
            discoveryRecoveryStatus,
            finalSettings = expectedSettings,
            error = failure?.ToString()
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (failure != null)
        {
            throw new InvalidOperationException("刷新稳定性检查失败，详情见 selftest/refresh-result.json。", failure);
        }
        Close();
    }
}
