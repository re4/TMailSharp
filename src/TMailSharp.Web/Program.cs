using System.Threading.RateLimiting;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TMailSharp.Configuration;
using TMailSharp.Data;
using TMailSharp.Identity;
using TMailSharp.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables();
builder.Services.AddOptions<MongoOptions>().BindConfiguration(MongoOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<MailOptions>().BindConfiguration(MailOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SmtpOptions>().BindConfiguration(SmtpOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ApiLimitOptions>().BindConfiguration(ApiLimitOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SecurityCheckOptions>().BindConfiguration(SecurityCheckOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.Section));
var configuredKeys = builder.Configuration["DataProtection:KeyDirectory"];
var dataDirectory = builder.Configuration["Identity:DataDirectory"] ?? (configuredKeys is null
    ? Path.Combine(builder.Environment.ContentRootPath, ".local", "identity") : Path.Combine(configuredKeys, "identity"));
if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dataDirectory);
else Directory.CreateDirectory(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
builder.Services.AddDbContext<IdentityDatabase>(options => options.UseSqlite(
    builder.Configuration.GetConnectionString("Identity") ?? $"Data Source={Path.Combine(dataDirectory, "identity.db")}"));
var keyDirectory = builder.Configuration["DataProtection:KeyDirectory"] ?? Path.Combine(dataDirectory, "keys");
if (OperatingSystem.IsWindows()) Directory.CreateDirectory(keyDirectory);
else Directory.CreateDirectory(keyDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
builder.Services.AddDataProtection().SetApplicationName("TMailSharp.v1.1.0").PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 12;
    options.Password.RequiredUniqueChars = 4;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireDigit = false;
    options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<IdentityDatabase>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = builder.Environment.IsDevelopment() ? "tmailsharp.identity.dev" : "__Host-tmailsharp.identity";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/account/denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 401;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 403;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.Configure<IdentityPasskeyOptions>(options =>
{
    options.ServerDomain = builder.Configuration["Identity:PasskeyDomain"];
    options.UserVerificationRequirement = "required";
});
builder.Services.AddAuthorization(options => options.AddPolicy("WebsiteAccount", policy =>
    policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser()));
builder.Services.AddAuthentication(options => options.DefaultAuthenticateScheme = "WebsiteOrApi")
    .AddPolicyScheme("WebsiteOrApi", null, options => options.ForwardDefaultSelector = context =>
        context.Request.Path.StartsWithSegments("/api") ? ApiKeyAuthenticationHandler.SchemeName : IdentityConstants.ApplicationScheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var address in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        options.KnownProxies.Add(System.Net.IPAddress.Parse(address));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, ct) =>
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) : 60;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = $"Too many requests. Try again in {seconds} seconds.", retryAfterSeconds = seconds }, ct);
    };
    foreach (var (policy, permits) in new[] { ("api", 180), ("create", 12), ("login", 10), ("challenge", 10) })
        options.AddPolicy(policy, context =>
        {
            var key = "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            var limit = permits;
            if (policy is "api" or "create" && context.Request.Path.StartsWithSegments("/api") &&
                context.User.Identity?.AuthenticationType == ApiKeyAuthenticationHandler.SchemeName &&
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId)
            {
                key = "account:" + userId;
                var configured = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiLimitOptions>>().Value;
                limit = policy == "create" ? configured.CreatesPerMinute : configured.RequestsPerMinute;
            }
            return RateLimitPartition.GetFixedWindowLimiter(key, _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        });
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
builder.Services.AddScoped<MailboxAccess>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ProofOfWork>();
builder.Services.AddSingleton<MongoMailStore>();
builder.Services.AddSingleton<MailProcessor>();
builder.Services.AddSingleton<SmtpReceiver>();
builder.Services.AddHostedService(services => services.GetRequiredService<SmtpReceiver>());
builder.Services.AddHostedService<RetentionService>();

var app = builder.Build();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch => branch.UseHttpsRedirection());
}
if (!app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Demo:Enabled"))
    throw new InvalidOperationException("Demo delivery can only be enabled in Development.");
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; worker-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-src 'self' about:; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    if (context.Request.Path == "/" || context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/inbox") || context.Request.Path.StartsWithSegments("/account") || context.Request.Path.StartsWithSegments("/admin"))
        context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/inbox") && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (context.Request.Headers["X-TMail-Request"] != "1" || (origin.Length > 0 && !string.Equals(origin,
            $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "The request origin could not be verified." });
            return;
        }
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
        await context.Response.WriteAsJsonAsync(new { error = "Something went wrong. Please try again." });
    }
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
await app.InitializeIdentityAsync();
await app.Services.GetRequiredService<MongoMailStore>().InitializeAsync();
await app.RunAsync();

public partial class Program;
