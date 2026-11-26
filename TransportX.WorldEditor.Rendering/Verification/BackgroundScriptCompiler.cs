using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using TransportX.Dependency;
using TransportX.Diagnostics;
using TransportX.Extensions.Network.Elements;
using TransportX.IO;
using TransportX.Scripting;
using TransportX.Scripting.Worlds.Commands;
using TransportX.Worlds;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Serialization.Storage;

namespace TransportX.WorldEditor.Rendering.Verification;

/// <summary>
/// Immutable snapshot of a world's scripts and unsaved in-memory overlays for background compile-only analysis.
/// </summary>
public sealed class ScriptCompileSnapshot
{
    public ScriptCompileSnapshot(
        string worldDirectory,
        string worldCsxFullPath,
        IReadOnlyList<string> manifestDependencies,
        IReadOnlyDictionary<string, string> overlayFiles)
    {
        WorldDirectory = worldDirectory;
        WorldCsxFullPath = worldCsxFullPath;
        ManifestDependencies = manifestDependencies;
        OverlayFiles = overlayFiles;
    }

    public string WorldDirectory { get; }
    public string WorldCsxFullPath { get; }
    public IReadOnlyList<string> ManifestDependencies { get; }
    public IReadOnlyDictionary<string, string> OverlayFiles { get; }
}

