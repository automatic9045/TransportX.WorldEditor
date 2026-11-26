using System;
using System.Collections.Generic;
using System.Text;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Serialization.Diagnostics;

namespace TransportX.WorldEditor.Serialization.Lists;

/// <summary>
/// Parses and serializes TransportX <c>Sounds.txt</c> 2-column TSV asset lists.
/// </summary>
public static class SoundListSerializer
{
    public static (SoundListDocument? Document, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(
        string? bundleKey,
        string relativePath,
        string filePath,
        string text)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(text);

        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>();
        SoundListDocument doc = new SoundListDocument
        {
            BundleKey = bundleKey,
            RelativePath = relativePath,
        };

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            int lineNumber = lineIndex + 1;
            if (!ModelListSerializer.TrySplitTsvLine(lines[lineIndex], [2], filePath, lineNumber, diagnostics, out string[] columns))
            {
                continue;
            }

            doc.Entries.Add(new SoundListEntry(columns[0], columns[1]));
        }

        return (doc, diagnostics);
    }

    public static string Generate(SoundListDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        StringBuilder sb = new StringBuilder();
        foreach (SoundListEntry entry in document.Entries)
        {
            sb.Append(entry.Key).Append('\t').Append(entry.RelativePath).Append('\n');
        }

        return sb.ToString();
    }
}
