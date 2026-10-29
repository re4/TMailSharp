using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using Microsoft.Extensions.Options;
using MimeKit;
using TMailSharp.Configuration;
using TMailSharp.Data;

namespace TMailSharp.Services;

public sealed partial class MailProcessor(MongoMailStore store, IOptions<MailOptions> options)
{
    private readonly HtmlSanitizer _sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.UnionWith(["a", "p", "br", "div", "span", "strong", "b", "em", "i", "u", "s", "h1", "h2", "h3", "h4", "ul", "ol", "li", "blockquote", "pre", "code", "hr", "table", "thead", "tbody", "tr", "th", "td"]);
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.UnionWith(["style", "title", "colspan", "rowspan", "align", "href"]);
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedCssProperties.UnionWith(["color", "background-color", "font-family", "font-size", "font-weight", "text-align", "text-decoration", "line-height", "padding", "margin", "border", "border-color", "border-radius", "border-collapse", "width", "max-width"]);
        sanitizer.AllowedAtRules.Clear();
        return sanitizer;
    }

    public async Task<int> DeliverAsync(byte[] raw, IReadOnlyCollection<string> recipients, CancellationToken ct)
    {
        if (raw.Length > options.Value.MaxMessageBytes) throw new AppException(413, "The message exceeds the size limit.");
        using var input = new MemoryStream(raw, writable: false);
        var mime = await MimeMessage.LoadAsync(input, ct);
        var fingerprint = Convert.ToHexString(SHA256.HashData(raw));
        var delivered = 0;
        foreach (var recipient in recipients.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mailbox = await store.FindRecipientAsync(recipient, ct);
            if (mailbox is null) continue;
            var document = await ParseAsync(mime, ct);
            document.DeliveryHash = fingerprint;
            if (await store.SaveMessageAsync(mailbox, document, ct)) delivered++;
        }
        return delivered;
    }

    public async Task<MessageDocument> ParseAsync(MimeMessage mime, CancellationToken ct)
    {
        var sender = mime.From.Mailboxes.FirstOrDefault();
        var html = mime.HtmlBody ?? "";
        var document = new HtmlParser().ParseDocument(_sanitizer.Sanitize(html));
        foreach (var link in document.QuerySelectorAll("a"))
        {
            link.SetAttribute("target", "_blank");
            link.SetAttribute("rel", "noopener noreferrer");
        }
        var safeHtml = document.Body?.InnerHtml ?? "";
        foreach (var block in document.QuerySelectorAll("p,div,br,li,h1,h2,h3,tr"))
            block.AppendChild(document.CreateTextNode("\n"));
        var text = mime.TextBody ?? document.Body?.TextContent ?? "";
        var preview = WhitespacePattern().Replace(text, " ").Trim();
        var result = new MessageDocument
        {
            Sender = sender?.Address ?? "unknown", SenderName = sender?.Name ?? "",
            Subject = string.IsNullOrWhiteSpace(mime.Subject) ? "(No subject)" : mime.Subject[..Math.Min(mime.Subject.Length, 998)],
            Preview = preview[..Math.Min(preview.Length, 180)], TextBody = text, HtmlBody = html.Length > 0 ? safeHtml : ""
        };
        long attachmentBytes = 0;
        foreach (var attachment in mime.Attachments)
        {
            if (result.Attachments.Count >= 20) throw new AppException(413, "A message can contain at most 20 attachments.");
            using var output = new MemoryStream();
            if (attachment is MessagePart { Message: not null } nested)
                await nested.Message.WriteToAsync(output, ct);
            else if (attachment is MimePart { Content: not null } part)
                await part.Content.DecodeToAsync(output, ct);
            attachmentBytes += output.Length;
            if (attachmentBytes > options.Value.MaxMessageBytes) throw new AppException(413, "The attachments exceed the size limit.");
            var rawName = attachment.ContentDisposition?.FileName ?? attachment.ContentType.Name ?? (attachment is MessagePart ? "message.eml" : "attachment");
            var name = Path.GetFileName(rawName.Replace('\\', '/'));
            name = new string(name.Where(c => !char.IsControl(c)).Take(180).ToArray());
            result.Attachments.Add(new AttachmentDocument
            {
                FileName = string.IsNullOrWhiteSpace(name) ? "attachment" : name,
                ContentType = attachment.ContentType.MimeType,
                Content = output.ToArray()
            });
        }
        return result;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
