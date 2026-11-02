using System.Numerics;
using System.Security.Cryptography;

namespace QualityStudio.Api;

/// <summary>
/// Ed25519 signature verification as RFC 8032 section 5.1.7 specifies it. .NET 10 ships no
/// standalone Ed25519 and the hand-off receiver must not pull a native crypto library into the
/// image for one verification per sign-in, so this follows the RFC's reference algorithm on
/// <see cref="BigInteger"/>. It only verifies public data: no secret key ever reaches it, so the
/// arithmetic not being constant-time leaks nothing.
/// </summary>
internal static class Ed25519
{
    public const int PublicKeyBytes = 32;
    public const int SignatureBytes = 64;

    internal static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    internal static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
    internal static readonly Point BasePoint = BasePointValue();

    internal readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    /// <summary>True only for a canonical signature by <paramref name="publicKey"/> over <paramref name="message"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeyBytes || signature.Length != SignatureBytes) return false;
        if (Decode(publicKey) is not { } a) return false;
        var encodedR = signature[..32];
        if (Decode(encodedR) is not { } r) return false;
        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
        // A scalar at or above the group order is malleable; RFC 8032 requires refusing it.
        if (s >= L) return false;

        var h = HashToScalar(encodedR, publicKey, message);
        return AreEqual(Multiply(s, BasePoint), Add(r, Multiply(h, a)));
    }

    internal static BigInteger HashToScalar(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<byte> message)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        sha.AppendData(first);
        sha.AppendData(second);
        sha.AppendData(message);
        return new BigInteger(sha.GetHashAndReset(), isUnsigned: true, isBigEndian: false) % L;
    }

    internal static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(p.T * 2 * D * q.T);
        var d = Mod(p.Z * 2 * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    internal static Point Multiply(BigInteger scalar, Point point)
    {
        var result = new Point(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);
        while (scalar > 0)
        {
            if (!scalar.IsEven) result = Add(result, point);
            point = Add(point, point);
            scalar >>= 1;
        }
        return result;
    }

    internal static byte[] Encode(Point point)
    {
        var zInverse = Inverse(point.Z);
        var x = Mod(point.X * zInverse);
        var y = Mod(point.Y * zInverse);
        var encoded = new byte[32];
        y.TryWriteBytes(encoded, out _, isUnsigned: true, isBigEndian: false);
        if (!x.IsEven) encoded[31] |= 0x80;
        return encoded;
    }

    private static Point? Decode(ReadOnlySpan<byte> encoded)
    {
        Span<byte> buffer = stackalloc byte[32];
        encoded.CopyTo(buffer);
        var sign = (buffer[31] & 0x80) != 0;
        buffer[31] &= 0x7f;
        var y = new BigInteger(buffer, isUnsigned: true, isBigEndian: false);
        if (RecoverX(y, sign) is not { } x) return null;
        return new Point(x, y, BigInteger.One, Mod(x * y));
    }

    private static BigInteger? RecoverX(BigInteger y, bool sign)
    {
        if (y >= P) return null;
        var x2 = Mod((y * y - 1) * Inverse(D * y * y + 1));
        if (x2.IsZero) return sign ? null : BigInteger.Zero;
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero) x = Mod(x * SqrtMinusOne);
        if (!Mod(x * x - x2).IsZero) return null;
        if (!x.IsEven != sign) x = P - x;
        return x;
    }

    private static bool AreEqual(Point p, Point q) =>
        Mod(p.X * q.Z - q.X * p.Z).IsZero && Mod(p.Y * q.Z - q.Y * p.Z).IsZero;

    private static Point BasePointValue()
    {
        var y = Mod(4 * Inverse(5));
        var x = RecoverX(y, sign: false) ?? throw new InvalidOperationException("The Ed25519 base point is invalid.");
        return new Point(x, y, BigInteger.One, Mod(x * y));
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static BigInteger Mod(BigInteger value)
    {
        var remainder = value % P;
        return remainder.Sign < 0 ? remainder + P : remainder;
    }
}
