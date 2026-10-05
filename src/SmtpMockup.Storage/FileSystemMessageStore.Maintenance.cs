using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmtpMockup.Core.Models;

namespace SmtpMockup.Storage;

public sealed partial class FileSystemMessageStore
{
    /// <summary>
    /// Construye el índice recorriendo los archivos existentes. Se llama al arrancar:
    /// la UI lee del índice, no de cada archivo (DESIGN §4.3). También lo invoca la UI cuando
    /// <c>FileSystemWatcher</c> detecta cambios hechos por fuera del proceso.
    /// </summary>
    /// <returns>Cantidad de mensajes indexados.</returns>
    public async Task<int> RebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        var summaries = new List<MessageSummary>();

        if (Directory.Exists(_rootDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(
                _rootDirectory, "*" + MessagePath.FileExtension, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // El patrón del archivo es el filtro: descarta los .json.tmp de una escritura
                // en curso, los .bak y cualquier otra cosa que alguien haya soltado en el
                // directorio. Sin esto, un archivo copiado a mano aparecería en el listado y
                // entraría en un borrado masivo (SPEC §10.2).
                if (!MessagePath.IsMessageFileName(System.IO.Path.GetFileName(path)))
                {
                    continue;
                }

                var message = await ReadQuietlyAsync(path, cancellationToken).ConfigureAwait(false);
                if (message is not null)
                {
                    summaries.Add(message.ToSummary());
                }
            }
        }

        lock (_gate)
        {
            _summaries.Clear();
            foreach (var summary in summaries)
            {
                _summaries[summary.Id] = summary;
            }
        }

        _logger.LogInformation("Message index rebuilt with {Count} entries", summaries.Count);

        return summaries.Count;
    }

    /// <summary>
    /// Borra los <c>.json.tmp</c> huérfanos de una escritura interrumpida: un temporal
    /// significa un mensaje que nunca llegó a renombrarse (SPEC §8).
    /// </summary>
    /// <returns>Cantidad de temporales purgados.</returns>
    public int PurgeTempFiles()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return 0;
        }

        var purged = 0;
        foreach (var path in Directory.EnumerateFiles(_rootDirectory, "*.json.tmp", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(path);
                purged++;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Could not purge the orphan temp file {Path}", path);
            }
        }

        if (purged > 0)
        {
            _logger.LogInformation("Purged {PurgedCount} orphan temp files", purged);
        }

        return purged;
    }

    /// <summary>
    /// Lee un archivo del índice sin propagar el error: un JSON corrupto no debe
    /// impedir indexar el resto de los mensajes (SPEC §10.2).
    /// </summary>
    private async Task<ReceivedMessage?> ReadQuietlyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous);

            return await JsonSerializer
                .DeserializeAsync<ReceivedMessage>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Skipping unreadable message file {Path}", path);
            return null;
        }
    }
}
