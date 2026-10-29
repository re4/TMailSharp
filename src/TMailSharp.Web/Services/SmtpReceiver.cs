using System.Buffers;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using TMailSharp.Configuration;
using TMailSharp.Data;

namespace TMailSharp.Services;

public sealed class SmtpReceiver(IOptions<SmtpOptions> smtpOptions, IOptions<MailOptions> mailOptions,
    MongoMailStore store, MailProcessor processor, ILogger<SmtpReceiver> logger) : BackgroundService
{
    public bool IsListening { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = smtpOptions.Value;
        if (!config.Enabled) return;
        var builder = new SmtpServerOptionsBuilder().ServerName(config.ServerName)
            .MaxMessageSize(mailOptions.Value.MaxMessageBytes, MaxMessageSizeHandling.Strict)
            .CommandWaitTimeout(TimeSpan.FromSeconds(60))
            .Endpoint(endpoint =>
            {
                endpoint.Endpoint(new IPEndPoint(IPAddress.Parse(config.Host), config.Port))
                    .AuthenticationRequired(false).AllowUnsecureAuthentication(false)
                    .SessionTimeout(TimeSpan.FromMinutes(2));
                if (!string.IsNullOrWhiteSpace(config.CertificatePath))
                    endpoint.Certificate(X509CertificateLoader.LoadPkcs12FromFile(config.CertificatePath, config.CertificatePassword))
                        .SupportedSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);
            });
        var services = new SmtpServer.ComponentModel.ServiceProvider();
        services.Add((IMailboxFilterFactory)new RecipientFilterFactory(store, mailOptions.Value));
        services.Add((IMessageStore)new IncomingStore(processor, logger));
        var server = new SmtpServer.SmtpServer(builder.Build(), services);
        try
        {
            var running = server.StartAsync(stoppingToken);
            IsListening = !running.IsCompleted;
            logger.LogInformation("Incoming SMTP listening on {Host}:{Port}", config.Host, config.Port);
            await running;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { IsListening = false; }
    }

    private sealed class RecipientFilterFactory(MongoMailStore database, MailOptions options) : IMailboxFilterFactory
    {
        public IMailboxFilter CreateInstance(ISessionContext context) => new RecipientFilter(database, options);
    }

    private sealed class RecipientFilter(MongoMailStore database, MailOptions options) : MailboxFilter
    {
        private int _recipients;
        public override Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
        {
            _recipients = 0;
            return Task.FromResult(size <= options.MaxMessageBytes);
        }

        public override async Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
        {
            if (_recipients >= 20 || await database.FindRecipientAsync($"{to.User}@{to.Host}", cancellationToken) is null) return false;
            _recipients++;
            return true;
        }
    }

    private sealed class IncomingStore(MailProcessor mailProcessor, ILogger logger) : MessageStore
    {
        public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                var recipients = transaction.To.Select(x => $"{x.User}@{x.Host}").ToArray();
                var count = await mailProcessor.DeliverAsync(buffer.ToArray(), recipients, cancellationToken);
                return count > 0 ? SmtpResponse.Ok : new SmtpResponse(SmtpReplyCode.TransactionFailed, "No active recipient could receive this message.");
            }
            catch (AppException) { return new SmtpResponse(SmtpReplyCode.TransactionFailed, "Message rejected by size or attachment limits."); }
            catch (FormatException) { return new SmtpResponse(SmtpReplyCode.TransactionFailed, "Invalid MIME message."); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Incoming message could not be stored");
                return new SmtpResponse((SmtpReplyCode)451, "Temporary storage failure. Retry later.");
            }
        }
    }
}
