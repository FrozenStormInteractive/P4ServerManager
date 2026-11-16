using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

using P4ServerManager.Daemon.Options;
using P4ServerManager.Daemon.Services.Authentication;

namespace P4ServerManager.Daemon.Hosting;

/// <summary>
/// Who may use the API. Through the TCP port, a user signs in with an account of the web interface and gets a session
/// cookie. Through the socket of the CLI, no sign-in is asked (<see cref="CliSocketAuthenticationHandler"/>).
/// </summary>
public static class DaemonAuthentication
{
    /// <summary>
    /// Selects the scheme of a request: the socket or the cookie.
    /// </summary>
    public const string DefaultScheme = "Default";

    public const string CookieName = "P4SM.Auth";

    /// <summary>
    /// Policy of the endpoints that act on the account of the signed-in user, which the CLI has not.
    /// </summary>
    public const string WebUserPolicy = "WebUser";

    /// <summary>
    /// Policy of the endpoints that manage the accounts: administrators, and the CLI.
    /// </summary>
    public const string AdministratorPolicy = "Administrator";

    /// <summary>
    /// Rate limiting policy of the sign-in and of the creation of the first account.
    /// </summary>
    public const string SignInRateLimitPolicy = "sign-in";

    public static WebApplicationBuilder AddDaemonAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<DaemonAuthenticationOptions>()
            .Bind(builder.Configuration.GetSection(DaemonAuthenticationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton<UserService>();
        builder.Services.AddSingleton<InitialUserBootstrapper>();
        builder.Services.AddSingleton<IAuthorizationHandler, AdministratorAuthorizationHandler>();

        builder.Services
            .AddAuthentication(DefaultScheme)
            .AddPolicyScheme(DefaultScheme, null, options => options.ForwardDefaultSelector = context =>
                CliSocket.IsCliSocketRequest(context)
                    ? CliSocketAuthenticationHandler.SchemeName
                    : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, CliSocketAuthenticationHandler>(
                CliSocketAuthenticationHandler.SchemeName, null)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                // The package serves plain HTTP; behind a reverse proxy that serves HTTPS, the cookie is marked secure.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.SlidingExpiration = true;
                // An API: no redirection to a sign-in page, which the web interface shows by itself.
                options.Events.OnRedirectToLogin = context => WriteStatusAsync(context, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => WriteStatusAsync(context, StatusCodes.Status403Forbidden);
                options.Events.OnValidatePrincipal = ValidateSecurityStampAsync;
            });
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IOptions<DaemonAuthenticationOptions>>((cookie, authentication) =>
                cookie.ExpireTimeSpan = authentication.Value.SessionLifetime);

        builder.Services.AddAuthorizationBuilder()
            // Every endpoint asks for a signed-in user, or the CLI, unless it allows anonymous requests.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(WebUserPolicy, policy => policy
                .RequireAuthenticatedUser()
                .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme))
            .AddPolicy(AdministratorPolicy, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new AdministratorRequirement()));

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
                await Results.Problem(
                        "Too many attempts. Wait a minute, then try again.",
                        statusCode: StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context.HttpContext);
            options.AddPolicy(SignInRateLimitPolicy, context =>
            {
                int permits = context.RequestServices.GetRequiredService<IOptions<DaemonAuthenticationOptions>>()
                    .Value.SignInAttemptsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permits,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
        });

        return builder;
    }

    /// <summary>
    /// Adds the authentication, the authorization and the protections of the browser requests to the pipeline.
    /// </summary>
    public static WebApplication UseDaemonAuthentication(this WebApplication app)
    {
        app.UseMiddleware<BrowserRequestGuard>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        return app;
    }

    // Rejects a session whose account was deleted, or whose password changed since the sign-in.
    private static async Task ValidateSecurityStampAsync(CookieValidatePrincipalContext context)
    {
        Guid? userId = context.Principal?.GetUserId();
        Guid? stamp = userId is null
            ? null
            : await context.HttpContext.RequestServices.GetRequiredService<UserService>()
                .GetSecurityStampAsync(userId.Value, context.HttpContext.RequestAborted);
        if (stamp is null || stamp != context.Principal!.GetSecurityStamp())
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    private static Task WriteStatusAsync(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}
