using Microsoft.Extensions.Options;
using SmtpMockup.Core.Hosting;
using SmtpMockup.Core.Options;
using SmtpMockup.Host.Web;
using SmtpMockup.Host.Hosting;
using SmtpMockup.Host.Logging;
using SmtpMockup.Smtp;
using SmtpMockup.Storage;
using SmtpMockup.Web.Components;
using SmtpMockup.Web.Services;

// El content root es el directorio del ejecutable, NO el directorio de trabajo. Es lo que permite
// que el mismo binario funcione haciendo doble clic y como Windows Service, donde el SCM arranca
// el proceso con C:\Windows\System32 como CWD: sin esto, appsettings.json no se encontraría y
// data/ y certs/ caerían en System32.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = HostPath.BaseDirectory,
});

// Modo de ejecución y logging (D-10). Se resuelve ANTES de Build() porque AddEventLog y
// UseWindowsService se aplican al builder, y porque un fallo de configuración tiene que poder
// registrarse en el Event Log: si se validara después de construir el host, el servicio moriría
// sin dejar rastro (que es justo lo que se pierde el día que algo anda mal en producción).
var isWindows = ServiceDetection.IsWindows;
var startedByServiceManager = ServiceDetection.IsWindowsService();

// Lectura tolerante a fallos: si 'Hosting:Mode' no existe o no es válido se usa Auto y el mensaje
// claro lo produce BindAndValidate más abajo, que es donde muere el proceso con código 1.
var hostingSettings = builder.Configuration.GetSection("Hosting").Get<HostingOptions>() ?? new HostingOptions();
var requestedMode = ParseModeOrDefault(hostingSettings.Mode);
var runningMode = HostingModeResolver.Resolve(requestedMode, isWindows, startedByServiceManager);

// En modo servicio el log en archivo no es opcional: el Event Log tiene cuota y se poda solo, así
// que sin archivo las trazas Debug se pierden y no queda histórico cuando el servicio ya no está.
// El validador de opciones sólo puede rechazar el caso explícito (Mode=WindowsService); aquí, con el
// modo ya resuelto, también se cubre Mode=Auto corriendo como servicio, que es el default.
if (HostingModeResolver.UsesFileLog(runningMode) && !hostingSettings.HasFileLog)
{
    await Console.Error.WriteLineAsync(
        $"smtp-mockup: running as a Windows Service but 'Hosting:LogDirectory' and/or " +
        $"'Hosting:LogFileName' are empty, so there would be no log file. Set 'Hosting:LogDirectory' " +
        $"to a relative path such as 'logs', or set 'Hosting:Mode' to 'Console' to run as a console " +
        $"application.");

    return 1;
}

var fileLogProvider = builder.Logging.ConfigureHostingLogging(hostingSettings, runningMode, builder.Configuration);

if (HostingModeResolver.IsContradictory(requestedMode, startedByServiceManager))
{
    Console.Error.WriteLine(
        "smtp-mockup: 'Hosting:Mode' is 'Console' but the process was started by the Windows Service " +
        "Control Manager, so the console log goes nowhere. Set 'Hosting:Mode' to 'Auto' or " +
        "'WindowsService'.");
}

// Configuración tipada + validación fail-fast: si algo está mal, el host muere al arrancar
// con un mensaje que nombra la clave offender (SPEC §6).
builder.Services.AddSmtpMockupOptions(builder.Configuration);

SmtpMockupOptions options;
try
{
    // Se valida antes de Build() para aplicar UseUrls() sin abrir ningún socket con una
    // configuración inválida; el mensaje va a stderr y el proceso sale con código 1.
    options = SmtpMockupConfiguration.BindAndValidate(builder.Configuration);
}
catch (OptionsValidationException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return 1;
}

// Persistencia (un JSON por correo) y listeners SMTP. El listener se registra por
// puerto habilitado, así 8025 y 8443 se activan de forma independiente (RF-05).
builder.Services.AddSmtpMockupStorage(builder.Configuration);

// Los listeners se registran desde una única regla testeable en vez de con un 'if' por puerto:
// escrita en línea aquí, 'Smtp:Plain:Enabled=false' se anunciaba apagado en el log y abría 8025
// igual. La decisión es ahora SmtpListenerSelection.GetEnabled(options), cubierta por tests.
foreach (var kind in SmtpListenerSelection.GetEnabled(options))
{
    builder.Services.AddSmtpMockupListener(kind);
}

