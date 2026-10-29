using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using TMailSharp.Configuration;
using TMailSharp.Services;

namespace TMailSharp.Data;

public sealed class MongoMailStore
{
    private readonly IMongoDatabase _database;
    private readonly MailOptions _options;
    public IMongoCollection<MailboxDocument> Mailboxes { get; }
    public IMongoCollection<MessageDocument> Messages { get; }
    public IMongoCollection<DomainDocument> Domains { get; }
    private IMongoCollection<AddressReservation> Reservations { get; }
    private IMongoCollection<MailSettingsDocument> Settings { get; }

    public MongoMailStore(IOptions<MongoOptions> mongo, IOptions<MailOptions> mail)
    {
        var settings = MongoClientSettings.FromConnectionString(mongo.Value.ConnectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(8);
        _database = new MongoClient(settings).GetDatabase(mongo.Value.Database);
        _options = mail.Value;
        Mailboxes = _database.GetCollection<MailboxDocument>("mailboxes");
        Messages = _database.GetCollection<MessageDocument>("messages");
        Domains = _database.GetCollection<DomainDocument>("domains");
        Reservations = _database.GetCollection<AddressReservation>("address_reservations");
        Settings = _database.GetCollection<MailSettingsDocument>("settings");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await PingAsync(ct);
        await Mailboxes.Indexes.CreateManyAsync([
            new(Builders<MailboxDocument>.IndexKeys.Ascending(x => x.Address), new CreateIndexOptions { Unique = true }),
            new(Builders<MailboxDocument>.IndexKeys.Ascending(x => x.OwnerHash).Ascending(x => x.ExpiresAt)),
            new(Builders<MailboxDocument>.IndexKeys.Ascending(x => x.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero })
        ], ct);
        await Messages.Indexes.CreateManyAsync([
            new(Builders<MessageDocument>.IndexKeys.Ascending(x => x.MailboxId).Descending(x => x.ReceivedAt)),
            new(Builders<MessageDocument>.IndexKeys.Ascending(x => x.MailboxId).Ascending(x => x.DeliveryHash), new CreateIndexOptions { Unique = true }),
            new(Builders<MessageDocument>.IndexKeys.Ascending(x => x.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero })
        ], ct);
        await Reservations.Indexes.CreateOneAsync(new CreateIndexModel<AddressReservation>(
            Builders<AddressReservation>.IndexKeys.Ascending(x => x.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
        await Settings.UpdateOneAsync(x => x.Id == "global", Builders<MailSettingsDocument>.Update
            .SetOnInsert(x => x.MessageRetentionMinutes, _options.MessageRetentionMinutes), new UpdateOptions { IsUpsert = true }, ct);
        var settings = await GetSettingsAsync(ct);
        // Persist initialization so deleting the last domain does not reseed it on restart.
        if (!settings.DomainsInitialized && await Domains.CountDocumentsAsync(FilterDefinition<DomainDocument>.Empty, cancellationToken: ct) == 0)
        {
            foreach (var name in _options.Domains.Select(AddressRules.NormalizeDomain).Distinct())
                await Domains.UpdateOneAsync(x => x.Name == name,
                    Builders<DomainDocument>.Update.SetOnInsert(x => x.Enabled, true).SetOnInsert(x => x.CreatedAt, DateTime.UtcNow),
                    new UpdateOptions { IsUpsert = true }, ct);
        }
        await Settings.UpdateOneAsync(x => x.Id == "global", Builders<MailSettingsDocument>.Update.Set(x => x.DomainsInitialized, true), cancellationToken: ct);
    }

    public Task<BsonDocument> PingAsync(CancellationToken ct) => _database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
    public Task<List<DomainDocument>> GetDomainsAsync(bool all = false, CancellationToken ct = default) =>
        Domains.Find(all ? FilterDefinition<DomainDocument>.Empty : Builders<DomainDocument>.Filter.Eq(x => x.Enabled, true)).SortBy(x => x.CreatedAt).ToListAsync(ct);

    public Task<List<MailboxDocument>> GetMailboxesAsync(string owner, CancellationToken ct) =>
        Mailboxes.Find(x => x.OwnerHash == owner && (x.IsPermanent || x.ExpiresAt > DateTime.UtcNow)).SortByDescending(x => x.CreatedAt).ToListAsync(ct);

    public async Task<MailboxDocument> RequireMailboxAsync(string id, string owner, CancellationToken ct) =>
        await Mailboxes.Find(x => x.Id == id && x.OwnerHash == owner && (x.IsPermanent || x.ExpiresAt > DateTime.UtcNow)).FirstOrDefaultAsync(ct)
            ?? throw new AppException(404, "This inbox has expired or is no longer available.");

    public async Task<MailboxDocument?> FindRecipientAsync(string address, CancellationToken ct)
    {
        var normalized = address.Trim().ToLowerInvariant();
        var mailbox = await Mailboxes.Find(x => x.Address == normalized && (x.IsPermanent || x.ExpiresAt > DateTime.UtcNow)).FirstOrDefaultAsync(ct);
        if (mailbox is null || mailbox.MessageCount >= _options.MaxMessagesPerMailbox) return null;
        return await Domains.Find(x => x.Name == mailbox.Domain && x.Enabled).AnyAsync(ct) ? mailbox : null;
    }

    public async Task<MailboxDocument> CreateMailboxAsync(string owner, string? localPart, string? domain, CancellationToken ct, bool permanent = false)
    {
        if (permanent && !owner.StartsWith("user:", StringComparison.Ordinal)) throw new AppException(401, "Sign in to reserve a permanent address.");
        var limit = owner.StartsWith("user:", StringComparison.Ordinal) ? _options.MaxMailboxesPerAccount : _options.MaxMailboxesPerSession;
        if (await Mailboxes.CountDocumentsAsync(x => x.OwnerHash == owner && (x.IsPermanent || x.ExpiresAt > DateTime.UtcNow), cancellationToken: ct) >= limit)
            throw new AppException(409, $"You can keep {limit} inboxes. Delete one to make room.");
        var domains = await GetDomainsAsync(ct: ct);
        var selected = string.IsNullOrWhiteSpace(domain) ? domains.FirstOrDefault()?.Name : AddressRules.NormalizeDomain(domain);
        if (selected is null || !domains.Any(x => x.Name == selected)) throw new AppException(400, "That domain is not available.");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var name = string.IsNullOrWhiteSpace(localPart) ? AddressRules.RandomLocalPart() : AddressRules.NormalizeLocalPart(localPart);
            var mailbox = new MailboxDocument { Address = $"{name}@{selected}", Domain = selected, OwnerHash = owner,
                IsPermanent = permanent, ExpiresAt = permanent ? null : DateTime.UtcNow.AddMinutes(_options.LifetimeMinutes) };
            try
            {
                await Reservations.InsertOneAsync(new AddressReservation { Address = mailbox.Address, ExpiresAt = mailbox.ExpiresAt?.AddDays(_options.AddressReuseDelayDays) }, cancellationToken: ct);
                await Mailboxes.InsertOneAsync(mailbox, cancellationToken: ct);
                return mailbox;
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                if (!string.IsNullOrWhiteSpace(localPart)) throw new AppException(409, "That address is already taken or recently expired. Try another name.");
            }
        }
        throw new AppException(503, "We couldn't create an address just now. Please try again.");
    }

    public async Task DeleteMailboxAsync(string id, string owner, CancellationToken ct)
    {
        var mailbox = await RequireMailboxAsync(id, owner, ct);
        // Delete the mailbox first so reads and new delivery are blocked immediately.
        await Mailboxes.DeleteOneAsync(x => x.Id == id && x.OwnerHash == owner, ct);
        await Messages.DeleteManyAsync(x => x.MailboxId == id, ct);
        if (mailbox.IsPermanent)
            await Reservations.UpdateOneAsync(x => x.Address == mailbox.Address, Builders<AddressReservation>.Update.Set(x => x.ExpiresAt, DateTime.UtcNow.AddDays(_options.AddressReuseDelayDays)), cancellationToken: ct);
    }

    public Task<List<MessageDocument>> GetMessagesAsync(string mailboxId, CancellationToken ct) =>
        Messages.Find(x => x.MailboxId == mailboxId && x.ExpiresAt > DateTime.UtcNow)
            .Project<MessageDocument>(Builders<MessageDocument>.Projection.Exclude(x => x.TextBody).Exclude(x => x.HtmlBody).Exclude("Attachments.Content"))
            .SortByDescending(x => x.ReceivedAt).Limit(_options.MaxMessagesPerMailbox).ToListAsync(ct);

    public async Task<MessageDocument> RequireMessageAsync(string mailboxId, string messageId, CancellationToken ct) =>
        await Messages.Find(x => x.Id == messageId && x.MailboxId == mailboxId && x.ExpiresAt > DateTime.UtcNow).FirstOrDefaultAsync(ct)
            ?? throw new AppException(404, "This message is no longer available.");

    public async Task<bool> SaveMessageAsync(MailboxDocument mailbox, MessageDocument message, CancellationToken ct)
    {
        if (await Messages.Find(x => x.MailboxId == mailbox.Id && x.DeliveryHash == message.DeliveryHash).AnyAsync(ct)) return true;
        var result = await Mailboxes.UpdateOneAsync(x => x.Id == mailbox.Id && (x.IsPermanent || x.ExpiresAt > DateTime.UtcNow) && x.MessageCount < _options.MaxMessagesPerMailbox,
            Builders<MailboxDocument>.Update.Inc(x => x.MessageCount, 1), cancellationToken: ct);
        if (result.ModifiedCount == 0) return false;
        message.MailboxId = mailbox.Id;
        message.Recipient = mailbox.Address;
        var domain = await Domains.Find(x => x.Name == mailbox.Domain).FirstOrDefaultAsync(ct);
        var retention = domain?.RetentionMinutes ?? (await GetSettingsAsync(ct)).MessageRetentionMinutes;
        message.ExpiresAt = message.ReceivedAt.AddMinutes(retention);
        if (mailbox.ExpiresAt is { } expires && expires < message.ExpiresAt) message.ExpiresAt = expires;
        try { await Messages.InsertOneAsync(message, cancellationToken: ct); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            await Mailboxes.UpdateOneAsync(x => x.Id == mailbox.Id, Builders<MailboxDocument>.Update.Inc(x => x.MessageCount, -1), cancellationToken: CancellationToken.None);
            return true;
        }
        catch
        {
            await Mailboxes.UpdateOneAsync(x => x.Id == mailbox.Id, Builders<MailboxDocument>.Update.Inc(x => x.MessageCount, -1), cancellationToken: CancellationToken.None);
            throw;
        }
        // A concurrent delete must not leave a deliverable orphan behind.
        if (!await Mailboxes.Find(x => x.Id == mailbox.Id && (x.IsPermanent || x.ExpiresAt > DateTime.UtcNow)).AnyAsync(ct))
        {
            await Messages.DeleteOneAsync(x => x.Id == message.Id, ct);
            return false;
        }
        return true;
    }

    public Task MarkReadAsync(string id, string mailboxId, CancellationToken ct) =>
        Messages.UpdateOneAsync(x => x.Id == id && x.MailboxId == mailboxId, Builders<MessageDocument>.Update.Set(x => x.IsRead, true), cancellationToken: ct);

    public async Task DeleteMessageAsync(string id, string mailboxId, CancellationToken ct)
    {
        var result = await Messages.DeleteOneAsync(x => x.Id == id && x.MailboxId == mailboxId, ct);
        if (result.DeletedCount > 0)
            await Mailboxes.UpdateOneAsync(x => x.Id == mailboxId && x.MessageCount > 0, Builders<MailboxDocument>.Update.Inc(x => x.MessageCount, -1), cancellationToken: ct);
    }

    public Task SetDomainAsync(string domain, bool enabled, CancellationToken ct) =>
        Domains.UpdateOneAsync(x => x.Name == domain,
            Builders<DomainDocument>.Update.Set(x => x.Enabled, enabled).SetOnInsert(x => x.CreatedAt, DateTime.UtcNow), new UpdateOptions { IsUpsert = true }, ct);

    public Task<MailSettingsDocument> GetSettingsAsync(CancellationToken ct = default) =>
        Settings.Find(x => x.Id == "global").SingleAsync(ct);

    public async Task SetRetentionAsync(int minutes, string? domain, CancellationToken ct)
    {
        if (minutes is < 1 or > 525600) throw new AppException(400, "Choose between 1 and 525600 minutes.");
        if (domain is null)
            await Settings.UpdateOneAsync(x => x.Id == "global", Builders<MailSettingsDocument>.Update.Set(x => x.MessageRetentionMinutes, minutes), cancellationToken: ct);
        else
        {
            var result = await Domains.UpdateOneAsync(x => x.Name == domain, Builders<DomainDocument>.Update.Set(x => x.RetentionMinutes, minutes), cancellationToken: ct);
            if (result.MatchedCount == 0) throw new AppException(404, "Domain not found.");
        }
        await ApplyRetentionAsync(domain, ct);
    }

    public async Task ResetDomainRetentionAsync(string domain, CancellationToken ct)
    {
        var result = await Domains.UpdateOneAsync(x => x.Name == domain, Builders<DomainDocument>.Update.Unset(x => x.RetentionMinutes), cancellationToken: ct);
        if (result.MatchedCount == 0) throw new AppException(404, "Domain not found.");
        await ApplyRetentionAsync(domain, ct);
    }

    private async Task ApplyRetentionAsync(string? domain, CancellationToken ct)
    {
        var domains = await GetDomainsAsync(true, ct);
        var global = await GetSettingsAsync(ct);
        var mailboxes = await Mailboxes.Find(domain is null ? FilterDefinition<MailboxDocument>.Empty : Builders<MailboxDocument>.Filter.Eq(x => x.Domain, domain)).ToListAsync(ct);
        foreach (var mailbox in mailboxes)
        {
            var lifetime = domains.FirstOrDefault(d => d.Name == mailbox.Domain)?.RetentionMinutes ?? global.MessageRetentionMinutes;
            BsonValue expiration = new BsonDocument("$dateAdd", new BsonDocument { { "startDate", "$ReceivedAt" }, { "unit", "minute" }, { "amount", lifetime } });
            if (mailbox.ExpiresAt is { } expires) expiration = new BsonDocument("$min", new BsonArray { expiration, new BsonDateTime(expires) });
            var pipeline = new[] { new BsonDocument("$set", new BsonDocument("ExpiresAt", expiration)) };
            await Messages.UpdateManyAsync(x => x.MailboxId == mailbox.Id, Builders<MessageDocument>.Update.Pipeline(pipeline), cancellationToken: ct);
        }
        await ClearExpiredMessagesAsync(ct);
    }

    public async Task DeleteDomainAsync(string domain, CancellationToken ct)
    {
        if ((await Domains.DeleteOneAsync(x => x.Name == domain, ct)).DeletedCount == 0)
            throw new AppException(404, "Domain not found.");
    }

    public async Task<MailboxDocument> ReserveMailboxAsync(string id, string owner, CancellationToken ct)
    {
        if (!owner.StartsWith("user:", StringComparison.Ordinal)) throw new AppException(401, "Sign in to reserve an address.");
        var mailbox = await RequireMailboxAsync(id, owner, ct);
        if (!await Domains.Find(x => x.Name == mailbox.Domain && x.Enabled).AnyAsync(ct)) throw new AppException(400, "That domain is no longer available.");
        // Reserve first: a partial failure must never make an address available to somebody else.
        await Reservations.UpdateOneAsync(x => x.Address == mailbox.Address, Builders<AddressReservation>.Update.Unset(x => x.ExpiresAt), cancellationToken: ct);
        await Mailboxes.UpdateOneAsync(x => x.Id == id && x.OwnerHash == owner,
            Builders<MailboxDocument>.Update.Set(x => x.IsPermanent, true).Unset(x => x.ExpiresAt), cancellationToken: ct);
        await ApplyRetentionAsync(mailbox.Domain, ct);
        return await RequireMailboxAsync(id, owner, ct);
    }

    public Task ClaimGuestMailboxesAsync(string guest, string account, CancellationToken ct) =>
        Mailboxes.UpdateManyAsync(x => x.OwnerHash == guest && !x.IsPermanent && x.ExpiresAt > DateTime.UtcNow,
            Builders<MailboxDocument>.Update.Set(x => x.OwnerHash, account), cancellationToken: ct);

    public async Task ClearExpiredMessagesAsync(CancellationToken ct)
    {
        await Messages.DeleteManyAsync(x => x.ExpiresAt <= DateTime.UtcNow, ct);
        var mailboxes = await Mailboxes.Find(x => x.IsPermanent || x.ExpiresAt > DateTime.UtcNow).ToListAsync(ct);
        foreach (var mailbox in mailboxes)
        {
            var count = (int)await Messages.CountDocumentsAsync(x => x.MailboxId == mailbox.Id && x.ExpiresAt > DateTime.UtcNow, cancellationToken: ct);
            await Mailboxes.UpdateOneAsync(x => x.Id == mailbox.Id && x.MessageCount == mailbox.MessageCount,
                Builders<MailboxDocument>.Update.Set(x => x.MessageCount, count), cancellationToken: ct);
        }
    }

    public async Task<object> GetStatsAsync(CancellationToken ct) => new
    {
        activeMailboxes = await Mailboxes.CountDocumentsAsync(x => x.IsPermanent || x.ExpiresAt > DateTime.UtcNow, cancellationToken: ct),
        storedMessages = await Messages.CountDocumentsAsync(x => x.ExpiresAt > DateTime.UtcNow, cancellationToken: ct),
        activeDomains = await Domains.CountDocumentsAsync(x => x.Enabled, cancellationToken: ct),
        storage = "MongoDB", lifetimeMinutes = _options.LifetimeMinutes,
        maxMessageBytes = _options.MaxMessageBytes, maxMessagesPerMailbox = _options.MaxMessagesPerMailbox
    };
}