/// <summary>
/// Performs debounced, compile-only Roslyn script diagnostics using the real TransportX <see cref="Commander"/>
/// globals without executing <c>RunAsync</c> or writing temporary files to disk.
/// </summary>
public static class BackgroundScriptCompiler
{
    private static readonly Regex EditorLoadDirectiveRegex = new(
        @"^[ \t]*#load[ \t]+""(?:[^""]*[/\\])?__Editor\.csx""[ \t]*",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ScriptCompileSnapshot CaptureSnapshot(WorldProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        string worldDir = string.IsNullOrWhiteSpace(project.WorldDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(project.WorldDirectory);

        string worldRel = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        string worldFullPath = Path.GetFullPath(Path.Combine(worldDir, worldRel));

        Dictionary<string, string> overlays = new(StringComparer.OrdinalIgnoreCase);

        if (!project.IsProtected(worldRel)
            && (IsFileDirty(project, WorldFileKind.WorldCsx, worldRel) || !File.Exists(worldFullPath)))
        {
            overlays[worldFullPath] = WorldProjectStorage.GenerateWorldCsx(project);
        }

        string initRel = WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        string initFullPath = Path.GetFullPath(Path.Combine(worldDir, initRel));
        if (!project.IsProtected(initRel)
            && (IsFileDirty(project, WorldFileKind.InitCsx, initRel) || !File.Exists(initFullPath)))
        {
            overlays[initFullPath] = WorldProjectStorage.GenerateInitCsx(project);
        }

        string templatesLoadRel = WorldProjectStorage.TemplatesLoadRelativePath;
        string templatesLoadFullPath = Path.GetFullPath(Path.Combine(worldDir, templatesLoadRel));
        if (!project.IsProtected(templatesLoadRel)
            && (IsFileDirty(project, WorldFileKind.TemplatesLoadCsx, templatesLoadRel) || !File.Exists(templatesLoadFullPath)))
        {
            overlays[templatesLoadFullPath] = WorldProjectStorage.GenerateTemplatesLoadCsx(project);
        }

        foreach (SplineTemplateDocument splineTpl in project.SplineTemplates.Values)
        {
            string rel = WorldProjectStorage.GetSplineTemplateRelativePath(splineTpl);
            string full = Path.GetFullPath(Path.Combine(worldDir, rel));
            if (!project.IsProtected(rel)
                && (IsFileDirty(project, WorldFileKind.SplineTemplateCsx, rel) || !File.Exists(full)))
            {
                overlays[full] = WorldProjectStorage.GenerateSplineTemplateCsx(project, splineTpl);
            }
        }

        foreach (JunctionTemplateDocument junctionTpl in project.JunctionTemplates.Values)
        {
            string rel = WorldProjectStorage.GetJunctionTemplateRelativePath(junctionTpl);
            string full = Path.GetFullPath(Path.Combine(worldDir, rel));
            if (!project.IsProtected(rel)
                && (IsFileDirty(project, WorldFileKind.JunctionTemplateCsx, rel) || !File.Exists(full)))
            {
                overlays[full] = WorldProjectStorage.GenerateJunctionTemplateCsx(project, junctionTpl);
            }
        }

        List<string> dependencies = new(project.Manifest.Dependencies);
        return new ScriptCompileSnapshot(worldDir, worldFullPath, dependencies, overlays);
    }

    public static IReadOnlyList<Error> Compile(
        ScriptCompileSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        List<Error> results = new();
        PluginLoadContext compileContext = new(typeof(BackgroundScriptCompiler).Assembly.Location);
        try
        {
            OverlayScriptSourceResolver resolver = new(
                snapshot.WorldDirectory,
                ScriptOptions.Default.SourceResolver,
                snapshot.OverlayFiles);

            ScriptOptions options = ScriptOptions.Default
                .WithSourceResolver(resolver)
                .WithFilePath(snapshot.WorldCsxFullPath)
                .AddImports("System", "System.Collections.Generic", "System.Linq", "System.Numerics", "System.Text")
                .WithFileEncoding(Encoding.UTF8)
                .WithEmitDebugInformation(true);

            options = AddReferencesAndImports(options, typeof(WorldBase).Assembly);
            options = AddReferencesAndImports(options, typeof(SplineBase).Assembly);
            options = AddReferencesAndImports(options, typeof(TransportX.Scripting.ScriptSourceResolver).Assembly);

            string manifestLocation = Path.ChangeExtension(snapshot.WorldCsxFullPath, ".manifest.xml");
            foreach (string rawDep in snapshot.ManifestDependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(rawDep))
                {
                    continue;
                }

                string expanded = PathMacros.Expand(rawDep);
                string assemblyPath = Path.GetFullPath(Path.Combine(snapshot.WorldDirectory, expanded));
                if (!File.Exists(assemblyPath))
                {
                    results.Add(new Error(
                        ErrorLevel.Error,
                        $"依存関係 '{assemblyPath}' が見つかりませんでした。",
                        manifestLocation));
                    continue;
                }

                try
                {
                    Assembly pluginAssembly = compileContext.LoadFromAssemblyPath(assemblyPath);
                    options = AddReferencesAndImports(options, pluginAssembly);
                }
                catch (Exception ex)
                {
                    results.Add(new Error(
                        ErrorLevel.Error,
                        $"依存関係 '{assemblyPath}' が読み込めませんでした。",
                        manifestLocation)
                    {
                        Exception = ex,
                    });
                }
            }

            string rawWorldText;
            if (snapshot.OverlayFiles.TryGetValue(snapshot.WorldCsxFullPath, out string? overlayWorldText))
            {
                rawWorldText = overlayWorldText;
            }
            else if (File.Exists(snapshot.WorldCsxFullPath))
            {
                try
                {
                    UTF8Encoding strictUtf8 = new(false, true);
                    using StreamReader reader = new(snapshot.WorldCsxFullPath, strictUtf8, true);
                    rawWorldText = reader.ReadToEnd();
                }
                catch (DecoderFallbackException ex)
                {
                    results.Add(new Error(
                        ErrorLevel.Error,
                        "サポートされない文字コードです。Shift-JIS 等で保存されている可能性があります。文字化けを防ぐため、ファイルを UTF-8 で保存し直してください。",
                        snapshot.WorldCsxFullPath)
                    {
                        Exception = ex,
                    });
                    return results;
                }
                catch (Exception ex)
                {
                    results.Add(new Error(
                        ErrorLevel.Error,
                        $"スクリプトファイル '{snapshot.WorldCsxFullPath}' を読み込めませんでした。",
                        snapshot.WorldCsxFullPath)
                    {
                        Exception = ex,
                    });
                    return results;
                }
            }
            else
            {
                return results;
            }

            cancellationToken.ThrowIfCancellationRequested();

            string scriptText = EditorLoadDirectiveRegex.Replace(rawWorldText, string.Empty);
            Script<object> script = CSharpScript.Create<object>(scriptText, options, typeof(Commander));
            ImmutableArray<Diagnostic> diagnostics = script.Compile(cancellationToken);

            foreach (Diagnostic diagnostic in diagnostics)
            {
                ErrorLevel errorLevel = diagnostic.Severity switch
                {
                    DiagnosticSeverity.Warning => ErrorLevel.Warning,
                    DiagnosticSeverity.Error => ErrorLevel.Error,
                    _ => ErrorLevel.Info,
                };
                if (errorLevel == ErrorLevel.Info)
                {
                    continue;
                }

                string message = diagnostic.GetMessage(CultureInfo.CurrentCulture);
                FileLinePositionSpan lineSpan = diagnostic.Location.GetLineSpan();
                string? sourcePath = diagnostic.Location.SourceTree?.FilePath;
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    sourcePath = snapshot.WorldCsxFullPath;
                }

                results.Add(new Error(errorLevel, message, sourcePath)
                {
                    Code = diagnostic.Id,
                    LineNumber = lineSpan.StartLinePosition.Line + 1,
                    LinePosition = lineSpan.StartLinePosition.Character + 1,
                });
            }
        }
        finally
        {
            compileContext.Unload();
        }

        return results;
    }

