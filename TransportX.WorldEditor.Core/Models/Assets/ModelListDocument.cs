using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models.Primitives;

namespace TransportX.WorldEditor.Core.Models.Assets;

/// <summary>
/// Physics material parameters attached to a model or primitive collider in <c>Models.txt</c>.
/// </summary>
public sealed record ColliderMaterialParams(
    float FrictionCoefficient = 1.0f,
    float MaxRecoveryVelocity = float.MaxValue,
    float SpringFrequency = 30.0f,
    float SpringDampingRatio = 1.0f);

/// <summary>
/// Identifies the collider command type used by a <c>Models.txt</c> entry.
/// </summary>
public enum ModelColliderKind
{
    NonCollision,
    BoundingBox,
    ConvexHull,
    ClosedModel,
    OpenModel,
    Box,
    Cylinder,
    Sphere,
}

/// <summary>
/// Discriminated specification of a <c>Models.txt</c> collider command sequence.
/// </summary>
public sealed record ModelColliderSpec
{
    public ModelColliderKind Kind { get; init; } = ModelColliderKind.NonCollision;

    /// <summary>
    /// When <see langword="false"/>, <c>$LH</c> is emitted to disable automatic right-to-left coordinate conversion.
    /// </summary>
    public bool MakeLeftHanded { get; init; } = true;

    public string? CollisionModelPath { get; init; }
    public ColliderMaterialParams? Material { get; init; }
    public float SizeX { get; init; }
    public float SizeY { get; init; }
    public float SizeZ { get; init; }
    public bool HasTranslationOffset { get; init; }
    public bool HasRotationOffset { get; init; }
    public PoseDegreesModel OffsetPose { get; init; } = PoseDegreesModel.Identity;

    public static ModelColliderSpec NonCollision(bool makeLeftHanded = true)
    {
        return new ModelColliderSpec
        {
            Kind = ModelColliderKind.NonCollision,
            MakeLeftHanded = makeLeftHanded,
        };
    }

    public static ModelColliderSpec BoundingBox(ColliderMaterialParams? material = null, bool makeLeftHanded = true)
    {
        return new ModelColliderSpec
        {
            Kind = ModelColliderKind.BoundingBox,
            MakeLeftHanded = makeLeftHanded,
            Material = material,
        };
    }

    public static ModelColliderSpec ConvexHull(ColliderMaterialParams? material = null, bool makeLeftHanded = true)
    {
        return new ModelColliderSpec
        {
            Kind = ModelColliderKind.ConvexHull,
            MakeLeftHanded = makeLeftHanded,
            Material = material,
        };
    }

    public static ModelColliderSpec MeshCollider(
        bool isOpen,
        string? collisionModelPath = null,
        ColliderMaterialParams? material = null,
        bool makeLeftHanded = true)
    {
        return new ModelColliderSpec
        {
            Kind = isOpen ? ModelColliderKind.OpenModel : ModelColliderKind.ClosedModel,
            MakeLeftHanded = makeLeftHanded,
            CollisionModelPath = collisionModelPath,
            Material = material,
        };
    }

    public static ModelColliderSpec BoxCollider(
        float width,
        float height,
        float length,
        PoseDegreesModel? offset = null,
        bool includeRotation = false,
        ColliderMaterialParams? material = null,
        bool makeLeftHanded = true)
    {
        return new ModelColliderSpec
        {
            Kind = ModelColliderKind.Box,
            MakeLeftHanded = makeLeftHanded,
            SizeX = width,
            SizeY = height,
            SizeZ = length,
            HasTranslationOffset = offset.HasValue,
            HasRotationOffset = offset.HasValue && includeRotation,
            OffsetPose = offset ?? PoseDegreesModel.Identity,
            Material = material,
        };
    }

    public static ModelColliderSpec CylinderCollider(
        float radius,
        float length,
        PoseDegreesModel? offset = null,
        bool includeRotation = false,
        ColliderMaterialParams? material = null,
        bool makeLeftHanded = true)
    {
        return new ModelColliderSpec
        {
            Kind = ModelColliderKind.Cylinder,
            MakeLeftHanded = makeLeftHanded,
            SizeX = radius,
            SizeY = length,
            HasTranslationOffset = offset.HasValue,
            HasRotationOffset = offset.HasValue && includeRotation,
            OffsetPose = offset ?? PoseDegreesModel.Identity,
            Material = material,
        };
    }

    public static ModelColliderSpec SphereCollider(
        float radius,
        PoseDegreesModel? offset = null,
        bool includeRotation = false,
        ColliderMaterialParams? material = null,
        bool makeLeftHanded = true)
    {
        return new ModelColliderSpec
        {
            Kind = ModelColliderKind.Sphere,
            MakeLeftHanded = makeLeftHanded,
            SizeX = radius,
            HasTranslationOffset = offset.HasValue,
            HasRotationOffset = offset.HasValue && includeRotation,
            OffsetPose = offset ?? PoseDegreesModel.Identity,
            Material = material,
        };
    }
}

/// <summary>
/// Represents a single record inside <c>Models.txt</c>.
/// </summary>
public sealed class ModelListEntry
{
    public string Key { get; set; } = string.Empty;
    public string? ModelPath { get; set; }
    public ModelColliderSpec Collider { get; set; } = ModelColliderSpec.NonCollision();
}

/// <summary>
/// Represents a loaded <c>Models.txt</c> resource list.
/// </summary>
public sealed class ModelListDocument
{
    public string? BundleKey { get; set; }
    public string RelativePath { get; set; } = "Models.txt";
    public List<ModelListEntry> Entries { get; } = new List<ModelListEntry>();
}
