using System;
using System.Collections.Generic;
using System.Drawing;
using TransportX.Rendering;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TxColliderMaterial = TransportX.Scripting.Data.Rendering.ColliderMaterial;
using TxCrossSection = TransportX.Scripting.Data.Rendering.CrossSection;
using TxCrossSectionSurface = TransportX.Scripting.Data.Rendering.CrossSectionSurface;
using TxCrossSectionVertex = TransportX.Scripting.Data.Rendering.CrossSectionVertex;
using TxCrossSectionVertexReference = TransportX.Scripting.Data.Rendering.CrossSectionVertexReference;
using TxMaterial = TransportX.Scripting.Data.Rendering.Material;
using TxTextureSlot = TransportX.Scripting.Data.Rendering.TextureSlot;

namespace TransportX.WorldEditor.Serialization.Xml;

/// <summary>
/// Serializes and parses TransportX <c>&lt;CrossSection&gt;</c> XML documents
/// via TransportX's <see cref="TxCrossSection"/> schema DTO.
/// </summary>
public static class CrossSectionXmlSerializer
{
    public static (CrossSectionDocument? Model, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(
        string key,
        string relativePath,
        string filePath,
        string xmlText)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(xmlText);

        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>();
        TxCrossSection? dto = TransportXmlBridge.Deserialize<TxCrossSection>(filePath, xmlText, diagnostics);
        if (dto is null)
        {
            return (null, diagnostics);
        }

        TransportXmlBridge.AppendErrors(dto.Errors, filePath, diagnostics);

        CrossSectionDocument model = new CrossSectionDocument
        {
            Key = key,
            RelativePath = relativePath,
        };

        foreach (TxCrossSectionVertex v in dto.Vertices)
        {
            model.Vertices.Add(new CrossSectionVertexModel
            {
                Key = v.Key.Value ?? string.Empty,
                X = v.X.Value,
                Y = v.Y.Value,
                NormalX = v.NormalX.Value,
                NormalY = v.NormalY.Value,
                U = v.U.Value,
                ColorHex = TransportXmlBridge.ColorToHex(v.Color.Value),
            });
        }

        foreach (TxCrossSectionSurface s in dto.Surfaces)
        {
            CrossSectionSurfaceModel surface = new CrossSectionSurfaceModel
            {
                VInterval = s.VInterval.Value,
                VOffset = s.VOffset.Value,
                MaterialName = s.Material.Value ?? string.Empty,
                ColliderMaterialName = s.ColliderMaterial.Value,
            };
            foreach (TxCrossSectionVertexReference vRef in s.Vertices)
            {
                surface.VertexKeys.Add(vRef.Key.Value ?? string.Empty);
            }

            model.Surfaces.Add(surface);
        }

        foreach (TxMaterial m in dto.Materials)
        {
            model.Materials.Add(new CrossSectionMaterialModel
            {
                Name = m.Name.Value ?? string.Empty,
                BaseColorHex = TransportXmlBridge.ColorToHex(m.BaseColor.Value),
                Metallic = m.Metallic.Value,
                Roughness = m.Roughness.Value,
                EmissiveHex = TransportXmlBridge.ColorToHex(m.Emissive.Value),
                EmissiveStrength = m.EmissiveStrength.Value,
                BaseColorTexture = FromTxTextureSlot(m.BaseColorTexture),
                NormalTexture = FromTxTextureSlot(m.NormalTexture),
                OcclusionTexture = FromTxTextureSlot(m.OcclusionTexture),
                RoughnessTexture = FromTxTextureSlot(m.RoughnessTexture),
                MetallicTexture = FromTxTextureSlot(m.MetallicTexture),
                EmissiveTexture = FromTxTextureSlot(m.EmissiveTexture),
            });
        }

        foreach (TxColliderMaterial cm in dto.ColliderMaterials)
        {
            model.ColliderMaterials.Add(new CrossSectionColliderMaterialModel
            {
                Name = cm.Name.Value ?? string.Empty,
                FrictionCoefficient = cm.FrictionCoefficient.Value,
                MaxRecoveryVelocity = cm.MaxRecoveryVelocity.Value,
                SpringFrequency = cm.SpringFrequency.Value,
                SpringDampingRatio = cm.SpringDampingRatio.Value,
            });
        }

        return (model, diagnostics);
    }

