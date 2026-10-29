using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using TMailSharp.Configuration;

namespace TMailSharp.Services;

public sealed class ProofOfWork(IDataProtectionProvider protection, IOptions<SecurityCheckOptions> options, TimeProvider clock)
{
    private readonly IDataProtector _protector = protection.CreateProtector("TMailSharp.SecurityCheck.v1");
    private readonly string _instance = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly Dictionary<string, long> _used = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _lastSweep;

    public ProofChallenge Issue(string purpose, string session)
    {
        if (purpose is not ("login" or "register")) throw new ArgumentException("Unknown security-check purpose.", nameof(purpose));
        var expires = clock.GetUtcNow().AddSeconds(options.Value.LifetimeSeconds).ToUnixTimeSeconds();
        var prefix = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var ticket = new Ticket(prefix, session, purpose, options.Value.Difficulty, expires, _instance);
        return new ProofChallenge(_protector.Protect(JsonSerializer.Serialize(ticket)), prefix, ticket.Difficulty,
            DateTimeOffset.FromUnixTimeSeconds(expires));
    }

    public bool TryConsume(string? token, string? nonce, string purpose, string session)
    {
        if (token is null || token.Length > 4096 || nonce is null || nonce.Length is < 1 or > 10 ||
            !uint.TryParse(nonce, NumberStyles.None, CultureInfo.InvariantCulture, out var counter)) return false;
        Ticket? ticket;
        try { ticket = JsonSerializer.Deserialize<Ticket>(_protector.Unprotect(token)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException) { return false; }
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (ticket is null || ticket.Challenge is null || ticket.Instance != _instance || ticket.Session != session ||
            ticket.Purpose != purpose || ticket.ExpiresAt <= now || ticket.Difficulty != options.Value.Difficulty) return false;
        byte[] prefix;
        try { prefix = WebEncoders.Base64UrlDecode(ticket.Challenge); }
        catch (FormatException) { return false; }
        if (prefix.Length != 32) return false;
        Span<byte> input = stackalloc byte[36];
        prefix.CopyTo(input);
        BinaryPrimitives.WriteUInt32BigEndian(input[32..], counter);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        if ((BinaryPrimitives.ReadUInt32BigEndian(hash) >> (32 - ticket.Difficulty)) != 0) return false;
        lock (_gate)
        {
            if (now - _lastSweep >= 30)
            {
                foreach (var expired in _used.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) _used.Remove(expired);
                _lastSweep = now;
            }
            // Reject rather than evict live entries: eviction would allow solved challenges to be replayed.
            if (_used.Count >= 20000 || !_used.TryAdd(ticket.Challenge, ticket.ExpiresAt)) return false;
        }
        return true;
    }

    private sealed record Ticket(string Challenge, string Session, string Purpose, int Difficulty, long ExpiresAt, string Instance);
}

public sealed record ProofChallenge(string Token, string Challenge, int Difficulty, DateTimeOffset ExpiresAt);
