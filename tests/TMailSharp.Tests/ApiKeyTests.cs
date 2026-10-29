using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TMailSharp.Identity;

namespace TMailSharp.Tests;

public sealed partial class MailAppTests
{
    private HttpClient ApiClient(string? key = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("https://localhost")
        });
        if (key is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static async Task<string> CreateKey(HttpClient client, string name = "Integration", string password = UserPassword)
    {
        using var response = await Form(client, "/account/api-keys", ("Input.Name", name), ("Input.Password", password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(body, "<code id=\"created-api-key\">([^<]+)</code>");
        Assert.True(match.Success, "The account page did not issue an API key.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [Fact]
    public async Task PublicApiRequiresKeyEvenWithSignedInCookieOrBrowserHeaders()
    {
        using var visitor = ApiClient();
        using var denied = await visitor.GetAsync("/api/bootstrap");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("Bearer", Assert.Single(denied.Headers.WwwAuthenticate).Scheme);
        Assert.Contains("API key is required", await denied.Content.ReadAsStringAsync());
        Assert.False(denied.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.PostAsJsonAsync("/api/mailboxes", new { })).StatusCode);
        using var browser = await Client();
        await Register(browser, "cookieonly");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/bootstrap")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("/api/mailboxes", new { permanent = true })).StatusCode);
        var websiteMailbox = await CreateMailbox(browser);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync($"/api/mailboxes/{websiteMailbox.GetProperty("id").GetString()}/reserve", null)).StatusCode);
        var key = await CreateKey(browser);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync("/api/bootstrap?api_key=" + key)).StatusCode);
        visitor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "tmk_" + new string('x', 43));
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync("/api/bootstrap")).StatusCode);
    }

    [Fact]
    public async Task ApiKeySupportsMailLifecycleWithoutCookiesOrCsrfAndIsolatesOwners()
    {
        using var alice = await Client();
        using var bob = await Client();
        await Register(alice, "aliceapi");
        await Register(bob, "bobapi");
        var aliceKey = await CreateKey(alice);
        using var api = ApiClient(aliceKey);
        using var other = ApiClient(await CreateKey(bob));
        using var bootstrapResponse = await api.GetAsync("/api/bootstrap");
        var bootstrap = await Json(bootstrapResponse);
        Assert.False(bootstrap.TryGetProperty("csrfToken", out _));
        Assert.False(bootstrap.TryGetProperty("user", out _));
        Assert.False(bootstrapResponse.Headers.Contains("Set-Cookie"));
        var mailbox = await Json(await api.PostAsJsonAsync("/api/mailboxes", new { domain = "drop.localhost" }));
        var id = mailbox.GetProperty("id").GetString();
        var reserved = await Json(await api.PostAsync($"/api/mailboxes/{id}/reserve", null));
        Assert.True(reserved.GetProperty("isPermanent").GetBoolean());
        await Json(await api.PostAsync($"/api/mailboxes/{id}/sample", null));
        var listing = await Json(await api.GetAsync($"/api/mailboxes/{id}/messages"));
        var messageId = Assert.Single(listing.GetProperty("messages").EnumerateArray()).GetProperty("id").GetString();
        var detail = await Json(await api.GetAsync($"/api/mailboxes/{id}/messages/{messageId}"));
        var attachmentId = Assert.Single(detail.GetProperty("attachments").EnumerateArray()).GetProperty("id").GetString();
        var attachment = $"/api/mailboxes/{id}/messages/{messageId}/attachments/{attachmentId}";
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync(attachment)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(attachment)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/mailboxes/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/mailboxes/{id}/messages/{messageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/mailboxes/{id}")).StatusCode);
        bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceKey);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/mailboxes/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/inbox/mailboxes/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/mailboxes/{id}/messages/{messageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/mailboxes/{id}")).StatusCode);
    }

    [Fact]
    public async Task ApiKeysAreHashedAndShownOnlyOnceAndRevocationIsImmediate()
    {
        using var owner = await Client();
        using var stranger = await Client();
        await Register(owner, "keyowner");
        await Register(stranger, "keystranger");
        var first = await CreateKey(owner, "First key");
        var second = await CreateKey(owner, "Second key");
        Assert.DoesNotContain(first, await owner.GetStringAsync("/account/api-keys"));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDatabase>();
        var record = await db.ApiKeys.AsNoTracking().SingleAsync(x => x.Name == "First key");
        Assert.Equal(ApiKeyService.Hash(first), record.Hash);
        Assert.NotEqual(first, record.Hash);
        Assert.Equal(first[..12], record.Prefix);
        Assert.Equal(HttpStatusCode.NotFound, (await Form(stranger, $"/account/api-keys/{record.Id}/revoke")).StatusCode);
        using var api = ApiClient(first);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/bootstrap")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Form(owner, $"/account/api-keys/{record.Id}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/bootstrap")).StatusCode);
        using var remaining = ApiClient(second);
        Assert.Equal(HttpStatusCode.OK, (await remaining.GetAsync("/api/bootstrap")).StatusCode);
    }

    [Fact]
    public async Task IssuingKeysRequiresAccountPasswordAndAntiforgery()
    {
        using var visitor = await Client();
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync("/account/api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Form(visitor, "/account/api-keys", ("Input.Name", "Denied"), ("Input.Password", UserPassword))).StatusCode);
        await Register(visitor, "keypassword");
        using var wrong = await Form(visitor, "/account/api-keys", ("Input.Name", "Denied"), ("Input.Password", "incorrect"));
        Assert.Contains("Password verification failed", await wrong.Content.ReadAsStringAsync());
        visitor.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await Form(visitor, "/account/api-keys", ("Input.Name", "Denied"), ("Input.Password", UserPassword))).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IdentityDatabase>().ApiKeys.ToListAsync());
    }

    [Fact]
    public async Task ApiKeysNeverGrantAdministrationOrKeyManagementAccess()
    {
        using var admin = await Client();
        await Login(admin, "admin", AdminPassword);
        using var api = ApiClient(await CreateKey(admin, password: AdminPassword));
        Assert.Equal(HttpStatusCode.Redirect, (await api.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await api.GetAsync("/account/api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await api.PostAsJsonAsync("/admin/retention", new { minutes = 1 })).StatusCode);
        var website = await Json(await api.GetAsync("/inbox/bootstrap"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, website.GetProperty("user").ValueKind);
    }

    [Fact]
    public async Task PasswordChangesAndAccountLockoutInvalidateApiAccess()
    {
        using var owner = await Client();
        await Register(owner, "keysecurity");
        using var api = ApiClient(await CreateKey(owner));
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByNameAsync("keysecurity"))!;
        Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(5))).Succeeded);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/bootstrap")).StatusCode);
        Assert.True((await users.SetLockoutEndDateAsync(user, null)).Succeeded);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/bootstrap")).StatusCode);
        Assert.True((await users.ChangePasswordAsync(user, UserPassword, "replacement-password-for-test-2026")).Succeeded);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/bootstrap")).StatusCode);
    }

    [Fact]
    public async Task KeyLimitIsEnforcedAndRevokingFreesASlot()
    {
        using var owner = await Client();
        await Register(owner, "keylimit");
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByNameAsync("keylimit"))!;
        var service = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        for (var i = 0; i < ApiKeyService.MaximumActiveKeys; i++) await service.CreateAsync(user, "Key " + i, default);
        await Assert.ThrowsAsync<TMailSharp.Services.AppException>(() => service.CreateAsync(user, "Too many", default));
        var keys = await service.ListAsync(user, default);
        await service.RevokeAsync(user.Id, keys[0].Id, default);
        await service.CreateAsync(user, "Replacement", default);
        Assert.Equal(10, (await service.ListAsync(user, default)).Count(x => x.Status == "Active"));
    }

    [Fact]
    public async Task ExistingIdentityDatabaseGetsKeyTableWithoutLosingAccounts()
    {
        using var owner = await Client();
        await Register(owner, "existingaccount");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDatabase>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ApiKeys");
            await IdentityInitializer.EnsureApiKeySchemaAsync(db);
            await IdentityInitializer.EnsureApiKeySchemaAsync(db);
            Assert.True(await db.Users.AnyAsync(x => x.UserName == "existingaccount"));
        }
        using var api = ApiClient(await CreateKey(owner));
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/bootstrap")).StatusCode);
    }
}
