using Disc2Flac;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

internal static class AdmBoundaryTests
{
 public static async Task Run(string output,string fixtureRoot)
 {
  string root=Path.GetFullPath(output),fixtures=Path.GetFullPath(fixtureRoot);Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME",root);
  var checks=new List<string>();void Check(bool b,string s){if(!b)throw new InvalidDataException(s);checks.Add(s);Console.WriteLine("PASS "+s);}
  var album=new AdmAlbumMetadata("Point-source fixture","Synthetic test author",null,[new(0,"First"),new(48000*10,"Second"),new(48000*20,"Third")]);
  string damf=Path.Combine(fixtures,"lamb-first30s.atmos"),wave=Path.Combine(root,"round-trip.wav");
  await DamfImporter.WriteAsync(damf,wave,album,null,CancellationToken.None);var adm=AdmWave.Open(wave);
  Check(AdmWave.HasMetadata(wave)&&adm.Album?.Chapters.Length==3,"Native BW64 retains exact chapter sample positions and labels");
  using(var f=File.OpenRead(wave)){var header=new byte[12];f.ReadExactly(header);Check(System.Text.Encoding.ASCII.GetString(header,0,4)=="BW64","Writer uses BW64/ds64 even for small fixtures, matching the >4 GiB production path");}
  using var expected=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures,"adm-expected-positions.json")));double error=0;
  foreach(var e in expected.RootElement.EnumerateArray()){int track=e.GetProperty("track").GetInt32();long sample=e.GetProperty("sample").GetInt64();var xyz=e.GetProperty("world").EnumerateArray().Select(v=>v.GetSingle()).ToArray();error=Math.Max(error,Vector3.Distance(adm.Scene.Sources.Single(s=>s.TrackIndex==track).At(sample).Position,new(xyz[0],xyz[1],xyz[2])));}
  Check(error<2e-6,$"C# adapter preserves all 4,053 independently generated DAMF trajectory checks (max {error:R})");
  using(var source=File.OpenRead(wave)){source.Position=adm.DataOffset;using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var bytes=new byte[65536];long left=adm.DataBytes;while(left>0){int n=(int)Math.Min(left,bytes.Length);source.ReadExactly(bytes.AsSpan(0,n));hash.AppendData(bytes.AsSpan(0,n));left-=n;}using var prior=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures,"adm-bridge.json")));Check(Convert.ToHexStringLower(hash.GetHashAndReset())==prior.RootElement.GetProperty("pcm24LittleEndianSha256").GetString(),"C# BW64 PCM equals the independently verified decoded CAF samples");}
  string cancelled=Path.Combine(root,"cancelled.wav");using(var cancel=new CancellationTokenSource()){
   bool stopped=false;try{await DamfImporter.WriteAsync(damf,cancelled,album,new Immediate(p=>{if(p.Fraction>.05)cancel.Cancel();}),cancel.Token);}catch(OperationCanceledException){stopped=true;}
   Check(stopped&&!File.Exists(cancelled)&&!Directory.EnumerateFiles(root,"cancelled.wav.*").Any(),"Cancellation during multi-gigabyte copy leaves no completed or partial ADM success file");}
  bool exists=false;try{await DamfImporter.WriteAsync(damf,wave,album,null,CancellationToken.None);}catch(IOException){exists=true;}Check(exists&&AdmWave.Open(wave).DataBytes==adm.DataBytes,"An existing ADM export is preserved");
  string bad=Path.Combine(root,"unsafe.atmos");File.WriteAllText(bad,File.ReadAllText(damf).Replace("audio: lamb-first30s.atmos.audio","audio: ../outside.audio"));
  bool invalid=false;try{await DamfImporter.WriteAsync(bad,Path.Combine(root,"invalid.wav"),album,null,CancellationToken.None);}catch(InvalidDataException){invalid=true;}Check(invalid,"DAMF cannot reference arbitrary files outside its generated sibling folder");
  bool range=false;try{SpatialInput.FromAdm(adm,adm.Scene.DurationSamples,1);}catch(InvalidDataException){range=true;}Check(range,"Out-of-range streaming seek is rejected before device startup");
  SpatialJson.Save(Path.Combine(root,"results.json"),new{passed=checks.Count,checks,error});
 }
 sealed class Immediate(Action<ConversionProgress> action):IProgress<ConversionProgress>{public void Report(ConversionProgress value)=>action(value);}
}
