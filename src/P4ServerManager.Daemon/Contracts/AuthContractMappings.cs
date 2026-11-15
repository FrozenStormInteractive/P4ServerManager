using P4ServerManager.Contracts;
using P4ServerManager.Daemon.Database.Entities;

namespace P4ServerManager.Daemon.Contracts;

/// <summary>
/// Maps the accounts of the web interface to the types of the API.
/// </summary>
public static class AuthContractMappings
{
    public static UserDto ToDto(this User user)
    {
        return new UserDto(user.Id, user.UserName, user.CreatedAt, user.LastSignInAt);
    }
}
