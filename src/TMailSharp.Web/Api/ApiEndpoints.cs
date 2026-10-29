using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using MimeKit;
using TMailSharp.Configuration;
using TMailSharp.Data;
using TMailSharp.Services;

namespace TMailSharp.Api;

public static class ApiEndpoints
{
    public static void MapMailApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireRateLimiting("api");
        api.MapGet("/bootstrap", async (MongoMailStore store, HttpContext context, IOptions<MailOptions> mail, IOptions<DemoOptions> demo, SmtpReceiver smtp, CancellationToken ct) =>
            Results.Ok(new { domains = await store.GetDomainsAsync(ct: ct), mailboxes = (await store.GetMailboxesAsync(Owner(context), ct)).Select(MailboxView),
                lifetimeMinutes = mail.Value.LifetimeMinutes, demoEnabled = demo.Value.Enabled, smtpListening = smtp.IsListening,
                maxMessageBytes = mail.Value.MaxMessageBytes, maxMailboxes = mail.Value.MaxMailboxesPerSession }));

        api.MapPost("/mailboxes", async (CreateMailboxRequest request, MongoMailStore store, HttpContext context, CancellationToken ct) =>
        {
            var mailbox = await store.CreateMailboxAsync(Owner(context), request.LocalPart, request.Domain, ct);
            return Results.Created($"/api/mailboxes/{mailbox.Id}", MailboxView(mailbox));
        }).RequireRateLimiting("create");

        api.MapDelete("/mailboxes/{id}", async (string id, MongoMailStore store, HttpContext context, CancellationToken ct) =>
        {
            await store.DeleteMailboxAsync(id, Owner(context), ct);
            return Results.NoContent();
        });

        api.MapGet("/mailboxes/{id}/messages", async (string id, MongoMailStore store, HttpContext context, CancellationToken ct) =>
        {
            var mailbox = await store.RequireMailboxAsync(id, Owner(context), ct);
            var messages = await store.GetMessagesAsync(id, ct);
            return Results.Ok(new { mailbox = MailboxView(mailbox), messages = messages.Select(MessageView) });
        });

        api.MapGet("/mailboxes/{id}/messages/{messageId}", async (string id, string messageId, MongoMailStore store, HttpContext context, CancellationToken ct) =>
        {
            await store.RequireMailboxAsync(id, Owner(context), ct);
            var message = await store.RequireMessageAsync(id, messageId, ct);
            await store.MarkReadAsync(messageId, id, ct);
            return Results.Ok(new { message.Id, message.Sender, message.SenderName, message.Recipient, message.Subject,
                message.TextBody, message.HtmlBody, message.ReceivedAt, isRead = true, attachments = message.Attachments.Select(AttachmentView) });
        });

        api.MapDelete("/mailboxes/{id}/messages/{messageId}", async (string id, string messageId, MongoMailStore store, HttpContext context, CancellationToken ct) =>
        {
            await store.RequireMailboxAsync(id, Owner(context), ct);
            await store.DeleteMessageAsync(messageId, id, ct);
            return Results.NoContent();
        });

        api.MapGet("/mailboxes/{id}/messages/{messageId}/content", async (string id, string messageId, MongoMailStore store, HttpContext context, CancellationToken ct) =>
        {
            await store.RequireMailboxAsync(id, Owner(context), ct);
            var message = await store.RequireMessageAsync(id, messageId, ct);
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'; sandbox allow-popups allow-popups-to-escape-sandbox";
            context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            var light = context.Request.Query["theme"] == "light";
            var colors = light ? "color:#1f2f4b;background:#fff" : "color:#d6e4f7;background:#0f192b";
            var scheme = light ? "light" : "dark";
            var linkColor = light ? "#2563eb" : "#86b7ff";
            return Results.Content("<!doctype html><html><head><meta charset='utf-8'><style>:root{color-scheme:" + scheme + "}body{margin:0;" + colors + ";font:14px/1.75 system-ui,sans-serif;overflow-wrap:anywhere}table{max-width:100%}pre{white-space:pre-wrap}a{color:" + linkColor + "}</style></head><body>" + message.HtmlBody + "</body></html>", "text/html; charset=utf-8");
        });

        api.MapGet("/mailboxes/{id}/messages/{messageId}/attachments/{attachmentId}", async (string id, string messageId, string attachmentId, MongoMailStore store, HttpContext context, CancellationToken ct) =>
        {
            await store.RequireMailboxAsync(id, Owner(context), ct);
            var message = await store.RequireMessageAsync(id, messageId, ct);
            var attachment = message.Attachments.FirstOrDefault(x => x.Id == attachmentId) ?? throw new AppException(404, "Attachment not found.");
            // Always download; never execute user-supplied HTML or SVG under the application origin.
            return Results.File(attachment.Content, "application/octet-stream", attachment.FileName);
        });

