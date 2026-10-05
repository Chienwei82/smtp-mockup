using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Hosting;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Web.Services;

/// <summary>
/// Vigila el directorio de mensajes con <see cref="FileSystemWatcher"/> para que los cambios
/// hechos <em>fuera</em> del proceso (copiar un JSON a mano, borrarlos con el explorador de
/// archivos, restaurarlos de una copia) también lleguen a la UI (SPEC §9.6).
/// </summary>
/// <remarks>
/// El evento del store es en proceso y sólo lo ven los cambios que pasan por
/// <c>IMessageStore</c>. Sin este servicio, borrar un archivo con el explorador dejaría la fila
/// colgada en el navegador hasta que expirara el TTL del índice. Al detectar un cambio se
/// reconstruye el índice y se publica por el mismo notificador que ya consume la UI.
/// </remarks>
public sealed class MessageDirectoryWatcher : BackgroundService
{
    /// <summary>Espera de agrupación de los eventos del watcher; evita un rebuild por archivo.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Ventana en la que se ignoran los eventos del sistema de archivos. El store ya avisó a
    /// la UI por su cuenta y actualizó el índice, así que reconstruirlo otra vez sería recorrer
    /// todos los mensajes por cada correo recibido. Es una supresión, no un descarte: si el
    /// archivo se copia desde otro sitio justo después, el siguiente evento la repasa.
    /// </summary>
    public static readonly TimeSpan DefaultSelfWriteSuppression = TimeSpan.FromSeconds(1);

    private readonly IMessageStore _store;
    private readonly IMessageChangeNotifier _notifier;
    private readonly ILogger<MessageDirectoryWatcher> _logger;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _selfWriteSuppression;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private bool _pending;
    private DateTimeOffset _lastStoreWriteUtc = DateTimeOffset.MinValue;

