using System.Text.Json.Serialization;

namespace EasyShare;

public sealed record StreamSettings
{
    public int QualityIndex { get; init; }
    public int VideoBitRateMbps { get; init; }
    public bool AudioEnabled { get; init; } = true;
    public int AudioBitRateKbps { get; init; } = 128;
    public bool UseTabletIme { get; init; } = true;

    [JsonIgnore]
    public int Scale => QualityIndex switch { 1 => 1, 2 => 3, _ => 2 };
    [JsonIgnore]
    public int FrameRate => QualityIndex == 2 ? 30 : 60;
    [JsonIgnore]
    public int EffectiveVideoBitRateMbps => VideoBitRateMbps == 0 ? (QualityIndex == 1 ? 32 : 12) : VideoBitRateMbps;

    public void Validate()
    {
        if (QualityIndex is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(QualityIndex), "画质选项无效。");
        }
        if (VideoBitRateMbps is not (0 or 4 or 8 or 12 or 20 or 32 or 50 or 80))
        {
            throw new ArgumentOutOfRangeException(nameof(VideoBitRateMbps), "请选择自动或 4 / 8 / 12 / 20 / 32 / 50 / 80 Mbps。");
        }
        if (AudioBitRateKbps is not (64 or 128 or 192 or 256))
        {
            throw new ArgumentOutOfRangeException(nameof(AudioBitRateKbps), "请选择 64 / 128 / 192 / 256 kbps 音频码率。");
        }
    }
}
