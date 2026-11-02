using System.Text;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class Ed25519Tests
{
    // RFC 8032 section 7.1, TEST 1 and TEST 2.
    private static readonly byte[] Rfc1Seed = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
    private static readonly byte[] Rfc1Public = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
    private static readonly byte[] Rfc1Signature = Convert.FromHexString(
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
    private static readonly byte[] Rfc2Seed = Convert.FromHexString("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
    private static readonly byte[] Rfc2Public = Convert.FromHexString("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
    private static readonly byte[] Rfc2Message = [0x72];
    private static readonly byte[] Rfc2Signature = Convert.FromHexString(
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");

    // Produced independently with `openssl pkeyutl -sign -rawin` (OpenSSL 3.0.13) and the RFC TEST 2 key.
    private const string OpenSslMessage = "eyJhbGciOiJFZERTQSJ9.eyJzdWIiOiJ4In0";
    private static readonly byte[] OpenSslSignature = Convert.FromHexString(
        "5f165ce4b3fcfb86717bb74494176199aa15dcd93dbc18d683e27c94b73a4d07c72118c59e45f7f8b3546327be33f86ba17902ab9fe40702f85d403873a1770f");

    [Fact]
    public void Verifies_the_rfc_8032_vectors_and_an_openssl_signature()
    {
        Assert.True(Ed25519.Verify(Rfc1Public, [], Rfc1Signature));
        Assert.True(Ed25519.Verify(Rfc2Public, Rfc2Message, Rfc2Signature));
        Assert.True(Ed25519.Verify(Rfc2Public, Encoding.ASCII.GetBytes(OpenSslMessage), OpenSslSignature));
    }

    [Fact]
    public void Refuses_a_changed_message_key_or_signature()
    {
        Assert.False(Ed25519.Verify(Rfc2Public, [0x73], Rfc2Signature));
        Assert.False(Ed25519.Verify(Rfc1Public, Rfc2Message, Rfc2Signature));
        var tampered = (byte[])Rfc2Signature.Clone();
        tampered[10] ^= 0x01;
        Assert.False(Ed25519.Verify(Rfc2Public, Rfc2Message, tampered));
        Assert.False(Ed25519.Verify(Rfc2Public[..31], Rfc2Message, Rfc2Signature));
        Assert.False(Ed25519.Verify(Rfc2Public, Rfc2Message, Rfc2Signature[..63]));
    }

    [Fact]
    public void Refuses_a_malleable_scalar_at_or_above_the_group_order()
    {
        // S + L verifies under the bare group equation; RFC 8032 requires S < L.
        var signature = (byte[])Rfc2Signature.Clone();
        var s = new System.Numerics.BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: false) + Ed25519.L;
        Assert.True(s.TryWriteBytes(signature.AsSpan(32), out _, isUnsigned: true, isBigEndian: false));
        Assert.False(Ed25519.Verify(Rfc2Public, Rfc2Message, signature));
    }

    [Fact]
    public void The_test_signer_reproduces_the_rfc_signatures()
    {
        var first = new TestEd25519Key(Rfc1Seed);
        Assert.Equal(Rfc1Public, first.PublicKey);
        Assert.Equal(Rfc1Signature, first.Sign([]));
        var second = new TestEd25519Key(Rfc2Seed);
        Assert.Equal(Rfc2Public, second.PublicKey);
        Assert.Equal(Rfc2Signature, second.Sign(Rfc2Message));
    }
}
