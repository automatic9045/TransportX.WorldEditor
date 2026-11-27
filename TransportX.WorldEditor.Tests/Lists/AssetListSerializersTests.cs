using System.Collections.Generic;
using System.IO;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TransportX.WorldEditor.Serialization.Lists;
using TransportX.WorldEditor.Tests.Xml;
using Xunit;

namespace TransportX.WorldEditor.Tests.Lists;

public sealed class AssetListSerializersTests
{
    [Fact]
    public void RealBasicSampleModelsTxtParsesAndRoundTripsWithoutParensOnZeroArgCommands()
    {
        string dataRoot = XmlSerializersTests.FindBasicSampleRoot();
        string modelsPath = Path.Combine(dataRoot, "Automatic9045", "BasicSample", "Models.txt");
        string raw = File.ReadAllText(modelsPath);

        (ModelListDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = ModelListSerializer.Parse(
            null,
            "Models.txt",
            modelsPath,
            raw);

        Assert.Empty(diag);
        Assert.NotNull(doc);
        Assert.Equal(55, doc!.Entries.Count);

        string regenerated = ModelListSerializer.Generate(doc);
        Assert.DoesNotContain("$NonCollision()", regenerated);
        Assert.DoesNotContain("$ConvexHull()", regenerated);
        Assert.DoesNotContain("$ClosedModel()", regenerated);
        Assert.DoesNotContain("$OpenModel()", regenerated);
        Assert.DoesNotContain("$BoundingBox()", regenerated);
        Assert.DoesNotContain("$LH()", regenerated);

        (ModelListDocument? rt, IReadOnlyList<ParseDiagnostic> rtDiag) = ModelListSerializer.Parse(
            null,
            "Models.txt",
            "rt.txt",
            regenerated);
        Assert.Empty(rtDiag);
        Assert.Equal(doc.Entries.Count, rt!.Entries.Count);
        for (int i = 0; i < doc.Entries.Count; i++)
        {
            Assert.Equal(doc.Entries[i].Key, rt.Entries[i].Key);
            Assert.Equal(doc.Entries[i].ModelPath, rt.Entries[i].ModelPath);
            Assert.Equal(doc.Entries[i].Collider, rt.Entries[i].Collider);
        }
    }

    [Fact]
    public void RejectsZeroArgCommandWithEmptyParensAndSupportsAllPrimitives()
    {
        string invalid = "Tree\tModels/Tree.glb\t$ConvexHull()\n";
        (ModelListDocument? _, IReadOnlyList<ParseDiagnostic> diag) = ModelListSerializer.Parse(null, "Models.txt", "bad.txt", invalid);
        Assert.NotEmpty(diag);

        ModelListDocument doc = new ModelListDocument { RelativePath = "Models.txt" };
        ColliderMaterialParams mat = new ColliderMaterialParams(0.8f, 12.0f, 45.0f, 0.9f);
        doc.Entries.Add(new ModelListEntry
        {
            Key = "WallBox",
            ModelPath = null,
            Collider = ModelColliderSpec.BoxCollider(2f, 3f, 4f, new PoseDegreesModel(1, 2, 3, 10, 20, 30), true, mat, makeLeftHanded: false),
        });
        doc.Entries.Add(new ModelListEntry
        {
            Key = "PostCyl",
            ModelPath = null,
            Collider = ModelColliderSpec.CylinderCollider(0.25f, 3.5f, PoseDegreesModel.FromTranslation(0, 1.75, 0), false, null),
        });
        doc.Entries.Add(new ModelListEntry
        {
            Key = "BollardSphere",
            ModelPath = "Models/Bollard.glb",
            Collider = ModelColliderSpec.SphereCollider(0.4f, null, false, mat),
        });
        doc.Entries.Add(new ModelListEntry
        {
            Key = "CustomCol",
            ModelPath = "Models/Vis.glb",
            Collider = ModelColliderSpec.MeshCollider(false, "models/col.glb", mat),
        });

        string text = ModelListSerializer.Generate(doc);
        Assert.DoesNotContain("E+", text);
        Assert.DoesNotContain("E-", text);
        Assert.Contains("$LH$Box(2, 3, 4, 1, 2, 3, 10, 20, 30, 0.8, 12, 45, 0.9)", text);
        Assert.Contains("NONE", text);

        (ModelListDocument? parsed, IReadOnlyList<ParseDiagnostic> parsedDiag) = ModelListSerializer.Parse(null, "Models.txt", "gen.txt", text);
        Assert.Empty(parsedDiag);
        Assert.Equal(4, parsed!.Entries.Count);
        Assert.Equal(doc.Entries[0].Collider, parsed.Entries[0].Collider);
        Assert.Equal(doc.Entries[1].Collider, parsed.Entries[1].Collider);
        Assert.Equal(doc.Entries[2].Collider, parsed.Entries[2].Collider);
        Assert.Equal(doc.Entries[3].Collider, parsed.Entries[3].Collider);
    }

    [Fact]
    public void SoundListRoundTripsWithCommentsIgnored()
    {
        string tsv = "# header comment\nBell\tSounds/Bell.wav\t# inline comment\nHorn\tSounds/Horn.wav\n";
        (SoundListDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = SoundListSerializer.Parse("bgm", "Sounds.txt", "Sounds.txt", tsv);
        Assert.Empty(diag);
        Assert.Equal(2, doc!.Entries.Count);
        Assert.Equal("Sounds/Bell.wav", doc.Entries[0].RelativePath);

        string generated = SoundListSerializer.Generate(doc);
        (SoundListDocument? rt, IReadOnlyList<ParseDiagnostic> rtDiag) = SoundListSerializer.Parse("bgm", "Sounds.txt", "rt.txt", generated);
        Assert.Empty(rtDiag);
        Assert.Equal(doc.Entries, rt!.Entries);
    }

    [Fact]
    public void CrossSectionListRoundTripsWithCommentsIgnoredAndReportsInvalidColumns()
    {
        string tsv = "# header comment\nRoad1L\tCrossSections/Road1L.xml\t# inline comment\nCurb\tCrossSections/Curb.xml\n";
        (CrossSectionListDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = CrossSectionListSerializer.Parse("CrossSections.txt", "CrossSections.txt", tsv);
        Assert.Empty(diag);
        Assert.Equal(2, doc!.Entries.Count);
        Assert.Equal("CrossSections/Road1L.xml", doc.Entries[0].RelativePath);
        Assert.Equal("Curb\tCrossSections/Curb.xml", CrossSectionListSerializer.FormatEntryLine(doc.Entries[1]));

        string generated = CrossSectionListSerializer.Generate(doc);
        (CrossSectionListDocument? rt, IReadOnlyList<ParseDiagnostic> rtDiag) = CrossSectionListSerializer.Parse("CrossSections.txt", "rt.txt", generated);
        Assert.Empty(rtDiag);
        Assert.Equal(doc.Entries, rt!.Entries);

        (CrossSectionListDocument? _, IReadOnlyList<ParseDiagnostic> badDiag) = CrossSectionListSerializer.Parse("CrossSections.txt", "bad.txt", "OnlyOneColumn\n");
        Assert.NotEmpty(badDiag);
    }
}
