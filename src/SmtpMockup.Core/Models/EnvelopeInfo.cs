namespace SmtpMockup.Core.Models;

/// <summary>
/// Transporte usado por la conexión que recibió el mensaje (SPEC §7 <c>envelope.transport</c>).
/// </summary>
public enum MailTransport
{
    /// <summary>Listener en texto plano (8025).</summary>
    Plain = 0,

    /// <summary>Listener que negocia TLS con el comando <c>STARTTLS</c>.</summary>
    StartTls = 1,

    /// <summary>Listener con TLS implícito (SMTPS, 8443 por defecto): el socket ya está cifrado.</summary>
    ImplicitTls = 2,
}

/// <summary>Una dirección de correo con nombre opcional (SPEC §7).</summary>
/// <param name="Name">Nombre legible; <see langword="null"/> si el cliente no lo envió.</param>
/// <param name="Address">La dirección, siempre en minúsculas.</param>
public sealed record MailAddressInfo(string? Name, string Address);

/// <summary>Un header del mensaje, en el orden original (SPEC §7 <c>headers</c>).</summary>
/// <param name="Name">Nombre del header.</param>
/// <param name="Value">Valor crudo, sin decodificar.</param>
public sealed record HeaderInfo(string Name, string Value);

/// <summary>
/// El sobre SMTP real: la única fuente fiable de Bcc, porque los clientes borran
/// la cabecera <c>Bcc</c> antes de enviar (SPEC §7).
/// </summary>
/// <param name="Helo">Dominio declarado en <c>EHLO</c>/<c>HELO</c>; <see langword="null"/> si no hubo.</param>
/// <param name="RemoteIp">IP del cliente.</param>
/// <param name="RemotePort">Puerto de origen del cliente.</param>
/// <param name="MailFrom">Remitente del sobre; <see langword="null"/> para el sobre nulo.</param>
/// <param name="RecipientTo">Destinatatios reales de <c>RCPT TO</c>, en orden.</param>
/// <param name="Authenticated">Siempre <see langword="false"/>: el mockup no autentica (RF-01).</param>
/// <param name="Transport">Transporte de la conexión.</param>
/// <param name="TlsNegotiated">Si se negoció TLS en esta conexión.</param>
public sealed record EnvelopeInfo(
    string? Helo,
    string RemoteIp,
    int RemotePort,
    string? MailFrom,
    IReadOnlyList<string> RecipientTo,
    bool Authenticated,
    MailTransport Transport,
    bool TlsNegotiated);

/// <summary>Cuerpos del mensaje en sus dos representaciones (SPEC §7 <c>body</c>).</summary>
/// <param name="Text">Primera parte <c>text/plain</c>; <see langword="null"/> si no hay.</param>
/// <param name="Html">Primera parte <c>text/html</c>; <see langword="null"/> si no hay.</param>
public sealed record BodyInfo(string? Text, string? Html)
{
    /// <summary>Indica si hay cuerpo de texto plano.</summary>
    public bool HasText => Text is not null;

    /// <summary>Indica si hay cuerpo HTML.</summary>
    public bool HasHtml => Html is not null;
}

/// <summary>Tamaños del mensaje (SPEC §7 <c>size</c>).</summary>
/// <param name="TotalBytes">Tamaño del MIME crudo tal como llegó por el socket.</param>
/// <param name="BodyBytes">Suma de los cuerpos decodificados.</param>
/// <param name="AttachmentCount">Cantidad de adjuntos.</param>
public sealed record SizeInfo(long TotalBytes, long BodyBytes, int AttachmentCount);

/// <summary>El MIME crudo original, para la descarga <c>.eml</c> (SPEC §7 <c>raw</c>).</summary>
/// <param name="SizeBytes">Tamaño del MIME crudo.</param>
/// <param name="Truncated">Si se recortó porque superó el máximo permitido.</param>
/// <param name="ContentBase64">MIME crudo en base64, o <see langword="null"/> si no se conserva.</param>
public sealed record RawMimeInfo(long SizeBytes, bool Truncated, string? ContentBase64);
