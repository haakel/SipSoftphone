using SipSoftphone;
using Xunit;

namespace SipSoftphone.Tests;

public class PhoneNormalizerTests
{
    [Theory]
    [InlineData("+989121234567", "9121234567")]
    [InlineData("00989121234567", "9121234567")]
    [InlineData("09121234567", "9121234567")]
    [InlineData("9121234567", "9121234567")]
    [InlineData("02112345678", "02112345678")]
    [InlineData("989121234567", "9121234567")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("abc", "")]
    public void Normalize_matches_melipayamak_rules(string? raw, string expected)
        => Assert.Equal(expected, PhoneNormalizer.Normalize(raw));

    [Fact]
    public void Normalize_strips_formatting_characters()
        => Assert.Equal("9121234567", PhoneNormalizer.Normalize("0912 123-4567"));

    [Fact]
    public void Normalize_keeps_short_numbers()
        => Assert.Equal("123", PhoneNormalizer.Normalize("123"));
}
