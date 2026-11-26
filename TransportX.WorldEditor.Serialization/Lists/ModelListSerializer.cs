using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Serialization.Diagnostics;

namespace TransportX.WorldEditor.Serialization.Lists;

/// <summary>
/// Parses and serializes TransportX <c>Models.txt</c> TSV asset lists in strict accordance with
/// <c>AssetList.cs</c>, <c>ModelsInternal.cs</c>, <c>Parser.cs</c>, and <c>ModelListInterpreter.cs</c>.
/// </summary>
public static partial class ModelListSerializer
{
    [GeneratedRegex(@"^""(.*)""$")]
    private static partial Regex QuotedStringRegex();

    [GeneratedRegex(@"^[+-]?[0-9]+$")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex(@"^[+-]?[0-9]*\.[0-9]+$")]
    private static partial Regex RealRegex();

    public static (ModelListDocument? Document, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(
        string? bundleKey,
        string relativePath,
        string filePath,
        string text)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(text);

        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>();
        ModelListDocument doc = new ModelListDocument
        {
            BundleKey = bundleKey,
            RelativePath = relativePath,
        };

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            int lineNumber = lineIndex + 1;
            if (!TrySplitTsvLine(lines[lineIndex], [2, 3], filePath, lineNumber, diagnostics, out string[] columns))
            {
                continue;
            }

            string key = columns[0];
            string? modelPath = string.IsNullOrEmpty(columns[1]) || columns[1].Equals("NONE", StringComparison.OrdinalIgnoreCase)
                ? null
                : columns[1];

            ModelColliderSpec collider = ModelColliderSpec.NonCollision(true);
            if (columns.Length == 3)
            {
                string commandListText = columns[2].ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(commandListText))
                {
                    if (!commandListText.StartsWith('$'))
                    {
                        diagnostics.Add(new ParseDiagnostic(
                            DiagnosticSeverity.Error,
                            filePath,
                            lineNumber,
                            1,
                            $"コマンド '{columns[2]}' は無効です。冒頭に '$' がありません。"));
                    }
                    else
                    {
                        string[] commandParts = commandListText.Split('$');
                        bool makeLeftHanded = true;
                        ModelColliderSpec? parsedCollider = null;

                        for (int partIdx = 1; partIdx < commandParts.Length; partIdx++)
                        {
                            string commandText = commandParts[partIdx];
                            if (!TryParseCommand(commandText, filePath, lineNumber, diagnostics, out string funcName, out List<object> args))
                            {
                                continue;
                            }

                            if (funcName == "lh")
                            {
                                if (args.Count != 0)
                                {
                                    diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, filePath, lineNumber, 1, "$LH に引数を渡すことはできません。"));
                                    continue;
                                }

                                makeLeftHanded = false;
                            }
                            else
                            {
                                parsedCollider = InterpretColliderCommand(funcName, args, makeLeftHanded, filePath, lineNumber, diagnostics);
                            }
                        }

                        if (parsedCollider is not null)
                        {
                            collider = parsedCollider with { MakeLeftHanded = makeLeftHanded };
                        }
                        else
                        {
                            collider = ModelColliderSpec.NonCollision(makeLeftHanded);
                        }
                    }
                }
            }

            doc.Entries.Add(new ModelListEntry
            {
                Key = key,
                ModelPath = modelPath,
                Collider = collider,
            });
        }

        return (doc, diagnostics);
    }

    public static string Generate(ModelListDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        StringBuilder sb = new StringBuilder();
        foreach (ModelListEntry entry in document.Entries)
        {
            string modelPathText = string.IsNullOrWhiteSpace(entry.ModelPath) ? "NONE" : entry.ModelPath;
            string commandsText = FormatColliderCommands(entry.Collider);
            sb.Append(entry.Key).Append('\t').Append(modelPathText).Append('\t').Append(commandsText).Append('\n');
        }

        return sb.ToString();
    }

    internal static bool TrySplitTsvLine(
        string rawText,
        int[] validColumnLengths,
        string filePath,
        int lineNumber,
        IList<ParseDiagnostic> diagnostics,
        out string[] columns)
    {
        string[] rawLine = rawText.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rawLine.Length == 0 || rawLine[0].StartsWith('#'))
        {
            columns = Array.Empty<string>();
            return false;
        }

        int columnCount = 0;
        for (int i = 0; i < rawLine.Length; i++)
        {
            int commentIndex = rawLine[i].IndexOf('#');
            if (0 <= commentIndex)
            {
                string token = rawLine[i][..commentIndex].TrimEnd();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    rawLine[i] = token;
                    columnCount = i + 1;
                }

                break;
            }

            if (!string.IsNullOrWhiteSpace(rawLine[i]))
            {
                columnCount = i + 1;
            }
        }

        if (columnCount == 0)
        {
            columns = Array.Empty<string>();
            return false;
        }

        bool validLength = false;
        for (int i = 0; i < validColumnLengths.Length; i++)
        {
            if (validColumnLengths[i] == columnCount)
            {
                validLength = true;
                break;
            }
        }

        if (!validLength)
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                filePath,
                lineNumber,
                1,
                $"レコード '{rawLine[0]}' の列数 ({columnCount}) が正しくありません。"));
            columns = Array.Empty<string>();
            return false;
        }

        columns = columnCount == rawLine.Length ? rawLine : rawLine.AsSpan(0, columnCount).ToArray();
        return true;
    }

    private static string FormatColliderCommands(ModelColliderSpec spec)
    {
        StringBuilder sb = new StringBuilder();
        if (!spec.MakeLeftHanded)
        {
            sb.Append("$LH");
        }

        switch (spec.Kind)
        {
            case ModelColliderKind.NonCollision:
                sb.Append("$NonCollision");
                break;

            case ModelColliderKind.BoundingBox:
                sb.Append("$BoundingBox");
                if (spec.Material is not null)
                {
                    sb.Append('(').Append(FormatMaterialArgs(spec.Material)).Append(')');
                }
                break;

            case ModelColliderKind.ConvexHull:
                sb.Append("$ConvexHull");
                if (spec.Material is not null)
                {
                    sb.Append('(').Append(FormatMaterialArgs(spec.Material)).Append(')');
                }
                break;

            case ModelColliderKind.ClosedModel:
            case ModelColliderKind.OpenModel:
                sb.Append(spec.Kind == ModelColliderKind.ClosedModel ? "$ClosedModel" : "$OpenModel");
                AppendMeshColliderArgs(sb, spec.CollisionModelPath, spec.Material);
                break;

            case ModelColliderKind.Box:
                sb.Append("$Box(")
                    .Append(FormatNumber(spec.SizeX)).Append(", ")
                    .Append(FormatNumber(spec.SizeY)).Append(", ")
                    .Append(FormatNumber(spec.SizeZ));
                AppendOptionalPoseAndMaterialArgs(sb, spec);
                sb.Append(')');
                break;

            case ModelColliderKind.Cylinder:
                sb.Append("$Cylinder(")
                    .Append(FormatNumber(spec.SizeX)).Append(", ")
                    .Append(FormatNumber(spec.SizeY));
                AppendOptionalPoseAndMaterialArgs(sb, spec);
                sb.Append(')');
                break;

            case ModelColliderKind.Sphere:
                sb.Append("$Sphere(")
                    .Append(FormatNumber(spec.SizeX));
                AppendOptionalPoseAndMaterialArgs(sb, spec);
                sb.Append(')');
                break;
        }

        return sb.ToString();
    }

    private static void AppendMeshColliderArgs(StringBuilder sb, string? collisionModelPath, ColliderMaterialParams? material)
    {
        bool hasPath = !string.IsNullOrEmpty(collisionModelPath);
        bool hasMaterial = material is not null;

        if (!hasPath && !hasMaterial)
        {
            return;
        }

        sb.Append('(');
        if (hasPath)
        {
            sb.Append(FormatQuotedString(collisionModelPath!));
            if (hasMaterial)
            {
                sb.Append(", ").Append(FormatMaterialArgs(material!));
            }
        }
        else if (hasMaterial)
        {
            sb.Append(FormatMaterialArgs(material!));
        }

        sb.Append(')');
    }

    private static void AppendOptionalPoseAndMaterialArgs(StringBuilder sb, ModelColliderSpec spec)
    {
        bool emitTranslation = spec.HasTranslationOffset || spec.HasRotationOffset;
        bool emitRotation = spec.HasRotationOffset;

        if (emitTranslation)
        {
            sb.Append(", ").Append(FormatNumber((float)spec.OffsetPose.X))
                .Append(", ").Append(FormatNumber((float)spec.OffsetPose.Y))
                .Append(", ").Append(FormatNumber((float)spec.OffsetPose.Z));
        }

        if (emitRotation)
        {
            sb.Append(", ").Append(FormatNumber((float)spec.OffsetPose.RotationX))
                .Append(", ").Append(FormatNumber((float)spec.OffsetPose.RotationY))
                .Append(", ").Append(FormatNumber((float)spec.OffsetPose.RotationZ));
        }

        if (spec.Material is not null)
        {
            sb.Append(", ").Append(FormatMaterialArgs(spec.Material));
        }
    }

    private static string FormatMaterialArgs(ColliderMaterialParams material)
    {
        return string.Join(", ",
            FormatNumber(material.FrictionCoefficient),
            FormatNumber(material.MaxRecoveryVelocity),
            FormatNumber(material.SpringFrequency),
            FormatNumber(material.SpringDampingRatio));
    }

    private static string FormatNumber(float value)
    {
        return value.ToString("0.#######", CultureInfo.InvariantCulture);
    }

    private static string FormatQuotedString(string text)
    {
        return "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static bool TryParseCommand(
        string text,
        string filePath,
        int lineNumber,
        IList<ParseDiagnostic> diagnostics,
        out string name,
        out List<object> parsedArgs)
    {
        name = string.Empty;
        parsedArgs = new List<object>();

        string? funcName = null;
        List<string> argTexts = new List<string>();
        StringBuilder buffer = new StringBuilder();
        bool isArgBegun = false;
        bool isInLiteral = false;
        bool isEscapingInLiteral = false;
        bool isArgCompleted = false;

        foreach (char c in text)
        {
            if (funcName is null)
            {
                switch (c)
                {
                    case ' ':
                    case '\t':
                        break;

                    case '(':
                        funcName = buffer.ToString();
                        buffer.Clear();
                        break;

                    default:
                        if (!(('a' <= c && c <= 'z') || ('A' <= c && c <= 'Z') || (0 < buffer.Length && '0' <= c && c <= '9')))
                        {
                            diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, filePath, lineNumber, 1, $"関数名として使用できない文字 '{c}' が含まれています。"));
                            return false;
                        }

                        buffer.Append(c);
                        break;
                }
            }
            else if (isInLiteral)
            {
                buffer.Append(c);
                if (isEscapingInLiteral)
                {
                    isEscapingInLiteral = false;
                }
                else if (c == '\\')
                {
                    isEscapingInLiteral = true;
                }
                else if (c == '"')
                {
                    isInLiteral = false;
                    isArgCompleted = true;
                }
            }
            else
            {
                switch (c)
                {
                    case ' ':
                    case '\t':
                        if (isArgBegun)
                        {
                            isArgCompleted = true;
                        }
                        break;

                    case ',':
                    case ')':
                        if (!isArgBegun)
                        {
                            diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, filePath, lineNumber, 1, $"コマンド '${funcName}' の {argTexts.Count + 1} 個目の引数が指定されていません（引数なしコマンドに '()' は付けられません）。"));
                            return false;
                        }

                        argTexts.Add(buffer.ToString());
                        buffer.Clear();
                        isArgBegun = false;
                        isArgCompleted = false;
                        break;

                    case '"':
                        if (isArgBegun || isArgCompleted)
                        {
                            diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, filePath, lineNumber, 1, $"{argTexts.Count + 1} 個目の引数の形式が不正です。"));
                            return false;
                        }

                        buffer.Append(c);
                        isArgBegun = true;
                        isInLiteral = true;
                        break;

                    default:
                        if (isArgCompleted)
                        {
                            diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, filePath, lineNumber, 1, $"{argTexts.Count + 1} 個目の引数の形式が不正です。"));
                            return false;
                        }

                        isArgBegun = true;
                        buffer.Append(c);
                        break;
                }
            }
        }

        funcName ??= buffer.ToString();
        if (isArgBegun || isInLiteral || isArgCompleted)
        {
            diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, filePath, lineNumber, 1, $"コマンド '${text}' の構文が終了していません。"));
            return false;
        }

        foreach (string argText in argTexts)
        {
            Match strMatch = QuotedStringRegex().Match(argText);
            if (strMatch.Success)
            {
                string unescaped = Regex.Replace(strMatch.Groups[1].Value, @"\\.", m => m.Value switch
                {
                    @"\\" => @"\",
                    @"\""" => "\"",
                    _ => m.Value,
                });
                parsedArgs.Add(unescaped);
            }
            else if (IntegerRegex().IsMatch(argText) && int.TryParse(argText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
            {
                parsedArgs.Add((float)intVal);
            }
            else if (RealRegex().IsMatch(argText) && float.TryParse(argText, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatVal))
            {
                parsedArgs.Add(floatVal);
            }
            else
            {
                diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, filePath, lineNumber, 1, $"引数 '{argText}' は不正な形式です。"));
                return false;
            }
        }

        name = funcName;
        return true;
    }

    private static ModelColliderSpec? InterpretColliderCommand(
        string funcName,
        IReadOnlyList<object> args,
        bool makeLeftHanded,
        string filePath,
        int lineNumber,
        IList<ParseDiagnostic> diagnostics)
    {
        switch (funcName)
        {
            case "noncollision":
                if (args.Count == 0)
                {
                    return ModelColliderSpec.NonCollision(makeLeftHanded);
                }
                break;

            case "boundingbox":
                if (args.Count == 0)
                {
                    return ModelColliderSpec.BoundingBox(null, makeLeftHanded);
                }

                if (args.Count == 4 && TryReadMaterial(args, 0, out ColliderMaterialParams bbMat))
                {
                    return ModelColliderSpec.BoundingBox(bbMat, makeLeftHanded);
                }
                break;

            case "convexhull":
                if (args.Count == 0)
                {
                    return ModelColliderSpec.ConvexHull(null, makeLeftHanded);
                }

                if (args.Count == 4 && TryReadMaterial(args, 0, out ColliderMaterialParams chMat))
                {
                    return ModelColliderSpec.ConvexHull(chMat, makeLeftHanded);
                }
                break;

            case "closedmodel":
            case "openmodel":
                bool isOpen = funcName == "openmodel";
                if (args.Count == 0)
                {
                    return ModelColliderSpec.MeshCollider(isOpen, null, null, makeLeftHanded);
                }

                if (args.Count == 1 && args[0] is string pathOnly)
                {
                    return ModelColliderSpec.MeshCollider(isOpen, pathOnly, null, makeLeftHanded);
                }

                if (args.Count == 4 && TryReadMaterial(args, 0, out ColliderMaterialParams meshMatOnly))
                {
                    return ModelColliderSpec.MeshCollider(isOpen, null, meshMatOnly, makeLeftHanded);
                }

                if (args.Count == 5 && args[0] is string meshPath && TryReadMaterial(args, 1, out ColliderMaterialParams meshMat))
                {
                    return ModelColliderSpec.MeshCollider(isOpen, meshPath, meshMat, makeLeftHanded);
                }
                break;

            case "box":
                return ParsePrimitiveCollider(ModelColliderKind.Box, 3, args, makeLeftHanded, filePath, lineNumber, diagnostics);

            case "cylinder":
                return ParsePrimitiveCollider(ModelColliderKind.Cylinder, 2, args, makeLeftHanded, filePath, lineNumber, diagnostics);

            case "sphere":
                return ParsePrimitiveCollider(ModelColliderKind.Sphere, 1, args, makeLeftHanded, filePath, lineNumber, diagnostics);
        }

        diagnostics.Add(new ParseDiagnostic(
            DiagnosticSeverity.Error,
            filePath,
            lineNumber,
            1,
            $"コマンド '${funcName}' の名前または引数仕様 (引数数 {args.Count}) が不正です。"));
        return null;
    }

    private static ModelColliderSpec? ParsePrimitiveCollider(
        ModelColliderKind kind,
        int shapeArgCount,
        IReadOnlyList<object> args,
        bool makeLeftHanded,
        string filePath,
        int lineNumber,
        IList<ParseDiagnostic> diagnostics)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is not float)
            {
                diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, filePath, lineNumber, 1, $"コマンド '${kind}' の {i + 1} 番目の引数は数値である必要があります。"));
                return null;
            }
        }

        float s0 = shapeArgCount <= args.Count ? (float)args[0] : 0f;
        float s1 = 1 < shapeArgCount && shapeArgCount <= args.Count ? (float)args[1] : 0f;
        float s2 = 2 < shapeArgCount && shapeArgCount <= args.Count ? (float)args[2] : 0f;

        PoseDegreesModel? offset = null;
        bool includeRot = false;
        ColliderMaterialParams? mat = null;

        int rem = args.Count - shapeArgCount;
        if (rem == 0)
        {
        }
        else if (rem == 3)
        {
            offset = PoseDegreesModel.FromTranslation((float)args[shapeArgCount], (float)args[shapeArgCount + 1], (float)args[shapeArgCount + 2]);
        }
        else if (rem == 4 && TryReadMaterial(args, shapeArgCount, out ColliderMaterialParams m4))
        {
            mat = m4;
        }
        else if (rem == 6)
        {
            offset = new PoseDegreesModel(
                (float)args[shapeArgCount], (float)args[shapeArgCount + 1], (float)args[shapeArgCount + 2],
                (float)args[shapeArgCount + 3], (float)args[shapeArgCount + 4], (float)args[shapeArgCount + 5]);
            includeRot = true;
        }
        else if (rem == 7 && TryReadMaterial(args, shapeArgCount + 3, out ColliderMaterialParams m7))
        {
            offset = PoseDegreesModel.FromTranslation((float)args[shapeArgCount], (float)args[shapeArgCount + 1], (float)args[shapeArgCount + 2]);
            mat = m7;
        }
        else if (rem == 10 && TryReadMaterial(args, shapeArgCount + 6, out ColliderMaterialParams m10))
        {
            offset = new PoseDegreesModel(
                (float)args[shapeArgCount], (float)args[shapeArgCount + 1], (float)args[shapeArgCount + 2],
                (float)args[shapeArgCount + 3], (float)args[shapeArgCount + 4], (float)args[shapeArgCount + 5]);
            includeRot = true;
            mat = m10;
        }
        else
        {
            diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, filePath, lineNumber, 1, $"コマンド '${kind}' の引数数 {args.Count} が正しくありません。"));
            return null;
        }

        return kind switch
        {
            ModelColliderKind.Box => ModelColliderSpec.BoxCollider(s0, s1, s2, offset, includeRot, mat, makeLeftHanded),
            ModelColliderKind.Cylinder => ModelColliderSpec.CylinderCollider(s0, s1, offset, includeRot, mat, makeLeftHanded),
            ModelColliderKind.Sphere => ModelColliderSpec.SphereCollider(s0, offset, includeRot, mat, makeLeftHanded),
            _ => null,
        };
    }

    private static bool TryReadMaterial(IReadOnlyList<object> args, int startIndex, out ColliderMaterialParams material)
    {
        if (startIndex + 3 < args.Count
            && args[startIndex] is float f
            && args[startIndex + 1] is float mrv
            && args[startIndex + 2] is float sf
            && args[startIndex + 3] is float sdr)
        {
            material = new ColliderMaterialParams(f, mrv, sf, sdr);
            return true;
        }

        material = new ColliderMaterialParams();
        return false;
    }
}
