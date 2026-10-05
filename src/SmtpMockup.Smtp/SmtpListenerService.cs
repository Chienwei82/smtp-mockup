using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;
using SmtpServer;
using SmtpServer.ComponentModel;
using SmtpServer.Protocol;

// El IMessageStore del dominio y el de la librería coinciden en nombre; se aliasan
// para que las firmas del listener no sean ambiguas.
using MockupStore = SmtpMockup.Core.Storage.IMessageStore;
using LibraryMessageStore = SmtpServer.Storage.IMessageStore;
using LibraryMailboxFilter = SmtpServer.Storage.IMailboxFilter;

namespace SmtpMockup.Smtp;

/// <summary>
/// Hosted service de un listener SMTP. Persiste cada mensaje recibido vía
/// <see cref="IMessageStore"/> y responde el código SMTP que corresponda (SPEC §10.1).
/// </summary>
/// <remarks>
/// El puerto no se escucha hasta que <see cref="StartAsync"/> corre, de modo que un test
/// puede arrancar el servicio, conectarse y detenerlo sin dejar sockets sueltos.
/// </remarks>
public sealed class SmtpListenerService(
    SmtpEndpointKind kind,
    IOptions<SmtpMockupOptions> options,
    IServiceProvider serviceProvider,
    ICertificateProvider certificateProvider,
    ILogger<SmtpListenerService> logger) : IHostedService, IAsyncDisposable
{
    private readonly SmtpMockupOptions _options = options.Value;
    private readonly int _configuredPort =
        kind == SmtpEndpointKind.StartTls ? options.Value.Smtp.StartTls.Port : options.Value.Smtp.Plain.Port;
    private readonly SmtpSecurityMode _security =
        kind == SmtpEndpointKind.StartTls ? options.Value.Smtp.StartTls.ParsedSecurity : options.Value.Smtp.Plain.ParsedSecurity;

    // La dirección de binding se resuelve una vez, aquí, y no dentro de StartAsync: es
    // configuración, y como tal la valida el validador de opciones antes de que se abra nada.
    // Con Port(port) a secas, SmtpServer resuelve a IPAddress.Any y el listener aceptaba
    // correo de toda la red sin avisar; con Endpoint(IPEndPoint) el bind es explícito.
    private readonly IPAddress _bindAddress =
        kind == SmtpEndpointKind.StartTls
            ? options.Value.Smtp.StartTls.ParsedBindAddress
            : options.Value.Smtp.Plain.ParsedBindAddress;

    private CancellationTokenSource? _shutdown;
    private Task? _runTask;
    private int _portInUse;
    private X509Certificate2? _certificate;
    private Microsoft.Extensions.DependencyInjection.ServiceProvider? _provider;

    /// <summary>
    /// El puerto realmente abierto. Los tests piden el puerto <c>0</c> para obtener uno
    /// efímero y leen esta propiedad una vez arrancado el listener.
    /// </summary>
    public int Port => _portInUse;

    /// <summary>
    /// La dirección realmente abierta. Expuesta para que los tests affirmen que el listener
    /// <em>no</em> acepta fuera del loopback por defecto, que es el comportamiento que se
    /// quiere fijar (D-1).
    /// </summary>
    public IPAddress BindAddress => _bindAddress;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // El certificado se resuelve antes de abrir el socket: si falta, el host muere con un
        // mensaje que explica cómo configurarlo, en vez de levantar 8443 y cortar la conexión
        // en cada sesión (fail-fast, SPEC §6.2).
        if (_security != SmtpSecurityMode.None)
        {
            _certificate = certificateProvider.Resolve();
        }

        var limits = new StorageLimits(
            MaxInlineAttachmentBytes: _options.Storage.MaxInlineAttachmentBytes,
            KeepRawMime: _options.Storage.KeepRawMime,
            MaxRawMimeBytes: _options.Storage.MaxRawMimeBytes);

        // Un IServiceProvider propio por listener: la librería resuelve el store y el
        // filtro por sesión desde aquí, así cada listener queda aislado del otro.
        // El store no tiene estado entre mensajes (el sobre y el contenido llegan como
        // argumentos de cada llamada), así que una única instancia es segura.
        var services = new ServiceCollection();
        services.AddSingleton<LibraryMessageStore>(
            new MockupMessageStore(
                serviceProvider.GetRequiredService<MockupStore>(), limits, _security, logger));
        services.AddSingleton<LibraryMailboxFilter>(new AcceptAllMailboxFilter());

        // El provider debe vivir tanto como el listener: la librería lo consulta en
        // cada sesión para resolver el store y el filtro. Un 'await using' aquí lo
        // dispondría al salir de StartAsync y el listener dejaría de funcionar.
        var provider = services.BuildServiceProvider();
        Interlocked.Exchange(ref _provider, provider)?.Dispose();

        var builder = new SmtpServerOptionsBuilder()
            .ServerName("smtp-mockup")
            .MaxMessageSize(_options.Smtp.MaxMessageSizeBytes, MaxMessageSizeHandling.Strict)
            .MaxAuthenticationAttempts(0);

        var connectionLimit = ApplyConnectionLimit(builder);

        builder.Endpoint(endpoint =>
        {
            // Endpoint(IPEndPoint) en vez de Port(port, isSecure): sólo el primero fija la
            // dirección de binding. Con el segundo, SmtpServer abre en IPAddress.Any.
            endpoint.Endpoint(new IPEndPoint(_bindAddress, _configuredPort));
            endpoint.IsSecure(_security == SmtpSecurityMode.Implicit);

            if (_certificate is not null)
            {
                // El mismo certificado sirve para TLS implícito y para STARTTLS: la biblioteca
                // lo consulta al negociar. Se resolvió una vez y se comparte entre sesiones,
                // así que todas presentan el mismo thumbprint.
                endpoint.Certificate(_certificate);
            }
        });

        var serverOptions = builder.Build();

        var server = new global::SmtpServer.SmtpServer(serverOptions, provider);

        if (connectionLimit is { } limit)
        {
            connectionLimit.Attach(server);
        }

        // La foto de los puertos en escucha se toma ANTES de arrancar: el puerto efímero es
        // «el nuevo que aparezca», y sólo se distingue del resto por no estar en esta foto.
        var listeningBefore = _configuredPort == 0 ? ListeningPorts(_bindAddress) : null;

        try
        {
            // StartAsync no completa mientras el listener atiende; no hay que esperarlo.
            _runTask = server.StartAsync(_shutdown.Token);
        }
        catch (Exception exception)
        {
            // Puerto ocupado: fail-fast indicando el puerto (SPEC §10.4). El mensaje nombra la
            // dirección también, porque 'puerto ocupado' y 'dirección no disponible' se
            // diagnostican distinto y con este texto no se puede saber cuál de las dos fue.
            throw new InvalidOperationException(
                $"Could not start the SMTP listener on {EndpointAddress.FormatHostPort(_bindAddress.ToString(), _configuredPort)}. "
                + "Is the port already in use, or is the bind address not available on this machine?",
                exception);
        }

        await WaitUntilAcceptingAsync(listeningBefore, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "SMTP listener started kind={Kind} endpoint={Endpoint} security={Security} " +
            "maxMessageSizeBytes={MaxMessageSizeBytes} certificate={Thumbprint}",
            kind,
            EndpointAddress.FormatHostPort(_bindAddress.ToString(), _portInUse),
            _security,
            _options.Smtp.MaxMessageSizeBytes,
            _certificate?.Thumbprint ?? "(none)");
    }

    /// <summary>
    /// Prepara el límite de conexiones simultáneas y devuelve el vigilante, o <see langword="null"/>
    /// si la opción está en «sin límite».
    /// </summary>
    /// <remarks>
    /// <para>
    /// SmtpServer 11.1.0 no expone ningún límite de concurrencia: <c>SmtpServerOptionsBuilder</c>
    /// sólo ofrece <c>MaxAuthenticationAttempts</c>, <c>MaxMessageSize</c> y <c>MaxRetryCount</c>.
    /// Así que el límite se construye con las dos piezas públicas que sí sirven:
    /// <c>SessionCreated</c>/<c>SessionCompleted</c> para contar, y <c>CustomSmtpGreeting</c> para
    /// responder.
    /// </para>
    /// <para>
    /// La respuesta va por el saludo y no escribiendo en <c>Context.Pipe.Output</c> porque la
    /// librería escribe su <c>220</c> <em>antes</em> de lanzar <c>SessionCreated</c>: cualquier
    /// byte escrito después llega cuando el cliente ya se ha Connected, y se quedaría colgando
    /// esperando un <c>EHLO</c> que nadie responde. Devolver <c>421</c> como saludo sí lo corta,
    /// porque el cliente lo lee como respuesta de bienvenida.
    /// </para>
    /// <para>
    /// El recuento es un conjunto de <c>SessionId</c>, no un <c>int</c>: con un contador, una
    /// sesión que disparase dos eventos de fin se contaría dos veces y el topping se degradaría
    /// hasta dejar de rechazar. <c>Remove</c> sobre un id ausente no hace nada, así que los tres
    /// eventos de fin pueden Attacharse sin miedo a double-decrementos.
    /// </para>
    /// </remarks>
    private ConnectionLimit? ApplyConnectionLimit(SmtpServerOptionsBuilder builder)
    {
        var configured = _options.Smtp.MaxConcurrentConnections;

        if (configured <= 0)
        {
            // 0 = sin límite, que es el default: el mockup no es un servidor de correo y un tope
            // bajo estorbaría a quien prueba una ráfaga de envíos.
            return null;
        }

        var limit = new ConnectionLimit(configured, logger);
        limit.Configure(builder);
        return limit;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Interlocked.Exchange y no una comprobación con null: StopAsync puede solaparse con
        // DisposeAsync (el host detiene los hosted services en paralelo y a continuación los
        // dispone), y sin esto los dos caminos podrían tocar el mismo CancellationTokenSource.
        var shutdown = Interlocked.Exchange(ref _shutdown, null);

        if (shutdown is not null)
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException)
            {
                // El cierre cancela el listener: no es un fallo que haya que reportar.
            }
        }

        // Se suelta aquí y no en DisposeAsync: StopAsync es quien lo usa, y DisposeAsync puede
        // ejecutarse antes de que este método termine. Liberarlo aquí garantiza que nadie lo use
        // después de estar liberado, sea cual sea el orden en que el host llame a los dos.
        shutdown?.Dispose();

        logger.LogInformation(
            "SMTP listener stopped kind={Kind} endpoint={Endpoint}",
            kind,
            EndpointAddress.FormatHostPort(_bindAddress.ToString(), _portInUse));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Por simetría con StopAsync, se recoge de forma atómica. Si StopAsync ya lo liberó,
        // aquí no queda nada que liberar; si el host sólo dispone sin detener, se cancela aquí.
        var shutdown = Interlocked.Exchange(ref _shutdown, null);

        if (shutdown is not null)
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
            shutdown.Dispose();
        }

        // El provider sólo se libera cuando el listener ya terminó de atender.
        Interlocked.Exchange(ref _provider, null)?.Dispose();

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Espera a que el socket acepte conexiones, sondeando con un timeout acotado.
    /// Evita esperas fijas, que son la causa habitual de tests intermitentes.
    /// </summary>
    private async Task WaitUntilAcceptingAsync(HashSet<int>? listeningBefore, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        if (listeningBefore is not null)
        {
            // Con puerto efímero el 0 es una petición, no un puerto: SmtpServer abre el socket y
            // elige uno, pero no lo publica en los options (se queda en 0), así que no hay API
            // para consultarlo. La única fuente fiable es el propio sistema, y ese valor es
            // además el que necesitan los tests para conectarse.
            var resolved = await ResolveEphemeralPortAsync(listeningBefore, cancellationToken).ConfigureAwait(false);
            _portInUse = resolved;

            logger.LogInformation(
                "Resolved ephemeral SMTP port {Port} for kind={Kind} on {BindAddress}",
                resolved,
                kind,
                _bindAddress);
        }
        else
        {
            _portInUse = _configuredPort;
        }

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Se sondea la dirección real, no IPAddress.Loopback fijo: con un bind fuera de
                // loopback, un probe a 127.0.0.1 nunca conectaría y el arranque fallaría con un
                // timeout que no explicaría la causa.
                using var probe = new TcpClient();
                await probe.ConnectAsync(_bindAddress, _portInUse, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            $"The SMTP listener on {EndpointAddress.FormatHostPort(_bindAddress.ToString(), _portInUse)} "
            + "did not start accepting connections.");
    }

    /// <summary>
    /// Descubre el puerto que el sistema asignó a este listener cuando se pidió el <c>0</c>.
    /// </summary>
    /// <remarks>
    /// Se comparan los sockets en escucha antes y después de arrancar el servidor y se toma el
    /// que sea nuevo. La alternativa —«busca cualquier puerto abierto en esta dirección»— está
    /// mal con dos listeners: ambos comparten dirección y el primero en aparecer se llevaría el
    /// puerto del segundo, y ambos acabarían reportando el mismo número. Un diff no tiene esa
    /// ambigüedad: cada listener se queda con el suyo.
    /// </remarks>
    private async Task<int> ResolveEphemeralPortAsync(HashSet<int> listeningBefore, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var added = ListeningPorts(_bindAddress).Except(listeningBefore).ToList();
            if (added.Count > 0)
            {
                return added.Min();
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Could not determine which ephemeral port the SMTP listener bound to on {_bindAddress}.");
    }

    /// <summary>Puertos TCP en escucha del sistema en la dirección dada.</summary>
    private static HashSet<int> ListeningPorts(IPAddress address)
        => [.. System.Net.NetworkInformation.IPGlobalProperties
            .GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Where(endpoint => endpoint.Address.Equals(address))
            .Select(endpoint => endpoint.Port)];

    /// <summary>
    /// Con puerto <c>0</c> el sistema asigna uno libre; lo recuperamos de los options
    /// ya resueltos para que el test y el log coincidan.
    /// </summary>
    private static int ResolveEphemeralPort(ISmtpServerOptions serverOptions)
        => serverOptions.Endpoints.Count > 0 ? serverOptions.Endpoints[0].Endpoint.Port : 0;
}
