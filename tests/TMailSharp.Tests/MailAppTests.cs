using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using TMailSharp.Data;
using TMailSharp.Services;
using TMailSharp.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace TMailSharp.Tests;

public sealed partial class MailAppTests : IAsyncLifetime
{
    private const string AdminPassword = "test-only-admin-password-strong-2026";
    private readonly string _database = $"tmailsharp_test_{Guid.NewGuid():N}";
    private readonly string _connection = Environment.GetEnvironmentVariable("TMAIL_TEST_MONGO") ?? "mongodb://127.0.0.1:27017";
    private readonly int _smtpPort = FreePort();
    private readonly string _identityFile = Path.Combine(Path.GetTempPath(), $"tmailsharp_test_{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> _factory = null!;
    private MongoMailStore Store => _factory.Services.GetRequiredService<MongoMailStore>();

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mongo:ConnectionString"] = _connection,
                ["Mongo:Database"] = _database,
                ["Smtp:Enabled"] = "true",
                ["Smtp:Host"] = "127.0.0.1",
                ["Smtp:Port"] = _smtpPort.ToString(),
                ["Smtp:CertificatePath"] = "",
                ["Admin:Password"] = AdminPassword,
                ["Demo:Enabled"] = "true",
                ["SecurityCheck:Difficulty"] = "16",
                ["Logging:LogLevel:Microsoft.AspNetCore.Identity"] = "Debug",
                ["DataProtection:KeyDirectory"] = Path.Combine(Path.GetTempPath(), "tmailsharp-test-keys"),
                ["ConnectionStrings:Identity"] = $"Data Source={_identityFile};Pooling=False"
            }));
            builder.ConfigureServices(services => services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()));
        });
        using var client = await Client();
        await client.GetAsync("/health");
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await new MongoClient(_connection).DropDatabaseAsync(_database);
        if (File.Exists(_identityFile)) File.Delete(_identityFile);
    }

    private async Task<HttpClient> Client()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-TMail-Request", "1");
        await RefreshToken(client);
        return client;
    }

    private static async Task RefreshToken(HttpClient client)
    {
        var bootstrap = await Json(await client.GetAsync("/inbox/bootstrap"));
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", bootstrap.GetProperty("csrfToken").GetString());
    }

    private static async Task<HttpResponseMessage> Form(HttpClient client, string path, params (string Key, string Value)[] values)
    {
        if (path is "/account/login" or "/account/register")
        {
            var proof = await SolveCheck(client, path.EndsWith("register", StringComparison.Ordinal) ? "register" : "login");
            values = [.. values, ("ProofToken", proof.ProofToken!), ("ProofNonce", proof.ProofNonce!)];
        }
        return await client.PostAsync(path, new FormUrlEncodedContent(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value))));
    }

    private static async Task Login(HttpClient client, string name, string password)
    {
        Assert.Equal(HttpStatusCode.Redirect, (await Form(client, "/account/login", ("UserName", name), ("Password", password))).StatusCode);
        await RefreshToken(client);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {response.StatusCode}: {content}");
        return JsonDocument.Parse(content).RootElement.Clone();
    }

    private static async Task<JsonElement> CreateMailbox(HttpClient client) =>
        await Json(await client.PostAsJsonAsync("/inbox/mailboxes", new { domain = "drop.localhost" }));

    [Fact]
    public async Task MailboxOwnerCanReadButAnotherBrowserCannotReadOrDelete()
    {
        using var owner = await Client();
        using var stranger = await Client();
        var mailbox = await CreateMailbox(owner);
        var id = mailbox.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/inbox/mailboxes/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/inbox/mailboxes/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/inbox/mailboxes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/inbox/mailboxes/{id}")).StatusCode);
    }

    [Fact]
    public async Task SampleDeliveryIncludesAttachmentAndDeletionRemovesEverything()
    {
        using var client = await Client();
        using var stranger = await Client();
        var mailbox = await CreateMailbox(client);
        var id = mailbox.GetProperty("id").GetString();
        await Json(await client.PostAsync($"/inbox/mailboxes/{id}/sample", null));
        var listing = await Json(await client.GetAsync($"/inbox/mailboxes/{id}/messages"));
        var message = Assert.Single(listing.GetProperty("messages").EnumerateArray());
        Assert.False(message.GetProperty("isRead").GetBoolean());
        Assert.False(message.TryGetProperty("textBody", out _));
        Assert.False(message.TryGetProperty("htmlBody", out _));
        var messageId = message.GetProperty("id").GetString();
        var detail = await Json(await client.GetAsync($"/inbox/mailboxes/{id}/messages/{messageId}"));
        Assert.Contains("482 916", detail.GetProperty("textBody").GetString());
        using var formatted = await client.GetAsync($"/inbox/mailboxes/{id}/messages/{messageId}/content");
        Assert.Equal(HttpStatusCode.OK, formatted.StatusCode);
        Assert.Contains("default-src 'none'", Assert.Single(formatted.Headers.GetValues("Content-Security-Policy")));
        Assert.Contains("482 916", await formatted.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/inbox/mailboxes/{id}/messages/{messageId}/content")).StatusCode);
        var attachment = Assert.Single(detail.GetProperty("attachments").EnumerateArray());
        var path = $"/inbox/mailboxes/{id}/messages/{messageId}/attachments/{attachment.GetProperty("id").GetString()}";
        using var download = await client.GetAsync(path);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("Welcome", await download.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(path)).StatusCode);
        await client.DeleteAsync($"/inbox/mailboxes/{id}");
        Assert.Equal(0, await Store.Messages.CountDocumentsAsync(x => x.MailboxId == id));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task ExpiryIsEnforcedImmediatelyAndIndexesExpireStoredDocuments()
    {
        using var client = await Client();
        var mailbox = await CreateMailbox(client);
        var id = mailbox.GetProperty("id").GetString();
        var address = mailbox.GetProperty("address").GetString()!;
        await Json(await client.PostAsync($"/inbox/mailboxes/{id}/sample", null));
        await Store.Mailboxes.UpdateOneAsync(x => x.Id == id, Builders<MailboxDocument>.Update.Set(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/inbox/mailboxes/{id}/messages")).StatusCode);
        Assert.Null(await Store.FindRecipientAsync(address, default));
        var collision = await client.PostAsJsonAsync("/inbox/mailboxes", new { localPart = address.Split('@')[0], domain = "drop.localhost" });
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        var mailboxIndexes = await (await Store.Mailboxes.Indexes.ListAsync()).ToListAsync();
        var messageIndexes = await (await Store.Messages.Indexes.ListAsync()).ToListAsync();
        Assert.Contains(mailboxIndexes, index => index.Contains("expireAfterSeconds") && index["expireAfterSeconds"] == 0);
        Assert.Contains(messageIndexes, index => index.Contains("expireAfterSeconds") && index["expireAfterSeconds"] == 0);
    }

    [Fact]
    public async Task UnsafeHtmlIsSanitizedAndDuplicateDeliveryIsIdempotent()
    {
        using var client = await Client();
        var mailbox = await CreateMailbox(client);
        var address = mailbox.GetProperty("address").GetString()!;
        var id = mailbox.GetProperty("id").GetString();
        var raw = Encoding.UTF8.GetBytes($"From: Sender <sender@example.com>\r\nTo: {address}\r\nSubject: Safe preview\r\nMIME-Version: 1.0\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<h1>Hello</h1><script>alert(1)</script><img src='https://tracker.example/pixel'><iframe srcdoc='unsafe'></iframe><form action='https://evil.example'><input></form><a href='javascript:alert(1)'>bad</a><a href='https://example.com/verify'>verify</a><p onclick='alert(1)' style='background-image:url(https://tracker.example/);color:green'>Safe text</p>");
        var processor = _factory.Services.GetRequiredService<MailProcessor>();
        Assert.Equal(1, await processor.DeliverAsync(raw, [address], default));
        Assert.Equal(1, await processor.DeliverAsync(raw, [address], default));
        var listing = await Json(await client.GetAsync($"/inbox/mailboxes/{id}/messages"));
        var message = Assert.Single(listing.GetProperty("messages").EnumerateArray());
        var detail = await Json(await client.GetAsync($"/inbox/mailboxes/{id}/messages/{message.GetProperty("id").GetString()}"));
        var html = detail.GetProperty("htmlBody").GetString()!;
        Assert.Contains("Safe text", html);
        Assert.Contains("https://example.com/verify", html);
        foreach (var unsafePart in new[] { "<script", "<img", "<iframe", "<form", "<input", "onclick", "javascript:", "background-image", "tracker.example" })
            Assert.DoesNotContain(unsafePart, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hello", detail.GetProperty("textBody").GetString());
        var stored = await Store.Mailboxes.Find(x => x.Id == id).SingleAsync();
        Assert.Equal(1, stored.MessageCount);
    }

    [Fact]
    public async Task AdminLoginGatesDomainChangesAndDisabledDomainsStayDisabled()
    {
        using var client = await Client();
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);
        var wrong = await Form(client, "/account/login", ("UserName", "admin"), ("Password", "wrong"));
        Assert.Contains("Sign-in failed", await wrong.Content.ReadAsStringAsync());
        await Login(client, "admin", AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Form(client, "/admin/domains", ("name", "TEST.Example.com"), ("enabled", "true"))).StatusCode);
        var mailbox = await Json(await client.PostAsJsonAsync("/inbox/mailboxes", new { domain = "test.example.com" }));
        Assert.NotNull(await Store.FindRecipientAsync(mailbox.GetProperty("address").GetString()!, default));
        Assert.Equal(HttpStatusCode.Redirect, (await Form(client, "/admin/domains", ("name", "test.example.com"), ("enabled", "false"))).StatusCode);
        Assert.Null(await Store.FindRecipientAsync(mailbox.GetProperty("address").GetString()!, default));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/inbox/mailboxes", new { domain = "test.example.com" })).StatusCode);
        await Store.InitializeAsync();
        Assert.DoesNotContain(await Store.GetDomainsAsync(), d => d.Name == "test.example.com");
        await client.PostAsync("/account/logout", null);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task MutationsRequireCustomHeaderAndMatchingOrigin()
    {
        using var client = await Client();
        client.DefaultRequestHeaders.Remove("X-TMail-Request");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/inbox/mailboxes", new { })).StatusCode);
        client.DefaultRequestHeaders.Add("X-TMail-Request", "1");
        client.DefaultRequestHeaders.Add("Origin", "https://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/inbox/mailboxes", new { })).StatusCode);
    }

    [Fact]
    public async Task InvalidAddressAndDomainAreRejected()
    {
        using var client = await Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/inbox/mailboxes", new { localPart = "../outside", domain = "drop.localhost" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/inbox/mailboxes", new { localPart = "admin", domain = "drop.localhost" })).StatusCode);
        Assert.Throws<AppException>(() => AddressRules.NormalizeDomain("https://evil.com/path"));
        Assert.Throws<AppException>(() => AddressRules.NormalizeDomain("-invalid.example.com"));
    }

    [Fact]
    public async Task SmtpReceivesRealMessageAndRejectsUnknownRecipientsAndRelaying()
    {
        using var client = await Client();
        var mailbox = await CreateMailbox(client);
        var address = mailbox.GetProperty("address").GetString();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _smtpPort);
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        Assert.StartsWith("220", await Reply(reader));
        await writer.WriteLineAsync("EHLO integration.test"); Assert.StartsWith("250", await Reply(reader));
        await writer.WriteLineAsync("MAIL FROM:<sender@example.com>"); Assert.StartsWith("250", await Reply(reader));
        await writer.WriteLineAsync("RCPT TO:<nobody@external.example>"); Assert.StartsWith("550", await Reply(reader));
        await writer.WriteLineAsync("RCPT TO:<not-created@drop.localhost>"); Assert.StartsWith("550", await Reply(reader));
        await writer.WriteLineAsync($"RCPT TO:<{address}>"); Assert.StartsWith("250", await Reply(reader));
        await writer.WriteLineAsync("DATA"); Assert.StartsWith("354", await Reply(reader));
        await writer.WriteLineAsync($"From: sender@example.com\r\nTo: {address}\r\nSubject: Real SMTP delivery\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nHello over the wire.\r\n.");
        Assert.StartsWith("250", await Reply(reader));
        await writer.WriteLineAsync("QUIT"); Assert.StartsWith("221", await Reply(reader));
        var listing = await Json(await client.GetAsync($"/inbox/mailboxes/{mailbox.GetProperty("id").GetString()}/messages"));
        var message = Assert.Single(listing.GetProperty("messages").EnumerateArray());
        Assert.Equal("Real SMTP delivery", message.GetProperty("subject").GetString());
    }

    private static async Task<string> Reply(StreamReader reader)
    {
        string line;
        do { line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new IOException("SMTP disconnected."); }
        while (line.Length > 3 && line[3] == '-');
        return line;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
