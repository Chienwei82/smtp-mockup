using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SmtpMockup.Smtp;

/// <summary>
/// Genera y persiste el certificado autofirmado de desarrollo del mockup (RF-15).
/// </summary>
public static class SelfSignedCertificateFactory
{
    /// <summary>OID de la extensión que <c>dotnet dev-certs</c> añade a su certificado.</summary>
    private const string HttpsDevelopmentCertificateOid = "1.3.6.1.4.1.311.20.2.1";

    /// <summary>OID de <c>serverAuth</c> (Enhanced Key Usage).</summary>
    private static readonly Oid ServerAuthenticationOid = new("1.3.6.1.5.5.7.3.1");

    /// <summary>Validez por defecto del certificado generado: 365 días.</summary>
    public const int DefaultValidityDays = 365;

    /// <summary>
    /// Crea un certificado autofirmado con <c>CN=localhost</c> y SAN
    /// <c>localhost</c>/<c>127.0.0.1</c>/<c>::1</c>, que es lo que necesitan los clientes para
    /// validar el nombre cuando el certificado ya está en su almacén de confianza.
    /// </summary>
    /// <param name="password">Contraseña del PFX exportado; vacía significa sin contraseña.</param>
    /// <param name="validityDays">Días de validez desde ahora.</param>
    public static X509Certificate2 Create(string? password, int validityDays = DefaultValidityDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(validityDays, 1);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([ServerAuthenticationOid], critical: false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));

        var now = DateTimeOffset.UtcNow;

        // El certificado se serializa a PFX y se relee: CreateSelfSigned devuelve un objeto que
        // no siempre expone la clave privada de forma utilizable con el almacén de claves, y lo
        // que necesita el servidor es un X509Certificate2 importable y reexportable.
        // Los flags son PfxKeyStorage.Flags y no unos literales porque macOS no admite claves
        // efímeras (ver PfxKeyStorage).
        using var generated = request.CreateSelfSigned(now, now.AddDays(validityDays));

        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx, password ?? string.Empty),
            password,
            PfxKeyStorage.Flags);
    }

    /// <summary>
    /// Escribe el PFX junto al ejecutable, creando el directorio si hace falta. La escritura es
    /// atómica (temporal + <c>Move</c>) para que un arranque interrumpido no deje un PFX
    /// corrupto que el siguiente intento no pueda abrir.
    /// </summary>
    /// <param name="pfxPath">Ruta absoluta del PFX.</param>
    /// <param name="certificate">Certificado a persistir.</param>
    /// <param name="password">Contraseña del PFX.</param>
    public static void Persist(string pfxPath, X509Certificate2 certificate, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pfxPath);
        ArgumentNullException.ThrowIfNull(certificate);

        var directory = Path.GetDirectoryName(pfxPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = pfxPath + ".tmp";
        File.WriteAllBytes(temporaryPath, certificate.Export(X509ContentType.Pfx, password ?? string.Empty));
        File.Move(temporaryPath, pfxPath, overwrite: true);

        RestrictAccess(pfxPath);
    }

    /// <summary>
    /// Indica si un certificado del almacén es el de desarrollo de <c>dotnet dev-certs</c>, que
    /// es como se distingue de un certificado personal cualquiera con el mismo CN.
    /// </summary>
    /// <param name="certificate">Certificado a inspeccionar.</param>
    public static bool IsDevelopmentCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value == HttpsDevelopmentCertificateOid)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// En Unix el PFX contiene la clave privada en claro, así que se limita a 600 para que otros
    /// usuarios de la máquina no puedan leerla. En Windows el ACL del directorio ya lo restringe.
    /// </summary>
    private static void RestrictAccess(string pfxPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(pfxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}