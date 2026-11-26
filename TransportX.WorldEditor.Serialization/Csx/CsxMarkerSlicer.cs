using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TransportX.WorldEditor.Serialization.Diagnostics;

namespace TransportX.WorldEditor.Serialization.Csx;

/// <summary>
/// Represents an id-less <c>// &lt;user-prebuild&gt;</c> block preserved verbatim inside a canonical .csx file.
/// </summary>
public sealed record CsxPreBuildBlock(
    int Index,
    int MarkerStartLine,
    int MarkerEndLine,
    string PlaceholderToken,
    string RawContent);

/// <summary>
/// Result of slicing a canonical .csx script into preprocessor directives, editor-generated code,
/// pre-build user blocks, trailing user code, and structural diagnostics.
/// </summary>
public sealed record CsxSlicedFile(
    IReadOnlyList<string> LoadDirectives,
    string EditorGeneratedCode,
    int EditorGeneratedStartLine,
    string? UserCode,
    IReadOnlyList<CsxPreBuildBlock> PreBuildBlocks,
    IReadOnlyList<ParseDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Splits a canonical TransportX .csx file into structured regions using marker comments while
/// preserving line numbers 1:1 for Roslyn diagnostics and verbatim user code blocks.
/// </summary>
public static partial class CsxMarkerSlicer
{
    public const string PlaceholderPrefix = "// __txe_prebuild_";

    [GeneratedRegex(@"^#load\s+""([^""]+)""\s*$")]
    private static partial Regex LoadDirectiveRegex();

    [GeneratedRegex(@"^//\s*<editor-generated(?:\s+version\s*=\s*""[^""]*"")?\s*>\s*$")]
    private static partial Regex EditorGeneratedStartRegex();

    [GeneratedRegex(@"^//\s*</editor-generated>\s*$")]
    private static partial Regex EditorGeneratedEndRegex();

    [GeneratedRegex(@"^//\s*<user-prebuild>\s*$")]
    private static partial Regex UserPreBuildStartRegex();

    [GeneratedRegex(@"^//\s*</user-prebuild>\s*$")]
    private static partial Regex UserPreBuildEndRegex();

    [GeneratedRegex(@"^//\s*<user-code>\s*$")]
    private static partial Regex UserCodeStartRegex();

    [GeneratedRegex(@"^//\s*</user-code>\s*$")]
    private static partial Regex UserCodeEndRegex();

    private readonly record struct LineSpan(
        int LineNumber,
        int StartOffset,
        int ContentEndOffset,
        int NextLineOffset,
        string Text);

    private enum BlockRegion
    {
        BeforeBody,
        InBodyBeforeEditor,
        InEditorGenerated,
        InUserPreBuild,
        InBodyAfterEditor,
        InUserCode,
        AfterUserCodeInBody,
        AfterBody,
    }

    /// <summary>
    /// Slices <paramref name="sourceText"/> according to the canonical .csx marker grammar.
    /// </summary>
    public static CsxSlicedFile Slice(string filePath, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(sourceText);

        List<LineSpan> lines = SplitLineSpans(sourceText);
        List<string> loadDirectives = new List<string>();
        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>();
        List<CsxPreBuildBlock> preBuildBlocks = new List<CsxPreBuildBlock>();
        List<string> editorGeneratedLines = new List<string>();

        BlockRegion region = BlockRegion.BeforeBody;
        int editorGeneratedStartLine = 0;
        int preBuildOpenIndex = -1;
        int userCodeOpenIndex = -1;
        string? userCode = null;
        bool sawEditorGenerated = false;

        for (int i = 0; i < lines.Count; i++)
        {
            LineSpan line = lines[i];
            string trimmed = line.Text.Trim();

            switch (region)
            {
                case BlockRegion.BeforeBody:
                {
                    if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    {
                        break;
                    }

                    Match loadMatch = LoadDirectiveRegex().Match(trimmed);
                    if (loadMatch.Success)
                    {
                        loadDirectives.Add(loadMatch.Groups[1].Value);
                        break;
                    }

                    if (trimmed == "{")
                    {
                        region = BlockRegion.InBodyBeforeEditor;
                        break;
                    }

                    diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Error,
                        filePath,
                        line.LineNumber,
                        1,
                        "スクリプト本体を囲むトップレベルの '{' より前に、#load またはコメント以外の記述が存在します。"));
                    break;
                }

                case BlockRegion.InBodyBeforeEditor:
                {
                    if (EditorGeneratedStartRegex().IsMatch(trimmed))
                    {
                        sawEditorGenerated = true;
                        region = BlockRegion.InEditorGenerated;
                        editorGeneratedStartLine = line.LineNumber + 1;
                        break;
                    }

                    if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (trimmed == "}")
                    {
                        diagnostics.Add(new ParseDiagnostic(
                            DiagnosticSeverity.Error,
                            filePath,
                            line.LineNumber,
                            1,
                            "'// <editor-generated>' ブロックが見つからないまま '}' に到達しました。"));
                        region = BlockRegion.AfterBody;
                        break;
                    }

                    diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Error,
                        filePath,
                        line.LineNumber,
                        1,
                        "'// <editor-generated>' より前に不正なステートメントが存在します。"));
                    break;
                }

                case BlockRegion.InEditorGenerated:
                {
                    if (EditorGeneratedStartRegex().IsMatch(trimmed))
                    {
                        diagnostics.Add(new ParseDiagnostic(
                            DiagnosticSeverity.Error,
                            filePath,
                            line.LineNumber,
                            1,
                            "'// <editor-generated>' ブロックをネストすることはできません。"));
                        editorGeneratedLines.Add(string.Empty);
                        break;
                    }

                    if (EditorGeneratedEndRegex().IsMatch(trimmed))
                    {
                        region = BlockRegion.InBodyAfterEditor;
                        break;
                    }

                    if (UserPreBuildStartRegex().IsMatch(trimmed))
                    {
                        preBuildOpenIndex = i;
                        region = BlockRegion.InUserPreBuild;
                        string placeholder = $"{PlaceholderPrefix}{preBuildBlocks.Count}__";
                        editorGeneratedLines.Add(placeholder);
                        break;
                    }

                    editorGeneratedLines.Add(line.Text);
                    break;
                }

                case BlockRegion.InUserPreBuild:
                {
                    editorGeneratedLines.Add(string.Empty);

                    if (EditorGeneratedEndRegex().IsMatch(trimmed))
                    {
                        diagnostics.Add(new ParseDiagnostic(
                            DiagnosticSeverity.Error,
                            filePath,
                            lines[preBuildOpenIndex].LineNumber,
                            1,
                            "'// <user-prebuild>' が閉じられないまま '// </editor-generated>' に到達しました。"));
                        preBuildOpenIndex = -1;
                        region = BlockRegion.InBodyAfterEditor;
                        break;
                    }

                    if (UserPreBuildStartRegex().IsMatch(trimmed))
                    {
                        diagnostics.Add(new ParseDiagnostic(
                            DiagnosticSeverity.Error,
                            filePath,
                            line.LineNumber,
                            1,
                            "'// <user-prebuild>' ブロックをネストすることはできません。"));
                        break;
                    }

                    if (UserPreBuildEndRegex().IsMatch(trimmed))
                    {
                        string rawPreBuild = RoslynCsxHelpers.DedentOneLevel(
                            ExtractInnerSlice(sourceText, lines, preBuildOpenIndex, i));
                        int blockIndex = preBuildBlocks.Count;
                        string token = $"{PlaceholderPrefix}{blockIndex}__";
                        preBuildBlocks.Add(new CsxPreBuildBlock(
                            blockIndex,
                            lines[preBuildOpenIndex].LineNumber,
                            line.LineNumber,
                            token,
                            rawPreBuild));
                        preBuildOpenIndex = -1;
                        region = BlockRegion.InEditorGenerated;
                    }

                    break;
                }

                case BlockRegion.InBodyAfterEditor:
                {
                    if (UserCodeStartRegex().IsMatch(trimmed))
                    {
                        userCodeOpenIndex = i;
                        region = BlockRegion.InUserCode;
                        break;
                    }

                    if (trimmed == "}")
                    {
                        region = BlockRegion.AfterBody;
                        break;
                    }

                    if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    {
                        break;
                    }

                    diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Error,
                        filePath,
                        line.LineNumber,
                        1,
                        "'// </editor-generated>' と '// <user-code>' または '}' の間に不正な記述があります。"));
                    break;
                }

                case BlockRegion.InUserCode:
                {
                    if (UserCodeEndRegex().IsMatch(trimmed))
                    {
                        userCode = RoslynCsxHelpers.DedentOneLevel(
                            ExtractInnerSlice(sourceText, lines, userCodeOpenIndex, i));
                        userCodeOpenIndex = -1;
                        region = BlockRegion.AfterUserCodeInBody;
                    }

                    break;
                }

                case BlockRegion.AfterUserCodeInBody:
                {
                    if (trimmed == "}")
                    {
                        region = BlockRegion.AfterBody;
                        break;
                    }

                    if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    {
                        break;
                    }

                    diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Error,
                        filePath,
                        line.LineNumber,
                        1,
                        "'// </user-code>' の後に '}' 以外の記述があります。"));
                    break;
                }

                case BlockRegion.AfterBody:
                {
                    if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    {
                        break;
                    }

                    diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Error,
                        filePath,
                        line.LineNumber,
                        1,
                        "スクリプト末尾の '}' の後に余分な記述が存在します。"));
                    break;
                }
            }
        }

        if (region == BlockRegion.BeforeBody)
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                filePath,
                lines.Count == 0 ? 1 : lines[^1].LineNumber,
                1,
                "スクリプト本体を囲むトップレベルの '{' ... '}' ブロックが見つかりません。"));
        }
        else if (region != BlockRegion.AfterBody)
        {
            if (region == BlockRegion.InUserPreBuild && 0 <= preBuildOpenIndex)
            {
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    filePath,
                    lines[preBuildOpenIndex].LineNumber,
                    1,
                    "'// <user-prebuild>' に対応する '// </user-prebuild>' が見つかりません。"));
            }

            if (region == BlockRegion.InEditorGenerated || region == BlockRegion.InUserPreBuild)
            {
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    filePath,
                    lines.Count == 0 ? 1 : lines[^1].LineNumber,
                    1,
                    "'// </editor-generated>' が見つかりません。"));
            }

            if (region == BlockRegion.InUserCode && 0 <= userCodeOpenIndex)
            {
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    filePath,
                    lines[userCodeOpenIndex].LineNumber,
                    1,
                    "'// <user-code>' に対応する '// </user-code>' が見つかりません。"));
            }

            if (!sawEditorGenerated && region == BlockRegion.InBodyBeforeEditor)
            {
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    filePath,
                    lines.Count == 0 ? 1 : lines[^1].LineNumber,
                    1,
                    "'// <editor-generated>' ブロックが見つかりません。"));
            }

            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                filePath,
                lines.Count == 0 ? 1 : lines[^1].LineNumber,
                1,
                "スクリプト本体を閉じる '}' が見つかりません。"));
        }

        string editorGeneratedCode = string.Join('\n', editorGeneratedLines);
        return new CsxSlicedFile(
            loadDirectives,
            editorGeneratedCode,
            editorGeneratedStartLine,
            userCode,
            preBuildBlocks,
            diagnostics);
    }

    private static List<LineSpan> SplitLineSpans(string sourceText)
    {
        List<LineSpan> result = new List<LineSpan>();
        int lineNumber = 1;
        int index = 0;

        while (index <= sourceText.Length)
        {
            if (index == sourceText.Length)
            {
                if (result.Count == 0)
                {
                    result.Add(new LineSpan(1, 0, 0, 0, string.Empty));
                }

                break;
            }

            int start = index;
            while (index < sourceText.Length && sourceText[index] != '\r' && sourceText[index] != '\n')
            {
                index++;
            }

            int contentEnd = index;
            if (index < sourceText.Length)
            {
                if (sourceText[index] == '\r' && index + 1 < sourceText.Length && sourceText[index + 1] == '\n')
                {
                    index += 2;
                }
                else
                {
                    index++;
                }
            }

            string text = sourceText.Substring(start, contentEnd - start);
            result.Add(new LineSpan(lineNumber, start, contentEnd, index, text));
            lineNumber++;

            if (index == sourceText.Length && 0 < index && (sourceText[index - 1] == '\n' || sourceText[index - 1] == '\r'))
            {
                break;
            }
        }

        return result;
    }

    private static string ExtractInnerSlice(string sourceText, IReadOnlyList<LineSpan> lines, int openMarkerIndex, int closeMarkerIndex)
    {
        if (closeMarkerIndex <= openMarkerIndex + 1)
        {
            return string.Empty;
        }

        int sliceStart = lines[openMarkerIndex].NextLineOffset;
        int sliceEnd = lines[closeMarkerIndex - 1].ContentEndOffset;
        if (sliceEnd < sliceStart)
        {
            return string.Empty;
        }

        return sourceText.Substring(sliceStart, sliceEnd - sliceStart);
    }
}
