using System;
using System.Collections.Generic;
using System.IO;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TransportX.WorldEditor.Serialization.Xml;
using Xunit;

namespace TransportX.WorldEditor.Tests.Xml;

public sealed class XmlSerializersTests
{
    public static string FindBasicSampleRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            string candidate = Path.Combine(current, "external", "TransportX", "_out", "Data");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate external/TransportX/_out/Data from AppContext.BaseDirectory.");
    }

    [Fact]
    public void RealBasicSampleXmlFilesParseAndRoundTrip()
    {
        string dataRoot = FindBasicSampleRoot();
        string worldInfoPath = Path.Combine(dataRoot, "Automatic9045.BasicSample.xml");
        string basicSampleDir = Path.Combine(dataRoot, "Automatic9045", "BasicSample");

        (WorldInfoModel? wi, IReadOnlyList<ParseDiagnostic> wiDiag) = WorldInfoXmlSerializer.Parse(
            worldInfoPath,
            File.ReadAllText(worldInfoPath));
        Assert.Empty(wiDiag);
        Assert.NotNull(wi);
        Assert.Equal("サンプルワールド", wi!.Title);
        Assert.Equal("$(bin)TransportX.Scripting.dll", wi.Path);
        Assert.Single(wi.Args);

        (WorldInfoModel? wi2, IReadOnlyList<ParseDiagnostic> wiDiag2) = WorldInfoXmlSerializer.Parse(
            "roundtrip.xml",
            WorldInfoXmlSerializer.Generate(wi));
        Assert.Empty(wiDiag2);
        Assert.Equal(wi.Title, wi2!.Title);
        Assert.Equal(wi.Args, wi2.Args);

        string manifestPath = Path.Combine(basicSampleDir, "World.manifest.xml");
        (ScriptManifestModel? mf, IReadOnlyList<ParseDiagnostic> mfDiag) = ScriptManifestXmlSerializer.Parse(
            manifestPath,
            File.ReadAllText(manifestPath));
        Assert.Empty(mfDiag);
        Assert.Contains("$(plugins)TransportX.Domains.RoadTraffic.dll", mf!.Dependencies);

        string layoutPath = Path.Combine(basicSampleDir, "LaneLayout1.xml");
        (LaneLayoutDocument? ll, IReadOnlyList<ParseDiagnostic> llDiag) = LaneLayoutXmlSerializer.Parse(
            "Layout1",
            "LaneLayout1.xml",
            layoutPath,
            File.ReadAllText(layoutPath));
        Assert.Empty(llDiag);
        Assert.Equal(4, ll!.Lanes.Count);

        string csPath = Path.Combine(basicSampleDir, "CrossSections", "Road1L.xml");
        (CrossSectionDocument? cs, IReadOnlyList<ParseDiagnostic> csDiag) = CrossSectionXmlSerializer.Parse(
            "Road1L",
            "CrossSections/Road1L.xml",
            csPath,
            File.ReadAllText(csPath));
        Assert.Empty(csDiag);
        Assert.Equal(10, cs!.Vertices.Count);
        Assert.Equal(4, cs.Surfaces.Count);
        Assert.Equal(3, cs.Materials.Count);
        Assert.Single(cs.ColliderMaterials);

        (CrossSectionDocument? csRt, IReadOnlyList<ParseDiagnostic> csRtDiag) = CrossSectionXmlSerializer.Parse(
            "Road1L",
            "CrossSections/Road1L.xml",
            "rt.xml",
            CrossSectionXmlSerializer.Generate(cs));
        Assert.Empty(csRtDiag);
        Assert.Equal(cs.Vertices.Count, csRt!.Vertices.Count);
        Assert.Equal(cs.Materials[1].OcclusionTexture.Path, csRt.Materials[1].OcclusionTexture.Path);

        string envPath = Path.Combine(basicSampleDir, "Environment1.xml");
        (EnvironmentProfileDocument? env, IReadOnlyList<ParseDiagnostic> envDiag) = EnvironmentProfileXmlSerializer.Parse(
            "Environment1.xml",
            envPath,
            File.ReadAllText(envPath));
        Assert.Empty(envDiag);
        Assert.Equal(0.25f, env!.Ibl.Saturation, 4);
        Assert.Equal("#FFFFFF", env.Bloom.TintHex);

        (EnvironmentProfileDocument? envRt, IReadOnlyList<ParseDiagnostic> envRtDiag) = EnvironmentProfileXmlSerializer.Parse(
            "Environment1.xml",
            "rt_env.xml",
            EnvironmentProfileXmlSerializer.Generate(env));
        Assert.Empty(envRtDiag);
        Assert.Equal(env.ToneMap.Contrast, envRt!.ToneMap.Contrast, 4);
        Assert.Equal(env.Exposure.Key, envRt.Exposure.Key, 4);
    }

    [Fact]
    public void CrossSectionScreenSampleModeAndAllTextureSlotsRoundTrip()
    {
        CrossSectionDocument doc = new CrossSectionDocument
        {
            Key = "SignalDisplay",
            RelativePath = "CrossSections/SignalDisplay.xml",
        };
        doc.Vertices.Add(new CrossSectionVertexModel { Key = "A", X = -1f, Y = 0f, U = 0f, ColorHex = "#FF0000" });
        doc.Vertices.Add(new CrossSectionVertexModel { Key = "B", X = 1f, Y = 0f, U = 1f, ColorHex = "#FFFFFF" });
        CrossSectionMaterialModel mat = new CrossSectionMaterialModel
        {
            Name = "M",
            BaseColorHex = "#ABCDEF",
            Metallic = 0.4f,
            Roughness = 0.25f,
            EmissiveHex = "#112233",
            EmissiveStrength = 3.5f,
            BaseColorTexture = new CrossSectionTextureSlotModel("a.png", TextureSampleModeModel.Texture),
            NormalTexture = new CrossSectionTextureSlotModel("n.png", TextureSampleModeModel.Texture),
            OcclusionTexture = new CrossSectionTextureSlotModel("o.png", TextureSampleModeModel.Texture),
            RoughnessTexture = new CrossSectionTextureSlotModel("r.png", TextureSampleModeModel.Texture),
            MetallicTexture = new CrossSectionTextureSlotModel("m.png", TextureSampleModeModel.Texture),
            EmissiveTexture = new CrossSectionTextureSlotModel("e.png", TextureSampleModeModel.Screen),
        };
        doc.Materials.Add(mat);

        CrossSectionSurfaceModel surf = new CrossSectionSurfaceModel
        {
            VInterval = 2.0f,
            VOffset = 0.5f,
            MaterialName = "M",
            ColliderMaterialName = null,
        };
        surf.VertexKeys.Add("A");
        surf.VertexKeys.Add("B");
        doc.Surfaces.Add(surf);

        string xml = CrossSectionXmlSerializer.Generate(doc);
        (CrossSectionDocument? parsed, IReadOnlyList<ParseDiagnostic> diag) = CrossSectionXmlSerializer.Parse(
            doc.Key,
            doc.RelativePath,
            "mem.xml",
            xml);

        Assert.Empty(diag);
        Assert.NotNull(parsed);
        Assert.Equal(TextureSampleModeModel.Screen, parsed!.Materials[0].EmissiveTexture.SampleMode);
        Assert.Equal(3.5f, parsed.Materials[0].EmissiveStrength, 4);
        Assert.Null(parsed.Surfaces[0].ColliderMaterialName);
    }
}
