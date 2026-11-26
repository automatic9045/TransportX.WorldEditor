using System;
using System.Collections.Generic;
using System.Linq;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace TransportX.WorldEditor.ViewModels.Dock;

/// <summary>
/// Constructs and manages the default 5-pane docking workspace layout for TransportX.WorldEditor.
/// </summary>
public sealed class EditorDockFactory : Factory
{
    private readonly ViewportDocumentViewModel ViewportDocument;
    private readonly SceneHierarchyToolViewModel SceneHierarchyTool;
    private readonly AssetBrowserToolViewModel AssetBrowserTool;
    private readonly InspectorToolViewModel InspectorTool;
    private readonly DiagnosticsToolViewModel DiagnosticsTool;
    private readonly TimelineToolViewModel TimelineTool;

    private IRootDock? ActiveRootDock;
    private ProportionalDock? MainLayoutDock;
    private ProportionalDock? LeftColumnDock;
    private ProportionalDock? CenterColumnDock;
    private ToolDock? LeftTopToolDock;
    private ToolDock? LeftBottomToolDock;
    private ToolDock? BottomToolDock;
    private ToolDock? RightToolDock;

    public DocumentDock? DocumentDock { get; private set; }
    public event Action? LayoutChanged;

    public EditorDockFactory(
        ViewportDocumentViewModel viewportDocument,
        SceneHierarchyToolViewModel sceneHierarchyTool,
        AssetBrowserToolViewModel assetBrowserTool,
        InspectorToolViewModel inspectorTool,
        DiagnosticsToolViewModel diagnosticsTool,
        TimelineToolViewModel? timelineTool = null)
    {
        ArgumentNullException.ThrowIfNull(viewportDocument);
        ArgumentNullException.ThrowIfNull(sceneHierarchyTool);
        ArgumentNullException.ThrowIfNull(assetBrowserTool);
        ArgumentNullException.ThrowIfNull(inspectorTool);
        ArgumentNullException.ThrowIfNull(diagnosticsTool);

        ViewportDocument = viewportDocument;
        SceneHierarchyTool = sceneHierarchyTool;
        AssetBrowserTool = assetBrowserTool;
        InspectorTool = inspectorTool;
        DiagnosticsTool = diagnosticsTool;
        TimelineTool = timelineTool ?? new TimelineToolViewModel();

        DockableAdded += (_, _) => LayoutChanged?.Invoke();
        DockableRemoved += (_, _) => LayoutChanged?.Invoke();
        DockableClosed += (_, _) => LayoutChanged?.Invoke();
        DockableHidden += (_, _) => LayoutChanged?.Invoke();
        DockableRestored += (_, _) => LayoutChanged?.Invoke();
    }

