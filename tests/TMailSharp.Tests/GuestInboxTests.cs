using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TMailSharp.Tests;

public sealed partial class MailAppTests
{
    [Fact]
    public async Task HomepageStartsAnonymousInboxThroughVersionedWebsiteClient()
    {
        using var browser = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost")
        });
        using var page = await browser.GetAsync("/");
        Assert.True(page.Headers.CacheControl?.NoStore);
        var html = await page.Content.ReadAsStringAsync();
        var endpoint = Regex.Match(html, "name=\"mailbox-base\" content=\"([^\"]+)\"");
        Assert.True(endpoint.Success);
        var websiteBase = WebUtility.HtmlDecode(endpoint.Groups[1].Value);
        Assert.Equal("/inbox", websiteBase);
        var script = Regex.Match(html, "src=\"([^\"]*/app\\.js\\?v=[^\"]+)\"");
        Assert.True(script.Success, "The website client needs a content-versioned URL to replace cached API-only clients.");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(WebUtility.HtmlDecode(script.Groups[1].Value))).StatusCode);
        var bootstrap = await Json(await browser.GetAsync(websiteBase + "/bootstrap"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, bootstrap.GetProperty("user").ValueKind);
        browser.DefaultRequestHeaders.Add("X-TMail-Request", "1");
        browser.DefaultRequestHeaders.Add("X-CSRF-TOKEN", bootstrap.GetProperty("csrfToken").GetString());
        var mailbox = await Json(await browser.PostAsJsonAsync(websiteBase + "/mailboxes", new { }));
        Assert.False(mailbox.GetProperty("isPermanent").GetBoolean());
        var id = mailbox.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"{websiteBase}/mailboxes/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync(websiteBase + "/mailboxes", new { permanent = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/bootstrap")).StatusCode);
    }

    [Fact]
    public async Task FreshGuestCookiesCannotBypassCreationLimitForAnIp()
    {
        for (var attempt = 0; attempt < 13; attempt++)
        {
            using var guest = await Client();
            using var response = await guest.PostAsJsonAsync("/inbox/mailboxes", new { localPart = "../invalid" });
            if (attempt < 12) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            else
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
                Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
                var error = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                Assert.InRange(error.GetProperty("retryAfterSeconds").GetInt32(), 1, 60);
            }
        }
    }

    [Fact]
    public async Task GuestSessionCannotKeepMoreThanTenActiveInboxes()
    {
        using var guest = await Client();
        string? first = null;
        for (var i = 0; i < 10; i++)
        {
            var mailbox = await CreateMailbox(guest);
            first ??= mailbox.GetProperty("id").GetString();
        }
        using var full = await guest.PostAsJsonAsync("/inbox/mailboxes", new { });
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        var bootstrap = await Json(await guest.GetAsync("/inbox/bootstrap"));
        Assert.Equal(10, bootstrap.GetProperty("mailboxes").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await guest.DeleteAsync($"/inbox/mailboxes/{first}")).StatusCode);
        Assert.False((await CreateMailbox(guest)).GetProperty("isPermanent").GetBoolean());
    }
}
