using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;
using SmtpMockup.Smtp;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// Tests de integración del listener de 8443 con un cliente MailKit real: el endpoint
/// **anuncia STARTTLS** (sin SSL implícito), el cliente actualiza y el mensaje se persiste con
/// el certificado configurado, que es el autofirmado generado y guardado junto al ejecutable
/// (SPEC §11.1, criterios 2 y 6).
/// </summary>
public sealed class StartTlsListenerTests
{
    [Fact]
    public async Task The_endpoint_advertises_starttls_and_does_not_require_it()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(kind: SmtpEndpointKind.StartTls);

        // SecureSocketOptions.None = no actualizar: el diálogo EHLO tiene que ofrecer STARTTLS
        // pero el mockup acepta el mensaje igual, porque la actualización es opcional (RF-03).
        using var client = await fixture.ConnectAsync(SecureSocketOptions.None);

        Assert.True(
            client.Capabilities.HasFlag(SmtpCapabilities.StartTLS),
            "The 8443 endpoint did not advertise STARTTLS in its EHLO response.");
        Assert.False(client.IsSecure);
    }

    [Fact]
    public async Task A_message_sent_after_upgrading_to_starttls_is_persisted_with_the_tls_transport()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(kind: SmtpEndpointKind.StartTls);

        // AutoStartTls = true: MailKit envía STARTTLS y valida el certificado autofirmado con
        // el callback, que es el equivalente en cliente de "confiar el certificado".
        using var client = await fixture.ConnectAsync(SecureSocketOptions.StartTls);
        Assert.True(client.IsSecure);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "destino@example.com"));
        message.Subject = "prueba sobre STARTTLS";
        message.Body = new TextPart("plain") { Text = "cifrado tras la actualización" };

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        var stored = await SingleIdAsync(fixture);

        Assert.Equal("prueba sobre STARTTLS", stored.Subject);
        Assert.Equal("cifrado tras la actualización", stored.Body.Text);
        Assert.Equal(MailTransport.StartTls, stored.Envelope.Transport);
        Assert.True(stored.Envelope.TlsNegotiated);
        Assert.False(stored.Envelope.Authenticated);
    }

    [Fact]
    public async Task The_certificate_used_for_the_upgrade_is_the_configured_one()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(kind: SmtpEndpointKind.StartTls);

        X509Certificate2? presented = null;
        using var client = new SmtpClient
        {
            Timeout = 30_000,
            // Se captura el certificado que presenta el servidor en vez de descartarlo: es lo
            // que hay que comprobar para verificar que 8443 usa el configurado.
            ServerCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is not null)
                {
                    var raw = certificate.Export(X509ContentType.Cert);
                    presented = X509CertificateLoader.LoadCertificate(raw);
                }

                return true;
            },
        };

        await client.ConnectAsync("127.0.0.1", fixture.Port, SecureSocketOptions.StartTls);

        var offered = Assert.IsType<X509Certificate2>(presented);
        Assert.Equal(fixture.RequireCertificate().Thumbprint, offered.Thumbprint);
        Assert.Equal("CN=localhost", offered.Subject);

        // El SAN incluye el loopback, así que un cliente que confíe el certificado valida
        // el nombre sin necesidad de desactivar la validación.
        Assert.Contains("localhost", offered.GetNameInfo(X509NameType.DnsName, forIssuer: false));

        await client.DisconnectAsync(true);
    }

    [Fact]
    public async Task A_client_that_does_not_upgrade_still_delivers_the_message_in_the_clear()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(kind: SmtpEndpointKind.StartTls);

        using var client = await fixture.ConnectAsync(SecureSocketOptions.None);

        await client.SendAsync(NewMessage("sin actualizar"));
        await client.DisconnectAsync(true);

        var stored = await SingleIdAsync(fixture);

        // El listener es un mockup: anuncia STARTTLS pero no obliga (RF-03).
        Assert.Equal(MailTransport.StartTls, stored.Envelope.Transport);
        Assert.False(stored.Envelope.TlsNegotiated);
    }

    [Fact]
    public async Task The_generated_self_signed_certificate_is_persisted_and_reused_on_the_next_start()
    {
        // El primer fixture se mantiene vivo mientras arranca el segundo: al liberarse borra
        // su directorio temporal, y con él el PFX que el segundo debe reutilizar.
        await using var first = await SmtpMockupFixture.StartAsync(kind: SmtpEndpointKind.StartTls);

        var pfxPath = Path.Combine(first.RequireCertificateDirectory(), "dev.pfx");
        var thumbprint = first.RequireCertificate().Thumbprint;

        Assert.True(File.Exists(pfxPath), $"The certificate was not persisted at {pfxPath}.");

        // AutoGenerateSelfSigned=false con un PFX existente obliga al provider a leerlo del
        // disco: si lo ignorara, no tendría de dónde sacar el certificado.
        await using var second = await SmtpMockupFixture.StartAsync(
            configure: options =>
            {
                options.Certificate.Path = pfxPath;
                options.Certificate.AutoGenerateSelfSigned = false;
            },
            kind: SmtpEndpointKind.StartTls);

        // Mismo thumbprint ⇒ el segundo arranque reutilizó el PFX, no generó otro.
        Assert.Equal(thumbprint, second.RequireCertificate().Thumbprint);
    }

    [Fact]
    public async Task A_missing_certificate_file_in_file_mode_fails_with_an_explicit_message()
    {
        var missing = Path.Combine(Path.GetTempPath(), "smtp-mockup-certs", "no-existe.pfx");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SmtpMockupFixture.StartAsync(
                configure: options =>
                {
                    options.Certificate.Mode = nameof(CertificateMode.File);
                    options.Certificate.Path = missing;
                },
                kind: SmtpEndpointKind.StartTls));

        Assert.Contains("Certificate:Mode", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task With_AutoGenerateSelfSigned_false_and_no_certificate_the_listener_does_not_start()
    {
        var missing = Path.Combine(Path.GetTempPath(), "smtp-mockup-certs", "ausente.pfx");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SmtpMockupFixture.StartAsync(
                configure: options =>
                {
                    options.Certificate.Path = missing;
                    options.Certificate.AutoGenerateSelfSigned = false;
                },
                kind: SmtpEndpointKind.StartTls));

        Assert.Contains("AutoGenerateSelfSigned", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_endpoint_can_still_be_switched_to_implicit_tls()
    {
        // El modo SMTPS queda disponible aunque no sea el default; el certificado es el mismo.
        await using var fixture = await SmtpMockupFixture.StartAsync(
            configure: options => options.Smtp.StartTls.Security = nameof(SmtpSecurityMode.Implicit),
            kind: SmtpEndpointKind.StartTls);

        using var client = await fixture.ConnectAsync(SecureSocketOptions.SslOnConnect);
        Assert.True(client.IsSecure);

        await client.SendAsync(NewMessage("sobre SMTPS"));
        await client.DisconnectAsync(true);

        var stored = await SingleIdAsync(fixture);
        Assert.Equal(MailTransport.ImplicitTls, stored.Envelope.Transport);
        Assert.True(stored.Envelope.TlsNegotiated);
    }

    private static MimeMessage NewMessage(string subject)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(null, "sender@example.com"));
        message.To.Add(new MailboxAddress(null, "destino@example.com"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "cuerpo" };
        return message;
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
