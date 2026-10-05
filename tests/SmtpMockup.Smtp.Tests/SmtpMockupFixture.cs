using System.Net;
using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;
using SmtpMockup.Smtp;
using SmtpMockup.Storage;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// Levanta un listener SMTP real con el <c>FileSystemMessageStore</c> sobre un
/// directorio temporal, y expone un cliente MailKit para enviar mensajes de verdad.
/// </summary>
/// <remarks>
/// Los tests usan puertos efímeros y un <c>TempDirectory</c> propio: nada se escribe
/// fuera del temporal y no hay <c>Thread.Sleep</c>, sólo esperas con timeout.
/// </remarks>
public sealed class SmtpMockupFixture : IAsyncDisposable
{
    private readonly SmtpListenerService _listener;
    private readonly string _storageDirectory;
    private string? _certificateDirectory;
    private readonly ServiceProvider _provider;

    private SmtpMockupFixture(
        SmtpListenerService listener,
        FileSystemMessageStore store,
        string storageDirectory,
        ServiceProvider provider)
    {
        _listener = listener;
        _storageDirectory = storageDirectory;
        _provider = provider;
        Store = store;
    }

    /// <summary>El store real, para verificar lo que se persistió.</summary>
    public FileSystemMessageStore Store { get; }

    /// <summary>El puerto en el que escucha el mockup.</summary>
    public int Port => _listener.Port;

    /// <summary>
    /// La dirección a la que está atado el listener. Expuesta para que los tests de binding
    /// puedan compararla con la que se configuró (D-1).
    /// </summary>
    public IPAddress BindAddress => _listener.BindAddress;

    /// <summary>El directorio donde se escriben los JSON.</summary>
    public string StorageDirectory => _storageDirectory;

    /// <summary>
    /// El directorio donde se guardó el PFX generado, si esta instancia tuvo que generarlo.
    /// Es un temporal del test, así que se borra al descartarlo.
    /// </summary>
    public string? CertificateDirectory => _certificateDirectory;

    /// <summary>
    /// El certificado que el listener presenta en 8443. Es <see langword="null"/> cuando el
    /// listener arrancado es el plano y no usa ninguno.
    /// </summary>
    public X509Certificate2? Certificate { get; private set; }

    /// <summary>
    /// El certificado del listener cifrado, fallando con un mensaje claro si no lo hay. Los
    /// tests de 8443 siempre esperan uno; el listener plano no, y por eso la propiedad es
    /// nullable y ésta es la vía para los tests que sí lo necesitan.
    /// </summary>
    /// <exception cref="InvalidOperationException">El listener no usa certificado.</exception>
    public X509Certificate2 RequireCertificate()
        => Certificate
            ?? throw new InvalidOperationException(
                "This fixture has no certificate: it was started with a listener that does not use TLS.");

    /// <summary>El directorio del PFX, fallando si este fixture no generó ninguno.</summary>
    /// <exception cref="InvalidOperationException">No se generó certificado en este arranque.</exception>
    public string RequireCertificateDirectory()
        => _certificateDirectory
            ?? throw new InvalidOperationException("This fixture did not generate a certificate.");

    /// <summary>
    /// Registra el directorio del PFX para borrarlo al liberar el fixture. Se hace después de
    /// resolver, porque resolver es justo lo que crea el directorio.
    /// </summary>
    /// <param name="directory">Directorio con el certificado persistido.</param>
    public void MarkCertificateDirectory(string directory) => _certificateDirectory = directory;

    /// <summary>
    /// Arranca un listener con la configuración indicada y espera a que acepte
    /// conexiones antes de devolver el fixture.
    /// </summary>
    /// <param name="configure">Ajustes sobre <c>SmtpMockupOptions</c>, si hace falta.</param>
    /// <param name="kind">
    /// Listener a arrancar. El cifrado sale de <c>Smtp:StartTls:Security</c>, así que el
    /// fixture no necesita un parámetro aparte para eso.
    /// </param>
    public static async Task<SmtpMockupFixture> StartAsync(
        Action<SmtpMockupOptions>? configure = null,
        SmtpEndpointKind kind = SmtpEndpointKind.Plain)
    {
        var storageDirectory = Path.Combine(
            Path.GetTempPath(), "smtp-mockup-tests", Guid.NewGuid().ToString("N"));

        // El certificado autofirmado se escribe en un temporal y no en el directorio del
        // ejecutable: los tests no deben dejar certs junto al binario de test.
        var certificateDirectory = Path.Combine(
            Path.GetTempPath(), "smtp-mockup-certs", Guid.NewGuid().ToString("N"));

        // La librería no devuelve el puerto asignado cuando se pide el 0, así que el
        // fixture reserva uno libre por su cuenta y lo pasa explícito. Menos elegante
        // que el puerto 0, pero determinista y sin colisiones entre tests.
        var port = ReserveFreePort();

        var options = new SmtpMockupOptions
        {
            Smtp = new SmtpOptions
            {
                Plain = new SmtpEndpointOptions
                {
                    Enabled = kind == SmtpEndpointKind.Plain,
                    Port = port,
                    Security = nameof(SmtpSecurityMode.None),
                },
                StartTls = new SmtpEndpointOptions
                {
                    Enabled = kind == SmtpEndpointKind.StartTls,
                    Port = ReserveFreePort(),

                    // El default de producción: 8443 anuncia STARTTLS. Los tests que
                    // ejercitan el modo implícito lo piden explícitamente.
                    Security = nameof(SmtpSecurityMode.StartTls),
                },
                MaxMessageSizeMb = 1,
            },
            Certificate = new CertificateOptions
            {
                Mode = nameof(CertificateMode.Auto),
                Path = Path.Combine(certificateDirectory, "dev.pfx"),
                Password = string.Empty,

                // Los tests no dependen de si la máquina tiene `dotnet dev-certs https`:
                // sin esta bandera, el certificado podría venir del dev-certs en CI y el
                // test de persistencia no significaría nada.
                UseDevelopmentCertificate = false,
            },
            Storage = new StorageOptions { Directory = storageDirectory, KeepRawMime = true },
            Web = new WebOptions { Enabled = false },
        };

        configure?.Invoke(options);

        var loggerFactory = LoggerFactory
            .Create(builder => builder.SetMinimumLevel(LogLevel.Warning));

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<SmtpMockupOptions>>(Options.Create(options));

        var store = new FileSystemMessageStore(
            Options.Create(options.Storage),
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            },
            loggerFactory.CreateLogger<FileSystemMessageStore>());

