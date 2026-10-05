namespace SmtpMockup.Core.Models;

/// <summary>
/// Vista reducida de un mensaje para el listado: nunca incluye cuerpos ni base64,
/// para que el índice en memoria sea pequeño (DESIGN §4.3).
/// </summary>
/// <param name="Id">Id ULID.</param>
/// <param name="ReceivedAtUtc">Instante de recepción.</param>
/// <param name="Subject">Subject crudo.</param>
/// <param name="SizeBytes">Tamaño total.</param>
/// <param name="AttachmentCount">Cantidad de adjuntos.</param>
/// <param name="HasAttachments">Si tiene al menos un adjunto.</param>
/// <param name="RemoteIp">IP del cliente.</param>
/// <param name="Transport">Transporte de la conexión.</param>
/// <param name="From">Primer remitente, para mostrar en la lista.</param>
/// <param name="FirstRecipient">Primer destinatario de sobre, para mostrar en la lista.</param>
public sealed record MessageSummary(
    string Id,
    DateTimeOffset ReceivedAtUtc,
    string? Subject,
    long SizeBytes,
    int AttachmentCount,
    bool HasAttachments,
    string RemoteIp,
    MailTransport Transport,
    string? From,
    string? FirstRecipient);
