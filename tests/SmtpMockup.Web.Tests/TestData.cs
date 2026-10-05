using SmtpMockup.Core.Models;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// Fábrica de mensajes y resúmenes para las pruebas. Evita repetir el mismo bloque de
/// <c>required</c> en cada test y deja claro qué campo se está variando.
/// </summary>
public static class TestData
{
    /// <summary>Un id ULID válido y fijo (no aleatorio) para poder afirmar sobre él.</summary>
    public const string Id = "01JQ8Z3K7F9A2B3C4D5E6F7G8H";

    /// <summary>Segundo id válido, para los casos con más de un mensaje.</summary>
    public const string OtherId = "01JQ8Z3K7F9A2B3C4D5E6F7G8J";

    /// <summary>Instante de recepción por defecto: fijo para que las aserciones no dependan del reloj.</summary>
    public static readonly DateTimeOffset ReceivedAt = new(2026, 2, 10, 18, 4, 5, TimeSpan.Zero);

    public static MessageSummary Summary(
        string? id = null,
        DateTimeOffset? receivedAt = null,
        string? subject = "Prueba",
        long sizeBytes = 100,
        int attachmentCount = 0,
        string? from = "sender@example.com",
        string? recipient = "a@example.com")
        => new(
            id ?? Id,
            receivedAt ?? ReceivedAt,
            subject,
            sizeBytes,
            attachmentCount,
            attachmentCount > 0,
            "127.0.0.1",
            MailTransport.Plain,
            from,
            recipient);

    public static ReceivedMessage Message(
        string? id = null,
        DateTimeOffset? receivedAt = null,
        string? subject = "Prueba",
        string? text = "Hola",
        string? html = "<p>Hola</p>",
        IReadOnlyList<AttachmentInfo>? attachments = null,
        string? rawMimeBase64 = "UmV0dXJuIFRv",
        string? from = "sender@example.com",
        string? to = "a@example.com")
        => new()
        {
            Id = id ?? Id,
            ReceivedAtUtc = receivedAt ?? ReceivedAt,
            Size = new SizeInfo(1_234, 10, attachments?.Count ?? 0),
            Envelope = new EnvelopeInfo(
                "dev-machine.local",
                "127.0.0.1",
                54_321,
                from,
                [to ?? "a@example.com", "hidden@example.com"],
                false,
                MailTransport.StartTls,
                true),
            Headers =
            [
                new HeaderInfo("Message-ID", "<abc@mail.example.com>"),
                new HeaderInfo("X-Custom", "1"),
            ],
            Subject = subject,
            MessageId = "abc@mail.example.com",
            SentDate = ReceivedAt.AddMinutes(-5),
            From = [new MailAddressInfo("Sender", from ?? "sender@example.com")],
            To = [new MailAddressInfo(null, to ?? "a@example.com")],
            Bcc = [new MailAddressInfo(null, "hidden@example.com")],
            Body = new BodyInfo(text, html),
            Attachments = attachments ?? [],
            Raw = rawMimeBase64 is null ? null : new RawMimeInfo(10, false, rawMimeBase64),
        };

    public static AttachmentInfo Attachment(
        string fileName = "reporte.pdf",
        string contentType = "application/pdf",
        byte[]? content = null,
        bool omitted = false,
        bool inline = false,
        string? contentBase64 = null)
        => new(
            fileName,
            contentType,
            content?.Length ?? 0,
            inline ? "cid:logo" : null,
            inline,
            "9f86d081",
            omitted,
            contentBase64 ?? (omitted ? null : Convert.ToBase64String(content ?? [1, 2, 3])));
}
