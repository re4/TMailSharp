using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MimeKit;
using TMailSharp.Configuration;
using TMailSharp.Data;
using TMailSharp.Identity;
using TMailSharp.Services;

namespace TMailSharp.Controllers;

[ApiController, EnableRateLimiting("api")]
public sealed class MailboxesController(MongoMailStore store, MailboxAccess access, IAntiforgery antiforgery,
    IOptions<MailOptions> mail, IOptions<DemoOptions> demo, SmtpReceiver smtp, MailProcessor processor) : ControllerBase
{
    private string Owner => access.Owner(HttpContext);
    internal static object ViewOf(MailboxDocument m) => new { m.Id, m.Address, m.Domain, m.CreatedAt, m.ExpiresAt, m.IsPermanent };

    [HttpGet("/api/bootstrap")]
    public async Task<IActionResult> Bootstrap(CancellationToken ct)
    {
        var owner = Owner;
        var legacy = Request.Cookies["tmailsharp.session"];
        if (legacy is { Length: 64 } && legacy.All(Uri.IsHexDigit))
        {
            await store.ClaimGuestMailboxesAsync(AddressRules.Hash(legacy), owner, ct);
            Response.Cookies.Delete("tmailsharp.session", new CookieOptions { Path = "/" });
        }
        return Ok(new {
        domains = await store.GetDomainsAsync(ct: ct),
        mailboxes = (await store.GetMailboxesAsync(owner, ct)).Select(ViewOf),
        lifetimeMinutes = mail.Value.LifetimeMinutes, demoEnabled = demo.Value.Enabled, smtpListening = smtp.IsListening,
        maxMessageBytes = mail.Value.MaxMessageBytes,
        maxMailboxes = User.Identity?.IsAuthenticated == true ? mail.Value.MaxMailboxesPerAccount : mail.Value.MaxMailboxesPerSession,
        messageRetentionMinutes = (await store.GetSettingsAsync(ct)).MessageRetentionMinutes,
        csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
        user = User.Identity?.IsAuthenticated == true ? new { name = User.Identity.Name, isAdmin = User.IsInRole("Administrator") } : null
        });
    }

    [HttpPost("/api/mailboxes"), EnableRateLimiting("create")]
    public async Task<IActionResult> Create(CreateMailboxRequest request, CancellationToken ct)
    {
        if (request.Permanent && User.Identity?.IsAuthenticated != true) return Unauthorized();
        var mailbox = await store.CreateMailboxAsync(Owner, request.LocalPart, request.Domain, ct, request.Permanent);
        return Created($"/api/mailboxes/{mailbox.Id}", ViewOf(mailbox));
    }

    [Authorize, HttpPost("/api/mailboxes/{id}/reserve")]
    public async Task<IActionResult> Reserve(string id, CancellationToken ct) => Ok(ViewOf(await store.ReserveMailboxAsync(id, Owner, ct)));

    [HttpDelete("/api/mailboxes/{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await store.DeleteMailboxAsync(id, Owner, ct);
        return NoContent();
    }

    [HttpGet("/api/mailboxes/{id}/messages")]
    public async Task<IActionResult> Messages(string id, CancellationToken ct)
    {
        var mailbox = await store.RequireMailboxAsync(id, Owner, ct);
        var messages = await store.GetMessagesAsync(id, ct);
        return Ok(new { mailbox = ViewOf(mailbox), messages = messages.Select(m => new
        { m.Id, m.Sender, m.SenderName, m.Subject, m.Preview, m.ReceivedAt, m.IsRead, attachmentCount = m.Attachments.Count }) });
    }

    [HttpGet("/api/mailboxes/{id}/messages/{messageId}")]
    public async Task<IActionResult> Message(string id, string messageId, CancellationToken ct)
    {
        await store.RequireMailboxAsync(id, Owner, ct);
        var message = await store.RequireMessageAsync(id, messageId, ct);
        await store.MarkReadAsync(messageId, id, ct);
        return Ok(new { message.Id, message.Sender, message.SenderName, message.Recipient, message.Subject,
            message.TextBody, message.HtmlBody, message.ReceivedAt, message.ExpiresAt, isRead = true,
            attachments = message.Attachments.Select(a => new { a.Id, a.FileName, size = a.Content.Length }) });
    }

    [HttpDelete("/api/mailboxes/{id}/messages/{messageId}")]
    public async Task<IActionResult> DeleteMessage(string id, string messageId, CancellationToken ct)
    {
        await store.RequireMailboxAsync(id, Owner, ct);
        await store.DeleteMessageAsync(messageId, id, ct);
        return NoContent();
    }

    [HttpGet("/api/mailboxes/{id}/messages/{messageId}/content")]
    public async Task<IActionResult> Content(string id, string messageId, CancellationToken ct)
    {
        await store.RequireMailboxAsync(id, Owner, ct);
        var message = await store.RequireMessageAsync(id, messageId, ct);
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'; sandbox allow-popups allow-popups-to-escape-sandbox";
        Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        var light = Request.Query["theme"] == "light";
        var colors = light ? "color:#1f2f4b;background:#fff" : "color:#d6e4f7;background:#0f192b";
        return base.Content("<!doctype html><html><head><meta charset='utf-8'><style>body{margin:0;" + colors +
            ";font:14px/1.75 system-ui,sans-serif;overflow-wrap:anywhere}table{max-width:100%}pre{white-space:pre-wrap}</style></head><body>" +
            message.HtmlBody + "</body></html>", "text/html; charset=utf-8");
    }

    [HttpGet("/api/mailboxes/{id}/messages/{messageId}/attachments/{attachmentId}")]
    public async Task<IActionResult> Attachment(string id, string messageId, string attachmentId, CancellationToken ct)
    {
        await store.RequireMailboxAsync(id, Owner, ct);
        var message = await store.RequireMessageAsync(id, messageId, ct);
        var attachment = message.Attachments.FirstOrDefault(a => a.Id == attachmentId) ?? throw new AppException(404, "Attachment not found.");
        return File(attachment.Content, "application/octet-stream", attachment.FileName);
    }

    [HttpPost("/api/mailboxes/{id}/sample"), EnableRateLimiting("create")]
    public async Task<IActionResult> Sample(string id, CancellationToken ct)
    {
        if (!demo.Value.Enabled) return NotFound();
        var mailbox = await store.RequireMailboxAsync(id, Owner, ct);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("TMailSharp", "hello@example.com"));
        message.To.Add(MailboxAddress.Parse(mailbox.Address));
        message.Subject = "A little hello for your new inbox";
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        var body = new BodyBuilder { TextBody = "Welcome. Your example verification code: 482 916", HtmlBody = "<h1>Welcome</h1><p>Your example verification code: <strong>482 916</strong></p>" };
        body.Attachments.Add("hello.txt", Encoding.UTF8.GetBytes("Welcome to TMailSharp."));
        message.Body = body.ToMessageBody();
        using var output = new MemoryStream();
        await message.WriteToAsync(output, ct);
        var count = await processor.DeliverAsync(output.ToArray(), [mailbox.Address], ct);
        if (count == 0) throw new AppException(409, "The inbox is full or expired.");
        return Ok(new { delivered = count });
    }
}

public sealed record CreateMailboxRequest(string? LocalPart, string? Domain, bool Permanent = false);