        // El listener resuelve IMessageStore de aquí, así el test verifica el store real.
        services.AddSingleton<IMessageStore>(store);

        await using var provider = services.BuildServiceProvider();

        var certificateProvider = new CertificateProvider(
            Options.Create(options),
            loggerFactory.CreateLogger<CertificateProvider>());

        var listener = new SmtpListenerService(
            kind,
            Options.Create(options),
            provider,
            certificateProvider,
            loggerFactory.CreateLogger<SmtpListenerService>());

        // Si el arranque falla (puerto ocupado, certificado que no se puede resolver), no
        // existe fixture al que disponer: sin este try/catch los temporales de storage y de
        // certificados se acumulan en /tmp cada vez que un test falla.
        try
        {
            await listener.StartAsync(CancellationToken.None);

            // Sólo se resuelve el certificado si el listener lo pide: con el listener plano
            // (Security=None) resolverlo generaría un PFX que nadie usa y dejaría directorios
            // temporales huérfanos por cada test de 8025.
            var security = kind == SmtpEndpointKind.StartTls
                ? options.StartTls.ParsedSecurity
                : options.Plain.ParsedSecurity;

            var fixture = new SmtpMockupFixture(listener, store, storageDirectory, provider);

            if (security != SmtpSecurityMode.None)
            {
                fixture.Certificate = certificateProvider.Resolve();
                fixture.MarkCertificateDirectory(certificateDirectory);
            }

            await store.RebuildIndexAsync();
            return fixture;
        }
        catch
        {
            await listener.StopAsync(CancellationToken.None);
            await listener.DisposeAsync();
            DeleteDirectory(storageDirectory);
            DeleteDirectory(certificateDirectory);
            throw;
        }
    }

    /// <summary>Borra un directorio temporal, ignorando que ya no exista.</summary>
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

    /// <summary>Reserva un puerto TCP libre para el listener.</summary>
    private static int ReserveFreePort()
    {
        // Se abre un socket efímero, se lee el puerto que el sistema asigna y se
        // libera enseguida. Hay una ventana mínima entre el cierre y el arranque del
        // listener, pero evita por completo el 8025 de desarrollo.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();

        try
        {
            return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>
    /// Abre un cliente MailKit conectado al mockup, <em>sin autenticación</em>: el
    /// mockup no pide credenciales (RF-01).
    /// </summary>
    public Task<SmtpClient> ConnectAsync()
        => ConnectAsync(SecureSocketOptions.None);

    /// <summary>
    /// Abre un cliente MailKit con el cifrado indicado, aceptando el certificado
    /// autofirmado del mockup. Aceptarlo es justo lo que un usuario hace confiando el
    /// certificado en su almacén (ver <c>docs/certificate-trust.md</c>), así que el test
    /// ejercita el camino que un cliente real recorrería con el certificado ya confiable.
    /// </summary>
    /// <param name="secureSocketOptions">
    /// <see cref="SecureSocketOptions.SslOnConnect"/> para TLS implícito,
    /// <see cref="SecureSocketOptions.StartTls"/> para el comando STARTTLS.
    /// </param>
    public async Task<SmtpClient> ConnectAsync(SecureSocketOptions secureSocketOptions)
    {
        var client = new SmtpClient
        {
            Timeout = 30_000,
            ServerCertificateValidationCallback = (_, _, _, _) => true,
        };

        await client.ConnectAsync("127.0.0.1", Port, secureSocketOptions);
        return client;
    }

    /// <summary>Archivos <c>.json</c> realmente escritos en disco.</summary>
    public IReadOnlyList<string> StoredFiles()
        => Directory.Exists(_storageDirectory)
            ? Directory.EnumerateFiles(_storageDirectory, "*.json", SearchOption.AllDirectories).ToList()
            : [];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _listener.StopAsync(CancellationToken.None);
        await _listener.DisposeAsync();
        await _provider.DisposeAsync();

        DeleteDirectory(_storageDirectory);

        if (_certificateDirectory is not null)
        {
            DeleteDirectory(_certificateDirectory);
        }
    }
}
