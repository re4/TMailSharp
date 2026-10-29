using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using TMailSharp.Api;
using TMailSharp.Configuration;
using TMailSharp.Data;
using TMailSharp.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false).AddEnvironmentVariables();
builder.Services.AddOptions<MongoOptions>().BindConfiguration(MongoOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<MailOptions>().BindConfiguration(MailOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SmtpOptions>().BindConfiguration(SmtpOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.Section));
var keyDirectory = builder.Configuration["DataProtection:KeyDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, ".keys");
var protection = builder.Services.AddDataProtection().SetApplicationName("TMailSharp");
if (!string.IsNullOrWhiteSpace(keyDirectory)) protection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
builder.Services.AddSingleton<MongoMailStore>();
builder.Services.AddSingleton<MailProcessor>();
builder.Services.AddSingleton<SmtpReceiver>();
builder.Services.AddHostedService(services => services.GetRequiredService<SmtpReceiver>());
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "tmailsharp.admin";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(4);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var address in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        options.KnownProxies.Add(System.Net.IPAddress.Parse(address));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, ct) => await context.HttpContext.Response.WriteAsJsonAsync(new { error = "A few too many requests. Try again in a minute." }, ct);
    foreach (var (policy, permits) in new[] { ("api", 180), ("create", 12), ("login", 5) })
        options.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32_768);

var app = builder.Build();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Demo:Enabled"))
    throw new InvalidOperationException("Demo delivery can only be enabled in Development.");
var adminPassword = builder.Configuration["Admin:Password"];
if (!string.IsNullOrEmpty(adminPassword) && adminPassword.Length < 20)
    throw new InvalidOperationException("Admin:Password must contain at least 20 characters.");

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-src 'self' about:; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (context.Request.Headers["X-TMail-Request"] != "1" ||
                (origin.Length > 0 && !string.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "This request must come from your TMailSharp tab." });
                return;
            }
        }
        var token = context.Request.Cookies["tmailsharp.session"];
        if (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit))
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append("tmailsharp.session", token, new CookieOptions
            { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(2), IsEssential = true, Path = "/" });
        }
        context.Items["OwnerHash"] = AddressRules.Hash(token);
    }
    try { await next(context); }
    catch (AppException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (Exception ex) when (!context.Response.HasStarted && ex is not OperationCanceledException)
    {
        app.Logger.LogError(ex, "Request failed");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "Something went wrong on the server. Please try again." });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapMailApi();
app.MapGet("/health", async (MongoMailStore store, SmtpReceiver receiver, Microsoft.Extensions.Options.IOptions<SmtpOptions> smtp, CancellationToken ct) =>
{
    await store.PingAsync(ct);
    return !smtp.Value.Enabled || receiver.IsListening ? Results.Ok(new { status = "healthy" }) : Results.StatusCode(503);
});
app.MapGet("/admin", () => Results.Redirect("/admin.html"));

try { await app.Services.GetRequiredService<MongoMailStore>().InitializeAsync(); }
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "Cannot initialize MongoDB. Start MongoDB or set Mongo__ConnectionString. See README.md.");
    throw;
}
await app.RunAsync();

public partial class Program;
