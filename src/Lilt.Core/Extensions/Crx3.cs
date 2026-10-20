using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lilt.Core.Extensions;

public static partial class ExtensionId
{
    // Chromium's ID: the first 16 bytes of SHA-256(public key), each nibble written as a letter a-p.
    public static string FromPublicKey(ReadOnlySpan<byte> spki) => FromBytes(SHA256.HashData(spki).AsSpan(0, 16));

    public static string FromBytes(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append((char)('a' + (b >> 4))).Append((char)('a' + (b & 15)));
        return sb.ToString();
    }

    [GeneratedRegex("(?<![a-z])[a-p]{32}(?![a-z])")]
    private static partial Regex IdPattern();

    // Accepts a bare ID or any Chrome Web Store URL containing one.
    public static string? Parse(string? input) => input is null ? null : IdPattern().Match(input.Trim()) is { Success: true } m ? m.Value : null;

    public static Uri WebStoreDownload(string id, string browserVersion)
    {
        var major = browserVersion.Split('.')[0];
        return new Uri($"https://clients2.google.com/service/update2/crx?response=redirect&prodversion={major}.0&acceptformat=crx3&x=id%3D{id}%26installsource%3Dondemand%26uc");
    }
}

// Verifies a CRX3 package the way Chromium does for the developer key: the signed header names the
// extension ID, a proof's public key must hash to that ID, and its signature must cover header + zip.
public static class Crx3
{
    public sealed record Package(byte[] Zip, byte[] PublicKey);

    static readonly byte[] Context = "CRX3 SignedData\0"u8.ToArray();

    public static Package Verify(byte[] crx, string expectedId)
    {
        if (crx.Length < 12 || !crx.AsSpan(0, 4).SequenceEqual("Cr24"u8)) throw new InvalidDataException("Not a CRX file.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(crx.AsSpan(4)) != 3) throw new InvalidDataException("Only CRX version 3 is supported.");
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(crx.AsSpan(8));
        if (headerSize > crx.Length - 12) throw new InvalidDataException("Truncated CRX header.");
        var header = crx.AsSpan(12, (int)headerSize).ToArray();
        var zip = crx.AsSpan(12 + (int)headerSize).ToArray();

        var fields = Proto(header);
        var signedData = fields.LastOrDefault(f => f.Field == 10000).Data ?? throw new InvalidDataException("CRX has no signed header.");
        var crxId = Proto(signedData).FirstOrDefault(f => f.Field == 1).Data ?? throw new InvalidDataException("CRX has no ID.");
        var id = ExtensionId.FromBytes(crxId);
        if (id != expectedId) throw new InvalidDataException($"Package is for extension {id}, not {expectedId}.");

        var message = new byte[Context.Length + 4 + signedData.Length + zip.Length];
        Context.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(Context.Length), (uint)signedData.Length);
        signedData.CopyTo(message, Context.Length + 4);
        zip.CopyTo(message, Context.Length + 4 + signedData.Length);

        foreach (var (field, proof) in fields.Where(f => f.Field is 2 or 3))
        {
            var parts = Proto(proof!);
            var key = parts.FirstOrDefault(p => p.Field == 1).Data;
            var signature = parts.FirstOrDefault(p => p.Field == 2).Data;
            if (key is null || signature is null || ExtensionId.FromPublicKey(key) != id) continue;
            if (field == 2 ? VerifyRsa(key, message, signature) : VerifyEcdsa(key, message, signature)) return new(zip, key);
        }
        throw new InvalidDataException("CRX signature check failed.");
    }

    static bool VerifyRsa(byte[] key, byte[] message, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(key, out _);
        return rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    static bool VerifyEcdsa(byte[] key, byte[] message, byte[] signature)
    {
        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(key, out _);
        return ec.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    // Minimal protobuf reader: only length-delimited fields are returned; others are skipped.
    static List<(int Field, byte[]? Data)> Proto(byte[] buf)
    {
        var list = new List<(int, byte[]?)>();
        var i = 0;
        while (i < buf.Length)
        {
            var key = Varint(buf, ref i);
            var field = (int)(key >> 3);
            switch (key & 7)
            {
                case 0: Varint(buf, ref i); break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                case 2:
                    var len = (int)Varint(buf, ref i);
                    if (len < 0 || len > buf.Length - i) throw new InvalidDataException("Malformed CRX header.");
                    list.Add((field, buf.AsSpan(i, len).ToArray()));
                    i += len;
                    break;
                default: throw new InvalidDataException("Malformed CRX header.");
            }
        }
        return list;
    }

    static ulong Varint(byte[] buf, ref int i)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (i >= buf.Length) throw new InvalidDataException("Malformed CRX header.");
            var b = buf[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Malformed CRX header.");
    }
}
