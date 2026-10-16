using EasyShare;
using System.Text.Json;

var devices = HdcClient.ParseDevices("[Empty]\r\nABC123 USB Connected localhost hdc\r\nABC123 USB Connected localhost hdc\n192.168.1.1:5555 TCP Connected localhost hdc\nLOCKED USB Unauthorized localhost hdc\nPENDING USB Authorization localhost hdc\nDEAD USB Offline localhost hdc\nCOM1 UART Ready unknown hdc\nFUTURE USB Unknown localhost hdc\n[Fail] Error\n");
if (devices.Count != 4 || devices.Count(d => d.IsReady) != 1 || devices[0].Serial != "ABC123"
    || devices.Any(d => d.Protocol != "HDC" || d.Transport != "USB"))
{
    throw new Exception("HDC must retain USB states, exclude UART/TCP/unknown records, and deduplicate devices.");
}
if (devices.Single(d => d.Serial == "LOCKED").State != DeviceState.Unauthorized
    || devices.Single(d => d.Serial == "PENDING").State != DeviceState.Unauthorized
    || devices.Single(d => d.Serial == "DEAD").State != DeviceState.Offline)
{
    throw new Exception("HDC unauthorized and offline devices must remain visible without becoming ready.");
}
if (HdcClient.ParseDevices("[Empty]\r\nCOM1 UART Ready unknown hdc\r\n").Count != 0
    || HdcClient.ParseDevices("").Count != 0)
{
    throw new Exception("Empty or UART-only HDC output must not create a USB device.");
}

var adbDevices = HdcClient.ParseAdbDevices("List of devices attached\r\n* daemon started successfully *\r\nABC123\tdevice product:tablet model:MatePad_11.5S device:HWTablet transport_id:1\r\nABC123 device model:MatePad_11.5S transport_id:1\nLOCKED_ADB\tunauthorized transport_id:2\nDEAD offline transport_id:3\n192.168.1.2:5555 device model:Wireless\n[fe80::1]:5555 device\nadb-network._adb-tls-connect._tcp device\nemulator-5554 device model:sdk_gphone\nUNKNOWN recovery\nBROKEN\n");
if (adbDevices.Count != 3 || adbDevices.Count(d => d.IsReady) != 1
    || adbDevices.Any(d => d.Protocol != "ADB" || d.Transport != "USB"))
{
    throw new Exception("ADB must retain known USB states, deduplicate serials, and exclude network/emulator/unknown records.");
}
if (adbDevices[0].Name != "MatePad 11.5S" || adbDevices[0].State != DeviceState.Ready
    || adbDevices.Single(d => d.Serial == "LOCKED_ADB").State != DeviceState.Unauthorized
    || adbDevices.Single(d => d.Serial == "DEAD").State != DeviceState.Offline)
{
    throw new Exception("ADB model, ready, unauthorized and offline states must be parsed correctly.");
}
if (HdcClient.ParseAdbDevices("List of devices attached\r\n\r\n").Count != 0
    || HdcClient.ParseAdbDevices("").Count != 0)
{
    throw new Exception("Empty ADB responses must not create fake devices.");
}
if (!adbDevices[0].DisplayName.Contains("ADB") || !devices[0].DisplayName.Contains("HDC")
    || !adbDevices.Single(d => d.State == DeviceState.Unauthorized).DisplayName.Contains("待授权")
    || !adbDevices.Single(d => d.State == DeviceState.Offline).DisplayName.Contains("离线"))
{
    throw new Exception("Display names must distinguish protocol and connection state.");
}
var legacyDevice = new DeviceInfo("LEGACY", "USB");
if (legacyDevice.Protocol != "HDC" || !legacyDevice.IsReady)
{
    throw new Exception("Existing two-argument DeviceInfo construction must remain compatible.");
}
Console.WriteLine("PASS: HDC/ADB USB discovery, unauthorized/offline states, model names, deduplication, network filtering and empty output");

var automatic = new StreamSettings();
automatic.Validate();
var clearAutomatic = automatic with { QualityIndex = 1 };
if (automatic.EffectiveVideoBitRateMbps != 12 || clearAutomatic.EffectiveVideoBitRateMbps != 32)
{
    throw new Exception("Automatic video bitrate must preserve the existing smooth and clear presets.");
}
var savedAutomatic = JsonSerializer.Deserialize<StreamSettings>(JsonSerializer.Serialize(automatic))
    ?? throw new Exception("Automatic settings must deserialize.");
