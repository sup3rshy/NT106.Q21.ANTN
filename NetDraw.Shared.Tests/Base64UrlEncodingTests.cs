using NetDraw.Shared.Protocol;
using Xunit;

namespace NetDraw.Shared.Tests;

public class Base64UrlEncodingTests
{
    [Fact]
    public void TryDecode_NullInput_ReturnsNull()
    {
        Assert.Null(Base64UrlEncoding.TryDecode(null));
    }

    [Fact]
    public void TryDecode_EmptyString_ReturnsNull()
    {
        Assert.Null(Base64UrlEncoding.TryDecode(""));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void TryDecode_Whitespace_ReturnsNull(string input)
    {
        Assert.Null(Base64UrlEncoding.TryDecode(input));
    }

    [Theory]
    [InlineData("AAA*")]   // outside alphabet
    [InlineData("AAA+")]   // standard b64 char, not url alphabet variant we read raw
    [InlineData("a b c")]  // spaces inside
    public void TryDecode_OutOfAlphabet_ReturnsNull(string input)
    {
        Assert.Null(Base64UrlEncoding.TryDecode(input));
    }

    [Fact]
    public void TryDecode_BadPadding_ReturnsNull()
    {
        // length % 4 == 1 is never a valid base64 / base64url length.
        Assert.Null(Base64UrlEncoding.TryDecode("A"));
        Assert.Null(Base64UrlEncoding.TryDecode("AAAAA"));
    }

    [Fact]
    public void EncodeThenDecode_RoundTrips()
    {
        var bytes = new byte[32];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7 + 3);

        var encoded = Base64UrlEncoding.Encode(bytes);
        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);

        var decoded = Base64UrlEncoding.TryDecode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(bytes, decoded);
    }

    [Fact]
    public void TryDecode_HandlesBase64UrlAlphabet()
    {
        // bytes that produce '-' and '_' under base64url
        var bytes = new byte[] { 0xfb, 0xff, 0xbf };
        var encoded = Base64UrlEncoding.Encode(bytes);
        Assert.Contains('-', encoded + "_");
        var decoded = Base64UrlEncoding.TryDecode(encoded);
        Assert.Equal(bytes, decoded);
    }
}
