using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;
using SmtpMockup.Smtp;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// El listener SMTP se ata a la dirección configurada y, por defecto, sólo a loopback (D-1).
/// </summary>
/// <remarks>
/// Estos tests son la razón de ser de <c>Smtp:*:BindAddress</c>: sin él, <c>Port(port)</c>
/// resuelve a <see cref="IPAddress.Any"/> y el mockup aceptaba correo de toda la red sin decirlo.
/// La prueba no es «la propiedad devuelve 127.0.0.1» sino que un socket a una dirección no
/// loopback <em>falla de verdad</em>.
/// </remarks>
public sealed class SmtpListenerBindAddressTests
{
    [Fact]
    public async Task Default_bind_address_does_not_accept_from_a_non_loopback_address()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        Assert.Equal(IPAddress.Loopback, fixture.BindAddress);

        var external = ExternalAddresses().FirstOrDefault();
        if (external is null)
        {
            // Máquina sin interfaz externa (contenedor con una sola loopback): no hay forma
            // de comprobar el negativo por red. Se comprueba lo comprobable y se dice por qué.
            Assert.Equal(IPAddress.Loopback, fixture.BindAddress);
            return;
        }

        // Si el listener se hubiera abierto en IPAddress.Any —que es lo que pasaba antes de
        // BindAddress—, esta conexión entraría y el test fallaría.
        Assert.False(await CanConnectAsync(external, fixture.Port));
    }

    [Fact]
    public async Task Default_bind_address_still_serves_loopback_clients()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        Assert.True(await CanConnectAsync(IPAddress.Loopback, fixture.Port));

        using var client = await fixture.ConnectAsync(SecureSocketOptions.None);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_configured_non_loopback_bind_address_is_honoured()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Smtp.Plain.BindAddress = "0.0.0.0");

        Assert.Equal(IPAddress.Any, fixture.BindAddress);

        // Con 0.0.0.0 el listener acepta por cualquier dirección de la máquina. Junto con el
        // test anterior, los dos juntos demuestran que la opción manda de verdad y que el
        // default no era accidental.
        Assert.True(await CanConnectAsync(IPAddress.Loopback, fixture.Port));

        var external = ExternalAddresses().FirstOrDefault();
        if (external is not null)
        {
            Assert.True(await CanConnectAsync(external, fixture.Port));
        }
    }

    [Fact]
    public async Task A_configured_any_bind_address_starts_and_serves_loopback()
    {
        // El sondeo de disponibilidad del arranque conectaba contra la propia dirección de bind.
        // Con 0.0.0.0 eso está mal: Any es una comodín de escucha, no un destino. En Linux
        // conectar a 0.0.0.0 funciona por casualidad (el kernel lo traduce a loopback) y por eso
        // el fallo solo aparecía en Windows, donde el connect se rechaza y el arranque expiraba
        // con un timeout que no señalaba la causa. Este test ata a Any y exige que arranque y
        // acepte: es el que falla si el sondeo vuelve a usar la dirección de bind.
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Smtp.Plain.BindAddress = "0.0.0.0");

        Assert.Equal(IPAddress.Any, fixture.BindAddress);

        // Any escucha en todas las direcciones, así que el loopback tiene que entrar.
        Assert.True(await CanConnectAsync(IPAddress.Loopback, fixture.Port));
    }

    [Fact]
    public async Task An_ipv6_loopback_bind_address_is_honoured()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Smtp.Plain.BindAddress = "::1");

        Assert.Equal(IPAddress.IPv6Loopback, fixture.BindAddress);

        // Que arranque ya es parte de la prueba: el probe de disponibilidad sondea la dirección
        // configurada y no IPAddress.Loopback fijo, así que si aquéllo siguiera siendo IPv4 el
        // arranque habría expirado con un timeout en lugar de llegar aquí.
        Assert.True(await CanConnectAsync(IPAddress.IPv6Loopback, fixture.Port));

        // ::1 no es dual-stack: el socket queda atado a IPv6 y el IPv4 loopback deja de
        // responder. Es el comportamiento correcto —aceptar IPv4 sin haberlo pedido también sería
        // una sorpresa— y por eso se afirma en vez de omitirse.
        Assert.False(await CanConnectAsync(IPAddress.Loopback, fixture.Port));
    }

    [Fact]
    public async Task A_loopback_listener_delivers_mail_end_to_end()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync();

        using var client = await fixture.ConnectAsync(SecureSocketOptions.None);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("sender@example.com", "sender@example.com"));
        message.To.Add(new MailboxAddress("to@example.com", "to@example.com"));
        message.Subject = "bind address";
        message.Body = new TextPart("plain") { Text = "Hola" };

        await client.SendAsync(message);

        Assert.NotEmpty(fixture.StoredFiles());
    }

    /// <summary>
    /// Direcciones IPv4 de la máquina que no son de loopback. Se usan para comprobar, por red y
    /// no por configuración, que un listener atado a loopback <em>no</em> es alcanzable desde
    /// fuera de la máquina.
    /// </summary>
    /// <remarks>
    /// Se descubren en vez de fijarse en una constante porque no hay una dirección «de prueba»
    /// universal: un contenedor puede no tener ninguna, y un test que dependa de una IP concreta
    /// del host falla en la máquina de al lado sin decir por qué.
    /// </remarks>
    private static IEnumerable<IPAddress> ExternalAddresses()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Where(address => !IPAddress.IsLoopback(address));

    /// <summary>
    /// Intenta abrir un socket TCP. Un bind a loopback deja los dirigidos a otra dirección
    /// sin respuesta, así que el resultado es <see langword="false"/> por timeout.
    /// </summary>
    private static async Task<bool> CanConnectAsync(IPAddress address, int port)
    {
        using var probe = new TcpClient();

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await probe.ConnectAsync(address, port, cancellation.Token);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}