        api.MapPost("/mailboxes/{id}/sample", async (string id, MongoMailStore store, MailProcessor processor, HttpContext context, IOptions<DemoOptions> demo, CancellationToken ct) =>
        {
            if (!demo.Value.Enabled) throw new AppException(404, "Sample delivery is disabled.");
            var mailbox = await store.RequireMailboxAsync(id, Owner(context), ct);
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("TMailSharp", "hello@example.com"));
            message.To.Add(MailboxAddress.Parse(mailbox.Address));
            message.Subject = "A little hello for your new inbox";
            message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
            var body = new BodyBuilder
            {
                TextBody = "Hello there!\n\nYour inbox is ready. This is a sample message, delivered through the same MIME processor used for incoming mail.\n\nYour example verification code: 482 916\n\nUse this address for a quick signup, a download, or testing your own app. Messages disappear when this inbox expires.\n\nEnjoy a quieter inbox,\nTMailSharp",
                HtmlBody = "<div style='padding:24px;font-family:system-ui;color:#d7e4fa;background-color:#14223a;border-radius:12px'><p style='color:#9eb6d8;font-size:12px'>A FRESH START</p><h1>Hello there.</h1><p>Your new inbox is ready for a little less noise.</p><p>This sample message went through the same MIME processor as incoming email.</p><p>Your example verification code</p><div style='font-size:30px;font-weight:bold;color:#93c5fd;background-color:#1d3860;padding:20px;border-radius:12px'>482 916</div><p>Use this address for a quick signup, a download, or testing your own app. Messages disappear when this inbox expires.</p><p>Enjoy a quieter inbox,<br><strong>TMailSharp</strong></p></div>"
            };
            body.Attachments.Add("hello.txt", Encoding.UTF8.GetBytes("Welcome to TMailSharp. This is a sample attachment."), new ContentType("text", "plain"));
            message.Body = body.ToMessageBody();
            using var output = new MemoryStream();
            await message.WriteToAsync(output, ct);
            var count = await processor.DeliverAsync(output.ToArray(), [mailbox.Address], ct);
            if (count == 0) throw new AppException(409, "The inbox is full or expired.");
            return Results.Ok(new { delivered = count });
        }).RequireRateLimiting("create");

        api.MapPost("/admin/login", async (LoginRequest request, HttpContext context, IOptions<AdminOptions> admin) =>
        {
            if (string.IsNullOrEmpty(admin.Value.Password)) throw new AppException(503, "Set Admin__Password on the server to enable administration.");
            var expected = SHA256.HashData(Encoding.UTF8.GetBytes(admin.Value.Password));
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(request.Password ?? ""));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new AppException(401, "That password isn't correct.");
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "administrator")], CookieAuthenticationDefaults.AuthenticationScheme));
            await context.SignInAsync(principal, new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(4) });
            return Results.Ok(new { authenticated = true });
        }).RequireRateLimiting("login");

        var adminApi = api.MapGroup("/admin").RequireAuthorization();
        adminApi.MapPost("/logout", async (HttpContext context) => { await context.SignOutAsync(); return Results.NoContent(); });
        adminApi.MapGet("/overview", async (MongoMailStore store, SmtpReceiver smtp, IOptions<SmtpOptions> options, CancellationToken ct) =>
            Results.Ok(new { stats = await store.GetStatsAsync(ct), domains = await store.GetDomainsAsync(true, ct), smtpListening = smtp.IsListening,
                smtpPort = options.Value.Port, smtpTls = !string.IsNullOrWhiteSpace(options.Value.CertificatePath) }));
        adminApi.MapPut("/domains", async (DomainRequest request, MongoMailStore store, CancellationToken ct) =>
        {
            var domain = AddressRules.NormalizeDomain(request.Name ?? "");
            await store.SetDomainAsync(domain, request.Enabled, ct);
            return Results.Ok(new { name = domain, enabled = request.Enabled });
        });
    }

    public static string Owner(HttpContext context) => (string)context.Items["OwnerHash"]!;
    private static object MailboxView(MailboxDocument m) => new { m.Id, m.Address, m.Domain, m.CreatedAt, m.ExpiresAt };
    private static object AttachmentView(AttachmentDocument a) => new { a.Id, a.FileName, size = a.Content.Length };
    private static object MessageView(MessageDocument m) => new { m.Id, m.Sender, m.SenderName, m.Subject, m.Preview, m.ReceivedAt, m.IsRead, attachmentCount = m.Attachments.Count };
}

public sealed record CreateMailboxRequest(string? LocalPart, string? Domain);
public sealed record LoginRequest(string? Password);
public sealed record DomainRequest(string? Name, bool Enabled);
