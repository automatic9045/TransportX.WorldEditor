using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Serialization.Diagnostics;

namespace TransportX.WorldEditor.Serialization.Sidecar;

/// <summary>
/// Serializes and deserializes optional editor-only sidecar metadata (<c>__Editor.json</c>).
/// </summary>
public static class EditorJsonSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    public static (EditorSidecarMetadata? Metadata, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(string filePath, string jsonText)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(jsonText);

        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>();
        try
        {
            EditorSidecarMetadata? metadata = JsonSerializer.Deserialize<EditorSidecarMetadata>(jsonText, JsonOptions);
            return (metadata ?? new EditorSidecarMetadata(), diagnostics);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Warning,
                filePath,
                ex.LineNumber.HasValue ? (int)ex.LineNumber.Value + 1 : 1,
                ex.BytePositionInLine.HasValue ? (int)ex.BytePositionInLine.Value + 1 : 1,
                $"__Editor.json の解析に失敗しました (任意ファイルのため既定値で継続します): {ex.Message}"));
            return (null, diagnostics);
        }
    }

    public static string Generate(EditorSidecarMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return JsonSerializer.Serialize(metadata, JsonOptions) + "\n";
    }
}
