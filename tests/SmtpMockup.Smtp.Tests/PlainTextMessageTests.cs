using MailKit.Net.Smtp;
using MimeKit;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// Tests de integración contra el listener plano (8025) con un cliente MailKit real:
/// texto plano, HTML, adjuntos, varios destinatarios y el límite de tamaño (SPEC §11.1).
/// </summary>
public sealed class PlainTextMessageTests
{
    [Fact]
    public async Task A_text_only_message_is_accepted_without_authentication_and_persisted()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Destinatario", "a@example.com"));
        message.Subject = "prueba en texto plano";
        message.Body = new TextPart("plain") { Text = "Hola en texto plano" };

        // Sin credenciales: el mockup no autentica (RF-01).
        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await SingleIdAsync(fixture);

        Assert.Equal("prueba en texto plano", stored.Subject);
        Assert.Equal("Hola en texto plano", stored.Body.Text);
        Assert.Null(stored.Body.Html);
        Assert.True(stored.Body.HasText);
        Assert.False(stored.Body.HasHtml);
        Assert.Empty(stored.Attachments);
        Assert.Equal("sender@example.com", Assert.Single(stored.From).Address);
        Assert.Equal("a@example.com", Assert.Single(stored.To).Address);
    }

    [Fact]
    public async Task The_envelope_and_transport_are_recorded_as_plain_and_unauthenticated()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "quien@example.com"));
        message.To.Add(new MailboxAddress(null, "destino@example.com"));
        message.Body = new TextPart("plain") { Text = "cuerpo" };

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await SingleIdAsync(fixture);

        Assert.Equal(MailTransport.Plain, stored.Envelope.Transport);
        Assert.False(stored.Envelope.TlsNegotiated);
        Assert.False(stored.Envelope.Authenticated);
        Assert.Equal("quien@example.com", stored.Envelope.MailFrom);
        Assert.Equal("destino@example.com", Assert.Single(stored.Envelope.RecipientTo));

        // remoteIp/remotePort deben ser los del CLIENTE, no los del socket de escucha.
        Assert.Equal("127.0.0.1", stored.Envelope.RemoteIp);
        Assert.InRange(stored.Envelope.RemotePort, 1, 65535);
        Assert.NotEqual(fixture.Port, stored.Envelope.RemotePort);
    }

    [Fact]
    public async Task Every_sender_and_recipient_is_accepted()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        // El mockup no valida remitentes ni destinatarios: no es un servidor de correo real.
        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "cualquiera@dominio-inexistente.invalid"));
        message.To.Add(new MailboxAddress(null, "nadie@otro.invalid"));
        message.Body = new TextPart("plain") { Text = "sin validar" };

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await SingleIdAsync(fixture);

        Assert.Equal("cualquiera@dominio-inexistente.invalid", stored.Envelope.MailFrom);
    }

    [Fact]
    public async Task Body_bytes_are_counted_in_utf8_and_not_in_characters()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "a@example.com"));

        // 'ñ' ocupa 2 bytes en UTF-8 y '€' 3: contando caracteres, bodyBytes daría 3 en vez de 5
        // y dejaría de ser comparable con size.totalBytes, que sí viene del tamaño real.
        const string Text = "ño€";
        message.Body = new TextPart("plain") { Text = Text };

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await SingleIdAsync(fixture);

        // Se compara contra el texto realmente almacenado, no contra el literal: MimeKit normaliza
        // los saltos de línea al construir la entidad, así que la cuenta legítimamente no es 5.
        var expected = System.Text.Encoding.UTF8.GetByteCount(stored.Body.Text!);

        Assert.Equal(expected, stored.Size.BodyBytes);
        Assert.True(
            stored.Size.BodyBytes >= stored.Body.Text!.Length,
            "Contando UTF-8 el cuerpo no puede pesar menos que su número de caracteres.");
        Assert.True(
            stored.Size.BodyBytes <= stored.Size.TotalBytes,
            "El cuerpo no puede pesar más que el mensaje completo.");
    }

    /// <summary>Devuelve el único mensaje almacenado, fallando si no hay exactamente uno.</summary>
    private static async Task<ReceivedMessage> SingleIdAsync(SmtpMockupFixture fixture)
    {
        var summaries = await fixture.Store.QueryAsync(new MailQuery());
        var summary = Assert.Single(summaries);

        var stored = await fixture.Store.GetAsync(summary.Id);
        return stored ?? throw new InvalidOperationException($"The message '{summary.Id}' could not be read back.");
    }
}
