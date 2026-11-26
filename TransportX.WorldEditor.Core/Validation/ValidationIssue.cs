namespace TransportX.WorldEditor.Core.Validation;

/// <summary>
/// Severity of a domain validation issue.
/// </summary>
public enum ValidationSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Represents a semantic/topological validation issue detected in a <c>WorldProject</c>.
/// </summary>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Message,
    string? SourcePath = null);