    public override IRootDock CreateLayout()
    {
        ViewportDocument.Id = "ViewportDocument";
        if (string.IsNullOrEmpty(ViewportDocument.Title))
        {
            ViewportDocument.Title = "3D ワールドビュー";
        }
        ViewportDocument.CanClose = false;

        SceneHierarchyTool.Id = "SceneHierarchy";
        SceneHierarchyTool.Title = "シーン階層";

        AssetBrowserTool.Id = "AssetBrowser";
        AssetBrowserTool.Title = "アセット / テンプレート";

        InspectorTool.Id = "Inspector";
        InspectorTool.Title = "プロパティ / 変更状態";

        TimelineTool.Id = "Timeline";
        TimelineTool.Title = "マルチトラック・タイムライン";

        DiagnosticsTool.Id = "Diagnostics";
        if (string.IsNullOrEmpty(DiagnosticsTool.Title))
        {
            DiagnosticsTool.Title = "診断 / エラー";
        }

        ToolDock leftTopDock = new ToolDock
        {
            Id = "LeftTopToolDock",
            Alignment = Alignment.Left,
            Proportion = 0.45,
            VisibleDockables = CreateList<IDockable>(SceneHierarchyTool),
            ActiveDockable = SceneHierarchyTool,
        };
        LeftTopToolDock = leftTopDock;

        ToolDock leftBottomDock = new ToolDock
        {
            Id = "LeftBottomToolDock",
            Alignment = Alignment.Left,
            Proportion = 0.55,
            VisibleDockables = CreateList<IDockable>(AssetBrowserTool),
            ActiveDockable = AssetBrowserTool,
        };
        LeftBottomToolDock = leftBottomDock;

        ProportionalDock leftColumn = new ProportionalDock
        {
            Id = "LeftColumnDock",
            Orientation = Orientation.Vertical,
            Proportion = 0.22,
            VisibleDockables = CreateList<IDockable>(
                leftTopDock,
                new ProportionalDockSplitter { CanResize = true },
                leftBottomDock),
        };
        LeftColumnDock = leftColumn;

        DocumentDock documentDock = new DocumentDock
        {
            Id = "DocumentDock",
            Proportion = 0.72,
            CanCreateDocument = false,
            VisibleDockables = CreateList<IDockable>(ViewportDocument),
            ActiveDockable = ViewportDocument,
        };
        DocumentDock = documentDock;

        ToolDock bottomDock = new ToolDock
        {
            Id = "BottomToolDock",
            Alignment = Alignment.Bottom,
            Proportion = 0.28,
            VisibleDockables = CreateList<IDockable>(DiagnosticsTool, TimelineTool),
            ActiveDockable = DiagnosticsTool,
        };
        BottomToolDock = bottomDock;

        ProportionalDock centerColumn = new ProportionalDock
        {
            Id = "CenterColumnDock",
            Orientation = Orientation.Vertical,
            Proportion = 0.56,
            VisibleDockables = CreateList<IDockable>(
                documentDock,
                new ProportionalDockSplitter { CanResize = true },
                bottomDock),
        };
        CenterColumnDock = centerColumn;

        ToolDock rightDock = new ToolDock
        {
            Id = "RightToolDock",
            Alignment = Alignment.Right,
            Proportion = 0.22,
            VisibleDockables = CreateList<IDockable>(InspectorTool),
            ActiveDockable = InspectorTool,
        };
        RightToolDock = rightDock;

        ProportionalDock mainLayout = new ProportionalDock
        {
            Id = "MainProportionalDock",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(
                leftColumn,
                new ProportionalDockSplitter { CanResize = true },
                centerColumn,
                new ProportionalDockSplitter { CanResize = true },
                rightDock),
        };
        MainLayoutDock = mainLayout;

        IRootDock rootDock = CreateRootDock();
        rootDock.Id = "RootDock";
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);
        rootDock.ActiveDockable = mainLayout;
        rootDock.DefaultDockable = mainLayout;
        ActiveRootDock = rootDock;

