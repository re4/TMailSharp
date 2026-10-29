using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TMailSharp.Models;
using TMailSharp.Services;

namespace TMailSharp.Identity;

public sealed class ApiKeyService(IdentityDatabase database)
{
    public const int MaximumActiveKeys = 10;

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<string> CreateAsync(ApplicationUser user, string name, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80) throw new AppException(400, "Choose a key name between 1 and 80 characters.");
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        if (await database.ApiKeys.CountAsync(x => x.UserId == user.Id && x.RevokedAt == null && x.SecurityStamp == user.SecurityStamp, ct) >= MaximumActiveKeys)
            throw new AppException(409, "Revoke an existing key before creating another. You can have up to 10 active keys.");
        var token = "tmk_" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        database.ApiKeys.Add(new ApiKeyRecord
        {
            UserId = user.Id, Name = name, Hash = Hash(token), Prefix = token[..12], SecurityStamp = user.SecurityStamp!
        });
        await database.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return token;
    }

    public async Task<IReadOnlyList<ApiKeySummary>> ListAsync(ApplicationUser user, CancellationToken ct) =>
        await database.ApiKeys.AsNoTracking().Where(x => x.UserId == user.Id)
            .OrderBy(x => x.RevokedAt != null || x.SecurityStamp != user.SecurityStamp).ThenByDescending(x => x.CreatedAt)
            .Take(50).Select(x => new ApiKeySummary(x.Id, x.Name, x.Prefix, x.CreatedAt,
                x.RevokedAt != null ? "Revoked" : x.SecurityStamp != user.SecurityStamp ? "Invalidated" : "Active")).ToListAsync(ct);

    public async Task RevokeAsync(string userId, string id, CancellationToken ct)
    {
        var count = await database.ApiKeys.Where(x => x.UserId == userId && x.Id == id && x.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, DateTime.UtcNow), ct);
        if (count == 0) throw new AppException(404, "API key not found.");
    }

    public async Task<ApiKeyRecord?> FindAsync(string token, CancellationToken ct)
    {
        if (token.Length != 47 || !token.StartsWith("tmk_", StringComparison.Ordinal) ||
            token.AsSpan(4).ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_")) return null;
        var hash = Hash(token);
        return await database.ApiKeys.AsNoTracking().SingleOrDefaultAsync(x => x.Hash == hash && x.RevokedAt == null, ct);
    }
}
