using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Models.Assets;

/// <summary>
/// Represents a single record inside <c>CrossSections.txt</c>.
/// </summary>
public readonly record struct CrossSectionListEntry(string Key, string RelativePath);

/// <summary>
/// Represents a loaded <c>CrossSections.txt</c> cross-section list.
/// </summary>
public sealed class CrossSectionListDocument
{
    public string RelativePath { get; set; } = "CrossSections.txt";
    public List<CrossSectionListEntry> Entries { get; } = new();
}
