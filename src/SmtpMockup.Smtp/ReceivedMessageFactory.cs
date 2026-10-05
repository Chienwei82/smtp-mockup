using System.Text;
using MimeKit;
using SmtpMockup.Core.Models;

namespace SmtpMockup.Smtp;

/// <summary>Límites de persistencia que el conversor necesita de <c>Storage:*</c>.</summary>
/// <param name="MaxInlineAttachmentBytes">Por encima, el adjunto se marca <c>omitted</c>.</param>
/// <param name="KeepRawMime">Si se conserva el MIME crudo.</param>
/// <param name="MaxRawMimeBytes">Tope del MIME crudo; 0 significa sin tope.</param>
internal readonly record struct StorageLimits(
    int MaxInlineAttachmentBytes,
    bool KeepRawMime,
    int MaxRawMimeBytes);

/// <summary>
/// Convierte un <see cref="MimeMessage"/> de MimeKit al modelo del SPEC §7: el adaptador
/// entre el pipeline de la librería SMTP y el dominio de <c>Core</c>.
/// </summary>
internal static partial class ReceivedMessageFactory
{
    /// <summary>Nombre que se usa cuando un adjunto no trae <c>filename</c>.</summary>
    public const string UnnamedAttachment = "unnamed";

    /// <summary>
    /// Construye el <see cref="ReceivedMessage"/> a partir del mensaje parseado y del
    /// sobre que reporta el servidor SMTP.
    /// </summary>
    /// <param name="message">Mensaje ya parseado por MimeKit.</param>
    /// <param name="envelope">Datos del sobre (remitente, RCPT TO, IP, TLS).</param>
    /// <param name="rawBytes">MIME crudo, para <c>raw.contentBase64</c>.</param>
    /// <param name="limits">Límites de adjuntos y MIME crudo.</param>
    /// <param name="id">Id ULID ya generado para este mensaje.</param>
    /// <param name="receivedAtUtc">Instante de recepción.</param>
    public static ReceivedMessage Create(
        MimeMessage message,
        EnvelopeInfo envelope,
        ReadOnlyMemory<byte> rawBytes,
        StorageLimits limits,
        string id,
        DateTimeOffset receivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(envelope);

        var attachments = ReadAttachments(message, limits.MaxInlineAttachmentBytes);
        var body = new BodyInfo(message.TextBody, message.HtmlBody);

        // bodyBytes se mide en UTF-8, no en caracteres: el campo se llama "bytes" y se compara con
        // totalBytes, que sí viene de rawBytes.Length. Con 'string.Length' un cuerpo con acentos o
        // emoji (2-4 bytes por carácter) inflaba el número y la suma de las dos piezas ya no cuadraba
        // con el tamaño real del mensaje.
        var bodyBytes = Utf8Length(body.Text) + Utf8Length(body.Html);

        return new ReceivedMessage
        {
            Id = id,
            ReceivedAtUtc = receivedAtUtc,
            Size = new SizeInfo(rawBytes.Length, bodyBytes, attachments.Count),
            Envelope = envelope,
            Headers = ReadHeaders(message),
            Subject = message.Subject,
            MessageId = NormalizeMessageId(message.MessageId, id),
            SentDate = message.Date,
            From = ReadAddresses(message.From),
            To = ReadAddresses(message.To),
            Cc = ReadAddresses(message.Cc),
            Bcc = ComputeBcc(envelope.RecipientTo, message),
            ReplyTo = ReadAddresses(message.ReplyTo),
            Body = body,
            Attachments = attachments,
            Raw = BuildRaw(rawBytes, limits),
        };
    }

    /// <summary>Tamaño en UTF-8 de un cuerpo, o cero si no lo hay.</summary>
    private static long Utf8Length(string? body) => body is null ? 0 : Encoding.UTF8.GetByteCount(body);
}
