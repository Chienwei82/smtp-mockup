using System.Text;
using MailKit.Net.Smtp;
using MimeKit;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// Tests de integración del cuerpo HTML y de los adjuntos (SPEC §11.1, criterio 4).
/// </summary>
public sealed class HtmlAndAttachmentMessageTests
{
    [Fact]
    public async Task A_message_with_html_and_two_attachments_persists_both_bodies_and_sizes()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        var pdfBytes = Encoding.UTF8.GetBytes("%PDF-1.4 contenido binario del reporte");
        var csvBytes = Encoding.UTF8.GetBytes("a,b,c\n1,2,3\n");

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "a@example.com"));
        message.Subject = "Reporte";

        var builder = new BodyBuilder
        {
            TextBody = "Hola en plano",
            HtmlBody = "<p>Hola en <b>HTML</b></p>",
        };

        builder.Attachments.Add("reporte.pdf", pdfBytes);
        builder.Attachments.Add("datos.csv", csvBytes);
        message.Body = builder.ToMessageBody();

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await OnlyStoredAsync(fixture);

        // Ambos cuerpos deben quedar poblados (RF-11).
        Assert.True(stored.Body.HasText);
        Assert.True(stored.Body.HasHtml);
        Assert.Equal("Hola en plano", stored.Body.Text);
        Assert.Equal("<p>Hola en <b>HTML</b></p>", stored.Body.Html);

        Assert.Equal(2, stored.Attachments.Count);
        Assert.Equal(2, stored.Size.AttachmentCount);

        var pdf = Assert.Single(stored.Attachments, a => a.FileName == "reporte.pdf");
        var csv = Assert.Single(stored.Attachments, a => a.FileName == "datos.csv");

        // sizeBytes debe ser SIEMPRE la longitud del base64 decodificado (SPEC §11.1).
        // Para un adjunto binario eso coincide además con el original byte a byte.
        Assert.Equal(pdfBytes.Length, pdf.SizeBytes);
        Assert.Equal(pdfBytes, Convert.FromBase64String(pdf.ContentBase64!));
        Assert.Equal(Convert.FromBase64String(csv.ContentBase64!).Length, csv.SizeBytes);

        // El CSV es text/*: al viajar por SMTP las terminaciones de línea se normalizan
        // a CRLF (LF -> CRLF), así que los bytes decodificados son 2 más que el original.
        // Es el comportamiento de MIME, no un defecto del mockup.
        Assert.Equal(csvBytes.Length + 2, csv.SizeBytes);
        Assert.Equal("text/csv", csv.ContentType);

        Assert.False(pdf.Omitted);
        Assert.False(pdf.IsInline);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.Equal(64, pdf.ChecksumSha256!.Length);
    }

    [Fact]
    public async Task An_html_only_message_keeps_the_html_body()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "a@example.com"));
        message.Body = new TextPart("html") { Text = "<h1>Sólo HTML</h1>" };

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await OnlyStoredAsync(fixture);

        Assert.Equal("<h1>Sólo HTML</h1>", stored.Body.Html);
        Assert.True(stored.Body.HasHtml);
    }

    [Fact]
    public async Task The_raw_mime_is_stored_base64_encoded()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "a@example.com"));
        message.Subject = "con raw";
        message.Body = new TextPart("plain") { Text = "cuerpo" };

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await OnlyStoredAsync(fixture);

        Assert.NotNull(stored.Raw);
        Assert.False(stored.Raw.Truncated);
        Assert.NotNull(stored.Raw.ContentBase64);

        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(stored.Raw.ContentBase64!));

        // El MIME crudo debe seguir siendo parseable y conservar el asunto.
        var reparsed = MimeMessage.Load(new MemoryStream(Convert.FromBase64String(stored.Raw.ContentBase64!)));
        Assert.Equal("con raw", reparsed.Subject);
        Assert.Contains("Subject: con raw", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Headers_are_preserved_including_custom_ones()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "a@example.com"));
        message.Subject = "con headers";
        message.Headers.Add("X-Custom", "valor-personalizado");
        message.Body = new TextPart("plain") { Text = "cuerpo" };

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await OnlyStoredAsync(fixture);

        Assert.Contains(stored.Headers, header => header.Name == "X-Custom" && header.Value == "valor-personalizado");
    }

    private static async Task<ReceivedMessage> OnlyStoredAsync(SmtpMockupFixture fixture)
    {
        var summaries = await fixture.Store.QueryAsync(new MailQuery());
        var summary = Assert.Single(summaries);

        return await fixture.Store.GetAsync(summary.Id)
            ?? throw new InvalidOperationException($"The message '{summary.Id}' could not be read back.");
    }

    [Fact]
    public async Task A_zero_inline_attachment_limit_means_no_limit_and_keeps_the_content()
    {
        // MaxInlineAttachmentBytes=0 se documenta como "sin tope", igual que MaxRawMimeBytes.
        // Antes, `length > 0` era cierto para todo adjunto no vacío y el 0 descartaba el
        // contenido de TODOS: poner el límite a cero para desactivarlo los eliminaba.
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Storage.MaxInlineAttachmentBytes = 0);

        var bytes = new byte[4096];

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "a@example.com"));
        message.Subject = "Sin tope";

        var builder = new BodyBuilder { TextBody = "Hola" };
        builder.Attachments.Add("datos.bin", bytes);
        message.Body = builder.ToMessageBody();

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await OnlyStoredAsync(fixture);
        var attachment = Assert.Single(stored.Attachments);

        Assert.False(attachment.Omitted);
        Assert.NotNull(attachment.ContentBase64);
        Assert.Equal(bytes.Length, attachment.SizeBytes);
    }

    [Fact]
    public async Task A_positive_inline_attachment_limit_still_omits_big_attachments()
    {
        // El otro lado del mismo cambio: con un tope real, lo de más se sigue omitiendo.
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Storage.MaxInlineAttachmentBytes = 16);

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "a@example.com"));
        message.Subject = "Con tope";

        var builder = new BodyBuilder { TextBody = "Hola" };
        builder.Attachments.Add("grande.bin", new byte[1024]);
        message.Body = builder.ToMessageBody();

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await OnlyStoredAsync(fixture);
        var attachment = Assert.Single(stored.Attachments);

        Assert.True(attachment.Omitted);
        Assert.Null(attachment.ContentBase64);

        // El tamaño y el checksum siguen ahí: omitido significa «no guardado en el JSON»,
        // no «desconocido».
        Assert.Equal(1024, attachment.SizeBytes);
        Assert.False(string.IsNullOrEmpty(attachment.ChecksumSha256));
    }
}
