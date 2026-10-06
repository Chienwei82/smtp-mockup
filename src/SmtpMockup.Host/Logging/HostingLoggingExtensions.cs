using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmtpMockup.Core.Hosting;
using SmtpMockup.Core.Options;

namespace SmtpMockup.Host.Logging;

/// <summary>
/// Configuración del logging según el modo de ejecución (D-10).
/// </summary>
/// <remarks>
/// <para><b>Consola:</b> sólo <c>Console</c>, porque el usuario tiene la terminal delante.</para>
/// <para><b>Windows Service:</b> <c>EventLog</c> (lo que muestra el Visor de eventos y
/// <c>sc query</c>) más un archivo diario en <c>Hosting:LogDirectory</c>, que es el destino donde
/// se puede bajar el nivel a <c>Debug</c> sin llenar el Event Log.</para>
/// <para>
/// Los providers por defecto de <c>WebApplicationBuilder</c> se limpian siempre: en servicio dejar
/// <c>Debug</c> o <c>EventSource</c> activados sólo añade ruido y, en el caso del Event Log, una
/// segunda fuente con otro nombre.
/// </para>
/// </remarks>
public static class HostingLoggingExtensions
{
    /// <summary>
    /// Aplica los sinks de logging del modo indicado y devuelve el provider de archivo creado, si
    /// lo hubo, para poder ejecutarle la poda de retención al arrancar.
    /// </summary>
    /// <param name="builder">Builder de logging del host.</param>
    /// <param name="options">Opciones de <c>Hosting:*</c>.</param>
    /// <param name="mode">Modo efectivo ya resuelto.</param>
    /// <param name="configuration">Configuración, para leer <c>Logging:LogLevel:Default</c>.</param>
    /// <returns>El provider de archivo, o <see langword="null"/> en modo consola.</returns>
    public static FileLoggerProvider? ConfigureHostingLogging(
        this ILoggingBuilder builder,
        HostingOptions options,
        RunningMode mode,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.ClearProviders();

        if (!HostingModeResolver.UsesFileLog(mode) && !HostingModeResolver.UsesEventLog(mode))
        {
            builder.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss ";
                console.UseUtcTimestamp = true;
            });

            return null;
        }

        // El archivo recibe todo lo que llegue al nivel por defecto: es el destino barato para
        // bajar a Debug. El Event Log se filtra aparte porque tiene cuota y no la tiene el archivo.
        var defaultLevel = ReadDefaultLevel(configuration);
        var fileProvider = new FileLoggerProvider(options, defaultLevel);
        builder.AddProvider(fileProvider);

        if (HostingModeResolver.UsesEventLog(mode))
        {
            // Guarda explícita, y no sólo el análisis: el provider de Event Log lanza
            // PlatformNotSupportedException fuera de Windows. El modo servicio nunca debería
            // ocurrir ahí (HostingModeResolver degrada a consola), pero si alguien fuerza
            // 'Hosting:Mode=WindowsService' en Linux el proceso debe arrancar y loguear a
            // archivo, no morir con una excepción de plataforma.
            if (OperatingSystem.IsWindows())
            {
                AddWindowsEventLog(builder, options);
            }
        }

        return fileProvider;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddWindowsEventLog(ILoggingBuilder builder, HostingOptions options)
    {
        var eventLogLevel = ParseLevel(options.EventLogLevel, LogLevel.Information);

        builder.AddEventLog(settings =>
        {
            settings.SourceName = options.EventLogSource;
            settings.LogName = "Application";
            settings.Filter = (_, level) => level >= eventLogLevel;
        });
    }

    /// <summary>Lee <c>Logging:LogLevel:Default</c>, con <c>Information</c> como fallback.</summary>
    private static LogLevel ReadDefaultLevel(IConfiguration configuration)
        => ParseLevel(configuration["Logging:LogLevel:Default"], LogLevel.Information);

    private static LogLevel ParseLevel(string? value, LogLevel fallback)
        => Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) && Enum.IsDefined(level)
            ? level
            : fallback;
}