using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Models.Assets;

/// <summary>
/// Represents a single record inside <c>Sounds.txt</c>.
/// </summary>
public sealed record SoundListEntry(string Key, string RelativePath);

/// <summary>
/// Represents a loaded <c>Sounds.txt</c> resource list.
/// </summary>
public sealed class SoundListDocument
{
    public string? BundleKey { get; set; }
    public string RelativePath { get; set; } = "Sounds.txt";
    public List<SoundListEntry> Entries { get; } = new List<SoundListEntry>();
}
