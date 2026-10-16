using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace EasyShare;

public sealed class MirrorSession : IAsyncDisposable
{
    private readonly Process process;
    private readonly TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> audioReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<string> log;
    private volatile bool stopping;
    private int audioState;
    private string? audioFailure;
    private string? keyboardFailure;
    public bool IsExternal { get; }
    public bool UseTabletIme { get; }
    public string KeyboardMode => !IsExternal ? "disabled" : UseTabletIme ? "uhid" : "sdk";
    public string? KeyboardError => Volatile.Read(ref keyboardFailure);
    public int VideoBitRateMbps { get; }
    public bool AudioEnabled { get; }
    public int AudioBitRateKbps { get; }
    // The Windows output callback has consumed audio samples, which may be silent.
    public bool AudioReady => Volatile.Read(ref audioState) == 1;
    public string? TextureSize { get; private set; }
    public event Action? TextureSizeChanged;
    public event Action? AudioStateChanged;
    public event Action? Exited;

    public MirrorSession(string root, string adb, DeviceInfo device, int scale, int fps, int videoBitRateMbps, bool audioEnabled, int audioBitRateKbps, bool useTabletIme, Action<string> log)
    {
        this.log = log;
        IsExternal = true;
        UseTabletIme = useTabletIme;
        VideoBitRateMbps = videoBitRateMbps;
        AudioEnabled = audioEnabled;
        AudioBitRateKbps = audioBitRateKbps;
        var executable = Path.Combine(root, "tools", "scrcpy", "scrcpy.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("缺少 ADB 投屏组件。请重新解压完整发布包。");
        }
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        info.Environment["ADB"] = adb;
        foreach (var value in new[]
        {
            "--serial=" + device.Serial, "--mouse=sdk", "--no-mouse-hover",
            // scrcpy also needs its key processor for right-click Back and middle-click Home.
            "--keyboard=" + KeyboardMode, "--mouse-bind=bhsn:++++", "--no-clipboard-autosync", "--no-power-on",
            "--max-size=" + (scale == 1 ? 0 : scale == 3 ? 1080 : 1600),
            "--max-fps=" + fps, $"--video-bit-rate={VideoBitRateMbps}M", "--video-codec=h264",
            "--window-title=EasyShare · 平板画面"
        })
        {
            info.ArgumentList.Add(value);
        }
        if (AudioEnabled)
        {
            info.ArgumentList.Add("--audio-source=output");
            info.ArgumentList.Add("--audio-codec=opus");
            info.ArgumentList.Add($"--audio-bit-rate={AudioBitRateKbps}K");
            info.ArgumentList.Add("--require-audio");
            info.ArgumentList.Add("--verbosity=verbose");
        }
        else
        {
            info.ArgumentList.Add("--no-audio");
        }
        if (scale == 1)
        {
            info.ArgumentList.Add("--no-downsize-on-error");
        }
        log($"ADB 画质：{(scale == 1 ? "原始分辨率，禁止自动降级" : scale == 3 ? "最长边 1080" : "最长边 1600")}；H.264 {VideoBitRateMbps} Mbps；帧率上限 {fps}；窗口自动适配。");
        log("鼠标控制：左键点击、长按和拖动，右键返回，中键回到主屏幕；自动剪贴板同步关闭。");
        log(UseTabletIme
            ? "中文输入：请求 UHID 外接键盘；电脑输入法保持英文，打拼音、空格选词。若只出现英文，在投屏的普通文本框中按 Ctrl + Shift 将小艺切到中文。"
            : "兼容键盘：使用 SDK 普通按键，中文请开启「中文输入 · 使用平板输入法」后重新投屏。");
        log(AudioEnabled
            ? $"系统声音：请求通过 USB 传到电脑，Opus 目标码率 {AudioBitRateKbps} kbps；等待实际输出确认。"
            : "系统声音：不传输，仍在平板播放。");
        process = StartProcess(info);
    }

    public MirrorSession(string root, string hdc, string serial, int scale, int fps, int videoBitRateMbps, bool jpeg, Action<string> log, bool selfTest = false)
    {
        this.log = log;
        VideoBitRateMbps = videoBitRateMbps;
        var java = Path.Combine(root, "tools", "jre", "bin", "java.exe");
        if (!File.Exists(java))
        {
            throw new FileNotFoundException("缺少 Java 运行环境。请重新解压完整发布包。");
        }
        var info = new ProcessStartInfo(java)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        info.Environment["HDC_SERVER_PORT"] = "8710";
        foreach (var value in new[]
        {
            "-Xmx256m", "-Dfile.encoding=UTF-8", "-cp",
            Path.Combine(root, "tools", "hosscrcpy", "hosScrcpy-1.0.18-beta.jar") + ";" + Path.Combine(root, "tools", "bridge"),
            "EasyShareBridge", "--hdc", hdc, "--sn", serial, "--scale", scale.ToString(), "--fps", fps.ToString(),
            "--bitrate", VideoBitRateMbps.ToString(),
            "--player", Path.Combine(root, "Player"), "--mode", jpeg ? "jpeg" : "h264", "--selftest", selfTest.ToString().ToLowerInvariant()
        })
        {
            info.ArgumentList.Add(value);
        }
        process = StartProcess(info);
    }

