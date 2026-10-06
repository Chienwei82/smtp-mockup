using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Hosting;
using SmtpMockup.Core.Options;

namespace SmtpMockup.Smtp;

/// <summary>
/// Provee el certificado TLS del listener cifrado (8443) a partir de <c>Certificate:*</c>.
/// </summary>
public interface ICertificateProvider
{
    /// <summary>
    /// Devuelve el certificado de servidor, generándolo y persistiéndolo si corresponde
    /// (SPEC §6.2).
    /// </summary>
    X509Certificate2 Resolve();
}

/// <summary>
/// Provee el certificado en modo <c>File</c> (carga estricta del PFX de <c>Certificate:Path</c>)
/// o <c>Auto</c>, donde el orden es <c>dev-certs</c> → PFX persistido → generación autofirmada
/// persistida.
/// </summary>
/// <remarks>
/// <para>
/// El resultado se cachea: el listener se construye una vez y todas las sesiones comparten el
/// mismo <see cref="X509Certificate2"/> (y por tanto el mismo thumbprint), que es lo que hace
/// posible que el cliente cachee la excepción de confianza.
/// </para>
/// <para>
/// El certificado sólo se resuelve si algún listener habilitado pide TLS: con los dos puertos
/// en modo <c>None</c> el host arranca sin tocar el almacén de certificados del usuario.
/// </para>
/// <para>
/// <b>Limitación conocida (Windows Service con cuenta LocalSystem).</b> El certificado de
/// desarrollo de <c>dotnet dev-certs https</c> vive en el almacén <c>CurrentUser\My</c> del
/// usuario interactivo. Un servicio que corre como LocalSystem no ve ese almacén (el suyo es el
/// de <c>SYSTEM</c>), así que cae al certificado autogenerado y persistido. No es un error: sólo
/// hay que confiar <em>ese</em> certificado, no el del usuario que lo instaló. Para evitar la
/// diferencia entre ejecutar el binario a mano y como servicio, lo habitual es apuntar
/// <c>Certificate:Mode</c> a <c>File</c> con un PFX explícito.
/// </para>
/// </remarks>
public sealed class CertificateProvider(
    IOptions<SmtpMockupOptions> options,
    ILogger<CertificateProvider> logger) : ICertificateProvider
{
    private readonly Lock _gate = new();
    private ResolvedCertificate? _cached;

    /// <inheritdoc />
    public X509Certificate2 Resolve()
    {
        lock (_gate)
        {
            return (_cached ??= ResolveCore()).Certificate;
        }
    }

    private ResolvedCertificate ResolveCore()
    {
        var settings = options.Value.Certificate;
        var mode = settings.ParsedMode;
        var pfxPath = ResolvePfxPath(settings.Path);

        var resolved = mode == CertificateMode.File
            ? new ResolvedCertificate(LoadStrictly(pfxPath, settings.Password), CertificateSource.File)
            : ResolveAutomatically(settings, pfxPath);

        logger.LogInformation(
            "CertificateResolved mode={Mode} source={Source} subject={Subject} thumbprint={Thumbprint} " +
            "notAfter={NotAfter:o}",
            mode,
            resolved.Source,
            resolved.Certificate.Subject,
            resolved.Certificate.Thumbprint,
            resolved.Certificate.NotAfter);

        return resolved;
    }


    /// <summary>
    /// Modo <c>Auto</c>: primero el certificado de desarrollo de .NET (que ya está en la
    /// confianza del equipo de desarrollo en Windows y en Linux con
    /// <c>dotnet dev-certs https --trust</c>), después el PFX propio ya persistido y, sólo si
    /// no hay nada, uno autofirmado nuevo.
    /// </summary>
    private ResolvedCertificate ResolveAutomatically(CertificateOptions settings, string pfxPath)
    {
        // El dev-certs sólo se consulta en modo Auto y si el usuario no lo desactivó: es un
        // certificado de otra herramienta y puede no ser el que el mockup quiere presentar.
        var development = settings.UseDevelopmentCertificate
            ? TryLoadDevelopmentCertificate()
            : null;

        if (development is not null)
        {
            return new ResolvedCertificate(development, CertificateSource.DevelopmentCertificate);
        }

        if (!string.IsNullOrWhiteSpace(settings.Path) && File.Exists(pfxPath))
        {
            var persisted = TryLoad(pfxPath, settings.Password);
            if (persisted is not null)
            {
                return new ResolvedCertificate(persisted, CertificateSource.PersistedFile);
            }

            // SPEC §6.2: un PFX con la contraseña equivocada no es fatal en modo Auto; se
            // regenera. Un warning basta para no ensuciar el primer arranque sin certificado.
            logger.LogWarning(
                "The persisted certificate at {Path} could not be opened with the configured password; " +
                "a new one will be generated.",
                pfxPath);
        }

        if (!settings.AutoGenerateSelfSigned)
        {
            throw new InvalidOperationException(
                "No TLS certificate is available for the encrypted SMTP listener and " +
                "'Certificate:AutoGenerateSelfSigned' is false. Either point 'Certificate:Path' at " +
                $"a readable PFX (current value: '{settings.Path}') or set " +
                "'Certificate:AutoGenerateSelfSigned' to true.");
        }

        var generated = SelfSignedCertificateFactory.Create(settings.Password);
        SelfSignedCertificateFactory.Persist(pfxPath, generated, settings.Password);
        logger.LogWarning(
            "Generated a self-signed certificate for {Subject} (thumbprint {Thumbprint}) and saved it to " +
            "{Path}. Clients only trust it once you trust it explicitly; see docs/certificate-trust.md.",
            generated.Subject,
            generated.Thumbprint,
            pfxPath);

        return new ResolvedCertificate(generated, CertificateSource.GeneratedSelfSigned);
    }

    /// <summary>Modo <c>File</c>: carga estricta; cualquier problema es un error fatal.</summary>
    private X509Certificate2 LoadStrictly(string pfxPath, string password)
    {
        if (string.IsNullOrWhiteSpace(pfxPath) || !File.Exists(pfxPath))
        {
            throw new InvalidOperationException(
                "'Certificate:Mode' is 'File' but the certificate file does not exist " +
                $"(resolved path: '{pfxPath}').");
        }

        try
        {
            return Load(pfxPath, password);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                $"'Certificate:Mode' is 'File' and the certificate at '{pfxPath}' could not be opened. " +
                "Check 'Certificate:Password'.",
                exception);
        }
    }

    private static X509Certificate2? TryLoad(string pfxPath, string password)
    {
        try
        {
            return Load(pfxPath, password);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static X509Certificate2 Load(string pfxPath, string password)
        => X509CertificateLoader.LoadPkcs12FromFile(
            pfxPath,
            password,
            PfxKeyStorage.Flags);

    /// <summary>
    /// Busca el certificado de desarrollo de <c>dotnet dev-certs https</c> en el almacén
    /// <c>CurrentUser\My</c> (donde esa herramienta lo guarda).
    /// </summary>
    /// <remarks>
    /// Se exige que el sujeto sea <c>CN=localhost</c> además del OID propio de dev-certs: en una
    /// máquina de desarrollo es habitual tener varios certificados en ese almacén, y presentar uno
    /// que no corresponda haría que el hostname del cliente no validara. Devuelve <see langword="null"/>
    /// —no lanza— si el almacén no existe o no es accesible, que es exactamente el caso de un
    /// Windows Service corriendo como LocalSystem (su <c>CurrentUser</c> es el de <c>SYSTEM</c>).
    /// </remarks>
    private X509Certificate2? TryLoadDevelopmentCertificate()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

            var now = DateTime.UtcNow;

            foreach (var certificate in store.Certificates)
            {
                if (!certificate.HasPrivateKey
                    || certificate.NotBefore.ToUniversalTime() > now
                    || certificate.NotAfter.ToUniversalTime() < now
                    || !SelfSignedCertificateFactory.IsDevelopmentCertificate(certificate))
                {
                    continue;
                }

                if (!HasLocalhostSubject(certificate))
                {
                    // Hay un dev-certs, pero no es el de localhost: sirve de poco para 8443.
                    logger.LogDebug(
                        "Ignoring the development certificate {Thumbprint} because its subject is {Subject}",
                        certificate.Thumbprint,
                        certificate.Subject);
                    continue;
                }

                // Se reexporta a PFX para devolver un certificado con clave privada usable
                // (efímera donde la plataforma lo permite: ver PfxKeyStorage). Así el proceso
                // no deja restos de la clave en el almacén al cerrar.
                return X509CertificateLoader.LoadPkcs12(
                    certificate.Export(X509ContentType.Pfx),
                    password: string.Empty,
                    keyStorageFlags: PfxKeyStorage.Flags);
            }
        }
        catch (CryptographicException exception)
        {
            // LocalSystem bajo un Windows Service es el caso típico: el almacén del usuario
            // interactivo no existe para esa cuenta y se sigue con el certificado generado.
            logger.LogDebug(exception, "Could not read the personal certificate store");
        }
        catch (PlatformNotSupportedException exception)
        {
            logger.LogDebug(exception, "The personal certificate store is not available on this platform");
        }

        return null;
    }

    /// <summary>Indica si el sujeto del certificado es <c>CN=localhost</c>.</summary>
    private static bool HasLocalhostSubject(X509Certificate2 certificate)
    {
        foreach (var subjectName in EnumerateSubjectNames(certificate.SubjectName))
        {
            var (name, value) = subjectName;

            if (name == "CN" && value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extrae los pares nombre/valor del sujeto. Se recorre la cadena en vez de usar
    /// <c>X500DistinguishedName.Decode</c> porque esa API no existe en todas las plataformas
    /// soportadas y el sujeto que produce <c>dotnet dev-certs</c> es siempre plano.
    /// </summary>
    private static IEnumerable<(string Name, string Value)> EnumerateSubjectNames(X500DistinguishedName subject)
    {
        foreach (var part in subject.Name.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                yield return (part[..separator].Trim(), part[(separator + 1)..].Trim());
            }
        }
    }

    /// <summary>
    /// Resuelve <see cref="CertificateOptions.Path"/> a una ruta absoluta. Las rutas relativas
    /// cuelgan del directorio del ejecutable, no del directorio de trabajo: el certificado
    /// acompaña al binario en un servicio de Windows o en un contenedor, donde el working
    /// directory es impredecible (SPEC §6.2).
    /// </summary>
    public static string ResolvePfxPath(string? path) => HostPath.Resolve(path);

    /// <summary>Origen del certificado resuelto; se registra en el log de arranque.</summary>
    private enum CertificateSource
    {
        File,
        PersistedFile,
        DevelopmentCertificate,
        GeneratedSelfSigned,
    }

    private sealed record ResolvedCertificate(X509Certificate2 Certificate, CertificateSource Source);
}