    public static string Generate(CrossSectionDocument model)
    {
        ArgumentNullException.ThrowIfNull(model);

        TxCrossSection dto = new TxCrossSection();

        foreach (CrossSectionVertexModel v in model.Vertices)
        {
            TxCrossSectionVertex vDto = new TxCrossSectionVertex
            {
                Key = new TransportX.Scripting.Data.XmlValue<string>(v.Key),
                X = new TransportX.Scripting.Data.XmlValue<float>(v.X),
                Y = new TransportX.Scripting.Data.XmlValue<float>(v.Y),
                NormalX = new TransportX.Scripting.Data.XmlValue<float>(v.NormalX),
                NormalY = new TransportX.Scripting.Data.XmlValue<float>(v.NormalY),
                U = new TransportX.Scripting.Data.XmlValue<float>(v.U),
                Color = new TransportX.Scripting.Data.XmlValue<Color>(TransportXmlBridge.HexToColor(v.ColorHex, Color.White)),
            };
            dto.Vertices.Add(vDto);
        }

        foreach (CrossSectionSurfaceModel s in model.Surfaces)
        {
            TxCrossSectionSurface sDto = new TxCrossSectionSurface
            {
                VInterval = new TransportX.Scripting.Data.XmlValue<float>(s.VInterval),
                VOffset = new TransportX.Scripting.Data.XmlValue<float>(s.VOffset),
                Material = new TransportX.Scripting.Data.XmlValue<string>(s.MaterialName),
                ColliderMaterial = new TransportX.Scripting.Data.XmlValue<string?>(s.ColliderMaterialName),
            };
            foreach (string vertexKey in s.VertexKeys)
            {
                TxCrossSectionVertexReference vRef = new TxCrossSectionVertexReference
                {
                    Key = new TransportX.Scripting.Data.XmlValue<string>(vertexKey),
                };
                sDto.Vertices.Add(vRef);
            }

            dto.Surfaces.Add(sDto);
        }

        foreach (CrossSectionMaterialModel m in model.Materials)
        {
            TxMaterial mDto = new TxMaterial
            {
                Name = new TransportX.Scripting.Data.XmlValue<string>(m.Name),
                BaseColor = new TransportX.Scripting.Data.XmlValue<Color>(TransportXmlBridge.HexToColor(m.BaseColorHex, Color.White)),
                Metallic = new TransportX.Scripting.Data.XmlValue<float>(m.Metallic),
                Roughness = new TransportX.Scripting.Data.XmlValue<float>(m.Roughness),
                Emissive = new TransportX.Scripting.Data.XmlValue<Color>(TransportXmlBridge.HexToColor(m.EmissiveHex, Color.White)),
                EmissiveStrength = new TransportX.Scripting.Data.XmlValue<float>(m.EmissiveStrength),
                BaseColorTexture = ToTxTextureSlot(m.BaseColorTexture),
                NormalTexture = ToTxTextureSlot(m.NormalTexture),
                OcclusionTexture = ToTxTextureSlot(m.OcclusionTexture),
                RoughnessTexture = ToTxTextureSlot(m.RoughnessTexture),
                MetallicTexture = ToTxTextureSlot(m.MetallicTexture),
                EmissiveTexture = ToTxTextureSlot(m.EmissiveTexture),
            };
            dto.Materials.Add(mDto);
        }

        foreach (CrossSectionColliderMaterialModel cm in model.ColliderMaterials)
        {
            TxColliderMaterial cmDto = new TxColliderMaterial
            {
                Name = new TransportX.Scripting.Data.XmlValue<string>(cm.Name),
                FrictionCoefficient = new TransportX.Scripting.Data.XmlValue<float>(cm.FrictionCoefficient),
                MaxRecoveryVelocity = new TransportX.Scripting.Data.XmlValue<float>(cm.MaxRecoveryVelocity),
                SpringFrequency = new TransportX.Scripting.Data.XmlValue<float>(cm.SpringFrequency),
                SpringDampingRatio = new TransportX.Scripting.Data.XmlValue<float>(cm.SpringDampingRatio),
            };
            dto.ColliderMaterials.Add(cmDto);
        }

        return TransportXmlBridge.Serialize(dto);
    }

    private static CrossSectionTextureSlotModel FromTxTextureSlot(TxTextureSlot slot)
    {
        if (ReferenceEquals(slot, TxTextureSlot.Empty)
            || (slot.Path.Value is null && slot.SampleMode.Value == TextureSampleMode.Texture))
        {
            return CrossSectionTextureSlotModel.Empty;
        }

        return new CrossSectionTextureSlotModel(
            slot.Path.Value,
            (TextureSampleModeModel)slot.SampleMode.Value);
    }

    private static TxTextureSlot ToTxTextureSlot(CrossSectionTextureSlotModel slot)
    {
        if (slot.IsEmpty && slot.SampleMode == TextureSampleModeModel.Texture)
        {
            return TxTextureSlot.Empty;
        }

        return new TxTextureSlot
        {
            Path = new TransportX.Scripting.Data.XmlValue<string?>(slot.Path),
            SampleMode = new TransportX.Scripting.Data.XmlValue<TextureSampleMode>((TextureSampleMode)slot.SampleMode),
        };
    }
}
