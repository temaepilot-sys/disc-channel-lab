using System.Text;

namespace Disc2Flac;

/// <summary>Original synthesized test material: no third-party recordings or proprietary metadata.</summary>
public static class AdmDemo
{
    public static void Create(string path)
    {
        const int rate = 48000, seconds = 12, channels = 3;
        string xml = """
        <ebuCoreMain xmlns="urn:ebu:metadata-schema:ebuCore_2017"><coreMetadata><format><audioFormatExtended version="ITU-R_BS.2076-2">
        <audioProgramme audioProgrammeID="APR_1001" audioProgrammeName="Spatial Lab: stereo bed + moving object"><audioContentIDRef>ACO_1001</audioContentIDRef></audioProgramme>
        <audioContent audioContentID="ACO_1001" audioContentName="Original test scene"><audioObjectIDRef>AO_1001</audioObjectIDRef><audioObjectIDRef>AO_1002</audioObjectIDRef></audioContent>
        <audioObject audioObjectID="AO_1001" audioObjectName="Stereo bed"><audioPackFormatIDRef>AP_00010002</audioPackFormatIDRef><audioTrackUIDRef>ATU_00000001</audioTrackUIDRef><audioTrackUIDRef>ATU_00000002</audioTrackUIDRef></audioObject>
        <audioObject audioObjectID="AO_1002" audioObjectName="Moving chime" start="00:00:01.00000" duration="00:00:10.00000"><audioPackFormatIDRef>AP_00031001</audioPackFormatIDRef><audioTrackUIDRef>ATU_00000003</audioTrackUIDRef></audioObject>
        <audioPackFormat audioPackFormatID="AP_00031001" audioPackFormatName="Point object" typeDefinition="Objects" typeLabel="0003"><audioChannelFormatIDRef>AC_00031001</audioChannelFormatIDRef></audioPackFormat>
        <audioChannelFormat audioChannelFormatID="AC_00031001" audioChannelFormatName="Moving source" typeDefinition="Objects" typeLabel="0003">
         <audioBlockFormat audioBlockFormatID="AB_00031001_00000001" rtime="00:00:00.00000" duration="00:00:02.00000"><position coordinate="azimuth">60</position><position coordinate="elevation">0</position><jumpPosition>1</jumpPosition></audioBlockFormat>
         <audioBlockFormat audioBlockFormatID="AB_00031001_00000002" rtime="00:00:02.00000" duration="00:00:02.00000"><position coordinate="azimuth">-60</position><position coordinate="elevation">0</position><jumpPosition>0</jumpPosition></audioBlockFormat>
         <audioBlockFormat audioBlockFormatID="AB_00031001_00000003" rtime="00:00:04.00000" duration="00:00:02.00000"><position coordinate="azimuth">180</position><position coordinate="elevation">0</position><jumpPosition>1</jumpPosition><gain>0.7</gain></audioBlockFormat>
         <audioBlockFormat audioBlockFormatID="AB_00031001_00000004" rtime="00:00:06.00000" duration="00:00:02.00000"><position coordinate="azimuth">0</position><position coordinate="elevation">75</position><jumpPosition interpolationLength="00:00:01.00000">1</jumpPosition></audioBlockFormat>
         <audioBlockFormat audioBlockFormatID="AB_00031001_00000005" rtime="00:00:08.00000" duration="00:00:02.00000"><position coordinate="azimuth">0</position><position coordinate="elevation">0</position><jumpPosition>0</jumpPosition></audioBlockFormat>
        </audioChannelFormat>
        <audioStreamFormat audioStreamFormatID="AS_00031001" audioStreamFormatName="PCM object" formatLabel="0001" formatDefinition="PCM"><audioChannelFormatIDRef>AC_00031001</audioChannelFormatIDRef><audioTrackFormatIDRef>AT_00031001_01</audioTrackFormatIDRef></audioStreamFormat>
        <audioTrackFormat audioTrackFormatID="AT_00031001_01" audioTrackFormatName="PCM object" formatLabel="0001" formatDefinition="PCM"><audioStreamFormatIDRef>AS_00031001</audioStreamFormatIDRef></audioTrackFormat>
        <audioTrackUID UID="ATU_00000001"><audioTrackFormatIDRef>AT_00010001_01</audioTrackFormatIDRef><audioPackFormatIDRef>AP_00010002</audioPackFormatIDRef></audioTrackUID>
        <audioTrackUID UID="ATU_00000002"><audioTrackFormatIDRef>AT_00010002_01</audioTrackFormatIDRef><audioPackFormatIDRef>AP_00010002</audioPackFormatIDRef></audioTrackUID>
        <audioTrackUID UID="ATU_00000003"><audioTrackFormatIDRef>AT_00031001_01</audioTrackFormatIDRef><audioPackFormatIDRef>AP_00031001</audioPackFormatIDRef></audioTrackUID>
        </audioFormatExtended></format></coreMetadata></ebuCoreMain>
        """;
        using var stream = new FileStream(path, FileMode.CreateNew); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(0u); writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        void Chunk(string id, byte[] bytes) { writer.Write(Encoding.ASCII.GetBytes(id)); writer.Write((uint)bytes.Length); writer.Write(bytes); if ((bytes.Length & 1) != 0) writer.Write((byte)0); }
        using var fmt = new MemoryStream(); using (var w = new BinaryWriter(fmt, Encoding.ASCII, true)) { w.Write((ushort)3); w.Write((ushort)channels); w.Write(rate); w.Write(rate * channels * 4); w.Write((ushort)(channels * 4)); w.Write((ushort)32); }
        Chunk("fmt ", fmt.ToArray()); Chunk("axml", Encoding.UTF8.GetBytes(xml));
        using var chna = new MemoryStream();
        using (var w = new BinaryWriter(chna, Encoding.ASCII, true))
        {
            w.Write((ushort)channels); w.Write((ushort)channels);
            for (int i = 1; i <= channels; i++)
            {
                w.Write((ushort)i); w.Write(Encoding.ASCII.GetBytes($"ATU_{i:00000000}"));
                w.Write(Encoding.ASCII.GetBytes(i == 3 ? "AT_00031001_01" : $"AT_0001000{i}_01"));
                w.Write(Encoding.ASCII.GetBytes(i == 3 ? "AP_00031001" : "AP_00010002")); w.Write((byte)0);
            }
        }
        Chunk("chna", chna.ToArray());
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write((uint)(rate * seconds * channels * 4));
        for (int frame = 0; frame < rate * seconds; frame++)
        {
            double t = frame / (double)rate, edge = Math.Min(1, Math.Min(t, seconds - t) / .03);
            writer.Write((float)(.035 * Math.Sin(2 * Math.PI * 220 * t) * edge));
            writer.Write((float)(.035 * Math.Sin(2 * Math.PI * 330 * t) * edge));
            double pulse = Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * 2 * t)), 3);
            writer.Write((float)(.15 * (Math.Sin(2 * Math.PI * 660 * t) + .3 * Math.Sin(2 * Math.PI * 2100 * t)) * pulse * edge));
        }
        long length = stream.Length; stream.Position = 4; writer.Write((uint)(length - 8));
    }
}
