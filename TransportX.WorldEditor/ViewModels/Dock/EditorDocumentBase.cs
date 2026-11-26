using System;
using Dock.Model.Mvvm.Controls;
using R3;

namespace TransportX.WorldEditor.ViewModels.Dock;

/// <summary>
/// Base class for dockable document tabs managed by <see cref="EditorDockFactory"/>
/// that own reactive <see cref="R3"/> subscriptions.
/// </summary>
public abstract class EditorDocumentBase : Document, IDisposable
{
    protected CompositeDisposable Disposables { get; } = new CompositeDisposable();

    protected void BindDirtyTitle(
        Services.WorldWorkspaceService workspace,
        string baseTitle,
        Func<Core.Models.WorldProject, Core.Commands.WorldFileTarget, bool> matchesTarget)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseTitle);
        ArgumentNullException.ThrowIfNull(matchesTarget);

        void Refresh()
        {
            Core.Models.WorldProject? project = workspace.CurrentProject.Value;
            bool isDirty = false;
            if (project is not null)
            {
                foreach (Core.Commands.WorldFileTarget dirty in project.DirtyFiles)
                {
                    if (matchesTarget(project, dirty))
                    {
                        isDirty = true;
                        break;
                    }
                }
            }

            string nextTitle = isDirty ? $"{baseTitle} *" : baseTitle;
            if (!string.Equals(Title, nextTitle, StringComparison.Ordinal))
            {
                Title = nextTitle;
            }
        }

        workspace.ProjectRevision.Subscribe(_ => Refresh()).AddTo(Disposables);
        Refresh();
    }

    public virtual void Dispose()
    {
        Disposables.Dispose();
    }
}
