using System.ComponentModel.DataAnnotations;

namespace TMailSharp.Configuration;

public sealed class MongoOptions
{
    public const string Section = "Mongo";
    [Required] public string ConnectionString { get; set; } = "mongodb://127.0.0.1:27017";
    [RegularExpression("^[a-zA-Z0-9_-]{1,60}$")] public string Database { get; set; } = "tmailsharp";
}

public sealed class MailOptions
{
    public const string Section = "Mail";
    public string[] Domains { get; set; } = ["drop.localhost"];
    [Range(5, 1440)] public int LifetimeMinutes { get; set; } = 60;
    [Range(1, 50)] public int MaxMailboxesPerSession { get; set; } = 10;
    [Range(1, 100)] public int MaxMailboxesPerAccount { get; set; } = 25;
    [Range(1, 525600)] public int MessageRetentionMinutes { get; set; } = 60;
    [Range(1024, 5_242_880)] public int MaxMessageBytes { get; set; } = 5_242_880;
    [Range(1, 500)] public int MaxMessagesPerMailbox { get; set; } = 100;
    [Range(1, 30)] public int AddressReuseDelayDays { get; set; } = 7;
}

public sealed class SmtpOptions
{
    public const string Section = "Smtp";
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    [Range(1, 65535)] public int Port { get; set; } = 2525;
    public string ServerName { get; set; } = "mail.localhost";
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
}

public sealed class AdminOptions
{
    public const string Section = "Admin";
    public string UserName { get; set; } = "admin";
    public string Password { get; set; } = "";
}

public sealed class DemoOptions
{
    public const string Section = "Demo";
    public bool Enabled { get; set; }
}
