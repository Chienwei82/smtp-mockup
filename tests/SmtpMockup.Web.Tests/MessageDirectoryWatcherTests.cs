using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;
using SmtpMockup.Web.Services;
using Microsoft.Extensions.Logging;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// Vigilancia del directorio (SPEC §9.6). Un cambio hecho por fuera del proceso (copiar un JSON
/// a mano, borrarlo con el explorador) tiene que llegar a la UI igual que uno del store.
/// </summary>
public sealed class MessageDirectoryWatcherTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "smtp-mockup-tests",
        Guid.NewGuid().ToString("N"));

    private readonly FakeMessageStore _store = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly StorageOptions _storageOptions;

    public MessageDirectoryWatcherTests()
    {
        _storageOptions = new StorageOptions { Directory = _directory };

        // Las carpetas del día existen antes de arrancar el vigilante. FileSystemWatcher
        // engancha inotify a los directorios que ya están, y añadirlos justo después de
        // EnableRaisingEvents es una carrera que en Linux se pierde; en producción el store
        // crea la carpeta del día al guardar el primer mensaje y las siguientes ya quedan
        // vigiladas.
        Directory.CreateDirectory(DayDirectory);
    }

    private string DayDirectory => Path.Combine(_directory, "2026", "02", "10");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>Un id ULID válido; el watcher sólo acepta archivos con este nombre (SPEC §10.2).</summary>
    private const string Id = "01JQ8Z3K7F9A2B3C4D5E6F7G8H";

    private MessageDirectoryWatcher CreateWatcher(TimeSpan? debounce = null, TimeSpan? suppression = null)
    {
        _store.Directory = _directory;

        return new(
            _store,
            _notifier,
            Options.Create(_storageOptions),
            NullLogger<MessageDirectoryWatcher>.Instance,
            debounce ?? TimeSpan.FromMilliseconds(50),
            suppression ?? TimeSpan.Zero);
    }

    /// <summary>Escribe un JSON de mensaje en disco, como si lo copiara alguien a mano.</summary>
    private string WriteMessageFile(string fileName = Id + ".json")
    {
        var path = Path.Combine(DayDirectory, fileName);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(TestData.Message(id: Id)));
        return path;
    }

    [Fact]
    public async Task A_file_created_outside_the_process_is_detected()
    {
        var watcher = CreateWatcher();
        await watcher.StartAsync(CancellationToken.None);

        var path = WriteMessageFile();
        await WaitForRebuildAsync(watcher, path);

        await watcher.StopAsync(CancellationToken.None);

        Assert.True(_store.RebuildCount > 0, "the watcher did not rebuild the index for the new file");
    }

    [Fact]
    public async Task A_change_that_the_store_can_see_notifies_the_ui()
    {
        var watcher = CreateWatcher();
        await watcher.StartAsync(CancellationToken.None);

        var path = WriteMessageFile();
        await WaitForRebuildAsync(watcher, path);
        await watcher.RefreshAsync();

        await watcher.StopAsync(CancellationToken.None);

        Assert.Single(_notifier.Notifications);
        Assert.True(_notifier.Notifications[0].External);
    }

    [Fact]
    public async Task Temporary_and_foreign_files_are_ignored()
    {
        var watcher = CreateWatcher();
        await watcher.StartAsync(CancellationToken.None);

        // Un .json.tmp es una escritura atómica en curso y un nombre que no es ULID no es un
        // mensaje: ninguno de los dos puede disparar un rebuild del índice.
        WriteMessageFile(Id + ".json.tmp");
        WriteMessageFile("notas.txt.json");

        await Task.Delay(500);
        await watcher.StopAsync(CancellationToken.None);

        Assert.Equal(0, _store.RebuildCount);
        Assert.Empty(_notifier.Notifications);
    }

    [Fact]
    public async Task The_writes_of_the_store_itself_do_not_trigger_a_rebuild()
    {
        // Cada correo recibido genera un evento del sistema de archivos. Sin la supresión, la UI
        // reconstruiría el índice entero una vez por mensaje.
        var watcher = CreateWatcher(suppression: TimeSpan.FromMinutes(1));
        await watcher.StartAsync(CancellationToken.None);

        await _store.SaveAsync(TestData.Message(id: Id));
        WriteMessageFile();

        await Task.Delay(500);
        await watcher.StopAsync(CancellationToken.None);

        Assert.Equal(0, _store.RebuildCount);
        Assert.Empty(_notifier.Notifications);
    }

    [Fact]
    public async Task Refreshing_without_changes_does_not_notify_the_ui()
    {
        var watcher = CreateWatcher();
        await watcher.StartAsync(CancellationToken.None);
        await watcher.StopAsync(CancellationToken.None);

        await watcher.RefreshAsync();

        Assert.Equal(1, _store.RebuildCount);
        Assert.Empty(_notifier.Notifications);
    }

    /// <summary>
    /// Espera a que el watcher reaccione, con tope de tiempo en vez de un <c>Thread.Sleep</c>
    /// fijo: es lo que evita que la prueba sea intermitente en CI.
    /// </summary>
    /// <remarks>
    /// Falla si se agota el plazo. Antes devolvía en silencio y dejaba que la aserción
    /// siguiente decidiera, y eso ocultaba el fallo real: el watcher no había reconstruido
    /// nada, el test gastaba los 30 s completos y luego pasaba igualmente porque otra vía
    /// (un <c>RefreshAsync</c> explícito, p. ej.) sí había reconstruido. Un test que espera
    /// 30 s y aun así sale verde no está probando nada.
    /// </remarks>
    /// <summary>
    /// Espera a que el watcher reaccione, con tope de tiempo en vez de un <c>Thread.Sleep</c>
    /// fijo: es lo que evita que la prueba sea intermitente en CI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fallaba de forma intermitente cuando sólo se escribía el archivo una vez. No era un
    /// problema del watcher: una sonda con el mismo código detectó el cambio en 5 de 5 veces,
    /// y los cinco tests pasaban siempre en solitario. Bajo carga (las pruebas en paralelo de
    /// los demás proyectos compiten por la CPU) el evento de inotify de esa única escritura
    /// llegaba tarde o se perdía, y los 30 s se consumían sin reconstruir nada.
    /// </para>
    /// <para>
    /// La causa es que la prueba daba por hecho algo que <see cref="FileSystemWatcher"/> no
    /// garantiza: que <em>una</em> escritura produce <em>un</em> evento. inotify agrupa y puede
    /// descartar avisos bajo presión, así que un solo evento no es una señal fiable. Por eso
    /// aquí se reescribe el archivo cada 250 ms hasta que el watcher reaccione: si el evento se
    /// perdió, el siguiente lo recupera. El aserto sigue siendo real — si el watcher no
    /// reacciona nunca, el plazo se agota y la prueba falla — y deja de depender de que un
    /// único evento llegue a tiempo.
    /// </para>
    /// <para>
    /// Antes devolvía en silencio al agotarse el plazo y dejaba que la aserción siguiente
    /// decidiera, y eso ocultaba el fallo real: el watcher no había reconstruido nada, la
    /// prueba gastaba los 30 s completos y luego pasaba igualmente porque otra vía (un
    /// <c>RefreshAsync</c> explícito, p. ej.) sí había reconstruido. Un test que espera 30 s y
    /// aun así sale verde no está probando nada.
    /// </para>
    /// </remarks>
    private async Task WaitForRebuildAsync(MessageDirectoryWatcher watcher, string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var nextTouch = DateTime.UtcNow.AddMilliseconds(250);

        while (DateTime.UtcNow < deadline)
        {
            if (_store.RebuildCount > 0)
            {
                return;
            }

            if (DateTime.UtcNow >= nextTouch)
            {
                WriteMessageFile(Path.GetFileName(path));
                nextTouch = DateTime.UtcNow.AddMilliseconds(250);
            }

            await Task.Delay(50);
        }

        Assert.Fail(
            "The watcher never rebuilt the index within 30s, even after rewriting the file every "
            + "250ms to force a fresh filesystem event. Watched directory: "
            + $"{watcher.WatchedDirectory}. If this is the first run on a machine with a low "
            + "inotify limit, check fs.inotify.max_user_watches and fs.inotify.max_queued_events.");
    }

    [Fact]
    public void The_watched_directory_is_resolved_against_the_executable()
        => Assert.True(Path.IsPathRooted(CreateWatcher().WatchedDirectory));

    private sealed class RecordingNotifier : IMessageChangeNotifier
    {
        private readonly List<MessageChangeNotification> _notifications = [];
        private readonly SemaphoreSlim _signal = new(0);

        public IReadOnlyList<MessageChangeNotification> Notifications
        {
            get
            {
                lock (_notifications)
                {
                    return [.. _notifications];
                }
            }
        }

        public event EventHandler<MessageChangeNotification>? Changed;

        public void NotifyExternalChange()
        {
            lock (_notifications)
            {
                _notifications.Add(new MessageChangeNotification([], 0, true));
            }

            Changed?.Invoke(this, new MessageChangeNotification([], 0, true));
            _signal.Release();
        }

        public async Task WaitAsync(TimeSpan timeout)
        {
            if (!await _signal.WaitAsync(timeout))
            {
                throw new TimeoutException("The watcher did not notify anything within the timeout.");
            }
        }
    }
}
