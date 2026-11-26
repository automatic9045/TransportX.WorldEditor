using System;
using System.IO;
using R3;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.ViewModels.Editors;

/// <summary>
/// Document ViewModel for editing <c>Environment*.xml</c> IBL / Exposure / ToneMap / Bloom parameters.
/// </summary>
public sealed class EnvironmentEditorDocumentViewModel : EditorDocumentBase
{
    private readonly WorldWorkspaceService Workspace;
    private readonly string DocumentKey;
    private bool IsSyncing;

    public BindableReactiveProperty<string> DiffuseTexturePath { get; }
    public BindableReactiveProperty<string> SpecularTexturePath { get; }
    public BindableReactiveProperty<double> IblIntensity { get; }
    public BindableReactiveProperty<double> IblSaturation { get; }

    public BindableReactiveProperty<decimal?> ExposureKey { get; }
    public BindableReactiveProperty<decimal?> ExposureMin { get; }
    public BindableReactiveProperty<decimal?> ExposureMax { get; }
    public BindableReactiveProperty<decimal?> DarkAdaptationSpeed { get; }
    public BindableReactiveProperty<decimal?> LightAdaptationSpeed { get; }

    public BindableReactiveProperty<decimal?> ToneContrast { get; }
    public BindableReactiveProperty<decimal?> ToneShoulder { get; }
    public BindableReactiveProperty<decimal?> ToneMaxLuminance { get; }
    public BindableReactiveProperty<decimal?> ToneMidtoneScale { get; }

    public BindableReactiveProperty<decimal?> BloomThreshold { get; }
    public BindableReactiveProperty<double> BloomIntensity { get; }
    public BindableReactiveProperty<double> BloomScatter { get; }
    public BindableReactiveProperty<double> BloomSoftKnee { get; }
    public BindableReactiveProperty<string> BloomTintHex { get; }

    public EnvironmentEditorDocumentViewModel(WorldWorkspaceService workspace, string documentKey)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(documentKey);

        Workspace = workspace;
        DocumentKey = WorldProject.NormalizeRelativePath(documentKey);
        Id = $"Doc_Env_{DocumentKey}";
        Title = Path.GetFileName(DocumentKey);

        DiffuseTexturePath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SpecularTexturePath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        IblIntensity = new BindableReactiveProperty<double>(1.0).AddTo(Disposables);
        IblSaturation = new BindableReactiveProperty<double>(1.0).AddTo(Disposables);

        ExposureKey = new BindableReactiveProperty<decimal?>(0.118m).AddTo(Disposables);
        ExposureMin = new BindableReactiveProperty<decimal?>(0.01m).AddTo(Disposables);
        ExposureMax = new BindableReactiveProperty<decimal?>(5m).AddTo(Disposables);
        DarkAdaptationSpeed = new BindableReactiveProperty<decimal?>(0.5m).AddTo(Disposables);
        LightAdaptationSpeed = new BindableReactiveProperty<decimal?>(1.5m).AddTo(Disposables);

        ToneContrast = new BindableReactiveProperty<decimal?>(1.6m).AddTo(Disposables);
        ToneShoulder = new BindableReactiveProperty<decimal?>(0.977m).AddTo(Disposables);
        ToneMaxLuminance = new BindableReactiveProperty<decimal?>(12m).AddTo(Disposables);
        ToneMidtoneScale = new BindableReactiveProperty<decimal?>(1.5m).AddTo(Disposables);

        BloomThreshold = new BindableReactiveProperty<decimal?>(2m).AddTo(Disposables);
        BloomIntensity = new BindableReactiveProperty<double>(0.2).AddTo(Disposables);
        BloomScatter = new BindableReactiveProperty<double>(1.0).AddTo(Disposables);
        BloomSoftKnee = new BindableReactiveProperty<double>(0.5).AddTo(Disposables);
        BloomTintHex = new BindableReactiveProperty<string>("#FFFFFF").AddTo(Disposables);

