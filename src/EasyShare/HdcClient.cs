using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace EasyShare;

public enum DeviceState
{
    Ready,
    Unauthorized,
    Offline
}

public record DeviceInfo(string Serial, string Transport, string Protocol = "HDC", DeviceState State = DeviceState.Ready, string? Name = null)
{
    public bool IsReady => State == DeviceState.Ready;

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) ? Serial : Name;
            var status = State switch
            {
                DeviceState.Ready => "已连接",
                DeviceState.Unauthorized => "待授权",
                _ => "离线"
            };
            return $"{Protocol} · {status} · {name}";
        }
    }
}

public static partial class HdcClient
{
    public static List<DeviceInfo> ParseDevices(string output)
    {
        var devices = new List<DeviceInfo>();
        foreach (var line in output.Split('\n'))
        {
            var parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length < 3 || !parts[1].Equals("USB", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            DeviceState? state = parts[2].ToLowerInvariant() switch
            {
                "connected" => DeviceState.Ready,
                "authorization" or "unauthorized" => DeviceState.Unauthorized,
                "offline" => DeviceState.Offline,
                _ => null
            };
            if (state != null)
            {
                devices.Add(new DeviceInfo(parts[0], "USB", "HDC", state.Value));
            }
        }
        return devices.DistinctBy(d => d.Serial).ToList();
    }

    public static List<DeviceInfo> ParseAdbDevices(string output)
    {
        var devices = new List<DeviceInfo>();
        foreach (var line in output.Split('\n'))
        {
            var parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length < 2 || parts[0].Contains(':')
                || parts[0].Contains("_adb-tls", StringComparison.OrdinalIgnoreCase)
                || parts[0].StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            DeviceState? state = parts[1].ToLowerInvariant() switch
            {
                "device" => DeviceState.Ready,
                "unauthorized" => DeviceState.Unauthorized,
                "offline" => DeviceState.Offline,
                _ => null
            };
            if (state != null)
            {
                var model = parts.Skip(2).FirstOrDefault(part => part.StartsWith("model:", StringComparison.OrdinalIgnoreCase));
                devices.Add(new DeviceInfo(parts[0], "USB", "ADB", state.Value, model?[6..].Replace('_', ' ')));
            }
        }
        return devices.DistinctBy(d => d.Serial).ToList();
    }

    public static async Task<string> RunAsync(string path, string[] args, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        };
        if (Path.GetFileName(path).Equals("hdc.exe", StringComparison.OrdinalIgnoreCase))
        {
            info.Environment["HDC_SERVER_PORT"] = "8710";
        }
        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info) ?? throw new IOException("无法启动设备连接工具。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            throw new TimeoutException("设备连接工具响应超时。请检查 USB 连接和设备授权。");
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0 || output.Contains("[Fail]", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"设备连接失败：{output} {error}".Trim());
        }
        return output;
    }

    public static string? FindAdb(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "tools", "scrcpy", "adb.exe");
        return File.Exists(path) ? path : null;
    }

    public static string? Find(string? configured, string baseDirectory)
    {
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(baseDirectory, "tools", "hdc", "hdc.exe"),
            @"C:\Program Files\Huawei\DevEco Studio\sdk\default\openharmony\toolchains\hdc.exe",
            @"C:\Program Files\Huawei\command-line-tools\sdk\default\openharmony\toolchains\hdc.exe"
        };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                candidates.Add(Path.Combine(directory.Trim('"'), "hdc.exe"));
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
