using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Models;

/// <summary>
/// Represents a TransportX world metadata descriptor (<c>&lt;Author&gt;.&lt;World&gt;.xml</c>).
/// </summary>
public sealed class WorldInfoModel
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Path { get; set; } = "$(bin)TransportX.Scripting.dll";
    public string? Identifier { get; set; }
    public string? AppPath { get; set; }
    public List<string> Args { get; } = new List<string>();
}
