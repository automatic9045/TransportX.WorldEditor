using System;
using System.Text;
using TransportX.WorldEditor.Core.Models;

namespace TransportX.WorldEditor.Serialization.Sidecar;

/// <summary>
/// Generates IDE completion stub <c>__Editor.csx</c> kept in sync with <c>World.manifest.xml</c>
/// and TransportX <c>Commander</c> globals (including <c>CrossSections</c>, <c>Sounds</c>, etc.).
/// </summary>
public static class EditorCsxGenerator
{
    public static string Generate(
        ScriptManifestModel manifest,
        string binPrefix = "../../../bin/Release/",
        string pluginsPrefix = "../../../bin/Release/Plugins/")
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(binPrefix);
        ArgumentNullException.ThrowIfNull(pluginsPrefix);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"#r \"{binPrefix}TransportX.dll\"");
        sb.AppendLine($"#r \"{binPrefix}TransportX.Extensions.dll\"");
        sb.AppendLine($"#r \"{binPrefix}TransportX.Scripting.dll\"");

        bool hasRoadTraffic = false;
        foreach (string dependency in manifest.Dependencies)
        {
            string expanded = dependency
                .Replace("$(bin)", binPrefix, StringComparison.OrdinalIgnoreCase)
                .Replace("$(plugins)", pluginsPrefix, StringComparison.OrdinalIgnoreCase);

            sb.AppendLine($"#r \"{expanded}\"");

            if (dependency.Contains("TransportX.Domains.RoadTraffic", StringComparison.OrdinalIgnoreCase))
            {
                hasRoadTraffic = true;
            }
        }

        sb.AppendLine();
        sb.AppendLine("global using System;");
        sb.AppendLine("global using System.Collections.Generic;");
        sb.AppendLine("global using System.Linq;");
        sb.AppendLine("global using System.Numerics;");
        sb.AppendLine("global using System.Text;");
        sb.AppendLine("global using System.Threading.Tasks;");
        sb.AppendLine();
        sb.AppendLine("global using TransportX;");
        sb.AppendLine("global using TransportX.Scripting;");
        sb.AppendLine("global using TransportX.Scripting.Worlds;");
        sb.AppendLine("global using TransportX.Scripting.Worlds.Commands;");
        sb.AppendLine("global using TransportX.Scripting.Worlds.Commands.Extensions;");
        sb.AppendLine("global using TransportX.Scripting.Worlds.Components;");

        if (hasRoadTraffic)
        {
            sb.AppendLine("global using TransportX.Domains.RoadTraffic.Scripting;");
            sb.AppendLine("global using TransportX.Domains.RoadTraffic.Scripting.Commands;");
        }

        sb.AppendLine();
        sb.AppendLine("ScriptWorld World => null!;");
        sb.AppendLine();
        sb.AppendLine("Avatar Avatar => null!;");
        sb.AppendLine("Background Background => null!;");
        sb.AppendLine("Camera Camera => null!;");
        sb.AppendLine("Chunks Chunks => null!;");
        sb.AppendLine("Components Components => null!;");
        sb.AppendLine("CrossSections CrossSections => null!;");
        sb.AppendLine("Debug Debug => null!;");
        sb.AppendLine("DirectionalLight DirectionalLight => null!;");
        sb.AppendLine("WorldEnvironment Environment => null!;");
        sb.AppendLine("Input Input => null!;");
        sb.AppendLine("Models Models => null!;");
        sb.AppendLine("Network Network => null!;");
        sb.AppendLine("TransportX.Scripting.Commands.Signals Signals => null!;");
        sb.AppendLine("Sounds Sounds => null!;");
        sb.AppendLine("Triggers Triggers => null!;");
        sb.AppendLine();
        sb.AppendLine("public T Component<T>() where T : class, IWorldInstantiable<T>, IComponentCommand => throw new InvalidOperationException();");

        return sb.ToString();
    }
}
