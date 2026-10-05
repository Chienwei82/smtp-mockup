using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace SmtpMockup.Host.Tests;

/// <summary>
/// El Host arranca de verdad, con los puertos y direcciones que le pida la configuración.
/// </summary>
/// <remarks>
/// Es la única capa donde se ve el programa entero: el log de arranque, el <c>UseUrls</c> y los
/// listeners a la vez. Un bug que sólo aparece al ensamblar las tres piezas —por ejemplo, una
/// URL con corchetes mal puestos— no lo caza ningún test de los otros proyectos.
/// </remarks>
public sealed class HostStartupTests
{
    [Fact]
    public async Task The_host_starts_with_ephemeral_ports_on_all_three_listeners()
    {
        using var factory = new HostFactory().Start();

        Assert.True(factory.PlainPort > 0, "The plain listener did not get a real port.");
        Assert.True(factory.StartTlsPort > 0, "The STARTTLS listener did not get a real port.");

        // Cada listener tiene que tener el suyo: si ambos resolvieran el mismo, uno de los dos
        // estaría escuchando en un puerto que no es el suyo y el otro no estaría escuchando.
        Assert.NotEqual(factory.PlainPort, factory.StartTlsPort);

        // Con Web:Port=0 la URL configurada es inútil: el puerto existe sólo después de que el
        // servidor abra el socket, y de eso se trata (D-6). Aquí TestServer sustituye a Kestrel,
        // así que el puerto web real se verifica en el arranque del binario, no aquí.
        Assert.Equal(0, factory.Options.Web.Port);
    }

    [Fact]
    public async Task Mail_sent_to_the_plain_port_is_persisted_and_served_by_the_ui()
    {
        using var factory = new HostFactory().Start();
        using var client = new SmtpClient { Timeout = 30_000 };

        await client.ConnectAsync("127.0.0.1", factory.PlainPort, SecureSocketOptions.None);
        await client.SendAsync(Message("a@ejemplo.com"));
        await client.DisconnectAsync(true);

        using var http = factory.CreateClient();
        var body = await http.GetStringAsync("/");

        // La ruta completa: SMTP → disco → UI. Es el criterio de aceptación 1 de la SPEC, y
        // aquí se ejercita sobre el proceso real y no sobre componentes aislados.
        Assert.Contains("End-to-end", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_startup_log_reports_the_real_endpoints()
    {
        using var factory = new HostFactory().Start();

        var log = string.Join("\n", factory.StartupLog);

        Assert.Contains("SMTP listener started", log, StringComparison.Ordinal);

        // El log dice el puerto resuelto, no el configurado: con 0 sería inútil para conectarse.
        Assert.Contains($"127.0.0.1:{factory.PlainPort}", log, StringComparison.Ordinal);
        Assert.Contains($"127.0.0.1:{factory.StartTlsPort}", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loopback_defaults_produce_no_binding_warning()
    {
        using var factory = new HostFactory().Start();

        // El default es loopback, así que el aviso no debe aparecer: si aparece siempre, nadie
        // lo leería y perdería su valor cuando de verdad importara.
        Assert.DoesNotContain(
            factory.StartupLog,
            line => line.Contains("not a loopback address", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Smtp:Plain:BindAddress", "Smtp:Plain")]
    [InlineData("Smtp:StartTls:BindAddress", "Smtp:StartTls")]
    public async Task A_non_loopback_smtp_bind_address_is_allowed_but_warned(string key, string expectedKey)
    {
        using var factory = new HostFactory(new Dictionary<string, string?>
        {
            [key] = "0.0.0.0",
        }).Start();

        // No es un error: levantar el SMTP en una red es legítimo en CI y en contenedores. Pero
        // tiene que decirse, porque el default es loopback y quien lo cambia tiene que saber que
        // dejó de estar en su máquina.
        Assert.Contains(
            factory.StartupLog,
            line => line.Contains("not a loopback address", StringComparison.Ordinal)
                && line.Contains(expectedKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_non_loopback_web_bind_address_is_allowed_but_warned()
    {
        using var factory = new HostFactory(new Dictionary<string, string?>
        {
            ["Web:BindAddress"] = "0.0.0.0",
        }).Start();

        Assert.Contains(
            factory.StartupLog,
            line => line.Contains("not a loopback address", StringComparison.Ordinal)
                && line.Contains("Web", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabling_the_web_ui_leaves_the_smtp_listeners_running()
    {
        using var factory = new HostFactory(new Dictionary<string, string?>
        {
            ["Web:Enabled"] = "false",
        }).Start();

        using var client = new SmtpClient { Timeout = 30_000 };
        await client.ConnectAsync("127.0.0.1", factory.PlainPort, SecureSocketOptions.None);

        Assert.True(client.IsConnected);
        Assert.Contains(
            factory.StartupLog,
            line => line.Contains("Web UI disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabling_the_plain_listener_keeps_the_starttls_one_running()
    {
        using var factory = new HostFactory(new Dictionary<string, string?>
        {
            ["Smtp:Plain:Enabled"] = "false",
        }).Start();

        // RF-05: la habilitación es independiente. Con el plano apagado, un cliente a 0.0.0.0
        // recibe conexión rechazada mientras el STARTTLS sigue escuchando.
        using var secure = new SmtpClient
        {
            Timeout = 30_000,
            ServerCertificateValidationCallback = (_, _, _, _) => true,
        };

        await secure.ConnectAsync("127.0.0.1", factory.StartTlsPort, SecureSocketOptions.StartTls);
        Assert.True(secure.IsConnected);
        await secure.DisconnectAsync(true);
    }

    private static MimeMessage Message(string recipient)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("sender@example.com", "sender@example.com"));
        message.To.Add(new MailboxAddress(recipient, recipient));
        message.Subject = "End-to-end";
        message.Body = new TextPart("plain") { Text = "Hola" };
        return message;
    }
}