    private static bool IsFileDirty(WorldProject project, WorldFileKind kind, string normalizedRel)
    {
        foreach (WorldFileTarget dirty in project.DirtyFiles)
        {
            if (dirty.Kind == kind
                && string.Equals(WorldProject.NormalizeRelativePath(dirty.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ScriptOptions AddReferencesAndImports(ScriptOptions options, Assembly assembly)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        IEnumerable<string> namespaces = types
            .Where(static t => t is not null && !string.IsNullOrEmpty(t.Namespace))
            .Select(static t => t!.Namespace!)
            .Distinct(StringComparer.Ordinal);

        return options
            .AddReferences(assembly)
            .AddImports(namespaces);
    }

    private sealed class OverlayScriptSourceResolver : SourceReferenceResolver
    {
        private readonly string WorldDirectory;
        private readonly SourceReferenceResolver FallbackResolver;
        private readonly IReadOnlyDictionary<string, string> OverlayFiles;
        private readonly string EditorCsxSentinelPath;

        public OverlayScriptSourceResolver(
            string worldDirectory,
            SourceReferenceResolver fallbackResolver,
            IReadOnlyDictionary<string, string> overlayFiles)
        {
            WorldDirectory = worldDirectory;
            FallbackResolver = fallbackResolver;
            OverlayFiles = overlayFiles;
            EditorCsxSentinelPath = Path.GetFullPath(Path.Combine(worldDirectory, "__Editor.csx"));
        }

        public override bool Equals(object? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => FallbackResolver.GetHashCode();

        public override string? NormalizePath(string path, string? baseFilePath)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string trimmed = path.Trim();
            if (string.Equals(Path.GetFileName(trimmed), "__Editor.csx", StringComparison.OrdinalIgnoreCase))
            {
                return EditorCsxSentinelPath;
            }

            try
            {
                string baseDir = !string.IsNullOrWhiteSpace(baseFilePath)
                    ? (Path.GetDirectoryName(baseFilePath) ?? WorldDirectory)
                    : WorldDirectory;
                return Path.IsPathRooted(trimmed)
                    ? Path.GetFullPath(trimmed)
                    : Path.GetFullPath(Path.Combine(baseDir, trimmed));
            }
            catch
            {
                return FallbackResolver.NormalizePath(path, baseFilePath);
            }
        }

        public override string? ResolveReference(string reference, string? baseFilePath)
        {
            string? normalized = NormalizePath(reference, baseFilePath);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                if (string.Equals(normalized, EditorCsxSentinelPath, StringComparison.OrdinalIgnoreCase)
                    || OverlayFiles.ContainsKey(normalized)
                    || File.Exists(normalized))
                {
                    return normalized;
                }
            }

            return FallbackResolver.ResolveReference(reference, baseFilePath);
        }

        public override Stream OpenRead(string resolvedPath)
        {
            if (string.Equals(Path.GetFileName(resolvedPath), "__Editor.csx", StringComparison.OrdinalIgnoreCase))
            {
                return new MemoryStream(Array.Empty<byte>());
            }

            if (OverlayFiles.TryGetValue(resolvedPath, out string? overlayText))
            {
                string cleanedOverlay = EditorLoadDirectiveRegex.Replace(overlayText, string.Empty);
                return new MemoryStream(Encoding.UTF8.GetBytes(cleanedOverlay));
            }

            using Stream originalStream = FallbackResolver.OpenRead(resolvedPath);
            using StreamReader reader = new(originalStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string rawContent = reader.ReadToEnd();
            string cleaned = EditorLoadDirectiveRegex.Replace(rawContent, string.Empty);
            return new MemoryStream(Encoding.UTF8.GetBytes(cleaned));
        }
    }
}
