using System.Buffers.Binary;
using System.Security.Cryptography;

namespace JIT.NuGetStrongName.Signing;

/// <summary>
///     A strong-name key pair loaded from an .snk file (CAPI PRIVATEKEYBLOB). Key material is kept
///     in memory only and never written to reports or diagnostics.
/// </summary>
public sealed class StrongNameKey
{
    private const byte PrivateKeyBlobType = 0x07;
    private const uint Rsa2Magic = 0x32415352; // "RSA2"
    private const uint CalgRsaSign = 0x00002400;
    private const uint CalgSha1 = 0x00008004;

    private StrongNameKey(byte[] keyBlob, byte[] publicKey)
    {
        KeyBlob = keyBlob;
        PublicKey = publicKey;
        PublicKeyToken = StrongNameIdentity.ComputeToken(publicKey);
    }

    /// <summary>The raw .snk blob passed to the signer.</summary>
    public byte[] KeyBlob { get; }

    /// <summary>The assembly public key (strong-name public key blob with its 12-byte header).</summary>
    public byte[] PublicKey { get; }

    public byte[] PublicKeyToken { get; }

    public string PublicKeyTokenHex => Convert.ToHexStringLower(PublicKeyToken);

    public static StrongNameKey Load(string path)
    {
        byte[] blob;
        try
        {
            blob = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StrongNameException($"Cannot read key file '{path}': {ex.Message}");
        }

        return FromBlob(blob, path);
    }

    public static StrongNameKey FromBlob(byte[] blob, string source = "key blob")
    {
        // BLOBHEADER (8) + RSAPUBKEY (12) + modulus + private parts.
        if (blob.Length < 20 || blob[0] != PrivateKeyBlobType)
        {
            throw new StrongNameException($"'{source}' is not a strong-name key pair (.snk with a private key).");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8));
        int bitLength = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(12));
        uint exponent = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(16));
        int modulusLength = bitLength / 8;
        if (magic != Rsa2Magic || bitLength < 512 || blob.Length < 20 + modulusLength)
        {
            throw new StrongNameException($"'{source}' is not a valid RSA strong-name key pair.");
        }

        var publicKey = new byte[12 + 20 + modulusLength];
        Span<byte> span = publicKey.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, CalgRsaSign);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], CalgSha1);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 20 + modulusLength);
        span[12] = 0x06; // PUBLICKEYBLOB
        span[13] = 0x02;
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], CalgRsaSign);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 0x31415352); // "RSA1"
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], bitLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], exponent);
        blob.AsSpan(20, modulusLength).CopyTo(span[32..]);

        // Reject blobs whose private part cannot actually sign.
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(ToPrivateParameters(blob, bitLength, exponent));
            rsa.SignHash(new byte[20], HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new StrongNameException($"'{source}' does not contain a usable RSA private key.");
        }

        return new StrongNameKey((byte[])blob.Clone(), publicKey);
    }

    private static RSAParameters ToPrivateParameters(byte[] blob, int bitLength, uint exponent)
    {
        int byteLen = bitLength / 8;
        int halfLen = bitLength / 16;
        var offset = 20;

        byte[] Take(int length)
        {
            if (offset + length > blob.Length)
            {
                throw new ArgumentException("Truncated key blob.");
            }

            byte[] value = blob.AsSpan(offset, length).ToArray();
            Array.Reverse(value);
            offset += length;
            return value;
        }

        Span<byte> exponentBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(exponentBytes, exponent);
        return new RSAParameters
               {
                   Exponent = exponentBytes.TrimStart((byte)0).ToArray(),
                   Modulus = Take(byteLen),
                   P = Take(halfLen),
                   Q = Take(halfLen),
                   DP = Take(halfLen),
                   DQ = Take(halfLen),
                   InverseQ = Take(halfLen),
                   D = Take(byteLen),
               };
    }
}

public sealed class StrongNameException(string message) : Exception(message);
