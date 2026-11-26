using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Init;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Serialization.Csx;
using TransportX.WorldEditor.Serialization.Diagnostics;
using DiagnosticSeverity = TransportX.WorldEditor.Serialization.Diagnostics.DiagnosticSeverity;

namespace TransportX.WorldEditor.Serialization.Import;

/// <summary>
/// Traverses non-editor TransportX scripts (entry <c>World.csx</c> and <c>#load</c> dependencies)
/// using Roslyn syntax analysis and reconstructs canonical editor documents (<see cref="InitDocument"/>,
/// <see cref="SplineTemplateDocument"/>, <see cref="JunctionTemplateDocument"/>, <see cref="WorldPlacementDocument"/>).
/// </summary>
internal sealed class CsxScriptImportEngine
{
    private const int MaxLoopIterations = 512;

    private static readonly CSharpParseOptions ScriptParseOptions = new(
        LanguageVersion.Latest,
        DocumentationMode.None,
        SourceCodeKind.Script);

    private abstract class BoundValue
    {
    }

    private sealed class SplineTemplateBinding : BoundValue
    {
        public SplineTemplateDocument Document { get; }
        public StringBuilder UserCodeBuilder { get; } = new();

        public SplineTemplateBinding(SplineTemplateDocument document)
        {
            Document = document;
        }
    }

    private sealed class JunctionTemplateBinding : BoundValue
    {
        public JunctionTemplateDocument Document { get; }
        public HashSet<string> UsedVarNames { get; } = new(StringComparer.Ordinal) { "tJunction" };
        public HashSet<string> DeclaredAnchors { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> AnchorAliases { get; } = new(StringComparer.Ordinal);
        public StringBuilder UserCodeBuilder { get; } = new();

        public JunctionTemplateBinding(JunctionTemplateDocument document)
        {
            Document = document;
        }

        public string AllocateAnchor(string requestedName)
        {
            string ssaName = CsxScriptImportEngine.AllocateUniqueAnchor(requestedName, DeclaredAnchors);
            AnchorAliases[requestedName] = ssaName;
            return ssaName;
        }
    }

    private sealed class JunctionPathBinding : BoundValue
    {
        public JunctionTemplateBinding ParentTemplate { get; }
        public JunctionPathTemplateModel Path { get; }
        public string CanonicalVarName { get; }

        public JunctionPathBinding(JunctionTemplateBinding parentTemplate, JunctionPathTemplateModel path, string canonicalVarName)
        {
            ParentTemplate = parentTemplate;
            Path = path;
            CanonicalVarName = canonicalVarName;
        }
    }

    private sealed class JunctionCurveBinding : BoundValue
    {
        public JunctionTemplateBinding ParentTemplate { get; }
        public JunctionCurveTemplateModel Curve { get; }
        public string CanonicalVarName { get; }

        public JunctionCurveBinding(JunctionTemplateBinding parentTemplate, JunctionCurveTemplateModel curve, string canonicalVarName)
        {
            ParentTemplate = parentTemplate;
            Curve = curve;
            CanonicalVarName = canonicalVarName;
        }
    }

    private sealed class PendingSplineFactoryBinding : BoundValue
    {
        public SplinePlacementNode Node { get; }
        public List<string> PendingPreBuildRawLines { get; } = new();
        public string? ExplicitKey { get; set; }

        public PendingSplineFactoryBinding(SplinePlacementNode node)
        {
            Node = node;
        }
    }

    private sealed class PendingJunctionFactoryBinding : BoundValue
    {
        public JunctionPlacementNode Node { get; }
        public List<string> PendingPreBuildRawLines { get; } = new();
        public string? ExplicitKey { get; set; }

        public PendingJunctionFactoryBinding(JunctionPlacementNode node)
        {
            Node = node;
        }
    }

    private sealed class BuiltNodeBinding : BoundValue
    {
        public bool IsJunction { get; }
        public string NodeId { get; }

        public BuiltNodeBinding(bool isJunction, string nodeId)
        {
            IsJunction = isJunction;
            NodeId = nodeId;
        }
    }

    private sealed class LexicalVarScope
    {
        public Dictionary<string, BoundValue> Bindings { get; } = new(StringComparer.Ordinal);
        public JunctionTemplateBinding? ActiveJunctionTemplate { get; set; }
        public SplineTemplateBinding? ActiveSplineTemplate { get; set; }
    }

    private readonly string SourceWorldDirectory;
    private readonly WorldProject TargetProject;
    private readonly List<ParseDiagnostic> Diagnostics;
    private readonly ImportExpressionEvaluator Evaluator = new();
    private readonly List<LexicalVarScope> VarScopes = new();
    private readonly HashSet<string> VisitedScripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> AbsorbedScriptPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> UsedNodeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> BuiltSplineIdByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> BuiltJunctionIdByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> WorldDeclaredAnchors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> WorldAnchorAliases = new(StringComparer.Ordinal);
    private readonly StringBuilder InitUserCodeBuilder = new();
    private readonly StringBuilder WorldUserCodeBuilder = new();
    private readonly List<SplineTemplateBinding> AllSplineTemplateBindings = new();
    private readonly List<JunctionTemplateBinding> AllJunctionTemplateBindings = new();

    private int PropCounter;
    private int SplineCounter;
    private int JunctionCounter;
    private int PreservedUserCodeCount;

    public CsxScriptImportEngine(string sourceWorldDirectory, WorldProject targetProject, List<ParseDiagnostic> diagnostics)
    {
        SourceWorldDirectory = Path.GetFullPath(sourceWorldDirectory);
        TargetProject = targetProject;
        Diagnostics = diagnostics;
        VarScopes.Add(new LexicalVarScope());
    }

    public IReadOnlySet<string> AbsorbedScripts => AbsorbedScriptPaths;
    public int PreservedUserCodeStatementCount => PreservedUserCodeCount;

    public void Execute(string entryScriptFullPath)
    {
        ProcessScriptFile(entryScriptFullPath, isEntryScript: true);

        TargetProject.Init.UserCode = InitUserCodeBuilder.ToString().TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(TargetProject.Init.UserCode))
        {
            TargetProject.Init.UserCode = null;
        }

        TargetProject.Placement.UserCode = WorldUserCodeBuilder.ToString().TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(TargetProject.Placement.UserCode))
        {
            TargetProject.Placement.UserCode = null;
        }

        foreach (SplineTemplateBinding sb in AllSplineTemplateBindings)
        {
            string code = sb.UserCodeBuilder.ToString().TrimEnd('\r', '\n');
            sb.Document.UserCode = string.IsNullOrWhiteSpace(code) ? null : code;
        }

