using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using TransportX.Diagnostics;
using TransportX.WorldEditor.Serialization.Diagnostics;
using EditorDiagnosticSeverity = TransportX.WorldEditor.Serialization.Diagnostics.DiagnosticSeverity;

namespace TransportX.WorldEditor.Serialization.Xml;

/// <summary>
/// Shared adapter utilities delegating XML serialization/deserialization directly to
/// TransportX's own <c>XmlSerializer</c> DTO types while formatting output cleanly and mapping errors.
/// </summary>
internal static class TransportXmlBridge
{
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static T? Deserialize<T>(string filePath, string xmlText, List<ParseDiagnostic> diagnostics)
        where T : class
    {
        XmlSerializer serializer = new XmlSerializer(typeof(T));
        try
        {
            using StringReader stringReader = new StringReader(xmlText);
            using XmlReader reader = XmlReader.Create(stringReader);
            return serializer.Deserialize(reader) as T;
        }
        catch (InvalidOperationException ex) when (ex.InnerException is XmlException xmlEx)
        {
            diagnostics.Add(new ParseDiagnostic(
                EditorDiagnosticSeverity.Error,
                filePath,
                xmlEx.LineNumber,
                xmlEx.LinePosition,
                $"XML の解析に失敗しました: {xmlEx.Message}"));
            return null;
        }
        catch (XmlException xmlEx)
        {
            diagnostics.Add(new ParseDiagnostic(
                EditorDiagnosticSeverity.Error,
                filePath,
                xmlEx.LineNumber,
                xmlEx.LinePosition,
                $"XML の解析に失敗しました: {xmlEx.Message}"));
            return null;
        }
        catch (InvalidOperationException ex)
        {
            diagnostics.Add(new ParseDiagnostic(
                EditorDiagnosticSeverity.Error,
                filePath,
                1,
                1,
                $"XML スキーマの読み込みに失敗しました: {ex.InnerException?.Message ?? ex.Message}"));
            return null;
        }
    }

    public static string Serialize<T>(T dto)
    {
        XmlSerializer serializer = new XmlSerializer(typeof(T));
        XmlWriterSettings settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = true,
            IndentChars = "    ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false,
        };

        using MemoryStream stream = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(stream, settings))
        {
            XmlSerializerNamespaces namespaces = new XmlSerializerNamespaces();
            namespaces.Add(string.Empty, string.Empty);
            serializer.Serialize(writer, dto, namespaces);
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static void AppendErrors(
        IEnumerable<Error> errors,
        string fallbackFilePath,
        IList<ParseDiagnostic> diagnostics)
    {
        foreach (Error error in errors)
        {
            EditorDiagnosticSeverity severity = error.Level switch
            {
                ErrorLevel.Info => EditorDiagnosticSeverity.Info,
                ErrorLevel.Warning => EditorDiagnosticSeverity.Warning,
                _ => EditorDiagnosticSeverity.Error,
            };

            string path = string.IsNullOrEmpty(error.Location) ? fallbackFilePath : error.Location;
            int line = 0 < error.LineNumber ? error.LineNumber : 1;
            int col = 0 < error.LinePosition ? error.LinePosition : 1;
            diagnostics.Add(new ParseDiagnostic(severity, path, line, col, error.Message));
        }
    }

    public static string ColorToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public static Color HexToColor(string? htmlColor, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(htmlColor))
        {
            return fallback;
        }

        try
        {
            return ColorTranslator.FromHtml(htmlColor.Trim());
        }
        catch
        {
            return fallback;
        }
    }
}