var changedAutomatic = savedAutomatic with { QualityIndex = 1 };
if (changedAutomatic.VideoBitRateMbps != 0 || changedAutomatic.EffectiveVideoBitRateMbps != 32)
{
    throw new Exception("Saving automatic bitrate must not freeze its previously computed value.");
}

// Changing resolution must not overwrite a bitrate the user explicitly selected.
var manual = new StreamSettings { VideoBitRateMbps = 50, AudioBitRateKbps = 192 };
foreach (var quality in new[] { 0, 1, 2 })
{
    var changedQuality = manual with { QualityIndex = quality };
    changedQuality.Validate();
    if (changedQuality.EffectiveVideoBitRateMbps != 50)
    {
        throw new Exception("A manual video bitrate must survive resolution preset changes.");
    }
}

// Disabling sound must preserve its selected bitrate for the next session.
var muted = manual with { QualityIndex = 2, AudioEnabled = false };
muted.Validate();
var saved = JsonSerializer.Serialize(muted);
var restored = JsonSerializer.Deserialize<StreamSettings>(saved)
    ?? throw new Exception("Saved stream settings must deserialize.");
restored.Validate();
if (restored != muted || restored.AudioEnabled || restored.AudioBitRateKbps != 192
    || restored.EffectiveVideoBitRateMbps != 50 || restored.Scale != 3 || restored.FrameRate != 30)
{
    throw new Exception("Persistence must retain audio/video choices and recompute the selected resolution preset.");
}
var unmuted = restored with { AudioEnabled = true };
if (unmuted.AudioBitRateKbps != 192 || unmuted.VideoBitRateMbps != 50)
{
    throw new Exception("Enabling sound again must reuse the saved bitrates.");
}

var legacySettings = JsonSerializer.Deserialize<StreamSettings>("{\"QualityIndex\":2,\"VideoBitRateMbps\":50,\"AudioEnabled\":false,\"AudioBitRateKbps\":192}")
    ?? throw new Exception("Legacy stream settings must deserialize.");
if (!legacySettings.UseTabletIme || legacySettings != muted)
{
    throw new Exception("Legacy settings must enable the tablet IME without changing existing audio/video choices.");
}
var ordinaryKeyboard = restored with { UseTabletIme = false };
var restoredKeyboard = JsonSerializer.Deserialize<StreamSettings>(JsonSerializer.Serialize(ordinaryKeyboard))
    ?? throw new Exception("Keyboard settings must deserialize.");
if (restoredKeyboard.UseTabletIme || restoredKeyboard != ordinaryKeyboard
    || (restoredKeyboard with { UseTabletIme = true }) != restored)
{
    throw new Exception("Disabling the tablet IME must survive persistence without changing other settings.");
}

foreach (var bitrate in new[] { 0, 4, 8, 12, 20, 32, 50, 80 })
{
    (automatic with { VideoBitRateMbps = bitrate }).Validate();
}
foreach (var bitrate in new[] { 64, 128, 192, 256 })
{
    (automatic with { AudioBitRateKbps = bitrate }).Validate();
}

// Configuration files are an input boundary: unsupported values must never reach a subprocess.
foreach (var invalidJson in new[]
{
    "{\"QualityIndex\":-1}",
    "{\"QualityIndex\":3}",
    "{\"VideoBitRateMbps\":-1}",
    "{\"VideoBitRateMbps\":13}",
    "{\"VideoBitRateMbps\":2147483647}",
    "{\"AudioBitRateKbps\":0}",
    "{\"AudioBitRateKbps\":127}",
    "{\"AudioBitRateKbps\":320}",
    "{\"AudioEnabled\":false,\"AudioBitRateKbps\":-1}"
})
{
    var invalid = JsonSerializer.Deserialize<StreamSettings>(invalidJson)
        ?? throw new Exception("Boundary test settings must deserialize before validation.");
    try
    {
        invalid.Validate();
    }
    catch (ArgumentOutOfRangeException)
    {
        continue;
    }
    throw new Exception($"Invalid persisted settings were accepted: {invalidJson}");
}

Console.WriteLine("PASS: automatic/manual video bitrate, independent audio choices, tablet IME defaults, JSON persistence and invalid configuration boundaries");
