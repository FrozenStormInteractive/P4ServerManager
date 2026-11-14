using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace P4ServerManager.Daemon.Tests.Api;

public sealed class FileSystemApiTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private readonly string _browsedDirectory = Path.Combine(Path.GetTempPath(), "p4sm-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_browsedDirectory);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Daemon:DataDirectory", _dataDirectory);
            // The tests only check the API: nothing is downloaded or started.
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        });
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string directory in new[] { _dataDirectory, _browsedDirectory })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetDirectories_ListsTheSubfoldersByName()
    {
        Directory.CreateDirectory(Path.Combine(_browsedDirectory, "beta"));
        Directory.CreateDirectory(Path.Combine(_browsedDirectory, "Alpha"));
        DirectoryInfo hidden = Directory.CreateDirectory(Path.Combine(_browsedDirectory, ".hidden"));
        if (OperatingSystem.IsWindows())
        {
            hidden.Attributes |= FileAttributes.Hidden;
        }

        await File.WriteAllTextAsync(Path.Combine(_browsedDirectory, "file.txt"), "", TestContext.Current.CancellationToken);

        JsonElement listing = await GetListingAsync(_browsedDirectory + Path.DirectorySeparatorChar);

        listing.GetProperty("path").GetString().ShouldBe(ApiPath(_browsedDirectory));
        listing.GetProperty("parent").GetString().ShouldBe(ApiPath(Path.GetDirectoryName(_browsedDirectory)!));
        listing.GetProperty("directories").EnumerateArray()
            .Select(d => (d.GetProperty("name").GetString(), d.GetProperty("path").GetString()))
            .ShouldBe([
                ("Alpha", ApiPath(Path.Combine(_browsedDirectory, "Alpha"))),
                ("beta", ApiPath(Path.Combine(_browsedDirectory, "beta"))),
            ]);
        JsonElement ancestors = listing.GetProperty("ancestors");
        ancestors[ancestors.GetArrayLength() - 1].GetProperty("path").GetString().ShouldBe(ApiPath(_browsedDirectory));
        ancestors[0].GetProperty("path").GetString().ShouldBe(ApiPath(Path.GetPathRoot(_browsedDirectory)!));
        listing.GetProperty("path").GetString().ShouldNotContain('\\');
        listing.GetProperty("roots").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task GetDirectories_ListsARootWithoutPath()
    {
        JsonElement listing = await GetListingAsync(null);

        string path = listing.GetProperty("path").GetString()!;
        ApiPath(Path.GetPathRoot(path)!).ShouldBe(path);
        listing.GetProperty("parent").ValueKind.ShouldBe(JsonValueKind.Null);
        listing.GetProperty("ancestors").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task GetDirectories_RejectsARelativePath()
    {
        HttpResponseMessage response = await _client.GetAsync(
            "/api/filesystem/directories?path=relative/folder",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("errors").TryGetProperty("path", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("file.txt")]
    public async Task GetDirectories_IsNotFoundForAMissingFolderOrAFile(string name)
    {
        await File.WriteAllTextAsync(Path.Combine(_browsedDirectory, "file.txt"), "", TestContext.Current.CancellationToken);

        HttpResponseMessage response = await _client.GetAsync(
            ListingUrl(Path.Combine(_browsedDirectory, name)),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDirectories_TellsWhichFoldersTheDaemonMayWriteTo()
    {
        string readOnly = Directory.CreateDirectory(Path.Combine(_browsedDirectory, "read-only")).FullName;
        Directory.CreateDirectory(Path.Combine(_browsedDirectory, "writable"));
        DenyWrites(readOnly);
        try
        {
            JsonElement listing = await GetListingAsync(_browsedDirectory);

            listing.GetProperty("writable").GetBoolean().ShouldBeTrue();
            listing.GetProperty("directories").EnumerateArray()
                .Select(d => (d.GetProperty("name").GetString(), d.GetProperty("writable").GetBoolean()))
                .ShouldBe([("read-only", false), ("writable", true)]);

            JsonElement inside = await GetListingAsync(readOnly);
            inside.GetProperty("writable").GetBoolean().ShouldBeFalse();
        }
        finally
        {
            AllowWrites(readOnly);
        }
    }

    [Fact]
    public async Task CreateDirectory_IsNotOffered()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/filesystem/directories",
            new { path = Path.Combine(_browsedDirectory, "main") },
            TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeFalse();
        Directory.Exists(Path.Combine(_browsedDirectory, "main")).ShouldBeFalse();
    }

    private static void DenyWrites(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            DirectoryInfo directory = new(path);
            System.Security.AccessControl.DirectorySecurity security = directory.GetAccessControl();
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.CreateFiles
                    | System.Security.AccessControl.FileSystemRights.CreateDirectories,
                System.Security.AccessControl.AccessControlType.Deny));
            directory.SetAccessControl(security);
        }
        else
        {
            Assert.SkipWhen(Environment.UserName == "root", "root may write to any folder.");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private static void AllowWrites(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            DirectoryInfo directory = new(path);
            System.Security.AccessControl.DirectorySecurity security = directory.GetAccessControl();
            security.RemoveAccessRuleAll(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.CreateFiles,
                System.Security.AccessControl.AccessControlType.Deny));
            directory.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // The API separates the folders with '/' on every platform.
    private static string ApiPath(string path)
    {
        return OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
    }

    private static string ListingUrl(string? path)
    {
        return path is null
            ? "/api/filesystem/directories"
            : $"/api/filesystem/directories?path={Uri.EscapeDataString(path)}";
    }

    private async Task<JsonElement> GetListingAsync(string? path)
    {
        HttpResponseMessage response = await _client.GetAsync(ListingUrl(path), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }
}
