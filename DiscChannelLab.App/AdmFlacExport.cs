using System.Security.Cryptography;

namespace Disc2Flac;

public sealed partial class MainViewModel
{
    bool _saveBinauralFlac;
    public bool SaveBinauralFlac
    {
        get => CurrentAdm is not null || _saveBinauralFlac;
        set
        {
            if (!Set(ref _saveBinauralFlac, value)) return;
            if (value) { SaveStereoDownmix = false; SaveIndividualChannels = false; SaveMultichannelFlac = false; SaveMixerDownmix = false; }
            else if (CurrentAdm is null && _quality == OutputQuality.HighResolution && SelectedStream?.CanMakeHighResolution != true) IsCdSelected = true;
            Changed(nameof(CanChooseHighRes)); Changed(nameof(HighResOptionText)); Changed(nameof(HighResHelpText)); Changed(nameof(SelectedStreamNote));
            Changed(nameof(CanConvert));
        }
    }
    public bool CanChooseBinauralFlac => !IsBusy && !IsPlaying && CurrentAdm is null && HeadphonesEnabled && HasVerifiedAudio;
    public string BinauralFlacOptionText => LanguageService.Instance.IsJapanese ? "ヘッドフォン・反射設定で2ch FLAC保存（頭の向きを固定）" : "Render headphone / reflection settings to stereo FLAC (fixed head pose)";
    async Task<bool> ConvertAdmSelectionAsync()
    {
        var disc = _disc!; var playlist = _loadedPlaylist!; var preferred = SelectedStream!; var tracks = Tracks.Where(t => t.IsSelected).ToArray();
        var options = IsAtmosStreamingEnabled ? ProgressiveAtmosSource.Options(HeadphoneSession.Current.Options, SelectedAtmosPresentation)
            : AdmFileFormat.RenderOptions(HeadphoneSession.Current.Options); var pose = HeadphoneSession.Current.Pose;
        string folder = Path.Combine(OutputFolder, ConversionService.SafeName(disc.AlbumTitle)); if (GroupByChapter) folder = Path.Combine(folder, ConversionService.SafeName(playlist.TitleName));
        bool renderNormal = _saveBinauralFlac || CurrentAdm is not null;
        BeginWork("Rendering headphone / reflection FLAC…"); SavedFolder = "";
        try
        {
            string hash = options.Method == HeadphoneMethod.MeasuredBrir ? options.BrirHash : options.Hrtf == HeadphoneHrtf.Sofa ? Convert.ToHexStringLower(SHA256.HashData(SofaHrtf.Read(options.SofaPath))) : HrtfEntry.BuiltIn.Hash;
            if (options.Method == HeadphoneMethod.MeasuredBrir) await BrirLibrary.PrepareAsync(options.BrirPath, options.BrirHash, _work!.Token);
            var snapshot = new SpatialRenderSnapshot(options, pose, new(StereoMixSettings.Default, null), VolumeCurve.Gain(Volume, PerceivedVolume), hash) { OutputMix = PreviewState(CurrentMix, null) };
            var tools = new ToolPaths(); Directory.CreateDirectory(folder);
            for (int i = 0; i < tracks.Length; i++)
            {
                var track = tracks[i]; var stream = IsAtmosStreamingEnabled ? preferred : AudioFileReader.StreamForTrack(playlist, track, preferred);
                if (stream.Codec != "adm" && !renderNormal) { await _conversion.ConvertAsync(disc, playlist, stream, [track], _quality, OutputFolder, new Progress<ConversionProgress>(), _work!.Token, GroupByChapter); continue; }
                Status = $"{i + 1}/{tracks.Length}: {track.Title}";
                var input = await PrepareSpatialTrackAsync(track, 0, track.DurationSeconds, _work!.Token);
                var mix = PreviewState(CurrentMix, null);
                if (stream.Codec != "adm" && mix.Channels is { } strips)
                    mix = mix with { Channels = input.Scene.Sources.Select(s => strips.FirstOrDefault(c => c.Code == s.Id) ?? new ExperimentalChannel(s.Id, 1, 0, false)).ToArray() };
                var conditions = stream.Codec == "adm" ? snapshot : snapshot with { Mix = mix, OutputMix = null };
                RenderedAudio? audio = null;
                try
                {
                    audio = await SpatialRendering.RenderAsync(input, conditions, options.Method == HeadphoneMethod.MeasuredBrir, _work.Token);
                    bool speakerStereo = input.Progressive is not null && input.Presentation == AtmosPresentation.Stereo;
                    string suffix = speakerStereo ? "Speaker Stereo" : options.Method == HeadphoneMethod.MeasuredBrir ? "Headphone BRIR" : options.Method == HeadphoneMethod.Direct ? "Headphone Direct" : "Headphone Binaural";
                    var tags = new Dictionary<string, string> { ["title"] = track.Title, ["album"] = playlist.TitleName, ["track"] = track.Number.ToString(), ["artist"] = track.Artist ?? disc.Artist ?? "", ["RENDERER"] = speakerStereo ? "Stereo" : options.Method.ToString(),
                        ["OUTPUT_USE"] = suffix, ["SPATIAL_PRESENTATION"] = input.Progressive is null ? stream.Codec : input.Presentation.ToString() };
                    await SpatialRendering.ExportAsync(audio, Path.Combine(folder, $"{track.Number:00} {ConversionService.SafeName(track.Title)} [{suffix}].flac"), tools.Ffmpeg, true, _work.Token, _quality == OutputQuality.Cd ? 44100 : 48000, _quality == OutputQuality.Cd ? 16 : 24, tags);
                }
                finally { if (audio is not null && File.Exists(audio.PcmPath)) File.Delete(audio.PcmPath); if (input.Wave is null && File.Exists(input.PcmPath)) File.Delete(input.PcmPath); }
                Progress = (i + 1d) / tracks.Length;
            }
            SavedFolder = folder; Status = "FLAC export completed."; return true;
        }
        catch (Exception ex) { HandleError(ex); return false; }
        finally { EndWork(); }
    }
}
