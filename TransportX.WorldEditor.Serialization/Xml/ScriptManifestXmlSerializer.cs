using System;
using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TxDependency = TransportX.Scripting.Data.Scripts.Dependency;
using TxScriptManifest = TransportX.Scripting.Data.Scripts.ScriptManifest;

namespace TransportX.WorldEditor.Serialization.Xml;

/// <summary>
/// Serializes and parses <c>World.manifest.xml</c> via TransportX's <see cref="TxScriptManifest"/>.
/// </summary>
public static class ScriptManifestXmlSerializer
{
    public static (ScriptManifestModel? Model, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(string filePath, string xmlText)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(xmlText);

        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>();
        TxScriptManifest? dto = TransportXmlBridge.Deserialize<TxScriptManifest>(filePath, xmlText, diagnostics);
        if (dto is null)
        {
            return (null, diagnostics);
        }

        ScriptManifestModel model = new ScriptManifestModel();
        foreach (TxDependency dep in dto.Dependencies)
        {
            TransportXmlBridge.AppendErrors(dep.Errors, filePath, diagnostics);
            if (!string.IsNullOrEmpty(dep.Path.Value))
            {
                model.Dependencies.Add(dep.Path.Value);
            }
        }

        return (model, diagnostics);
    }

    public static string Generate(ScriptManifestModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        TxScriptManifest dto = new TxScriptManifest();
        foreach (string depPath in model.Dependencies)
        {
            TxDependency dep = new TxDependency
            {
                Path = new TransportX.Scripting.Data.XmlValue<string>(depPath),
            };
            dto.Dependencies.Add(dep);
        }

        return TransportXmlBridge.Serialize(dto);
    }
}
