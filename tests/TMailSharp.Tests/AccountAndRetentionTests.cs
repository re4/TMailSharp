using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using TMailSharp.Data;
using TMailSharp.Identity;

namespace TMailSharp.Tests;

public sealed partial class MailAppTests
{
    private const string UserPassword = "a-long-unique-test-password-2026";

    private static async Task Register(HttpClient client, string userName)
    {
        using var result = await Form(client, "/account/register", ("UserName", userName), ("Password", UserPassword), ("ConfirmPassword", UserPassword));
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        await RefreshToken(client);
    }

    [Fact]
    public async Task PermanentMailboxesSurviveNewLoginAndStayReservedToTheirOwner()
    {
        using var alice = await Client();
        var guest = await CreateMailbox(alice);
        await Register(alice, "alice");
        var claimed = await Json(await alice.GetAsync("/inbox/bootstrap"));
        Assert.Contains(claimed.GetProperty("mailboxes").EnumerateArray(), box => box.GetProperty("id").GetString() == guest.GetProperty("id").GetString());
        var mailbox = await Json(await alice.PostAsJsonAsync("/inbox/mailboxes", new { localPart = "reserved-alice", domain = "drop.localhost", permanent = true }));
        Assert.True(mailbox.GetProperty("isPermanent").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, mailbox.GetProperty("expiresAt").ValueKind);
        using var otherDevice = await Client();
        await Login(otherDevice, "alice", UserPassword);
        var id = mailbox.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await otherDevice.GetAsync($"/inbox/mailboxes/{id}/messages")).StatusCode);
        using var bob = await Client();
        await Register(bob, "bob");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/inbox/mailboxes/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/inbox/mailboxes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsJsonAsync("/inbox/mailboxes", new { localPart = "reserved-alice", domain = "drop.localhost", permanent = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/inbox/mailboxes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsJsonAsync("/inbox/mailboxes", new { localPart = "reserved-alice", domain = "drop.localhost", permanent = true })).StatusCode);
    }

    [Fact]
    public async Task ReadingAndReopeningNeverDeletesMessagesOrChangesTheirExpiry()
    {
        using var client = await Client();
        var mailbox = await CreateMailbox(client);
        var id = mailbox.GetProperty("id").GetString();
        await Json(await client.PostAsync($"/inbox/mailboxes/{id}/sample", null));
        var stored = await Store.Messages.Find(x => x.MailboxId == id).SingleAsync();
        for (var i = 0; i < 3; i++)
        {
            var detail = await Json(await client.GetAsync($"/inbox/mailboxes/{id}/messages/{stored.Id}"));
            Assert.True(detail.GetProperty("isRead").GetBoolean());
            Assert.Contains("482 916", detail.GetProperty("textBody").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/inbox/mailboxes/{id}/messages/{stored.Id}/content")).StatusCode);
            var listing = await Json(await client.GetAsync($"/inbox/mailboxes/{id}/messages"));
            Assert.Single(listing.GetProperty("messages").EnumerateArray());
        }
        var after = await Store.Messages.Find(x => x.Id == stored.Id).SingleAsync();
        Assert.Equal(stored.ExpiresAt, after.ExpiresAt);
        Assert.True(after.IsRead);
        Assert.Equal(1, (await Store.Mailboxes.Find(x => x.Id == id).SingleAsync()).MessageCount);
    }

    [Fact]
    public async Task RetentionClearsMailWithoutReleasingPermanentAddresses()
    {
        using var client = await Client();
        await Login(client, "admin", AdminPassword);
        var mailbox = await Json(await client.PostAsJsonAsync("/inbox/mailboxes", new { domain = "drop.localhost", permanent = true }));
        var id = mailbox.GetProperty("id").GetString();
        await Json(await client.PostAsync($"/inbox/mailboxes/{id}/sample", null));
        await Store.Messages.UpdateManyAsync(x => x.MailboxId == id, Builders<MessageDocument>.Update.Set(x => x.ReceivedAt, DateTime.UtcNow.AddMinutes(-2)));
        Assert.Equal(HttpStatusCode.Redirect, (await Form(client, "/admin/retention", ("minutes", "1"))).StatusCode);
        var listing = await Json(await client.GetAsync($"/inbox/mailboxes/{id}/messages"));
        Assert.Empty(listing.GetProperty("messages").EnumerateArray());
        var persisted = await Store.Mailboxes.Find(x => x.Id == id).SingleAsync();
        Assert.True(persisted.IsPermanent);
        Assert.Null(persisted.ExpiresAt);
        Assert.Equal(0, persisted.MessageCount);
        Assert.NotNull(await Store.FindRecipientAsync(persisted.Address, default));
    }

    [Fact]
    public async Task DeletedDomainsStayDeletedAndCannotReceiveNewMail()
    {
        using var client = await Client();
        await Login(client, "admin", AdminPassword);
        var mailbox = await CreateMailbox(client);
        foreach (var domain in await Store.GetDomainsAsync(true))
            Assert.Equal(HttpStatusCode.Redirect, (await Form(client, "/admin/domains/delete", ("name", domain.Name))).StatusCode);
        await Store.InitializeAsync();
        Assert.Empty(await Store.GetDomainsAsync(true));
        Assert.Null(await Store.FindRecipientAsync(mailbox.GetProperty("address").GetString()!, default));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/inbox/mailboxes", new { domain = "drop.localhost" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/inbox/mailboxes/{mailbox.GetProperty("id").GetString()}/messages")).StatusCode);
    }

    [Fact]
    public async Task RegistrationCannotGrantAdminAndUnsafeRequestsRequireAntiforgeryTokens()
    {
        using var guest = await Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync("/inbox/mailboxes", new { permanent = true })).StatusCode);
        var reserved = await Form(guest, "/account/register", ("UserName", "admin"), ("Password", UserPassword), ("ConfirmPassword", UserPassword));
        Assert.Contains("reserved", await reserved.Content.ReadAsStringAsync());
        await Register(guest, "ordinary-user");
        var denied = await guest.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("/account/denied", denied.Headers.Location!.ToString());
        guest.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PostAsJsonAsync("/inbox/mailboxes", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Form(guest, "/account/logout")).StatusCode);
    }

    [Fact]
    public async Task AuthenticatorIsRequiredAfterPasswordAndRecoveryCodesAreSingleUse()
    {
        using var client = await Client();
        await Register(client, "two-factor-user");
        string key;
        string recovery;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByNameAsync("two-factor-user"))!;
            Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
            key = (await users.GetAuthenticatorKeyAsync(user))!;
        }
        var enabled = await Form(client, "/account/authenticator/enable", ("Code", Totp(key)));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByNameAsync("two-factor-user"))!;
            recovery = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 1))!.Single();
        }
        await client.PostAsync("/account/logout", null);
        await RefreshToken(client);
        var login = await Form(client, "/account/login", ("UserName", "two-factor-user"), ("Password", UserPassword));
        Assert.Contains("two-factor", login.Headers.Location!.ToString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await Json(await client.GetAsync("/inbox/bootstrap"))).GetProperty("user").ValueKind);
        Assert.Equal(HttpStatusCode.Redirect, (await Form(client, "/account/two-factor", ("Code", recovery), ("RecoveryCode", "true"))).StatusCode);
        await RefreshToken(client);
        Assert.Equal("two-factor-user", (await Json(await client.GetAsync("/inbox/bootstrap"))).GetProperty("user").GetProperty("name").GetString());
        using var second = await Client();
        await Form(second, "/account/login", ("UserName", "two-factor-user"), ("Password", UserPassword));
        Assert.Equal(HttpStatusCode.OK, (await Form(second, "/account/two-factor", ("Code", recovery), ("RecoveryCode", "true"))).StatusCode);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await Json(await second.GetAsync("/inbox/bootstrap"))).GetProperty("user").ValueKind);
    }

    private static string Totp(string key)
    {
        var bits = 0; var value = 0; var bytes = new List<byte>();
        foreach (var ch in key.TrimEnd('='))
        {
            value = (value << 5) | "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(char.ToUpperInvariant(ch)); bits += 5;
            if (bits >= 8) { bytes.Add((byte)(value >> (bits -= 8))); }
        }
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var digest = HMACSHA1.HashData(bytes.ToArray(), counter); var offset = digest[^1] & 15;
        var code = ((digest[offset] & 127) << 24) | (digest[offset + 1] << 16) | (digest[offset + 2] << 8) | digest[offset + 3];
        return (code % 1_000_000).ToString("D6");
    }

    [Fact]
    public async Task UpgradeTransfersLegacyGuestOwnershipToProtectedSession()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var mailbox = await Store.CreateMailboxAsync(TMailSharp.Services.AddressRules.Hash(token), null, "drop.localhost", default);
        using var client = await Client();
        client.DefaultRequestHeaders.Add("Cookie", "tmailsharp.session=" + token);
        var bootstrap = await Json(await client.GetAsync("/inbox/bootstrap"));
        Assert.Contains(bootstrap.GetProperty("mailboxes").EnumerateArray(), box => box.GetProperty("id").GetString() == mailbox.Id);
        client.DefaultRequestHeaders.Remove("Cookie");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/inbox/mailboxes/{mailbox.Id}/messages")).StatusCode);
        using var stranger = await Client();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/inbox/mailboxes/{mailbox.Id}/messages")).StatusCode);
    }

    [Fact]
    public async Task DomainRetentionOverridesDefaultAndCanReturnToInheritedSchedule()
    {
        await Store.SetRetentionAsync(90, null, default);
        await Store.SetRetentionAsync(15, "drop.localhost", default);
        var mailbox = await Store.CreateMailboxAsync("user:retention-test", null, "drop.localhost", default, true);
        var message = new MessageDocument { DeliveryHash = "retention-check", ReceivedAt = DateTime.UtcNow };
        Assert.True(await Store.SaveMessageAsync(mailbox, message, default));
        var stored = await Store.Messages.Find(x => x.Id == message.Id).SingleAsync();
        Assert.InRange((stored.ExpiresAt - stored.ReceivedAt).TotalMinutes, 14.99, 15.01);
        await Store.SetRetentionAsync(120, null, default);
        stored = await Store.Messages.Find(x => x.Id == message.Id).SingleAsync();
        Assert.InRange((stored.ExpiresAt - stored.ReceivedAt).TotalMinutes, 14.99, 15.01);
        await Store.ResetDomainRetentionAsync("drop.localhost", default);
        stored = await Store.Messages.Find(x => x.Id == message.Id).SingleAsync();
        Assert.InRange((stored.ExpiresAt - stored.ReceivedAt).TotalMinutes, 119.99, 120.01);
    }
}
