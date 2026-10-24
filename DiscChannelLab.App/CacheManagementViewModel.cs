namespace Disc2Flac;

public sealed partial class MainViewModel
{
    private readonly CacheStorage _cache;
    private CacheUsage? _cacheUsage;
    private bool _cacheRefreshing;
    public bool IsClearingCache { get; private set; }
    public string CacheSizeText => _cacheUsage is null ? LanguageService.T("容量を確認しています…") : CacheStorage.FormatBytes(_cacheUsage.ClearableBytes);
    public string CacheDetailsText => _cacheUsage is null ? "" :
        $"{LanguageService.T("音声")}: {CacheStorage.FormatBytes(_cacheUsage.AudioBytes)}\n" +
        $"{LanguageService.T("ビューアー")}: {CacheStorage.FormatBytes(_cacheUsage.ViewerBytes)}\n" +
        $"{LanguageService.T("必要なツール（保持）")}: {CacheStorage.FormatBytes(_cacheUsage.ToolBytes)}";
    public bool CanClearCache => !IsBusy && _playback is null && !IsPlaying && !IsAtmosStreamingEnabled && InspectionTask.IsCompleted && !_cacheRefreshing && !_cache.IsInUse;
    public string CacheClearHint => LanguageService.T(_cacheUsage?.ScanErrors > 0 ? "一部の容量を確認できませんでした。" :
        IsAtmosStreamingEnabled ? "ADM再生を解除してからキャッシュをクリアしてください。" :
        _playback is not null || IsPlaying || IsBusy || !InspectionTask.IsCompleted ? "再生・変換・診断の終了後にクリアできます。" :
        _cache.IsInUse ? "他のプレーヤーとビューアーを終了してからクリアできます。" :
        "曲情報・設定・保存済みFLACは保持します。音声キャッシュは次回再生成します。");
    public void RefreshCacheControls()
    {
        Changed(nameof(CanClearCache)); Changed(nameof(CacheClearHint));
    }
    public async Task RefreshCacheSizeAsync()
    {
        if (_cacheRefreshing) return;
        _cacheRefreshing = true; RefreshCacheControls();
        try { _cacheUsage = await _cache.MeasureAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _log.Write("CACHE SIZE: " + ex.Message); }
        finally
        {
            _cacheRefreshing = false;
            Changed(nameof(CacheSizeText)); Changed(nameof(CacheDetailsText)); RefreshCacheControls();
        }
    }
    public async Task ClearCachesAsync()
    {
        if (!CanClearCache) return;
        IsClearingCache = true;
        BeginWork("キャッシュをクリアしています");
        try
        {
            var result = await _cache.ClearAsync(_work!.Token);
            _cacheUsage = result.Usage;
            _discService.ClearCache(); _navigation.ClearCache();
            foreach (var error in result.Errors) _log.Write("CACHE CLEAR: " + error);
            Status = result.Errors.Count == 0 ? "キャッシュをクリアしました。" : "使用中またはアクセスできないキャッシュが一部残りました。";
        }
        catch (Exception ex) { HandleError(ex); }
        finally
        {
            IsClearingCache = false;
            EndWork();
            await RefreshCacheSizeAsync();
        }
    }
}
