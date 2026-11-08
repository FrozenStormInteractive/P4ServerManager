namespace P4ServerManager.Daemon.Services.Instances.Admin;

/// <param name="Name">User name.</param>
/// <param name="Password">Password, in memory only: the database stores it encrypted.</param>
public sealed record SuperUserCredentials(string Name, string Password)
{
    // Keeps the password out of logs that print the record.
    public override string ToString()
    {
        return $"SuperUserCredentials {{ Name = {Name} }}";
    }
}
