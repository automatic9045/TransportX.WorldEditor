namespace TransportX.WorldEditor.Serialization.Diagnostics;

/// <summary>
/// Represents a source-located diagnostic produced while parsing or validating a world file.
/// </summary>
public sealed record ParseDiagnostic(
    DiagnosticSeverity Severity,
    string FilePath,
    int Line,
    int Column,
    string Message);
