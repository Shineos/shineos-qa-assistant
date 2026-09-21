using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ShineosQA.Backend;

/// <summary>サムネイル用の最小PNGエンコーダ（RGBA・フィルタ0）。System.Drawingへの依存を避けるため自前実装</summary>
public static class Png
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] EncodeRgba(byte[] rgba, int width, int height)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("rgba length mismatch");

        var rowBytes = width * 4;
        var raw = new byte[(rowBytes + 1) * height]; // 各行先頭にフィルタ種別0
        for (int y = 0; y < height; y++)
            Array.Copy(rgba, y * rowBytes, raw, y * (rowBytes + 1) + 1, rowBytes);

        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        using var outMs = new MemoryStream();
        outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }); // シグネチャ

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: RGBA
        WriteChunk(outMs, "IHDR", ihdr);
        WriteChunk(outMs, "IDAT", idat);
        WriteChunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    private static void WriteChunk(MemoryStream s, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        s.Write(crc);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
