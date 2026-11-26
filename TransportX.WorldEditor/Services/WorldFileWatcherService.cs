using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TransportX.WorldEditor.Services;

/// <summary>
/// Watches an active world directory (and external <c>WorldInfo.xml</c> parent folder when separate)
/// for external modifications, suppressing editor self-writes and debouncing burst file-system notifications.
/// </summary>
public sealed class WorldFileWatcherService : IDisposable
{
    private readonly object SyncRoot = new();
    private readonly ConcurrentDictionary<string, DateTime> SuppressedWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> PendingDebounces = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? WorldDirWatcher;
    private FileSystemWatcher? WorldInfoWatcher;
    private bool Disposed;

    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromMilliseconds(220);

    public event Action<string>? FileExternallyChanged;

    public void StartWatching(string? worldDirectory, string? worldInfoFilePath)
    {
        lock (SyncRoot)
        {
            StopInternal();
            if (Disposed)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(worldDirectory) && Directory.Exists(worldDirectory))
            {
                string fullWorldDir = Path.GetFullPath(worldDirectory);
                FileSystemWatcher watcher = new(fullWorldDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                watcher.Changed += OnWatcherChanged;
                watcher.Created += OnWatcherChanged;
                watcher.Renamed += OnWatcherRenamed;
                watcher.EnableRaisingEvents = true;
                WorldDirWatcher = watcher;
            }

            if (!string.IsNullOrWhiteSpace(worldInfoFilePath))
            {
                string fullInfoPath = Path.GetFullPath(worldInfoFilePath);
                string? infoDir = Path.GetDirectoryName(fullInfoPath);
                string infoFileName = Path.GetFileName(fullInfoPath);
                if (!string.IsNullOrEmpty(infoDir)
                    && Directory.Exists(infoDir)
                    && !string.IsNullOrEmpty(infoFileName)
                    && (string.IsNullOrWhiteSpace(worldDirectory)
                        || !fullInfoPath.StartsWith(Path.GetFullPath(worldDirectory), StringComparison.OrdinalIgnoreCase)))
                {
                    FileSystemWatcher infoWatcher = new(infoDir, infoFileName)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    };
                    infoWatcher.Changed += OnWatcherChanged;
                    infoWatcher.Created += OnWatcherChanged;
                    infoWatcher.Renamed += OnWatcherRenamed;
                    infoWatcher.EnableRaisingEvents = true;
                    WorldInfoWatcher = infoWatcher;
                }
            }
        }
    }

    public void Stop()
    {
        lock (SyncRoot)
        {
            StopInternal();
        }
    }

    public void SuppressPaths(IEnumerable<string> fullPaths, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);
        DateTime expiresAtUtc = DateTime.UtcNow.Add(duration);
        foreach (string rawPath in fullPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(rawPath);
            SuppressedWrites[fullPath] = expiresAtUtc;
        }
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        HandleRawEvent(e.FullPath);
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        HandleRawEvent(e.FullPath);
    }

    private void HandleRawEvent(string rawFullPath)
    {
        if (Disposed || string.IsNullOrWhiteSpace(rawFullPath))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rawFullPath);
        }
        catch
        {
            return;
        }

        string fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(fileName)
            || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("~", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (SuppressedWrites.TryGetValue(fullPath, out DateTime expiresAtUtc))
        {
            if (DateTime.UtcNow <= expiresAtUtc)
            {
                return;
            }

            SuppressedWrites.TryRemove(fullPath, out _);
        }

        CancellationTokenSource nextCts = new();
        lock (SyncRoot)
        {
            if (Disposed)
            {
                nextCts.Dispose();
                return;
            }

            if (PendingDebounces.TryGetValue(fullPath, out CancellationTokenSource? prev))
            {
                prev.Cancel();
                prev.Dispose();
            }

            PendingDebounces[fullPath] = nextCts;
        }

        TimeSpan delay = DebounceDelay;
        CancellationToken token = nextCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (TimeSpan.Zero < delay)
                {
                    await Task.Delay(delay, token);
                }

                if (token.IsCancellationRequested || Disposed)
                {
                    return;
                }

                if (SuppressedWrites.TryGetValue(fullPath, out DateTime recheckExpires) && DateTime.UtcNow <= recheckExpires)
                {
                    return;
                }

                FileExternallyChanged?.Invoke(fullPath);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (SyncRoot)
                {
                    if (PendingDebounces.TryGetValue(fullPath, out CancellationTokenSource? current)
                        && ReferenceEquals(current, nextCts))
                    {
                        PendingDebounces.Remove(fullPath);
                    }
                }

                nextCts.Dispose();
            }
        }, CancellationToken.None);
    }

    private void StopInternal()
    {
        if (WorldDirWatcher is not null)
        {
            WorldDirWatcher.EnableRaisingEvents = false;
            WorldDirWatcher.Changed -= OnWatcherChanged;
            WorldDirWatcher.Created -= OnWatcherChanged;
            WorldDirWatcher.Renamed -= OnWatcherRenamed;
            WorldDirWatcher.Dispose();
            WorldDirWatcher = null;
        }

        if (WorldInfoWatcher is not null)
        {
            WorldInfoWatcher.EnableRaisingEvents = false;
            WorldInfoWatcher.Changed -= OnWatcherChanged;
            WorldInfoWatcher.Created -= OnWatcherChanged;
            WorldInfoWatcher.Renamed -= OnWatcherRenamed;
            WorldInfoWatcher.Dispose();
            WorldInfoWatcher = null;
        }

        foreach (CancellationTokenSource cts in PendingDebounces.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        PendingDebounces.Clear();
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (Disposed)
            {
                return;
            }

            Disposed = true;
            StopInternal();
        }
    }
}