if (options.Web.Enabled)
{
    // EndpointAddress compone la URL y no una interpolación: con 'Web:BindAddress=::1' la
    // forma ingenua produce 'http://::1:8888', que es una autoridad ambigua y hace que
    // Kestrel falle al arrancar. El validador acepta IPv6, así que aquí tiene que salir bien.
    builder.WebHost.UseUrls(EndpointAddress.FormatHttpUrl(options.Web.BindAddress, options.Web.Port));

    // UI Blazor (Interactive Server) y sus servicios. Los componentes reciben IMessageStore
    // directamente: no hay cliente HTTP ni capa de mapeo, y el circuito de SignalR ya es el
    // canal para las actualizaciones en vivo (D-06 y D-11).
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddSmtpMockupWeb(options.Web.WatchDirectory);
}

// Apagado ordenado: 5 s de drain para que los circuitos de Blazor terminen y los listeners SMTP
// cierren las conexiones abiertas (DESIGN §4.5). Sin esto, un 'Stop-Service' corta en seco.
builder.Services.Configure<HostOptions>(hostOptions => hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(5));

// UseWindowsService engancha el ciclo de vida al SCM: sin esta llamada, 'Stop-Service' no
// mata el proceso y hay que hacerlo a la fuerza, con el riesgo de perder un mensaje a medio
// escribir. Sólo tiene efecto en Windows, y sólo tiene sentido en modo servicio.
if (runningMode == RunningMode.WindowsService)
{
    builder.Host.UseWindowsService();
}

var app = builder.Build();

// El certificado se resuelve antes de arrancar: si falta y no se puede generar, el proceso
// muere con el mensaje del resolver (que nombra la clave y la ruta) en vez de un stack trace
// de hosted service, que no le dice nada a quien está configurando (SPEC §6.2).
try
{
    if (options.Smtp.StartTls.Enabled
        && options.Smtp.StartTls.ParsedSecurity != SmtpSecurityMode.None)
    {
        app.Services.GetRequiredService<ICertificateProvider>().Resolve();
    }
}
catch (InvalidOperationException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return 1;
}

// El índice en memoria se construye al arrancar y los .json.tmp huérfanos se purgan:
// un temporal significa un mensaje que nunca llegó a renombrarse (SPEC §8).
var store = app.Services.GetRequiredService<FileSystemMessageStore>();
store.PurgeTempFiles();
await store.RebuildIndexAsync();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SmtpMockup.Startup");

// La primera línea del arranque dice en qué modo corre el proceso y dónde se está escribiendo el
// log. Sin eso, un servicio que "no dice nada" es imposible de diagnosticar: no hay consola donde
// mirar, y el Event Log sólo se abre si se sabe el nombre del origen.
logger.LogInformation(
    "Running as {Mode} (configured Hosting:Mode={ConfiguredMode}, started by SCM={StartedByServiceManager}); " +
    "executable directory={BaseDirectory}; content root={ContentRoot}",
    runningMode,
    requestedMode,
    startedByServiceManager,
    HostPath.BaseDirectory,
    app.Environment.ContentRootPath);

// Poda de logs antiguos: al arrancar es el momento barato de hacerlo, con el archivo del día ya
// escrito y sin write-ahead que se pierda.
if (fileLogProvider is not null)
{
    var pruned = fileLogProvider.PruneExpired(DateOnly.FromDateTime(DateTime.UtcNow));

    if (pruned.Count > 0)
    {
        logger.LogInformation(
            "Pruned {Count} expired log file(s) older than {RetentionDays} day(s)", pruned.Count, options.Hosting.LogRetentionDays);
    }
}

// Los avisos de binding van antes del resumen: un listener SMTP fuera de loopback es una
// decisión deliberada (CI, contenedores, pruebas desde otra máquina) pero no un default, así que
// se dice una vez y con nombre de clave. El default es 127.0.0.1 porque el mockup acepta
// cualquier correo sin autenticación: exponerlo a la red local no debe ser un silencio.
WarnIfNotLoopback(logger, options.Smtp.Plain, "Smtp:Plain");
WarnIfNotLoopback(logger, options.Smtp.StartTls, "Smtp:StartTls");

logger.LogInformation(
    "Logging to {Sinks}",
    runningMode == RunningMode.WindowsService
        ? $"file '{FileLogLayout.ResolveFile(options.Hosting, DateOnly.FromDateTime(DateTime.UtcNow))}' " +
          $"and Event Log source '{options.Hosting.EventLogSource}' (min level {options.Hosting.EventLogLevel})"
        : "the console");

