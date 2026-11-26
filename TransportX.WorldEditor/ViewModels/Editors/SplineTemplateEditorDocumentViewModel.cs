using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using R3;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels.Controls;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.ViewModels.Editors;

/// <summary>
/// Row wrapper for a <see cref="SplineTemplateExtrusionRule"/>.
/// </summary>
public sealed class SplineTemplateExtrusionRow : INotifyPropertyChanged
{
    private int IndexValue;
    private SplineTemplateExtrusionRule ModelValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index => IndexValue;
    public SplineTemplateExtrusionRule Model => ModelValue;
    public string Summary =>
        $"[{(string.IsNullOrEmpty(ModelValue.Key) ? "-" : ModelValue.Key)}] {ModelValue.CrossSectionKey} (From={ModelValue.From:0.##}m Off=({ModelValue.OffsetX:0.##}, {ModelValue.OffsetY:0.##}))";

    public SplineTemplateExtrusionRow(int index, SplineTemplateExtrusionRule model)
    {
        IndexValue = index;
        ModelValue = model;
    }

    public void Update(int index, SplineTemplateExtrusionRule model)
    {
        IndexValue = index;
        ModelValue = model;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>
/// Row wrapper for a <see cref="SplineTemplatePropRule"/>.
/// </summary>
public sealed class SplineTemplatePropRow : INotifyPropertyChanged
{
    private int IndexValue;
    private SplineTemplatePropRule ModelValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index => IndexValue;
    public SplineTemplatePropRule Model => ModelValue;

    public string Summary
    {
        get
        {
            string keyLabel = string.IsNullOrEmpty(ModelValue.Key) ? "-" : ModelValue.Key;
            string modelsLabel = 0 < ModelValue.ModelKeys.Count ? string.Join(", ", ModelValue.ModelKeys) : "(モデル未指定)";
            return $"[{keyLabel}] {modelsLabel} (From={ModelValue.From:0.##}m Span={ModelValue.Span:0.##}m Int={ModelValue.Interval:0.##}m)";
        }
    }

    public SplineTemplatePropRow(int index, SplineTemplatePropRule model)
    {
        IndexValue = index;
        ModelValue = model;
    }

    public void Update(int index, SplineTemplatePropRule model)
    {
        IndexValue = index;
        ModelValue = model;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>
/// Document ViewModel for editing a <see cref="SplineTemplateDocument"/>
/// (<c>Templates/Splines/&lt;Key&gt;.csx</c>) with full Undo/Redo support.
/// </summary>
public sealed class SplineTemplateEditorDocumentViewModel : EditorDocumentBase
{
    private readonly WorldWorkspaceService Workspace;
    private bool IsSyncing;
    private bool IsCommitting;

    public string TemplateKey { get; }

    public BindableReactiveProperty<string> RelativePath { get; }
    public BindableReactiveProperty<bool> IsProtected { get; }
    public BindableReactiveProperty<bool> IsEditable { get; }
    public BindableReactiveProperty<bool> HasWarningBanner { get; }
    public BindableReactiveProperty<string> WarningBannerText { get; }
    public BindableReactiveProperty<string> OutletLayoutKey { get; }
    public BindableReactiveProperty<string> UserCode { get; }
    public BindableReactiveProperty<int> PreviewVersion { get; }
    public BindableReactiveProperty<bool> Show3DNetworkOverlay { get; }
    public BindableReactiveProperty<bool> UseCurvedPreview { get; }

    public event Action? Reset3DCameraRequested;

    public ObservableCollection<string> AvailableLaneLayoutKeys { get; } = new();
    public ObservableCollection<string> AvailableCrossSectionKeys { get; } = new();
    public ObservableCollection<string> AvailableModelKeys { get; } = new();

    public ObservableCollection<SplineTemplateExtrusionRow> Extrusions { get; } = new();
    public ObservableCollection<SplineTemplatePropRow> Props { get; } = new();

    public BindableReactiveProperty<SplineTemplateExtrusionRow?> SelectedExtrusion { get; }
    public BindableReactiveProperty<bool> HasSelectedExtrusion { get; }
    public BindableReactiveProperty<string> ExtKey { get; }
    public BindableReactiveProperty<string> ExtCrossSectionKey { get; }
    public BindableReactiveProperty<decimal?> ExtOffsetX { get; }
    public BindableReactiveProperty<decimal?> ExtOffsetY { get; }
    public BindableReactiveProperty<decimal?> ExtBaseInterval { get; }
    public BindableReactiveProperty<decimal?> ExtMaxDeviation { get; }
    public BindableReactiveProperty<decimal?> ExtMaxAngleDeltaDeg { get; }
    public BindableReactiveProperty<decimal?> ExtVIntervalScale { get; }
    public BindableReactiveProperty<decimal?> ExtVOffset { get; }
    public BindableReactiveProperty<decimal?> ExtFrom { get; }

    public BindableReactiveProperty<SplineTemplatePropRow?> SelectedProp { get; }
    public BindableReactiveProperty<bool> HasSelectedProp { get; }
    public BindableReactiveProperty<string> PropKey { get; }
    public BindableReactiveProperty<string> PropModelKeysText { get; }
    public BindableReactiveProperty<string> ModelKeyToAdd { get; }
    public PropModelKeysEditorViewModel PropModelsEditor { get; }
    public BindableReactiveProperty<decimal?> PropPosX { get; }
    public BindableReactiveProperty<decimal?> PropPosY { get; }
    public BindableReactiveProperty<decimal?> PropPosZ { get; }
    public BindableReactiveProperty<decimal?> PropRotX { get; }
    public BindableReactiveProperty<decimal?> PropRotY { get; }
    public BindableReactiveProperty<decimal?> PropRotZ { get; }
    public BindableReactiveProperty<decimal?> PropFrom { get; }
    public BindableReactiveProperty<decimal?> PropSpan { get; }
    public BindableReactiveProperty<decimal?> PropInterval { get; }

    public ReactiveCommand<Unit> AddExtrusionCommand { get; }
    public ReactiveCommand<Unit> RemoveExtrusionCommand { get; }
    public ReactiveCommand<Unit> MoveExtrusionUpCommand { get; }
    public ReactiveCommand<Unit> MoveExtrusionDownCommand { get; }

    public ReactiveCommand<Unit> AddPropCommand { get; }
    public ReactiveCommand<Unit> RemovePropCommand { get; }
    public ReactiveCommand<Unit> MovePropUpCommand { get; }
    public ReactiveCommand<Unit> MovePropDownCommand { get; }
    public ReactiveCommand<Unit> AddModelKeyToPropCommand { get; }
    public ReactiveCommand<Unit> Reset3DPreviewCameraCommand { get; }
    public ReactiveCommand<Unit> SetStraightPreviewCommand { get; }
    public ReactiveCommand<Unit> SetCurvedPreviewCommand { get; }

    public WorldProject? GetProject() => Workspace.CurrentProject.Value;

    public SplineTemplateEditorDocumentViewModel(WorldWorkspaceService workspace, string templateKey)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(templateKey);

        Workspace = workspace;
        TemplateKey = templateKey;
        Id = $"Doc_SplineTemplate_{templateKey}";
        Title = $"SplineTpl: {templateKey}";

        RelativePath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        IsProtected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsEditable = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        HasWarningBanner = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        WarningBannerText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        OutletLayoutKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        UserCode = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PreviewVersion = new BindableReactiveProperty<int>(0).AddTo(Disposables);
        Show3DNetworkOverlay = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        UseCurvedPreview = new BindableReactiveProperty<bool>(false).AddTo(Disposables);

        SelectedExtrusion = new BindableReactiveProperty<SplineTemplateExtrusionRow?>(null).AddTo(Disposables);
        HasSelectedExtrusion = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ExtKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        ExtCrossSectionKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        ExtOffsetX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        ExtOffsetY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        ExtBaseInterval = new BindableReactiveProperty<decimal?>(5m).AddTo(Disposables);
        ExtMaxDeviation = new BindableReactiveProperty<decimal?>(0.01m).AddTo(Disposables);
        ExtMaxAngleDeltaDeg = new BindableReactiveProperty<decimal?>(2m).AddTo(Disposables);
        ExtVIntervalScale = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);
        ExtVOffset = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        ExtFrom = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        SelectedProp = new BindableReactiveProperty<SplineTemplatePropRow?>(null).AddTo(Disposables);
        HasSelectedProp = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        PropKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PropModelKeysText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        ModelKeyToAdd = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PropModelsEditor = new PropModelKeysEditorViewModel(AvailableModelKeys, PropModelKeysText, ModelKeyToAdd).AddTo(Disposables);
        PropPosX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropPosY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropPosZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropFrom = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropSpan = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropInterval = new BindableReactiveProperty<decimal?>(20m).AddTo(Disposables);

        AddExtrusionCommand = new ReactiveCommand<Unit>(_ => AddExtrusion()).AddTo(Disposables);
        RemoveExtrusionCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedExtrusion()).AddTo(Disposables);
        MoveExtrusionUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedExtrusion(-1)).AddTo(Disposables);
        MoveExtrusionDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedExtrusion(1)).AddTo(Disposables);

