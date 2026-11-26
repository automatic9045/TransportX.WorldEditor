using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TransportX.WorldEditor.Core.Validation;

/// <summary>
/// Validates and sanitizes strings against C# identifier rules (ECMA-334 / Roslyn),
/// permitting Unicode letters (including Japanese characters) while rejecting spaces,
/// invalid punctuation, leading digits, and C# reserved keywords.
/// </summary>
public static class CSharpIdentifierUtilities
{
    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
        "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    };

    public static bool IsReservedKeyword(string? value)
    {
        return !string.IsNullOrEmpty(value) && ReservedKeywords.Contains(value);
    }

    public static bool IsValidIdentifier(string? candidate)
    {
        return TryValidateIdentifier(candidate, out _);
    }

    public static bool IsValidAnchorId(string? candidate)
    {
        return TryValidateAnchorId(candidate, out _);
    }

    public static bool TryValidateAnchorId(string? candidate, out string errorReason)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            errorReason = "Anchor ID は空にできません。";
            return false;
        }

        if (!string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal))
        {
            errorReason = "Anchor ID の前後に空白を含めることはできません。";
            return false;
        }

        foreach (Rune rune in candidate.EnumerateRunes())
        {
            if (!IsIdentifierPartRune(rune))
            {
                errorReason = $"Anchor ID '{candidate}' の文字 '{rune}' は使用できません（空白や記号は不可）。";
                return false;
            }
        }

        errorReason = string.Empty;
        return true;
    }

    public static bool TryValidateIdentifier(string? candidate, out string errorReason)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            errorReason = "Key は必須です。空にはできません。";
            return false;
        }

        if (!string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal))
        {
            errorReason = "Key の前後に空白を含めることはできません。";
            return false;
        }

        if (ReservedKeywords.Contains(candidate))
        {
            errorReason = $"'{candidate}' は C# の予約語のため Key に使用できません。";
            return false;
        }

        bool isFirst = true;
        foreach (Rune rune in candidate.EnumerateRunes())
        {
            if (isFirst)
            {
                isFirst = false;
                if (!IsIdentifierStartRune(rune))
                {
                    errorReason = $"Key '{candidate}' の先頭文字 '{rune}' は C# の識別子として使用できません。";
                    return false;
                }
            }
            else if (!IsIdentifierPartRune(rune))
            {
                errorReason = $"Key '{candidate}' の文字 '{rune}' は C# の識別子として使用できません（空白や記号は不可）。";
                return false;
            }
        }

        errorReason = string.Empty;
        return true;
    }

    public static bool IsIdentifierStartRune(Rune rune)
    {
        if (rune.Value == '_')
        {
            return true;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.LetterNumber;
    }

    public static bool IsIdentifierPartRune(Rune rune)
    {
        if (IsIdentifierStartRune(rune))
        {
            return true;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.Format;
    }

    public static string SanitizeOrFallback(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        StringBuilder sb = new(raw.Length);
        foreach (Rune rune in raw.Trim().EnumerateRunes())
        {
            if (sb.Length == 0)
            {
                if (IsIdentifierStartRune(rune))
                {
                    sb.Append(rune.ToString());
                }
                else if (IsIdentifierPartRune(rune))
                {
                    sb.Append('_').Append(rune.ToString());
                }
                else
                {
                    sb.Append('_');
                }
            }
            else if (IsIdentifierPartRune(rune))
            {
                sb.Append(rune.ToString());
            }
            else
            {
                sb.Append('_');
            }
        }

        if (sb.Length == 0)
        {
            return fallback;
        }

        string candidate = sb.ToString();
        return ReservedKeywords.Contains(candidate) ? $"_{candidate}" : candidate;
    }
}
