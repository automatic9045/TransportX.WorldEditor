using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Serialization.Diagnostics;

namespace TransportX.WorldEditor.Serialization.Import;

/// <summary>
/// Preview metadata inspected from a non-editor TransportX world source before importing.
/// </summary>
public sealed record WorldImportSourcePreview(
    string SourcePath,
    string SourceWorldDirectory,
    string SuggestedAuthor,
    string SuggestedWorldKey,
    string SuggestedTitle,
    string SuggestedDescription,
    string SuggestedDestinationParentDirectory);

/// <summary>
/// Input configuration for importing non-editor TransportX world scripts into an editor-managed project.
/// </summary>
public sealed record WorldImportOptions(
    string SourcePath,
    string DestinationParentDirectory,
    string Author,
    string WorldKey,
    string Title,
    string Description,
    bool CopyAssets = true,
    bool OverwriteExisting = false);

/// <summary>
/// Summary and diagnostics produced by <see cref="WorldProjectImporter.Import"/>.
/// </summary>
public sealed record WorldImportResult(
    bool Succeeded,
    string DestinationWorldInfoPath,
    string DestinationWorldDirectory,
    WorldProject? Project,
    IReadOnlyList<ParseDiagnostic> Diagnostics,
    int ImportedSplineTemplateCount,
    int ImportedJunctionTemplateCount,
    int ImportedSplineCount,
    int ImportedJunctionCount,
    int ImportedPropCount,
    int PreservedUserCodeStatementCount,
    int CopiedAssetFileCount);
