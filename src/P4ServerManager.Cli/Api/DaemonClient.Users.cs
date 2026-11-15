using System.Net.Http.Json;

using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Api;

// Accounts of the web interface. The CLI needs none: the socket is trusted.
internal sealed partial class DaemonClient
{
    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken cancellationToken)
    {
        return SendAsync(HttpMethod.Get, "api/users", Json.IReadOnlyListUserDto, cancellationToken);
    }

    public Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Post,
            "api/users",
            Json.UserDto,
            cancellationToken,
            JsonContent.Create(request, Json.CreateUserRequest));
    }

    public async Task SetUserPasswordAsync(
        Guid id,
        SetUserPasswordRequest request,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Put, $"api/users/{id}/password")
            {
                Content = JsonContent.Create(request, Json.SetUserPasswordRequest),
            },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<UserDto> SetUserAdministratorAsync(
        Guid id,
        SetUserAdministratorRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            HttpMethod.Put,
            $"api/users/{id}/administrator",
            Json.UserDto,
            cancellationToken,
            JsonContent.Create(request, Json.SetUserAdministratorRequest));
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"api/users/{id}"),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Finds an account by its name, whatever its case.
    /// </summary>
    /// <exception cref="CliException">No account has this name.</exception>
    public async Task<UserDto> ResolveUserAsync(string userName, CancellationToken cancellationToken)
    {
        IReadOnlyList<UserDto> users = await GetUsersAsync(cancellationToken);
        return users.FirstOrDefault(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException($"No account is named {userName}. List them with 'p4sm user list'.");
    }
}
