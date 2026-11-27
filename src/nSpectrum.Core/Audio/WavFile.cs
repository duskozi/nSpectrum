using System.Buffers.Binary;
using System.Text;

namespace nSpectrum.Core.Audio;

/// <summary>
/// Minimal RIFF/WAVE reader: PCM 8/16/24/32-bit, IEEE float 32/64-bit, and WAVE_FORMAT_EXTENSIBLE.
/// Used to feed files through the production pipeline in tests. Not a user-facing feature.
/// </summary>
public static class WavFile
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Reads a whole file into interleaved floats in [-1, 1].</summary>
    public static (AudioFormat Format, float[] Samples) Read(string path)
    {
        using var s = File.OpenRead(path);
        return Read(s);
    }

    public static (AudioFormat Format, float[] Samples) Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Tag(r) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
        r.ReadUInt32();
        if (Tag(r) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        ushort format = 0, channels = 0, bits = 0;
        int rate = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = Tag(r);
            uint size = r.ReadUInt32();
            long next = stream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                format = r.ReadUInt16();
                channels = r.ReadUInt16();
                rate = r.ReadInt32();
                r.ReadInt32(); // byte rate
                r.ReadUInt16(); // block align
                bits = r.ReadUInt16();
                if (format == FormatExtensible && size >= 40)
                {
                    r.ReadUInt16(); // cbSize
                    r.ReadUInt16(); // valid bits
                    r.ReadUInt32(); // channel mask
                    format = r.ReadUInt16(); // first two bytes of the sub-format GUID
                }
            }
            else if (id == "data")
            {
                if (rate == 0) throw new InvalidDataException("data chunk before fmt chunk.");
                var af = new AudioFormat(rate, channels);
                af.Validate();
                // Streaming writers leave size at 0 or 0xFFFFFFFF: read to the end in that case.
                long avail = stream.Length - stream.Position;
                long take = size == 0 || size == uint.MaxValue ? avail : Math.Min(size, avail);
                int frameBytes = Math.Max(1, bits / 8) * channels;
                take -= take % frameBytes; // whole frames only, so channels can never slip
                if (take > Array.MaxLength) throw new InvalidDataException("WAV data is too large to load at once.");
                byte[] raw = r.ReadBytes((int)take);
                return (af, Decode(raw, format, bits));
            }
            stream.Position = next;
        }
        throw new InvalidDataException("No data chunk.");
    }

    private static float[] Decode(byte[] raw, ushort format, ushort bits)
    {
        ReadOnlySpan<byte> b = raw;
        switch (format, bits)
        {
            case (FormatPcm, 8):
            {
                // 8-bit WAV is unsigned with 128 as zero.
                var o = new float[b.Length];
                for (int i = 0; i < o.Length; i++) o[i] = (b[i] - 128) / 128f;
                return o;
            }
            case (FormatPcm, 16):
            {
                var o = new float[b.Length / 2];
                for (int i = 0; i < o.Length; i++) o[i] = BinaryPrimitives.ReadInt16LittleEndian(b[(2 * i)..]) / 32768f;
                return o;
            }
            case (FormatPcm, 24):
            {
                var o = new float[b.Length / 3];
                for (int i = 0; i < o.Length; i++)
                {
                    int v = b[3 * i] | (b[3 * i + 1] << 8) | ((sbyte)b[3 * i + 2] << 16);
                    o[i] = v / 8388608f;
                }
                return o;
            }
            case (FormatPcm, 32):
            {
                var o = new float[b.Length / 4];
                for (int i = 0; i < o.Length; i++) o[i] = (float)(BinaryPrimitives.ReadInt32LittleEndian(b[(4 * i)..]) / 2147483648.0);
                return o;
            }
            case (FormatFloat, 32):
            {
                var o = new float[b.Length / 4];
                for (int i = 0; i < o.Length; i++) o[i] = BinaryPrimitives.ReadSingleLittleEndian(b[(4 * i)..]);
                return o;
            }
            case (FormatFloat, 64):
            {
                var o = new float[b.Length / 8];
                for (int i = 0; i < o.Length; i++) o[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(b[(8 * i)..]);
                return o;
            }
            default:
                throw new NotSupportedException($"WAV format {format} with {bits} bits is not supported.");
        }
    }

    private static string Tag(BinaryReader r) => Encoding.ASCII.GetString(r.ReadBytes(4));
}
