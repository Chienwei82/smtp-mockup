using System.Text;
using MailKit.Net.Smtp;
using MimeKit;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// Tests del límite de tamaño: un mensaje que lo supera debe recibir <c>552</c> y no
/// dejar ningún archivo en disco (SPEC §10.1 y §11.1, criterio 5).
/// </summary>
public sealed class MessageSizeLimitTests
{
    /// <summary>Tope configurado por el fixture (1 MB).</summary>
    private const int MaxMessageSizeMb = 1;

    [Fact]
    public async Task A_message_over_the_limit_is_rejected_with_552_and_nothing_is_stored()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Smtp.MaxMessageSizeMb = MaxMessageSizeMb);

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "a@example.com"));
        message.Subject = "demasiado grande";
        message.Body = new TextPart("plain") { Text = new string('x', 2 * 1024 * 1024) };

        // MailKit lanza SmtpCommandException con el código que devolvió el servidor.
        var exception = await Assert.ThrowsAsync<SmtpCommandException>(
            () => client.SendAsync(message));

        // 552 = "message size exceeds fixed maximum message size". El servidor lo
        // detecta en MAIL FROM gracias a la extensión SIZE (RFC 1870), así que
        // MailKit lo reporta como remitente no aceptado con ese status code.
        Assert.Equal(SmtpErrorCode.SenderNotAccepted, exception.ErrorCode);
        Assert.Equal(552, (int)exception.StatusCode);

        await client.DisconnectAsync(true);

        // Ningún archivo debe haber quedado escrito.
        Assert.Empty(fixture.StoredFiles());
        Assert.Equal(0, await fixture.Store.CountAsync());
    }

    [Fact]
    public async Task A_message_within_the_limit_is_accepted()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Smtp.MaxMessageSizeMb = MaxMessageSizeMb);

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "a@example.com"));
        message.Subject = "dentro del límite";
        message.Body = new TextPart("plain") { Text = new string('x', 64 * 1024) };

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var summaries = await fixture.Store.QueryAsync(new MailQuery());

        Assert.Single(summaries);
        Assert.Single(fixture.StoredFiles());
    }

    [Fact]
    public async Task A_large_attachment_over_the_limit_is_still_rejected()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Smtp.MaxMessageSizeMb = MaxMessageSizeMb);

        using var client = await fixture.ConnectAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "a@example.com"));

        var builder = new BodyBuilder { TextBody = "con adjunto grande" };
        builder.Attachments.Add("grande.bin", new MemoryStream(new byte[2 * 1024 * 1024]));
        message.Body = builder.ToMessageBody();

        var exception = await Assert.ThrowsAsync<SmtpCommandException>(
            () => client.SendAsync(message));

        Assert.Equal(552, (int)exception.StatusCode);
        Assert.Empty(fixture.StoredFiles());
    }
}
