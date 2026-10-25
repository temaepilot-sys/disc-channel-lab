using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PureHDF;

namespace Disc2Flac;

public sealed class BrirRouteRow
{
    public string Channel { get; set; } = "";
    public string File { get; set; } = "";
    public int Measurement { get; set; }
    public int LeftEar { get; set; }
    public int RightEar { get; set; } = 1;
    public BrirRoute Route() => new(Channel.Trim().ToUpperInvariant(), System.IO.Path.GetFullPath(File), Measurement, LeftEar, RightEar);
}

public partial class SpatialLabWindow
{
    readonly ObservableCollection<BrirRouteRow> brirRows = [];
    int brirInspection;
    void InitializeBrir()
    {
        BrirGrid.ItemsSource = brirRows;
        try { if (File.Exists(BrirLibrary.LastProfilePath)) ReadBrir(BrirLibrary.LastProfilePath); }
        catch (Exception ex) { BrirStatus.Text = "Saved BRIR profile was not loaded: " + ex.Message; }
    }
    void ReadBrir(string path)
    {
        var profile = BrirLibrary.ReadProfile(path); BrirName.Text = profile.Name; BrirSource.Text = profile.Source; BrirLicense.Text = profile.License;
        BrirConfirmed.IsChecked = profile.LayoutConfirmed; BrirDryLfe.IsChecked = profile.CenteredDryLfe;
        brirRows.Clear();
        foreach (var route in profile.Routes) brirRows.Add(new() { Channel = route.Channel, File = System.IO.Path.GetFullPath(route.File, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!),
            Measurement = route.Measurement, LeftEar = route.LeftEar, RightEar = route.RightEar });
        BrirStatus.Text = "Profile loaded for review. Validate and apply to use it: " + path;
    }
    BrirProfile EditedBrir()
    {
        if (!BrirGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !BrirGrid.CommitEdit(DataGridEditingUnit.Row, true)) throw new InvalidDataException("Complete the BRIR table edit first.");
        return new(BrirName.Text.Trim(), brirRows.Select(r => r.Route()).ToArray(), BrirSource.Text.Trim(), BrirLicense.Text.Trim(),
            LayoutConfirmed: BrirConfirmed.IsChecked == true, CenteredDryLfe: BrirDryLfe.IsChecked == true);
    }
    void OpenBrir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "BRIR route profile (*.json)|*.json" };
        string presets = System.IO.Path.Combine(AppContext.BaseDirectory, "Presets", "BRIR");
        if (Directory.Exists(presets)) dialog.InitialDirectory = presets;
        if (dialog.ShowDialog(this) != true) return;
        try { ReadBrir(dialog.FileName); } catch (Exception ex) { BrirStatus.Text = ex.Message; }
    }
    void AddBrir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Measured stereo response (*.wav;*.sofa)|*.wav;*.sofa", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) brirRows.Add(new() { File = file });
        BrirConfirmed.IsChecked = false; BrirGrid.SelectedItem = brirRows.Last();
        BrirStatus.Text = "Enter speaker codes explicitly. No assignment is inferred from filenames or track order.";
    }
    void DuplicateBrir_Click(object sender, RoutedEventArgs e)
    {
        if (BrirGrid.SelectedItem is not BrirRouteRow row) return;
        brirRows.Add(new() { File = row.File, Measurement = row.Measurement, LeftEar = row.LeftEar, RightEar = row.RightEar }); BrirConfirmed.IsChecked = false;
    }
    void RemoveBrir_Click(object sender, RoutedEventArgs e) { if (BrirGrid.SelectedItem is BrirRouteRow row) brirRows.Remove(row); }
    void SaveBrir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "BRIR route profile|*.brir.json", FileName = "measured-room.brir.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SpatialJson.Save(dialog.FileName, EditedBrir()); BrirStatus.Text = "Profile saved (not applied): " + dialog.FileName;
            SaveCompletion.Show(this, "BRIRプロファイルを保存しました。", dialog.FileName);
        }
        catch (Exception ex) { BrirStatus.Text = ex.Message; }
    }
    async Task<PreparedBrir> PrepareEditedBrir(CancellationToken ct)
    {
        var profile = EditedBrir();
        string id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(profile, SpatialJson.Options)));
        string path = AppDataPaths.Current.PreferencePath("profiles/brir/" + id + ".json");
        SpatialJson.Save(path, profile);
        try
        {
            var loaded = await BrirLibrary.PrepareAsync(path, token: ct);
            SpatialJson.Save(BrirLibrary.LastProfilePath, profile);
            BrirStatus.Text = $"Validated: {loaded.Profile.Name} · {loaded.Responses.Count} routes · 48 kHz · longest response {loaded.Responses.Values.Max(x => Math.Max(x.Left.Length, x.Right.Length)) / 48000d:0.000}s\nSHA-256 {loaded.Hash}";
            return loaded;
        }
        catch { throw; }
    }
    async void ApplyBrir_Click(object sender, RoutedEventArgs e) => await Job(async ct =>
    {
        StatusText.Text = "Loading and validating measured responses (outside the audio thread)…";
        var brir = await PrepareEditedBrir(ct); await model.ApplySpatialBrirAsync(brir, ct); UpdateActiveHrtf();
        CompareBrir.IsChecked = true;
        StatusText.Text = "Measured BRIR applied. " + (model.IsPlaying ? "Playback continues at the held position. " : "Use Play / Save FLAC in the main window. ") +
            "Head orientation is fixed to the measurements; no HRIR or additional room reflections.";
    }, pauseMainPlayback: false);
    async void PureHrir_Click(object sender, RoutedEventArgs e) => await Job(async ct =>
    {
        await model.ApplySpatialHrtfAsync(ActiveEntry() ?? HrtfEntry.BuiltIn, ct);
        await model.ApplySpatialRoomAsync(new(), ct);
        if (!closed) { UpdateActiveHrtf(); StatusText.Text = "Pure HRIR applied."; }
    }, pauseMainPlayback: false);
    async void CompareBrir_Click(object sender, RoutedEventArgs e)
    {
        FixedYaw.Text = FixedPitch.Text = FixedRoll.Text = "0"; CompareBrir.IsChecked = true; CompareRoom.IsChecked = false;
        Pages.SelectedItem = ComparisonTab; await PrepareComparisonAsync();
    }
    SpatialRenderSnapshot BrirSnapshot(PreparedBrir brir, SpatialRenderSnapshot basis)
    {
        if (Pose() != new HeadPose(0,0,0)) throw new InvalidDataException("Measured BRIR comparison/export requires yaw, pitch and roll = 0. Dynamic head rotation is not synthesized.");
        if (model.IsAtmosStreamingEnabled && model.SelectedAtmosPresentation != AtmosPresentation.Surround51)
            throw new NotSupportedException("On tab 2 (Compare / Export), click 'Use 5.1 for HRIR / BRIR' before using measured speaker BRIR. Stereo bypass and moving Objects are not supported.");
        return basis with { Options = basis.Options with { Method = HeadphoneMethod.MeasuredBrir, BrirPath = brir.Path, BrirHash = brir.Hash, UseTracker = false, Room = new() },
            HrtfHash = brir.Hash, EngineVersion = "SpatialLab BRIR-1 / partitioned FFT 256-1024-4096" };
    }
    async void BrirRoute_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BrirGrid.SelectedItem is not BrirRouteRow route || BrirFileDetails is null) return;
        int request = ++brirInspection;
        try
        {
            var details = await Task.Run(() =>
            {
                if (System.IO.Path.GetExtension(route.File).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return "Stereo WAV · explicit left/right ear indices · response must be no longer than 6 seconds. Leading silence is preserved.\n" + route.File;
                using var h5 = H5File.OpenRead(route.File); var ir = h5.Dataset("Data.IR");
                string result = h5.Attribute("SOFAConventions").Read<string>() + " · Data.IR [" + string.Join(" × ", ir.Space.Dimensions) + "]\n";
                foreach (var name in new[] { "SourcePosition", "ListenerView", "ListenerUp" })
                {
                    try
                    {
                        var d = h5.Dataset(name); var shape = d.Space.Dimensions;
                        if (shape.Length != 2 || shape[1] != 3 || shape[0] > 100000) continue;
                        var data = d.Type.Size == 4 ? d.Read<float[]>().Select(v => (double)v).ToArray() : d.Read<double[]>();
                        int index = shape[0] == 1 ? 0 : route.Measurement;
                        if (index >= 0 && index * 3 + 2 < data.Length) result += name + $" [{index}] = " + string.Join(", ", data.Skip(index * 3).Take(3).Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))) + "\n";
                    }
                    catch (Exception) { result += name + ": unavailable; consult dataset documentation\n"; }
                }
                return result;
            });
            if (!closed && request == brirInspection) BrirFileDetails.Text = details;
        }
        catch (Exception ex) { if (!closed && request == brirInspection) BrirFileDetails.Text = ex.Message; }
    }
}
