using System.Text.Json.Serialization;

using FluentValidation;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Filters;
using P4ServerManager.Daemon.Hosting;
using P4ServerManager.Daemon.Hubs;
using P4ServerManager.Daemon.Options;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Authentication;
using P4ServerManager.Daemon.Services.Backups;
using P4ServerManager.Daemon.Services.Builds;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Operations;
using P4ServerManager.Daemon.Services.Perforce;
using P4ServerManager.Daemon.Services.Processes;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddOptions<DaemonOptions>()
    .Bind(builder.Configuration.GetSection(DaemonOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<DaemonPaths>();
builder.AddCliSocket();
builder.AddDaemonAuthentication();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContextFactory<DaemonDbContext>((services, options) =>
    options.UseSqlite($"Data Source={services.GetRequiredService<DaemonPaths>().DatabasePath}"));

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(PerforceFtpClient.HttpClientName, (services, client) =>
{
    string baseUrl = services.GetRequiredService<IOptions<DaemonOptions>>().Value.PerforceFtpBaseUrl;
    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
});
builder.Services.AddSingleton(_ => P4ServerPlatformDefinition.Detect());
builder.Services.AddSingleton<PerforceFtpClient>();
builder.Services.AddSingleton<IP4ServerReleaseCatalog, P4ServerReleaseCatalog>();
builder.Services.AddSingleton<P4ServerBuildArchive>();
builder.Services.AddSingleton<P4ServerBuildCache>();
builder.Services.AddSingleton<IP4ServerBuildCache>(services => services.GetRequiredService<P4ServerBuildCache>());
builder.Services.AddSingleton<BuildNotifier>();
builder.Services.AddSingleton<BuildImporter>();
builder.Services.AddSingleton<BuildArchiveSynchronizer>();
builder.Services.AddSingleton<BuildArchiveWorker>();
builder.Services.AddHostedService(services => services.GetRequiredService<BuildArchiveWorker>());
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();

builder.Services.AddSingleton<OperationTracker>();
builder.Services.AddSingleton<OperationRunner>();
builder.Services.AddInstanceServices();
builder.Services.AddBackupServices();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// Encrypts the stored passwords. The keys live in the data directory, readable by the daemon only.
builder.Services.AddDataProtection().SetApplicationName("P4ServerManager");
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<DaemonPaths, ILoggerFactory>((options, paths, loggerFactory) =>
        options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(paths.KeysDirectory), loggerFactory));

builder.Services.AddControllers(options =>
    {
        // Keys the validation errors by the JSON names, as RequestValidationFilter does.
        options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider());
        options.Filters.Add<RequestValidationFilter>(RequestValidationFilter.Order);
        options.Filters.Add<PerforceFtpExceptionFilter>();
    })
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<SessionCookieDocumentTransformer>());

var app = builder.Build();

await PrepareDataDirectoryAsync(app.Services);

app.UseDefaultFiles();
// The web interface loads without a session: it shows the sign-in page by itself.
app.MapStaticAssets().AllowAnonymous();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

// The CLI talks plain HTTP through its socket: redirecting it to the HTTPS port would send it off the socket.
app.UseWhen(context => !CliSocket.IsCliSocketRequest(context), branch => branch.UseHttpsRedirection());

app.UseDaemonAuthentication();

app.MapControllers();
// A hub connection ends with the session it was opened with.
app.MapHub<InstancesHub>(InstancesHub.Path, options => options.CloseOnAuthenticationExpiration = true);
app.MapHub<BuildsHub>(BuildsHub.Path, options => options.CloseOnAuthenticationExpiration = true);

app.MapFallbackToFile("/index.html").AllowAnonymous();

app.Run();

// Creates the data directory, brings the database schema up to date and creates the first account from the file of the
// install wizard, before the hosted services read the database and before the daemon listens.
static async Task PrepareDataDirectoryAsync(IServiceProvider services)
{
    DaemonPaths paths = services.GetRequiredService<DaemonPaths>();
    Directory.CreateDirectory(paths.DataDirectory);
    if (OperatingSystem.IsWindows())
    {
        Directory.CreateDirectory(paths.KeysDirectory);
    }
    else
    {
        Directory.CreateDirectory(paths.KeysDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    IDbContextFactory<DaemonDbContext> dbContextFactory =
        services.GetRequiredService<IDbContextFactory<DaemonDbContext>>();
    await using (DaemonDbContext db = await dbContextFactory.CreateDbContextAsync())
    {
        await db.Database.MigrateAsync();
    }

    await services.GetRequiredService<InitialUserBootstrapper>().RunAsync(CancellationToken.None);
}

/// <summary>
/// Entry point, made visible to the integration tests.
/// </summary>
public partial class Program;
