namespace TransportX.WorldEditor.Core.Models.Environment;

/// <summary>
/// Image-Based Lighting parameters inside an <c>EnvironmentProfile</c> XML document.
/// </summary>
public sealed class IblSettings
{
    public string? DiffuseTexturePath { get; set; }
    public string? SpecularTexturePath { get; set; }
    public float Intensity { get; set; } = 1.0f;
    public float Saturation { get; set; } = 1.0f;
}

/// <summary>
/// Auto-exposure parameters inside an <c>EnvironmentProfile</c> XML document.
/// </summary>
public sealed class ExposureSettings
{
    public float Key { get; set; } = 0.118f;
    public float Min { get; set; } = 0.01f;
    public float Max { get; set; } = 5.0f;
    public float DarkAdaptationSpeed { get; set; } = 0.5f;
    public float LightAdaptationSpeed { get; set; } = 1.5f;
}

/// <summary>
/// Tone mapping parameters inside an <c>EnvironmentProfile</c> XML document.
/// </summary>
public sealed class ToneMapSettings
{
    public float Contrast { get; set; } = 1.6f;
    public float Shoulder { get; set; } = 0.977f;
    public float MaxLuminance { get; set; } = 12.0f;
    public float MidtoneScale { get; set; } = 1.5f;
}

/// <summary>
/// Bloom post-processing parameters inside an <c>EnvironmentProfile</c> XML document.
/// </summary>
public sealed class BloomSettings
{
    public float Threshold { get; set; } = 2.0f;
    public float Intensity { get; set; } = 0.2f;
    public float Scatter { get; set; } = 1.0f;
    public float SoftKnee { get; set; } = 0.5f;
    public string TintHex { get; set; } = "#FFFFFF";
}

/// <summary>
/// Represents an <c>Environment*.xml</c> visual atmosphere profile.
/// </summary>
public sealed class EnvironmentProfileDocument
{
    public string RelativePath { get; set; } = "Environment1.xml";
    public IblSettings Ibl { get; set; } = new IblSettings();
    public ExposureSettings Exposure { get; set; } = new ExposureSettings();
    public ToneMapSettings ToneMap { get; set; } = new ToneMapSettings();
    public BloomSettings Bloom { get; set; } = new BloomSettings();
}
