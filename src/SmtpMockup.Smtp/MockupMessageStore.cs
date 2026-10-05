using System.Buffers;
using System.Net;
using Microsoft.Extensions.Logging;
using MimeKit;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;

// El IMessageStore del dominio y el de la librería coinciden en nombre; sealia el del
// dominio porque es el que aparece en las firmas de este adaptador.
using MockupStore = SmtpMockup.Core.Storage.IMessageStore;
using LibraryMessageStore = SmtpServer.Storage.IMessageStore;

namespace SmtpMockup.Smtp;

/// <summary>
/// Puente entre el pipeline de <c>SmtpServer</c> y <see cref="MockupStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// La librería entrega el MIME crudo del <c>DATA</c> más el sobre (MAIL FROM / RCPT TO),
/// y este adaptador lo convierte al modelo del SPEC §7 y lo persiste.
/// </para>
/// <para>
/// El punto clave del diseño: <see cref="SaveAsync"/> devuelve la respuesta SMTP final.
/// Ése es el <c>250</c> que ve el cliente, así que el mensaje queda en disco <em>antes</em>
/// de que el envío se dé por aceptado (DESIGN §3). Un error de disco devuelve <c>451</c>
/// en vez de perder al cliente (SPEC §10.2).
/// </para>
/// </remarks>
internal sealed class MockupMessageStore(
    MockupStore store,
    StorageLimits limits,
    SmtpSecurityMode security,
    ILogger logger) : MessageStore
{
    /// <summary>
    /// Respuesta <c>451</c> (error temporal) para fallos de disco: el cliente puede
    /// reintentar más tarde en vez de perder el mensaje (SPEC §10.1).
    /// </summary>
    private static readonly SmtpResponse TemporaryFailure =
        new(SmtpReplyCode.Aborted, "4.3.0 Temporary storage failure; please retry later.");

    /// <summary>Respuesta <c>550</c> para MIME que no se puede parsear (SPEC §10.1).</summary>
    private static readonly SmtpResponse UnparseableMessage =
        new(SmtpReplyCode.MailboxUnavailable, "5.6.0 Message content could not be parsed.");

    /// <summary>
    /// Clave con la que la librería publica el extremo remoto del cliente dentro de
    /// <c>ISessionContext.Properties</c>.
    /// </summary>
    private const string RemoteEndPointKey = "EndpointListener:RemoteEndPoint";

    /// <summary>Valor de <c>remoteIp</c> cuando la sesión no expone el extremo remoto.</summary>
    private const string UnknownAddress = "unknown";
    /// <inheritdoc />
    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transaction);

        byte[] raw;
        try
        {
            raw = ToArray(buffer);
        }
        catch (Exception exception) when (exception is IOException or OutOfMemoryException)
        {
            logger.LogWarning(exception, "Could not buffer the incoming message; replying 451");
            return TemporaryFailure;
        }

        var receivedAtUtc = DateTimeOffset.UtcNow;
        var id = MessageId.New(receivedAtUtc.UtcDateTime).Value;

        try
        {
            var message = await MimeMessage.LoadAsync(new MemoryStream(raw), cancellationToken)
                .ConfigureAwait(false);

            var envelope = BuildEnvelope(context, transaction, security);

            var received = ReceivedMessageFactory.Create(
                message,
                envelope,
                raw,
                limits,
                id,
                receivedAtUtc);

            await store.SaveAsync(received, cancellationToken).ConfigureAwait(false);

            logger.LogDebug(
                "SMTP message accepted id={MessageId} remote={RemoteIp} sizeBytes={SizeBytes}",
                received.Id,
                envelope.RemoteIp,
                received.Size.TotalBytes);

            // El id viaja en el 250 para que el cliente pueda correlacionar el envío
            // con el archivo stored (ENHANCEDSTATUSCODES).
            return SmtpResponse.Ok;
        }
        catch (MimeKit.ParseException exception)
        {
            // MIME inválido: 550 y no se persiste (SPEC §10.1).
            logger.LogWarning(
                exception,
                "Rejecting an unparseable message with 550 id={MessageId} sizeBytes={SizeBytes}",
                id,
                raw.Length);

            return UnparseableMessage;
        }
        catch (MessageStoreException exception)
        {
            // Disco lleno o sin permisos: 451 para que el cliente reintente después
            // en vez de perder el mensaje (SPEC §10.1).
            logger.LogError(exception, "Storage failed; replying 451 id={MessageId}", id);
            return TemporaryFailure;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Cualquier otro fallo no debe tumbar el listener (RNF-04).
            logger.LogError(exception, "Unexpected failure while receiving id={MessageId}; replying 451", id);
            return TemporaryFailure;
        }
    }

    /// <summary>
    /// Traduce el sobre de la librería al <see cref="EnvelopeInfo"/> del SPEC. El Bcc
    /// se deriva de los <c>RCPT TO</c> reales, no de la cabecera <c>Bcc</c>.
    /// </summary>
    private static EnvelopeInfo BuildEnvelope(
        ISessionContext context,
        IMessageTransaction transaction,
        SmtpSecurityMode security)
    {
        // La IP/puerto del cliente NO están en EndpointDefinition: ése es el socket de
        // escucha. La librería los publica en Properties con estas claves.
        var remote = context.Properties.TryGetValue(RemoteEndPointKey, out var value)
            ? value as IPEndPoint
            : null;

        var recipients = new List<string>(transaction.To.Count);
        foreach (var mailbox in transaction.To)
        {
            // IMailbox expone User y Host por separado; AsAddress() los recombina.
            var address = mailbox.AsAddress();
            if (!string.IsNullOrWhiteSpace(address))
            {
                recipients.Add(address.ToLowerInvariant());
            }
        }

        return new EnvelopeInfo(
            Helo: null,
            RemoteIp: remote?.Address.ToString() ?? UnknownAddress,
            RemotePort: remote?.Port ?? 0,
            MailFrom: transaction.From?.AsAddress().ToLowerInvariant(),
            RecipientTo: recipients,
            Authenticated: false,
            Transport: TransportFor(security),
            // Con TLS implícito IsSecure es cierto desde el primer byte; con STARTTLS sólo
            // después de que el comando STARTTLS del cliente se haya completado. Se pregunta
            // al pipe en vez de asumirlo por el modo configurado (SPEC §7).
            TlsNegotiated: security != SmtpSecurityMode.None && context.Pipe.IsSecure);
    }

    /// <summary>Traduce el modo de cifrado configurado al transporte del modelo de dominio.</summary>
    private static MailTransport TransportFor(SmtpSecurityMode security) => security switch
    {
        SmtpSecurityMode.Implicit => MailTransport.ImplicitTls,
        SmtpSecurityMode.StartTls => MailTransport.StartTls,
        _ => MailTransport.Plain,
    };

    /// <summary>Materializa el <see cref="ReadOnlySequence{T}"/> del socket en un array.</summary>
    private static byte[] ToArray(ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsSingleSegment)
        {
            return buffer.FirstSpan.ToArray();
        }

        var result = new byte[buffer.Length];
        buffer.CopyTo(result);
        return result;
    }
}
