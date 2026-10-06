using System.Security.Cryptography.X509Certificates;

namespace SmtpMockup.Smtp;

/// <summary>
/// Flags de almacenamiento de clave al (re)cargar un PFX (SPEC §9.3).
/// </summary>
/// <remarks>
/// En Linux se pide <see cref="X509KeyStorageFlags.EphemeralKeySet"/> para que la clave privada
/// viva solo en memoria. <b>En Windows no se pide, porque no funciona</b>: Schannel no sabe usar
/// una clave efímera en el handshake. El servidor cierra la conexión y el cliente ve
/// <c>SslHandshakeException</c> con un <c>IOException: unexpected EOF</c> dentro — o sea, el error
/// aparece en el <i>cliente</i> aunque la causa sea la clave del <i>servidor</i>, y el mensaje de
/// MailKit habla de confianza del certificado, que aquí no es el problema. Es un fallo conocido del
/// runtime (dotnet/runtime#103101, cerrado como resuelto en 9.0 pero con el workaround — reexportar
/// el PFX — como única salida; dotnet/runtime#114640 lo vuelve a pedir como funcionalidad
/// pendiente). No hay arreglo de código: solo dejar de pedir la clave efímera.
/// <para>
/// La contrapartida es que, en Windows, la clave se materializa en un archivo temporal que el
/// sistema limpia al cerrar el proceso. Para un mockup de desarrollo con un único PFX es
/// aceptable, y es el precio de que STARTTLS funcione.
/// </para>
/// </remarks>
internal static class PfxKeyStorage
{
    /// <summary>Flags con los que cargar cualquier PFX del proyecto.</summary>
    internal static X509KeyStorageFlags Flags { get; } =
        X509KeyStorageFlags.Exportable
        | (SupportsEphemeralKeys ? X509KeyStorageFlags.EphemeralKeySet : default);

    /// <summary>
    /// Si la plataforma admite claves efímeras y, sobre todo, si el TLS de esa plataforma las usa
    /// en el handshake. Solo Linux cumple las dos cosas.
    /// </summary>
    internal static bool SupportsEphemeralKeys => OperatingSystem.IsLinux();
}