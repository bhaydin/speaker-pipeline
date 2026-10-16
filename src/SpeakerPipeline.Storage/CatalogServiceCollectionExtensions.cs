using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

public static class CatalogServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogStorage(this IServiceCollection services, IConfiguration configuration)
        => services.AddSingleton<ICatalogRepository>(sp => new CatalogRepository(
            sp.GetRequiredService<TableServiceClient>().GetTableClient(configuration["Storage:CatalogTableName"] ?? "Catalog")));
}
