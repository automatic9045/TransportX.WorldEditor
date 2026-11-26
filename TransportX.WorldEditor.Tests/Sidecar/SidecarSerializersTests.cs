using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TransportX.WorldEditor.Serialization.Sidecar;
using Xunit;

namespace TransportX.WorldEditor.Tests.Sidecar;

public sealed class SidecarSerializersTests
{
    [Fact]
    public void EditorJsonRoundTripsAllMetadata()
    {
        EditorSidecarMetadata meta = new EditorSidecarMetadata
        {
            SchemaVersion = 1,
            LastCameraPose = new CameraBookmarkModel("Last", 1, -2, 120.5, 18.0, 240.0, 15.0, 45.0, 0.0),
            VisibilityLayers = new VisibilityLayerSettings(true, true, true, false, true),
        };
        meta.CameraBookmarks.Add(new CameraBookmarkModel("交差点1", 0, 0, 10.0, 25.0, 80.0, 45.0, 0.0, 0.0));
        meta.HiddenNodeIds.Add("spline_003");
        meta.BackgroundMaps.Add(new BackgroundMapReference("航空写真", "Ref/Map.png", 0, 0, 0, -0.5, 0, 500, 500, 0.5, true));
        meta.AnchorLabels["s_001"] = "Spline1終端";

        string json = EditorJsonSerializer.Generate(meta);
        (EditorSidecarMetadata? rt, IReadOnlyList<ParseDiagnostic> diag) = EditorJsonSerializer.Parse("__Editor.json", json);

        Assert.Empty(diag);
        Assert.NotNull(rt);
        Assert.Equal(meta.LastCameraPose, rt!.LastCameraPose);
        Assert.Single(rt.CameraBookmarks);
        Assert.Equal("交差点1", rt.CameraBookmarks[0].Name);
        Assert.True(rt.VisibilityLayers.Colliders);
        Assert.False(rt.VisibilityLayers.ChunkGrid);
        Assert.Equal(["spline_003"], rt.HiddenNodeIds);
        Assert.Single(rt.BackgroundMaps);
        Assert.Equal("Spline1終端", rt.AnchorLabels["s_001"]);
    }

    [Fact]
    public void EditorCsxGeneratorIncludesCrossSectionsAndManifestPlugins()
    {
        ScriptManifestModel manifest = new ScriptManifestModel();
        manifest.Dependencies.Add("$(plugins)TransportX.Domains.RoadTraffic.dll");

        string csx = EditorCsxGenerator.Generate(manifest);

        Assert.Contains("#r \"../../../bin/Release/TransportX.dll\"", csx);
        Assert.Contains("#r \"../../../bin/Release/Plugins/TransportX.Domains.RoadTraffic.dll\"", csx);
        Assert.Contains("global using TransportX.Domains.RoadTraffic.Scripting;", csx);
        Assert.Contains("CrossSections CrossSections => null!;", csx);
        Assert.Contains("Sounds Sounds => null!;", csx);
        Assert.Contains("TransportX.Scripting.Commands.Signals Signals => null!;", csx);
        Assert.Contains("public T Component<T>() where T : class, IWorldInstantiable<T>, IComponentCommand", csx);
    }
}
