using System;
using System.Collections.Generic;
using System.IO;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using TransportX.Collections;
using TransportX.Diagnostics;
using TransportX.Physics;
using TransportX.Rendering;
using TransportX.Rendering.Backend;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Primitives;
using Vortice.Direct3D11;

namespace TransportX.WorldEditor.Rendering.Assets;

/// <summary>
/// Loads all <see cref="ModelListDocument"/> bundles defined inside a <see cref="WorldProject"/>
/// into a <see cref="ModelCollection"/> using TransportX's <see cref="ModelFactory"/> and <see cref="ColliderFactory"/>.
/// </summary>
public static class EditorModelLoader
{
    public static void LoadProjectModels(
        WorldProject project,
        IGraphicsHost graphicsHost,
        IPhysicsHost physicsHost,
        IErrorCollector errorCollector,
        ModelCollection targetCollection)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(graphicsHost);
        ArgumentNullException.ThrowIfNull(physicsHost);
        ArgumentNullException.ThrowIfNull(errorCollector);
        ArgumentNullException.ThrowIfNull(targetCollection);

        using ModelFactory factory = new ModelFactory(
            graphicsHost.Context,
            graphicsHost.WICFactory,
            physicsHost.Simulation,
            errorCollector);

        HashSet<string> loadedKeys = new HashSet<string>(StringComparer.Ordinal);
        int bundleIndex = 0;

        foreach (ModelListDocument listDoc in project.ModelLists.Values)
        {
            string bundleKey = string.IsNullOrWhiteSpace(listDoc.BundleKey)
                ? $"EditorBundle_{bundleIndex++}"
                : listDoc.BundleKey;

            Dictionary<string, ModelResourceSet> bundleModels = new Dictionary<string, ModelResourceSet>(StringComparer.Ordinal);
            string listDir = Path.GetDirectoryName(Path.Combine(project.WorldDirectory, listDoc.RelativePath))
                ?? project.WorldDirectory;

            foreach (ModelListEntry entry in listDoc.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || !loadedKeys.Add(entry.Key))
                {
                    continue;
                }

                try
                {
                    ModelResourceSet resource = LoadSingleEntry(entry, listDir, factory, physicsHost, errorCollector);
                    bundleModels[entry.Key] = resource;
                }
                catch (Exception ex)
                {
                    errorCollector.Report(new Error(
                        ErrorLevel.Warning,
                        $"モデル '{entry.Key}' の読込に失敗しました: {ex.Message}",
                        listDoc.RelativePath));
                }
            }

