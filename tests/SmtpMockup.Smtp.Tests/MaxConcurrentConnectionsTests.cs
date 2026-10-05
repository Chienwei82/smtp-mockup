using MailKit.Net.Smtp;
using MailKit.Security;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// Límite de conexiones simultáneas (<c>Smtp:MaxConcurrentConnections</c>).
/// </summary>
/// <remarks>
/// SmtpServer 11.1.0 no expone ningún límite de concurrencia, así que el mockup lo construye
/// encima de los eventos de sesión. Estos tests comprueban el comportamiento observable —que el
/// cliente recibe un <c>421</c>, no que haya un contador por dentro— porque es lo que un
/// desarrollador que topa con el tope ve desde su aplicación.
/// </remarks>
public sealed class MaxConcurrentConnectionsTests
{
    [Fact]
    public async Task A_connection_over_the_limit_is_refused_with_421()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Smtp.MaxConcurrentConnections = 1);

        // La primera sesión ocupa el único hueco permitido.
        using var accepted = await fixture.ConnectAsync(SecureSocketOptions.None);
        Assert.True(accepted.IsConnected);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.ConnectAsync(SecureSocketOptions.None));

        AssertMentions421(exception);
    }

    [Fact]
    public async Task Closing_a_connection_frees_its_slot()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Smtp.MaxConcurrentConnections = 1);

        var first = await fixture.ConnectAsync(SecureSocketOptions.None);
        await first.DisconnectAsync(true);
        first.Dispose();

        // Al cerrar la primera sesión, el hueco queda libre. Si el recuento no se liberara, el
        // mockup acabaría rechazando conexiones para siempre.
        //
        // La liberación NO es síncrona con el cierre del socket: 'DisconnectAsync' espera al
        // QUIT del cliente, y el evento SessionCompleted que descuenta la sesión lo dispara
        // el servidor después, en su propia tarea. Conectar de inmediato compite con ese
        // evento y el 421 es correcto en ese instante, así que el test sería flaky: en
        // isolation pasa siempre y con la suite entera en paralelo falla.
        // Se reintenta con un timeout en vez de con Thread.Sleep, como manda la convención.
        var deadline = TimeSpan.FromSeconds(10);
        var started = DateTimeOffset.UtcNow;
        Exception? last = null;

        while (DateTimeOffset.UtcNow - started < deadline)
        {
            try
            {
                using var second = await fixture.ConnectAsync(SecureSocketOptions.None);
                Assert.True(second.IsConnected);
                return;
            }
            catch (SmtpCommandException exception) when (
                exception.StatusCode == SmtpStatusCode.ServiceNotAvailable)
            {
                last = exception;
                await Task.Delay(50);
            }
        }

        Assert.Fail(
            "The slot was never released: the connection kept getting 421 "
            + $"{deadline.TotalSeconds:N0}s after the first client disconnected. "
            + $"Last error: {last?.Message}");
    }

    [Fact]
    public async Task Zero_means_no_limit()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Smtp.MaxConcurrentConnections = 0);

        // El default: un mockup que rechaza una ráfaga de envíos estorbaría sin motivo.
        var clients = new List<SmtpClient>();

        for (var i = 0; i < 5; i++)
        {
            var client = await fixture.ConnectAsync(SecureSocketOptions.None);
            Assert.True(client.IsConnected);
            clients.Add(client);
        }

        foreach (var client in clients)
        {
            await client.DisconnectAsync(true);
            client.Dispose();
        }
    }

    [Fact]
    public async Task Mail_still_flows_with_a_limit_in_place()
    {
        await using var fixture = await SmtpMockupFixture.StartAsync(
            options => options.Smtp.MaxConcurrentConnections = 2);

        using var client = await fixture.ConnectAsync(SecureSocketOptions.None);
        var message = new MimeKit.MimeMessage();
        message.From.Add(new MimeKit.MailboxAddress("sender@example.com", "sender@example.com"));
        message.To.Add(new MimeKit.MailboxAddress("a@example.com", "a@example.com"));
        message.Subject = "Con limite";
        message.Body = new MimeKit.TextPart("plain") { Text = "Hola" };

        await client.SendAsync(message);

        Assert.NotEmpty(fixture.StoredFiles());
    }

    /// <summary>
    /// El cliente ve un rechazo de bienvenida, no un error de red, así que se comprueba el
    /// código de estado y no el texto.
    /// </summary>
    /// <remarks>
    /// Se mira <see cref="SmtpCommandException.StatusCode"/> y no
    /// <c>ErrorCode</c>: <c>SmtpErrorCode</c> sólo tiene cuatro valores
    /// (<c>MessageNotAccepted</c>, <c>SenderNotAccepted</c>,
    /// <c>RecipientNotAccepted</c> y <c>UnexpectedStatusCode</c>) y un 421 cae en el
    /// último. El número de verdad está en <c>StatusCode</c>, que es un
    /// <c>SmtpStatusCode</c>. La versión anterior buscaba la cadena "421" en el mensaje y
    /// falla siempre: MailKit separa el código del texto, así que el mensaje del mockup
    /// llega como "4.7.0 Too many concurrent connections…".
    /// </remarks>
    private static void AssertMentions421(Exception exception)
    {
        var command = Assert.IsType<SmtpCommandException>(exception);
        Assert.True(
            command.StatusCode == SmtpStatusCode.ServiceNotAvailable,
            $"Expected a 421 rejection, but got {command.StatusCode} ({command.StatusCode}) "
            + $"with the message: {command.Message}");
    }
}
