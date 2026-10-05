using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Hosting;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Storage;

/// <summary>
/// Persistencia en archivos JSON: un correo = un archivo, particionado por día, con
/// escritura atómica <c>&lt;id&gt;.json.tmp</c> → rename (SPEC §7 y §8).
/// </summary>
/// <remarks>
/// Se implementa en dos parciales: este contiene el índice en memoria y la
/// notificación de cambios; <see cref="FileSystemMessageStore"/> las operaciones CRUD.
/// </remarks>
public sealed partial class FileSystemMessageStore : IMessageStore, IDisposable
{
    private readonly string _rootDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<FileSystemMessageStore> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MessageSummary> _summaries = new(StringComparer.Ordinal);

    /// <summary>Crea el store con la configuración de <c>Storage:*</c>.</summary>
    /// <remarks>
    /// <see cref="StorageOptions.Directory"/> relativa se resuelve contra el directorio del
    /// ejecutable, no contra el directorio de trabajo: como Windows Service el proceso arranca con
    /// <c>C:\Windows\System32</c> como CWD, y los correos se irían a <c>System32\data\messages</c>
    /// (o fallarían por permisos) y desaparecerían del <c>data\</c> del despliegue.
    /// </remarks>
    /// <exception cref="MessageStoreException">El directorio no se pudo crear.</exception>
    public FileSystemMessageStore(
        IOptions<StorageOptions> options,
        JsonSerializerOptions jsonOptions,
        ILogger<FileSystemMessageStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _rootDirectory = HostPath.Resolve(options.Value.Directory);
        _jsonOptions = jsonOptions;
        _logger = logger;

        EnsureRootDirectory();
    }

    /// <inheritdoc />
    public event EventHandler<MessageStoreChangedEventArgs>? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        // System.Threading.Lock no implementa IDisposable: sólo hay que soltar la
        // referencia para que el recolector lo recupere.
    }

    /// <summary>
    /// Emite el evento fuera de cualquier lock y con cada handler protegido: un suscriptor
    /// que lance se registra y se ignora, nunca rompe el guardado (SPEC §10.2).
    /// </summary>
    private void RaiseChanged(MessageStoreChangeKind kind, MessageSummary? summary, int deletedCount = 1)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var args = new MessageStoreChangedEventArgs(kind, summary) { DeletedCount = deletedCount };

        foreach (EventHandler<MessageStoreChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "A Changed subscriber threw; the save itself succeeded and the subscriber was ignored");
            }
        }
    }

    private IReadOnlyList<MessageSummary> Snapshot()
    {
        lock (_gate)
        {
            return _summaries.Values.ToList();
        }
    }

    private void UpdateIndex(MessageSummary summary)
    {
        lock (_gate)
        {
            _summaries[summary.Id] = summary;
        }
    }

    private void RemoveFromIndex(string id)
    {
        lock (_gate)
        {
            _summaries.Remove(id);
        }
    }

    private void EnsureRootDirectory()
    {
        try
        {
            Directory.CreateDirectory(_rootDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MessageStoreException(
                $"Could not create the storage directory '{_rootDirectory}'.", _rootDirectory, exception);
        }
    }
}
