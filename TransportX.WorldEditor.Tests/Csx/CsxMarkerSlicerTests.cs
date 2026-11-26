using System;
using TransportX.WorldEditor.Serialization.Csx;
using Xunit;

namespace TransportX.WorldEditor.Tests.Csx;

public sealed class CsxMarkerSlicerTests
{
    [Fact]
    public void Slice_ExtractsDirectivesEditorBlockPreBuildsAndUserCodeWithExactLineNumbers_AndDedentsOneLevel()
    {
        string script = string.Join('\n',
            "#load \"__Editor.csx\"",
            "#load \"Init.csx\"",
            "{",
            "    // <editor-generated version=\"1\">",
            "    var fSpline_001 = Chunks[0, 0].BeginSpline(\"Spline1\", 0, 0, 0);",
            "    fSpline_001.Curves.Straight(100, out var s_001);",
            "    // <user-prebuild>",
            "    fSpline_001.SpeedLimit(1, 50)",
            "        .SpeedLimit(2, 50);",
            "    // </user-prebuild>",
            "    var spline_001 = fSpline_001.Build();",
            "    // <user-prebuild>",
            "    fJunction_001.Paths[\"NE_L\"].TrafficDensity(0);",
            "    // </user-prebuild>",
            "    var junction_001 = fJunction_001.Build();",
            "    // </editor-generated>",
            "",
            "    // <user-code>",
            "    Triggers.OnStart(() =>",
            "    {",
            "        Debug.ShowDialog(\"Ready\");",
            "    });",
            "    // </user-code>",
            "}");

        CsxSlicedFile sliced = CsxMarkerSlicer.Slice("World.csx", script);

        Assert.False(sliced.HasErrors);
        Assert.Equal(["__Editor.csx", "Init.csx"], sliced.LoadDirectives);
        Assert.Equal(5, sliced.EditorGeneratedStartLine);

        Assert.Equal(2, sliced.PreBuildBlocks.Count);
        Assert.Equal(7, sliced.PreBuildBlocks[0].MarkerStartLine);
        Assert.Equal(10, sliced.PreBuildBlocks[0].MarkerEndLine);
        Assert.Equal("fSpline_001.SpeedLimit(1, 50)\n    .SpeedLimit(2, 50);", sliced.PreBuildBlocks[0].RawContent);

        Assert.Equal(12, sliced.PreBuildBlocks[1].MarkerStartLine);
        Assert.Equal(14, sliced.PreBuildBlocks[1].MarkerEndLine);
        Assert.Equal("fJunction_001.Paths[\"NE_L\"].TrafficDensity(0);", sliced.PreBuildBlocks[1].RawContent);

        string[] editorLines = sliced.EditorGeneratedCode.Split('\n');
        Assert.Equal(11, editorLines.Length);
        Assert.Equal("// __txe_prebuild_0__", editorLines[2]);
        Assert.Equal(string.Empty, editorLines[3]);
        Assert.Equal(string.Empty, editorLines[4]);
        Assert.Equal(string.Empty, editorLines[5]);
        Assert.Equal("    var spline_001 = fSpline_001.Build();", editorLines[6]);
        Assert.Equal("// __txe_prebuild_1__", editorLines[7]);
        Assert.Equal("    var junction_001 = fJunction_001.Build();", editorLines[10]);

        Assert.Equal("Triggers.OnStart(() =>\n{\n    Debug.ShowDialog(\"Ready\");\n});", sliced.UserCode);
    }

    [Fact]
    public void Slice_AllowsOptionalUserCodeToBeOmitted()
    {
        string script = string.Join('\n',
            "#load \"../../__Editor.csx\"",
            "{",
            "    // <editor-generated>",
            "    var tSpline = Network.Templates.CreateSpline(\"Spline1\", \"Layout1\");",
            "    // </editor-generated>",
            "}");

        CsxSlicedFile sliced = CsxMarkerSlicer.Slice("Spline1.csx", script);

        Assert.False(sliced.HasErrors);
        Assert.Single(sliced.LoadDirectives);
        Assert.Null(sliced.UserCode);
        Assert.Empty(sliced.PreBuildBlocks);
    }

    [Theory]
    [InlineData("var x = 1;")]
    [InlineData("{\n    var x = 1;\n}")]
    [InlineData("{\n    // <editor-generated>\n    // <user-prebuild>\n    x();\n    // </editor-generated>\n}")]
    [InlineData("{\n    // <editor-generated>\n    // </editor-generated>\n    // <user-code>\n}")]
    public void Slice_ReportsDiagnosticsForMalformedStructure(string invalidScript)
    {
        CsxSlicedFile sliced = CsxMarkerSlicer.Slice("Invalid.csx", invalidScript);

        Assert.True(sliced.HasErrors);
        Assert.NotEmpty(sliced.Diagnostics);
    }
}
