namespace SmtpMockup.Core.Models;

/// <summary>
/// Un correo recibido y persistido: el contrato del JSON en disco (SPEC §7).
/// Los campos opcionales usan <see langword="null"/> o listas vacías; nunca se omiten.
/// </summary>
public sealed record ReceivedMessage
{
    /// <summary>Versión del esquema del JSON; hoy <c>"1.0"</c>.</summary>
    public const string CurrentSchemaVersion = "1.0";

    /// <summary>Versión del esquema con el que se serializó.</summary>
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Id ULID del mensaje.</summary>
    public required string Id { get; init; }

    /// <summary>Instante de recepción en UTC.</summary>
    public required DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>Tamaños del mensaje.</summary>
    public required SizeInfo Size { get; init; }

    /// <summary>El sobre SMTP.</summary>
    public required EnvelopeInfo Envelope { get; init; }

    /// <summary>Todos los headers, en orden.</summary>
    public required IReadOnlyList<HeaderInfo> Headers { get; init; }

    /// <summary>
    /// El subject <em>crudo</em>, tal como vino (puede ser <c>=?UTF-8?B?...?=</c>).
    /// La UI lo decodifica al mostrarlo, así no se pierde información.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>El valor de <c>Message-ID</c> sin ángulos; generado si el cliente no lo envió.</summary>
    public string? MessageId { get; init; }

    /// <summary>La cabecera <c>Date</c> del cliente; <see langword="null"/> si falta o es inválida.</summary>
    public DateTimeOffset? SentDate { get; init; }

    /// <summary>Cabecera <c>From</c>.</summary>
    public IReadOnlyList<MailAddressInfo> From { get; init; } = [];

    /// <summary>Cabecera <c>To</c>.</summary>
    public IReadOnlyList<MailAddressInfo> To { get; init; } = [];

    /// <summary>Cabecera <c>Cc</c>.</summary>
    public IReadOnlyList<MailAddressInfo> Cc { get; init; } = [];

    /// <summary>
    /// Destinatarios de sobre que <em>no</em> aparecen en ninguna cabecera visible.
    /// Se calcula comparando <see cref="EnvelopeInfo.RecipientTo"/> contra To/Cc.
    /// </summary>
    public IReadOnlyList<MailAddressInfo> Bcc { get; init; } = [];

    /// <summary>Cabecera <c>Reply-To</c>.</summary>
    public IReadOnlyList<MailAddressInfo> ReplyTo { get; init; } = [];

    /// <summary>Cuerpos decodificados.</summary>
    public required BodyInfo Body { get; init; }

    /// <summary>Adjuntos y recursos inline.</summary>
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];

    /// <summary>MIME crudo, si <c>Storage:KeepRawMime</c> lo conserva.</summary>
    public RawMimeInfo? Raw { get; init; }

    /// <summary>
    /// Proyección ligera para el listado y el índice en memoria: sin cuerpos ni base64.
    /// </summary>
    public MessageSummary ToSummary() => new(
        Id,
        ReceivedAtUtc,
        Subject,
        Size.TotalBytes,
        Size.AttachmentCount,
        Size.AttachmentCount > 0,
        Envelope.RemoteIp,
        Envelope.Transport,
        From.FirstOrDefault()?.Address,
        Envelope.RecipientTo.Count > 0 ? Envelope.RecipientTo[0] : null);
}
