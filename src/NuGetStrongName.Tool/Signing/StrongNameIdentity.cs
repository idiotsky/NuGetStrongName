using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace JIT.NuGetStrongName.Signing;

public enum SignatureState
{
    /// <summary>The assembly has no public key.</summary>
    Unsigned,

    /// <summary>The signature verifies against the assembly's public key.</summary>
    Valid,

    /// <summary>A public key is present but the signature is absent, zeroed (delay/public signed) or does not verify.</summary>
    Invalid,

    /// <summary>The public key is a platform key (e.g. the ECMA key) whose signature cannot be checked locally.</summary>
    PlatformKey,
}

/// <summary>
///     Strong-name identity helpers: public key tokens and cross-platform signature verification.
///     Parsing uses System.Reflection.Metadata; hashing and RSA use System.Security.Cryptography.
///     The signed-region rules follow ECMA-335 II.6.2.1.3 (as implemented by the CLR and Roslyn).
/// </summary>
public static class StrongNameIdentity
{
    private static readonly byte[] m_ecmaKey = [0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] ComputeToken(ReadOnlySpan<byte> publicKey)
    {
        byte[] hash = SHA1.HashData(publicKey);
        byte[] token = hash.AsSpan(hash.Length - 8).ToArray();
        Array.Reverse(token);
        return token;
    }

    public static SignatureState Verify(PEReader peReader)
    {
        MetadataReader metadata = peReader.GetMetadataReader();
        byte[] publicKey = metadata.GetBlobBytes(metadata.GetAssemblyDefinition().PublicKey);
        if (publicKey.Length == 0)
        {
            return SignatureState.Unsigned;
        }

        if (publicKey.AsSpan().SequenceEqual(m_ecmaKey))
        {
            return SignatureState.PlatformKey;
        }

        CorHeader corHeader = peReader.PEHeaders.CorHeader!;
        DirectoryEntry signatureDirectory = corHeader.StrongNameSignatureDirectory;
        if ((corHeader.Flags & CorFlags.StrongNameSigned) == 0 || signatureDirectory.Size == 0)
        {
            return SignatureState.Invalid;
        }

        if (!TryReadPublicKey(publicKey, out RSAParameters parameters, out HashAlgorithmName hashAlgorithm))
        {
            return SignatureState.Invalid;
        }

        if (!peReader.PEHeaders.TryGetDirectoryOffset(signatureDirectory, out int signatureOffset))
        {
            return SignatureState.Invalid;
        }

        ImmutableArray<byte> image = peReader.GetEntireImage().GetContent();
        if (signatureOffset + signatureDirectory.Size > image.Length)
        {
            return SignatureState.Invalid;
        }

        byte[] signature = image.AsSpan(signatureOffset, signatureDirectory.Size).ToArray();
        Array.Reverse(signature);

        byte[]? hash = ComputeSigningHash(peReader, image.AsSpan(), signatureOffset, signatureDirectory.Size,
            hashAlgorithm);
        if (hash is null)
        {
            return SignatureState.Invalid;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            return rsa.VerifyHash(hash, signature, hashAlgorithm, RSASignaturePadding.Pkcs1)
                ? SignatureState.Valid
                : SignatureState.Invalid;
        }
        catch (CryptographicException)
        {
            return SignatureState.Invalid;
        }
    }

    /// <returns>null when the image is truncated (a section or the headers extend past the end of the file).</returns>
    private static byte[]? ComputeSigningHash(PEReader peReader, ReadOnlySpan<byte> image, int signatureOffset, 
        int signatureSize, HashAlgorithmName algorithm)
    {
        PEHeaders headers = peReader.PEHeaders;
        int optionalHeaderStart = headers.PEHeaderStartOffset;
        bool isPe32Plus = headers.PEHeader!.Magic == PEMagic.PE32Plus;
        int headersEnd = optionalHeaderStart + headers.CoffHeader.SizeOfOptionalHeader
                                             + headers.SectionHeaders.Length * 40;
        int imageLength = image.Length;
        if (headersEnd > imageLength
            || headers.SectionHeaders.Any(s => s.PointerToRawData < 0 ||
                                               (long)s.PointerToRawData + s.SizeOfRawData > imageLength))
        {
            return null;
        }

        using var hash = IncrementalHash.CreateHash(algorithm);

        byte[] headerBytes = image[..headersEnd].ToArray();
        headerBytes.AsSpan(optionalHeaderStart + 64, 4).Clear(); // CheckSum
        headerBytes.AsSpan(optionalHeaderStart + (isPe32Plus ? 144 : 128), 8).Clear(); // Certificate table entry
        hash.AppendData(headerBytes);

        foreach (SectionHeader section in headers.SectionHeaders)
        {
            int start = section.PointerToRawData;
            int end = start + section.SizeOfRawData;
            int sigEnd = signatureOffset + signatureSize;
            if (signatureOffset >= start && sigEnd <= end)
            {
                hash.AppendData(image[start..signatureOffset]);
                hash.AppendData(image[sigEnd..end]);
            }
            else
            {
                hash.AppendData(image[start..end]);
            }
        }

        return hash.GetHashAndReset();
    }

    private static bool TryReadPublicKey(byte[] publicKey, out RSAParameters parameters,
        out HashAlgorithmName hashAlgorithm)
    {
        parameters = default;
        hashAlgorithm = HashAlgorithmName.SHA1;

        // StrongNamePublicKeyBlob header (12) + PUBLICKEYBLOB (BLOBHEADER 8 + RSAPUBKEY 12 + modulus).
        if (publicKey.Length < 32)
        {
            return false;
        }

        Span<byte> span = publicKey.AsSpan();
        hashAlgorithm = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]) switch
        {
            0x800c => HashAlgorithmName.SHA256,
            0x800d => HashAlgorithmName.SHA384,
            0x800e => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA1,
        };

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
        int bitLength = BinaryPrimitives.ReadInt32LittleEndian(span[24..]);
        uint exponent = BinaryPrimitives.ReadUInt32LittleEndian(span[28..]);
        int modulusLength = bitLength / 8;
        if (magic != 0x31415352 || modulusLength == 0 || publicKey.Length < 32 + modulusLength)
        {
            return false;
        }

        byte[] modulus = span.Slice(32, modulusLength).ToArray();
        Array.Reverse(modulus);
        Span<byte> exponentBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(exponentBytes, exponent);
        parameters = new RSAParameters
                     {
                         Modulus = modulus,
                         Exponent = exponentBytes.TrimStart((byte)0).ToArray(),
                     };
        return true;
    }
}
