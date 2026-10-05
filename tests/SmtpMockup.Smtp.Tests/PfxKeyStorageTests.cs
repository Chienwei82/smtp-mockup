using System.Security.Cryptography.X509Certificates;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// Tests de <see cref="PfxKeyStorage"/>: los flags con los que se carga cualquier PFX del proyecto.
/// </summary>
/// <remarks>
/// Estos tests existen por un bug que solo aparece en macOS, y que por eso no se vio nunca en la
/// máquina de desarrollo: <c>X509KeyStorageFlags.EphemeralKeySet</c> es rechazado por la plataforma
/// con <see cref="PlatformNotSupportedException"/>, lo que rompía 15 tests y el arranque real con
/// STARTTLS en macOS. El primero de los dos tests de abajo solo puede fallar en macOS, que es
/// justo donde importa; el segundo pasa en todas partes y avisa si alguien vuelve a cambiar el
/// criterio sin darse cuenta.
/// </remarks>
public sealed class PfxKeyStorageTests
{
    [Fact]
    public void Ephemeral_keys_are_not_requested_on_macos_because_the_platform_rejects_them()
    {
        if (!OperatingSystem.IsMacOS())
        {
            // En Windows y Linux sí se piden: es lo que hace la clave borrable al cerrar el proceso.
            Assert.True(PfxKeyStorage.Flags.HasFlag(X509KeyStorageFlags.EphemeralKeySet));
            return;
        }

        Assert.False(
            PfxKeyStorage.Flags.HasFlag(X509KeyStorageFlags.EphemeralKeySet),
            "macOS no implementa claves efímeras: pasarle el flag lanza PlatformNotSupportedException.");
    }

    [Fact]
    public void The_certificate_can_always_be_reexported_because_the_key_is_exportable()
    {
        // Sin Exportable el PFX no se puede volver a escribir ni recargar, y el arranque
        // contrario fallaría. Es el otro flag que los tres puntos de carga necesitan siempre.
        Assert.True(PfxKeyStorage.Flags.HasFlag(X509KeyStorageFlags.Exportable));
    }
}