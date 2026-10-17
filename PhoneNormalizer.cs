namespace SipSoftphone;

/// <summary>
/// Same normalization rules as Melipayamak Auto Search extension (normalizePhone in content.js):
///   +989121234567   -> 9121234567
///   00989121234567  -> 9121234567
///   09121234567     -> 9121234567
///   9121234567      -> 9121234567
///   02112345678     -> 02112345678 (landline stays)
/// </summary>
public static class PhoneNormalizer
{
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var hasPlus = raw.Contains('+');
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return "";

        if ((hasPlus || digits.StartsWith("00")) && digits.StartsWith("98") && digits.Length >= 12)
            digits = digits[2..];
        else if (digits.StartsWith("0098") && digits.Length >= 14)
            digits = digits[4..];
        else if (digits.StartsWith("98") && digits.Length >= 12)
            digits = digits[2..];
        else if (digits.StartsWith("09") && digits.Length == 11)
            digits = digits[1..];

        return digits;
    }
}