logger.LogInformation(
    "SMTP listeners configured: plain={PlainEnabled} on {PlainEndpoint}, secure={SecureEnabled} on " +
    "{SecureEndpoint} (security={Security}, max {MaxMessageSizeMb} MB)",
    options.Smtp.Plain.Enabled,
    EndpointAddress.FormatHostPort(options.Smtp.Plain.BindAddress, options.Smtp.Plain.Port),
    options.Smtp.StartTls.Enabled,
    EndpointAddress.FormatHostPort(options.Smtp.StartTls.BindAddress, options.Smtp.StartTls.Port),
    options.Smtp.StartTls.Security,
    options.Smtp.MaxMessageSizeMb);
logger.LogInformation(
    "Certificate mode: {Mode} ({Path}), autoGenerateSelfSigned={AutoGenerateSelfSigned}",
    options.Certificate.Mode,
    CertificateProvider.ResolvePfxPath(options.Certificate.Path),
    options.Certificate.AutoGenerateSelfSigned);
logger.LogInformation("Storage directory: {Directory}", HostPath.Resolve(options.Storage.Directory));

if (options.Web.Enabled)
{
    // El log de arriba sale de la configuración; con un puerto efímero, el puerto real sólo se
    // conoce cuando Kestrel abre el socket, y lo dice IServerAddressesFeature. Con un puerto
    // fijo, las dos líneas coinciden.
    logger.LogInformation("Web UI configured on {Url}", EndpointAddress.FormatHttpUrl(options.Web.BindAddress, options.Web.Port));

    if (!EndpointAddress.IsLoopback(options.Web.BindAddress))
    {
        logger.LogWarning(
            "Web:BindAddress {BindAddress} is not a loopback address; the UI is reachable from " +
            "the network and has no authentication.",
            options.Web.BindAddress);
    }
}
else
{
    logger.LogInformation("Web UI disabled (Web:Enabled=false); only the SMTP listeners will run.");
}

if (options.Web.Enabled)
{
    // Los assets de MudBlazor (CSS y JS) llegan como static web assets del paquete y el CSS
    // propio de la RCL como _content/SmtpMockup.Web. Sin UseStaticFiles no se sirve ninguno y
    // la UI aparece sin estilos (SPEC §9.1).
    app.UseStaticFiles();

    // Los componentes de Blazor se renderizan en el servidor, así que el antifalsificación
    // se comprueba en cada_endpoint: sin este middleware, MapRazorComponents devuelve 500
    // en la primera petición. No hay formularios POST, pero el middleware se exige igual.
    app.UseAntiforgery();

    app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
    MessageDownloadEndpoints.Map(app);

    logger.LogInformation(
        "Live updates: store events with a {Debounce} ms debounce; directory watching {Watch}",
        options.Web.LiveUpdateDebounceMilliseconds,
        options.Web.WatchDirectory ? "enabled" : "disabled");

    // Con Web:Port=0 el puerto real sólo se conoce cuando el servidor abre el socket, así que se lee
    // de IServerAddressesFeature y no de la configuración: decir 'puerto 0' en el log sería justo
    // el tipo de mentira que este arreglo viene a quitar. Con un puerto fijo, coincide.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        // GetService y no GetRequiredService: este feature lo registra el servidor real
        // (Kestrel), pero los tests de integración usan TestServer, que no lo expone. Pedirlo
        // con GetRequiredService haría que una línea de log informativa tumbara el arranque.
        var addresses = app.Services
            .GetService<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?.Addresses;

        if (addresses is { Count: > 0 })
        {
            logger.LogInformation("Web UI listening on {Addresses}", string.Join(", ", addresses));
        }
    });
}

app.Run();
return 0;

// Convierte 'Hosting:Mode' a enum sin lanzar. Se usa antes de BindAndValidate para poder elegir el
// destino del log; un valor desconocido cae en Auto y lo reporta después el validador, que es
// quien sabe dar el mensaje que nombra la clave.
static HostingMode ParseModeOrDefault(string? value)
    => Enum.TryParse<HostingMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
        ? mode
        : HostingMode.Auto;

// Avisa, una vez y nombrando la clave, cuando un listener SMTP queda fuera de loopback. No es un
// error —levantar el mockup en una red es legítimo— pero el default es loopback y quien lo cambia
// tiene que saber que dejó de estar en su máquina.
static void WarnIfNotLoopback(ILogger logger, SmtpEndpointOptions endpoint, string key)
{
    if (endpoint.Enabled && !EndpointAddress.IsLoopback(endpoint.BindAddress))
    {
        logger.LogWarning(
            "{Key}:BindAddress {BindAddress} is not a loopback address; this SMTP listener accepts " +
            "every message from the network without authentication.",
            key,
            endpoint.BindAddress);
    }
}
