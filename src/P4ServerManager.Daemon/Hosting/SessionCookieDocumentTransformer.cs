using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace P4ServerManager.Daemon.Hosting;

/// <summary>
/// Describes the session cookie in the OpenAPI document: every operation asks for it, except the ones that allow
/// anonymous requests. The requests through the socket of the CLI need no cookie.
/// </summary>
public sealed class SessionCookieDocumentTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeId = "sessionCookie";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = DaemonAuthentication.CookieName,
            Description = "Session of a user of the web interface, set by POST /api/auth/login. Requests that change "
                + $"something also carry the {BrowserRequestGuard.HeaderName} header. Requests through the socket of "
                + "the CLI need neither.",
        };

        OpenApiSecurityRequirement requirement = new()
        {
            [new OpenApiSecuritySchemeReference(SchemeId, document)] = [],
        };

        HashSet<string> anonymous = context.DescriptionGroups
            .SelectMany(g => g.Items)
            .Where(d => d.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            .Select(d => $"{d.HttpMethod}:/{d.RelativePath}".ToUpperInvariant())
            .ToHashSet();

        foreach ((string path, IOpenApiPathItem pathItem) in document.Paths)
        {
            foreach ((HttpMethod method, OpenApiOperation operation) in pathItem.Operations ?? [])
            {
                if (!anonymous.Contains($"{method.Method}:{path}".ToUpperInvariant()))
                {
                    operation.Security = [requirement];
                }
            }
        }

        return Task.CompletedTask;
    }
}
