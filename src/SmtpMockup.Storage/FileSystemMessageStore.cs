using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Storage;

public sealed partial class FileSystemMessageStore
{
    /// <inheritdoc />
    public async Task<bool> SaveAsync(ReceivedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        MessagePath.Validate(message.Id);

        var summary = message.ToSummary();
        var finalPath = MessagePath.For(_rootDirectory, message.Id, message.ReceivedAtUtc);
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new MessageStoreException(
                "Could not resolve the message directory.", finalPath, new IOException("No directory."));

        try
        {
            Directory.CreateDirectory(directory);

            var tempPath = MessagePath.TempFor(finalPath);

            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, message, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Rename sin sobrescribir: el id es un ULID, así que una colisión real
            // no debería ocurrir y conviene fallar de forma explícita.
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch (IOException exception)
        {
            throw new MessageStoreException(
                "Could not persist the message; the client will receive 451.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new MessageStoreException(
                "Access denied while persisting the message; the client will receive 451.", exception);
        }

        UpdateIndex(summary);
        RaiseChanged(MessageStoreChangeKind.Stored, summary);

        _logger.LogInformation(
            "Message stored id={MessageId} from={From} to={Recipients} sizeBytes={SizeBytes} attachments={AttachmentCount}",
            message.Id,
            message.Envelope.MailFrom,
            string.Join(",", message.Envelope.RecipientTo),
            message.Size.TotalBytes,
            message.Size.AttachmentCount);

        return true;
    }

    /// <inheritdoc />
    public async Task<ReceivedMessage?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        MessagePath.Validate(id);

        var path = MessagePath.For(_rootDirectory, id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);

            return await JsonSerializer
                .DeserializeAsync<ReceivedMessage>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new MessageStoreException($"The stored message '{id}' is not valid JSON.", path, exception);
        }
        catch (IOException exception)
        {
            throw new MessageStoreException("Could not read the stored message.", path, exception);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MessageSummary>> QueryAsync(
        MailQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult(query.Apply(Snapshot()));
    }

    /// <inheritdoc />
    public Task<int> CountAsync(
        MailQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult(Snapshot().Count(summary => query.Matches(summary)));
    }

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot().Count);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        MessagePath.Validate(id);

        var path = MessagePath.For(_rootDirectory, id);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException exception)
        {
            throw new MessageStoreException("Could not delete the stored message.", path, exception);
        }

        RemoveFromIndex(id);
        RaiseChanged(MessageStoreChangeKind.Deleted, summary: null);

        _logger.LogInformation("Message deleted id={MessageId}", id);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public async Task<int> DeleteManyAsync(MailQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var ids = query.Apply(Snapshot()).Select(summary => summary.Id).ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var path = MessagePath.For(_rootDirectory, id);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (IOException exception)
            {
                throw new MessageStoreException("Could not delete the stored messages.", exception);
            }

            RemoveFromIndex(id);
        }

        RaiseChanged(MessageStoreChangeKind.Deleted, summary: null, deletedCount: deleted);
        _logger.LogInformation("Bulk delete removed {DeletedCount} messages", deleted);
        return deleted;
    }
}
