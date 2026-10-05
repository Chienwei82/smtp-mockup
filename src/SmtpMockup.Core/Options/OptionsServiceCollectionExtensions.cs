using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SmtpMockup.Core.Options;

/// <summary>
/// Registro de la configuración tipada de smtp-mockup. Todos los bloques se validan
/// al arrancar (<c>ValidateOnStart</c>): un valor inválido mata el host con un mensaje
/// explícito en vez de usar un default silencioso (SPEC §6).
/// </summary>
public static class OptionsServiceCollectionExtensions
{
    /// <summary>
    /// Enlaza y valida <c>Smtp</c>, <c>Certificate</c>, <c>Storage</c> y <c>Web</c>.
    /// </summary>
    /// <param name="services">Colección de servicios.</param>
    /// <param name="configuration">Configuración con las secciones de la aplicación.</param>
    /// <returns>La misma colección, para encadenar llamadas.</returns>
    public static IServiceCollection AddSmtpMockupOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Los validadores se registran como IValidateOptions<T>: Options los ejecuta y,
        // con ValidateOnStart, una configuración inválida detiene el arranque.
        services.AddSingleton<IValidateOptions<HostingOptions>, HostingOptionsValidator>();
        services.AddSingleton<IValidateOptions<SmtpMockupOptions>, SmtpMockupOptionsValidator>();
        services.AddSingleton<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>();
        services.AddSingleton<IValidateOptions<CertificateOptions>, CertificateOptionsValidator>();
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
        services.AddSingleton<IValidateOptions<WebOptions>, WebOptionsValidator>();

        services.AddOptions<HostingOptions>()
            .Bind(configuration.GetSection("Hosting"))
            .ValidateOnStart();

        services.AddOptions<SmtpMockupOptions>()
            .Bind(configuration)
            .ValidateOnStart();

        services.AddOptions<SmtpOptions>()
            .Bind(configuration.GetSection("Smtp"))
            .ValidateOnStart();

        services.AddOptions<CertificateOptions>()
            .Bind(configuration.GetSection("Certificate"))
            .ValidateOnStart();

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection("Storage"))
            .ValidateOnStart();

        services.AddOptions<WebOptions>()
            .Bind(configuration.GetSection("Web"))
            .ValidateOnStart();

        return services;
    }
}