        InitLayout(rootDock);
        return rootDock;
    }

    public override void CloseDockable(IDockable dockable)
    {
        base.CloseDockable(dockable);
        LayoutChanged?.Invoke();
    }

    public override void AddDockable(IDock dock, IDockable dockable)
    {
        base.AddDockable(dock, dockable);
        LayoutChanged?.Invoke();
    }

    public bool IsDockableVisible(string dockableId)
    {
        if (ActiveRootDock is null || string.IsNullOrEmpty(dockableId))
        {
            return false;
        }

        return FindDockableById(ActiveRootDock, dockableId) is not null;
    }

    public bool ToggleToolPane(IDockable tool, string defaultToolDockId)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (ActiveRootDock is null)
        {
            return false;
        }

        if (IsDockableVisible(tool.Id))
        {
            CloseDockable(tool);
            return false;
        }

        if (ActiveRootDock.HiddenDockables is not null && ActiveRootDock.HiddenDockables.Contains(tool))
        {
            RestoreDockable(tool);
            if (IsDockableVisible(tool.Id))
            {
                if (tool.Owner is IDock restoredOwner)
                {
                    SetActiveDockable(tool);
                    SetFocusedDockable(restoredOwner, tool);
                }

                LayoutChanged?.Invoke();
                return true;
            }

            ActiveRootDock.HiddenDockables.Remove(tool);
        }

        RemoveFromPinnedCollections(ActiveRootDock, tool);

        ToolDock targetDock = EnsureToolDockAttached(defaultToolDockId);
        targetDock.VisibleDockables ??= CreateList<IDockable>();
        if (!targetDock.VisibleDockables.Contains(tool))
        {
            AddDockable(targetDock, tool);
        }

        SetActiveDockable(tool);
        SetFocusedDockable(targetDock, tool);
        LayoutChanged?.Invoke();
        return true;
    }

    public IRootDock ResetLayoutPreservingDocuments()
    {
        List<EditorDocumentBase> openDocs = new();
        string? activeDocId = null;
        if (DocumentDock?.VisibleDockables is not null)
        {
            activeDocId = (DocumentDock.ActiveDockable as EditorDocumentBase)?.Id;
            foreach (EditorDocumentBase doc in DocumentDock.VisibleDockables.OfType<EditorDocumentBase>())
            {
                if (!ReferenceEquals(doc, ViewportDocument))
                {
                    openDocs.Add(doc);
                }
            }
        }

        IRootDock newRoot = CreateLayout();
        if (DocumentDock is not null)
        {
            EditorDocumentBase? activeDoc = null;
            foreach (EditorDocumentBase doc in openDocs)
            {
                AddDockable(DocumentDock, doc);
                if (string.Equals(doc.Id, activeDocId, StringComparison.Ordinal))
                {
                    activeDoc = doc;
                }
            }

            if (activeDoc is not null)
            {
                SetActiveDockable(activeDoc);
                SetFocusedDockable(DocumentDock, activeDoc);
            }
        }

        LayoutChanged?.Invoke();
        return newRoot;
    }

    private ToolDock EnsureToolDockAttached(string defaultToolDockId)
    {
        ProportionalDock mainLayout = EnsureMainLayoutAttached();

        ToolDock targetDock = ResolveOrCreateToolDock(defaultToolDockId);
        if (ContainsDockable(ActiveRootDock!, targetDock))
        {
            return targetDock;
        }

        targetDock.VisibleDockables = CreateList<IDockable>();
        switch (defaultToolDockId)
        {
            case "LeftTopToolDock":
            {
                ProportionalDock leftCol = EnsureLeftColumnAttached(mainLayout);
                AttachChildWithSplitter(leftCol, targetDock, insertAtStart: true);
                break;
            }

            case "LeftBottomToolDock":
            {
                ProportionalDock leftCol = EnsureLeftColumnAttached(mainLayout);
                AttachChildWithSplitter(leftCol, targetDock, insertAtStart: false);
                break;
            }

            case "BottomToolDock":
            {
                ProportionalDock centerCol = EnsureCenterColumnAttached(mainLayout);
                AttachChildWithSplitter(centerCol, targetDock, insertAtStart: false);
                break;
            }

            default:
            {
                AttachChildWithSplitter(mainLayout, targetDock, insertAtStart: false);
                break;
            }
        }

        return targetDock;
    }

    private ProportionalDock EnsureMainLayoutAttached()
    {
        if (MainLayoutDock is null)
        {
            MainLayoutDock = new ProportionalDock
            {
                Id = "MainProportionalDock",
                Orientation = Orientation.Horizontal,
                VisibleDockables = CreateList<IDockable>(),
            };
        }

        if (ActiveRootDock is not null && !ContainsDockable(ActiveRootDock, MainLayoutDock))
        {
            ActiveRootDock.VisibleDockables ??= CreateList<IDockable>();
            AddDockable(ActiveRootDock, MainLayoutDock);
            ActiveRootDock.ActiveDockable = MainLayoutDock;
            ActiveRootDock.DefaultDockable = MainLayoutDock;
        }

        return MainLayoutDock;
    }

    private ProportionalDock EnsureLeftColumnAttached(ProportionalDock mainLayout)
    {
        if (LeftColumnDock is null)
        {
            LeftColumnDock = new ProportionalDock
            {
                Id = "LeftColumnDock",
                Orientation = Orientation.Vertical,
                Proportion = 0.22,
                VisibleDockables = CreateList<IDockable>(),
            };
        }

        if (!ContainsDockable(ActiveRootDock!, LeftColumnDock))
        {
            LeftColumnDock.VisibleDockables = CreateList<IDockable>();
            AttachChildWithSplitter(mainLayout, LeftColumnDock, insertAtStart: true);
        }

        return LeftColumnDock;
    }

    private ProportionalDock EnsureCenterColumnAttached(ProportionalDock mainLayout)
    {
        if (CenterColumnDock is not null && ContainsDockable(ActiveRootDock!, CenterColumnDock))
        {
            return CenterColumnDock;
        }

        if (DocumentDock?.Owner is ProportionalDock existingParent && existingParent.Orientation == Orientation.Vertical)
        {
            CenterColumnDock = existingParent;
            return CenterColumnDock;
        }

        CenterColumnDock ??= new ProportionalDock
        {
            Id = "CenterColumnDock",
            Orientation = Orientation.Vertical,
            Proportion = 0.56,
            VisibleDockables = CreateList<IDockable>(),
        };
        CenterColumnDock.VisibleDockables = CreateList<IDockable>();
        if (DocumentDock is not null)
        {
            AddDockable(CenterColumnDock, DocumentDock);
        }

        int insertIndex = LeftColumnDock is not null && ContainsDockable(mainLayout, LeftColumnDock) ? 2 : 0;
        if (mainLayout.VisibleDockables is not null && mainLayout.VisibleDockables.Count < insertIndex)
        {
            insertIndex = mainLayout.VisibleDockables.Count;
        }

        InsertChildWithSplitter(mainLayout, CenterColumnDock, insertIndex);
        return CenterColumnDock;
    }

    private ToolDock ResolveOrCreateToolDock(string defaultToolDockId)
    {
        return defaultToolDockId switch
        {
            "LeftTopToolDock" => LeftTopToolDock ??= new ToolDock
            {
                Id = "LeftTopToolDock",
                Alignment = Alignment.Left,
                Proportion = 0.45,
                VisibleDockables = CreateList<IDockable>(),
            },
            "LeftBottomToolDock" => LeftBottomToolDock ??= new ToolDock
            {
                Id = "LeftBottomToolDock",
                Alignment = Alignment.Left,
                Proportion = 0.55,
                VisibleDockables = CreateList<IDockable>(),
            },
            "BottomToolDock" => BottomToolDock ??= new ToolDock
            {
                Id = "BottomToolDock",
                Alignment = Alignment.Bottom,
                Proportion = 0.28,
                VisibleDockables = CreateList<IDockable>(),
            },
            _ => RightToolDock ??= new ToolDock
            {
                Id = "RightToolDock",
                Alignment = Alignment.Right,
                Proportion = 0.22,
                VisibleDockables = CreateList<IDockable>(),
            },
        };
    }

    private void AttachChildWithSplitter(ProportionalDock parent, IDockable child, bool insertAtStart)
    {
        parent.VisibleDockables ??= CreateList<IDockable>();
        if (parent.VisibleDockables.Count == 0)
        {
            AddDockable(parent, child);
            return;
        }

        if (insertAtStart)
        {
            ProportionalDockSplitter splitter = new ProportionalDockSplitter { CanResize = true };
            InsertDockable(parent, splitter, 0);
            InsertDockable(parent, child, 0);
        }
        else
        {
            AddDockable(parent, new ProportionalDockSplitter { CanResize = true });
            AddDockable(parent, child);
        }
    }

    private void InsertChildWithSplitter(ProportionalDock parent, IDockable child, int index)
    {
        parent.VisibleDockables ??= CreateList<IDockable>();
        if (parent.VisibleDockables.Count == 0)
        {
            AddDockable(parent, child);
            return;
        }

        int clamped = Math.Clamp(index, 0, parent.VisibleDockables.Count);
        InsertDockable(parent, child, clamped);
        if (0 < clamped)
        {
            InsertDockable(parent, new ProportionalDockSplitter { CanResize = true }, clamped);
        }
        else if (1 < parent.VisibleDockables.Count)
        {
            InsertDockable(parent, new ProportionalDockSplitter { CanResize = true }, 1);
        }
    }

    private static IDockable? FindDockableById(IDockable root, string dockableId)
    {
        if (string.Equals(root.Id, dockableId, StringComparison.Ordinal))
        {
            return root;
        }

        if (root is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (IDockable child in dock.VisibleDockables)
            {
                IDockable? found = FindDockableById(child, dockableId);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static bool ContainsDockable(IDockable root, IDockable target)
    {
        if (ReferenceEquals(root, target))
        {
            return true;
        }

        if (root is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (IDockable child in dock.VisibleDockables)
            {
                if (ContainsDockable(child, target))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void RemoveFromPinnedCollections(IRootDock root, IDockable tool)
    {
        root.LeftPinnedDockables?.Remove(tool);
        root.RightPinnedDockables?.Remove(tool);
        root.TopPinnedDockables?.Remove(tool);
        root.BottomPinnedDockables?.Remove(tool);
    }

    public TDocument OpenOrActivateDocument<TDocument>(string documentId, Func<TDocument> factory)
        where TDocument : EditorDocumentBase
    {
        ArgumentNullException.ThrowIfNull(documentId);
        ArgumentNullException.ThrowIfNull(factory);

        if (DocumentDock is null)
        {
            return factory();
        }

        IList<IDockable>? visible = DocumentDock.VisibleDockables;
        if (visible is not null)
        {
            TDocument? existing = visible.OfType<TDocument>().FirstOrDefault(d => string.Equals(d.Id, documentId, StringComparison.Ordinal));
            if (existing is not null)
            {
                SetActiveDockable(existing);
                SetFocusedDockable(DocumentDock, existing);
                return existing;
            }
        }

        TDocument created = factory();
        AddDockable(DocumentDock, created);
        SetActiveDockable(created);
        SetFocusedDockable(DocumentDock, created);
        return created;
    }

    public void ActivateViewport()
    {
        if (DocumentDock is null)
        {
            return;
        }

        SetActiveDockable(ViewportDocument);
        SetFocusedDockable(DocumentDock, ViewportDocument);
    }

    public void CloseDocumentById(string documentId)
    {
        if (DocumentDock?.VisibleDockables is null || string.IsNullOrEmpty(documentId))
        {
            return;
        }

        EditorDocumentBase? doc = DocumentDock.VisibleDockables
            .OfType<EditorDocumentBase>()
            .FirstOrDefault(d => !ReferenceEquals(d, ViewportDocument) && string.Equals(d.Id, documentId, StringComparison.Ordinal));
        if (doc is not null)
        {
            CloseDockable(doc);
            doc.Dispose();
        }
    }

    public void CloseDynamicDocuments()
    {
        if (DocumentDock?.VisibleDockables is null)
        {
            return;
        }

        List<EditorDocumentBase> dynamicDocs = DocumentDock.VisibleDockables
            .OfType<EditorDocumentBase>()
            .Where(d => !ReferenceEquals(d, ViewportDocument))
            .ToList();
        foreach (EditorDocumentBase doc in dynamicDocs)
        {
            CloseDockable(doc);
            doc.Dispose();
        }
    }
}
