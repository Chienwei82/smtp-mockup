using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;
using SmtpMockup.Smtp;

namespace SmtpMockup.Host.Tests;

/// <summary>
/// Proveedor de log en memoria. Existe para poder afirmar sobre los <em>warnings</em> de arranque
/// (dirección fuera de loopback), que es la mitad del comportamiento de D-1 que no se puede ver
/// desde un puerto.
/// </summary>
public sealed class LogCollector : ILoggerProvider
{
    private readonly List<string> _lines = [];

    /// <summary>Copia de las líneas capturadas, segura para afirmar desde el hilo del test.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CollectingLogger(this);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private void Add(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
        }
    }

    private sealed class CollectingLogger(LogCollector owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner.Add($"[{logLevel}] {formatter(state, exception)}");
    }
}
/// <summary>
/// Arranca el <em>composition root real</em> (<c>Program</c>) con puertos efímeros, un directorio
/// de mensajes temporal y la UI en loopback.
/// </summary>
/// <remarks>
/// Hasta ahora no había ninguna prueba del Host: los puertos, el binding y el fail-fast se
/// ejercitaban en los proyectos de abajo (Core valida, Smtp levanta listeners, Web renderiza
/// componentes) pero nadie arrancaba el proceso de verdad. Eso dejaba sin cubrir exactamente los
/// errores que sólo existen en el ensamblado: una URL mal compuesta, un <c>UseUrls</c> que pisa
/// la configuración, o un warning de binding que no sale porque la rama no se ejecuta.
/// </remarks>
public sealed class HostFactory : WebApplicationFactory<Program>
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(), "smtp-mockup-host-tests", Guid.NewGuid().ToString("N"));

    private readonly string _certificateDirectory = Path.Combine(
        Path.GetTempPath(), "smtp-mockup-host-certs", Guid.NewGuid().ToString("N"));

    private readonly Dictionary<string, string?> _overrides;

    private readonly LogCollector _logCollector = new();

    /// <param name="overrides">
    /// Claves en notación de <c>appsettings.json</c> (<c>Smtp:Plain:BindAddress</c>). Se aplican
    /// sobre los valores del fixture, así que una prueba puede apartarse de la configuración
    /// normal sin ensuciar al resto.
    /// </param>
    public HostFactory(IDictionary<string, string?>? overrides = null)
    {
        _overrides = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                _overrides[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>Las opciones ya validadas por el Host, con los puertos que realmente se abrieron.</summary>
    public SmtpMockupOptions Options { get; private set; } = new();

    /// <summary>Las líneas de log del arranque, para afirmar sobre los avisos de binding.</summary>
    public IReadOnlyList<string> StartupLog => _logCollector.Lines;

    /// <summary>Puerto real del listener en claro.</summary>
    public int PlainPort => PlainListener.Port;

    /// <summary>Puerto real del listener STARTTLS.</summary>
    public int StartTlsPort => StartTlsListener.Port;

    private SmtpListenerService PlainListener
        => Services.GetRequiredKeyedService<SmtpListenerService>(SmtpEndpointKind.Plain);

    private SmtpListenerService StartTlsListener
        => Services.GetRequiredKeyedService<SmtpListenerService>(SmtpEndpointKind.StartTls);

    /// <summary>
    /// Fuerza el arranque y devuelve la instancia lista. <see cref="WebApplicationFactory{T}"/>
    /// arranca de forma diferida en el primer <c>CreateClient</c>, y hay pruebas que sólo leen
    /// opciones o escuchan un puerto: para ésas hay que arrancar antes.
    /// </summary>
    public HostFactory Start()
    {
        _ = Server;
        Options = Services.GetRequiredService<IOptions<SmtpMockupOptions>>().Value;
        return this;
    }

    // Nota: WebApplicationFactory usa TestServer, no Kestrel, así que aquí no hay socket HTTP real
    // ni IServerAddressesFeature. Por eso ResolvedWebUrl devuelve vacío aquí y el puerto web
    // efímero se cubre en el arranque real del binario, no en estos tests.

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // El Host fija su propio ContentRoot (AppContext.BaseDirectory) para funcionar también
        // como servicio de Windows, donde el SCM arranca el proceso con System32 como CWD.
        // WebApplicationFactory intentaría poner aquí el directorio del proyecto de pruebas, que
        // no tiene appsettings.json, así que se le pasa el del Host: la configuración que se
        // ejercita tiene que ser la de producción.
        builder.UseContentRoot(AppContext.BaseDirectory);
        builder.UseEnvironment("Development");

        // Puertos efímeros en los tres listeners: dos pruebas pueden correr en paralelo sin
        // pelearse por el 8025/8443/8888 del desarrollo.
        builder.UseSetting("Smtp:Plain:Enabled", Setting("Smtp:Plain:Enabled", "true"));
        builder.UseSetting("Smtp:Plain:Port", Setting("Smtp:Plain:Port", "0"));
        builder.UseSetting("Smtp:Plain:BindAddress", Setting("Smtp:Plain:BindAddress", "127.0.0.1"));

        builder.UseSetting("Smtp:StartTls:Enabled", Setting("Smtp:StartTls:Enabled", "true"));
        builder.UseSetting("Smtp:StartTls:Port", Setting("Smtp:StartTls:Port", "0"));
        builder.UseSetting("Smtp:StartTls:BindAddress", Setting("Smtp:StartTls:BindAddress", "127.0.0.1"));

        builder.UseSetting("Storage:Directory", _storageDirectory);
        builder.UseSetting("Storage:KeepRawMime", "true");

        // Sin esto, el certificado podría venir de 'dotnet dev-certs https' en una máquina que
        // lo tenga, y el arranque dependería del entorno en lugar de ser reproducible.
        builder.UseSetting("Certificate:Mode", "Auto");
        builder.UseSetting("Certificate:Path", Path.Combine(_certificateDirectory, "dev.pfx"));
        builder.UseSetting("Certificate:AutoGenerateSelfSigned", "true");
        builder.UseSetting("Certificate:UseDevelopmentCertificate", "false");

        builder.UseSetting("Web:Enabled", Setting("Web:Enabled", "true"));
        builder.UseSetting("Web:Port", Setting("Web:Port", "0"));
        builder.UseSetting("Web:BindAddress", Setting("Web:BindAddress", "127.0.0.1"));
        builder.UseSetting("Web:WatchDirectory", "false");

        builder.ConfigureLogging(logging => logging.AddProvider(_logCollector));
    }

    private string Setting(string key, string fallback) => _overrides.GetValueOrDefault(key) ?? fallback;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            DeleteDirectory(_storageDirectory);
            DeleteDirectory(_certificateDirectory);
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Un temporal que no se puede borrar no debe fallar el test.
        }
    }
}
