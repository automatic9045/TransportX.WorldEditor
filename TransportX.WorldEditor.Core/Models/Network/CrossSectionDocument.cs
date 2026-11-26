using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TransportX.WorldEditor.Core.Models.Network;

/// <summary>
/// Texture sampling mode matching <c>TransportX.Rendering.TextureSampleMode</c>.
/// </summary>
public enum TextureSampleModeModel : uint
{
    None = 0,
    Texture = 1,
    Screen = 2,
}

/// <summary>
/// Represents a texture slot inside a <c>CrossSectionMaterialModel</c>.
/// </summary>
public sealed record CrossSectionTextureSlotModel(
    string? Path = null,
    TextureSampleModeModel SampleMode = TextureSampleModeModel.Texture)
{
    public static CrossSectionTextureSlotModel Empty { get; } = new CrossSectionTextureSlotModel(null, TextureSampleModeModel.Texture);

    public bool IsEmpty => string.IsNullOrEmpty(Path);
}

/// <summary>
/// Represents a 2D cross-section profile vertex (<c>CrossSectionVertex</c>).
/// </summary>
public sealed class CrossSectionVertexModel
{
    public string Key { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float NormalX { get; set; }
    public float NormalY { get; set; } = 1.0f;
    public float U { get; set; }
    public string ColorHex { get; set; } = "#FFFFFF";
}

/// <summary>
/// Represents an extruded surface strip (<c>CrossSectionSurface</c>) referencing vertices, material, and optional collider material.
/// </summary>
public sealed class CrossSectionSurfaceModel
{
    public float VInterval { get; set; } = 1.0f;
    public float VOffset { get; set; }
    public string MaterialName { get; set; } = string.Empty;
    public string? ColliderMaterialName { get; set; }
    public List<string> VertexKeys { get; } = new List<string>();
}

/// <summary>
/// Represents a PBR material inside a <c>CrossSection</c> XML document.
/// </summary>
public sealed class CrossSectionMaterialModel : INotifyPropertyChanged
{
    private string NameValue = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => NameValue;
        set
        {
            if (string.Equals(NameValue, value, StringComparison.Ordinal))
            {
                return;
            }

            NameValue = value;
            OnPropertyChanged();
        }
    }

    public string BaseColorHex { get; set; } = "#FFFFFF";
    public float Metallic { get; set; }
    public float Roughness { get; set; } = 1.0f;
    public string EmissiveHex { get; set; } = "#FFFFFF";
    public float EmissiveStrength { get; set; } = 1.0f;

    public CrossSectionTextureSlotModel BaseColorTexture { get; set; } = CrossSectionTextureSlotModel.Empty;
    public CrossSectionTextureSlotModel NormalTexture { get; set; } = CrossSectionTextureSlotModel.Empty;
    public CrossSectionTextureSlotModel OcclusionTexture { get; set; } = CrossSectionTextureSlotModel.Empty;
    public CrossSectionTextureSlotModel RoughnessTexture { get; set; } = CrossSectionTextureSlotModel.Empty;
    public CrossSectionTextureSlotModel MetallicTexture { get; set; } = CrossSectionTextureSlotModel.Empty;
    public CrossSectionTextureSlotModel EmissiveTexture { get; set; } = CrossSectionTextureSlotModel.Empty;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Represents a physics collider material inside a <c>CrossSection</c> XML document.
/// </summary>
public sealed class CrossSectionColliderMaterialModel : INotifyPropertyChanged
{
    private string NameValue = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => NameValue;
        set
        {
            if (string.Equals(NameValue, value, StringComparison.Ordinal))
            {
                return;
            }

            NameValue = value;
            OnPropertyChanged();
        }
    }

    public float FrictionCoefficient { get; set; } = 1.0f;
    public float MaxRecoveryVelocity { get; set; } = float.MaxValue;
    public float SpringFrequency { get; set; } = 30.0f;
    public float SpringDampingRatio { get; set; } = 1.0f;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Represents a <c>CrossSections/*.xml</c> procedural extrusion cross-section document.
/// </summary>
public sealed class CrossSectionDocument
{
    public string Key { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public List<CrossSectionVertexModel> Vertices { get; } = new List<CrossSectionVertexModel>();
    public List<CrossSectionSurfaceModel> Surfaces { get; } = new List<CrossSectionSurfaceModel>();
    public List<CrossSectionMaterialModel> Materials { get; } = new List<CrossSectionMaterialModel>();
    public List<CrossSectionColliderMaterialModel> ColliderMaterials { get; } = new List<CrossSectionColliderMaterialModel>();
}
