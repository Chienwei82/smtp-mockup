using SmtpMockup.Core.Models;

namespace SmtpMockup.Core.Storage;

/// <summary>Tipo de cambio que notifica <see cref="IMessageStore.Changed"/>.</summary>
public enum MessageStoreChangeKind
{
    /// <summary>Se guardó un mensaje nuevo.</summary>
    Stored = 0,

    /// <summary>Se borró al menos un mensaje.</summary>
    Deleted = 1,
}

/// <summary>
/// Datos del cambio notificado. Para borrados masivos <see cref="Message"/> es
/// <see langword="null"/> y <see cref="DeletedCount"/> trae el total, para que la UI
/// no tenga que releer el disco (DESIGN §7).
/// </summary>
public sealed record MessageStoreChangedEventArgs(MessageStoreChangeKind Kind, MessageSummary? Message)
{
    /// <summary>Instante del cambio.</summary>
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Cantidad de mensajes afectados; 1 salvo en borrados masivos.</summary>
    public int DeletedCount { get; init; } = 1;
}

/// <summary>
/// Contrato de persistencia de mensajes. La UI la inyecta directamente (sin HTTP) y
/// el listener SMTP la consume para persistir antes de responder <c>250</c> (SPEC §7).
/// </summary>
public interface IMessageStore
{
    /// <summary>
    /// Se dispara <em>después</em> del rename atómico y fuera de cualquier lock, para
    /// que un <c>250</c> de SMTP o una fila en la UI nunca apunten a un archivo inexistente.
    /// </summary>
    event EventHandler<MessageStoreChangedEventArgs>? Changed;

    /// <summary>
    /// Persiste el mensaje. El <c>250</c> del SMTP sólo se envía si esto devuelve
    /// <see langword="true"/>, de modo que el mensaje está en disco antes de aceptarlo.
    /// </summary>
    /// <returns><see langword="true"/> si se persistió; <see langword="false"/> si no.</returns>
    /// <exception cref="MessageStoreException">Error de I/O (disco lleno, sin permisos).</exception>
    Task<bool> SaveAsync(ReceivedMessage message, CancellationToken cancellationToken = default);

    /// <summary>Lee un mensaje por id, o <see langword="null"/> si no existe.</summary>
    Task<ReceivedMessage?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista los mensajes que cumplen el filtro, del más reciente al más antiguo.
    /// Se resuelve sobre el índice en memoria, nunca leyendo cada archivo.
    /// </summary>
    /// <remarks>
    /// La paginación de <see cref="MailQuery"/> se aplica <em>después</em> del filtro, así que
    /// el total de la página no dice cuántos hay: para eso está <see cref="CountAsync(MailQuery)"/>.
    /// </remarks>
    Task<IReadOnlyList<MessageSummary>> QueryAsync(MailQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cuenta los mensajes que cumplen el filtro, <em>ignorando</em> la paginación. Es lo que
    /// permite a la UI dibujar "página 3 de 12" y el total sin releer cada archivo.
    /// </summary>
    Task<int> CountAsync(MailQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconstruye el índice en memoria desde el disco. La UI lo llama cuando el vigilante del
    /// directorio detecta un cambio hecho fuera del proceso, porque el índice sólo se actualiza
    /// en <see cref="SaveAsync"/> y <see cref="DeleteAsync"/>.
    /// </summary>
    /// <returns>Cantidad de mensajes indexados.</returns>
    Task<int> RebuildIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>Número total de mensajes almacenados.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Borra un mensaje por id.</summary>
    /// <returns><see langword="true"/> si existía y se borró.</returns>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Borra todos los mensajes que cumplen el filtro.</summary>
    /// <returns>Cantidad de mensajes borrados.</returns>
    Task<int> DeleteManyAsync(MailQuery query, CancellationToken cancellationToken = default);
}
