using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using TMailSharp.Configuration;

namespace TMailSharp.Identity;

public static class IdentityInitializer
{
    public static async Task InitializeIdentityAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var database = services.GetRequiredService<IdentityDatabase>();
        await database.Database.EnsureCreatedAsync();
        await EnsureApiKeySchemaAsync(database);
        var keys = services.GetRequiredService<IKeyManager>();
        var compromised = Guid.Parse("cb90e7a2-100d-42a3-bb3a-2412d5fee480");
        if (keys.GetAllKeys().Any(k => k.KeyId == compromised && !k.IsRevoked))
            keys.RevokeKey(compromised, "Publicly disclosed v1.0 key");
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync("Administrator"))
            Require(await roles.CreateAsync(new IdentityRole("Administrator")));
        var settings = services.GetRequiredService<IOptions<AdminOptions>>().Value;
        if (string.IsNullOrWhiteSpace(settings.Password)) return;
        if (settings.Password.Length < 20) throw new InvalidOperationException("The initial administrator password must contain at least 20 characters.");
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var administrator = await users.FindByNameAsync(settings.UserName);
        if (administrator is null)
        {
            administrator = new ApplicationUser { UserName = settings.UserName };
            Require(await users.CreateAsync(administrator, settings.Password));
            Require(await users.AddToRoleAsync(administrator, "Administrator"));
        }
        // Never elevate an existing registered account or reset its password from configuration.
        else if (!await users.IsInRoleAsync(administrator, "Administrator"))
            throw new InvalidOperationException("The administrator name belongs to a non-administrator account.");
    }

    public static Task EnsureApiKeySchemaAsync(IdentityDatabase database) => database.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "ApiKeys" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_ApiKeys" PRIMARY KEY,
            "UserId" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "Hash" TEXT NOT NULL,
            "Prefix" TEXT NOT NULL,
            "SecurityStamp" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "RevokedAt" TEXT NULL,
            CONSTRAINT "FK_ApiKeys_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ApiKeys_Hash" ON "ApiKeys" ("Hash");
        CREATE INDEX IF NOT EXISTS "IX_ApiKeys_UserId" ON "ApiKeys" ("UserId");
        """);

    private static void Require(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}