    private Process StartProcess(ProcessStartInfo info)
    {
        var child = new Process { StartInfo = info, EnableRaisingEvents = true };
        child.OutputDataReceived += (_, e) => OnOutput(e.Data);
        child.ErrorDataReceived += (_, e) => OnOutput(e.Data);
        child.Exited += (_, _) =>
        {
            var error = KeyboardError ?? Volatile.Read(ref audioFailure) ?? "投屏服务已退出。请查看日志并检查设备连接。";
            ready.TrySetException(new IOException(error));
            if (AudioEnabled)
            {
                Interlocked.Exchange(ref audioState, 2);
                audioReady.TrySetException(new IOException(KeyboardError ?? Volatile.Read(ref audioFailure)
                    ?? (stopping ? "声音传输已停止。" : "投屏服务已退出，未完成系统声音输出确认。请查看日志；可关闭声音后先传输画面。")));
                AudioStateChanged?.Invoke();
            }
            if (!stopping)
            {
                Exited?.Invoke();
            }
        };
        child.Start();
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        return child;
    }

    private void OnOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        if (UseTabletIme && !stopping && (line.Contains("com.genymobile.scrcpy.control.UhidManager.", StringComparison.Ordinal)
            || line.Contains("Could not send UHID_CREATE message (keyboard)", StringComparison.Ordinal)
            || line.Contains("Could not push UHID_INPUT message (key)", StringComparison.Ordinal)))
        {
            const string message = "平板外接键盘连接失败。可取消「中文输入 · 使用平板输入法」后重新投屏，暂用普通键盘；详情见日志。";
            if (Interlocked.CompareExchange(ref keyboardFailure, message, null) == null)
            {
                log(message);
                ready.TrySetException(new IOException(message));
                if (AudioEnabled)
                {
                    audioReady.TrySetException(new IOException(message));
                }
            }
        }
        if (AudioEnabled && line.StartsWith("VERBOSE: [Audio] Buffering:", StringComparison.Ordinal))
        {
            // scrcpy emits this only after SDL has pulled samples and playback has started.
            if (!stopping && Interlocked.CompareExchange(ref audioState, 1, 0) == 0)
            {
                log(line);
                log("系统声音输出已建立：Windows 播放回调已取得音频样本（样本可能为静音，尚未代表听测通过）。");
                audioReady.TrySetResult(true);
                AudioStateChanged?.Invoke();
            }
            return;
        }
        if (line.StartsWith("VERBOSE:", StringComparison.Ordinal))
        {
            return;
        }
        if (line.StartsWith("EASYSHARE_READY ", StringComparison.Ordinal))
        {
            ready.TrySetResult(line[16..]);
        }
        else
        {
            log(line);
            if (AudioEnabled && !stopping && IsAudioFailure(line))
            {
                FailAudio("系统声音传输失败。请检查电脑声音输出和平板支持情况；可关闭声音后先投屏。详情：" + line);
            }
            var size = IsExternal ? Regex.Match(line, @"^INFO: Texture(?: \([^)]*\))?: (\d+)x(\d+)") : Match.Empty;
            if (size.Success)
            {
                TextureSize = $"{size.Groups[1].Value} × {size.Groups[2].Value}";
                TextureSizeChanged?.Invoke();
                ready.TrySetResult("");
            }
        }
    }

    private static bool IsAudioFailure(string line)
    {
        var error = line.StartsWith("ERROR:", StringComparison.Ordinal)
            || line.StartsWith("[server] ERROR:", StringComparison.Ordinal);
        return (error && line.Contains("audio", StringComparison.OrdinalIgnoreCase))
            || line.StartsWith("WARN: Demuxer 'audio':", StringComparison.Ordinal)
            || line.StartsWith("WARN: Audio stream error:", StringComparison.Ordinal)
            || line.StartsWith("[server] WARN: Audio disabled:", StringComparison.Ordinal)
            || line.StartsWith("ERROR: Resampling failed:", StringComparison.Ordinal)
            || line.StartsWith("ERROR: Failed to initialize the resampling context", StringComparison.Ordinal);
    }

    private void FailAudio(string message)
    {
        Interlocked.CompareExchange(ref audioFailure, message, null);
        var error = Volatile.Read(ref audioFailure)!;
        var previousState = Interlocked.Exchange(ref audioState, 2);
        audioReady.TrySetException(new IOException(error));
        ready.TrySetException(new IOException(error));
        if (previousState != 2)
        {
            log(error);
            AudioStateChanged?.Invoke();
        }
    }

    public Task<string> WaitForReadyAsync() => ready.Task.WaitAsync(TimeSpan.FromSeconds(20));

    public async Task WaitForAudioAsync()
    {
        if (!AudioEnabled)
        {
            throw new InvalidOperationException("本次投屏未启用系统声音传输。");
        }
        try
        {
            await audioReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            const string message = "尚未收到系统声音输出确认。请在平板播放一段声音后重试；也可关闭声音后先传输画面。";
            FailAudio(message);
            throw new TimeoutException(message);
        }
        if (!AudioReady)
        {
            throw new IOException(Volatile.Read(ref audioFailure) ?? "系统声音传输已结束。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping = true;
        if (AudioEnabled)
        {
            Interlocked.Exchange(ref audioState, 2);
            AudioStateChanged?.Invoke();
        }
        try
        {
            if (!process.HasExited)
            {
                if (IsExternal)
                {
                    process.CloseMainWindow();
                }
                else
                {
                    await process.StandardInput.WriteLineAsync("stop");
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException)
        {
            log("停止投屏服务：" + e.Message);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            process.Dispose();
        }
    }
}