        foreach (JunctionTemplateBinding jb in AllJunctionTemplateBindings)
        {
            string code = jb.UserCodeBuilder.ToString().TrimEnd('\r', '\n');
            jb.Document.UserCode = string.IsNullOrWhiteSpace(code) ? null : code;
        }
    }

    private void PushVarScope()
    {
        Evaluator.PushScope();
        VarScopes.Add(new LexicalVarScope());
    }

    private void PopVarScope()
    {
        Evaluator.PopScope();
        if (1 < VarScopes.Count)
        {
            VarScopes.RemoveAt(VarScopes.Count - 1);
        }
    }

    private void SetBinding(string varName, BoundValue value, bool preferExistingScope)
    {
        if (preferExistingScope)
        {
            for (int i = VarScopes.Count - 1; 0 <= i; i--)
            {
                if (VarScopes[i].Bindings.ContainsKey(varName))
                {
                    VarScopes[i].Bindings[varName] = value;
                    return;
                }
            }
        }

        VarScopes[^1].Bindings[varName] = value;
    }

    private bool TryGetBinding(string varName, out BoundValue? value)
    {
        for (int i = VarScopes.Count - 1; 0 <= i; i--)
        {
            if (VarScopes[i].Bindings.TryGetValue(varName, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private JunctionTemplateBinding? GetActiveJunctionTemplate()
    {
        for (int i = VarScopes.Count - 1; 0 <= i; i--)
        {
            if (VarScopes[i].ActiveJunctionTemplate is not null)
            {
                return VarScopes[i].ActiveJunctionTemplate;
            }
        }

        return null;
    }

    private SplineTemplateBinding? GetActiveSplineTemplate()
    {
        for (int i = VarScopes.Count - 1; 0 <= i; i--)
        {
            if (VarScopes[i].ActiveSplineTemplate is not null)
            {
                return VarScopes[i].ActiveSplineTemplate;
            }
        }

        return null;
    }

    private void ProcessScriptFile(string fullScriptPath, bool isEntryScript)
    {
        string normalizedFull = Path.GetFullPath(fullScriptPath);
        if (!VisitedScripts.Add(normalizedFull))
        {
            return;
        }

        if (!File.Exists(normalizedFull))
        {
            Diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                normalizedFull,
                1,
                1,
                $"インポート対象のスクリプトが見つかりません: '{normalizedFull}'"));
            return;
        }

        string sourceText = File.ReadAllText(normalizedFull, Encoding.UTF8);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceText, ScriptParseOptions, normalizedFull);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

        foreach (Diagnostic diag in tree.GetDiagnostics())
        {
            if (diag.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            {
                FileLinePositionSpan span = diag.Location.GetLineSpan();
                Diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Warning,
                    normalizedFull,
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1,
                    $"元スクリプト内の C# 診断メッセージ: {diag.GetMessage()}"));
            }
        }

        string scriptDir = Path.GetDirectoryName(normalizedFull) ?? SourceWorldDirectory;
        foreach (LoadDirectiveTriviaSyntax loadDirective in root.GetLoadDirectives())
        {
            string rawTarget = loadDirective.File.ValueText;
            if (string.IsNullOrWhiteSpace(rawTarget))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(rawTarget), "__Editor.csx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string targetFull = Path.GetFullPath(Path.Combine(scriptDir, rawTarget));
            if (!File.Exists(targetFull))
            {
                string missingRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(SourceWorldDirectory, targetFull));
                TargetProject.Placement.ExtraLoadScripts.Add(missingRel);
                Diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Warning,
                    normalizedFull,
                    loadDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    1,
                    $"#load 対象ファイル '{rawTarget}' が見つかりませんでした。ExtraLoadScripts にそのまま保持します。"));
                continue;
            }

            ProcessScriptFile(targetFull, isEntryScript: false);
        }

        bool fileLooksLikeInit = !isEntryScript
            && (string.Equals(Path.GetFileName(normalizedFull), "Init.csx", StringComparison.OrdinalIgnoreCase)
                || sourceText.Contains("Models.LoadList", StringComparison.Ordinal)
                || sourceText.Contains("Sounds.LoadList", StringComparison.Ordinal)
                || sourceText.Contains("CrossSections.Load", StringComparison.Ordinal)
                || sourceText.Contains("Network.LaneLayouts.Load", StringComparison.Ordinal)
                || sourceText.Contains("Network.LaneTraffic.", StringComparison.Ordinal));

        List<StatementSyntax> topLevelStatements = new();
        foreach (MemberDeclarationSyntax member in root.Members)
        {
            if (member is GlobalStatementSyntax globalStmt)
            {
                topLevelStatements.Add(globalStmt.Statement);
            }
            else if (member is FieldDeclarationSyntax fieldDecl)
            {
                topLevelStatements.Add(
                    SyntaxFactory.LocalDeclarationStatement(fieldDecl.Modifiers, fieldDecl.Declaration)
                        .WithTriviaFrom(fieldDecl));
            }
            else if (member is not IncompleteMemberSyntax)
            {
                Diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Info,
                    normalizedFull,
                    member.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    1,
                    $"トップレベル宣言を <user-code> へ保持しました: {member}"));
            }
        }

        int recognizedInFile = 0;
        List<string> customScriptFallbackLines = new();
        foreach (StatementSyntax stmt in topLevelStatements)
        {
            int countBefore = recognizedInFile;
            ProcessStatement(
                stmt,
                normalizedFull,
                fileLooksLikeInit,
                isCustomFallbackProbe: !isEntryScript && !fileLooksLikeInit,
                customScriptFallbackLines,
                ref recognizedInFile);
        }

        if (isEntryScript || fileLooksLikeInit || 0 < recognizedInFile)
        {
            AbsorbedScriptPaths.Add(normalizedFull);
            if (!isEntryScript && !fileLooksLikeInit && 0 < customScriptFallbackLines.Count)
            {
                foreach (string line in customScriptFallbackLines)
                {
                    AppendIndentedLine(WorldUserCodeBuilder, line);
                }
            }
        }
        else
        {
            // Pure custom script (no Init/Template/Placement commands): keep it as an external #load script
            string relPath = WorldProject.NormalizeRelativePath(Path.GetRelativePath(SourceWorldDirectory, normalizedFull));
            if (!TargetProject.Placement.ExtraLoadScripts.Contains(relPath, StringComparer.OrdinalIgnoreCase))
            {
                TargetProject.Placement.ExtraLoadScripts.Add(relPath);
            }

            Diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Info,
                normalizedFull,
                1,
                1,
                $"カスタムスクリプト '{relPath}' をそのまま ExtraLoadScripts (#load) として引き継ぎました。"));
        }
    }

    private void ProcessStatement(
        StatementSyntax statement,
        string filePath,
        bool isInitContext,
        bool isCustomFallbackProbe,
        List<string>? customFallbackLines,
        ref int recognizedCount)
    {
        if (statement is EmptyStatementSyntax)
        {
            return;
        }

        string? statementDisplayName = RoslynCsxHelpers.ExtractStatementDisplayName(statement);

        if (statement is BlockSyntax block)
        {
            PushVarScope();
            foreach (StatementSyntax inner in block.Statements)
            {
                ProcessStatement(inner, filePath, isInitContext, isCustomFallbackProbe, customFallbackLines, ref recognizedCount);
            }

            PopVarScope();
            return;
        }

        if (statement is ForStatementSyntax forStmt)
        {
            if (TryUnrollForStatement(forStmt, filePath, isInitContext, isCustomFallbackProbe, customFallbackLines, ref recognizedCount))
            {
                return;
            }
        }

        if (statement is LocalDeclarationStatementSyntax localDecl && 0 < localDecl.Declaration.Variables.Count)
        {
            bool handledAnyDecl = false;
            foreach (VariableDeclaratorSyntax declarator in localDecl.Declaration.Variables)
            {
                string varName = declarator.Identifier.ValueText;
                if (declarator.Initializer?.Value is ExpressionSyntax initExpr
                    && TryHandleValueAssignment(
                        varName,
                        localDecl.Declaration.Type,
                        initExpr,
                        isDeclaration: true,
                        filePath,
                        isInitContext,
                        statementDisplayName,
                        ref recognizedCount))
                {
                    handledAnyDecl = true;
                }
            }

            if (handledAnyDecl)
            {
                return;
            }
        }

        if (statement is ExpressionStatementSyntax exprStmt)
        {
            if (exprStmt.Expression is AssignmentExpressionSyntax assign
                && assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                if (assign.Left is MemberAccessExpressionSyntax leftMa
                    && leftMa.Name.Identifier.ValueText == "Key"
                    && leftMa.Expression is IdentifierNameSyntax keyTargetIdent
                    && TryGetBinding(keyTargetIdent.Identifier.ValueText, out BoundValue? keyBinding)
                    && Evaluator.TryEvalNullableString(assign.Right, out string? explicitKey))
                {
                    if (keyBinding is PendingSplineFactoryBinding psKey)
                    {
                        psKey.ExplicitKey = explicitKey;
                        recognizedCount++;
                        return;
                    }

                    if (keyBinding is PendingJunctionFactoryBinding pjKey)
                    {
                        pjKey.ExplicitKey = explicitKey;
                        recognizedCount++;
                        return;
                    }
                }

                if (assign.Left is IdentifierNameSyntax lhsIdent
                    && TryHandleValueAssignment(
                        lhsIdent.Identifier.ValueText,
                        null,
                        assign.Right,
                        isDeclaration: false,
                        filePath,
                        isInitContext,
                        statementDisplayName,
                        ref recognizedCount))
                {
                    return;
                }
            }

            if (TryHandleCompoundScalarAssignment(exprStmt.Expression, ref recognizedCount))
            {
                return;
            }

            if (exprStmt.Expression is InvocationExpressionSyntax inv
                && TryHandleInvocationStatement(inv, filePath, isInitContext, statementDisplayName, ref recognizedCount))
            {
                return;
            }
        }

        // Preserved fallback statement
        PreserveUnrecognizedStatement(statement, filePath, isInitContext, isCustomFallbackProbe, customFallbackLines);
    }

    private bool TryUnrollForStatement(
        ForStatementSyntax forStmt,
        string filePath,
        bool isInitContext,
        bool isCustomFallbackProbe,
        List<string>? customFallbackLines,
        ref int recognizedCount)
    {
        if (forStmt.Declaration is null
            || forStmt.Declaration.Variables.Count != 1
            || forStmt.Initializers.Count != 0
            || forStmt.Incrementors.Count != 1
            || forStmt.Condition is not BinaryExpressionSyntax condition)
        {
            return false;
        }

        VariableDeclaratorSyntax loopVarDecl = forStmt.Declaration.Variables[0];
        string loopVarName = loopVarDecl.Identifier.ValueText;
        if (loopVarDecl.Initializer?.Value is not ExpressionSyntax initExpr
            || !Evaluator.TryEvalDouble(initExpr, out double startVal))
        {
            return false;
        }

        if (condition.Left is not IdentifierNameSyntax condLeft
            || condLeft.Identifier.ValueText != loopVarName
            || !Evaluator.TryEvalDouble(condition.Right, out double boundVal))
        {
            return false;
        }

        if (!TryExtractLoopStep(forStmt.Incrementors[0], loopVarName, out double stepVal))
        {
            return false;
        }

        bool EvalCondition(double cur)
        {
            if (condition.IsKind(SyntaxKind.LessThanExpression))
            {
                return cur < boundVal;
            }

            if (condition.IsKind(SyntaxKind.LessThanOrEqualExpression))
            {
                return cur <= boundVal;
            }

            if (condition.IsKind(SyntaxKind.GreaterThanExpression))
            {
                return boundVal < cur;
            }

            if (condition.IsKind(SyntaxKind.GreaterThanOrEqualExpression))
            {
                return boundVal <= cur;
            }

            if (condition.IsKind(SyntaxKind.NotEqualsExpression))
            {
                return 1e-12 < double.Abs(cur - boundVal);
            }

            return false;
        }

        // Dry-run loop count so runaway/infinite loops cannot hang or exhaust memory
        double probe = startVal;
        int iterCount = 0;
        while (EvalCondition(probe))
        {
            iterCount++;
            if (MaxLoopIterations < iterCount)
            {
                return false;
            }

            probe += stepVal;
        }

        PushVarScope();
        try
        {
            double current = startVal;
            for (int iter = 0; iter < iterCount; iter++)
            {
                Evaluator.SetDouble(loopVarName, current, preferExistingScope: false);
                PushVarScope();
                try
                {
                    if (forStmt.Statement is BlockSyntax innerBlock)
                    {
                        foreach (StatementSyntax inner in innerBlock.Statements)
                        {
                            ProcessStatement(inner, filePath, isInitContext, isCustomFallbackProbe, customFallbackLines, ref recognizedCount);
                        }
                    }
                    else
                    {
                        ProcessStatement(forStmt.Statement, filePath, isInitContext, isCustomFallbackProbe, customFallbackLines, ref recognizedCount);
                    }
                }
                finally
                {
                    PopVarScope();
                }

                current += stepVal;
            }
        }
        finally
        {
            PopVarScope();
        }

        return true;
    }

    private bool TryExtractLoopStep(ExpressionSyntax incExpr, string loopVarName, out double step)
    {
        step = 0.0;
        if (incExpr is PostfixUnaryExpressionSyntax post && post.Operand is IdentifierNameSyntax postId && postId.Identifier.ValueText == loopVarName)
        {
            if (post.IsKind(SyntaxKind.PostIncrementExpression))
            {
                step = 1.0;
                return true;
            }

            if (post.IsKind(SyntaxKind.PostDecrementExpression))
            {
                step = -1.0;
                return true;
            }
        }

        if (incExpr is PrefixUnaryExpressionSyntax pre && pre.Operand is IdentifierNameSyntax preId && preId.Identifier.ValueText == loopVarName)
        {
            if (pre.IsKind(SyntaxKind.PreIncrementExpression))
            {
                step = 1.0;
                return true;
            }

            if (pre.IsKind(SyntaxKind.PreDecrementExpression))
            {
                step = -1.0;
                return true;
            }
        }

        if (incExpr is AssignmentExpressionSyntax assign
            && assign.Left is IdentifierNameSyntax lhs
            && lhs.Identifier.ValueText == loopVarName)
        {
            if (assign.IsKind(SyntaxKind.AddAssignmentExpression) && Evaluator.TryEvalDouble(assign.Right, out double addStep))
            {
                step = addStep;
                return 0.0 != step;
            }

            if (assign.IsKind(SyntaxKind.SubtractAssignmentExpression) && Evaluator.TryEvalDouble(assign.Right, out double subStep))
            {
                step = -subStep;
                return 0.0 != step;
            }

            if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && assign.Right is BinaryExpressionSyntax bin
                && bin.Left is IdentifierNameSyntax bLeft
                && bLeft.Identifier.ValueText == loopVarName
                && Evaluator.TryEvalDouble(bin.Right, out double exprStep))
            {
                if (bin.IsKind(SyntaxKind.AddExpression))
                {
                    step = exprStep;
                    return 0.0 != step;
                }

                if (bin.IsKind(SyntaxKind.SubtractExpression))
                {
                    step = -exprStep;
                    return 0.0 != step;
                }
            }
        }

        return false;
    }

    private bool TryHandleCompoundScalarAssignment(ExpressionSyntax expr, ref int recognizedCount)
    {
        if (expr is AssignmentExpressionSyntax assign && assign.Left is IdentifierNameSyntax ident)
        {
            string name = ident.Identifier.ValueText;
            if (Evaluator.TryGetDouble(name, out double cur) && Evaluator.TryEvalDouble(assign.Right, out double rhs))
            {
                if (assign.IsKind(SyntaxKind.AddAssignmentExpression))
                {
                    Evaluator.SetDouble(name, cur + rhs, preferExistingScope: true);
                    return true;
                }

                if (assign.IsKind(SyntaxKind.SubtractAssignmentExpression))
                {
                    Evaluator.SetDouble(name, cur - rhs, preferExistingScope: true);
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryHandleValueAssignment(
        string targetVarName,
        TypeSyntax? declType,
        ExpressionSyntax initExpr,
        bool isDeclaration,
        string filePath,
        bool isInitContext,
        string? statementDisplayName,
        ref int recognizedCount)
    {
        bool preferExisting = !isDeclaration;

        if (initExpr is InvocationExpressionSyntax inv)
        {
            (ExpressionSyntax rootReceiver, List<InvocationExpressionSyntax> calls) = RoslynCsxHelpers.UnwrapFluentChain(inv);
            if (0 < calls.Count)
            {
                // 1. Template creation: Network.Templates.CreateSpline(...) / CreateJunction(...)
                if (rootReceiver.ToString() == "Network.Templates")
                {
                    string? firstMethod = RoslynCsxHelpers.GetInvokedMethodName(calls[0]);
                    SeparatedSyntaxList<ArgumentSyntax> firstArgs = calls[0].ArgumentList.Arguments;

                    if (firstMethod == "CreateSpline"
                        && firstArgs.Count == 2
                        && Evaluator.TryEvalString(firstArgs[0].Expression, out string splineKey)
                        && Evaluator.TryEvalString(firstArgs[1].Expression, out string layoutKey))
                    {
                        SplineTemplateDocument doc = new()
                        {
                            Key = splineKey,
                            OutletLayoutKey = layoutKey,
                            RelativePath = Storage.WorldProjectStorage.BuildSplineTemplateRelativePath(splineKey),
                        };
                        TargetProject.SplineTemplates[doc.Key] = doc;

                        SplineTemplateBinding binding = new(doc);
                        AllSplineTemplateBindings.Add(binding);
                        SetBinding(targetVarName, binding, preferExisting);
                        VarScopes[^1].ActiveSplineTemplate = binding;

                        ApplySplineTemplateCalls(calls, 1, binding, filePath);
                        recognizedCount++;
                        return true;
                    }

                    if (firstMethod == "CreateJunction"
                        && firstArgs.Count == 1
                        && Evaluator.TryEvalString(firstArgs[0].Expression, out string juncKey))
                    {
                        JunctionTemplateDocument doc = new()
                        {
                            Key = juncKey,
                            RelativePath = Storage.WorldProjectStorage.BuildJunctionTemplateRelativePath(juncKey),
                        };
                        TargetProject.JunctionTemplates[doc.Key] = doc;

                        JunctionTemplateBinding binding = new(doc);
                        AllJunctionTemplateBindings.Add(binding);
                        SetBinding(targetVarName, binding, preferExisting);
                        VarScopes[^1].ActiveJunctionTemplate = binding;

                        ApplyJunctionTemplateFluentTail(calls, 1, binding);
                        recognizedCount++;
                        return true;
                    }
                }

                // 2. Junction sub-elements: tJunction.Wire(...) / tJunction.AddCurve(...)
                if (rootReceiver is IdentifierNameSyntax junReceiverIdent
                    && TryGetBinding(junReceiverIdent.Identifier.ValueText, out BoundValue? junBound)
                    && junBound is JunctionTemplateBinding junBinding)
                {
                    string? firstMethod = RoslynCsxHelpers.GetInvokedMethodName(calls[0]);
                    SeparatedSyntaxList<ArgumentSyntax> firstArgs = calls[0].ArgumentList.Arguments;

                    if (firstMethod == "Wire"
                        && firstArgs.Count == 5
                        && Evaluator.TryEvalString(firstArgs[0].Expression, out string pathKey)
                        && Evaluator.TryEvalString(firstArgs[1].Expression, out string fromPort)
                        && Evaluator.TryEvalInt(firstArgs[2].Expression, out int fromPin)
                        && Evaluator.TryEvalString(firstArgs[3].Expression, out string toPort)
                        && Evaluator.TryEvalInt(firstArgs[4].Expression, out int toPin))
                    {
                        JunctionPathTemplateModel path = new()
                        {
                            Key = pathKey,
                            FromPortKey = fromPort,
                            FromPinIndex = fromPin,
                            ToPortKey = toPort,
                            ToPinIndex = toPin,
                        };
                        junBinding.Document.Paths.Add(path);

                        string canonicalPathVar = JunctionTemplateCsxSerializer.AllocateUniqueVarName("tPath", pathKey, junBinding.UsedVarNames);
                        JunctionPathBinding pathBinding = new(junBinding, path, canonicalPathVar);
                        SetBinding(targetVarName, pathBinding, preferExisting);

                        ApplyJunctionPathCallChain(calls, 1, pathBinding);
                        recognizedCount++;
                        return true;
                    }

                    if (firstMethod == "AddCurve"
                        && TryParseJunctionAddCurveArgs(firstArgs, out string curveKey, out JunctionCurveOriginModel? origin))
                    {
                        JunctionCurveTemplateModel curve = new()
                        {
                            Key = curveKey,
                            Origin = origin!,
                        };
                        junBinding.Document.Curves.Add(curve);

                        string canonicalCurveVar = JunctionTemplateCsxSerializer.AllocateUniqueVarName("tCurve", curveKey, junBinding.UsedVarNames);
                        JunctionCurveBinding curveBinding = new(junBinding, curve, canonicalCurveVar);
                        SetBinding(targetVarName, curveBinding, preferExisting);

                        ApplyJunctionCurveCallChain(calls, 1, curveBinding);
                        recognizedCount++;
                        return true;
                    }
                }

                // 3. Build statement: var spline = fSpline.Build(); / var junc = fJunction.Build();
                if (calls.Count == 1
                    && RoslynCsxHelpers.GetInvokedMethodName(calls[0]) == "Build"
                    && calls[0].ArgumentList.Arguments.Count == 0
                    && rootReceiver is IdentifierNameSyntax buildTargetIdent
                    && TryGetBinding(buildTargetIdent.Identifier.ValueText, out BoundValue? factoryBound))
                {
                    if (factoryBound is PendingSplineFactoryBinding pendingSpline)
                    {
                        string builtId = FinalizeBuiltSpline(pendingSpline, buildTargetIdent.Identifier.ValueText, statementDisplayName);
                        SetBinding(targetVarName, new BuiltNodeBinding(isJunction: false, builtId), preferExisting);
                        recognizedCount++;
                        return true;
                    }

                    if (factoryBound is PendingJunctionFactoryBinding pendingJunction)
                    {
                        string builtId = FinalizeBuiltJunction(pendingJunction, buildTargetIdent.Identifier.ValueText, statementDisplayName);
                        SetBinding(targetVarName, new BuiltNodeBinding(isJunction: true, builtId), preferExisting);
                        recognizedCount++;
                        return true;
                    }
                }

                // 4. Placement factory start: Chunks[cx, cz].BeginSpline/PutJunction, spline.IntoSpline/IntoJunction, junction.IntoSpline/IntoJunction
                if (TryParsePlacementFactoryStart(rootReceiver, calls, out BoundValue? startedBinding))
                {
                    if (!string.IsNullOrWhiteSpace(statementDisplayName))
                    {
                        if (startedBinding is PendingSplineFactoryBinding startedSpline)
                        {
                            startedSpline.Node.DisplayName = statementDisplayName;
                        }
                        else if (startedBinding is PendingJunctionFactoryBinding startedJunction)
                        {
                            startedJunction.Node.DisplayName = statementDisplayName;
                        }
                    }

                    SetBinding(targetVarName, startedBinding!, preferExisting);
                    recognizedCount++;
                    return true;
                }
            }
        }

        // 5. Numeric / string local variable (when type isn't explicitly incompatible)
        if (!isInitContext && (declType is null || declType.ToString() != "string") && Evaluator.TryEvalDouble(initExpr, out double numVal))
        {
            Evaluator.SetDouble(targetVarName, numVal, preferExisting);
            return true;
        }

        if (Evaluator.TryEvalString(initExpr, out string strVal))
        {
            Evaluator.SetString(targetVarName, strVal, preferExisting);
            return true;
        }

        return false;
    }

    private bool TryHandleInvocationStatement(
        InvocationExpressionSyntax outerInvocation,
        string filePath,
        bool isInitContext,
        string? statementDisplayName,
        ref int recognizedCount)
    {
        (ExpressionSyntax rootReceiver, List<InvocationExpressionSyntax> calls) = RoslynCsxHelpers.UnwrapFluentChain(outerInvocation);
        if (calls.Count == 0)
        {
            return false;
        }

        // 1. Bare Init / Global single calls
        if (calls.Count == 1 && TryHandleInitInvocation(outerInvocation, filePath))
        {
            recognizedCount++;
            return true;
        }

        if (calls.Count == 1 && TryHandleGlobalWorldCall(rootReceiver, calls[0], filePath))
        {
            recognizedCount++;
            return true;
        }

        // 2. Chunks[cx, cz].PutProp(...)
        if (calls.Count == 1 && TryParseChunksIndexer(rootReceiver, out ChunkIndexModel chunk))
        {
            InvocationExpressionSyntax call = calls[0];
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;
            if (RoslynCsxHelpers.GetInvokedMethodName(call) == "PutProp"
                && (args.Count == 4 || args.Count == 7)
                && Evaluator.TryEvalString(args[0].Expression, out string modelKey)
                && TryReadPose3Or6(args, 1, out PoseDegreesModel pose))
            {
                PropCounter++;
                string? propDisplay = string.IsNullOrWhiteSpace(statementDisplayName) ? null : statementDisplayName;
                TargetProject.Placement.Props.Add(new ChunkPropPlacement($"prop_{PropCounter:D3}", modelKey, chunk, pose, propDisplay));
                recognizedCount++;
                return true;
            }
        }

        // 3. Member timeline / Ports / Paths / Curves overrides:
        //    fSpline.Curves / Gradients / Cants, tPath.Width, fJunction.Paths[key] / Curves[key]
        if (rootReceiver is MemberAccessExpressionSyntax maRoot && maRoot.Expression is IdentifierNameSyntax ownerIdent)
        {
            string ownerName = ownerIdent.Identifier.ValueText;
            string memberName = maRoot.Name.Identifier.ValueText;
            if (TryGetBinding(ownerName, out BoundValue? ownerBound))
            {
                if (ownerBound is PendingSplineFactoryBinding ps)
                {
                    if (memberName == "Curves" && TryApplySplineCurveCalls(calls, ps.Node))
                    {
                        recognizedCount++;
                        return true;
                    }

                    if (memberName == "Gradients" && TryApplySplineGradientCalls(calls, ps.Node))
                    {
                        recognizedCount++;
                        return true;
                    }

                    if (memberName == "Cants" && TryApplySplineCantCalls(calls, ps.Node))
                    {
                        recognizedCount++;
                        return true;
                    }

                    if (memberName is "Curves" or "Gradients" or "Cants")
                    {
                        ps.PendingPreBuildRawLines.Add($".{memberName}{FormatChainBody(calls, CreateWorldAndTemplateIdentifierMap(null))}");
                        recognizedCount++;
                        return true;
                    }
                }

                if (ownerBound is JunctionPathBinding jp && memberName == "Width")
                {
                    ApplyJunctionWidthCallChain(calls, jp);
                    recognizedCount++;
                    return true;
                }
            }
        }

        if (rootReceiver is ElementAccessExpressionSyntax elemAccess
            && elemAccess.ArgumentList.Arguments.Count == 1
            && Evaluator.TryEvalString(elemAccess.ArgumentList.Arguments[0].Expression, out string subKey)
            && elemAccess.Expression is MemberAccessExpressionSyntax collMa
            && collMa.Expression is IdentifierNameSyntax junFactIdent
            && TryGetBinding(junFactIdent.Identifier.ValueText, out BoundValue? junFactBound)
            && junFactBound is PendingJunctionFactoryBinding pjOverride)
        {
            string collName = collMa.Name.Identifier.ValueText;
            if (collName == "Paths")
            {
                ApplyJunctionPathOverrideCalls(subKey, calls, pjOverride);
                recognizedCount++;
                return true;
            }

            if (collName == "Curves")
            {
                ApplyJunctionCurveOverrideCalls(subKey, calls, pjOverride);
                recognizedCount++;
                return true;
            }
        }

        // 4. Bare calls on identifier receiver: tSpline.*, tJunction.*, tPath.*, tCurve.*, fSpline.*, fJunction.*
        if (rootReceiver is IdentifierNameSyntax targetIdent
            && TryGetBinding(targetIdent.Identifier.ValueText, out BoundValue? targetBound))
        {
            if (targetBound is SplineTemplateBinding st)
            {
                ApplySplineTemplateCalls(calls, 0, st, filePath);
                recognizedCount++;
                return true;
            }

            if (targetBound is JunctionTemplateBinding jt)
            {
                ApplyJunctionTemplateFluentTail(calls, 0, jt);
                recognizedCount++;
                return true;
            }

            if (targetBound is JunctionPathBinding jp)
            {
                ApplyJunctionPathCallChain(calls, 0, jp);
                recognizedCount++;
                return true;
            }

            if (targetBound is JunctionCurveBinding jc)
            {
                ApplyJunctionCurveCallChain(calls, 0, jc);
                recognizedCount++;
                return true;
            }

            if (targetBound is PendingSplineFactoryBinding psf)
            {
                if (calls.Count == 1
                    && RoslynCsxHelpers.GetInvokedMethodName(calls[0]) == "Build"
                    && calls[0].ArgumentList.Arguments.Count == 0)
                {
                    FinalizeBuiltSpline(psf, targetIdent.Identifier.ValueText, statementDisplayName);
                    recognizedCount++;
                    return true;
                }

                ApplyPendingSplineFactoryCalls(calls, psf);
                recognizedCount++;
                return true;
            }

            if (targetBound is PendingJunctionFactoryBinding pjf)
            {
                if (calls.Count == 1
                    && RoslynCsxHelpers.GetInvokedMethodName(calls[0]) == "Build"
                    && calls[0].ArgumentList.Arguments.Count == 0)
                {
                    FinalizeBuiltJunction(pjf, targetIdent.Identifier.ValueText, statementDisplayName);
                    recognizedCount++;
                    return true;
                }

                ApplyPendingJunctionFactoryCalls(calls, pjf);
                recognizedCount++;
                return true;
            }
        }

        return false;
    }

    private bool TryHandleInitInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax ma)
        {
            return false;
        }

        string receiverPath = ma.Expression.ToString();
        string methodName = ma.Name.Identifier.ValueText;
        SeparatedSyntaxList<ArgumentSyntax> args = invocation.ArgumentList.Arguments;

        if ((receiverPath == "Models" || receiverPath == "Sounds") && methodName == "LoadList")
        {
            string? bundleKey = null;
            string path;
            if (args.Count == 1 && Evaluator.TryEvalString(args[0].Expression, out string p1))
            {
                path = ResolveScriptAssetPath(p1, filePath);
            }
            else if (args.Count == 2
                && Evaluator.TryEvalString(args[0].Expression, out string k)
                && Evaluator.TryEvalString(args[1].Expression, out string p2))
            {
                bundleKey = k;
                path = ResolveScriptAssetPath(p2, filePath);
            }
            else
            {
                return false;
            }

            if (receiverPath == "Models")
            {
                TargetProject.Init.ModelLists.Add(new AssetListLoadReference(bundleKey, path));
            }
            else
            {
                TargetProject.Init.SoundLists.Add(new AssetListLoadReference(bundleKey, path));
            }

            return true;
        }

        if (receiverPath == "CrossSections" && methodName == "Load" && args.Count == 2
            && Evaluator.TryEvalString(args[0].Expression, out string csKey)
            && Evaluator.TryEvalString(args[1].Expression, out string csPath))
        {
            TargetProject.Init.CrossSections.Add(new ResourceLoadReference(csKey, ResolveScriptAssetPath(csPath, filePath)));
            return true;
        }

        if (receiverPath == "Network.LaneLayouts" && methodName == "Load" && args.Count == 2
            && Evaluator.TryEvalString(args[0].Expression, out string llKey)
            && Evaluator.TryEvalString(args[1].Expression, out string llPath))
        {
            TargetProject.Init.LaneLayouts.Add(new ResourceLoadReference(llKey, ResolveScriptAssetPath(llPath, filePath)));
            return true;
        }

        if (receiverPath == "Network.LaneTraffic" && methodName == "AddType" && args.Count == 3
            && Evaluator.TryEvalString(args[0].Expression, out string tKey)
            && Evaluator.TryEvalString(args[1].Expression, out string tName)
            && Evaluator.TryEvalString(args[2].Expression, out string tColor))
        {
            TargetProject.Init.LaneTraffic.Types.Add(new LaneTrafficTypeDefinition(tKey, tName, tColor));
            return true;
        }

        if (receiverPath == "Network.LaneTraffic" && methodName == "AddGroup" && args.Count == 3
            && Evaluator.TryEvalString(args[0].Expression, out string gKey)
            && Evaluator.TryEvalString(args[1].Expression, out string gTypes)
            && Evaluator.TryEvalString(args[2].Expression, out string gColor))
        {
            List<string> typeKeys = gTypes
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            TargetProject.Init.LaneTraffic.Groups.Add(new LaneTrafficGroupDefinition(gKey, typeKeys, gColor));
            return true;
        }

        return false;
    }

    private bool TryHandleGlobalWorldCall(ExpressionSyntax rootReceiver, InvocationExpressionSyntax call, string filePath)
    {
        if (rootReceiver is not IdentifierNameSyntax rootGlobal)
        {
            return false;
        }

        string receiverName = rootGlobal.Identifier.ValueText;
        string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

        switch (receiverName)
        {
            case "Camera":
                if (method == "Locate"
                    && args.Count == 8
                    && Evaluator.TryEvalInt(args[0].Expression, out int camCx)
                    && Evaluator.TryEvalInt(args[1].Expression, out int camCz)
                    && TryReadPose6(args, 2, out PoseDegreesModel camPose))
                {
                    TargetProject.Placement.Camera.Pose = new WorldPoseDegreesModel(new ChunkIndexModel(camCx, camCz), camPose);
                    return true;
                }
                return false;

            case "Avatar":
                if (method == "Load" && args.Count == 1 && Evaluator.TryEvalString(args[0].Expression, out string avatarPath))
                {
                    TargetProject.Placement.Avatar ??= new AvatarSettingsModel();
                    TargetProject.Placement.Avatar.XmlPath = ResolveScriptAssetPath(avatarPath, filePath);
                    return true;
                }

                if (method == "Locate"
                    && (args.Count == 5 || args.Count == 8)
                    && Evaluator.TryEvalInt(args[0].Expression, out int avCx)
                    && Evaluator.TryEvalInt(args[1].Expression, out int avCz)
                    && TryReadPose3Or6(args, 2, out PoseDegreesModel avPose))
                {
                    TargetProject.Placement.Avatar ??= new AvatarSettingsModel();
                    TargetProject.Placement.Avatar.InitialPose = new WorldPoseDegreesModel(new ChunkIndexModel(avCx, avCz), avPose);
                    return true;
                }
                return false;

            case "Environment":
                if (method == "SetDefault" && args.Count == 1 && Evaluator.TryEvalString(args[0].Expression, out string envPath))
                {
                    TargetProject.Placement.EnvironmentPath = ResolveScriptAssetPath(envPath, filePath);
                    return true;
                }
                return false;

            case "DirectionalLight":
                if (method == "SetColor" && args.Count == 1 && Evaluator.TryEvalString(args[0].Expression, out string hex))
                {
                    TargetProject.Placement.DirectionalLight.ColorHex = hex;
                    return true;
                }

                if (method == "SetDirection"
                    && args.Count == 3
                    && Evaluator.TryEvalDouble(args[0].Expression, out double dx)
                    && Evaluator.TryEvalDouble(args[1].Expression, out double dy)
                    && Evaluator.TryEvalDouble(args[2].Expression, out double dz))
                {
                    TargetProject.Placement.DirectionalLight.DirectionX = dx;
                    TargetProject.Placement.DirectionalLight.DirectionY = dy;
                    TargetProject.Placement.DirectionalLight.DirectionZ = dz;
                    return true;
                }

                if (method == "SetIntensity" && args.Count == 1 && Evaluator.TryEvalDouble(args[0].Expression, out double intensity))
                {
                    TargetProject.Placement.DirectionalLight.Intensity = intensity;
                    return true;
                }
                return false;

            case "Background":
                if (method == "Add" && args.Count == 1 && Evaluator.TryEvalString(args[0].Expression, out string bgKey))
                {
                    TargetProject.Placement.BackgroundModels.Add(bgKey);
                    return true;
                }
                return false;

            case "Network":
                if (method == "Connect"
                    && args.Count == 2
                    && TryParsePortReference(args[0].Expression, out PortReferenceModel? portA)
                    && TryParsePortReference(args[1].Expression, out PortReferenceModel? portB))
                {
                    TargetProject.Placement.Connections.Add(new ExplicitPortConnection(portA!, portB!));
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    private void ApplySplineTemplateCalls(
        IReadOnlyList<InvocationExpressionSyntax> calls,
        int startIndex,
        SplineTemplateBinding binding,
        string filePath)
    {
        List<InvocationExpressionSyntax> pendingUnrecognized = new();

        void FlushUnrecognized()
        {
            if (pendingUnrecognized.Count == 0)
            {
                return;
            }

            AppendRewrittenChain(binding.UserCodeBuilder, "tSpline", pendingUnrecognized, CreateWorldAndTemplateIdentifierMap(null));
            pendingUnrecognized.Clear();
        }

        for (int i = startIndex; i < calls.Count; i++)
        {
            InvocationExpressionSyntax call = calls[i];
            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "PutExtrusion" && TryParseSplineTemplateExtrusion(args, out SplineTemplateExtrusionRule? extRule))
            {
                FlushUnrecognized();
                binding.Document.Extrusions.Add(extRule!);
                continue;
            }

            if (method == "PutProp" && TryParseSplineTemplateProp(args, out SplineTemplatePropRule? propRule))
            {
                FlushUnrecognized();
                binding.Document.Props.Add(propRule!);
                continue;
            }

            pendingUnrecognized.Add(call);
        }

        FlushUnrecognized();
    }

    private void ApplyJunctionTemplateFluentTail(
        IReadOnlyList<InvocationExpressionSyntax> calls,
        int startIndex,
        JunctionTemplateBinding binding)
    {
        List<InvocationExpressionSyntax> pendingUnrecognized = new();

        void FlushUnrecognized()
        {
            if (pendingUnrecognized.Count == 0)
            {
                return;
            }

            AppendRewrittenChain(binding.UserCodeBuilder, "tJunction", pendingUnrecognized, CreateWorldAndTemplateIdentifierMap(binding));
            pendingUnrecognized.Clear();
        }

        for (int i = startIndex; i < calls.Count; i++)
        {
            InvocationExpressionSyntax call = calls[i];
            if (TryApplyJunctionTemplateSingleCall(call, binding))
            {
                FlushUnrecognized();
                continue;
            }

            pendingUnrecognized.Add(call);
        }

        FlushUnrecognized();
    }

    private bool TryApplyJunctionTemplateSingleCall(InvocationExpressionSyntax call, JunctionTemplateBinding binding)
    {
        string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

        if (method == "AddPort" && args.Count == 8
            && Evaluator.TryEvalString(args[0].Expression, out string portKey)
            && Evaluator.TryEvalString(args[1].Expression, out string portLayout)
            && TryReadPose6(args, 2, out PoseDegreesModel portPose))
        {
            binding.Document.Ports.Add(new JunctionPortDefinition(portKey, portLayout, portPose));
            return true;
        }

        if (method == "AddRelay" && args.Count == 9
            && Evaluator.TryEvalString(args[0].Expression, out string inKey)
            && Evaluator.TryEvalString(args[1].Expression, out string outKey)
            && Evaluator.TryEvalString(args[2].Expression, out string relayLayout)
            && TryReadPose6(args, 3, out PoseDegreesModel relayPose))
        {
            binding.Document.Relays.Add(new JunctionRelayDefinition(inKey, outKey, relayLayout, relayPose));
            return true;
        }

        if (method == "PutProp" && (args.Count == 4 || args.Count == 7)
            && Evaluator.TryEvalString(args[0].Expression, out string modelKey)
            && TryReadPose3Or6(args, 1, out PoseDegreesModel pose))
        {
            binding.Document.Props.Add(new JunctionStaticPropPlacement(modelKey, pose));
            return true;
        }

        return false;
    }

    private void ApplyJunctionPathCallChain(
        IReadOnlyList<InvocationExpressionSyntax> calls,
        int startIndex,
        JunctionPathBinding binding)
    {
        List<InvocationExpressionSyntax> pendingUnrecognized = new();

        void FlushUnrecognized()
        {
            if (pendingUnrecognized.Count == 0)
            {
                return;
            }

            AppendRewrittenChain(
                binding.ParentTemplate.UserCodeBuilder,
                binding.CanonicalVarName,
                pendingUnrecognized,
                CreateWorldAndTemplateIdentifierMap(binding.ParentTemplate));
            pendingUnrecognized.Clear();
        }

        for (int i = startIndex; i < calls.Count; i++)
        {
            InvocationExpressionSyntax call = calls[i];
            if (TryParseJunctionCurveSegment(call, allowToEnd: true, out JunctionCurveSegment? seg, binding.ParentTemplate))
            {
                FlushUnrecognized();
                binding.Path.Segments.Add(seg!);
                continue;
            }

            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "TrafficDensity" && args.Count == 1
                && Evaluator.TryEvalDouble(args[0].Expression, out double factor))
            {
                FlushUnrecognized();
                binding.Path.TrafficDensityFactor = factor;
                continue;
            }

            if (method == "PutProp" && TryParseCurveProp(args, binding.ParentTemplate.AnchorAliases, out CurvePropPlacementRule? propRule))
            {
                FlushUnrecognized();
                binding.Path.Props.Add(propRule!);
                continue;
            }

            if (method == "PutExtrusion" && TryParseCurveExtrusion(args, binding.ParentTemplate.AnchorAliases, out CurveExtrusionPlacementRule? extRule))
            {
                FlushUnrecognized();
                binding.Path.Extrusions.Add(extRule!);
                continue;
            }

            pendingUnrecognized.Add(call);
        }

        FlushUnrecognized();
    }

    private void ApplyJunctionWidthCallChain(IReadOnlyList<InvocationExpressionSyntax> calls, JunctionPathBinding binding)
    {
        List<InvocationExpressionSyntax> fallbackTail = new();
        bool fellBackToUserCode = false;

        for (int i = 0; i < calls.Count; i++)
        {
            InvocationExpressionSyntax call = calls[i];
            if (fellBackToUserCode)
            {
                fallbackTail.Add(call);
                continue;
            }

            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "Constant" && (args.Count == 1 || args.Count == 2))
            {
                if (Evaluator.TryEvalAnchoredDistance(args[0].Expression, binding.ParentTemplate.AnchorAliases, out AnchoredDistance length, out bool isMax)
                    && !isMax)
                {
                    string? anchor = null;
                    bool anchorOk = args.Count == 1 || TryReadOutAnchor(args[1], out anchor, binding.ParentTemplate);
                    if (anchorOk)
                    {
                        binding.Path.WidthSegments.Add(new JunctionWidthSegment(JunctionWidthSegmentKind.Constant, 0.0, 0.0, length, anchor));
                        continue;
                    }
                }
            }
            else if (method == "TransitionTo" && (args.Count == 3 || args.Count == 4))
            {
                if (Evaluator.TryEvalDouble(args[0].Expression, out double left)
                    && Evaluator.TryEvalDouble(args[1].Expression, out double right)
                    && Evaluator.TryEvalAnchoredDistance(args[2].Expression, binding.ParentTemplate.AnchorAliases, out AnchoredDistance length, out bool isMax)
                    && !isMax)
                {
                    string? anchor = null;
                    bool anchorOk = args.Count == 3 || TryReadOutAnchor(args[3], out anchor, binding.ParentTemplate);
                    if (anchorOk)
                    {
                        binding.Path.WidthSegments.Add(new JunctionWidthSegment(JunctionWidthSegmentKind.TransitionTo, left, right, length, anchor));
                        continue;
                    }
                }
            }

            fellBackToUserCode = true;
            fallbackTail.Add(call);
        }

        if (0 < fallbackTail.Count)
        {
            AppendRewrittenChain(
                binding.ParentTemplate.UserCodeBuilder,
                $"{binding.CanonicalVarName}.Width",
                fallbackTail,
                CreateWorldAndTemplateIdentifierMap(binding.ParentTemplate));
        }
    }

    private void ApplyJunctionCurveCallChain(
        IReadOnlyList<InvocationExpressionSyntax> calls,
        int startIndex,
        JunctionCurveBinding binding)
    {
        List<InvocationExpressionSyntax> pendingUnrecognized = new();

        void FlushUnrecognized()
        {
            if (pendingUnrecognized.Count == 0)
            {
                return;
            }

            AppendRewrittenChain(
                binding.ParentTemplate.UserCodeBuilder,
                binding.CanonicalVarName,
                pendingUnrecognized,
                CreateWorldAndTemplateIdentifierMap(binding.ParentTemplate));
            pendingUnrecognized.Clear();
        }

        for (int i = startIndex; i < calls.Count; i++)
        {
            InvocationExpressionSyntax call = calls[i];
            if (TryParseJunctionCurveSegment(call, allowToEnd: false, out JunctionCurveSegment? seg, binding.ParentTemplate))
            {
                FlushUnrecognized();
                binding.Curve.Segments.Add(seg!);
                continue;
            }

            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "PutProp" && TryParseCurveProp(args, binding.ParentTemplate.AnchorAliases, out CurvePropPlacementRule? propRule))
            {
                FlushUnrecognized();
                binding.Curve.Props.Add(propRule!);
                continue;
            }

            if (method == "PutExtrusion" && TryParseCurveExtrusion(args, binding.ParentTemplate.AnchorAliases, out CurveExtrusionPlacementRule? extRule))
            {
                FlushUnrecognized();
                binding.Curve.Extrusions.Add(extRule!);
                continue;
            }

            pendingUnrecognized.Add(call);
        }

        FlushUnrecognized();
    }

    private bool TryParsePlacementFactoryStart(
        ExpressionSyntax rootReceiver,
        IReadOnlyList<InvocationExpressionSyntax> calls,
        out BoundValue? startedBinding)
    {
        startedBinding = null;
        if (calls.Count == 0)
        {
            return false;
        }

        InvocationExpressionSyntax firstCall = calls[0];
        string? method = RoslynCsxHelpers.GetInvokedMethodName(firstCall);
        SeparatedSyntaxList<ArgumentSyntax> args = firstCall.ArgumentList.Arguments;

        if (method == "BeginSpline" && TryParseChunksIndexer(rootReceiver, out ChunkIndexModel splineChunk))
        {
            if (!TryParseBeginSplineArgs(args, out string? templateKey, out PoseDegreesModel pose))
            {
                return false;
            }

            SplinePlacementNode node = new()
            {
                Origin = SplineOriginModel.FromChunk(splineChunk, pose),
                TemplateKey = templateKey,
            };
            PendingSplineFactoryBinding binding = new(node);
            if (1 < calls.Count)
            {
                List<InvocationExpressionSyntax> tail = calls.Skip(1).ToList();
                ApplyPendingSplineFactoryCalls(tail, binding);
            }

            startedBinding = binding;
            return true;
        }

        if (method == "IntoSpline" && TryResolveBuiltNodeReceiver(rootReceiver, out bool srcIsJunction, out string srcNodeId))
        {
            SplinePlacementNode? node = null;
            if (!srcIsJunction
                && args.Count == 1
                && Evaluator.TryEvalNullableString(args[0].Expression, out string? splineTpl1))
            {
                node = new SplinePlacementNode
                {
                    Origin = SplineOriginModel.FromSpline(srcNodeId),
                    TemplateKey = splineTpl1,
                };
            }
            else if (srcIsJunction
                && args.Count == 2
                && Evaluator.TryEvalString(args[0].Expression, out string portKey)
                && Evaluator.TryEvalNullableString(args[1].Expression, out string? splineTpl2))
            {
                node = new SplinePlacementNode
                {
                    Origin = SplineOriginModel.FromJunction(srcNodeId, portKey),
                    TemplateKey = splineTpl2,
                };
            }

            if (node is null)
            {
                return false;
            }

            PendingSplineFactoryBinding binding = new(node);
            if (1 < calls.Count)
            {
                List<InvocationExpressionSyntax> tail = calls.Skip(1).ToList();
                ApplyPendingSplineFactoryCalls(tail, binding);
            }

            startedBinding = binding;
            return true;
        }

        if (method == "PutJunction" && TryParseChunksIndexer(rootReceiver, out ChunkIndexModel junChunk))
        {
            if ((args.Count != 4 && args.Count != 7)
                || !Evaluator.TryEvalString(args[0].Expression, out string tplKey)
                || !TryReadPose3Or6(args, 1, out PoseDegreesModel pose))
            {
                return false;
            }

            JunctionPlacementNode node = new()
            {
                TemplateKey = tplKey,
                Origin = JunctionOriginModel.FromChunk(junChunk, pose),
            };
            PendingJunctionFactoryBinding binding = new(node);
            if (1 < calls.Count)
            {
                List<InvocationExpressionSyntax> tail = calls.Skip(1).ToList();
                ApplyPendingJunctionFactoryCalls(tail, binding);
            }

            startedBinding = binding;
            return true;
        }

        if (method == "IntoJunction" && TryResolveBuiltNodeReceiver(rootReceiver, out bool jSrcIsJunction, out string jSrcNodeId))
        {
            JunctionPlacementNode? node = null;
            if (!jSrcIsJunction
                && args.Count == 2
                && Evaluator.TryEvalString(args[0].Expression, out string tplKey1)
                && Evaluator.TryEvalString(args[1].Expression, out string targetPort1))
            {
                node = new JunctionPlacementNode
                {
                    TemplateKey = tplKey1,
                    Origin = JunctionOriginModel.FromSpline(jSrcNodeId, targetPort1),
                };
            }
            else if (jSrcIsJunction
                && args.Count == 3
                && Evaluator.TryEvalString(args[0].Expression, out string srcPort)
                && Evaluator.TryEvalString(args[1].Expression, out string tplKey2)
                && Evaluator.TryEvalString(args[2].Expression, out string targetPort2))
            {
                node = new JunctionPlacementNode
                {
                    TemplateKey = tplKey2,
                    Origin = JunctionOriginModel.FromJunction(jSrcNodeId, srcPort, targetPort2),
                };
            }

            if (node is null)
            {
                return false;
            }

            PendingJunctionFactoryBinding binding = new(node);
            if (1 < calls.Count)
            {
                List<InvocationExpressionSyntax> tail = calls.Skip(1).ToList();
                ApplyPendingJunctionFactoryCalls(tail, binding);
            }

            startedBinding = binding;
            return true;
        }

        return false;
    }

    private void ApplyPendingSplineFactoryCalls(IReadOnlyList<InvocationExpressionSyntax> calls, PendingSplineFactoryBinding binding)
    {
        List<InvocationExpressionSyntax> pendingPreBuild = new();

        void FlushPreBuild()
        {
            if (pendingPreBuild.Count == 0)
            {
                return;
            }

            binding.PendingPreBuildRawLines.Add(FormatChainBody(pendingPreBuild, CreateWorldAndTemplateIdentifierMap(null)));
            pendingPreBuild.Clear();
        }

        foreach (InvocationExpressionSyntax call in calls)
        {
            if (TryApplySplineFactorySingleCall(call, binding.Node))
            {
                FlushPreBuild();
                continue;
            }

            pendingPreBuild.Add(call);
        }

        FlushPreBuild();
    }

    private void ApplyPendingJunctionFactoryCalls(IReadOnlyList<InvocationExpressionSyntax> calls, PendingJunctionFactoryBinding binding)
    {
        List<InvocationExpressionSyntax> pendingPreBuild = new();

        void FlushPreBuild()
        {
            if (pendingPreBuild.Count == 0)
            {
                return;
            }

            binding.PendingPreBuildRawLines.Add(FormatChainBody(pendingPreBuild, CreateWorldAndTemplateIdentifierMap(null)));
            pendingPreBuild.Clear();
        }

        foreach (InvocationExpressionSyntax call in calls)
        {
            if (RoslynCsxHelpers.GetInvokedMethodName(call) == "PutProp"
                && (call.ArgumentList.Arguments.Count == 4 || call.ArgumentList.Arguments.Count == 7)
                && Evaluator.TryEvalString(call.ArgumentList.Arguments[0].Expression, out string modelKey)
                && TryReadPose3Or6(call.ArgumentList.Arguments, 1, out PoseDegreesModel pose))
            {
                FlushPreBuild();
                binding.Node.Props.Add(new JunctionStaticPropPlacement(modelKey, pose));
                continue;
            }

            pendingPreBuild.Add(call);
        }

        FlushPreBuild();
    }

    private void ApplyJunctionPathOverrideCalls(string pathKey, IReadOnlyList<InvocationExpressionSyntax> calls, PendingJunctionFactoryBinding binding)
    {
        JunctionPathOverrideModel? ov = binding.Node.PathOverrides.FirstOrDefault(p => string.Equals(p.PathKey, pathKey, StringComparison.Ordinal));
        if (ov is null)
        {
            ov = new JunctionPathOverrideModel { PathKey = pathKey };
            binding.Node.PathOverrides.Add(ov);
        }

        List<InvocationExpressionSyntax> pendingUnrecognized = new();

        void FlushUnrecognized()
        {
            if (pendingUnrecognized.Count == 0)
            {
                return;
            }

            string targetExpr = $".Paths[{RoslynCsxHelpers.FormatStringLiteral(pathKey)}]";
            binding.PendingPreBuildRawLines.Add(targetExpr + FormatChainBody(pendingUnrecognized, CreateWorldAndTemplateIdentifierMap(null)));
            pendingUnrecognized.Clear();
        }

        foreach (InvocationExpressionSyntax call in calls)
        {
            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "TrafficDensity" && args.Count == 1 && Evaluator.TryEvalDouble(args[0].Expression, out double factor))
            {
                FlushUnrecognized();
                ov.TrafficDensityFactor = factor;
                continue;
            }

            if (method == "PutProp" && TryParseCurveProp(args, WorldAnchorAliases, out CurvePropPlacementRule? prop))
            {
                FlushUnrecognized();
                ov.Props.Add(prop!);
                continue;
            }

            if (method == "PutExtrusion" && TryParseCurveExtrusion(args, WorldAnchorAliases, out CurveExtrusionPlacementRule? ext))
            {
                FlushUnrecognized();
                ov.Extrusions.Add(ext!);
                continue;
            }

            pendingUnrecognized.Add(call);
        }

        FlushUnrecognized();
    }

    private void ApplyJunctionCurveOverrideCalls(string curveKey, IReadOnlyList<InvocationExpressionSyntax> calls, PendingJunctionFactoryBinding binding)
    {
        JunctionCurveOverrideModel? ov = binding.Node.CurveOverrides.FirstOrDefault(c => string.Equals(c.CurveKey, curveKey, StringComparison.Ordinal));
        if (ov is null)
        {
            ov = new JunctionCurveOverrideModel { CurveKey = curveKey };
            binding.Node.CurveOverrides.Add(ov);
        }

        List<InvocationExpressionSyntax> pendingUnrecognized = new();

        void FlushUnrecognized()
        {
            if (pendingUnrecognized.Count == 0)
            {
                return;
            }

            string targetExpr = $".Curves[{RoslynCsxHelpers.FormatStringLiteral(curveKey)}]";
            binding.PendingPreBuildRawLines.Add(targetExpr + FormatChainBody(pendingUnrecognized, CreateWorldAndTemplateIdentifierMap(null)));
            pendingUnrecognized.Clear();
        }

        foreach (InvocationExpressionSyntax call in calls)
        {
            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "PutProp" && TryParseCurveProp(args, WorldAnchorAliases, out CurvePropPlacementRule? prop))
            {
                FlushUnrecognized();
                ov.Props.Add(prop!);
                continue;
            }

            if (method == "PutExtrusion" && TryParseCurveExtrusion(args, WorldAnchorAliases, out CurveExtrusionPlacementRule? ext))
            {
                FlushUnrecognized();
                ov.Extrusions.Add(ext!);
                continue;
            }

            pendingUnrecognized.Add(call);
        }

        FlushUnrecognized();
    }

    private string FinalizeBuiltSpline(PendingSplineFactoryBinding pending, string factoryVarName, string? buildStatementDisplayName = null)
    {
        SplineCounter++;
        string nodeId = AllocateFinalNodeId(pending.ExplicitKey, $"spline_{SplineCounter:D3}");
        SplinePlacementNode node = pending.Node;
        node.Id = nodeId;
        node.Key = nodeId;
        if (string.IsNullOrWhiteSpace(node.DisplayName) && !string.IsNullOrWhiteSpace(buildStatementDisplayName))
        {
            node.DisplayName = buildStatementDisplayName;
        }

        const string canonicalFactoryVar = "fSpline";
        StringBuilder preBuildSb = new();
        foreach (string rawTail in pending.PendingPreBuildRawLines)
        {
            preBuildSb.Append(canonicalFactoryVar).Append(rawTail).Append(";\n");
            PreservedUserCodeCount++;
        }

        string preBuild = preBuildSb.ToString().TrimEnd('\r', '\n');
        node.PreBuildUserCode = string.IsNullOrWhiteSpace(preBuild) ? null : preBuild;

        BuiltSplineIdByKey[nodeId] = nodeId;
        if (!string.IsNullOrWhiteSpace(pending.ExplicitKey))
        {
            BuiltSplineIdByKey[pending.ExplicitKey.Trim()] = nodeId;
        }

        TargetProject.Placement.Splines.Add(node);
        return nodeId;
    }

    private string FinalizeBuiltJunction(PendingJunctionFactoryBinding pending, string factoryVarName, string? buildStatementDisplayName = null)
    {
        JunctionCounter++;
        string nodeId = AllocateFinalNodeId(pending.ExplicitKey, $"junction_{JunctionCounter:D3}");
        JunctionPlacementNode node = pending.Node;
        node.Id = nodeId;
        node.Key = nodeId;
        if (string.IsNullOrWhiteSpace(node.DisplayName) && !string.IsNullOrWhiteSpace(buildStatementDisplayName))
        {
            node.DisplayName = buildStatementDisplayName;
        }

        const string canonicalFactoryVar = "fJunction";
        StringBuilder preBuildSb = new();
        foreach (string rawTail in pending.PendingPreBuildRawLines)
        {
            preBuildSb.Append(canonicalFactoryVar).Append(rawTail).Append(";\n");
            PreservedUserCodeCount++;
        }

        string preBuild = preBuildSb.ToString().TrimEnd('\r', '\n');
        node.PreBuildUserCode = string.IsNullOrWhiteSpace(preBuild) ? null : preBuild;

        BuiltJunctionIdByKey[nodeId] = nodeId;
        if (!string.IsNullOrWhiteSpace(pending.ExplicitKey))
        {
            BuiltJunctionIdByKey[pending.ExplicitKey.Trim()] = nodeId;
        }

        TargetProject.Placement.Junctions.Add(node);
        return nodeId;
    }

    private string AllocateFinalNodeId(string? explicitKey, string defaultPrefix)
    {
        string baseId;
        string? trimmed = explicitKey?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && CSharpIdentifierUtilities.TryValidateNodeKey(trimmed, out _))
        {
            baseId = trimmed;
        }
        else if (!string.IsNullOrEmpty(trimmed))
        {
            StringBuilder cleaned = new(trimmed.Length);
            foreach (Rune rune in trimmed.EnumerateRunes())
            {
                if (!Rune.IsControl(rune))
                {
                    cleaned.Append(rune.ToString());
                }
            }

            string candidateClean = cleaned.ToString().Trim();
            baseId = string.IsNullOrEmpty(candidateClean) ? defaultPrefix : candidateClean;
        }
        else
        {
            baseId = defaultPrefix;
        }

        string candidate = baseId;
        int suffix = 2;
        while (!UsedNodeIds.Add(candidate))
        {
            candidate = $"{baseId}_{suffix}";
            suffix++;
        }

        return candidate;
    }

    private void PreserveUnrecognizedStatement(
        StatementSyntax statement,
        string filePath,
        bool isInitContext,
        bool isCustomFallbackProbe,
        List<string>? customFallbackLines)
    {
        JunctionTemplateBinding? activeJun = GetActiveJunctionTemplate();
        SplineTemplateBinding? activeSpline = GetActiveSplineTemplate();

        IReadOnlyDictionary<string, string> idMap = CreateWorldAndTemplateIdentifierMap(activeJun);
        StatementSyntax rewritten = ImportExpressionEvaluator.RewriteSyntaxForUserCode(
            statement,
            idMap,
            Evaluator.SnapshotDoubles());

        string text = rewritten.ToString().TrimEnd();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (activeJun is not null)
        {
            AppendIndentedBlock(activeJun.UserCodeBuilder, text);
            PreservedUserCodeCount++;
        }
        else if (activeSpline is not null)
        {
            AppendIndentedBlock(activeSpline.UserCodeBuilder, text);
            PreservedUserCodeCount++;
        }
        else if (isInitContext)
        {
            AppendIndentedBlock(InitUserCodeBuilder, text);
            PreservedUserCodeCount++;
        }
        else if (isCustomFallbackProbe && customFallbackLines is not null)
        {
            customFallbackLines.Add(text);
        }
        else
        {
            AppendIndentedBlock(WorldUserCodeBuilder, text);
            PreservedUserCodeCount++;
        }
    }

    private IReadOnlyDictionary<string, string> CreateWorldAndTemplateIdentifierMap(JunctionTemplateBinding? activeJunction)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (LexicalVarScope scope in VarScopes)
        {
            foreach (KeyValuePair<string, BoundValue> kv in scope.Bindings)
            {
                switch (kv.Value)
                {
                    case SplineTemplateBinding:
                        map[kv.Key] = "tSpline";
                        break;
                    case JunctionTemplateBinding:
                        map[kv.Key] = "tJunction";
                        break;
                    case JunctionPathBinding jp:
                        map[kv.Key] = jp.CanonicalVarName;
                        break;
                    case JunctionCurveBinding jc:
                        map[kv.Key] = jc.CanonicalVarName;
                        break;
                    case PendingSplineFactoryBinding:
                        map[kv.Key] = "fSpline";
                        break;
                    case PendingJunctionFactoryBinding:
                        map[kv.Key] = "fJunction";
                        break;
                    case BuiltNodeBinding bn:
                        map[kv.Key] = bn.IsJunction
                            ? $"Network.Junctions[{RoslynCsxHelpers.FormatStringLiteral(bn.NodeId)}]"
                            : $"Network.Splines[{RoslynCsxHelpers.FormatStringLiteral(bn.NodeId)}]";
                        break;
                }
            }
        }

        foreach (KeyValuePair<string, string> anchorKv in WorldAnchorAliases)
        {
            map[anchorKv.Key] = DistanceExprCodec.FormatAnchorVariableName(anchorKv.Value);
        }

        if (activeJunction is not null)
        {
            foreach (KeyValuePair<string, string> jAnchorKv in activeJunction.AnchorAliases)
            {
                map[jAnchorKv.Key] = DistanceExprCodec.FormatAnchorVariableName(jAnchorKv.Value);
            }
        }

        return map;
    }

    private void AppendRewrittenChain(
        StringBuilder sb,
        string targetExpr,
        IReadOnlyList<InvocationExpressionSyntax> calls,
        IReadOnlyDictionary<string, string> identifierMap)
    {
        if (calls.Count == 0)
        {
            return;
        }

        string body = FormatChainBody(calls, identifierMap);
        sb.Append(targetExpr).Append(body).Append(";\n");
        PreservedUserCodeCount++;
    }

    private string FormatChainBody(
        IReadOnlyList<InvocationExpressionSyntax> calls,
        IReadOnlyDictionary<string, string> identifierMap)
    {
        IReadOnlyDictionary<string, double> scalarSnapshot = Evaluator.SnapshotDoubles();
        StringBuilder sb = new();
        foreach (InvocationExpressionSyntax call in calls)
        {
            string? methodName = RoslynCsxHelpers.GetInvokedMethodName(call);
            ArgumentListSyntax rewrittenArgs = ImportExpressionEvaluator.RewriteSyntaxForUserCode(
                call.ArgumentList,
                identifierMap,
                scalarSnapshot);
            sb.Append('.').Append(methodName ?? call.Expression.ToString()).Append(rewrittenArgs.ToString());
        }

        return sb.ToString();
    }

    private static void AppendIndentedLine(StringBuilder sb, string line)
    {
        AppendIndentedBlock(sb, line);
    }

    private static void AppendIndentedBlock(StringBuilder sb, string block)
    {
        string[] lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.Append('\n');
            }
            else
            {
                sb.Append(line).Append('\n');
            }
        }
    }

    private bool TryApplySplineFactorySingleCall(InvocationExpressionSyntax call, SplinePlacementNode node)
    {
        string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

        if (method == "ConnectBezier")
        {
            if (args.Count < 1 || 3 < args.Count || !TryParsePortReference(args[0].Expression, out PortReferenceModel? targetPort))
            {
                return false;
            }

            string? anchorId = null;
            double handleScale = 0.5;
            if (args.Count == 2)
            {
                if (TryReadOutWorldAnchor(args[1], out anchorId))
                {
                }
                else if (!Evaluator.TryEvalDouble(args[1].Expression, out handleScale))
                {
                    return false;
                }
            }
            else if (args.Count == 3)
            {
                if (!TryReadOutWorldAnchor(args[1], out anchorId) || !Evaluator.TryEvalDouble(args[2].Expression, out handleScale))
                {
                    return false;
                }
            }

            node.ConnectBezier = new SplineConnectBezierModel(targetPort!, handleScale, anchorId);
            return true;
        }

        if (method == "PutProp")
        {
            string? key = null;
            int bodyStartIdx = 0;
            if (2 <= args.Count
                && Evaluator.TryEvalNullableString(args[0].Expression, out string? maybeKey)
                && Evaluator.TryEvalStringList(args[1].Expression, out _))
            {
                key = maybeKey;
                bodyStartIdx = 1;
            }

            if (!TryParsePropBody(
                args,
                bodyStartIdx,
                WorldAnchorAliases,
                out List<string> models,
                out PoseDegreesModel pose,
                out AnchoredDistance from,
                out double span,
                out double interval,
                out int count))
            {
                return false;
            }

            SplineInstancePropRule rule = new()
            {
                Key = key,
                Pose = pose,
                From = from,
                Span = span,
                Interval = interval,
                Count = count,
            };
            rule.ModelKeys.AddRange(models);
            node.Props.Add(rule);
            return true;
        }

        if (method == "SkipProp" && TryParseSkipRule(args, out SplineSkipRule? propSkip))
        {
            node.PropSkips.Add(propSkip!);
            return true;
        }

        if (method == "PutExtrusion" && TryParseCurveExtrusion(args, WorldAnchorAliases, out CurveExtrusionPlacementRule? extRule))
        {
            node.Extrusions.Add(extRule!);
            return true;
        }

        if (method == "SkipExtrusion" && TryParseSkipRule(args, out SplineSkipRule? extSkip))
        {
            node.ExtrusionSkips.Add(extSkip!);
            return true;
        }

        return false;
    }

    private bool TryApplySplineCurveCalls(IReadOnlyList<InvocationExpressionSyntax> calls, SplinePlacementNode node)
    {
        foreach (InvocationExpressionSyntax call in calls)
        {
            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "Straight" && (args.Count == 1 || args.Count == 2)
                && Evaluator.TryEvalDouble(args[0].Expression, out double len))
            {
                string? anchor = null;
                if (args.Count == 2 && !TryReadOutWorldAnchor(args[1], out anchor))
                {
                    return false;
                }

                node.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, len, anchor));
                continue;
            }

            if ((method == "ByRadius" || method == "ByCurvature") && (args.Count == 2 || args.Count == 3)
                && Evaluator.TryEvalDouble(args[0].Expression, out double val)
                && Evaluator.TryEvalDouble(args[1].Expression, out double arcLen))
            {
                string? anchor = null;
                if (args.Count == 3 && !TryReadOutWorldAnchor(args[2], out anchor))
                {
                    return false;
                }

                SplineCurveSegmentKind kind = method == "ByRadius"
                    ? SplineCurveSegmentKind.ByRadius
                    : SplineCurveSegmentKind.ByCurvature;
                node.Curves.Add(new SplineCurveSegment(kind, val, arcLen, anchor));
                continue;
            }

            return false;
        }

        return true;
    }

    private bool TryApplySplineGradientCalls(IReadOnlyList<InvocationExpressionSyntax> calls, SplinePlacementNode node)
    {
        foreach (InvocationExpressionSyntax call in calls)
        {
            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "Constant" && (args.Count == 1 || args.Count == 2)
                && Evaluator.TryEvalDouble(args[0].Expression, out double cLen))
            {
                string? anchor = null;
                if (args.Count == 2 && !TryReadOutWorldAnchor(args[1], out anchor))
                {
                    return false;
                }

                node.Gradients.Add(new SplineGradientSegment(SplineGradientSegmentKind.Constant, 0.0, cLen, anchor));
                continue;
            }

            SplineGradientSegmentKind? kind = method switch
            {
                "TransitionByRadian" => SplineGradientSegmentKind.TransitionByRadian,
                "TransitionByDegree" => SplineGradientSegmentKind.TransitionByDegree,
                "TransitionByPercent" => SplineGradientSegmentKind.TransitionByPercent,
                "TransitionByPermil" => SplineGradientSegmentKind.TransitionByPermil,
                _ => null,
            };

            if (kind.HasValue && (args.Count == 2 || args.Count == 3)
                && Evaluator.TryEvalDouble(args[0].Expression, out double delta)
                && Evaluator.TryEvalDouble(args[1].Expression, out double tLen))
            {
                string? anchor = null;
                if (args.Count == 3 && !TryReadOutWorldAnchor(args[2], out anchor))
                {
                    return false;
                }

                node.Gradients.Add(new SplineGradientSegment(kind.Value, delta, tLen, anchor));
                continue;
            }

            return false;
        }

        return true;
    }

    private bool TryApplySplineCantCalls(IReadOnlyList<InvocationExpressionSyntax> calls, SplinePlacementNode node)
    {
        foreach (InvocationExpressionSyntax call in calls)
        {
            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "Constant" && (args.Count == 1 || args.Count == 2)
                && Evaluator.TryEvalDouble(args[0].Expression, out double cLen))
            {
                string? anchor = null;
                if (args.Count == 2 && !TryReadOutWorldAnchor(args[1], out anchor))
                {
                    return false;
                }

                node.Cants.Add(new SplineCantSegment(SplineCantSegmentKind.Constant, 0.0, 0.0, cLen, anchor));
                continue;
            }

            if (method == "TransitionToHeight" && (args.Count == 3 || args.Count == 4)
                && Evaluator.TryEvalDouble(args[0].Expression, out double height)
                && Evaluator.TryEvalDouble(args[1].Expression, out double gauge)
                && Evaluator.TryEvalDouble(args[2].Expression, out double hLen))
            {
                string? anchor = null;
                if (args.Count == 4 && !TryReadOutWorldAnchor(args[3], out anchor))
                {
                    return false;
                }

                node.Cants.Add(new SplineCantSegment(SplineCantSegmentKind.TransitionToHeight, height, gauge, hLen, anchor));
                continue;
            }

            SplineCantSegmentKind? kind = method switch
            {
                "TransitionToRadian" => SplineCantSegmentKind.TransitionToRadian,
                "TransitionToDegree" => SplineCantSegmentKind.TransitionToDegree,
                "TransitionToPercent" => SplineCantSegmentKind.TransitionToPercent,
                _ => null,
            };

            if (kind.HasValue && (args.Count == 2 || args.Count == 3)
                && Evaluator.TryEvalDouble(args[0].Expression, out double target)
                && Evaluator.TryEvalDouble(args[1].Expression, out double tLen))
            {
                string? anchor = null;
                if (args.Count == 3 && !TryReadOutWorldAnchor(args[2], out anchor))
                {
                    return false;
                }

                node.Cants.Add(new SplineCantSegment(kind.Value, target, 0.0, tLen, anchor));
                continue;
            }

            return false;
        }

        return true;
    }

    private bool TryParseSkipRule(SeparatedSyntaxList<ArgumentSyntax> args, out SplineSkipRule? skip)
    {
        skip = null;
        if (args.Count == 1 && Evaluator.TryEvalString(args[0].Expression, out string keyOnly))
        {
            skip = new SplineSkipRule(keyOnly, null, null);
            return true;
        }

        if (args.Count == 3
            && Evaluator.TryEvalString(args[0].Expression, out string key)
            && Evaluator.TryEvalAnchoredDistance(args[1].Expression, WorldAnchorAliases, out AnchoredDistance from, out bool fromMax)
            && !fromMax
            && Evaluator.TryEvalAnchoredDistance(args[2].Expression, WorldAnchorAliases, out AnchoredDistance to, out _))
        {
            skip = new SplineSkipRule(key, from, to);
            return true;
        }

        return false;
    }

    private bool TryResolveBuiltNodeReceiver(ExpressionSyntax expr, out bool isJunction, out string nodeId)
    {
        isJunction = false;
        nodeId = string.Empty;

        if (expr is IdentifierNameSyntax ident
            && TryGetBinding(ident.Identifier.ValueText, out BoundValue? bound)
            && bound is BuiltNodeBinding built)
        {
            isJunction = built.IsJunction;
            nodeId = built.NodeId;
            return true;
        }

        if (expr is ElementAccessExpressionSyntax elem
            && elem.ArgumentList.Arguments.Count == 1
            && Evaluator.TryEvalString(elem.ArgumentList.Arguments[0].Expression, out string keyStr)
            && elem.Expression is MemberAccessExpressionSyntax ma
            && ma.Expression is IdentifierNameSyntax netIdent
            && netIdent.Identifier.ValueText == "Network")
        {
            string collName = ma.Name.Identifier.ValueText;
            if (collName == "Splines" && BuiltSplineIdByKey.TryGetValue(keyStr, out string? splineId))
            {
                isJunction = false;
                nodeId = splineId;
                return true;
            }

            if (collName == "Junctions" && BuiltJunctionIdByKey.TryGetValue(keyStr, out string? junctionId))
            {
                isJunction = true;
                nodeId = junctionId;
                return true;
            }
        }

        return false;
    }

    private bool TryParsePortReference(ExpressionSyntax expr, out PortReferenceModel? portRef)
    {
        portRef = null;
        if (expr is MemberAccessExpressionSyntax ma)
        {
            string portName = ma.Name.Identifier.ValueText;
            if ((portName == "Inlet" || portName == "Outlet")
                && TryResolveBuiltNodeReceiver(ma.Expression, out bool sIsJunction, out string sNodeId)
                && !sIsJunction)
            {
                PortTargetKind kind = portName == "Inlet" ? PortTargetKind.SplineInlet : PortTargetKind.SplineOutlet;
                portRef = new PortReferenceModel(sNodeId, kind);
                return true;
            }
        }

        if (expr is ElementAccessExpressionSyntax elem
            && elem.ArgumentList.Arguments.Count == 1
            && Evaluator.TryEvalString(elem.ArgumentList.Arguments[0].Expression, out string portKey)
            && elem.Expression is MemberAccessExpressionSyntax portsAccess
            && portsAccess.Name.Identifier.ValueText == "Ports"
            && portsAccess.Expression is MemberAccessExpressionSyntax junctionProp
            && junctionProp.Name.Identifier.ValueText == "Junction"
            && TryResolveBuiltNodeReceiver(junctionProp.Expression, out bool jIsJunction, out string jNodeId)
            && jIsJunction)
        {
            portRef = new PortReferenceModel(jNodeId, PortTargetKind.JunctionPort, portKey);
            return true;
        }

        return false;
    }

    private bool TryParseSplineTemplateExtrusion(SeparatedSyntaxList<ArgumentSyntax> args, out SplineTemplateExtrusionRule? rule)
    {
        rule = null;
        if (args.Count < 5)
        {
            return false;
        }

        string? key = null;
        int idx = 0;

        if (Evaluator.TryEvalNullableString(args[0].Expression, out string? maybeKey)
            && Evaluator.TryEvalString(args[1].Expression, out _))
        {
            key = maybeKey;
            idx = 1;
        }

        if (!Evaluator.TryEvalString(args[idx].Expression, out string csKey))
        {
            return false;
        }

        idx++;
        int rem = args.Count - idx;
        double offsetX = 0.0;
        double offsetY = 0.0;
        double baseInterval;
        double maxDev;
        double maxAngle;
        double vScale = 1.0;
        double vOffset = 0.0;
        double from;

        if (rem == 4)
        {
            if (!Evaluator.TryEvalDouble(args[idx++].Expression, out baseInterval)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out maxDev)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out maxAngle)
                || !Evaluator.TryEvalDouble(args[idx].Expression, out from))
            {
                return false;
            }
        }
        else if (rem == 6)
        {
            if (!Evaluator.TryEvalDouble(args[idx++].Expression, out offsetX)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out offsetY)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out baseInterval)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out maxDev)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out maxAngle)
                || !Evaluator.TryEvalDouble(args[idx].Expression, out from))
            {
                return false;
            }
        }
        else if (rem == 8)
        {
            if (!Evaluator.TryEvalDouble(args[idx++].Expression, out offsetX)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out offsetY)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out baseInterval)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out maxDev)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out maxAngle)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out vScale)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out vOffset)
                || !Evaluator.TryEvalDouble(args[idx].Expression, out from))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        rule = new SplineTemplateExtrusionRule
        {
            Key = key,
            CrossSectionKey = csKey,
            OffsetX = offsetX,
            OffsetY = offsetY,
            BaseInterval = baseInterval,
            MaxDeviation = maxDev,
            MaxAngleDeltaDeg = maxAngle,
            VIntervalScale = vScale,
            VOffset = vOffset,
            From = from,
        };
        return true;
    }

    private bool TryParseSplineTemplateProp(SeparatedSyntaxList<ArgumentSyntax> args, out SplineTemplatePropRule? rule)
    {
        rule = null;
        if (args.Count < 7)
        {
            return false;
        }

        string? key = null;
        int idx = 0;

        if (Evaluator.TryEvalNullableString(args[0].Expression, out string? maybeKey)
            && Evaluator.TryEvalStringList(args[1].Expression, out _))
        {
            key = maybeKey;
            idx = 1;
        }

        if (!Evaluator.TryEvalStringList(args[idx].Expression, out List<string> models))
        {
            return false;
        }

        idx++;
        int rem = args.Count - idx;
        if (rem != 6 && rem != 9)
        {
            return false;
        }

        if (!Evaluator.TryEvalDouble(args[idx++].Expression, out double x)
            || !Evaluator.TryEvalDouble(args[idx++].Expression, out double y)
            || !Evaluator.TryEvalDouble(args[idx++].Expression, out double z))
        {
            return false;
        }

        double rx = 0.0;
        double ry = 0.0;
        double rz = 0.0;
        if (rem == 9)
        {
            if (!Evaluator.TryEvalDouble(args[idx++].Expression, out rx)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out ry)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out rz))
            {
                return false;
            }
        }

        if (!Evaluator.TryEvalDouble(args[idx++].Expression, out double from)
            || !Evaluator.TryEvalDouble(args[idx++].Expression, out double span)
            || !Evaluator.TryEvalDouble(args[idx].Expression, out double interval))
        {
            return false;
        }

        rule = new SplineTemplatePropRule
        {
            Key = key,
            Pose = new PoseDegreesModel(x, y, z, rx, ry, rz),
            From = from,
            Span = span,
            Interval = interval,
        };
        rule.ModelKeys.AddRange(models);
        return true;
    }

    private bool TryParseCurveProp(
        SeparatedSyntaxList<ArgumentSyntax> args,
        IReadOnlyDictionary<string, string> anchorAliases,
        out CurvePropPlacementRule? rule)
    {
        rule = null;
        if (!TryParsePropBody(
            args,
            0,
            anchorAliases,
            out List<string> models,
            out PoseDegreesModel pose,
            out AnchoredDistance from,
            out double span,
            out double interval,
            out int count))
        {
            return false;
        }

        rule = new CurvePropPlacementRule
        {
            Pose = pose,
            From = from,
            Span = span,
            Interval = interval,
            Count = count,
        };
        rule.ModelKeys.AddRange(models);
        return true;
    }

    private bool TryParsePropBody(
        SeparatedSyntaxList<ArgumentSyntax> args,
        int startIdx,
        IReadOnlyDictionary<string, string> anchorAliases,
        out List<string> modelKeys,
        out PoseDegreesModel pose,
        out AnchoredDistance from,
        out double span,
        out double interval,
        out int count)
    {
        modelKeys = new List<string>();
        pose = PoseDegreesModel.Identity;
        from = AnchoredDistance.Zero;
        span = 0.0;
        interval = 1.0;
        count = int.MaxValue;

        if (args.Count <= startIdx || !Evaluator.TryEvalStringList(args[startIdx].Expression, out modelKeys))
        {
            return false;
        }

        int idx = startIdx + 1;
        int rem = args.Count - idx;
        bool hasRot;
        bool hasCount;

        if (rem == 6)
        {
            hasRot = false;
            hasCount = false;
        }
        else if (rem == 7)
        {
            hasRot = false;
            hasCount = true;
        }
        else if (rem == 9)
        {
            hasRot = true;
            hasCount = false;
        }
        else if (rem == 10)
        {
            hasRot = true;
            hasCount = true;
        }
        else
        {
            return false;
        }

        if (!Evaluator.TryEvalDouble(args[idx++].Expression, out double x)
            || !Evaluator.TryEvalDouble(args[idx++].Expression, out double y)
            || !Evaluator.TryEvalDouble(args[idx++].Expression, out double z))
        {
            return false;
        }

        double rx = 0.0;
        double ry = 0.0;
        double rz = 0.0;
        if (hasRot)
        {
            if (!Evaluator.TryEvalDouble(args[idx++].Expression, out rx)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out ry)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out rz))
            {
                return false;
            }
        }

        if (!Evaluator.TryEvalAnchoredDistance(args[idx++].Expression, anchorAliases, out from, out bool isMax) || isMax)
        {
            return false;
        }

        if (!Evaluator.TryEvalDouble(args[idx++].Expression, out span)
            || !Evaluator.TryEvalDouble(args[idx++].Expression, out interval))
        {
            return false;
        }

        if (hasCount && !Evaluator.TryEvalInt(args[idx].Expression, out count))
        {
            return false;
        }

        pose = new PoseDegreesModel(x, y, z, rx, ry, rz);
        return true;
    }

    private bool TryParseCurveExtrusion(
        SeparatedSyntaxList<ArgumentSyntax> args,
        IReadOnlyDictionary<string, string> anchorAliases,
        out CurveExtrusionPlacementRule? rule)
    {
        rule = null;
        if (args.Count < 5)
        {
            return false;
        }

        string? key = null;
        int idx = 0;

        if (Evaluator.TryEvalNullableString(args[0].Expression, out string? maybeKey)
            && Evaluator.TryEvalString(args[1].Expression, out _))
        {
            key = maybeKey;
            idx = 1;
        }

        if (!Evaluator.TryEvalString(args[idx].Expression, out string csKey))
        {
            return false;
        }

        idx++;
        int rem = args.Count - idx;

        double offsetX = 0.0;
        double offsetY = 0.0;
        double baseInterval;
        double maxDeviation;
        double maxAngleDelta;
        double vScale = 1.0;
        double vOffset = 0.0;
        AnchoredDistance from;
        AnchoredDistance? to = null;

        bool hasXY = rem == 6 || rem == 7 || rem == 8 || rem == 9;
        bool hasUV = rem == 8 || rem == 9;
        bool hasTo = rem == 5 || rem == 7 || rem == 9;

        if (rem < 4 || 9 < rem)
        {
            return false;
        }

        if (hasXY)
        {
            if (!Evaluator.TryEvalDouble(args[idx++].Expression, out offsetX)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out offsetY))
            {
                return false;
            }
        }

        if (!Evaluator.TryEvalDouble(args[idx++].Expression, out baseInterval)
            || !Evaluator.TryEvalDouble(args[idx++].Expression, out maxDeviation)
            || !Evaluator.TryEvalDouble(args[idx++].Expression, out maxAngleDelta))
        {
            return false;
        }

        if (hasUV)
        {
            if (!Evaluator.TryEvalDouble(args[idx++].Expression, out vScale)
                || !Evaluator.TryEvalDouble(args[idx++].Expression, out vOffset))
            {
                return false;
            }
        }

        if (!Evaluator.TryEvalAnchoredDistance(args[idx++].Expression, anchorAliases, out from, out bool fromMax) || fromMax)
        {
            return false;
        }

        if (hasTo)
        {
            if (!Evaluator.TryEvalAnchoredDistance(args[idx].Expression, anchorAliases, out AnchoredDistance toVal, out bool toIsMax))
            {
                return false;
            }

            to = toIsMax ? null : toVal;
        }

        rule = new CurveExtrusionPlacementRule
        {
            Key = key,
            CrossSectionKey = csKey,
            OffsetX = offsetX,
            OffsetY = offsetY,
            BaseInterval = baseInterval,
            MaxDeviation = maxDeviation,
            MaxAngleDeltaDeg = maxAngleDelta,
            VIntervalScale = vScale,
            VOffset = vOffset,
            From = from,
            To = to,
        };
        return true;
    }

    private bool TryParseJunctionCurveSegment(
        InvocationExpressionSyntax call,
        bool allowToEnd,
        out JunctionCurveSegment? segment,
        JunctionTemplateBinding templateBinding)
    {
        segment = null;
        string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
        if (method is null)
        {
            return false;
        }

        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

        if (method == "StraightTo")
        {
            if ((args.Count != 6 && args.Count != 7) || !TryReadPose6(args, 0, out PoseDegreesModel pose))
            {
                return false;
            }

            string? anchorId = null;
            if (args.Count == 7 && !TryReadOutAnchor(args[6], out anchorId, templateBinding))
            {
                return false;
            }

            segment = new JunctionCurveSegment(JunctionCurveSegmentKind.StraightTo, pose, 0.5, anchorId);
            return true;
        }

        if (method == "BezierTo")
        {
            if (args.Count < 6 || 8 < args.Count || !TryReadPose6(args, 0, out PoseDegreesModel pose))
            {
                return false;
            }

            string? anchorId = null;
            double handleScale = 0.5;
            if (args.Count == 7)
            {
                if (TryReadOutAnchor(args[6], out anchorId, templateBinding))
                {
                }
                else if (!Evaluator.TryEvalDouble(args[6].Expression, out handleScale))
                {
                    return false;
                }
            }
            else if (args.Count == 8)
            {
                if (!TryReadOutAnchor(args[6], out anchorId, templateBinding)
                    || !Evaluator.TryEvalDouble(args[7].Expression, out handleScale))
                {
                    return false;
                }
            }

            segment = new JunctionCurveSegment(JunctionCurveSegmentKind.BezierTo, pose, handleScale, anchorId);
            return true;
        }

        if (allowToEnd && method == "StraightToEnd")
        {
            string? anchorId = null;
            if (args.Count == 1 && !TryReadOutAnchor(args[0], out anchorId, templateBinding))
            {
                return false;
            }

            if (1 < args.Count)
            {
                return false;
            }

            segment = new JunctionCurveSegment(JunctionCurveSegmentKind.StraightToEnd, PoseDegreesModel.Identity, 0.5, anchorId);
            return true;
        }

        if (allowToEnd && method == "BezierToEnd")
        {
            if (2 < args.Count)
            {
                return false;
            }

            string? anchorId = null;
            double handleScale = 0.5;
            if (args.Count == 1)
            {
                if (TryReadOutAnchor(args[0], out anchorId, templateBinding))
                {
                }
                else if (!Evaluator.TryEvalDouble(args[0].Expression, out handleScale))
                {
                    return false;
                }
            }
            else if (args.Count == 2)
            {
                if (!TryReadOutAnchor(args[0], out anchorId, templateBinding)
                    || !Evaluator.TryEvalDouble(args[1].Expression, out handleScale))
                {
                    return false;
                }
            }

            segment = new JunctionCurveSegment(JunctionCurveSegmentKind.BezierToEnd, PoseDegreesModel.Identity, handleScale, anchorId);
            return true;
        }

        return false;
    }

    private bool TryParseJunctionAddCurveArgs(
        SeparatedSyntaxList<ArgumentSyntax> args,
        out string key,
        out JunctionCurveOriginModel? origin)
    {
        key = string.Empty;
        origin = null;

        if (args.Count == 7
            && Evaluator.TryEvalString(args[0].Expression, out key)
            && TryReadPose6(args, 1, out PoseDegreesModel absPose))
        {
            origin = JunctionCurveOriginModel.Absolute(absPose);
            return true;
        }

        if ((args.Count == 3 || args.Count == 9)
            && Evaluator.TryEvalString(args[0].Expression, out key)
            && Evaluator.TryEvalString(args[1].Expression, out string portKey)
            && Evaluator.TryEvalInt(args[2].Expression, out int pinIndex))
        {
            if (args.Count == 3)
            {
                origin = JunctionCurveOriginModel.FromPortPin(portKey, pinIndex);
                return true;
            }

            if (TryReadPose6(args, 3, out PoseDegreesModel relOffset))
            {
                origin = JunctionCurveOriginModel.FromPortPinWithOffset(portKey, pinIndex, relOffset);
                return true;
            }
        }

        return false;
    }

    private bool TryParseBeginSplineArgs(SeparatedSyntaxList<ArgumentSyntax> args, out string? templateKey, out PoseDegreesModel pose)
    {
        templateKey = null;
        pose = PoseDegreesModel.Identity;

        if (args.Count == 3 || args.Count == 6)
        {
            return TryReadPose3Or6(args, 0, out pose);
        }

        if ((args.Count == 4 || args.Count == 7)
            && Evaluator.TryEvalNullableString(args[0].Expression, out templateKey))
        {
            return TryReadPose3Or6(args, 1, out pose);
        }

        return false;
    }

    private bool TryReadPose3Or6(SeparatedSyntaxList<ArgumentSyntax> args, int startIdx, out PoseDegreesModel pose)
    {
        pose = PoseDegreesModel.Identity;
        int rem = args.Count - startIdx;
        if (rem != 3 && rem != 6)
        {
            return false;
        }

        if (!Evaluator.TryEvalDouble(args[startIdx].Expression, out double x)
            || !Evaluator.TryEvalDouble(args[startIdx + 1].Expression, out double y)
            || !Evaluator.TryEvalDouble(args[startIdx + 2].Expression, out double z))
        {
            return false;
        }

        double rx = 0.0;
        double ry = 0.0;
        double rz = 0.0;
        if (rem == 6)
        {
            if (!Evaluator.TryEvalDouble(args[startIdx + 3].Expression, out rx)
                || !Evaluator.TryEvalDouble(args[startIdx + 4].Expression, out ry)
                || !Evaluator.TryEvalDouble(args[startIdx + 5].Expression, out rz))
            {
                return false;
            }
        }

        pose = new PoseDegreesModel(x, y, z, rx, ry, rz);
        return true;
    }

    private bool TryReadPose6(SeparatedSyntaxList<ArgumentSyntax> args, int startIdx, out PoseDegreesModel pose)
    {
        pose = PoseDegreesModel.Identity;
        if (args.Count < startIdx + 6)
        {
            return false;
        }

        if (!Evaluator.TryEvalDouble(args[startIdx].Expression, out double x)
            || !Evaluator.TryEvalDouble(args[startIdx + 1].Expression, out double y)
            || !Evaluator.TryEvalDouble(args[startIdx + 2].Expression, out double z)
            || !Evaluator.TryEvalDouble(args[startIdx + 3].Expression, out double rx)
            || !Evaluator.TryEvalDouble(args[startIdx + 4].Expression, out double ry)
            || !Evaluator.TryEvalDouble(args[startIdx + 5].Expression, out double rz))
        {
            return false;
        }

        pose = new PoseDegreesModel(x, y, z, rx, ry, rz);
        return true;
    }

    private bool TryParseChunksIndexer(ExpressionSyntax expr, out ChunkIndexModel chunk)
    {
        chunk = ChunkIndexModel.Zero;
        if (expr is not ElementAccessExpressionSyntax elemAccess
            || elemAccess.Expression is not IdentifierNameSyntax chunksIdent
            || chunksIdent.Identifier.ValueText != "Chunks"
            || elemAccess.ArgumentList.Arguments.Count != 2
            || !Evaluator.TryEvalInt(elemAccess.ArgumentList.Arguments[0].Expression, out int cx)
            || !Evaluator.TryEvalInt(elemAccess.ArgumentList.Arguments[1].Expression, out int cz))
        {
            return false;
        }

        chunk = new ChunkIndexModel(cx, cz);
        return true;
    }

    private static bool TryReadOutAnchor(ArgumentSyntax arg, out string? ssaAnchor, JunctionTemplateBinding templateBinding)
    {
        ssaAnchor = null;
        if (!RoslynCsxHelpers.IsOutArgument(arg, out string? rawAnchor))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(rawAnchor))
        {
            ssaAnchor = templateBinding.AllocateAnchor(rawAnchor);
        }

        return true;
    }

    private bool TryReadOutWorldAnchor(ArgumentSyntax arg, out string? ssaAnchor)
    {
        ssaAnchor = null;
        if (!RoslynCsxHelpers.IsOutArgument(arg, out string? rawAnchor))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(rawAnchor))
        {
            ssaAnchor = AllocateUniqueAnchor(rawAnchor, WorldDeclaredAnchors);
            WorldAnchorAliases[rawAnchor] = ssaAnchor;
        }

        return true;
    }

    private string ResolveScriptAssetPath(string path, string scriptFilePath)
    {
        string normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
        {
            return normalized;
        }

        string scriptDir = Path.GetDirectoryName(scriptFilePath) ?? SourceWorldDirectory;
        string fullResolved = Path.GetFullPath(Path.Combine(scriptDir, normalized));
        return Path.GetRelativePath(SourceWorldDirectory, fullResolved).Replace('\\', '/');
    }

    private static string AllocateUniqueAnchor(string requestedName, HashSet<string> usedAnchors)
    {
        string baseName = CSharpIdentifierUtilities.SanitizeOrFallback(requestedName, "s");
        string candidate = baseName;
        int suffix = 2;
        while (!usedAnchors.Add(candidate))
        {
            candidate = $"{baseName}_{suffix}";
            suffix++;
        }

        return candidate;
    }
}
