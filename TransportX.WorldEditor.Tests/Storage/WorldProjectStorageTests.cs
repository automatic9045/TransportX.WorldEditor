using System;
using System.Collections.Generic;
using System.IO;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Core.Models.Init;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TransportX.WorldEditor.Serialization.Storage;
using Xunit;

namespace TransportX.WorldEditor.Tests.Storage;

public sealed class WorldProjectStorageTests
{
    private static WorldProject CreateSampleProject(string tempRoot)
    {
        string worldDir = Path.Combine(tempRoot, "AuthorA", "TestWorld");
        WorldProject project = new WorldProject
        {
            WorldInfoFilePath = Path.Combine(tempRoot, "AuthorA.TestWorld.xml"),
            WorldDirectory = worldDir,
        };

        project.WorldInfo.Title = "テストワールド";
        project.WorldInfo.Description = "End-to-End 検証";
        project.WorldInfo.Author = "AuthorA";
        project.WorldInfo.Args.Add(@"AuthorA\TestWorld\World.csx");

        project.Manifest.Dependencies.Add("$(plugins)TransportX.Domains.RoadTraffic.dll");

        ModelListDocument modelList = new ModelListDocument { RelativePath = "Models.txt" };
        modelList.Entries.Add(new ModelListEntry { Key = "RoadSign", ModelPath = "Models/Sign.glb", Collider = ModelColliderSpec.ConvexHull() });
        project.ModelLists["Models.txt"] = modelList;

        SoundListDocument soundList = new SoundListDocument { RelativePath = "Sounds.txt" };
        soundList.Entries.Add(new SoundListEntry("Bell", "Sounds/Bell.wav"));
        project.SoundLists["Sounds.txt"] = soundList;

        LaneLayoutDocument layout = new LaneLayoutDocument { Key = "Layout1", RelativePath = "LaneLayouts/Layout1.xml" };
        layout.Lanes.Add(new LaneDefinition("Cars", LaneFlowDirections.Out, -2f, -0.12f, 2f, 2f));
        layout.Lanes.Add(new LaneDefinition("Cars", LaneFlowDirections.In, 2f, -0.12f, 2f, 2f));
        project.LaneLayouts[layout.Key] = layout;

        CrossSectionDocument cs = new CrossSectionDocument { Key = "Road1L", RelativePath = "CrossSections/Road1L.xml" };
        cs.Vertices.Add(new CrossSectionVertexModel { Key = "L", X = -4f, Y = -0.12f });
        cs.Vertices.Add(new CrossSectionVertexModel { Key = "R", X = 4f, Y = -0.12f });
        cs.Materials.Add(new CrossSectionMaterialModel { Name = "Asphalt" });
        cs.ColliderMaterials.Add(new CrossSectionColliderMaterialModel { Name = "RoadCol" });
        CrossSectionSurfaceModel surf = new CrossSectionSurfaceModel { MaterialName = "Asphalt", ColliderMaterialName = "RoadCol" };
        surf.VertexKeys.Add("L");
        surf.VertexKeys.Add("R");
        cs.Surfaces.Add(surf);
        project.CrossSections[cs.Key] = cs;

        EnvironmentProfileDocument env = new EnvironmentProfileDocument { RelativePath = "Environment1.xml" };
        env.Ibl.Intensity = 1.25f;
        project.Environments["Environment1.xml"] = env;

        project.Init.ModelLists.Add(new AssetListLoadReference(null, "Models.txt"));
        project.Init.SoundLists.Add(new AssetListLoadReference(null, "Sounds.txt"));
        project.Init.CrossSections.Add(new ResourceLoadReference("Road1L", "CrossSections/Road1L.xml"));
        project.Init.LaneTraffic.Types.Add(new LaneTrafficTypeDefinition("Cars", "自動車", "#00FFFF"));
        project.Init.LaneLayouts.Add(new ResourceLoadReference("Layout1", "LaneLayouts/Layout1.xml"));

        SplineTemplateDocument splineTpl = new SplineTemplateDocument
        {
            Key = "Spline1",
            RelativePath = "Templates/Splines/Spline1.csx",
            OutletLayoutKey = "Layout1",
        };
        splineTpl.Extrusions.Add(new SplineTemplateExtrusionRule { Key = "Road", CrossSectionKey = "Road1L" });
        project.SplineTemplates[splineTpl.Key] = splineTpl;

        JunctionTemplateDocument junTpl = new JunctionTemplateDocument
        {
            Key = "DeadEnd1",
            RelativePath = "Templates/Junctions/DeadEnd1.csx",
        };
        junTpl.Ports.Add(new JunctionPortDefinition("0", "Layout1", new PoseDegreesModel(0, 0, 0, 0, 180, 0)));
        JunctionPathTemplateModel uturn = new JunctionPathTemplateModel
        {
            Key = "Uturn",
            FromPortKey = "0",
            FromPinIndex = 1,
            ToPortKey = "0",
            ToPinIndex = 0,
        };
        uturn.Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.BezierToEnd, PoseDegreesModel.Identity, 0.7));
        junTpl.Paths.Add(uturn);
        project.JunctionTemplates[junTpl.Key] = junTpl;

        project.Placement.EnvironmentPath = "Environment1.xml";
        SplinePlacementNode s1 = new SplinePlacementNode
        {
            Id = "spline_001",
            TemplateKey = "Spline1",
            Origin = SplineOriginModel.FromChunk(ChunkIndexModel.Zero, PoseDegreesModel.Identity),
            PreBuildUserCode = "fSpline.SpeedLimit(1, 40);",
        };
        s1.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0, 50, "s_end"));
        SplineInstancePropRule prop = new SplineInstancePropRule
        {
            From = AnchoredDistance.RelativeTo("s_end", -10),
            Span = 1,
            Interval = 1,
            Count = 1,
        };
        prop.ModelKeys.Add("RoadSign");
        s1.Props.Add(prop);
        project.Placement.Splines.Add(s1);

        JunctionPlacementNode j1 = new JunctionPlacementNode
        {
            Id = "junction_001",
            TemplateKey = "DeadEnd1",
            Origin = JunctionOriginModel.FromSpline("spline_001", "0"),
        };
        project.Placement.Junctions.Add(j1);

        project.Sidecar.AnchorLabels["s_end"] = "Spline終端";
        return project;
    }

    [Fact]
    public void FullSaveLoadValidateAndDifferentialSaveWorkEndToEnd()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tx_we_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            WorldProject original = CreateSampleProject(tempRoot);
            WorldProjectSaveResult fullSave = WorldProjectStorage.Save(original, dirtyOnly: false);

            Assert.Empty(fullSave.SkippedProtectedFiles);
            Assert.Empty(fullSave.Diagnostics);
            Assert.True(File.Exists(original.WorldInfoFilePath));
            Assert.True(File.Exists(Path.Combine(original.WorldDirectory, "__Editor.csx")));
            Assert.True(File.Exists(Path.Combine(original.WorldDirectory, "__Editor.json")));
            string templatesLoadFull = Path.Combine(original.WorldDirectory, "Templates", "Load.csx");
            Assert.True(File.Exists(templatesLoadFull));
            string templatesLoadText = File.ReadAllText(templatesLoadFull);
            Assert.Contains("#load \"../__Editor.csx\"", templatesLoadText);
            Assert.Contains("#load \"Splines/Spline1.csx\"", templatesLoadText);
            Assert.Contains("#load \"Junctions/DeadEnd1.csx\"", templatesLoadText);
            string worldCsxText = File.ReadAllText(Path.Combine(original.WorldDirectory, "World.csx"));
            Assert.Contains("#load \"Templates/Load.csx\"", worldCsxText);
            Assert.DoesNotContain("#load \"Templates/Splines/", worldCsxText);
            Assert.DoesNotContain("#load \"Templates/Junctions/", worldCsxText);

            WorldProjectLoadResult loaded = WorldProjectStorage.Load(original.WorldInfoFilePath);
            Assert.DoesNotContain(loaded.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.Empty(loaded.Project.ProtectedFiles);

            IReadOnlyList<ValidationIssue> issues = WorldValidator.Validate(loaded.Project);
            Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);

            Assert.Equal("Spline終端", loaded.Project.Sidecar.AnchorLabels["s_end"]);
            Assert.Equal("fSpline.SpeedLimit(1, 40);", loaded.Project.Placement.Splines[0].PreBuildUserCode);

            loaded.Project.SplineTemplates["Spline1"].UserCode = "tSpline.SpeedLimit(1, 55);";
            loaded.Project.MarkDirty(WorldFileKind.SplineTemplateCsx, "Templates/Splines/Spline1.csx");

            WorldProjectSaveResult diffSave = WorldProjectStorage.Save(loaded.Project, dirtyOnly: true);
            Assert.Single(diffSave.WrittenFiles);
            Assert.EndsWith("Spline1.csx", diffSave.WrittenFiles[0]);
            Assert.Empty(loaded.Project.DirtyFiles);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ProtectedReadOnlyBlocksOverwriteWhenEditorGeneratedIsTampered()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tx_we_protect_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            WorldProject original = CreateSampleProject(tempRoot);
            WorldProjectStorage.Save(original, dirtyOnly: false);

            string splineTplFullPath = Path.Combine(original.WorldDirectory, "Templates", "Splines", "Spline1.csx");
            string tamperedText = string.Join('\n',
            [
                "#load \"../../__Editor.csx\"",
                "{",
                "    // <editor-generated version=\"1\">",
                "    var tSpline = Network.Templates.CreateSpline(\"Spline1\", \"Layout1\");",
                "    for (int i = 0; i < 2; i++) { tSpline.PutExtrusion(\"Road\", \"Road1L\", 5, 0.01, 2, 0); }",
                "    // </editor-generated>",
                "}",
            ]);
            File.WriteAllText(splineTplFullPath, tamperedText);

            WorldProjectLoadResult reloaded = WorldProjectStorage.Load(original.WorldInfoFilePath);
            Assert.Contains(reloaded.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.FilePath == splineTplFullPath);
            Assert.True(reloaded.Project.IsProtected("Templates/Splines/Spline1.csx"));

            reloaded.Project.MarkDirty(WorldFileKind.SplineTemplateCsx, "Templates/Splines/Spline1.csx");
            WorldProjectSaveResult saveAttempt = WorldProjectStorage.Save(reloaded.Project, dirtyOnly: true);

            Assert.Empty(saveAttempt.WrittenFiles);
            Assert.Contains("Templates/Splines/Spline1.csx", saveAttempt.SkippedProtectedFiles);
            Assert.Equal(tamperedText, File.ReadAllText(splineTplFullPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
