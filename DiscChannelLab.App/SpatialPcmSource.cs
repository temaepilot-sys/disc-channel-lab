using System.Buffers.Binary;

namespace Disc2Flac;

/// <summary>Bounded raw PCM reader. Multi-gigabyte ADM files are never expanded into a second float cache.</summary>
public static class SpatialPcmSource
{
    public static Stream Open(SpatialInput input)
    {
        if (input.Progressive is { } live) return live.Open(input);
        if (input.Wave is { } wave) return new AdmFloatStream(wave, input.PcmOffsetFrames, input.Frames);
        var file = new FileStream(input.PcmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        file.Position = checked(input.PcmOffsetFrames * input.StoredChannels * 4); return file;
    }
    sealed class AdmFloatStream : Stream
    {
        readonly FileStream file; readonly AdmWave wave; readonly byte[] raw; long left;
        public AdmFloatStream(AdmWave source, long offset, long frames)
        {
            wave = source; left = frames; raw = new byte[256 * wave.Channels * (wave.Bits / 8)];
            file = new(wave.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            if (file.Length < wave.DataOffset + wave.DataBytes) { file.Dispose(); throw new InvalidDataException("ADM source was truncated; reopen it."); }
            file.Position = checked(wave.DataOffset + offset * wave.Channels * (wave.Bits / 8));
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> target)
        {
            int frames = (int)Math.Min(Math.Min(256, left), target.Length / (wave.Channels * 4));
            if (frames == 0) return 0;
            int size = wave.Bits / 8; file.ReadExactly(raw.AsSpan(0, frames * wave.Channels * size));
            for (int i = 0; i < frames * wave.Channels; i++)
            {
                var p = raw.AsSpan(i * size);
                float sample = wave.Float ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(p)) : wave.Bits switch
                { 16 => BinaryPrimitives.ReadInt16LittleEndian(p) / 32768f, 24 => ((p[0] | p[1] << 8 | p[2] << 16) << 8 >> 8) / 8388608f, _ => BinaryPrimitives.ReadInt32LittleEndian(p) / 2147483648f };
                if (!float.IsFinite(sample)) throw new InvalidDataException("Nonfinite ADM PCM.");
                BinaryPrimitives.WriteSingleLittleEndian(target[(i * 4)..], sample);
            }
            left -= frames; return frames * wave.Channels * 4;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