        BindString(DiffuseTexturePath, "Ibl.Diffuse", "IBL 拡散光テクスチャを変更",
            e => e.Ibl.DiffuseTexturePath ?? string.Empty,
            (e, v) => e.Ibl.DiffuseTexturePath = string.IsNullOrWhiteSpace(v) ? null : v);
        BindString(SpecularTexturePath, "Ibl.Specular", "IBL 反射光テクスチャを変更",
            e => e.Ibl.SpecularTexturePath ?? string.Empty,
            (e, v) => e.Ibl.SpecularTexturePath = string.IsNullOrWhiteSpace(v) ? null : v);
        BindFloatDouble(IblIntensity, "Ibl.Intensity", "IBL 強度を変更",
            e => e.Ibl.Intensity, (e, v) => e.Ibl.Intensity = v);
        BindFloatDouble(IblSaturation, "Ibl.Saturation", "IBL 彩度を変更",
            e => e.Ibl.Saturation, (e, v) => e.Ibl.Saturation = v);

        BindFloatDecimal(ExposureKey, "Exposure.Key", "露出基準輝度を変更",
            e => e.Exposure.Key, (e, v) => e.Exposure.Key = v);
        BindFloatDecimal(ExposureMin, "Exposure.Min", "露出下限値を変更",
            e => e.Exposure.Min, (e, v) => e.Exposure.Min = v);
        BindFloatDecimal(ExposureMax, "Exposure.Max", "露出上限値を変更",
            e => e.Exposure.Max, (e, v) => e.Exposure.Max = v);
        BindFloatDecimal(DarkAdaptationSpeed, "Exposure.Dark", "暗順応速度を変更",
            e => e.Exposure.DarkAdaptationSpeed, (e, v) => e.Exposure.DarkAdaptationSpeed = v);
        BindFloatDecimal(LightAdaptationSpeed, "Exposure.Light", "明順応速度を変更",
            e => e.Exposure.LightAdaptationSpeed, (e, v) => e.Exposure.LightAdaptationSpeed = v);

        BindFloatDecimal(ToneContrast, "Tone.Contrast", "トーンマップ コントラストを変更",
            e => e.ToneMap.Contrast, (e, v) => e.ToneMap.Contrast = v);
        BindFloatDecimal(ToneShoulder, "Tone.Shoulder", "トーンマップ ショルダーを変更",
            e => e.ToneMap.Shoulder, (e, v) => e.ToneMap.Shoulder = v);
        BindFloatDecimal(ToneMaxLuminance, "Tone.MaxLum", "トーンマップ最大輝度を変更",
            e => e.ToneMap.MaxLuminance, (e, v) => e.ToneMap.MaxLuminance = v);
        BindFloatDecimal(ToneMidtoneScale, "Tone.Midtone", "中間調スケールを変更",
            e => e.ToneMap.MidtoneScale, (e, v) => e.ToneMap.MidtoneScale = v);

        BindFloatDecimal(BloomThreshold, "Bloom.Threshold", "ブルーム閾値を変更",
            e => e.Bloom.Threshold, (e, v) => e.Bloom.Threshold = v);
        BindFloatDouble(BloomIntensity, "Bloom.Intensity", "ブルーム強度を変更",
            e => e.Bloom.Intensity, (e, v) => e.Bloom.Intensity = v);
        BindFloatDouble(BloomScatter, "Bloom.Scatter", "ブルーム拡散を変更",
            e => e.Bloom.Scatter, (e, v) => e.Bloom.Scatter = v);
        BindFloatDouble(BloomSoftKnee, "Bloom.SoftKnee", "ブルームソフト閾値を変更",
            e => e.Bloom.SoftKnee, (e, v) => e.Bloom.SoftKnee = v);
        BindString(BloomTintHex, "Bloom.Tint", "ブルームティント色を変更",
            e => e.Bloom.TintHex, (e, v) => e.Bloom.TintHex = v);

