using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Core.Models.Init;
using TransportX.WorldEditor.Serialization.Csx;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TransportX.WorldEditor.Serialization.Lists;
using TransportX.WorldEditor.Serialization.Storage;
using TransportX.WorldEditor.Serialization.Xml;

namespace TransportX.WorldEditor.Serialization.Import;

/// <summary>
/// Reads non-editor TransportX world scripts and assets, reconstructs canonical editor scripts
/// and project metadata, copies assets into a new destination project, and writes the project to disk.
/// </summary>
public static class WorldProjectImporter
{
    public static WorldImportSourcePreview? ReadSourcePreview(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        string fullSourcePath;
        try
        {
            fullSourcePath = Path.GetFullPath(sourcePath);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(fullSourcePath))
        {
            return null;
        }

        string ext = Path.GetExtension(fullSourcePath);
        string sourceDir = Path.GetDirectoryName(fullSourcePath) ?? Directory.GetCurrentDirectory();

        if (string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase))
        {
            string xmlText = File.ReadAllText(fullSourcePath, Encoding.UTF8);
            (WorldInfoModel? info, _) = WorldInfoXmlSerializer.Parse(fullSourcePath, xmlText);
            if (info is null)
            {
                return null;
            }

            string mainArg = 0 < info.Args.Count ? info.Args[0] : "World.csx";
            string mainScriptFull = Path.GetFullPath(Path.Combine(sourceDir, mainArg));
            string worldDir = Path.GetDirectoryName(mainScriptFull) ?? sourceDir;
            string rawKey = Path.GetFileName(worldDir);
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                rawKey = Path.GetFileNameWithoutExtension(fullSourcePath);
            }

