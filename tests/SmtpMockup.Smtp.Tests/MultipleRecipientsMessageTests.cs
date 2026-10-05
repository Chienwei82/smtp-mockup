using MailKit.Net.Smtp;
using MimeKit;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// Tests de integración con varios destinatarios y con Bcc (SPEC §7 y §11.1).
/// </summary>
public sealed class MultipleRecipientsMessageTests
{
    [Fact]
    public async Task Several_recipients_are_all_recorded_in_the_envelope()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "a@example.com"));
        message.To.Add(new MailboxAddress(null, "b@example.com"));
        message.Cc.Add(new MailboxAddress(null, "c@example.com"));
        message.Bcc.Add(new MailboxAddress(null, "oculto@example.com"));
        message.Body = new TextPart("plain") { Text = "para varios" };

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await OnlyStoredAsync(fixture);

        // Los RCPT TO reales incluyen To, Cc y Bcc: el sobre registra a quién se
        // entregó el mensaje, no sólo lo que el cliente dejó en las cabeceras.
        Assert.Equal(
            ["a@example.com", "b@example.com", "c@example.com", "oculto@example.com"],
            stored.Envelope.RecipientTo.Order(StringComparer.Ordinal));

        Assert.Equal(["a@example.com", "b@example.com"], stored.To.Select(a => a.Address).Order(StringComparer.Ordinal));
        Assert.Equal("c@example.com", Assert.Single(stored.Cc).Address);
    }

    [Fact]
    public async Task A_bcc_recipient_is_absent_from_the_headers_and_appears_as_bcc()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "visible@example.com"));
        message.Bcc.Add(new MailboxAddress("Oculto", "oculto@example.com"));
        message.Body = new TextPart("plain") { Text = "prueba de bcc" };

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await OnlyStoredAsync(fixture);

        // El cliente borra la cabecera Bcc, así que el Bcc sólo se puede reconstruir
        // comparando los RCPT TO reales contra las cabeceras visibles (SPEC §7).
        Assert.DoesNotContain(stored.Headers, header => header.Name.Equals("Bcc", StringComparison.OrdinalIgnoreCase));

        var bcc = Assert.Single(stored.Bcc);
        Assert.Equal("oculto@example.com", bcc.Address);

        // Y el destinatario oculto no debe aparecer en To.
        Assert.Equal(["visible@example.com"], stored.To.Select(a => a.Address));
    }

    private static async Task<Core.Models.ReceivedMessage> OnlyStoredAsync(SmtpMockupFixture fixture)
    {
        var summaries = await fixture.Store.QueryAsync(new MailQuery());
        var summary = Assert.Single(summaries);

        return await fixture.Store.GetAsync(summary.Id)
            ?? throw new InvalidOperationException($"The message '{summary.Id}' could not be read back.");
    }
}
