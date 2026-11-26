using System;
using System.Collections.Generic;
using System.Xml.Linq;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TxWorldInfo = TransportX.Worlds.WorldInfo;

namespace TransportX.WorldEditor.Serialization.Xml;

/// <summary>
/// Serializes and parses TransportX <c>&lt;Author&gt;.&lt;World&gt;.xml</c> (<c>&lt;WorldInfo&gt;</c>) files
/// using TransportX's <see cref="TxWorldInfo"/> schema while preserving unexpanded path macros (<c>$(bin)</c>).
/// </summary>
public static class WorldInfoXmlSerializer
{
    public static (WorldInfoModel? Model, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(string filePath, string xmlText)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(xmlText);

        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>();
        TxWorldInfo? dto = TransportXmlBridge.Deserialize<TxWorldInfo>(filePath, xmlText, diagnostics);
        if (dto is null)
        {
            return (null, diagnostics);
        }

        // Preserve unexpanded $(bin) / $(plugins) macro tokens and explicit presence of <AppPath>,
        // because TxWorldInfo setters automatically expand macros to local absolute paths.
        XDocument rawDoc = XDocument.Parse(xmlText);
        XElement? rawRoot = rawDoc.Root;
        string? rawPath = rawRoot?.Element("Path")?.Value;
        XElement? rawAppPathEl = rawRoot?.Element("AppPath");

        WorldInfoModel model = new WorldInfoModel
        {
            Title = dto.Title,
            Description = dto.Description,
            Author = dto.Author,
            Path = rawPath ?? dto.Path,
            Identifier = dto.Identifier,
            AppPath = rawAppPathEl?.Value,
        };
        model.Args.AddRange(dto.Args);

        return (model, diagnostics);
    }

    public static string Generate(WorldInfoModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        TxWorldInfo dto = new TxWorldInfo
        {
            Title = model.Title,
            Description = model.Description,
            Author = model.Author,
            Path = model.Path,
            Identifier = model.Identifier,
            Args = model.Args.ToArray(),
        };

        string xml = TransportXmlBridge.Serialize(dto);

        // Restore unexpanded macro text and omit default local AppPath when not explicitly configured.
        XDocument doc = XDocument.Parse(xml);
        if (doc.Root is not null)
        {
            XElement? pathEl = doc.Root.Element("Path");
            if (pathEl is not null)
            {
                pathEl.Value = model.Path;
            }

            XElement? appPathEl = doc.Root.Element("AppPath");
            if (string.IsNullOrEmpty(model.AppPath))
            {
                appPathEl?.Remove();
            }
            else if (appPathEl is not null)
            {
                appPathEl.Value = model.AppPath;
            }
        }

        return doc.Declaration + "\n" + doc.Root!.ToString(SaveOptions.None) + "\n";
    }
}
