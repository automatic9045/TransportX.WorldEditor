using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Core.Models.Init;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Serialization.Csx;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TransportX.WorldEditor.Serialization.Lists;
using TransportX.WorldEditor.Serialization.Sidecar;
using TransportX.WorldEditor.Serialization.Xml;

namespace TransportX.WorldEditor.Serialization.Storage;

/// <summary>
/// Result of loading a complete TransportX world folder into a <see cref="WorldProject"/>.
/// </summary>
public sealed record WorldProjectLoadResult(
    WorldProject Project,
    IReadOnlyList<ParseDiagnostic> Diagnostics);

/// <summary>
/// Result of saving modified or full files of a <see cref="WorldProject"/> to disk.
/// </summary>
public sealed record WorldProjectSaveResult(
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> SkippedProtectedFiles,
    IReadOnlyList<ParseDiagnostic> Diagnostics);

/// <summary>
/// Result of reloading a single externally modified world file into an open <see cref="WorldProject"/>.
/// </summary>
public sealed record ExternalFileReloadResult(
    bool Handled,
    bool Reloaded,
    bool SkippedDirty,
    WorldFileKind Kind,
    string RelativePath,
    string? Message,
    IReadOnlyList<ParseDiagnostic> Diagnostics);

/// <summary>
/// Orchestrates loading and differential/protected saving of all TransportX world files
/// (WorldInfo XML, Manifest XML, canonical <c>.csx</c> files, asset TSVs, CrossSection/LaneLayout/Environment XMLs,
/// <c>__Editor.csx</c>, and <c>__Editor.json</c>).
/// </summary>
public static class WorldProjectStorage
{
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static WorldProjectLoadResult Load(string worldInfoPath)
    {
        ArgumentNullException.ThrowIfNull(worldInfoPath);

        string fullWorldInfoPath = Path.GetFullPath(worldInfoPath);
        string worldInfoDir = Path.GetDirectoryName(fullWorldInfoPath) ?? Directory.GetCurrentDirectory();

        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>();
        WorldProject project = new WorldProject
        {
            WorldInfoFilePath = fullWorldInfoPath,
            WorldDirectory = worldInfoDir,
        };

        if (!File.Exists(fullWorldInfoPath))
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                fullWorldInfoPath,
                1,
                1,
                $"ワールド情報ファイルが見つかりません: '{fullWorldInfoPath}'"));
            return new WorldProjectLoadResult(project, diagnostics);
        }

        string worldInfoText = File.ReadAllText(fullWorldInfoPath, Encoding.UTF8);
        (WorldInfoModel? worldInfo, IReadOnlyList<ParseDiagnostic> wiDiagnostics) = WorldInfoXmlSerializer.Parse(fullWorldInfoPath, worldInfoText);
        diagnostics.AddRange(wiDiagnostics);
        if (worldInfo is null)
        {
            return new WorldProjectLoadResult(project, diagnostics);
        }

        project.WorldInfo = worldInfo;
        string mainScriptRelArg = 0 < worldInfo.Args.Count ? worldInfo.Args[0] : "World.csx";
        string mainScriptFullPath = Path.GetFullPath(Path.Combine(worldInfoDir, mainScriptRelArg));
        string worldDir = Path.GetDirectoryName(mainScriptFullPath) ?? worldInfoDir;
        project.WorldDirectory = worldDir;

        string worldCsxRelPath = WorldProject.NormalizeRelativePath(Path.GetRelativePath(worldDir, mainScriptFullPath));
        project.Placement.RelativePath = worldCsxRelPath;

        string manifestFullPath = Path.ChangeExtension(mainScriptFullPath, ".manifest.xml");
        if (File.Exists(manifestFullPath))
        {
            string manifestText = File.ReadAllText(manifestFullPath, Encoding.UTF8);
            (ScriptManifestModel? manifest, IReadOnlyList<ParseDiagnostic> mfDiag) = ScriptManifestXmlSerializer.Parse(manifestFullPath, manifestText);
            diagnostics.AddRange(mfDiag);
            if (manifest is not null)
            {
                project.Manifest = manifest;
            }
        }

        string editorJsonPath = Path.Combine(worldDir, "__Editor.json");
        if (File.Exists(editorJsonPath))
        {
            string jsonText = File.ReadAllText(editorJsonPath, Encoding.UTF8);
            (EditorSidecarMetadata? sidecar, IReadOnlyList<ParseDiagnostic> jsonDiag) = EditorJsonSerializer.Parse(editorJsonPath, jsonText);
            diagnostics.AddRange(jsonDiag);
            if (sidecar is not null)
            {
                project.Sidecar = sidecar;
            }
        }

        IReadOnlyList<string> loadDirectives = Array.Empty<string>();
        if (File.Exists(mainScriptFullPath))
        {
            string worldCsxText = File.ReadAllText(mainScriptFullPath, Encoding.UTF8);
            (ParsedWorldCsx? parsedWorld, IReadOnlyList<ParseDiagnostic> worldDiag) = WorldCsxSerializer.Parse(mainScriptFullPath, worldCsxText, worldCsxRelPath);
            diagnostics.AddRange(worldDiag);

            if (parsedWorld is not null)
            {
                project.Placement = parsedWorld.Document;
                loadDirectives = parsedWorld.LoadDirectives;
            }
            else
            {
                project.ProtectedFiles.Add(worldCsxRelPath);
                CsxSlicedFile fallbackSlice = CsxMarkerSlicer.Slice(mainScriptFullPath, worldCsxText);
                loadDirectives = fallbackSlice.LoadDirectives;
                WorldCsxSerializer.PopulateFallback(worldCsxText, worldCsxRelPath, project.Placement);
            }
        }

        string initDir = worldDir;
        foreach (string rawLoad in loadDirectives)
        {
            string normalizedLoad = WorldProject.NormalizeRelativePath(rawLoad);
            if (string.Equals(Path.GetFileName(normalizedLoad), "__Editor.csx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string targetFullPath = Path.GetFullPath(Path.Combine(worldDir, normalizedLoad));
            if (!File.Exists(targetFullPath))
            {
                project.Placement.ExtraLoadScripts.Add(normalizedLoad);
                continue;
            }

            string scriptText = File.ReadAllText(targetFullPath, Encoding.UTF8);
            if (IsInitScript(normalizedLoad, scriptText))
            {
                initDir = Path.GetDirectoryName(targetFullPath) ?? worldDir;
                (InitDocument? initDoc, IReadOnlyList<ParseDiagnostic> initDiag) = InitCsxSerializer.Parse(targetFullPath, scriptText, normalizedLoad);
                diagnostics.AddRange(initDiag);
                if (initDoc is not null)
                {
                    project.Init = initDoc;
                }
                else
                {
                    project.ProtectedFiles.Add(normalizedLoad);
                    InitCsxSerializer.PopulateFallback(scriptText, normalizedLoad, project.Init);
                }
            }
            else if (IsTemplatesLoadScript(normalizedLoad))
            {
                LoadTemplatesFromLoadScript(project, worldDir, targetFullPath, scriptText, diagnostics);
            }
            else
            {
                project.Placement.ExtraLoadScripts.Add(normalizedLoad);
            }
        }

        LoadInitResources(project, initDir, diagnostics);

        if (!string.IsNullOrEmpty(project.Placement.EnvironmentPath))
        {
            string envRel = WorldProject.NormalizeRelativePath(project.Placement.EnvironmentPath);
            string envFullPath = Path.GetFullPath(Path.Combine(worldDir, envRel));
            if (File.Exists(envFullPath))
            {
                string envXml = File.ReadAllText(envFullPath, Encoding.UTF8);
                (EnvironmentProfileDocument? envDoc, IReadOnlyList<ParseDiagnostic> envDiag) = EnvironmentProfileXmlSerializer.Parse(envRel, envFullPath, envXml);
                diagnostics.AddRange(envDiag);
                if (envDoc is not null)
                {
                    project.Environments[envRel] = envDoc;
                }
            }
        }

        return new WorldProjectLoadResult(project, diagnostics);
    }

    public static WorldProjectSaveResult Save(WorldProject project, bool dirtyOnly = true)
    {
        ArgumentNullException.ThrowIfNull(project);

        List<WorldFileTarget> targets = dirtyOnly
            ? ExpandTargetsForDirtySave(project)
            : CollectAllTargets(project, scriptsAndMetadataOnly: false);

        return SaveTargets(project, targets);
    }

    internal static WorldProjectSaveResult SaveCanonicalScriptsAndMetadataOnly(WorldProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        List<WorldFileTarget> targets = CollectAllTargets(project, scriptsAndMetadataOnly: true);
        return SaveTargets(project, targets);
    }

    private static WorldProjectSaveResult SaveTargets(WorldProject project, List<WorldFileTarget> targets)
    {
        List<string> writtenFiles = new List<string>();
        List<string> skippedProtected = new List<string>();
        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>();

        foreach (WorldFileTarget target in targets)
        {
            string normalizedRel = WorldProject.NormalizeRelativePath(target.RelativePath);
            if (target.Kind != WorldFileKind.WorldInfo && project.IsProtected(normalizedRel))
            {
                if (!skippedProtected.Contains(normalizedRel, StringComparer.OrdinalIgnoreCase))
                {
                    skippedProtected.Add(normalizedRel);
                    diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Warning,
                        Path.Combine(project.WorldDirectory, normalizedRel),
                        1,
                        1,
                        $"ファイル '{normalizedRel}' は <editor-generated> パースエラーのため ProtectedReadOnly 状態にあり、上書き保存をスキップしました。"));
                }

                continue;
            }

            if (TryWriteTarget(project, target, out string? fullPathWritten))
            {
                if (fullPathWritten is not null)
                {
                    writtenFiles.Add(fullPathWritten);
                }

                project.DirtyFiles.Remove(target);
            }
        }

        // Remove dirty markers for resource targets that were deleted from the project (e.g. LaneLayout/ModelList removal)
        List<WorldFileTarget> remainingDirty = new List<WorldFileTarget>(project.DirtyFiles);
        foreach (WorldFileTarget dirtyTarget in remainingDirty)
        {
            string normalizedRel = WorldProject.NormalizeRelativePath(dirtyTarget.RelativePath);
            if (dirtyTarget.Kind != WorldFileKind.WorldInfo && project.IsProtected(normalizedRel))
            {
                continue;
            }

            if (!IsResourcePresentInProject(project, dirtyTarget))
            {
                project.DirtyFiles.Remove(dirtyTarget);
            }
        }

        return new WorldProjectSaveResult(writtenFiles, skippedProtected, diagnostics);
    }

    private static bool IsResourcePresentInProject(WorldProject project, WorldFileTarget target)
    {
        string normalizedRel = WorldProject.NormalizeRelativePath(target.RelativePath);
        return target.Kind switch
        {
            WorldFileKind.ModelsTxt => project.ModelLists.Values.Any(
                m => string.Equals(WorldProject.NormalizeRelativePath(m.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)),
            WorldFileKind.SoundsTxt => project.SoundLists.Values.Any(
                s => string.Equals(WorldProject.NormalizeRelativePath(s.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)),
            WorldFileKind.CrossSectionsTxt => project.CrossSectionLists.Values.Any(
                c => string.Equals(WorldProject.NormalizeRelativePath(c.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)),
            WorldFileKind.LaneLayoutXml => project.LaneLayouts.Values.Any(
                l => string.Equals(WorldProject.NormalizeRelativePath(l.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)),
            WorldFileKind.CrossSectionXml => project.CrossSections.Values.Any(
                c => string.Equals(WorldProject.NormalizeRelativePath(c.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)),
            WorldFileKind.EnvironmentXml => project.Environments.Values.Any(
                e => string.Equals(WorldProject.NormalizeRelativePath(e.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)),
            WorldFileKind.SplineTemplateCsx => project.SplineTemplates.Values.Any(
                t => string.Equals(WorldProject.NormalizeRelativePath(t.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)),
            WorldFileKind.JunctionTemplateCsx => project.JunctionTemplates.Values.Any(
                t => string.Equals(WorldProject.NormalizeRelativePath(t.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)),
            _ => true,
        };
    }

    private static List<WorldFileTarget> ExpandTargetsForDirtySave(WorldProject project)
    {
        List<WorldFileTarget> list = new List<WorldFileTarget>(project.DirtyFiles);
        bool manifestDirty = list.Any(t => t.Kind == WorldFileKind.Manifest);
        bool hasEditorCsx = list.Any(t => t.Kind == WorldFileKind.EditorCsx);
        if (manifestDirty && !hasEditorCsx)
        {
            list.Add(new WorldFileTarget(WorldFileKind.EditorCsx, "__Editor.csx"));
        }

        return list;
    }

    private static List<WorldFileTarget> CollectAllTargets(WorldProject project, bool scriptsAndMetadataOnly)
    {
        List<WorldFileTarget> list = new List<WorldFileTarget>();

        if (!string.IsNullOrEmpty(project.WorldInfoFilePath))
        {
            list.Add(new WorldFileTarget(WorldFileKind.WorldInfo, Path.GetFileName(project.WorldInfoFilePath)));
        }

        string manifestRel = WorldProject.NormalizeRelativePath(
            Path.ChangeExtension(project.Placement.RelativePath, ".manifest.xml"));
        list.Add(new WorldFileTarget(WorldFileKind.Manifest, manifestRel));
        list.Add(new WorldFileTarget(WorldFileKind.EditorCsx, "__Editor.csx"));
        list.Add(new WorldFileTarget(WorldFileKind.EditorJson, "__Editor.json"));

        if (!scriptsAndMetadataOnly)
        {
            foreach (ModelListDocument modelList in project.ModelLists.Values)
            {
                list.Add(new WorldFileTarget(WorldFileKind.ModelsTxt, WorldProject.NormalizeRelativePath(modelList.RelativePath)));
            }

            foreach (SoundListDocument soundList in project.SoundLists.Values)
            {
                list.Add(new WorldFileTarget(WorldFileKind.SoundsTxt, WorldProject.NormalizeRelativePath(soundList.RelativePath)));
            }

            foreach (CrossSectionListDocument csList in project.CrossSectionLists.Values)
            {
                list.Add(new WorldFileTarget(WorldFileKind.CrossSectionsTxt, WorldProject.NormalizeRelativePath(csList.RelativePath)));
            }

            foreach (LaneLayoutDocument layout in project.LaneLayouts.Values)
            {
                list.Add(new WorldFileTarget(WorldFileKind.LaneLayoutXml, WorldProject.NormalizeRelativePath(layout.RelativePath)));
            }

            foreach (CrossSectionDocument cs in project.CrossSections.Values)
            {
                list.Add(new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(cs.RelativePath)));
            }

            foreach (EnvironmentProfileDocument env in project.Environments.Values)
            {
                list.Add(new WorldFileTarget(WorldFileKind.EnvironmentXml, WorldProject.NormalizeRelativePath(env.RelativePath)));
            }
        }

        list.Add(new WorldFileTarget(WorldFileKind.InitCsx, WorldProject.NormalizeRelativePath(project.Init.RelativePath)));

        foreach (SplineTemplateDocument splineTpl in project.SplineTemplates.Values)
        {
            if (string.IsNullOrEmpty(splineTpl.RelativePath))
            {
                splineTpl.RelativePath = BuildSplineTemplateRelativePath(splineTpl.Key);
            }

            list.Add(new WorldFileTarget(WorldFileKind.SplineTemplateCsx, WorldProject.NormalizeRelativePath(splineTpl.RelativePath)));
        }

        foreach (JunctionTemplateDocument junctionTpl in project.JunctionTemplates.Values)
        {
            if (string.IsNullOrEmpty(junctionTpl.RelativePath))
            {
                junctionTpl.RelativePath = BuildJunctionTemplateRelativePath(junctionTpl.Key);
            }

            list.Add(new WorldFileTarget(WorldFileKind.JunctionTemplateCsx, WorldProject.NormalizeRelativePath(junctionTpl.RelativePath)));
        }

        list.Add(new WorldFileTarget(WorldFileKind.TemplatesLoadCsx, TemplatesLoadRelativePath));
        list.Add(new WorldFileTarget(WorldFileKind.WorldCsx, WorldProject.NormalizeRelativePath(project.Placement.RelativePath)));
        return list;
    }

    private static bool TryWriteTarget(WorldProject project, WorldFileTarget target, out string? fullPathWritten)
    {
        fullPathWritten = null;
        string normalizedRel = WorldProject.NormalizeRelativePath(target.RelativePath);

        switch (target.Kind)
        {
            case WorldFileKind.WorldInfo:
                if (string.IsNullOrEmpty(project.WorldInfoFilePath))
                {
                    return false;
                }

                WriteUtf8File(project.WorldInfoFilePath, WorldInfoXmlSerializer.Generate(project.WorldInfo));
                fullPathWritten = project.WorldInfoFilePath;
                return true;

            case WorldFileKind.Manifest:
                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, ScriptManifestXmlSerializer.Generate(project.Manifest));
                return true;

            case WorldFileKind.EditorCsx:
                fullPathWritten = Path.Combine(project.WorldDirectory, "__Editor.csx");
                WriteUtf8File(fullPathWritten, EditorCsxGenerator.Generate(project.Manifest));
                return true;

            case WorldFileKind.EditorJson:
                fullPathWritten = Path.Combine(project.WorldDirectory, "__Editor.json");
                WriteUtf8File(fullPathWritten, EditorJsonSerializer.Generate(project.Sidecar));
                return true;

            case WorldFileKind.ModelsTxt:
                ModelListDocument? modelList = project.ModelLists.Values.FirstOrDefault(
                    m => string.Equals(WorldProject.NormalizeRelativePath(m.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase));
                if (modelList is null)
                {
                    return false;
                }

                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, ModelListSerializer.Generate(modelList));
                return true;

            case WorldFileKind.SoundsTxt:
                SoundListDocument? soundList = project.SoundLists.Values.FirstOrDefault(
                    s => string.Equals(WorldProject.NormalizeRelativePath(s.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase));
                if (soundList is null)
                {
                    return false;
                }

                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, SoundListSerializer.Generate(soundList));
                return true;

            case WorldFileKind.CrossSectionsTxt:
                CrossSectionListDocument? csList = project.CrossSectionLists.Values.FirstOrDefault(
                    c => string.Equals(WorldProject.NormalizeRelativePath(c.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase));
                if (csList is null)
                {
                    return false;
                }

                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, CrossSectionListSerializer.Generate(csList));
                return true;

            case WorldFileKind.LaneLayoutXml:
                LaneLayoutDocument? layout = project.LaneLayouts.Values.FirstOrDefault(
                    l => string.Equals(WorldProject.NormalizeRelativePath(l.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase));
                if (layout is null)
                {
                    return false;
                }

                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, LaneLayoutXmlSerializer.Generate(layout));
                return true;

            case WorldFileKind.CrossSectionXml:
                CrossSectionDocument? crossSection = project.CrossSections.Values.FirstOrDefault(
                    c => string.Equals(WorldProject.NormalizeRelativePath(c.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase));
                if (crossSection is null)
                {
                    return false;
                }

                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, CrossSectionXmlSerializer.Generate(crossSection));
                return true;

            case WorldFileKind.EnvironmentXml:
                EnvironmentProfileDocument? env = project.Environments.Values.FirstOrDefault(
                    e => string.Equals(WorldProject.NormalizeRelativePath(e.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase));
                if (env is null)
                {
                    return false;
                }

                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, EnvironmentProfileXmlSerializer.Generate(env));
                return true;

            case WorldFileKind.InitCsx:
                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, GenerateInitCsx(project));
                return true;

            case WorldFileKind.SplineTemplateCsx:
                SplineTemplateDocument? splineTpl = project.SplineTemplates.Values.FirstOrDefault(
                    t => string.Equals(WorldProject.NormalizeRelativePath(t.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase));
                if (splineTpl is null)
                {
                    return false;
                }

                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, GenerateSplineTemplateCsx(project, splineTpl));
                return true;

            case WorldFileKind.JunctionTemplateCsx:
                JunctionTemplateDocument? junctionTpl = project.JunctionTemplates.Values.FirstOrDefault(
                    t => string.Equals(WorldProject.NormalizeRelativePath(t.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase));
                if (junctionTpl is null)
                {
                    return false;
                }

                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, GenerateJunctionTemplateCsx(project, junctionTpl));
                return true;

            case WorldFileKind.TemplatesLoadCsx:
                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, GenerateTemplatesLoadCsx(project));
                return true;

            case WorldFileKind.WorldCsx:
                fullPathWritten = Path.Combine(project.WorldDirectory, normalizedRel);
                WriteUtf8File(fullPathWritten, GenerateWorldCsx(project));
                return true;

            default:
                return false;
        }
    }

    public static string BuildSplineTemplateRelativePath(string key) =>
        $"Templates/Splines/{RoslynCsxHelpers.SanitizeFileName(key, "Spline")}.csx";

    public static string BuildJunctionTemplateRelativePath(string key) =>
        $"Templates/Junctions/{RoslynCsxHelpers.SanitizeFileName(key, "Junction")}.csx";

    public static string GetSplineTemplateRelativePath(SplineTemplateDocument splineTpl)
    {
        ArgumentNullException.ThrowIfNull(splineTpl);
        return string.IsNullOrEmpty(splineTpl.RelativePath)
            ? BuildSplineTemplateRelativePath(splineTpl.Key)
            : WorldProject.NormalizeRelativePath(splineTpl.RelativePath);
    }

    public static string GetJunctionTemplateRelativePath(JunctionTemplateDocument junctionTpl)
    {
        ArgumentNullException.ThrowIfNull(junctionTpl);
        return string.IsNullOrEmpty(junctionTpl.RelativePath)
            ? BuildJunctionTemplateRelativePath(junctionTpl.Key)
            : WorldProject.NormalizeRelativePath(junctionTpl.RelativePath);
    }

    public static string GenerateInitCsx(WorldProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        string fullPath = Path.Combine(
            project.WorldDirectory,
            WorldProject.NormalizeRelativePath(project.Init.RelativePath));
        string initToEditorRel = GetRelativeScriptLoadPath(
            fullPath,
            Path.Combine(project.WorldDirectory, "__Editor.csx"));
        return InitCsxSerializer.Generate(project.Init, initToEditorRel);
    }

    public static string GenerateSplineTemplateCsx(WorldProject project, SplineTemplateDocument splineTpl)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(splineTpl);
        string relPath = GetSplineTemplateRelativePath(splineTpl);
        string fullPath = Path.Combine(project.WorldDirectory, relPath);
        string toEditorRel = GetRelativeScriptLoadPath(
            fullPath,
            Path.Combine(project.WorldDirectory, "__Editor.csx"));
        return SplineTemplateCsxSerializer.Generate(splineTpl, toEditorRel);
    }

    public static string GenerateJunctionTemplateCsx(WorldProject project, JunctionTemplateDocument junctionTpl)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(junctionTpl);
        string relPath = GetJunctionTemplateRelativePath(junctionTpl);
        string fullPath = Path.Combine(project.WorldDirectory, relPath);
        string toEditorRel = GetRelativeScriptLoadPath(
            fullPath,
            Path.Combine(project.WorldDirectory, "__Editor.csx"));
        return JunctionTemplateCsxSerializer.Generate(junctionTpl, toEditorRel);
    }

    public const string TemplatesLoadRelativePath = "Templates/Load.csx";

    public static string GenerateTemplatesLoadCsx(WorldProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        string templatesLoadFull = Path.Combine(project.WorldDirectory, TemplatesLoadRelativePath);
        string toEditorRel = GetRelativeScriptLoadPath(
            templatesLoadFull,
            Path.Combine(project.WorldDirectory, "__Editor.csx"));

        StringBuilder sb = new();
        sb.Append("#load ").Append(RoslynCsxHelpers.FormatStringLiteral(toEditorRel)).Append('\n');

        foreach (SplineTemplateDocument st in project.SplineTemplates.Values.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            string tplFull = Path.Combine(project.WorldDirectory, GetSplineTemplateRelativePath(st));
            string tplRel = GetRelativeScriptLoadPath(templatesLoadFull, tplFull);
            sb.Append("#load ").Append(RoslynCsxHelpers.FormatStringLiteral(tplRel)).Append('\n');
        }

        foreach (JunctionTemplateDocument jt in project.JunctionTemplates.Values.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            string tplFull = Path.Combine(project.WorldDirectory, GetJunctionTemplateRelativePath(jt));
            string tplRel = GetRelativeScriptLoadPath(templatesLoadFull, tplFull);
            sb.Append("#load ").Append(RoslynCsxHelpers.FormatStringLiteral(tplRel)).Append('\n');
        }

        return sb.ToString();
    }

    public static string GenerateWorldCsx(WorldProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        string initRel = WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        return WorldCsxSerializer.Generate(project.Placement, [TemplatesLoadRelativePath], "__Editor.csx", initRel);
    }

    public static ExternalFileReloadResult ReloadExternalFile(WorldProject project, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(fullPath);

        string normalizedFull = Path.GetFullPath(fullPath);
        if (!string.IsNullOrWhiteSpace(project.WorldInfoFilePath)
            && string.Equals(normalizedFull, Path.GetFullPath(project.WorldInfoFilePath), StringComparison.OrdinalIgnoreCase))
        {
            string worldInfoRel = Path.GetFileName(project.WorldInfoFilePath);
            if (IsFileDirty(project, WorldFileKind.WorldInfo, worldInfoRel))
            {
                return new ExternalFileReloadResult(
                    true,
                    false,
                    true,
                    WorldFileKind.WorldInfo,
                    worldInfoRel,
                    $"未保存の変更があるため '{worldInfoRel}' の自動反映をスキップしました。",
                    Array.Empty<ParseDiagnostic>());
            }

            if (!File.Exists(normalizedFull))
            {
                return new ExternalFileReloadResult(
                    true,
                    false,
                    false,
                    WorldFileKind.WorldInfo,
                    worldInfoRel,
                    $"外部で '{worldInfoRel}' が削除または移動されました。",
                    Array.Empty<ParseDiagnostic>());
            }

            string wiText = File.ReadAllText(normalizedFull, Encoding.UTF8);
            (WorldInfoModel? wi, IReadOnlyList<ParseDiagnostic> wiDiag) = WorldInfoXmlSerializer.Parse(normalizedFull, wiText);
            if (wi is not null)
            {
                project.WorldInfo = wi;
            }

            return new ExternalFileReloadResult(true, true, false, WorldFileKind.WorldInfo, worldInfoRel, null, wiDiag);
        }

        if (string.IsNullOrWhiteSpace(project.WorldDirectory))
        {
            return new ExternalFileReloadResult(false, false, false, WorldFileKind.WorldCsx, string.Empty, null, Array.Empty<ParseDiagnostic>());
        }

        string worldDirFull = Path.GetFullPath(project.WorldDirectory);
        string relRaw = Path.GetRelativePath(worldDirFull, normalizedFull);
        if (Path.IsPathRooted(relRaw) || relRaw.StartsWith("..", StringComparison.Ordinal))
        {
            return new ExternalFileReloadResult(false, false, false, WorldFileKind.WorldCsx, string.Empty, null, Array.Empty<ParseDiagnostic>());
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(relRaw);
        if (string.Equals(Path.GetFileName(normalizedRel), "__Editor.csx", StringComparison.OrdinalIgnoreCase))
        {
            return new ExternalFileReloadResult(false, false, false, WorldFileKind.EditorCsx, normalizedRel, null, Array.Empty<ParseDiagnostic>());
        }

        if (!TryIdentifyWorldFile(project, normalizedRel, out WorldFileKind kind, out string? existingDictKey))
        {
            return new ExternalFileReloadResult(false, false, false, WorldFileKind.WorldCsx, normalizedRel, null, Array.Empty<ParseDiagnostic>());
        }

        if (IsFileDirty(project, kind, normalizedRel))
        {
            return new ExternalFileReloadResult(
                true,
                false,
                true,
                kind,
                normalizedRel,
                $"未保存の変更があるため '{normalizedRel}' の自動反映をスキップしました。",
                Array.Empty<ParseDiagnostic>());
        }

        if (!File.Exists(normalizedFull))
        {
            return new ExternalFileReloadResult(
                true,
                false,
                false,
                kind,
                normalizedRel,
                $"外部で '{normalizedRel}' が削除または移動されました。",
                Array.Empty<ParseDiagnostic>());
        }

        string content = File.ReadAllText(normalizedFull, Encoding.UTF8);
        List<ParseDiagnostic> diagnostics = new();

        switch (kind)
        {
            case WorldFileKind.TemplatesLoadCsx:
                project.SplineTemplates.Clear();
                project.JunctionTemplates.Clear();
                LoadTemplatesFromLoadScript(project, worldDirFull, normalizedFull, content, diagnostics);
                break;

            case WorldFileKind.WorldCsx:
                (ParsedWorldCsx? parsedWorld, IReadOnlyList<ParseDiagnostic> worldDiag) = WorldCsxSerializer.Parse(normalizedFull, content, normalizedRel);
                diagnostics.AddRange(worldDiag);
                if (parsedWorld is not null)
                {
                    project.ProtectedFiles.Remove(normalizedRel);
                    project.Placement = parsedWorld.Document;
                }
                else
                {
                    project.ProtectedFiles.Add(normalizedRel);
                    WorldCsxSerializer.PopulateFallback(content, normalizedRel, project.Placement);
                }
                break;

            case WorldFileKind.InitCsx:
                (InitDocument? initDoc, IReadOnlyList<ParseDiagnostic> initDiag) = InitCsxSerializer.Parse(normalizedFull, content, normalizedRel);
                diagnostics.AddRange(initDiag);
                if (initDoc is not null)
                {
                    project.ProtectedFiles.Remove(normalizedRel);
                    project.Init = initDoc;
                    string initDir = Path.GetDirectoryName(normalizedFull) ?? worldDirFull;
                    LoadInitResources(project, initDir, diagnostics);
                }
                else
                {
                    project.ProtectedFiles.Add(normalizedRel);
                    InitCsxSerializer.PopulateFallback(content, normalizedRel, project.Init);
                }
                break;

            case WorldFileKind.Manifest:
                (ScriptManifestModel? manifest, IReadOnlyList<ParseDiagnostic> mfDiag) = ScriptManifestXmlSerializer.Parse(normalizedFull, content);
                diagnostics.AddRange(mfDiag);
                if (manifest is not null)
                {
                    project.Manifest = manifest;
                }
                break;

            case WorldFileKind.EditorJson:
                (EditorSidecarMetadata? sidecar, IReadOnlyList<ParseDiagnostic> jsonDiag) = EditorJsonSerializer.Parse(normalizedFull, content);
                diagnostics.AddRange(jsonDiag);
                if (sidecar is not null)
                {
                    project.Sidecar = sidecar;
                }
                break;

            case WorldFileKind.ModelsTxt:
                string? modelBundle = existingDictKey is not null && project.ModelLists.TryGetValue(existingDictKey, out ModelListDocument? curModelList)
                    ? curModelList.BundleKey
                    : null;
                (ModelListDocument? nextModels, IReadOnlyList<ParseDiagnostic> modelDiag) = ModelListSerializer.Parse(modelBundle, normalizedRel, normalizedFull, content);
                diagnostics.AddRange(modelDiag);
                if (nextModels is not null)
                {
                    string storeKey = existingDictKey ?? normalizedRel;
                    project.ModelLists[storeKey] = nextModels;
                }
                break;

            case WorldFileKind.SoundsTxt:
                string? soundBundle = existingDictKey is not null && project.SoundLists.TryGetValue(existingDictKey, out SoundListDocument? curSoundList)
                    ? curSoundList.BundleKey
                    : null;
                (SoundListDocument? nextSounds, IReadOnlyList<ParseDiagnostic> soundDiag) = SoundListSerializer.Parse(soundBundle, normalizedRel, normalizedFull, content);
                diagnostics.AddRange(soundDiag);
                if (nextSounds is not null)
                {
                    string storeKey = existingDictKey ?? normalizedRel;
                    project.SoundLists[storeKey] = nextSounds;
                }
                break;

            case WorldFileKind.CrossSectionsTxt:
                (CrossSectionListDocument? nextCrossSectionList, IReadOnlyList<ParseDiagnostic> csListDiag) =
                    CrossSectionListSerializer.Parse(normalizedRel, normalizedFull, content);
                diagnostics.AddRange(csListDiag);
                if (nextCrossSectionList is not null)
                {
                    string storeKey = existingDictKey ?? normalizedRel;
                    project.CrossSectionLists[storeKey] = nextCrossSectionList;
                    SyncCrossSectionsFromListsOnReload(project, diagnostics);
                }
                break;

            case WorldFileKind.EnvironmentXml:
                (EnvironmentProfileDocument? nextEnv, IReadOnlyList<ParseDiagnostic> envDiag) = EnvironmentProfileXmlSerializer.Parse(normalizedRel, normalizedFull, content);
                diagnostics.AddRange(envDiag);
                if (nextEnv is not null)
                {
                    string storeKey = existingDictKey ?? normalizedRel;
                    project.Environments[storeKey] = nextEnv;
                }
                break;

            case WorldFileKind.LaneLayoutXml:
                string laneKey = existingDictKey ?? Path.GetFileNameWithoutExtension(normalizedRel);
                (LaneLayoutDocument? nextLane, IReadOnlyList<ParseDiagnostic> laneDiag) = LaneLayoutXmlSerializer.Parse(laneKey, normalizedRel, normalizedFull, content);
                diagnostics.AddRange(laneDiag);
                if (nextLane is not null)
                {
                    project.LaneLayouts[laneKey] = nextLane;
                }
                break;

            case WorldFileKind.CrossSectionXml:
                string csKey = existingDictKey ?? Path.GetFileNameWithoutExtension(normalizedRel);
                (CrossSectionDocument? nextCs, IReadOnlyList<ParseDiagnostic> csDiag) = CrossSectionXmlSerializer.Parse(csKey, normalizedRel, normalizedFull, content);
                diagnostics.AddRange(csDiag);
                if (nextCs is not null)
                {
                    project.CrossSections[csKey] = nextCs;
                }
                break;

            case WorldFileKind.SplineTemplateCsx:
                (SplineTemplateDocument? nextSplineTpl, IReadOnlyList<ParseDiagnostic> stDiag) = SplineTemplateCsxSerializer.Parse(normalizedFull, content, normalizedRel);
                diagnostics.AddRange(stDiag);
                if (nextSplineTpl is not null)
                {
                    project.ProtectedFiles.Remove(normalizedRel);
                    if (existingDictKey is not null && !string.Equals(existingDictKey, nextSplineTpl.Key, StringComparison.Ordinal))
                    {
                        project.SplineTemplates.Remove(existingDictKey);
                    }

                    project.SplineTemplates[nextSplineTpl.Key] = nextSplineTpl;
                }
                else
                {
                    project.ProtectedFiles.Add(normalizedRel);
                }
                break;

            case WorldFileKind.JunctionTemplateCsx:
                (JunctionTemplateDocument? nextJunctionTpl, IReadOnlyList<ParseDiagnostic> jtDiag) = JunctionTemplateCsxSerializer.Parse(normalizedFull, content, normalizedRel);
                diagnostics.AddRange(jtDiag);
                if (nextJunctionTpl is not null)
                {
                    project.ProtectedFiles.Remove(normalizedRel);
                    if (existingDictKey is not null && !string.Equals(existingDictKey, nextJunctionTpl.Key, StringComparison.Ordinal))
                    {
                        project.JunctionTemplates.Remove(existingDictKey);
                    }

                    project.JunctionTemplates[nextJunctionTpl.Key] = nextJunctionTpl;
                }
                else
                {
                    project.ProtectedFiles.Add(normalizedRel);
                }
                break;

            default:
                return new ExternalFileReloadResult(false, false, false, kind, normalizedRel, null, Array.Empty<ParseDiagnostic>());
        }

        return new ExternalFileReloadResult(true, true, false, kind, normalizedRel, null, diagnostics);
    }

    private static bool IsFileDirty(WorldProject project, WorldFileKind kind, string normalizedRel)
    {
        foreach (WorldFileTarget dirty in project.DirtyFiles)
        {
            if (dirty.Kind != kind)
            {
                continue;
            }

            if (kind == WorldFileKind.WorldInfo
                || string.Equals(WorldProject.NormalizeRelativePath(dirty.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryIdentifyWorldFile(
        WorldProject project,
        string normalizedRel,
        out WorldFileKind kind,
        out string? existingDictKey)
    {
        existingDictKey = null;

        if (string.Equals(normalizedRel, WorldProject.NormalizeRelativePath(project.Placement.RelativePath), StringComparison.OrdinalIgnoreCase))
        {
            kind = WorldFileKind.WorldCsx;
            return true;
        }

        if (string.Equals(normalizedRel, WorldProject.NormalizeRelativePath(project.Init.RelativePath), StringComparison.OrdinalIgnoreCase))
        {
            kind = WorldFileKind.InitCsx;
            return true;
        }

        if (string.Equals(normalizedRel, TemplatesLoadRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            kind = WorldFileKind.TemplatesLoadCsx;
            return true;
        }

        string manifestRel = WorldProject.NormalizeRelativePath(Path.ChangeExtension(project.Placement.RelativePath, ".manifest.xml"));
        if (string.Equals(normalizedRel, manifestRel, StringComparison.OrdinalIgnoreCase))
        {
            kind = WorldFileKind.Manifest;
            return true;
        }

        if (string.Equals(normalizedRel, "__Editor.json", StringComparison.OrdinalIgnoreCase))
        {
            kind = WorldFileKind.EditorJson;
            return true;
        }

        foreach (KeyValuePair<string, ModelListDocument> kv in project.ModelLists)
        {
            if (string.Equals(WorldProject.NormalizeRelativePath(kv.Value.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                kind = WorldFileKind.ModelsTxt;
                existingDictKey = kv.Key;
                return true;
            }
        }

        foreach (KeyValuePair<string, SoundListDocument> kv in project.SoundLists)
        {
            if (string.Equals(WorldProject.NormalizeRelativePath(kv.Value.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                kind = WorldFileKind.SoundsTxt;
                existingDictKey = kv.Key;
                return true;
            }
        }

        foreach (KeyValuePair<string, CrossSectionListDocument> kv in project.CrossSectionLists)
        {
            if (string.Equals(WorldProject.NormalizeRelativePath(kv.Value.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                kind = WorldFileKind.CrossSectionsTxt;
                existingDictKey = kv.Key;
                return true;
            }
        }

        foreach (KeyValuePair<string, EnvironmentProfileDocument> kv in project.Environments)
        {
            if (string.Equals(WorldProject.NormalizeRelativePath(kv.Value.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                kind = WorldFileKind.EnvironmentXml;
                existingDictKey = kv.Key;
                return true;
            }
        }

        foreach (KeyValuePair<string, LaneLayoutDocument> kv in project.LaneLayouts)
        {
            if (string.Equals(WorldProject.NormalizeRelativePath(kv.Value.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                kind = WorldFileKind.LaneLayoutXml;
                existingDictKey = kv.Key;
                return true;
            }
        }

        foreach (KeyValuePair<string, CrossSectionDocument> kv in project.CrossSections)
        {
            if (string.Equals(WorldProject.NormalizeRelativePath(kv.Value.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                kind = WorldFileKind.CrossSectionXml;
                existingDictKey = kv.Key;
                return true;
            }
        }

        foreach (KeyValuePair<string, SplineTemplateDocument> kv in project.SplineTemplates)
        {
            if (string.Equals(WorldProject.NormalizeRelativePath(kv.Value.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                kind = WorldFileKind.SplineTemplateCsx;
                existingDictKey = kv.Key;
                return true;
            }
        }

        foreach (KeyValuePair<string, JunctionTemplateDocument> kv in project.JunctionTemplates)
        {
            if (string.Equals(WorldProject.NormalizeRelativePath(kv.Value.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                kind = WorldFileKind.JunctionTemplateCsx;
                existingDictKey = kv.Key;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private static string GetRelativeScriptLoadPath(string fromFilePath, string targetFilePath)
    {
        string fromDir = Path.GetDirectoryName(fromFilePath) ?? ".";
        return Path.GetRelativePath(fromDir, targetFilePath).Replace('\\', '/');
    }

    internal static void WriteUtf8File(string fullPath, string content)
    {
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content, Utf8NoBom);
    }

    internal static void LoadInitResources(WorldProject project, string initDir, List<ParseDiagnostic> diagnostics)
    {
        foreach (AssetListLoadReference modelRef in project.Init.ModelLists)
        {
            string fullPath = Path.GetFullPath(Path.Combine(initDir, modelRef.RelativePath));
            string worldRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, fullPath));
            if (!File.Exists(fullPath))
            {
                diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, fullPath, 1, 1, $"モデルリスト '{fullPath}' が見つかりません。"));
                continue;
            }

            string text = File.ReadAllText(fullPath, Encoding.UTF8);
            (ModelListDocument? listDoc, IReadOnlyList<ParseDiagnostic> diag) = ModelListSerializer.Parse(modelRef.BundleKey, worldRel, fullPath, text);
            diagnostics.AddRange(diag);
            if (listDoc is not null)
            {
                project.ModelLists[worldRel] = listDoc;
            }
        }

        foreach (AssetListLoadReference soundRef in project.Init.SoundLists)
        {
            string fullPath = Path.GetFullPath(Path.Combine(initDir, soundRef.RelativePath));
            string worldRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, fullPath));
            if (!File.Exists(fullPath))
            {
                diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, fullPath, 1, 1, $"サウンドリスト '{fullPath}' が見つかりません。"));
                continue;
            }

            string text = File.ReadAllText(fullPath, Encoding.UTF8);
            (SoundListDocument? listDoc, IReadOnlyList<ParseDiagnostic> diag) = SoundListSerializer.Parse(soundRef.BundleKey, worldRel, fullPath, text);
            diagnostics.AddRange(diag);
            if (listDoc is not null)
            {
                project.SoundLists[worldRel] = listDoc;
            }
        }

        foreach (string csListPath in project.Init.CrossSectionLists)
        {
            string fullPath = Path.GetFullPath(Path.Combine(initDir, csListPath));
            string worldRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, fullPath));
            if (!File.Exists(fullPath))
            {
                diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, fullPath, 1, 1, $"断面リスト '{fullPath}' が見つかりません。"));
                continue;
            }

            string text = File.ReadAllText(fullPath, Encoding.UTF8);
            (CrossSectionListDocument? listDoc, IReadOnlyList<ParseDiagnostic> diag) = CrossSectionListSerializer.Parse(worldRel, fullPath, text);
            diagnostics.AddRange(diag);
            if (listDoc is not null)
            {
                project.CrossSectionLists[worldRel] = listDoc;
                LoadCrossSectionsFromList(project, listDoc, diagnostics);
            }
        }

        foreach (ResourceLoadReference layoutRef in project.Init.LaneLayouts)
        {
            string fullPath = Path.GetFullPath(Path.Combine(initDir, layoutRef.RelativePath));
            string worldRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, fullPath));
            if (!File.Exists(fullPath))
            {
                diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, fullPath, 1, 1, $"LaneLayout ファイル '{fullPath}' が見つかりません。"));
                continue;
            }

            string xml = File.ReadAllText(fullPath, Encoding.UTF8);
            (LaneLayoutDocument? llDoc, IReadOnlyList<ParseDiagnostic> diag) = LaneLayoutXmlSerializer.Parse(layoutRef.Key, worldRel, fullPath, xml);
            diagnostics.AddRange(diag);
            if (llDoc is not null)
            {
                project.LaneLayouts[llDoc.Key] = llDoc;
            }
        }
    }

    public static string GetListDirectory(string worldDirectory, string listRelativePath)
    {
        ArgumentNullException.ThrowIfNull(worldDirectory);
        ArgumentNullException.ThrowIfNull(listRelativePath);

        string listFullPath = Path.GetFullPath(Path.Combine(worldDirectory, WorldProject.NormalizeRelativePath(listRelativePath)));
        return Path.GetDirectoryName(listFullPath) ?? Path.GetFullPath(worldDirectory);
    }

    internal static void LoadCrossSectionsFromList(
        WorldProject project,
        CrossSectionListDocument listDoc,
        List<ParseDiagnostic> diagnostics)
    {
        string listDir = GetListDirectory(project.WorldDirectory, listDoc.RelativePath);
        foreach (CrossSectionListEntry entry in listDoc.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.RelativePath))
            {
                continue;
            }

            string xmlFullPath = Path.GetFullPath(Path.Combine(listDir, entry.RelativePath));
            string xmlWorldRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, xmlFullPath));
            if (!File.Exists(xmlFullPath))
            {
                diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, xmlFullPath, 1, 1, $"CrossSection ファイル '{xmlFullPath}' が見つかりません。"));
                continue;
            }

            string xml = File.ReadAllText(xmlFullPath, Encoding.UTF8);
            (CrossSectionDocument? csDoc, IReadOnlyList<ParseDiagnostic> csDiag) = CrossSectionXmlSerializer.Parse(entry.Key, xmlWorldRel, xmlFullPath, xml);
            diagnostics.AddRange(csDiag);
            if (csDoc is not null)
            {
                project.CrossSections[csDoc.Key] = csDoc;
            }
        }
    }

    private static void SyncCrossSectionsFromListsOnReload(WorldProject project, List<ParseDiagnostic> diagnostics)
    {
        HashSet<string> activeKeys = new(StringComparer.Ordinal);
        foreach (CrossSectionListDocument listDoc in project.CrossSectionLists.Values)
        {
            foreach (CrossSectionListEntry entry in listDoc.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key))
                {
                    activeKeys.Add(entry.Key);
                }
            }
        }

        List<string> staleKeys = new();
        foreach (KeyValuePair<string, CrossSectionDocument> kv in project.CrossSections)
        {
            if (!activeKeys.Contains(kv.Key) && !IsFileDirty(project, WorldFileKind.CrossSectionXml, kv.Value.RelativePath))
            {
                staleKeys.Add(kv.Key);
            }
        }

        foreach (string staleKey in staleKeys)
        {
            project.CrossSections.Remove(staleKey);
        }

        foreach (CrossSectionListDocument listDoc in project.CrossSectionLists.Values)
        {
            string listDir = GetListDirectory(project.WorldDirectory, listDoc.RelativePath);
            foreach (CrossSectionListEntry entry in listDoc.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.RelativePath))
                {
                    continue;
                }

                string xmlFullPath = Path.GetFullPath(Path.Combine(listDir, entry.RelativePath));
                string xmlWorldRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, xmlFullPath));
                if (IsFileDirty(project, WorldFileKind.CrossSectionXml, xmlWorldRel))
                {
                    continue;
                }

                if (!File.Exists(xmlFullPath))
                {
                    diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, xmlFullPath, 1, 1, $"CrossSection ファイル '{xmlFullPath}' が見つかりません。"));
                    continue;
                }

                string xml = File.ReadAllText(xmlFullPath, Encoding.UTF8);
                (CrossSectionDocument? csDoc, IReadOnlyList<ParseDiagnostic> csDiag) = CrossSectionXmlSerializer.Parse(entry.Key, xmlWorldRel, xmlFullPath, xml);
                diagnostics.AddRange(csDiag);
                if (csDoc is not null)
                {
                    project.CrossSections[csDoc.Key] = csDoc;
                }
            }
        }
    }

    private static void LoadTemplatesFromLoadScript(
        WorldProject project,
        string worldDir,
        string loadScriptFullPath,
        string loadScriptText,
        List<ParseDiagnostic> diagnostics)
    {
        string loadScriptDir = Path.GetDirectoryName(loadScriptFullPath) ?? worldDir;
        CsxSlicedFile sliced = CsxMarkerSlicer.Slice(loadScriptFullPath, loadScriptText);
        foreach (string rawLoad in sliced.LoadDirectives)
        {
            string normalizedRaw = WorldProject.NormalizeRelativePath(rawLoad);
            if (string.Equals(Path.GetFileName(normalizedRaw), "__Editor.csx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string tplFullPath = Path.GetFullPath(Path.Combine(loadScriptDir, normalizedRaw));
            string tplWorldRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(worldDir, tplFullPath));
            if (!File.Exists(tplFullPath))
            {
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    tplFullPath,
                    1,
                    1,
                    $"テンプレートファイル '{tplWorldRel}' が見つかりません。"));
                continue;
            }

            string tplText = File.ReadAllText(tplFullPath, Encoding.UTF8);
            if (IsSplineTemplateScript(tplWorldRel, tplText))
            {
                (SplineTemplateDocument? tpl, IReadOnlyList<ParseDiagnostic> tplDiag) = SplineTemplateCsxSerializer.Parse(tplFullPath, tplText, tplWorldRel);
                diagnostics.AddRange(tplDiag);
                if (tpl is not null)
                {
                    project.SplineTemplates[tpl.Key] = tpl;
                }
                else
                {
                    project.ProtectedFiles.Add(tplWorldRel);
                }
            }
            else if (IsJunctionTemplateScript(tplWorldRel, tplText))
            {
                (JunctionTemplateDocument? tpl, IReadOnlyList<ParseDiagnostic> tplDiag) = JunctionTemplateCsxSerializer.Parse(tplFullPath, tplText, tplWorldRel);
                diagnostics.AddRange(tplDiag);
                if (tpl is not null)
                {
                    project.JunctionTemplates[tpl.Key] = tpl;
                }
                else
                {
                    project.ProtectedFiles.Add(tplWorldRel);
                }
            }
            else
            {
                project.ProtectedFiles.Add(tplWorldRel);
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    tplFullPath,
                    1,
                    1,
                    $"'{tplWorldRel}' は有効な SplineTemplate / JunctionTemplate スクリプトとして認識できませんでした。"));
            }
        }
    }

    private static bool IsTemplatesLoadScript(string normalizedLoad)
    {
        return string.Equals(normalizedLoad, TemplatesLoadRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInitScript(string normalizedLoad, string content)
    {
        if (string.Equals(Path.GetFileName(normalizedLoad), "Init.csx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return content.Contains("Models.LoadList", StringComparison.Ordinal)
            || content.Contains("CrossSections.LoadList", StringComparison.Ordinal)
            || content.Contains("Network.LaneLayouts.Load", StringComparison.Ordinal)
            || content.Contains("Network.LaneTraffic.", StringComparison.Ordinal);
    }

    private static bool IsSplineTemplateScript(string normalizedLoad, string content)
    {
        if (normalizedLoad.StartsWith("Templates/Splines/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return content.Contains("Network.Templates.CreateSpline", StringComparison.Ordinal)
            && !content.Contains("Network.Templates.CreateJunction", StringComparison.Ordinal);
    }

    private static bool IsJunctionTemplateScript(string normalizedLoad, string content)
    {
        if (normalizedLoad.StartsWith("Templates/Junctions/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return content.Contains("Network.Templates.CreateJunction", StringComparison.Ordinal)
            && !content.Contains("Network.Templates.CreateSpline", StringComparison.Ordinal);
    }
}
