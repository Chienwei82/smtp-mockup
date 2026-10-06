using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// Tests de <see cref="PfxKeyStorage"/>: los flags con los que se carga cualquier PFX del proyecto.
/// </summary>
/// <remarks>
/// Estos tests existen por un bug que solo se ve en CI, nunca en la máquina de desarrollo (Linux):
/// pedir <c>EphemeralKeySet</c> en una plataforma donde no funciona.
/// <para>
/// En Windows, Schannel no usa claves efímeras en el handshake; el servidor corta la conexión y el
/// cliente ve <c>SslHandshakeException</c> con un <c>unexpected EOF</c> dentro, cuyo mensaje habla
/// de confianza del certificado y no señala la causa real.
/// </para>
/// La única plataforma que admite la clave efímera <i>y</i> la usa en el handshake es Linux.
/// </remarks>
public sealed class PfxKeyStorageTests
{
    [Fact]
    public void Only_linux_gets_ephemeral_keys()
    {
        // Es la decisión, en un solo sitio. Las dos líneas siguientes del fichero de producción la
        // leen, y este test es lo que obliga a que las tres cosas cambien a la vez.
        Assert.Equal(OperatingSystem.IsLinux(), PfxKeyStorage.SupportsEphemeralKeys);
    }

    [Fact]
    public void The_flags_agree_with_the_platform_support()
    {
        // Flags es lo que consume el código; SupportsEphemeralKeys es el criterio. Si alguien
        // tocara una y no la otra, el handshake se rompería en alguna plataforma.
        Assert.Equal(
            PfxKeyStorage.SupportsEphemeralKeys,
            PfxKeyStorage.Flags.HasFlag(X509KeyStorageFlags.EphemeralKeySet));
    }

    [Fact]
    public void The_certificate_can_always_be_reexported_because_the_key_is_exportable()
    {
        // Sin Exportable el PFX no se puede volver a escribir ni recargar, y el arranque
        // contrario fallaría. Es el otro flag que los tres puntos de carga necesitan siempre,
        // también en Windows.
        Assert.True(PfxKeyStorage.Flags.HasFlag(X509KeyStorageFlags.Exportable));
    }

    [Fact]
    public void Loading_and_exporting_a_certificate_works_on_this_platform()
    {
        // El aserto real: hacer el viaje completo del PFX con los flags que el código usa.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=smtp-mockup-test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        var pfx = created.Export(X509ContentType.Pfx, "clave");

        using var reloaded = X509CertificateLoader.LoadPkcs12(pfx, "clave", PfxKeyStorage.Flags);

        Assert.Equal("CN=smtp-mockup-test", reloaded.Subject);
        Assert.True(reloaded.HasPrivateKey);

        // Y se puede reexportar, que es lo que permite persistir el PFX en el arranque siguiente.
        Assert.NotEmpty(reloaded.Export(X509ContentType.Pfx, "clave"));
    }
}