        AddPropCommand = new ReactiveCommand<Unit>(_ => AddProp()).AddTo(Disposables);
        RemovePropCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedProp()).AddTo(Disposables);
        MovePropUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedProp(-1)).AddTo(Disposables);
        MovePropDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedProp(1)).AddTo(Disposables);
        AddModelKeyToPropCommand = new ReactiveCommand<Unit>(_ => AppendModelKeyToProp()).AddTo(Disposables);
        Reset3DPreviewCameraCommand = new ReactiveCommand<Unit>(_ => Reset3DCameraRequested?.Invoke()).AddTo(Disposables);
        SetStraightPreviewCommand = new ReactiveCommand<Unit>(_ => UseCurvedPreview.Value = false).AddTo(Disposables);
        SetCurvedPreviewCommand = new ReactiveCommand<Unit>(_ => UseCurvedPreview.Value = true).AddTo(Disposables);

        OutletLayoutKey.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncing)
            {
                CommitOutletLayoutKey();
            }
        }).AddTo(Disposables);

        UserCode.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncing)
            {
                CommitUserCode();
            }
        }).AddTo(Disposables);

        SelectedExtrusion.Subscribe(_ => SyncExtrusionInspector()).AddTo(Disposables);
        HookExtrusionCommit(ExtKey);
        HookExtrusionCommit(ExtCrossSectionKey);
        HookExtrusionCommit(ExtOffsetX);
        HookExtrusionCommit(ExtOffsetY);
        HookExtrusionCommit(ExtBaseInterval);
        HookExtrusionCommit(ExtMaxDeviation);
        HookExtrusionCommit(ExtMaxAngleDeltaDeg);
        HookExtrusionCommit(ExtVIntervalScale);
        HookExtrusionCommit(ExtVOffset);
        HookExtrusionCommit(ExtFrom);

        SelectedProp.Subscribe(_ => SyncPropInspector()).AddTo(Disposables);
        HookPropCommit(PropKey);
        HookPropCommit(PropModelKeysText);
        HookPropCommit(PropPosX);
        HookPropCommit(PropPosY);
        HookPropCommit(PropPosZ);
        HookPropCommit(PropRotX);
        HookPropCommit(PropRotY);
        HookPropCommit(PropRotZ);
        HookPropCommit(PropFrom);
        HookPropCommit(PropSpan);
        HookPropCommit(PropInterval);

        Workspace.ProjectRevision.Subscribe(_ => SyncFromModel()).AddTo(Disposables);
        SyncFromModel();
        BindDirtyTitle(
            Workspace,
            $"SplineTpl: {TemplateKey}",
            (project, target) =>
            {
                if (target.Kind != Core.Commands.WorldFileKind.SplineTemplateCsx)
                {
                    return false;
                }

                return project.SplineTemplates.TryGetValue(TemplateKey, out Core.Models.Templates.SplineTemplateDocument? doc)
                    && string.Equals(
                        WorldProject.NormalizeRelativePath(target.RelativePath),
                        WorldProject.NormalizeRelativePath(doc.RelativePath),
                        StringComparison.OrdinalIgnoreCase);
            });
    }

    private void HookExtrusionCommit<T>(BindableReactiveProperty<T> prop)
    {
        prop.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncing)
            {
                CommitSelectedExtrusion();
            }
        }).AddTo(Disposables);
    }

    private void HookPropCommit<T>(BindableReactiveProperty<T> prop)
    {
        prop.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncing)
            {
                CommitSelectedProp();
            }
        }).AddTo(Disposables);
    }

    private SplineTemplateDocument? ResolveDocument(out WorldProject? project)
    {
        project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return null;
        }

        return project.SplineTemplates.TryGetValue(TemplateKey, out SplineTemplateDocument? doc) ? doc : null;
    }

    private static void SyncStringCollection(ObservableCollection<string> target, List<string> next)
    {
        if (target.SequenceEqual(next, StringComparer.Ordinal))
        {
            return;
        }

        target.Clear();
        foreach (string item in next)
        {
            target.Add(item);
        }
    }

    private void SyncFromModel()
    {
        IsSyncing = true;
        try
        {
            int prevExtIdx = SelectedExtrusion.Value?.Index ?? -1;
            int prevPropIdx = SelectedProp.Value?.Index ?? -1;

            SplineTemplateDocument? doc = ResolveDocument(out WorldProject? project);
            if (project is null || doc is null)
            {
                AvailableLaneLayoutKeys.Clear();
                AvailableCrossSectionKeys.Clear();
                AvailableModelKeys.Clear();
                Extrusions.Clear();
                Props.Clear();
                RelativePath.Value = string.Empty;
                IsProtected.Value = false;
                IsEditable.Value = false;
                HasWarningBanner.Value = true;
                WarningBannerText.Value = $"スプラインテンプレート '{TemplateKey}' は現在のワールドに存在しません。";
                OutletLayoutKey.Value = string.Empty;
                UserCode.Value = string.Empty;
                SelectedExtrusion.Value = null;
                SelectedProp.Value = null;
                SyncExtrusionInspector();
                SyncPropInspector();
                return;
            }

            List<string> nextLaneKeys = project.LaneLayouts.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).ToList();
            if (!string.IsNullOrEmpty(doc.OutletLayoutKey) && !nextLaneKeys.Contains(doc.OutletLayoutKey))
            {
                nextLaneKeys.Add(doc.OutletLayoutKey);
            }

            List<string> nextCsKeys = project.CrossSections.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < doc.Extrusions.Count; i++)
            {
                string extCs = doc.Extrusions[i].CrossSectionKey;
                if (!string.IsNullOrEmpty(extCs) && !nextCsKeys.Contains(extCs))
                {
                    nextCsKeys.Add(extCs);
                }
            }

            List<string> nextModelKeys = new();
            foreach (ModelListDocument list in project.ModelLists.Values)
            {
                foreach (ModelListEntry entry in list.Entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Key) && !nextModelKeys.Contains(entry.Key))
                    {
                        nextModelKeys.Add(entry.Key);
                    }
                }
            }

            SyncStringCollection(AvailableLaneLayoutKeys, nextLaneKeys);
            SyncStringCollection(AvailableCrossSectionKeys, nextCsKeys);
            SyncStringCollection(AvailableModelKeys, nextModelKeys);

            if (string.IsNullOrEmpty(ModelKeyToAdd.Value) && 0 < AvailableModelKeys.Count)
            {
                ModelKeyToAdd.Value = AvailableModelKeys[0];
            }

            bool isProtected = project.IsProtected(doc.RelativePath);
            RelativePath.Value = doc.RelativePath;
            IsProtected.Value = isProtected;
            IsEditable.Value = !isProtected;
            if (!IsCommitting)
            {
                OutletLayoutKey.Value = doc.OutletLayoutKey;
                UserCode.Value = doc.UserCode ?? string.Empty;
            }

            List<string> warnings = new();
            if (isProtected)
            {
                warnings.Add($"ファイル '{doc.RelativePath}' は上書き保護されているため編集できません。");
            }

            if (string.IsNullOrEmpty(doc.OutletLayoutKey))
            {
                warnings.Add("OutletLayoutKey が未設定です (進路レイアウトを選択してください)。");
            }
            else if (!project.LaneLayouts.ContainsKey(doc.OutletLayoutKey))
            {
                warnings.Add($"OutletLayout '{doc.OutletLayoutKey}' が Init.csx に存在しません。");
            }

            if (Extrusions.Count == doc.Extrusions.Count)
            {
                for (int i = 0; i < doc.Extrusions.Count; i++)
                {
                    Extrusions[i].Update(i, doc.Extrusions[i]);
                }
            }
            else
            {
                Extrusions.Clear();
                for (int i = 0; i < doc.Extrusions.Count; i++)
                {
                    Extrusions.Add(new SplineTemplateExtrusionRow(i, doc.Extrusions[i]));
                }

                SelectedExtrusion.Value = 0 < Extrusions.Count
                    ? Extrusions[0 <= prevExtIdx ? int.Clamp(prevExtIdx, 0, Extrusions.Count - 1) : 0]
                    : null;
            }

            if (Props.Count == doc.Props.Count)
            {
                for (int i = 0; i < doc.Props.Count; i++)
                {
                    Props[i].Update(i, doc.Props[i]);
                }
            }
            else
            {
                Props.Clear();
                for (int i = 0; i < doc.Props.Count; i++)
                {
                    Props.Add(new SplineTemplatePropRow(i, doc.Props[i]));
                }

                SelectedProp.Value = 0 < Props.Count
                    ? Props[0 <= prevPropIdx ? int.Clamp(prevPropIdx, 0, Props.Count - 1) : 0]
                    : null;
            }

            HasWarningBanner.Value = 0 < warnings.Count;
            WarningBannerText.Value = string.Join(" / ", warnings);

            if (!IsCommitting)
            {
                SyncExtrusionInspector();
                SyncPropInspector();
            }
        }
        finally
        {
            IsSyncing = false;
            PreviewVersion.Value++;
        }
    }

    private void SyncExtrusionInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            SplineTemplateExtrusionRow? row = SelectedExtrusion.Value;
            HasSelectedExtrusion.Value = row is not null;
            if (row is null)
            {
                ExtKey.Value = string.Empty;
                ExtCrossSectionKey.Value = string.Empty;
                ExtOffsetX.Value = 0m;
                ExtOffsetY.Value = 0m;
                ExtBaseInterval.Value = 5m;
                ExtMaxDeviation.Value = 0.01m;
                ExtMaxAngleDeltaDeg.Value = 2m;
                ExtVIntervalScale.Value = 1m;
                ExtVOffset.Value = 0m;
                ExtFrom.Value = 0m;
                return;
            }

            SplineTemplateExtrusionRule m = row.Model;
            ExtKey.Value = m.Key ?? string.Empty;
            ExtCrossSectionKey.Value = m.CrossSectionKey;
            ExtOffsetX.Value = (decimal)m.OffsetX;
            ExtOffsetY.Value = (decimal)m.OffsetY;
            ExtBaseInterval.Value = (decimal)m.BaseInterval;
            ExtMaxDeviation.Value = (decimal)m.MaxDeviation;
            ExtMaxAngleDeltaDeg.Value = (decimal)m.MaxAngleDeltaDeg;
            ExtVIntervalScale.Value = (decimal)m.VIntervalScale;
            ExtVOffset.Value = (decimal)m.VOffset;
            ExtFrom.Value = (decimal)m.From;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void SyncPropInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            SplineTemplatePropRow? row = SelectedProp.Value;
            HasSelectedProp.Value = row is not null;
            if (row is null)
            {
                PropKey.Value = string.Empty;
                PropModelKeysText.Value = string.Empty;
                PropPosX.Value = 0m;
                PropPosY.Value = 0m;
                PropPosZ.Value = 0m;
                PropRotX.Value = 0m;
                PropRotY.Value = 0m;
                PropRotZ.Value = 0m;
                PropFrom.Value = 0m;
                PropSpan.Value = 0m;
                PropInterval.Value = 20m;
                return;
            }

            SplineTemplatePropRule m = row.Model;
            PropKey.Value = m.Key ?? string.Empty;
            PropModelKeysText.Value = PropModelKeysTextHelper.Format(m.ModelKeys);
            PropPosX.Value = (decimal)m.Pose.X;
            PropPosY.Value = (decimal)m.Pose.Y;
            PropPosZ.Value = (decimal)m.Pose.Z;
            PropRotX.Value = (decimal)m.Pose.RotationX;
            PropRotY.Value = (decimal)m.Pose.RotationY;
            PropRotZ.Value = (decimal)m.Pose.RotationZ;
            PropFrom.Value = (decimal)m.From;
            PropSpan.Value = (decimal)m.Span;
            PropInterval.Value = (decimal)m.Interval;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void CommitOutletLayoutKey()
    {
        string layoutKey = OutletLayoutKey.Value.Trim();
        IsCommitting = true;
        try
        {
            Workspace.MutateSplineTemplate(
                TemplateKey,
                $"SplineTemplate '{TemplateKey}' の OutletLayout を '{layoutKey}' に変更",
                draft => draft.OutletLayoutKey = layoutKey);
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void CommitUserCode()
    {
        string text = UserCode.Value;
        IsCommitting = true;
        try
        {
            Workspace.MutateSplineTemplate(
                TemplateKey,
                $"SplineTemplate '{TemplateKey}' のユーザーコードを編集",
                draft => draft.UserCode = string.IsNullOrWhiteSpace(text) ? null : text,
                $"SplineTemplate.{TemplateKey}.UserCode");
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void AddExtrusion()
    {
        SplineTemplateDocument? doc = ResolveDocument(out WorldProject? _);
        if (doc is null)
        {
            return;
        }

        int newIndex = doc.Extrusions.Count;
        string key = GenerateUniqueRuleKey(doc.Extrusions.Select(static e => e.Key), "Ext");
        string csKey = AvailableCrossSectionKeys.FirstOrDefault() ?? string.Empty;

        SplineTemplateExtrusionRule rule = new()
        {
            Key = key,
            CrossSectionKey = csKey,
        };

        if (Workspace.MutateSplineTemplate(
            TemplateKey,
            $"SplineTemplate '{TemplateKey}' に押し出し '{key}' を追加",
            draft => draft.Extrusions.Add(rule)))
        {
            if (0 <= newIndex && newIndex < Extrusions.Count)
            {
                SelectedExtrusion.Value = Extrusions[newIndex];
            }
        }
    }

    private void RemoveSelectedExtrusion()
    {
        SplineTemplateExtrusionRow? row = SelectedExtrusion.Value;
        if (row is null)
        {
            return;
        }

        int index = row.Index;
        Workspace.MutateSplineTemplate(
            TemplateKey,
            $"SplineTemplate '{TemplateKey}' から押し出し #{index} を削除",
            draft =>
            {
                if (0 <= index && index < draft.Extrusions.Count)
                {
                    draft.Extrusions.RemoveAt(index);
                }
            });
    }

    private void MoveSelectedExtrusion(int delta)
    {
        SplineTemplateExtrusionRow? row = SelectedExtrusion.Value;
        if (row is null)
        {
            return;
        }

        int index = row.Index;
        int target = index + delta;
        if (target < 0 || Extrusions.Count <= target)
        {
            return;
        }

        if (Workspace.MutateSplineTemplate(
            TemplateKey,
            $"SplineTemplate '{TemplateKey}' の押し出し順序を変更",
            draft =>
            {
                if (0 <= index && index < draft.Extrusions.Count && 0 <= target && target < draft.Extrusions.Count)
                {
                    SplineTemplateExtrusionRule item = draft.Extrusions[index];
                    draft.Extrusions.RemoveAt(index);
                    draft.Extrusions.Insert(target, item);
                }
            }))
        {
            if (0 <= target && target < Extrusions.Count)
            {
                SelectedExtrusion.Value = Extrusions[target];
            }
        }
    }

    private void CommitSelectedExtrusion()
    {
        SplineTemplateExtrusionRow? row = SelectedExtrusion.Value;
        if (row is null)
        {
            return;
        }

        int index = row.Index;
        SplineTemplateExtrusionRule updated = new()
        {
            Key = string.IsNullOrWhiteSpace(ExtKey.Value) ? null : ExtKey.Value.Trim(),
            CrossSectionKey = ExtCrossSectionKey.Value.Trim(),
            OffsetX = (double)(ExtOffsetX.Value ?? 0m),
            OffsetY = (double)(ExtOffsetY.Value ?? 0m),
            BaseInterval = double.Max(0.1, (double)(ExtBaseInterval.Value ?? 5m)),
            MaxDeviation = double.Max(0.0001, (double)(ExtMaxDeviation.Value ?? 0.01m)),
            MaxAngleDeltaDeg = double.Max(0.1, (double)(ExtMaxAngleDeltaDeg.Value ?? 2m)),
            VIntervalScale = double.Max(0.01, (double)(ExtVIntervalScale.Value ?? 1m)),
            VOffset = (double)(ExtVOffset.Value ?? 0m),
            From = double.Max(0.0, (double)(ExtFrom.Value ?? 0m)),
        };

        IsCommitting = true;
        try
        {
            Workspace.MutateSplineTemplate(
                TemplateKey,
                $"SplineTemplate '{TemplateKey}' の押し出し #{index} を変更",
                draft =>
                {
                    if (0 <= index && index < draft.Extrusions.Count)
                    {
                        draft.Extrusions[index] = updated;
                    }
                },
                $"SplineTemplate.{TemplateKey}.Ext.{index}");
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void AddProp()
    {
        SplineTemplateDocument? doc = ResolveDocument(out WorldProject? _);
        if (doc is null)
        {
            return;
        }

        int newIndex = doc.Props.Count;
        string key = GenerateUniqueRuleKey(doc.Props.Select(static p => p.Key), "Prop");
        SplineTemplatePropRule rule = new()
        {
            Key = key,
            From = 0.0,
            Span = 0.0,
            Interval = 20.0,
        };
        if (0 < AvailableModelKeys.Count)
        {
            rule.ModelKeys.Add(AvailableModelKeys[0]);
        }

        if (Workspace.MutateSplineTemplate(
            TemplateKey,
            $"SplineTemplate '{TemplateKey}' にプロップ '{key}' を追加",
            draft => draft.Props.Add(rule)))
        {
            if (0 <= newIndex && newIndex < Props.Count)
            {
                SelectedProp.Value = Props[newIndex];
            }
        }
    }

    private void RemoveSelectedProp()
    {
        SplineTemplatePropRow? row = SelectedProp.Value;
        if (row is null)
        {
            return;
        }

        int index = row.Index;
        Workspace.MutateSplineTemplate(
            TemplateKey,
            $"SplineTemplate '{TemplateKey}' からプロップ #{index} を削除",
            draft =>
            {
                if (0 <= index && index < draft.Props.Count)
                {
                    draft.Props.RemoveAt(index);
                }
            });
    }

    private void MoveSelectedProp(int delta)
    {
        SplineTemplatePropRow? row = SelectedProp.Value;
        if (row is null)
        {
            return;
        }

        int index = row.Index;
        int target = index + delta;
        if (target < 0 || Props.Count <= target)
        {
            return;
        }

        if (Workspace.MutateSplineTemplate(
            TemplateKey,
            $"SplineTemplate '{TemplateKey}' のプロップ順序を変更",
            draft =>
            {
                if (0 <= index && index < draft.Props.Count && 0 <= target && target < draft.Props.Count)
                {
                    SplineTemplatePropRule item = draft.Props[index];
                    draft.Props.RemoveAt(index);
                    draft.Props.Insert(target, item);
                }
            }))
        {
            if (0 <= target && target < Props.Count)
            {
                SelectedProp.Value = Props[target];
            }
        }
    }

    private void AppendModelKeyToProp()
    {
        string toAdd = ModelKeyToAdd.Value.Trim();
        if (string.IsNullOrEmpty(toAdd) || SelectedProp.Value is null)
        {
            return;
        }

        PropModelKeysText.Value = PropModelKeysTextHelper.Append(PropModelKeysText.Value, toAdd);
    }

    private void CommitSelectedProp()
    {
        SplineTemplatePropRow? row = SelectedProp.Value;
        if (row is null)
        {
            return;
        }

        int index = row.Index;
        SplineTemplatePropRule updated = new()
        {
            Key = string.IsNullOrWhiteSpace(PropKey.Value) ? null : PropKey.Value.Trim(),
            Pose = new PoseDegreesModel(
                (double)(PropPosX.Value ?? 0m),
                (double)(PropPosY.Value ?? 0m),
                (double)(PropPosZ.Value ?? 0m),
                (double)(PropRotX.Value ?? 0m),
                (double)(PropRotY.Value ?? 0m),
                (double)(PropRotZ.Value ?? 0m)),
            From = double.Max(0.0, (double)(PropFrom.Value ?? 0m)),
            Span = double.Max(0.0, (double)(PropSpan.Value ?? 0m)),
            Interval = double.Max(0.0, (double)(PropInterval.Value ?? 0m)),
        };

        updated.ModelKeys.AddRange(PropModelKeysTextHelper.Parse(PropModelKeysText.Value));

        IsCommitting = true;
        try
        {
            Workspace.MutateSplineTemplate(
                TemplateKey,
                $"SplineTemplate '{TemplateKey}' のプロップ #{index} を変更",
                draft =>
                {
                    if (0 <= index && index < draft.Props.Count)
                    {
                        draft.Props[index] = updated;
                    }
                },
                $"SplineTemplate.{TemplateKey}.Prop.{index}");
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private static string GenerateUniqueRuleKey(IEnumerable<string?> existingKeys, string prefix)
    {
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? k in existingKeys)
        {
            if (!string.IsNullOrWhiteSpace(k))
            {
                used.Add(k.Trim());
            }
        }

        int n = 1;
        while (true)
        {
            string candidate = $"{prefix}{n}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }

            n++;
        }
    }
}
