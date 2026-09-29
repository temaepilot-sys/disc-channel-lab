using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Disc2Flac;

public sealed class LanguageService : INotifyPropertyChanged
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BDDVD2Flac", "language.txt");

    public static LanguageService Instance { get; } = new();
    public bool IsJapanese { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public void Load()
    {
        try { Apply(File.Exists(PreferencePath) && File.ReadAllText(PreferencePath).Trim() == "ja"); }
        catch (IOException) { Apply(false); }
        catch (UnauthorizedAccessException) { Apply(false); }
    }

    public void SetJapanese(bool japanese)
    {
        if (IsJapanese == japanese) return;
        Apply(japanese);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, japanese ? "ja" : "en");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal void Apply(bool japanese)
    {
        IsJapanese = japanese;
        PropertyChanged?.Invoke(this, new(nameof(IsJapanese)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string T(string text)
    {
        if (Instance.IsJapanese || string.IsNullOrEmpty(text)) return text;
        if (English.TryGetValue(text, out var translated)) return translated;
        foreach (var (pattern, replacement) in Patterns)
            text = pattern.Replace(text, replacement);
        return text.Replace("全チャンネル", "all channels", StringComparison.Ordinal);
    }

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["末尾の映像区間は無音"] = "Video-only tail rendered as silence",
        ["標準ミックスを使用：このフェーダーは再生に適用されていません。"] = "Standard mix is active: these faders are not applied to playback.",
        ["設定を保存…"] = "Save preset…",
        ["設定を読み込む…"] = "Load preset…",
        ["ミキサー設定を保存"] = "Save mixer preset",
        ["ミキサー設定を読み込む"] = "Load mixer preset",
        ["ミキサー設定"] = "Mixer preset",
        ["ミキサー設定ファイルの形式または値が不正です。"] = "The mixer preset format or values are invalid.",
        ["設定ファイルと音源のチャンネル構成が異なります。同じ構成の音声を選択してください。"] = "The preset and audio have different channel configurations. Select audio with the same channel configuration.",
        ["設定はディスク情報と独立したJSONに保存できます。再生への適用がオフの場合は標準ミックスを使用します。"] = "Save presets as JSON independently of disc information. When playback is bypassed, the standard mix is used.",
        ["対応するマルチチャンネル音声を選択すると、チャンネルごとの操作が表示されます。"] = "Select a supported multichannel audio stream to display individual channel controls.",
        ["チャンネルミキサー"] = "Channel mixer",
        ["ミキサーを再生に適用"] = "Apply mixer to playback",
        ["チャンネルミキサーの設定で2ch FLACを保存"] = "Export stereo FLAC using channel mixer settings",
        ["ミックス名"] = "Mix name",
        ["Master音量も保存に適用"] = "Include Master volume in export",
        ["このミックスをFLAC保存"] = "Export this mix to FLAC",
        ["メイン画面の選択曲・音質・保存先を使用。保存開始時に再生を停止します。既存ファイルは上書きしません。"] = "Uses selected tracks, quality and destination from the main window. Export stops playback. Existing files are preserved.",
        ["ミックス名は80文字以内で入力してください。"] = "Enter a mix name of up to 80 characters.",
        ["ミキサーの設定または音源のチャンネル配置が不正です。"] = "Invalid mixer settings or source channel layout.",
        ["閉じる"] = "Close",
        ["ミキサーを開く"] = "Open mixer",
        ["実験ミキサー（再生のみ）"] = "Experimental mixer (playback only)",
        ["実験用チャンネルミキサー"] = "Experimental channel mixer",
        ["実験ミキサーを再生に適用（FLAC保存には適用しません）"] = "Apply experimental mixer to playback (does not affect FLAC export)",
        ["入力"] = "Input",
        ["調整後 L/R"] = "Post L/R",
        ["合成後 L/R"] = "Output L/R",
        ["元のパン位置"] = "Original pan",
        ["赤表示：0 dBFS以上"] = "Red: 0 dBFS or above",
        ["入力：従来のピーク。調整後 L/R：各チャンネルのフェーダー・パン適用後、Master適用前。合成後 L/R：Master適用後、クリップ前。"] = "Input: original peaks. Post L/R: each channel after fader and pan, before Master. Output L/R: summed signal after Master, before clipping.",
        ["オン：チャンネル別設定を使用。オフ：従来のステレオミックス比率を使用。閉じても設定と再生は維持されます。"] = "On: use individual channel settings. Off: use the original stereo mix settings. Closing this window preserves playback and settings.",
        ["フェーダー：0〜100%（聴感カーブ）／パン：等電力カーブ／メーター：−60〜0 dBFS"] = "Faders: 0–100% (listening curve) · Pan: equal power · Meters: −60 to 0 dBFS",
        ["読み取り・診断"] = "Reading and diagnostics",
        ["一部区間のみ"] = "Some sections only",
        ["選択中の曲に、この音声が存在しない区間があります。保存する曲または音声を変更してください。"] = "This audio is absent from some selected tracks. Change the track selection or audio stream before saving.",
        ["選択タイトルを再解析"] = "Retry selected title",
        ["全タイトルを診断"] = "Inspect all titles",
        ["診断を中止"] = "Stop inspection",
        ["診断レポートを保存"] = "Save diagnostic report",
        ["短い末尾を前の曲に含める"] = "Merge short final chapter",
        ["末尾の1秒未満の区間を前の曲に含めています。元の区切りも保持しています。"] = "The final segment shorter than one second is included in the previous track. Original boundaries are preserved.",
        ["ディスクに記録された元の区切りを表示しています。"] = "Showing the original disc chapter boundaries.",
        ["チャプターの補正はありません。"] = "No chapter correction is needed.",
        ["チャプター情報を表示しています。音声は再解析できます。"] = "Chapter information is shown. Audio analysis can be retried.",
        ["音声を確認中"] = "Checking audio",
        ["再生確認済み"] = "Audio decode verified",
        ["音声あり・一部未確認"] = "Audio found; some streams or clips need checking",
        ["音声なし"] = "No audio detected",
        ["対応対象外の音声"] = "Audio format outside supported scope",
        ["解析失敗・再試行可能"] = "Analysis failed; retry available",
        ["保護されたディスク"] = "Protected disc",
        ["未確認"] = "Not inspected",
        ["ディスクの診断が完了しました。"] = "Disc inspection completed.",
        ["診断レポートを保存しました。"] = "Diagnostic report saved.",
        ["言語"] = "Language",
        ["ダークモード"] = "Dark mode",
        ["ディスク"] = "Disc",
        ["ドライブ"] = "Drive",
        ["再読み込み"] = "Reload",
        ["フォルダーを開く"] = "Open folder",
        ["ISOを開く"] = "Open ISO",
        ["タイトル / チャプター"] = "Title / chapter",
        ["音声"] = "Audio",
        ["ディスク名（親フォルダー）"] = "Disc name (parent folder)",
        ["タイトル名（アルバム・サブフォルダー）"] = "Title name (album and subfolder)",
        ["チャプター名を編集"] = "Edit chapter names",
        ["保存する音質"] = "Output quality",
        ["CD音質相当 · 16bit / 44.1kHz"] = "CD quality · 16-bit / 44.1 kHz",
        ["ハイレゾ · 元の形式"] = "High resolution · source format",
        ["ハイレゾは元音源が24bit / 88.2kHz以上のときに選べます。"] = "High resolution is available when the source is at least 24-bit / 88.2 kHz.",
        ["全行を一括編集"] = "Edit all rows",
        ["全行の曲名と、ディスク名・タイトル名をテキストで編集します"] = "Edit all track titles and the disc and title names as text",
        ["選択行を一括編集"] = "Edit selected rows",
        ["表で Ctrl または Shift を使って選んだ行を、上から順に編集します"] = "Edit Ctrl- or Shift-selected rows in order",
        ["すべて選択"] = "Select all",
        ["選択を解除"] = "Clear selection",
        ["曲・チャプター"] = "Tracks and chapters",
        ["保存"] = "Save",
        ["曲名（ダブルクリックで再生）"] = "Track title (double-click to play)",
        ["アーティスト"] = "Artist",
        ["日付"] = "Date", ["ジャンル"] = "Genre", ["発売元"] = "Publisher", ["説明"] = "Description",
        ["開始"] = "Start",
        ["終了"] = "End",
        ["長さ"] = "Length",
        ["音量"] = "Volume",
        ["連続再生"] = "Continuous play",
        ["前の曲"] = "Previous",
        ["停止"] = "Stop",
        ["次の曲"] = "Next",
        ["聴感カーブ"] = "Perceptual volume",
        ["オン: 100%未満は小音量を細かく調整。オフ: 増幅率を線形で調整。どちらも100%は原音、200%は2倍。"] = "On: finer adjustment below 100%. Off: linear gain. 100% is original level and 200% is twice the amplitude.",
        ["試聴する音"] = "Preview audio",
        ["個別チャンネルは左右のスピーカーに同じ音を出します"] = "A solo channel plays equally through both speakers",
        ["入力チャンネルのピーク · ミックス前 / 音量調整前"] = "Input channel peaks · before mixing and volume",
        ["分割位置の調整（0.1秒単位）"] = "Adjust split point (0.1 second)",
        ["-10秒"] = "−10 s", ["-1秒"] = "−1 s", ["-0.1秒"] = "−0.1 s",
        ["+0.1秒"] = "+0.1 s", ["+1秒"] = "+1 s", ["+10秒"] = "+10 s",
        ["分割位置"] = "Split point",
        ["選択した行の先頭からの時刻（時:分:秒.ミリ秒）"] = "Time from the start of the selected row (hh:mm:ss.mmm)",
        ["この位置で分割"] = "Split here",
        ["次の行と結合"] = "Merge with next",
        ["無音候補を探す"] = "Find silence",
        ["FLACに保存"] = "Save as FLAC",
        ["保存先を選ぶ"] = "Choose output folder",
        ["さらにチャプター別フォルダーを作成する"] = "Also create a folder for each chapter",
        ["多チャンネル音声を2chステレオのFLACで保存"] = "Save multichannel audio as stereo FLAC",
        ["チャンネル別のモノラルFLACを曲ごとに保存"] = "Save one mono FLAC per channel and track",
        ["ステレオミックス比率（試聴にも適用）"] = "Stereo mix levels (also used for preview)",
        ["バーは聴感カーブ、数値は実際の比率です。"] = "Sliders use a perceptual curve; numbers show actual mix levels.",
        ["デフォルトに戻す"] = "Reset to defaults", ["ミュート"] = "Mute",
        ["極性反転"] = "Invert polarity",
        ["フェーダーリンク"] = "Fader links",
        ["オンにすると左側の値に揃え、以後は左右どちらの操作でも同じ音量になります。"] = "Enable to match the left channel's level; then adjusting either side sets both to the same gain.",
        ["極性反転：波形の正負を反転します。"] = "Invert polarity: reverse the sign of this channel's waveform.",
        ["ミックスを調整すると試聴音をステレオミックスに切り替えます。"] =
            "Adjusting the mix switches preview to the stereo mix.",
        ["前方左右"] = "Front L/R", ["センター"] = "Center", ["サラウンド"] = "Surround",
        ["FLACで保存"] = "Save FLAC", ["中止"] = "Cancel",
        ["保存フォルダーを開く"] = "Open output folder",
        ["曲情報とチャプター名を一括編集"] = "Edit tracks and chapter names",
        ["曲名は上から1行ずつ対応します。⇥ の後にアーティスト名を加えると、その曲のアーティストも変更できます。貼り付けたタブも ⇥ と表示します。"] = "Enter one track title per line. Add an artist after ⇥ to update that track's artist. Pasted tabs are shown as ⇥.",
        ["選択行のアーティスト"] = "Artist for selected rows",
        ["曲名と曲ごとのアーティスト"] = "Track titles and artists",
        ["1行1曲。区切りの ⇥ はタブを見える形で表示しています。スペースとは別です。"] = "One track per line. ⇥ marks a tab separator and is distinct from a space.",
        ["チャプター名"] = "Chapter names",
        ["キャンセル"] = "Cancel", ["反映する"] = "Apply",
        ["BDMV / AUDIO_TS / VIDEO_TS またはディスクのルートを選択"] = "Select a BDMV, AUDIO_TS, VIDEO_TS or disc root folder",
        ["Blu-ray / DVD ISOファイルを選択"] = "Select a Blu-ray or DVD ISO file",
        ["ISOファイル (*.iso)|*.iso"] = "ISO files (*.iso)|*.iso",
        ["FLAC の保存先を選択"] = "Choose a folder for FLAC output",
        ["ディスクを入れてください"] = "Insert a disc",
        ["光学ドライブを確認しています"] = "Checking optical drives",
        ["一時停止"] = "Pause", ["再生"] = "Play",
        ["曲を選択してください"] = "Select a track",
        ["タイトル名を入力してください。"] = "Enter a title name.",
        ["チャプターを選択してください"] = "Select a chapter",
        ["名称未設定"] = "Untitled",
        ["ステレオミックス"] = "Stereo mix",
        ["この音声のチャンネル数または配置には対応していません。"] = "This channel count or layout is not supported.",
        ["元音声は非可逆圧縮です。FLAC保存後の音質は元音声を超えません。"] = "The source uses lossy compression. Saving as FLAC cannot improve its quality.",
        ["モノラルのまま FLAC に保存します。試聴時は左右に同じ音を出します。"] = "Save as mono FLAC. Preview plays equally through both speakers.",
        ["ディスクを解析しています"] = "Analyzing disc",
        ["ISOをマウントしています"] = "Mounting ISO",
        ["対応するディスクを待っています"] = "Waiting for a supported disc",
        ["アルバム候補を解析しています"] = "Analyzing title candidates",
        ["全編を1曲として表示しています。「FLACで保存」で1ファイルに保存します。"] = "The entire title is shown as one track. Save FLAC creates one file.",
        ["曲情報とチャプター名を反映しました"] = "Track information and chapter names updated",
        ["再生が終わりました"] = "Playback finished",
        ["一時停止しました"] = "Paused",
        ["再生を停止しました"] = "Playback stopped",
        ["無音の境界候補を探しています"] = "Finding silent split points",
        ["無音候補は見つかりませんでした。時刻を指定して分割できます。"] = "No silent split points found. You can split at a chosen time.",
        ["FLAC に変換しています"] = "Converting to FLAC",
        ["処理を中止しました"] = "Operation canceled",
        ["音声の解析または変換に失敗しました。詳細はログを確認してください。"] = "Audio analysis or conversion failed. Check the log for details.",
        ["短いクリップの繰り返し"] = "Repeated short clips",
        ["深度不明"] = "Unknown bit depth",
        ["可逆圧縮音声"] = "Lossless audio",
        ["圧縮音声"] = "Compressed audio",
        ["全チャンネル"] = "All channels",
        ["実効ゲイン 0%（消音）"] = "Effective gain 0% (muted)",
        ["表で行を選択すると、一括でアーティスト名を設定できます。"] = "Select rows in the table to set their artist together.",
        ["アーティスト名を設定する行を表で選択してください。"] = "Select the rows whose artist you want to set.",
        ["選択行に設定するアーティスト名を入力してください。"] = "Enter an artist for the selected rows.",
        ["アルバム名を入力してください。"] = "Enter an album name.",
        ["編集する行を選択してください。"] = "Select rows to edit.",
        ["編集対象の行が変わりました。画面から開き直してください。"] = "The selected rows changed. Reopen the editor.",
        ["チャプター名の行数がチャプター数と一致しません。"] = "The number of chapter-name lines does not match the chapter count.",
        ["曲名情報の保存範囲が不正です。"] = "The saved track boundaries are invalid.",
        ["ディスク識別情報がありません。"] = "Disc identification is missing.",
        ["再生区間がプレイリストの範囲外です。"] = "Playback range is outside the title.",
        ["再生区間に対応するクリップが不足しています。"] = "The playback range is missing one or more clips.",
        ["ISOファイルが見つかりません。"] = "ISO file not found.",
        ["ISOにドライブ文字が割り当てられていません。"] = "The mounted ISO has no drive letter.",
        ["ISOを自動マウントできませんでした。エクスプローラーでISOをマウントし、表示されたドライブを選んでください。"] = "Could not mount the ISO automatically. Mount it in File Explorer and select its drive.",
        ["ISOを開きましたが、BDMV / AUDIO_TS / VIDEO_TS が見つかりません。"] = "The ISO was opened, but BDMV, AUDIO_TS and VIDEO_TS were not found.",
        ["再生用の ffplay.exe が見つかりません。"] = "ffplay.exe is required for playback but was not found.",
        ["DVD の再生元がありません。"] = "DVD playback source is missing.",
        ["音声プレーヤーを起動できません。"] = "Could not start the audio player.",
        ["音声デコーダーを起動できません。"] = "Could not start the audio decoder.",
        ["DVD の試聴に失敗しました。"] = "DVD preview failed.",
        ["音声プレビューに失敗しました。"] = "Audio preview failed.",
        ["再生音声のサンプルが途中で切れています。"] = "Playback audio samples ended unexpectedly.",
        ["音声デコーダーが失敗しました。"] = "Audio decoder failed.",
        ["保存する曲を選択してください。"] = "Select tracks to save.",
        ["保存する曲またはチャプターを選択してください。"] = "Select tracks or chapters to save.",
        ["選択した音質で保存できない音声です。"] = "This audio cannot be saved at the selected quality.",
        ["DVD のタイトル情報がありません。"] = "DVD title information is missing.",
        ["DVD-Video の IFO ヘッダーが不正です。"] = "Invalid DVD-Video IFO header.",
        ["DVD-Video のタイトル表がありません。"] = "DVD-Video title table is missing.",
        ["DVD-Video のタイトル数が不正です。"] = "Invalid DVD-Video title count.",
        ["DVD-Audio の ATS ヘッダーが不正です。"] = "Invalid DVD-Audio ATS header.",
        ["DVD-Audio のタイトル数が不正です。"] = "Invalid DVD-Audio title count.",
        ["DVD-Audio の曲・セル数が不正です。"] = "Invalid DVD-Audio track or cell count.",
        ["DVD-Audio のセル位置が AOB の範囲外です。"] = "DVD-Audio cell position is outside the AOB file.",
        ["DVD-Audio の曲境界が不正です。"] = "Invalid DVD-Audio track boundary.",
        ["DVD-Audio の曲セクターが重複しています。"] = "DVD-Audio track sectors overlap.",
        ["DVD-Audio IFO の表がファイル範囲外です。"] = "DVD-Audio IFO table is outside the file.",
        ["必要な AOB セクターが見つかりません。"] = "Required AOB sectors were not found.",
        ["DVD-Audio の曲開始位置がありません。"] = "DVD-Audio track start is missing.",
        ["DVD-Audio の曲終了位置がありません。"] = "DVD-Audio track end is missing.",
        ["2ch保存とチャンネル別保存は同時に選べません。"] = "Stereo and per-channel output cannot be selected together.",
        ["この音声のチャンネル配置は個別保存に対応していません。"] = "This channel layout does not support per-channel output.",
        ["この音声のチャンネル配置は2ch変換に対応していません。"] = "This channel layout does not support stereo conversion.",
        ["この音声はCD音質への変換条件を満たしていません。"] = "This audio does not meet the requirements for CD-quality output.",
        ["この音声はハイレゾで保存できません。"] = "This audio cannot be saved at high resolution.",
        ["チャプター情報がありません。"] = "Chapter information is missing.",
        ["出力チャンネルの指定が重複しています。"] = "Output channel was specified more than once.",
        ["FLAC の周波数・ビット深度・チャンネル数が指定と一致しません。"] = "FLAC sample rate, bit depth or channel count does not match the requested output.",
        ["FLAC のチャンネル配置がモノラルではありません。"] = "FLAC output is not mono.",
        ["FLAC のチャンネル配置がステレオではありません。"] = "FLAC output is not stereo.",
        ["FLAC のチャンネル配置が入力と一致しません。"] = "FLAC channel layout does not match the input.",
        ["保存先のファイル名を決められません。"] = "Could not choose an output filename.",
        ["再生可能な DVD-Audio / DVD-Video のタイトルが見つかりません。"] = "No playable DVD-Audio or DVD-Video titles were found.",
        ["このディスクはコピー保護されているため処理できません。"] = "This disc is copy-protected and cannot be processed.",
        ["再生可能なプレイリストを確認できませんでした。"] = "No playable titles were found.",
        ["BDMV、AUDIO_TS、VIDEO_TS が見つかりません。"] = "BDMV, AUDIO_TS or VIDEO_TS was not found.",
        ["対応する音声ストリームがありません。"] = "No supported audio streams were found.",
        ["BDMV/PLAYLIST が見つかりません。非保護の Blu-ray Audio を選んでください。"] = "BDMV/PLAYLIST was not found. Select an unprotected Blu-ray Audio disc.",
        ["MPLS ヘッダーが不正です。"] = "Invalid MPLS header.",
        ["PlayItem 数が不正です。"] = "Invalid PlayItem count.",
        ["PlayItem が途中で切れています。"] = "PlayItem ended unexpectedly.",
        ["Clip ID が不正です。"] = "Invalid clip ID.",
        ["PlayItem の再生区間が不正です。"] = "Invalid PlayItem playback range.",
        ["再生時間が短すぎます。"] = "Playback duration is too short.",
        ["MPLS 内のオフセットが不正です。"] = "Invalid offset in MPLS.",
        ["MPLS が途中で切れています。"] = "MPLS ended unexpectedly.",
        ["内蔵 FFmpeg の識別子が不正です。"] = "Invalid embedded FFmpeg identifier.",
        ["内蔵 FFmpeg を読み込めません。"] = "Could not read embedded FFmpeg.",
        ["ミックス比率は0～100%で指定してください。"] = "Mix levels must be between 0% and 100%.",
        ["選択したチャンネルは音源にありません。"] = "The selected channel is not present in the source.",
        ["このチャンネル配置のステレオミックスには対応していません。"] = "Stereo mixing is not supported for this channel layout.",
        ["FL · 前方左"] = "FL · front left", ["FR · 前方右"] = "FR · front right",
        ["FC · センター"] = "FC · center", ["LFE · 低音"] = "LFE · low frequency",
        ["BL · 後方左"] = "BL · back left", ["BR · 後方右"] = "BR · back right",
        ["SL · 側方左"] = "SL · side left", ["SR · 側方右"] = "SR · side right",
        ["BC · 後方中央"] = "BC · back center",
        ["FLC · 前方左中央"] = "FLC · front left center", ["FRC · 前方右中央"] = "FRC · front right center",
        ["TFL · 上方前左"] = "TFL · top front left", ["TFR · 上方前右"] = "TFR · top front right"
    };

    private static readonly (Regex Pattern, string Replacement)[] Patterns =
    [
        (R(@"^チャプター (\d+): (.*)$"), "Chapter $1: $2"),
        (R(@"^チャプター (\d+)–(\d+) にまたがる曲$"), "Track spanning chapters $1–$2"),
        (R(@"^(\d+)ch をチャンネルごとのモノラル FLAC に保存します。$"), "Save $1ch as separate mono FLAC files."),
        (R(@"^(\d+)ch を設定した比率で2chに変換して FLAC に保存します。$"), "Downmix $1ch to stereo FLAC using the selected levels."),
        (R(@"^(\d+)ch の配置を維持して FLAC に保存します。試聴時は設定した比率でステレオに変換します。$"), "Save the original $1ch layout to FLAC. Preview uses the selected stereo mix."),
        (R(@"^(\d+)ch の配置を維持して FLAC に保存します。試聴時は FFmpeg の標準設定でステレオに変換します。$"), "Save the original $1ch layout to FLAC. Preview uses FFmpeg's default stereo mix."),
        (R(@"^(\d+) 曲・チャプターを表示しています。左側でタイトルを確認してください。$"), "Showing $1 tracks and chapters. Check the titles on the left."),
        (R(@"^(\d+) 曲・チャプターを確認しました$"), "Found $1 tracks and chapters"),
        (R(@"^(\d+) 曲・区間になりました。新しい行の曲名を編集できます。$"), "Now $1 tracks or sections. You can edit the new row's title."),
        (R(@"^(\d+) 曲・区間になりました。$"), "Now $1 tracks or sections."),
        (R(@"^(.*) を選択しました$"), "Selected $1"),
        (R(@"^(.*) を再生しています$"), "Playing $1"),
        (R(@"^(\d+) 件の無音候補が見つかりました。聴いて位置を確認してください。$"), "Found $1 silent split points. Listen and confirm their positions."),
        (R(@"^1チャプター$"), "1 chapter"),
        (R(@"^(\d+)チャプター$"), "$1 chapters"),
        (R(@"^全 (\d+) チャプター。上から1行ずつ入力します。空欄の行は従来の名前で保存します。$"), "$1 chapters. Enter one name per line. Blank lines keep the previous name."),
        (R(@"^全 (\d+) 行を編集$"), "Edit all $1 rows"),
        (R(@"^選択した (\d+) 行を編集$"), "Edit $1 selected rows"),
        (R(@"^表で選択中: (.*) 行目$"), "Selected rows: $1"),
        (R(@"^表で選択中: (\d+) 行$"), "$1 rows selected in the table"),
        (R(@"^実効ゲイン (.*)%（(.*) dB）$"), "Effective gain $1% ($2 dB)"),
        (R(@"^(.*) · ピーク (.*) / 実効 (.*)$"), "$1 · peak $2 / RMS $3"),
        (R(@"^無音付近 · (.*)$"), "Near silence · $1"),
        (R(@"^(\d+) / (\d+) 曲 · (.*) を保存しています$"), "Saving track $1 / $2 · $3"),
        (R(@"^(\d+) / (\d+) 曲 · (.*) を保存しました$"), "Saved track $1 / $2 · $3"),
        (R(@"^(\d+) ファイルを保存しました$"), "Saved $1 files"),
        (R(@"^アルバム名を保存できませんでした: (.*)$"), "Could not save album name: $1"),
        (R(@"^タイトル名を保存できませんでした: (.*)$"), "Could not save title name: $1"),
        (R(@"^曲名情報を保存できませんでした: (.*)$"), "Could not save track information: $1"),
        (R(@"^処理に失敗しました: (.*)$"), "Operation failed: $1"),
        (R(@"^曲名は (\d+) 行必要です。現在は (\d+) 行です。$"), "Expected $1 track-title lines; found $2."),
        (R(@"^チャプター名は (\d+) 行必要です。現在は (\d+) 行です。空欄も1行として残してください。$"), "Expected $1 chapter-name lines; found $2. Keep blank lines."),
        (R(@"^(.*) \(分割\)$"), "$1 (split)"),
        (R(@"^(.*) \(全編\)$"), "$1 (full title)"),
        (R(@"^(.*) が見つかりません。アプリの tools フォルダーに配置してください。$"), "$1 was not found. Place it in the app's tools folder."),
        (R(@"^(.*) を起動できません。$"), "Could not start $1."),
        (R(@"^(.*) が失敗しました。$"), "$1 failed."),
        (R(@"^音声クリップが見つかりません: (.*)$"), "Audio clip not found: $1"),
        (R(@"^クリップ (.*) の音声形式が選択した音声と一致しません。$"), "Clip $1 audio format does not match the selected stream."),
        (R(@"^クリップ (.*) の音声開始時刻が不明です。$"), "Clip $1 audio start time is unknown."),
        (R(@"^クリップ (.*) の(?:再生位置|音声開始位置)が不正です。$"), "Clip $1 playback position is invalid."),
        (R(@"^(.*) の曲境界(?:を確定できません|が不正です)。$"), "Invalid track boundary for $1."),
        (R(@"^(.*) の(?:音声区間|再生区間)が短すぎます。$"), "Audio range for $1 is too short."),
        (R(@"^(.*) に対応する音声クリップがありません。$"), "No audio clips were found for $1."),
        (R(@"^(.*) の音声を最後まで正確に読み取れませんでした。$"), "Could not read all audio for $1 accurately."),
        (R(@"^(.*) の音声区間を正確に読み取れませんでした。$"), "Could not read the audio range for $1 accurately."),
        (R(@"^(.*) の結合後のサンプル数が一致しません。$"), "Combined sample count for $1 does not match."),
        (R(@"^AOB のサイズが不正です: (.*)$"), "Invalid AOB size: $1"),
        (R(@"^AOB を最後まで読めませんでした: (.*)$"), "Could not read the complete AOB file: $1"),
        (R(@"^内蔵ツールに (.*) がありません。$"), "Embedded tool $1 is missing."),
        (R(@"^内蔵ツール (.*) の展開に失敗しました。$"), "Could not extract embedded tool $1.")
    ];

    private static Regex R(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
}

public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LanguageService.IsJapanese))
        {
            Source = LanguageService.Instance,
            Mode = BindingMode.OneWay,
            Converter = new LocalizedTextConverter(Key)
        }.ProvideValue(serviceProvider);

    private sealed class LocalizedTextConverter(string key) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => LanguageService.T(key);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
