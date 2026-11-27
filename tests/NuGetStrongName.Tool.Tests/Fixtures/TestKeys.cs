using System.Buffers.Binary;
using System.Security.Cryptography;

namespace JIT.NuGetStrongName.Tool.Tests.Fixtures;

/// <summary>Generates throw-away strong-name key pairs (CAPI PRIVATEKEYBLOB, the .snk format) for tests.</summary>
public static class TestKeys
{
    private static readonly Lazy<byte[]> m_signing = new(() => Create(2048));
    private static readonly Lazy<byte[]> m_vendor = new(() => Create(1024));

    public static byte[] SigningKey => m_signing.Value;

    public static byte[] VendorKey => m_vendor.Value;

    private static byte[] Create(int bits)
    {
        using var rsa = RSA.Create(bits);
        RSAParameters p = rsa.ExportParameters(true);
        int byteLen = bits / 8;
        int halfLen = bits / 16;
        var blob = new byte[20 + byteLen + halfLen * 5 + byteLen];
        Span<byte> span = blob.AsSpan();
        span[0] = 0x07; // PRIVATEKEYBLOB
        span[1] = 0x02;
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 0x00002400); // CALG_RSA_SIGN
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x32415352); // RSA2
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], bits);
        var exponent = new byte[4];
        p.Exponent!.CopyTo(exponent, 4 - p.Exponent.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], BinaryPrimitives.ReadUInt32BigEndian(exponent));
        var offset = 20;

        void Put(byte[] value, int length)
        {
            byte[] little = value.Reverse().ToArray();
            little.CopyTo(blob, offset);
            offset += length;
        }

        Put(p.Modulus!, byteLen);
        Put(p.P!, halfLen);
        Put(p.Q!, halfLen);
        Put(p.DP!, halfLen);
        Put(p.DQ!, halfLen);
        Put(p.InverseQ!, halfLen);
        Put(p.D!, byteLen);
        return blob;
    }
}