    /// <summary>Crea el vigilante.</summary>
    /// <param name="store">Store cuyo índice se reconstruye al detectar cambios.</param>
    /// <param name="notifier">Notificador que avisa a la UI.</param>
    /// <param name="storageOptions">Aporta el directorio vigilado (<c>Storage:Directory</c>).</param>
    /// <param name="logger">Log del servicio.</param>
    /// <param name="debounce">
    /// Espera de agrupación. Es un parámetro y no una constante para que los tests no dependan
    /// del reloj ni dorman 500 ms por cada prueba.
    /// </param>
    /// <param name="selfWriteSuppression">
    /// Ventana en la que se ignoran los eventos del sistema de archivos provocados por el propio store.
    /// </param>
    public MessageDirectoryWatcher(
        IMessageStore store,
        IMessageChangeNotifier notifier,
        IOptions<StorageOptions> storageOptions,
        ILogger<MessageDirectoryWatcher> logger,
        TimeSpan? debounce = null,
        TimeSpan? selfWriteSuppression = null)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);

        _store = store;
        _notifier = notifier;
        _logger = logger;
        _debounce = debounce ?? DefaultDebounce;
        _selfWriteSuppression = selfWriteSuppression ?? DefaultSelfWriteSuppression;
        WatchedDirectory = HostPath.Resolve(storageOptions.Value.Directory);

        // El store es la fuente de verdad de "guardado": cada vez que escribe, ya actualizó el
        // índice y ya avisó. Aquí sólo se marca la hora para filtrar el evento de disco.
        _store.Changed += OnStoreChanged;
    }

    /// <summary>Directorio vigilado, ya resuelto contra el directorio del ejecutable.</summary>
    public string WatchedDirectory { get; }

    /// <summary>
    /// Marca que un cambio del store acaba de ocurrir. Se registra fuera del lock para no
    /// mantenerlo tomado desde el hilo del listener SMTP.
    /// </summary>
    private void OnStoreChanged(object? sender, MessageStoreChangedEventArgs args)
    {
        lock (_gate)
        {
            _lastStoreWriteUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        System.IO.Directory.CreateDirectory(WatchedDirectory);

        _timer = new Timer(
            _ => _ = OnTimerAsync(stoppingToken),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        try
        {
            _watcher = new FileSystemWatcher(WatchedDirectory)
            {
                // Los mensajes viven en data/messages/AAAA/MM/DD/, así que hay que mirar
                // recursivamente: un filtro por subdirectorio se perdería casi todo.
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };

            _watcher.Created += OnCreated;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;

            _logger.LogInformation("Watching {WatchedDirectory} for external message changes", WatchedDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Un watcher que no se puede instalar (permisos, ruta de red, límite de inotify)
            // degrada la UI a "cambios externos al TTL del índice", pero no impide arrancar.
            _logger.LogWarning(
                exception,
                "Could not watch {WatchedDirectory} for external changes; the list only refreshes on store events",
                WatchedDirectory);
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _timer?.Dispose();
        _timer = null;
        _store.Changed -= OnStoreChanged;

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// Callback del temporizador: si hay un cambio pendiente, reconstruye el índice y avisa.
    /// El temporizador se rearma en cada evento, así que si no llegó nada no hay nada que hacer.
    /// </summary>
    private async Task OnTimerAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_pending)
            {
                return;
            }

            _pending = false;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconstruye el índice y avisa a la UI. Es <c>internal</c> y no privado para que la
    /// prueba pueda dispararlo sin depender del temporizador.
    /// </summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Dos refrescos solapados compararían snapshots cruzados y podrían avisar dos veces
            // de lo mismo, así que se serializan.
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var before = await IdsAsync(cancellationToken).ConfigureAwait(false);
            await _store.RebuildIndexAsync(cancellationToken).ConfigureAwait(false);
            var after = await IdsAsync(cancellationToken).ConfigureAwait(false);

            // Un rebuild que no cambia nada (por ejemplo, un archivo que se sigue escribiendo)
            // no debe re-renderizar la UI: se compara el conjunto de ids, no sólo el total, para
            // que un borrado seguido de un alta con el mismo cardinal sí se notifique.
            if (!before.SetEquals(after))
            {
                _notifier.NotifyExternalChange();
            }
        }
        catch (OperationCanceledException)
        {
            // Se está apagando: no es un error.
        }
        catch (MessageStoreException exception)
        {
            _logger.LogWarning(exception, "Could not rebuild the message index after an external change");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Ids actualmente indexados. Vive en memoria, así que el coste es despreciable.</summary>
    private async Task<HashSet<string>> IdsAsync(CancellationToken cancellationToken)
    {
        var all = await _store
            .QueryAsync(new MailQuery { PageSize = MailQuery.HardMaxPageSize }, cancellationToken)
            .ConfigureAwait(false);

        return [.. all.Select(summary => summary.Id)];
    }

    private void OnCreated(object sender, FileSystemEventArgs args) => OnPath(args.FullPath);

    private void OnChanged(object sender, FileSystemEventArgs args) => OnPath(args.FullPath);

    private void OnDeleted(object sender, FileSystemEventArgs args) => OnPath(args.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs args) => OnPath(args.FullPath);

    private void OnError(object sender, ErrorEventArgs args)
        => _logger.LogWarning(args.GetException(), "The message directory watcher reported an error");

    /// <summary>
    /// Encola un rebuild si el camino es un JSON de mensaje. Se ignoran los
    /// <c>.json.tmp</c> de una escritura atómica en curso y cualquier otro archivo: si no,
    /// un guardado propio del store dispararía un rebuild por cada mensaje recibido.
    /// </summary>
    private void OnPath(string path)
    {
        if (!IsMessageFile(path))
        {
            return;
        }

        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _lastStoreWriteUtc < _selfWriteSuppression)
            {
                return;
            }

            _pending = true;
            _timer?.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// El watcher acepta exactamente los mismos archivos que el índice. Usar un filtro más
    /// laxo aquí que en el indexado hace que cualquier <c>.txt.json</c> suelto en el directorio
    /// dispare un recorrido completo de todos los mensajes sin ningún efecto útil.
    /// </summary>
    private static bool IsMessageFile(string path)
        => MessagePath.IsMessageFileName(System.IO.Path.GetFileName(path));
}