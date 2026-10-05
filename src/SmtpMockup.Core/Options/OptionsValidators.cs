using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Core.Options;

/// <summary>
/// Valida <see cref="HostingOptions"/>: modo conocido, carpeta de logs utilizable y nivel de
/// Event Log reconocido.
/// </summary>
public sealed class HostingOptionsValidator : IValidateOptions<HostingOptions>
{
    /// <summary>Tope razonable para la retención de logs; más que esto es un descuido.</summary>
    public const int MaxLogRetentionDays = 3_650;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HostingOptions options)
    {
        var failures = new List<string>();

        if (!Enum.TryParse<HostingMode>(options.Mode, ignoreCase: true, out var mode)
            || !Enum.IsDefined(mode))
        {
            failures.Add(HostingOptions.InvalidModeMessage(options.Mode));
        }

        if (string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            // Vacía es legítimo (desactiva el log en archivo), pero se dice explícitamente para
            // que nadie crea que es un default y se queda sin log en el servicio.
            if (Enum.TryParse<HostingMode>(options.Mode, ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed)
                && parsed == HostingMode.WindowsService)
            {
                failures.Add(
                    "'Hosting:LogDirectory' is empty: a Windows Service would run without a log file. " +
                    "Set a relative path such as 'logs' or 'Auto' to run as a console application.");
            }
        }
        else
        {
            if (options.LogDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                failures.Add(
                    $"'Hosting:LogDirectory' contains invalid characters: '{options.LogDirectory}'.");
            }

            if (string.IsNullOrWhiteSpace(options.LogFileName)
                || options.LogFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                failures.Add(
                    $"'Hosting:LogFileName' is not a valid file name: '{options.LogFileName}'.");
            }
        }

        if (options.LogRetentionDays is < 0 or > MaxLogRetentionDays)
        {
            failures.Add(
                $"'Hosting:LogRetentionDays' must be between 0 and {MaxLogRetentionDays} " +
                $"(current value: {options.LogRetentionDays}).");
        }

        if (string.IsNullOrWhiteSpace(options.EventLogSource))
        {
            failures.Add("'Hosting:EventLogSource' must not be empty.");
        }

        if (!Enum.TryParse<LogLevel>(options.EventLogLevel, ignoreCase: true, out var eventLogLevel)
            || !Enum.IsDefined(eventLogLevel))
        {
            failures.Add(
                "'Hosting:EventLogLevel' must be a Microsoft.Extensions.Logging.LogLevel value " +
                $"(Trace, Debug, Information, Warning, Error, Critical or None; " +
                $"current value: '{options.EventLogLevel}').");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Valida <see cref="SmtpOptions"/>: puertos, listeners activos y tope de tamaño.</summary>
public sealed class SmtpOptionsValidator : IValidateOptions<SmtpOptions>
{
    /// <summary>Rango válido de un puerto TCP.</summary>
    public const int MaxPort = 65_535;

    /// <summary>
    /// Tope máximo del tamaño de mensaje, en MB.
    /// </summary>
    /// <remarks>
    /// 2047, no 2048: <c>2048 * 1024 * 1024</c> es 2.147.483.648, una unidad por encima de
    /// <see cref="int.MaxValue"/>, y <see cref="SmtpOptions.MaxMessageSizeBytes"/> lo calcularía
    /// en <c>int</c> sin comprobarlo. El tope tiene que ser el del número, no un número redondo.
    /// </remarks>
    public const int MaxMessageSizeMbLimit = 2_047;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SmtpOptions options)
    {
        var failures = new List<string>();

        ValidateEndpoint("Smtp:Plain", options.Plain, failures);
        ValidateEndpoint("Smtp:StartTls", options.StartTls, failures);

        if (!options.Plain.Enabled && !options.StartTls.Enabled)
        {
            failures.Add(
                "At least one SMTP listener must be enabled: set 'Smtp:Plain:Enabled' or " +
                "'Smtp:StartTls:Enabled' to true.");
        }

        // Con ambos en 0 el sistema asigna un puerto libre a cada uno, así que no pueden
        // colisionar. Compararlos sería comparar dos valores que nadie va a usar.
        if (options.Plain.Enabled
            && options.StartTls.Enabled
            && options.Plain.Port != 0
            && options.Plain.Port == options.StartTls.Port)
        {
            failures.Add(
                $"'Smtp:Plain:Port' and 'Smtp:StartTls:Port' cannot use the same port " +
                $"({options.Plain.Port}); give each listener a different port.");
        }

        if (options.MaxConcurrentConnections < 0)
        {
            failures.Add(
                "'Smtp:MaxConcurrentConnections' must be 0 (no limit) or greater " +
                $"(current value: {options.MaxConcurrentConnections}).");
        }

        if (options.MaxMessageSizeMb is < 1 or > MaxMessageSizeMbLimit)
        {
            failures.Add(
                $"'Smtp:MaxMessageSizeMb' must be between 1 and {MaxMessageSizeMbLimit} " +
                $"(current value: {options.MaxMessageSizeMb}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateEndpoint(string key, SmtpEndpointOptions endpoint, List<string> failures)
    {
        // 0 se acepta a propósito: es la forma de pedir un puerto efímero, que es lo que necesitan
        // los tests de integración para levantar instancias en paralelo y lo que
        // SmtpListenerService ya resolvía (ResolveEphemeralPort) pero el validador rechazaba.
        if (endpoint.Port < 0 || endpoint.Port > MaxPort)
        {
            failures.Add(
                $"'{key}:Port' must be between 0 (ephemeral) and {MaxPort} " +
                $"(current value: {endpoint.Port}).");
        }

        // Una dirección que no se entiende es un error, pero una dirección válida fuera de
        // loopback no lo es: el SMTP en claro en una red es un caso legítimo (CI, contenedor,
        // pruebas de integración desde otra máquina) y sólo merece un warning en el log.
        if (!EndpointAddress.IsValidLiteral(endpoint.BindAddress))
        {
            failures.Add(EndpointAddress.InvalidLiteralMessage(endpoint.BindAddress, key));
        }

        if (!Enum.TryParse<SmtpSecurityMode>(endpoint.Security, ignoreCase: true, out var security)
            || !Enum.IsDefined(security))
        {
            failures.Add(SmtpEndpointOptions.InvalidSecurityModeMessage(endpoint.Security, key));
        }
    }
}

/// <summary>Valida <see cref="CertificateOptions"/>: modo conocido y ruta en modo <c>File</c>.</summary>
public sealed class CertificateOptionsValidator : IValidateOptions<CertificateOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CertificateOptions options)
    {
        var failures = new List<string>();

        if (!Enum.TryParse<CertificateMode>(options.Mode, ignoreCase: true, out var mode)
            || !Enum.IsDefined(mode))
        {
            failures.Add(
                $"'Certificate:Mode' must be 'Auto' or 'File' (current value: '{options.Mode}').");
        }
        else if (mode == CertificateMode.File && string.IsNullOrWhiteSpace(options.Path))
        {
            failures.Add("'Certificate:Path' is required when 'Certificate:Mode' is 'File'.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Valida <see cref="StorageOptions"/>.</summary>
public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Directory))
        {
            failures.Add("'Storage:Directory' must not be empty.");
        }
        else if (options.Directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            failures.Add($"'Storage:Directory' contains invalid characters: '{options.Directory}'.");
        }

        // Un tope negativo no es "sin tope": al compararlo contra el tamaño real del adjunto o
        // del MIME, `length > -1` es siempre cierto, así que todo se omitiría o se truncaría
        // sin que se note en ningún log.
        if (options.MaxInlineAttachmentBytes < 0)
        {
            failures.Add(
                "'Storage:MaxInlineAttachmentBytes' must be 0 or greater " +
                $"(current value: {options.MaxInlineAttachmentBytes}).");
        }

        // Aquí 0 sí significa "sin tope" (lo dice la documentación de la opción), de modo que el
        // único valor inválido es el negativo.
        if (options.MaxRawMimeBytes < 0)
        {
            failures.Add(
                "'Storage:MaxRawMimeBytes' must be 0 (no limit) or greater " +
                $"(current value: {options.MaxRawMimeBytes}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Valida <see cref="WebOptions"/>: puerto, binding, paginación y previsualización.</summary>
public sealed class WebOptionsValidator : IValidateOptions<WebOptions>
{
    /// <summary>Menor tamaño de página admisible: cero filas por página no es un listado.</summary>
    public const int MinPageSize = 1;

    /// <summary>Tope duro de filas por página (<see cref="MailQuery.HardMaxPageSize"/>).</summary>
    public const int MaxPageSize = MailQuery.HardMaxPageSize;

    /// <summary>
    /// Tope de la espera de agrupación. Por encima de unos segundos la UI "parece colgada"
    /// tras un envío masivo, que es justo lo contrario de lo que promete la actualización en vivo.
    /// </summary>
    public const int MaxLiveUpdateDebounceMilliseconds = 10_000;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, WebOptions options)
    {
        var failures = new List<string>();

        // 0 se acepta a propósito: es la forma de pedir un puerto efímero, que es lo que
        // necesitan los tests de integración para levantar dos instancias en paralelo. Los
        // listeners SMTP ya lo admitían (SmtpListenerService resuelve el puerto real después de
        // arrancar); aquí se cierra la asimetría.
        if (options.Port < 0 || options.Port > SmtpOptionsValidator.MaxPort)
        {
            failures.Add(
                $"'Web:Port' must be between 0 (ephemeral) and {SmtpOptionsValidator.MaxPort} " +
                $"(current value: {options.Port}).");
        }

        if (!EndpointAddress.IsValidLiteral(options.BindAddress))
        {
            failures.Add(EndpointAddress.InvalidLiteralMessage(options.BindAddress, "Web"));
        }

        failures.AddRange(ValidatePaging(options));

        if (options.MaxHtmlPreviewBytes <= 0)
        {
            failures.Add(
                $"'Web:MaxHtmlPreviewBytes' must be greater than 0 " +
                $"(current value: {options.MaxHtmlPreviewBytes}).");
        }

        if (options.LiveUpdateDebounceMilliseconds is < 0 or > MaxLiveUpdateDebounceMilliseconds)
        {
            failures.Add(
                $"'Web:LiveUpdateDebounceMilliseconds' must be between 0 and " +
                $"{MaxLiveUpdateDebounceMilliseconds} " +
                $"(current value: {options.LiveUpdateDebounceMilliseconds}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Reglas de paginación del listado. El tope duro existe para que un <c>pageSize</c>
    /// absurdo no provoque un barrido de memoria, y el tamaño por defecto no puede quedar
    /// fuera de ese tope: el listado arrancaría ya inconsistente con su propio selector.
    /// </summary>
    private static IEnumerable<string> ValidatePaging(WebOptions options)
    {
        if (options.MaxPageSize is < MinPageSize or > MaxPageSize)
        {
            yield return
                $"'Web:MaxPageSize' must be between {MinPageSize} and {MaxPageSize} " +
                $"(current value: {options.MaxPageSize}).";
        }

        if (options.DefaultPageSize < MinPageSize || options.DefaultPageSize > options.MaxPageSize)
        {
            yield return
                $"'Web:DefaultPageSize' must be between {MinPageSize} and 'Web:MaxPageSize' " +
                $"({options.MaxPageSize}) (current value: {options.DefaultPageSize}).";
        }
    }
}

/// <summary>
/// Valida reglas que cruzan secciones: el puerto de la UI no puede chocar con un listener SMTP activo.
/// </summary>
public sealed class SmtpMockupOptionsValidator : IValidateOptions<SmtpMockupOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SmtpMockupOptions options)
    {
        var failures = new List<string>();

        if (!options.Web.Enabled || options.Web.Port == 0)
        {
            // Con Web:Port=0 el sistema asigna un puerto libre, así que no puede chocar con
            // ningún listener SMTP: compararlo sería comparar contra el puerto que nadie usa.
            return ValidateOptionsResult.Success;
        }

        // Un puerto 0 en cualquiera de los dos lados significa «uno libre», así que no puede
        // haber choque: sólo dos puertos concretos iguales pueden chocar de verdad.
        if (options.Smtp.Plain.Enabled && options.Smtp.Plain.Port != 0 && options.Web.Port == options.Smtp.Plain.Port)
        {
            failures.Add(
                $"'Web:Port' cannot be {options.Web.Port} because the plain SMTP listener already " +
                "uses it ('Smtp:Plain:Port').");
        }

        if (options.Smtp.StartTls.Enabled && options.Smtp.StartTls.Port != 0 && options.Web.Port == options.Smtp.StartTls.Port)
        {
            failures.Add(
                $"'Web:Port' cannot be {options.Web.Port} because the STARTTLS listener already " +
                "uses it ('Smtp:StartTls:Port').");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}