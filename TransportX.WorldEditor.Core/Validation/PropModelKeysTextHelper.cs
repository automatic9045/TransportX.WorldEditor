using System;
using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Validation;

/// <summary>
/// Parses and formats comma-separated prop model key lists while preserving empty slots (<c>""</c>).
/// </summary>
public static class PropModelKeysTextHelper
{
    public static List<string> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new List<string>();
        }

        if (!text.Contains(','))
        {
            string single = text.Trim();
            return string.IsNullOrEmpty(single) ? new List<string>() : [single];
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        List<string> result = new(parts.Length);
        foreach (string part in parts)
        {
            result.Add(part);
        }

        return result;
    }

    public static string Format(IReadOnlyList<string> modelKeys)
    {
        ArgumentNullException.ThrowIfNull(modelKeys);
        return string.Join(", ", modelKeys);
    }

    public static string Append(string? currentText, string keyToAdd)
    {
        List<string> keys = Parse(currentText);
        keys.Add(keyToAdd.Trim());
        return Format(keys);
    }
}
