using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Models;

/// <summary>
/// Represents a <c>World.manifest.xml</c> assembly dependency manifest.
/// </summary>
public sealed class ScriptManifestModel
{
    public List<string> Dependencies { get; } = new List<string>();
}