        Workspace.ProjectRevision.Subscribe(_ => SyncFromModel()).AddTo(Disposables);
        SyncFromModel();
        BindDirtyTitle(
            Workspace,
            Path.GetFileName(DocumentKey),
            (_, target) => target.Kind == WorldFileKind.EnvironmentXml
                && string.Equals(WorldProject.NormalizeRelativePath(target.RelativePath), DocumentKey, StringComparison.OrdinalIgnoreCase));
    }

    private EnvironmentProfileDocument? GetDocument()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        return project is not null && project.Environments.TryGetValue(DocumentKey, out EnvironmentProfileDocument? doc) ? doc : null;
    }

    private void BindString(
        BindableReactiveProperty<string> prop,
        string keySuffix,
        string desc,
        Func<EnvironmentProfileDocument, string> getter,
        Action<EnvironmentProfileDocument, string> setter)
    {
        prop.Skip(1).Subscribe(newVal =>
        {
            EnvironmentProfileDocument? doc = GetDocument();
            if (IsSyncing || doc is null)
            {
                return;
            }

            string oldVal = getter(doc);
            if (string.Equals(oldVal, newVal, StringComparison.Ordinal))
            {
                return;
            }

            Workspace.Execute(new MergeableValueCommand<string>(
                desc,
                $"Env.{DocumentKey}.{keySuffix}",
                InvalidationScope.FullRebuild,
                new WorldFileTarget(WorldFileKind.EnvironmentXml, DocumentKey),
                oldVal,
                newVal,
                v => setter(doc, v)));
        }).AddTo(Disposables);
    }

    private void BindFloatDouble(
        BindableReactiveProperty<double> prop,
        string keySuffix,
        string desc,
        Func<EnvironmentProfileDocument, float> getter,
        Action<EnvironmentProfileDocument, float> setter)
    {
        prop.Skip(1).Subscribe(newVal =>
        {
            EnvironmentProfileDocument? doc = GetDocument();
            if (IsSyncing || doc is null)
            {
                return;
            }

            float oldVal = getter(doc);
            float fNew = (float)newVal;
            if (MathF.Abs(oldVal - fNew) < 1e-6f)
            {
                return;
            }

            Workspace.Execute(new MergeableValueCommand<float>(
                desc,
                $"Env.{DocumentKey}.{keySuffix}",
                InvalidationScope.FullRebuild,
                new WorldFileTarget(WorldFileKind.EnvironmentXml, DocumentKey),
                oldVal,
                fNew,
                v => setter(doc, v)));
        }).AddTo(Disposables);
    }

    private void BindFloatDecimal(
        BindableReactiveProperty<decimal?> prop,
        string keySuffix,
        string desc,
        Func<EnvironmentProfileDocument, float> getter,
        Action<EnvironmentProfileDocument, float> setter)
    {
        prop.Skip(1).Subscribe(newVal =>
        {
            EnvironmentProfileDocument? doc = GetDocument();
            if (IsSyncing || doc is null || !newVal.HasValue)
            {
                return;
            }

            float oldVal = getter(doc);
            float fNew = (float)newVal.Value;
            if (MathF.Abs(oldVal - fNew) < 1e-6f)
            {
                return;
            }

            Workspace.Execute(new MergeableValueCommand<float>(
                desc,
                $"Env.{DocumentKey}.{keySuffix}",
                InvalidationScope.FullRebuild,
                new WorldFileTarget(WorldFileKind.EnvironmentXml, DocumentKey),
                oldVal,
                fNew,
                v => setter(doc, v)));
        }).AddTo(Disposables);
    }

    private void SyncFromModel()
    {
        IsSyncing = true;
        try
        {
            EnvironmentProfileDocument? doc = GetDocument();
            if (doc is null)
            {
                return;
            }

            DiffuseTexturePath.Value = doc.Ibl.DiffuseTexturePath ?? string.Empty;
            SpecularTexturePath.Value = doc.Ibl.SpecularTexturePath ?? string.Empty;
            IblIntensity.Value = doc.Ibl.Intensity;
            IblSaturation.Value = doc.Ibl.Saturation;

            ExposureKey.Value = (decimal)doc.Exposure.Key;
            ExposureMin.Value = (decimal)doc.Exposure.Min;
            ExposureMax.Value = (decimal)doc.Exposure.Max;
            DarkAdaptationSpeed.Value = (decimal)doc.Exposure.DarkAdaptationSpeed;
            LightAdaptationSpeed.Value = (decimal)doc.Exposure.LightAdaptationSpeed;

            ToneContrast.Value = (decimal)doc.ToneMap.Contrast;
            ToneShoulder.Value = (decimal)doc.ToneMap.Shoulder;
            ToneMaxLuminance.Value = (decimal)doc.ToneMap.MaxLuminance;
            ToneMidtoneScale.Value = (decimal)doc.ToneMap.MidtoneScale;

            BloomThreshold.Value = (decimal)doc.Bloom.Threshold;
            BloomIntensity.Value = doc.Bloom.Intensity;
            BloomScatter.Value = doc.Bloom.Scatter;
            BloomSoftKnee.Value = doc.Bloom.SoftKnee;
            BloomTintHex.Value = doc.Bloom.TintHex;
        }
        finally
        {
            IsSyncing = false;
        }
    }
}
