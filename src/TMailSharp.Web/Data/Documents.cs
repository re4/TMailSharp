using MongoDB.Bson.Serialization.Attributes;

namespace TMailSharp.Data;

public sealed class MailboxDocument
{
    [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Address { get; set; } = "";
    public string Domain { get; set; } = "";
    public string OwnerHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonIgnoreIfNull] public DateTime? ExpiresAt { get; set; }
    public bool IsPermanent { get; set; }
    public int MessageCount { get; set; }
}

public sealed class AddressReservation
{
    [BsonId] public string Address { get; set; } = "";
    [BsonIgnoreIfNull] public DateTime? ExpiresAt { get; set; }
}

public sealed class DomainDocument
{
    [BsonId] public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? RetentionMinutes { get; set; }
}

public sealed class MailSettingsDocument
{
    [BsonId] public string Id { get; set; } = "global";
    public int MessageRetentionMinutes { get; set; } = 60;
    public bool DomainsInitialized { get; set; }
}

public sealed class MessageDocument
{
    [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MailboxId { get; set; } = "";
    public string Sender { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string DeliveryHash { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Preview { get; set; } = "";
    public string TextBody { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public bool IsRead { get; set; }
    public List<AttachmentDocument> Attachments { get; set; } = [];
}

public sealed class AttachmentDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "attachment";
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Content { get; set; } = [];
}
