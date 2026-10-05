using System.Security.Cryptography.X509Certificates;

namespace SmtpMockup.Smtp;

/// <summary>
/// Flags de almacenamiento de clave al (re)cargar un PFX (SPEC §9.3).
/// </summary>
/// <remarks>
/// En Windows y Linux se pide <see cref="X509KeyStorageFlags.EphemeralKeySet"/> para que la clave
/// privada viva solo en memoria. <b>macOS no lo soporta</b>: Apple no implementa claves efímeras
/// y la plataforma lanza <see cref="PlatformNotSupportedException"/> en cuanto se le pasa el flag
/// (lo dice el propio runtime: <i>"This platform does not support loading with
/// EphemeralKeySet. Remove the flag to allow keys to be temporarily created on disk."</i>).
/// Eso rompía los 15 tests de SMTP y de host en macOS, y más importante: rompía el arranque real
/// con STARTTLS en macOS, que es una de las plataformas que el proyecto publica.
/// <para>
/// La contrapartida de no poder usar claves efímeras es que la clave se materializa en un archivo
/// temporal que el sistema limpia al cerrar el proceso. Es exactamente lo que la plataforma
/// ofrece, y para un mockup de desarrollo con PFX de una sola clave es aceptable.
/// </para>
/// </remarks>
internal static class PfxKeyStorage
{
    /// <summary>Flags con los que cargar cualquier PFX del proyecto.</summary>
    internal static X509KeyStorageFlags Flags { get; } =
        X509KeyStorageFlags.Exportable
        | (OperatingSystem.IsMacOS() ? default : X509KeyStorageFlags.EphemeralKeySet);
}