            List<ID3D11ShaderResourceView> texturesCopy = new List<ID3D11ShaderResourceView>(factory.Textures);
            ModelBundle bundle = new ModelBundle(bundleKey, bundleModels, texturesCopy);
            targetCollection.AdoptBundle(bundle, allowOverride: true);
        }
    }

    private static ModelResourceSet LoadSingleEntry(
        ModelListEntry entry,
        string baseDir,
        ModelFactory factory,
        IPhysicsHost physicsHost,
        IErrorCollector errorCollector)
    {
        ModelColliderSpec spec = entry.Collider;
        ColliderMaterial material = ToRuntimeMaterial(spec.Material);

        string? fullVisualPath = string.IsNullOrWhiteSpace(entry.ModelPath)
            ? null
            : Path.GetFullPath(Path.Combine(baseDir, entry.ModelPath.Replace('/', Path.DirectorySeparatorChar)));

        if (fullVisualPath is not null && !File.Exists(fullVisualPath))
        {
            errorCollector.Report(new Error(
                ErrorLevel.Warning,
                $"モデルファイルが見つかりません ({entry.Key}): {entry.ModelPath}",
                entry.ModelPath));
            fullVisualPath = null;
        }

        if (fullVisualPath is null)
        {
            return CreateWithPrimitiveIfRequested(ModelResourceSet.Empty(), spec, material, physicsHost);
        }

        switch (spec.Kind)
        {
            case ModelColliderKind.NonCollision:
                return factory.Load(fullVisualPath, spec.MakeLeftHanded);

            case ModelColliderKind.BoundingBox:
                return factory.LoadWithBoundingBox(fullVisualPath, spec.MakeLeftHanded, material);

            case ModelColliderKind.ConvexHull:
                return factory.LoadWithConvexHull(fullVisualPath, spec.MakeLeftHanded, material);

            case ModelColliderKind.ClosedModel:
            case ModelColliderKind.OpenModel:
            {
                bool isOpen = spec.Kind == ModelColliderKind.OpenModel;
                string colPath = fullVisualPath;
                if (!string.IsNullOrWhiteSpace(spec.CollisionModelPath))
                {
                    string candidate = Path.GetFullPath(Path.Combine(baseDir, spec.CollisionModelPath.Replace('/', Path.DirectorySeparatorChar)));
                    if (File.Exists(candidate))
                    {
                        colPath = candidate;
                    }
                    else
                    {
                        errorCollector.Report(new Error(
                            ErrorLevel.Warning,
                            $"コリジョンモデルが見つかりません ({entry.Key}): {spec.CollisionModelPath}",
                            spec.CollisionModelPath));
                    }
                }

                return factory.LoadWithCollisionModel(
                    fullVisualPath,
                    spec.MakeLeftHanded,
                    colPath,
                    spec.MakeLeftHanded,
                    material,
                    isOpen);
            }

            case ModelColliderKind.Box:
            case ModelColliderKind.Cylinder:
            case ModelColliderKind.Sphere:
            {
                ModelResourceSet visual = factory.Load(fullVisualPath, spec.MakeLeftHanded);
                return CreateWithPrimitiveIfRequested(visual, spec, material, physicsHost);
            }

            default:
                return factory.Load(fullVisualPath, spec.MakeLeftHanded);
        }
    }

    private static ModelResourceSet CreateWithPrimitiveIfRequested(
        ModelResourceSet baseModel,
        ModelColliderSpec spec,
        ColliderMaterial material,
        IPhysicsHost physicsHost)
    {
        Pose offset = ToOffsetPose(spec);

        switch (spec.Kind)
        {
            case ModelColliderKind.Box:
            {
                Box box = new Box(MathF.Max(0.01f, spec.SizeX), MathF.Max(0.01f, spec.SizeY), MathF.Max(0.01f, spec.SizeZ));
                return baseModel with { Collider = ColliderFactory.Box(physicsHost.Simulation, box, material, offset) };
            }

            case ModelColliderKind.Cylinder:
            {
                Cylinder cyl = new Cylinder(MathF.Max(0.01f, spec.SizeX), MathF.Max(0.01f, spec.SizeY));
                return baseModel with { Collider = ColliderFactory.Cylinder(physicsHost.Simulation, cyl, material, offset) };
            }

            case ModelColliderKind.Sphere:
            {
                Sphere sphere = new Sphere(MathF.Max(0.01f, spec.SizeX));
                return baseModel with { Collider = ColliderFactory.Sphere(physicsHost.Simulation, sphere, material, offset) };
            }

            default:
                return baseModel;
        }
    }

    private static Pose ToOffsetPose(ModelColliderSpec spec)
    {
        if (!spec.HasTranslationOffset && !spec.HasRotationOffset)
        {
            return Pose.Identity;
        }

        PoseDegreesModel p = spec.OffsetPose;
        float rx = spec.HasRotationOffset ? (float)p.RotationX : 0f;
        float ry = spec.HasRotationOffset ? (float)p.RotationY : 0f;
        float rz = spec.HasRotationOffset ? (float)p.RotationZ : 0f;
        float tx = spec.HasTranslationOffset ? (float)p.X : 0f;
        float ty = spec.HasTranslationOffset ? (float)p.Y : 0f;
        float tz = spec.HasTranslationOffset ? (float)p.Z : 0f;
        return SixDoF.FromDegrees(tx, ty, tz, rx, ry, rz).ToPose();
    }

    private static ColliderMaterial ToRuntimeMaterial(ColliderMaterialParams? param)
    {
        if (param is null)
        {
            return ColliderMaterial.Default;
        }

        return new ColliderMaterial(
            param.FrictionCoefficient,
            param.MaxRecoveryVelocity,
            new SpringSettings(param.SpringFrequency, param.SpringDampingRatio));
    }
}
