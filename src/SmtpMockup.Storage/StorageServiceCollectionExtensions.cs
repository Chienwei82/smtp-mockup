using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Storage;

/// <summary>Registro de DI del proyecto <c>Storage</c>.</summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registra <see cref="FileSystemMessageStore"/> como <see cref="IMessageStore"/>
    /// (interfaz única de Core) y las opciones de JSON del contrato SPEC §7:
    /// camelCase, sin ignorar nulos y con sangrado según <c>Storage:WriteIndented</c>.
    /// </summary>
    public static IServiceCollection AddSmtpMockupStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<StorageOptions>(configuration.GetSection("Storage"));

        services.TryAddSingleton(provider =>
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                WriteIndented = provider.GetRequiredService<IOptions<StorageOptions>>().Value.WriteIndented,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            };

            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            return options;
        });

        services.TryAddSingleton<FileSystemMessageStore>();
        services.TryAddSingleton<IMessageStore>(provider =>
            provider.GetRequiredService<FileSystemMessageStore>());

        return services;
    }
}
