using Iron.Api.Modules;
using Iron.Data;
using Iron.Data.Seeding;
using Iron.Files.Api;
using Iron.Files.Contracts;
using Iron.Files.Data;
using Iron.Files.Services;
using Iron.Kernel;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Files;

/// <summary>Files (TF-160, thin in S1): metadata in <c>files.files</c>, bytes in the tenant's blob container, the hash checked on download.</summary>
public sealed class FilesModule : IModule
{
    public string Name => "files";

    public IReadOnlyList<Permission> Permissions => FilesPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new StorageOptions();
        configuration.GetSection(StorageOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddIronDbContext<FilesDbContext>();
        services.AddSingleton<IObjectStorage, BlobObjectStorage>();
        services.AddScoped<FileService>();
        services.AddScoped<ISeeder, FilesSeeder>();
        services.AddHealthChecks().AddCheck<ObjectStorageHealthCheck>("object-storage", tags: ["ready"]);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        FilesEndpoints.Map(endpoints);
    }
}
