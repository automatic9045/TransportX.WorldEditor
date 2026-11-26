using System;
using R3;

namespace TransportX.WorldEditor.ViewModels;

/// <summary>
/// Base class for disposable R3-backed ViewModels.
/// </summary>
public abstract class ViewModelBase : IDisposable
{
    protected CompositeDisposable Disposables { get; } = new CompositeDisposable();

    public virtual void Dispose()
    {
        Disposables.Dispose();
    }
}
