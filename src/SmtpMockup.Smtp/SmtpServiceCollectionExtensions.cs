using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;

namespace SmtpMockup.Smtp;

/// <summary>Registro de DI del proyecto <c>Smtp</c>.</summary>
public static class SmtpServiceCollectionExtensions
{
    /// <summary>
    /// Registra un listener SMTP. Se llama una vez por puerto habilitado (RF-05), de modo
    /// que 8025 y 8443 se puedan activar por separado.
    /// </summary>
    /// <remarks>
    /// El listener se registra como servicio <em>con clave</em> por <paramref name="kind"/>:
    /// un <c>TryAddSingleton</c> normal se deduplicaría por tipo y el segundo puerto acabaría
    /// arrancando otra vez el listener del primero.
    /// </remarks>
    /// <param name="services">Contenedor de servicios.</param>
    /// <param name="kind">Qué listener se registra.</param>
    /// <returns>El mismo contenedor, para encadenar llamadas.</returns>
    public static IServiceCollection AddSmtpMockupListener(
        this IServiceCollection services,
        SmtpEndpointKind kind)
    {
        ArgumentNullException.ThrowIfNull(services);

        // El resolver es singleton: resuelve (y cachea) un único certificado para todo el
        // proceso, de modo que todas las sesiones de 8443 presentan el mismo thumbprint.
        services.TryAddSingleton<ICertificateProvider, CertificateProvider>();

        services.AddKeyedSingleton(
            kind,
            static (provider, key) => new SmtpListenerService(
                (SmtpEndpointKind)(key ?? throw new InvalidOperationException(
                    "The listener registration requires a SmtpEndpointKind key.")),
                provider.GetRequiredService<IOptions<SmtpMockupOptions>>(),
                provider,
                provider.GetRequiredService<ICertificateProvider>(),
                provider.GetRequiredService<ILogger<SmtpListenerService>>()));

        // IHostedService no admite claves, así que se resuelve el listener con clave y se
        // expone como hosted service normal: el host los arranca todos, en orden de registro.
        services.AddSingleton<IHostedService>(
            provider => provider.GetRequiredKeyedService<SmtpListenerService>(kind));

        return services;
    }
}
