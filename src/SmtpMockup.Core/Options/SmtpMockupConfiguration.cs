using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace SmtpMockup.Core.Options;

/// <summary>
/// Enlaza y valida la configuración de smtp-mockup sin depender de DI. Lo usa el Host
/// para validar <em>antes</em> de <c>WebApplication.Build()</c> (así no se abre ningún
/// socket con una configuración inválida) y los tests para ejercitar los mismos mensajes.
/// </summary>
public static class SmtpMockupConfiguration
{
    /// <summary>
    /// Devuelve la configuración tipada, o lanza <see cref="OptionsValidationException"/> con
    /// todos los errores encontrados (fail-fast, SPEC §6).
    /// </summary>
    /// <param name="configuration">Configuración con las secciones de la aplicación.</param>
    /// <exception cref="OptionsValidationException">Alguna clave es inválida.</exception>
    public static SmtpMockupOptions BindAndValidate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SmtpMockupOptions
        {
            Hosting = configuration.GetSection("Hosting").Get<HostingOptions>() ?? new HostingOptions(),
            Smtp = configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions(),
            Certificate = configuration.GetSection("Certificate").Get<CertificateOptions>()
                ?? new CertificateOptions(),
            Storage = configuration.GetSection("Storage").Get<StorageOptions>()
                ?? new StorageOptions(),
            Web = configuration.GetSection("Web").Get<WebOptions>() ?? new WebOptions(),
        };

        var failures = new List<string>();
        failures.AddRange(Failures(new HostingOptionsValidator(), options.Hosting));
        failures.AddRange(Failures(new SmtpOptionsValidator(), options.Smtp));
        failures.AddRange(Failures(new CertificateOptionsValidator(), options.Certificate));
        failures.AddRange(Failures(new StorageOptionsValidator(), options.Storage));
        failures.AddRange(Failures(new WebOptionsValidator(), options.Web));
        failures.AddRange(Failures(new SmtpMockupOptionsValidator(), options));

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                $"Invalid smtp-mockup configuration:{Environment.NewLine}  - " +
                string.Join($"{Environment.NewLine}  - ", failures),
                typeof(SmtpMockupOptions),
                failures);
        }

        return options;
    }

    private static IEnumerable<string> Failures<T>(IValidateOptions<T> validator, T value)
        where T : class
        => validator.Validate(null, value).Failures ?? [];
}