using System;
using System.Collections.Generic;
using System.Text;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Serialization.Diagnostics;

namespace TransportX.WorldEditor.Serialization.Lists;

/// <summary>
/// Parses and serializes TransportX <c>CrossSections.txt</c> 2-column TSV cross-section lists.
/// </summary>
public static class CrossSectionListSerializer
{
    public static (CrossSectionListDocument? Document, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(
        string relativePath,
        string filePath,
        string text)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(text);

        List<ParseDiagnostic> diagnostics = new();
        CrossSectionListDocument doc = new()
        {
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

            doc.Entries.Add(new CrossSectionListEntry(columns[0], columns[1]));
        }

        return (doc, diagnostics);
    }

    public static string FormatEntryLine(CrossSectionListEntry entry)
    {
        return $"{entry.Key}\t{entry.RelativePath}";
    }

    public static string Generate(CrossSectionListDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        StringBuilder sb = new();
        foreach (CrossSectionListEntry entry in document.Entries)
        {
            sb.Append(FormatEntryLine(entry)).Append('\n');
        }

        return sb.ToString();
    }
}
