using System;
using System.Diagnostics;
using System.IO;
using TransportX.WorldEditor.Core.Models;

namespace TransportX.WorldEditor.Services;

/// <summary>
/// Resolves and launches external Mode 3 (<c>TransportX.Player.exe</c>) runtime processes
/// and opens script/resource files in the system default external code editor.
/// </summary>
public static class PlayerLauncherService
{
    public static string? ResolvePlayerExecutablePath(WorldProject? project)
    {
        if (project is not null && !string.IsNullOrWhiteSpace(project.Sidecar.PlayerExecutablePath))
        {
            string configured = project.Sidecar.PlayerExecutablePath;
            if (File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }
        }

        string appLocal = Path.Combine(AppContext.BaseDirectory, "TransportX.Player.exe");
        if (File.Exists(appLocal))
        {
            return Path.GetFullPath(appLocal);
        }

        ReadOnlySpan<string> candidateSubPaths =
        [
            Path.Combine("external", "TransportX", "_out", "bin", "TransportX.Player.exe"),
            Path.Combine("external", "TransportX", "TransportX.Player", "bin", "Release", "net10.0", "TransportX.Player.exe"),
            Path.Combine("external", "TransportX", "TransportX.Player", "bin", "Debug", "net10.0", "TransportX.Player.exe"),
        ];

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && dir is not null; depth++)
        {
            for (int i = 0; i < candidateSubPaths.Length; i++)
            {
                string candidate = Path.Combine(dir.FullName, candidateSubPaths[i]);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static bool LaunchPlayer(string playerExePath, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(playerExePath) || !File.Exists(playerExePath))
        {
            errorMessage = $"TransportX.Player.exe が見つかりません: '{playerExePath}'";
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(playerExePath);
            ProcessStartInfo startInfo = new()
            {
                FileName = fullPath,
                WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty,
                UseShellExecute = true,
            };
            Process? started = Process.Start(startInfo);
            if (started is null)
            {
                errorMessage = "TransportX.Player.exe の起動に失敗しました。";
                return false;
            }

            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public static bool OpenInExternalEditor(string filePath, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            errorMessage = $"ファイルが見つかりません: '{filePath}'";
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = Path.GetFullPath(filePath),
                UseShellExecute = true,
            };
            Process.Start(startInfo);
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
