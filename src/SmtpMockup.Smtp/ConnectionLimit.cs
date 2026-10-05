using Microsoft.Extensions.Logging;
using SmtpServer;

namespace SmtpMockup.Smtp;

/// <summary>
/// Límite de conexiones SMTP simultáneas (<c>Smtp:MaxConcurrentConnections</c>).
/// </summary>
/// <remarks>
/// <para>
/// SmtpServer 11.1.0 no trae ningún límite de concurrencia: <c>SmtpServerOptionsBuilder</c> sólo
/// ofrece <c>MaxAuthenticationAttempts</c>, <c>MaxMessageSize</c> y <c>MaxRetryCount</c>. Esta
/// clase lo construye encima de las dos piezas públicas que sí sirven.
/// </para>
/// <para>
/// <b>Contar</b> con los eventos de sesión: <c>SessionCreated</c> suma y los tres de fin restan.
/// <c>SessionCreated</c> se dispara <em>antes</em> del saludo, de modo que cuando la función de
/// saludo consulta el recuento, la sesión que acaba de conectar ya está dentro. Por eso la
/// comparación es <c>Count &gt; limit</c> y no <c>&gt;=</c>.
/// </para>
/// <para>
/// <b>Rechazar</b> devolviendo <c>421</c> desde <c>CustomSmtpGreeting</c>, que es lo que RFC 5321
/// reserva para «demasiadas conexiones». No se escribe en <c>Context.Pipe.Output</c> porque la
/// librería escribe su <c>220</c> antes de lanzar <c>SessionCreated</c>: esos bytes llegarían
/// después de que el cliente ya se conectara, y se quedaría esperando.
/// </para>
/// </remarks>
internal sealed class ConnectionLimit(int limit, ILogger logger)
{
    /// <summary>Respuesta de rechazo; 4.7.0 es «recursos del sistema insuficientes».</summary>
    internal const string RejectionReply =
        "421 4.7.0 Too many concurrent connections; closing transmission channel";

    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _active = [];

    /// <summary>Registra el saludo personalizado que decide si una sesión entra o se rechaza.</summary>
    /// <param name="builder">Constructor de opciones del servidor.</param>
    public void Configure(SmtpServerOptionsBuilder builder)
    {
        // Sólo se sustituye el saludo cuando la opción está activa: con el límite en 0 (el
        // default) la librería conserva su saludo original y no se toca nada.
        builder.CustomSmtpGreeting(_ => IsOverLimit() ? RejectionReply : DefaultGreeting());
    }

    /// <summary>Conecta el recuento a los eventos de sesión del servidor.</summary>
    /// <param name="server">Servidor al que suscribirse.</param>
    public void Attach(SmtpServer.SmtpServer server)
    {
        server.SessionCreated += (_, args) =>
        {
            int active;

            lock (_gate)
            {
                _active.Add(args.Context.SessionId);
                active = _active.Count;
            }

            if (active > limit)
            {
                logger.LogWarning(
                    "Refusing SMTP connection {SessionId}: the limit of {Limit} concurrent connection(s) is reached",
                    args.Context.SessionId,
                    limit);
            }
        };

        server.SessionCompleted += (_, args) => Forget(args.Context.SessionId);
        server.SessionCancelled += (_, args) => Forget(args.Context.SessionId);
        server.SessionFaulted += (_, args) => Forget(args.Context.SessionId);
    }

    /// <summary>Número de sesiones vivas. Expuesto para las pruebas.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _active.Count;
            }
        }
    }

    private void Forget(Guid sessionId)
    {
        lock (_gate)
        {
            _active.Remove(sessionId);
        }
    }

    /// <summary>Si al consultar el recuento ya se está en el tope.</summary>
    private bool IsOverLimit()
    {
        lock (_gate)
        {
            return _active.Count > limit;
        }
    }

    /// <summary>
    /// Reproduce el saludo por defecto de la librería (<c>220 smtp-mockup v&lt;versión&gt; ESMTP
    /// ready</c>) para las sesiones aceptadas.
    /// </summary>
    /// <remarks>
    /// Al sustituir <c>CustomSmtpGreeting</c> se pierde el saludo propio de la librería, así que
    /// hay que reponerlo o las sesiones aceptadas se presentarían con otro banner. La versión es
    /// la del ensamblado de SmtpServer, que es de donde procede el original.
    /// </remarks>
    private static string DefaultGreeting()
    {
        var version = typeof(SmtpServer.SmtpServer).Assembly.GetName().Version;
        var versionText = version is null ? string.Empty : $" v{version}";

        return $"220 smtp-mockup{versionText} ESMTP ready";
    }
}