            return new WorldImportSourcePreview(
                fullSourcePath,
                worldDir,
                string.IsNullOrWhiteSpace(info.Author) ? "Imported" : info.Author,
                $"{rawKey}_Imported",
                string.IsNullOrWhiteSpace(info.Title) ? rawKey : info.Title,
                info.Description ?? string.Empty,
                sourceDir);
        }

        if (string.Equals(ext, ".csx", StringComparison.OrdinalIgnoreCase))
        {
            string worldDir = sourceDir;
            string rawKey = Path.GetFileName(worldDir);
            string? authorDir = Path.GetDirectoryName(worldDir);
            string suggestedAuthor = !string.IsNullOrWhiteSpace(authorDir) ? Path.GetFileName(authorDir) : "Imported";
            string? suggestedDestParent = !string.IsNullOrWhiteSpace(authorDir) ? Path.GetDirectoryName(authorDir) : Directory.GetParent(worldDir)?.FullName;

            return new WorldImportSourcePreview(
                fullSourcePath,
                worldDir,
                string.IsNullOrWhiteSpace(suggestedAuthor) ? "Imported" : suggestedAuthor,
                string.IsNullOrWhiteSpace(rawKey) ? "ImportedWorld" : $"{rawKey}_Imported",
                string.IsNullOrWhiteSpace(rawKey) ? "Imported World" : rawKey,
                string.Empty,
                suggestedDestParent ?? worldDir);
        }

        return null;
    }

    public static WorldImportResult Import(WorldImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<ParseDiagnostic> diagnostics = new();
        if (string.IsNullOrWhiteSpace(options.SourcePath))
        {
            diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, string.Empty, 1, 1, "インポート元のファイルパスが指定されていません。"));
            return FailResult(string.Empty, string.Empty, diagnostics);
        }

        string sourceFullPath = Path.GetFullPath(options.SourcePath);
        if (!File.Exists(sourceFullPath))
        {
            diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, sourceFullPath, 1, 1, $"インポート元ファイルが見つかりません: '{sourceFullPath}'"));
            return FailResult(string.Empty, string.Empty, diagnostics);
        }

        if (string.IsNullOrWhiteSpace(options.DestinationParentDirectory)
            || !IsValidFolderSegment(options.Author)
            || !IsValidFolderSegment(options.WorldKey))
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                sourceFullPath,
                1,
                1,
                "出力先フォルダ・著者名・ワールドフォルダ名は有効なフォルダ名として指定してください。"));
            return FailResult(string.Empty, string.Empty, diagnostics);
        }

        string destParentFull = Path.GetFullPath(options.DestinationParentDirectory);
        string authorTrimmed = options.Author.Trim();
        string worldKeyTrimmed = options.WorldKey.Trim();
        string destWorldDir = Path.GetFullPath(Path.Combine(destParentFull, authorTrimmed, worldKeyTrimmed));
        string destWorldInfoPath = Path.GetFullPath(Path.Combine(destParentFull, $"{authorTrimmed}.{worldKeyTrimmed}.xml"));

        if (!TryResolveSourceEntry(sourceFullPath, diagnostics, out WorldInfoModel? sourceWorldInfo, out string sourceMainScriptFull, out string sourceWorldDir))
        {
            return FailResult(destWorldInfoPath, destWorldDir, diagnostics);
        }

        if (IsSameOrSubdirectory(destWorldDir, sourceWorldDir) || IsSameOrSubdirectory(sourceWorldDir, destWorldDir))
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                destWorldDir,
                1,
                1,
                $"インポート先 '{destWorldDir}' がインポート元 '{sourceWorldDir}' と同一または包含関係にあるため中止しました。別のフォルダまたはワールド名を指定してください。"));
            return FailResult(destWorldInfoPath, destWorldDir, diagnostics);
        }

        if (!options.OverwriteExisting && (Directory.Exists(destWorldDir) || File.Exists(destWorldInfoPath)))
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                destWorldInfoPath,
                1,
                1,
                $"出力先プロジェクト '{destWorldDir}' または '{Path.GetFileName(destWorldInfoPath)}' は既に存在します。別のワールド名を指定してください。"));
            return FailResult(destWorldInfoPath, destWorldDir, diagnostics);
        }

        WorldProject project = new()
        {
            WorldInfoFilePath = destWorldInfoPath,
            WorldDirectory = destWorldDir,
        };

        project.WorldInfo = new WorldInfoModel
        {
            Title = string.IsNullOrWhiteSpace(options.Title) ? worldKeyTrimmed : options.Title.Trim(),
            Description = options.Description ?? string.Empty,
            Author = authorTrimmed,
            Path = string.IsNullOrWhiteSpace(sourceWorldInfo?.Path) ? "$(bin)TransportX.Scripting.dll" : sourceWorldInfo!.Path,
            Identifier = sourceWorldInfo?.Identifier,
            AppPath = sourceWorldInfo?.AppPath,
        };
        project.WorldInfo.Args.Add(Path.Combine(authorTrimmed, worldKeyTrimmed, "World.csx"));

        string sourceManifestFull = Path.ChangeExtension(sourceMainScriptFull, ".manifest.xml");
        if (File.Exists(sourceManifestFull))
        {
            string mfText = File.ReadAllText(sourceManifestFull, Encoding.UTF8);
            (ScriptManifestModel? parsedMf, IReadOnlyList<ParseDiagnostic> mfDiag) = ScriptManifestXmlSerializer.Parse(sourceManifestFull, mfText);
            diagnostics.AddRange(mfDiag);
            if (parsedMf is not null)
            {
                project.Manifest = parsedMf;
            }
        }

        project.Init.RelativePath = "Init.csx";
        project.Placement.RelativePath = "World.csx";

        CsxScriptImportEngine engine = new(sourceWorldDir, project, diagnostics);
        engine.Execute(sourceMainScriptFull);

        if (RoslynCsxHelpers.HasError(diagnostics))
        {
            return FailResult(destWorldInfoPath, destWorldDir, diagnostics);
        }

        AssignCanonicalTemplatePaths(project);
        RebaseExternalPaths(project, sourceWorldDir, destWorldDir, diagnostics);

        int copiedAssetCount = 0;
        if (options.CopyAssets)
        {
            copiedAssetCount = CopyWorldAssets(
                sourceWorldDir,
                destWorldDir,
                sourceFullPath,
                sourceManifestFull,
                engine.AbsorbedScripts);
        }
        else
        {
            Directory.CreateDirectory(destWorldDir);
        }

        if (0 < engine.LegacyLoadedCrossSections.Count)
        {
            WriteGeneratedCrossSectionListFromLegacyLoads(
                project,
                engine.LegacyLoadedCrossSections,
                sourceWorldDir,
                destWorldDir);
        }

        // Load Init resource models/cross-sections/layouts from destination directory when present
        List<ParseDiagnostic> resourceLoadDiag = new();
        WorldProjectStorage.LoadInitResources(project, destWorldDir, resourceLoadDiag);
        foreach (ParseDiagnostic diag in resourceLoadDiag)
        {
            diagnostics.Add(diag with { Severity = DiagnosticSeverity.Warning });
        }

        if (!string.IsNullOrEmpty(project.Placement.EnvironmentPath))
        {
            string envRel = WorldProject.NormalizeRelativePath(project.Placement.EnvironmentPath);
            string envFull = Path.GetFullPath(Path.Combine(destWorldDir, envRel));
            if (File.Exists(envFull))
            {
                string envXml = File.ReadAllText(envFull, Encoding.UTF8);
                (EnvironmentProfileDocument? envDoc, IReadOnlyList<ParseDiagnostic> envDiag) = EnvironmentProfileXmlSerializer.Parse(envRel, envFull, envXml);
                diagnostics.AddRange(envDiag);
                if (envDoc is not null)
                {
                    project.Environments[envRel] = envDoc;
                }
            }
        }

        WorldProjectSaveResult saveResult = WorldProjectStorage.SaveCanonicalScriptsAndMetadataOnly(project);
        diagnostics.AddRange(saveResult.Diagnostics);

        return new WorldImportResult(
            true,
            destWorldInfoPath,
            destWorldDir,
            project,
            diagnostics,
            project.SplineTemplates.Count,
            project.JunctionTemplates.Count,
            project.Placement.Splines.Count,
            project.Placement.Junctions.Count,
            project.Placement.Props.Count,
            engine.PreservedUserCodeStatementCount,
            copiedAssetCount);
    }

    private static bool TryResolveSourceEntry(
        string sourceFullPath,
        List<ParseDiagnostic> diagnostics,
        out WorldInfoModel? sourceWorldInfo,
        out string sourceMainScriptFull,
        out string sourceWorldDir)
    {
        sourceWorldInfo = null;
        sourceMainScriptFull = string.Empty;
        sourceWorldDir = string.Empty;

        string ext = Path.GetExtension(sourceFullPath);
        if (string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase))
        {
            string xmlText = File.ReadAllText(sourceFullPath, Encoding.UTF8);
            (WorldInfoModel? parsedInfo, IReadOnlyList<ParseDiagnostic> wiDiag) = WorldInfoXmlSerializer.Parse(sourceFullPath, xmlText);
            diagnostics.AddRange(wiDiag);
            if (parsedInfo is null)
            {
                return false;
            }

            sourceWorldInfo = parsedInfo;
            string infoDir = Path.GetDirectoryName(sourceFullPath) ?? Directory.GetCurrentDirectory();
            string mainArg = 0 < parsedInfo.Args.Count ? parsedInfo.Args[0] : "World.csx";
            sourceMainScriptFull = Path.GetFullPath(Path.Combine(infoDir, mainArg));
        }
        else if (string.Equals(ext, ".csx", StringComparison.OrdinalIgnoreCase))
        {
            sourceMainScriptFull = sourceFullPath;
        }
        else
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                sourceFullPath,
                1,
                1,
                $"サポートされていないインポート元拡張子です: '{ext}' (.xml または .csx を指定してください)"));
            return false;
        }

        if (!File.Exists(sourceMainScriptFull))
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                sourceMainScriptFull,
                1,
                1,
                $"エントリのワールドスクリプトが見つかりません: '{sourceMainScriptFull}'"));
            return false;
        }

        sourceWorldDir = Path.GetDirectoryName(sourceMainScriptFull) ?? Directory.GetCurrentDirectory();
        return true;
    }

    private static void AssignCanonicalTemplatePaths(WorldProject project)
    {
        HashSet<string> usedRelPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (Core.Models.Templates.SplineTemplateDocument st in project.SplineTemplates.Values)
        {
            string sanitized = RoslynCsxHelpers.SanitizeFileName(st.Key, "Spline");
            string candidate = $"Templates/Splines/{sanitized}.csx";
            int suffix = 2;
            while (!usedRelPaths.Add(candidate))
            {
                candidate = $"Templates/Splines/{sanitized}_{suffix}.csx";
                suffix++;
            }

            st.RelativePath = candidate;
        }

        foreach (Core.Models.Templates.JunctionTemplateDocument jt in project.JunctionTemplates.Values)
        {
            string sanitized = RoslynCsxHelpers.SanitizeFileName(jt.Key, "Junction");
            string candidate = $"Templates/Junctions/{sanitized}.csx";
            int suffix = 2;
            while (!usedRelPaths.Add(candidate))
            {
                candidate = $"Templates/Junctions/{sanitized}_{suffix}.csx";
                suffix++;
            }

            jt.RelativePath = candidate;
        }
    }

    private static void RebaseExternalPaths(
        WorldProject project,
        string sourceWorldDir,
        string destWorldDir,
        List<ParseDiagnostic> diagnostics)
    {
        if (project.Placement.Avatar is not null
            && TryRebaseSinglePath(project.Placement.Avatar.XmlPath, sourceWorldDir, destWorldDir, out string rebasedAvatar))
        {
            project.Placement.Avatar.XmlPath = rebasedAvatar;
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Info,
                sourceWorldDir,
                1,
                1,
                $"外部アバターパスを再解決しました: {rebasedAvatar}"));
        }

        if (!string.IsNullOrWhiteSpace(project.Placement.EnvironmentPath)
            && TryRebaseSinglePath(project.Placement.EnvironmentPath, sourceWorldDir, destWorldDir, out string rebasedEnv))
        {
            project.Placement.EnvironmentPath = rebasedEnv;
        }

        for (int i = 0; i < project.Init.ModelLists.Count; i++)
        {
            AssetListLoadReference item = project.Init.ModelLists[i];
            if (TryRebaseSinglePath(item.RelativePath, sourceWorldDir, destWorldDir, out string rebased))
            {
                project.Init.ModelLists[i] = item with { RelativePath = rebased };
            }
        }

        for (int i = 0; i < project.Init.SoundLists.Count; i++)
        {
            AssetListLoadReference item = project.Init.SoundLists[i];
            if (TryRebaseSinglePath(item.RelativePath, sourceWorldDir, destWorldDir, out string rebased))
            {
                project.Init.SoundLists[i] = item with { RelativePath = rebased };
            }
        }

        for (int i = 0; i < project.Init.CrossSectionLists.Count; i++)
        {
            string listPath = project.Init.CrossSectionLists[i];
            if (TryRebaseSinglePath(listPath, sourceWorldDir, destWorldDir, out string rebased))
            {
                project.Init.CrossSectionLists[i] = rebased;
            }
        }

        for (int i = 0; i < project.Init.LaneLayouts.Count; i++)
        {
            ResourceLoadReference item = project.Init.LaneLayouts[i];
            if (TryRebaseSinglePath(item.RelativePath, sourceWorldDir, destWorldDir, out string rebased))
            {
                project.Init.LaneLayouts[i] = item with { RelativePath = rebased };
            }
        }
    }

    private static void WriteGeneratedCrossSectionListFromLegacyLoads(
        WorldProject project,
        IReadOnlyList<CrossSectionListEntry> entries,
        string sourceWorldDir,
        string destWorldDir)
    {
        string listRelPath = ResolveUniqueCrossSectionListPath(project, destWorldDir);
        CrossSectionListDocument listDoc = new()
        {
            RelativePath = listRelPath,
        };

        foreach (CrossSectionListEntry entry in entries)
        {
            string entryRelPath = TryRebaseSinglePath(entry.RelativePath, sourceWorldDir, destWorldDir, out string rebased)
                ? rebased
                : entry.RelativePath;
            listDoc.Entries.Add(new CrossSectionListEntry(entry.Key, entryRelPath));
        }

        project.Init.CrossSectionLists.Add(listRelPath);
        string listFullPath = Path.Combine(destWorldDir, listRelPath);
        WorldProjectStorage.WriteUtf8File(listFullPath, CrossSectionListSerializer.Generate(listDoc));
    }

    private static string ResolveUniqueCrossSectionListPath(WorldProject project, string destWorldDir)
    {
        string candidate = "CrossSections.txt";
        int suffix = 1;
        while (project.Init.CrossSectionLists.Any(p => string.Equals(WorldProject.NormalizeRelativePath(p), candidate, StringComparison.OrdinalIgnoreCase))
            || File.Exists(Path.Combine(destWorldDir, candidate)))
        {
            candidate = suffix == 1 ? "CrossSections_Imported.txt" : $"CrossSections_Imported{suffix}.txt";
            suffix++;
        }

        return candidate;
    }

    private static bool TryRebaseSinglePath(
        string? rawRelativePath,
        string sourceWorldDir,
        string destWorldDir,
        out string rebasedPath)
    {
        rebasedPath = rawRelativePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawRelativePath))
        {
            return false;
        }

        string fullResolved = Path.GetFullPath(Path.Combine(sourceWorldDir, rawRelativePath));
        if (IsSameOrSubdirectory(fullResolved, sourceWorldDir))
        {
            return false;
        }

        if (!File.Exists(fullResolved) && !Directory.Exists(fullResolved))
        {
            return false;
        }

        rebasedPath = Path.GetRelativePath(destWorldDir, fullResolved).Replace('\\', '/');
        return true;
    }

    private static int CopyWorldAssets(
        string sourceWorldDir,
        string destWorldDir,
        string sourceEntryPath,
        string sourceManifestFull,
        IReadOnlySet<string> absorbedScripts)
    {
        Directory.CreateDirectory(destWorldDir);
        int copiedCount = 0;

        foreach (string srcFile in Directory.EnumerateFiles(sourceWorldDir, "*", SearchOption.AllDirectories))
        {
            string fullSrc = Path.GetFullPath(srcFile);
            if (absorbedScripts.Contains(fullSrc))
            {
                continue;
            }

            if (string.Equals(fullSrc, sourceEntryPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullSrc, sourceManifestFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fileName = Path.GetFileName(fullSrc);
            if (string.Equals(fileName, "__Editor.csx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "__Editor.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relPath = Path.GetRelativePath(sourceWorldDir, fullSrc);
            string destFile = Path.Combine(destWorldDir, relPath);
            string? destSubDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destSubDir))
            {
                Directory.CreateDirectory(destSubDir);
            }

            File.Copy(fullSrc, destFile, overwrite: true);
            copiedCount++;
        }

        return copiedCount;
    }

    private static bool IsSameOrSubdirectory(string candidatePath, string rootPath)
    {
        string normCandidate = AppendTrailingSeparator(Path.GetFullPath(candidatePath));
        string normRoot = AppendTrailingSeparator(Path.GetFullPath(rootPath));
        return normCandidate.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static bool IsValidFolderSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed == "." || trimmed == ".." || trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            return false;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        return trimmed.IndexOfAny(invalid) < 0;
    }

    private static WorldImportResult FailResult(string destWorldInfoPath, string destWorldDir, IReadOnlyList<ParseDiagnostic> diagnostics)
    {
        return new WorldImportResult(
            false,
            destWorldInfoPath,
            destWorldDir,
            null,
            diagnostics,
            0,
            0,
            0,
            0,
            0,
            0,
            0);
    }
}
