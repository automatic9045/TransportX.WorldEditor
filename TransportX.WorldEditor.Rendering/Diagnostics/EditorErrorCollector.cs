using System;
using System.Collections.Generic;
using System.Threading;
using TransportX.Diagnostics;

namespace TransportX.WorldEditor.Rendering.Diagnostics;

/// <summary>
/// Thread-safe <see cref="IErrorCollector"/> implementation that routes TransportX diagnostics
/// to the editor UI without displaying blocking modal dialogs.
/// </summary>
public sealed class EditorErrorCollector : IErrorCollector
{
    private readonly Lock SyncRoot = new Lock();
    private readonly List<Error> ErrorsList = new List<Error>();

    public IReadOnlyList<Error> Errors
    {
        get
        {
            lock (SyncRoot)
            {
                return ErrorsList.ToArray();
            }
        }
    }

    public bool HasFatalError
    {
        get
        {
            lock (SyncRoot)
            {
                for (int i = 0; i < ErrorsList.Count; i++)
                {
                    if (ErrorsList[i].Level == ErrorLevel.Fatal)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    public event EventHandler<ErrorEventArgs>? Reported;

    public void Report(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        lock (SyncRoot)
        {
            ErrorsList.Add(error);
        }

        Reported?.Invoke(this, new ErrorEventArgs(error));
    }

    public void Clear()
    {
        lock (SyncRoot)
        {
            ErrorsList.Clear();
        }
    }
}
