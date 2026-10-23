using System.Text.Json.Serialization;

using FluentValidation;

using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using P4ServerManager.Daemon.Database;
using P4ServerManager.Daemon.Filters;
using P4ServerManager.Daemon.Hubs;
using P4ServerManager.Daemon.Options;
using P4ServerManager.Daemon.Services;
using P4ServerManager.Daemon.Services.Instances;
using P4ServerManager.Daemon.Services.Instances.Provisioning;
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
builder.Services.AddSingleton<IP4ServerBuildCache, P4ServerBuildCache>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();

builder.Services.AddSingleton<InstanceRuntimeRegistry>();
builder.Services.AddSingleton<InstanceNotifier>();
builder.Services.AddSingleton<P4ServerLauncher>();
builder.Services.AddSingleton<InstanceSupervisor>();
builder.Services.AddHostedService(services => services.GetRequiredService<InstanceSupervisor>());
builder.Services.AddSingleton<OperationTracker>();
builder.Services.AddSingleton<OperationRunner>();
builder.Services.AddProvisioning();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

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
builder.Services.AddOpenApi();

var app = builder.Build();

await PrepareDataDirectoryAsync(app.Services);

app.UseDefaultFiles();
app.MapStaticAssets();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();


app.MapControllers();
app.MapHub<InstancesHub>(InstancesHub.Path);

app.MapFallbackToFile("/index.html");

app.Run();

// Creates the data directory and brings the database schema up to date, before the hosted services read it.
static async Task PrepareDataDirectoryAsync(IServiceProvider services)
{
    DaemonPaths paths = services.GetRequiredService<DaemonPaths>();
    Directory.CreateDirectory(paths.DataDirectory);

    IDbContextFactory<DaemonDbContext> dbContextFactory =
        services.GetRequiredService<IDbContextFactory<DaemonDbContext>>();
    await using DaemonDbContext db = await dbContextFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

/// <summary>
/// Entry point, made visible to the integration tests.
/// </summary>
public partial class Program;
