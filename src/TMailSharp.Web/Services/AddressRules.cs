using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TMailSharp.Services;

public static partial class AddressRules
{
    private static readonly string[] Adjectives = ["quiet", "fresh", "happy", "swift", "mellow", "little", "bright", "kind", "mint", "clear", "soft", "sunny"];
    private static readonly string[] Nouns = ["otter", "fern", "cloud", "wren", "comet", "willow", "river", "panda", "maple", "pebble", "robin", "meadow"];

    public static string RandomLocalPart() => $"{Adjectives[RandomNumberGenerator.GetInt32(Adjectives.Length)]}.{Nouns[RandomNumberGenerator.GetInt32(Nouns.Length)]}.{RandomNumberGenerator.GetInt32(1000, 10000)}";

    public static string NormalizeLocalPart(string localPart)
    {
        var value = localPart.Trim().ToLowerInvariant();
        if (!LocalPartPattern().IsMatch(value) || value.Contains("..", StringComparison.Ordinal))
            throw new AppException(400, "Use 3–40 letters, numbers, dots, underscores, or hyphens. Start and end with a letter or number.");
        if (value is "postmaster" or "abuse" or "admin" or "webmaster" or "security")
            throw new AppException(400, "That address is reserved. Please choose another name.");
        return value;
    }

    public static string NormalizeDomain(string domain)
    {
        string value;
        try { value = new IdnMapping().GetAscii(domain.Trim().TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { throw new AppException(400, "Enter a valid domain, such as inbox.example.com."); }
        if (value.Length > 253 || !DomainPattern().IsMatch(value))
            throw new AppException(400, "Enter a valid domain, such as inbox.example.com.");
        return value;
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,38}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPartPattern();
    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
}

public sealed class AppException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
