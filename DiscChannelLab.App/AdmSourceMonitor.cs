namespace Disc2Flac;

/// <summary>Immutable source selection, scoped to one parsed ADM. Preview only; no disc or export settings.</summary>
public sealed class AdmSourceMonitor
{
    static readonly PreviewMixState All = new(StereoMixSettings.Default, null);
    sealed record State(AdmWave Wave, string[] Ids, PreviewMixState Mix);
    State? state;
    public PreviewMixState For(AdmWave wave)
    {
        var selected = Volatile.Read(ref state);
        return ReferenceEquals(selected?.Wave, wave) ? selected!.Mix : All;
    }
    public IReadOnlyList<string> Selected(AdmWave? wave)
    {
        var selected = Volatile.Read(ref state);
        return ReferenceEquals(selected?.Wave, wave) && selected is not null ? Array.AsReadOnly(selected.Ids) : Array.Empty<string>();
    }
    public void Set(AdmWave wave, IEnumerable<string> sources, SpatialScene? liveScene = null)
    {
        var scene = liveScene ?? wave.Scene;
        var ids = sources.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Any(id => !scene.Sources.Any(s => s.Id == id))) throw new ArgumentException("Solo source is not part of this ADM.");
        if (ids.Length == 0) { Clear(); return; }
        var mix = new PreviewMixState(StereoMixSettings.Default, null,
            scene.Sources.Select(s => new ExperimentalChannel(s.Id, 1, 0, false, Solo: ids.Contains(s.Id, StringComparer.Ordinal))).ToArray());
        Volatile.Write(ref state, new(wave, ids, mix));
    }
    public bool ClearIfDifferent(AdmWave? wave)
    {
        var selected = Volatile.Read(ref state);
        if (selected is null || ReferenceEquals(selected.Wave, wave)) return false;
        Clear(); return true;
    }
    public void Clear() => Volatile.Write(ref state, null);
}

public sealed partial class AudioNavigationService
{
    public AdmSourceMonitor AdmMonitor { get; } = new();
    AdmWave? spatialAdmWave;
    public AdmWave? SpatialAdmWave { get => Volatile.Read(ref spatialAdmWave); set => Volatile.Write(ref spatialAdmWave, value); }
}

public sealed partial class MainViewModel
{
    public IReadOnlyList<string> AdmSoloSources => _navigation.AdmMonitor.Selected(CurrentAdm);
    public string AdmSoloDescription
    {
        get
        {
            var ids = AdmSoloSources;
            return ids.Count == 0 ? "Solo off · all ADM sources" : "Solo (preview): " +
                string.Join(", ", CurrentAdmScene!.Sources.Where(s => ids.Contains(s.Id)).Select(s => s.Name));
        }
    }
    public void ToggleAdmSourceSolo(string id, bool additive = false)
    {
        if (IsBusy || !IsAdmPlaybackEnabled || CurrentAdm is not { } wave || CurrentAdmScene?.Sources.Any(s => s.Id == id) != true) return;
        var ids = AdmSoloSources.ToHashSet(StringComparer.Ordinal);
        if (additive) { if (!ids.Add(id)) ids.Remove(id); }
        else if (ids.Count == 1 && ids.Contains(id)) ids.Clear();
        else { ids.Clear(); ids.Add(id); }
        _navigation.AdmMonitor.Set(wave, ids, CurrentAdmScene); NotifyAdmSolo();
    }
    public void SetAdmSourceSolo(string id, bool enabled)
    {
        if (IsBusy || !IsAdmPlaybackEnabled || CurrentAdm is not { } wave || CurrentAdmScene?.Sources.Any(s => s.Id == id) != true) return;
        var ids = AdmSoloSources.ToHashSet(StringComparer.Ordinal);
        if (enabled) ids.Add(id); else ids.Remove(id);
        _navigation.AdmMonitor.Set(wave, ids, CurrentAdmScene); NotifyAdmSolo();
    }
    public void ClearAdmSourceSolo() { _navigation.AdmMonitor.Clear(); NotifyAdmSolo(); }
    void NotifyAdmSolo() { Changed(nameof(AdmSoloSources)); Changed(nameof(AdmSoloDescription)); }
}
