using System;
using System.Collections.Generic;
using System.Drawing;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TxEnvironmentProfile = TransportX.Scripting.Data.Environment.EnvironmentProfile;

namespace TransportX.WorldEditor.Serialization.Xml;

/// <summary>
/// Serializes and parses TransportX <c>&lt;EnvironmentProfile&gt;</c> XML documents
/// via TransportX's <see cref="TxEnvironmentProfile"/> schema DTO.
/// </summary>
public static class EnvironmentProfileXmlSerializer
{
    public static (EnvironmentProfileDocument? Model, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(
        string relativePath,
        string filePath,
        string xmlText)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(xmlText);

        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>();
        TxEnvironmentProfile? dto = TransportXmlBridge.Deserialize<TxEnvironmentProfile>(filePath, xmlText, diagnostics);
        if (dto is null)
        {
            return (null, diagnostics);
        }

        TransportXmlBridge.AppendErrors(dto.Errors, filePath, diagnostics);

        EnvironmentProfileDocument model = new EnvironmentProfileDocument
        {
            RelativePath = relativePath,
            Ibl = new IblSettings
            {
                DiffuseTexturePath = dto.IBL.DiffuseTexturePath.Value,
                SpecularTexturePath = dto.IBL.SpecularTexturePath.Value,
                Intensity = dto.IBL.Intensity.Value,
                Saturation = dto.IBL.Saturation.Value,
            },
            Exposure = new ExposureSettings
            {
                Key = dto.Exposure.Key.Value,
                Min = dto.Exposure.Min.Value,
                Max = dto.Exposure.Max.Value,
                DarkAdaptationSpeed = dto.Exposure.DarkAdaptationSpeed.Value,
                LightAdaptationSpeed = dto.Exposure.LightAdaptationSpeed.Value,
            },
            ToneMap = new ToneMapSettings
            {
                Contrast = dto.ToneMap.Contrast.Value,
                Shoulder = dto.ToneMap.Shoulder.Value,
                MaxLuminance = dto.ToneMap.MaxLuminance.Value,
                MidtoneScale = dto.ToneMap.MidtoneScale.Value,
            },
            Bloom = new BloomSettings
            {
                Threshold = dto.Bloom.Threshold.Value,
                Intensity = dto.Bloom.Intensity.Value,
                Scatter = dto.Bloom.Scatter.Value,
                SoftKnee = dto.Bloom.SoftKnee.Value,
                TintHex = TransportXmlBridge.ColorToHex(dto.Bloom.Tint.Value),
            },
        };

        return (model, diagnostics);
    }

    public static string Generate(EnvironmentProfileDocument model)
    {
        ArgumentNullException.ThrowIfNull(model);

        TxEnvironmentProfile dto = new TxEnvironmentProfile();
        dto.IBL.DiffuseTexturePath = new TransportX.Scripting.Data.XmlValue<string?>(model.Ibl.DiffuseTexturePath);
        dto.IBL.SpecularTexturePath = new TransportX.Scripting.Data.XmlValue<string?>(model.Ibl.SpecularTexturePath);
        dto.IBL.Intensity = new TransportX.Scripting.Data.XmlValue<float>(model.Ibl.Intensity);
        dto.IBL.Saturation = new TransportX.Scripting.Data.XmlValue<float>(model.Ibl.Saturation);

        dto.Exposure.Key = new TransportX.Scripting.Data.XmlValue<float>(model.Exposure.Key);
        dto.Exposure.Min = new TransportX.Scripting.Data.XmlValue<float>(model.Exposure.Min);
        dto.Exposure.Max = new TransportX.Scripting.Data.XmlValue<float>(model.Exposure.Max);
        dto.Exposure.DarkAdaptationSpeed = new TransportX.Scripting.Data.XmlValue<float>(model.Exposure.DarkAdaptationSpeed);
        dto.Exposure.LightAdaptationSpeed = new TransportX.Scripting.Data.XmlValue<float>(model.Exposure.LightAdaptationSpeed);

        dto.ToneMap.Contrast = new TransportX.Scripting.Data.XmlValue<float>(model.ToneMap.Contrast);
        dto.ToneMap.Shoulder = new TransportX.Scripting.Data.XmlValue<float>(model.ToneMap.Shoulder);
        dto.ToneMap.MaxLuminance = new TransportX.Scripting.Data.XmlValue<float>(model.ToneMap.MaxLuminance);
        dto.ToneMap.MidtoneScale = new TransportX.Scripting.Data.XmlValue<float>(model.ToneMap.MidtoneScale);

        dto.Bloom.Threshold = new TransportX.Scripting.Data.XmlValue<float>(model.Bloom.Threshold);
        dto.Bloom.Intensity = new TransportX.Scripting.Data.XmlValue<float>(model.Bloom.Intensity);
        dto.Bloom.Scatter = new TransportX.Scripting.Data.XmlValue<float>(model.Bloom.Scatter);
        dto.Bloom.SoftKnee = new TransportX.Scripting.Data.XmlValue<float>(model.Bloom.SoftKnee);
        dto.Bloom.Tint = new TransportX.Scripting.Data.XmlValue<Color>(TransportXmlBridge.HexToColor(model.Bloom.TintHex, Color.White));

        return TransportXmlBridge.Serialize(dto);
    }
}
