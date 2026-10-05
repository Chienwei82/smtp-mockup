using System.Text.Json;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// Doble de <see cref="IMessageStore"/> en memoria. Implementa el contrato real —incluido el
/// evento <c>Changed</c>— para que las pruebas de la UI ejerciten el mismo camino que en
/// producción: el componente se suscribe al store a través del notificador, no a un mock.
/// </summary>
public sealed class FakeMessageStore : IMessageStore
{
    private readonly Dictionary<string, ReceivedMessage> _messages = new(StringComparer.Ordinal);

    public event EventHandler<MessageStoreChangedEventArgs>? Changed;

    /// <summary>Si es <see langword="true"/>, toda operación que lista o cuenta lanza.</summary>
    public bool FailReads { get; set; }

    public IReadOnlyCollection<ReceivedMessage> Messages => _messages.Values;

    public int RebuildCount { get; private set; }

    public Task<bool> SaveAsync(ReceivedMessage message, CancellationToken cancellationToken = default)
    {
        _messages[message.Id] = message;
        Raise(new MessageStoreChangedEventArgs(MessageStoreChangeKind.Stored, message.ToSummary()));
        return Task.FromResult(true);
    }

    public Task<ReceivedMessage?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_messages.GetValueOrDefault(id));

    public Task<IReadOnlyList<MessageSummary>> QueryAsync(
        MailQuery query,
        CancellationToken cancellationToken = default)
    {
        if (FailReads)
        {
            throw new MessageStoreException("disco simulado caído");
        }

        return Task.FromResult(query.Apply(_messages.Values.Select(message => message.ToSummary())));
    }

    public Task<int> CountAsync(MailQuery query, CancellationToken cancellationToken = default)
    {
        if (FailReads)
        {
            throw new MessageStoreException("disco simulado caído");
        }

        return Task.FromResult(
            _messages.Values.Select(message => message.ToSummary()).Count(summary => query.Matches(summary)));
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_messages.Count);

    /// <summary>
    /// Reconstruye el índice leyendo los archivos del directorio, igual que el store real. Sin
    /// esto el doble no detectaría nada y las pruebas de la vigilancia del directorio pasarían
    /// sin comprobar nada.
    /// </summary>
    public Task<int> RebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        RebuildCount++;

        if (!string.IsNullOrEmpty(Directory) && System.IO.Directory.Exists(Directory))
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(
                Directory, "*" + MessagePath.FileExtension, SearchOption.AllDirectories))
            {
                if (!MessagePath.IsMessageFileName(System.IO.Path.GetFileName(path)))
                {
                    continue;
                }

                var message = JsonSerializer.Deserialize<ReceivedMessage>(File.ReadAllText(path));
                if (message is not null)
                {
                    _messages[message.Id] = message;
                }
            }
        }

        return Task.FromResult(_messages.Count);
    }

    /// <summary>Directorio del que <see cref="RebuildIndexAsync"/> lee, si es que lee.</summary>
    public string? Directory { get; set; }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var removed = _messages.Remove(id);
        if (removed)
        {
            Raise(new MessageStoreChangedEventArgs(MessageStoreChangeKind.Deleted, null));
        }

        return Task.FromResult(removed);
    }

    public Task<int> DeleteManyAsync(MailQuery query, CancellationToken cancellationToken = default)
    {
        var ids = query.Apply(_messages.Values.Select(m => m.ToSummary())).Select(s => s.Id).ToList();
        foreach (var id in ids)
        {
            _messages.Remove(id);
        }

        if (ids.Count > 0)
        {
            Raise(new MessageStoreChangedEventArgs(MessageStoreChangeKind.Deleted, null) { DeletedCount = ids.Count });
        }

        return Task.FromResult(ids.Count);
    }

    /// <summary>Inserta un mensaje sin lanzar el evento: para preparar el estado inicial.</summary>
    public void Seed(ReceivedMessage message) => _messages[message.Id] = message;

    /// <summary>Publica un cambio como si lo hiciera el store, sin tocar los datos.</summary>
    public void Raise(MessageStoreChangedEventArgs args)
    {
        foreach (EventHandler<MessageStoreChangedEventArgs> handler in (Changed?.GetInvocationList()
            ?? []).Cast<EventHandler<MessageStoreChangedEventArgs>>())
        {
            handler(this, args);
        }
    }
}
