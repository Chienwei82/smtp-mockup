using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;
using SmtpMockup.Smtp;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// Tests del <see cref="CertificateProvider"/>: qué certificado se elige, que el generado se
/// persiste junto al ejecutable y que los modos estrictos fallan con un mensaje utilizable
/// (SPEC §6.2, RF-15).
/// </summary>
public sealed class CertificateProviderTests : IDisposable
{
    private readonly string _directory;

    public CertificateProviderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "smtp-mockup-certs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Auto_mode_generates_a_self_signed_certificate_and_persists_it()
    {
        var pfxPath = Path.Combine(_directory, "dev.pfx");
        var resolver = CreateProvider(pfxPath);

        var certificate = resolver.Resolve();

        Assert.Equal("CN=localhost", certificate.Subject);
        Assert.True(File.Exists(pfxPath), "The generated certificate was not persisted.");
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("localhost", san.EnumerateDnsNames());
        Assert.Contains(IPAddress.Loopback, san.EnumerateIPAddresses());
        Assert.Contains(IPAddress.IPv6Loopback, san.EnumerateIPAddresses());

        // La clave privada tiene que servir para el handshake: un PFX sin clave no sirve de nada.
        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public void A_relative_path_is_resolved_next_to_the_executable_not_the_working_directory()
    {
        var resolved = CertificateProvider.ResolvePfxPath(Path.Combine("certs", "dev.pfx"));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "certs", "dev.pfx")),
            resolved);
    }

    [Fact]
    public void An_absolute_path_is_used_as_is()
    {
        var absolute = Path.Combine(_directory, "custom.pfx");

        Assert.Equal(Path.GetFullPath(absolute), CertificateProvider.ResolvePfxPath(absolute));
    }

    [Fact]
    public void The_second_provider_reuses_the_persisted_certificate_instead_of_generating_one()
    {
        var pfxPath = Path.Combine(_directory, "dev.pfx");
        var first = CreateProvider(pfxPath).Resolve();

        var second = CreateProvider(pfxPath).Resolve();

        Assert.Equal(first.Thumbprint, second.Thumbprint);
        Assert.Equal(first.SerialNumber, second.SerialNumber);
    }

    [Fact]
    public void Resolving_twice_returns_the_same_instance_so_every_session_shares_the_thumbprint()
    {
        var resolver = CreateProvider(Path.Combine(_directory, "dev.pfx"));

        Assert.Same(resolver.Resolve(), resolver.Resolve());
    }

    [Fact]
    public void A_persisted_certificate_is_reopened_with_the_configured_password()
    {
        var pfxPath = Path.Combine(_directory, "dev.pfx");
        var withPassword = CreateProvider(pfxPath, password: "s3cret").Resolve();

        var reopened = CreateProvider(pfxPath, password: "s3cret").Resolve();

        Assert.Equal(withPassword.Thumbprint, reopened.Thumbprint);
    }


    [Fact]
    public void In_file_mode_a_wrong_password_is_a_fatal_error_naming_the_path()
    {
        var pfxPath = Path.Combine(_directory, "dev.pfx");
        CreateProvider(pfxPath, password: "correcta").Resolve();

        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateProvider(pfxPath, password: "incorrecta", mode: CertificateMode.File).Resolve());

        Assert.Contains("Certificate:Password", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(pfxPath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void In_auto_mode_a_wrong_password_regenerates_the_certificate_with_a_warning()
    {
        var pfxPath = Path.Combine(_directory, "dev.pfx");
        var original = CreateProvider(pfxPath).Resolve();

        var regenerated = CreateProvider(pfxPath, password: "incorrecta").Resolve();

        Assert.NotEqual(original.Thumbprint, regenerated.Thumbprint);
        Assert.True(File.Exists(pfxPath));
    }

    [Fact]
    public void Without_a_certificate_and_without_auto_generation_the_provider_explains_the_options()
    {
        var missing = Path.Combine(_directory, "no-existe.pfx");
        var resolver = CreateProvider(missing, autoGenerateSelfSigned: false);

        var exception = Assert.Throws<InvalidOperationException>(resolver.Resolve);

        Assert.Contains("AutoGenerateSelfSigned", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Certificate:Path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void A_generated_certificate_is_a_leaf_for_server_authentication_only()
    {
        var certificate = CreateProvider(Path.Combine(_directory, "dev.pfx")).Resolve();

        // No es CA: si lo fuera, un cliente podría encadenar confianza indebida.
        var basicConstraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.False(basicConstraints.CertificateAuthority);

        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(usage.EnhancedKeyUsages.Cast<Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public void Persisting_creates_missing_directories_and_leaves_no_temporary_file_behind()
    {
        var pfxPath = Path.Combine(_directory, "nested", "deep", "dev.pfx");

        SelfSignedCertificateFactory.Persist(
            pfxPath,
            CreateProvider(pfxPath).Resolve(),
            password: string.Empty);

        Assert.True(File.Exists(pfxPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(pfxPath)!, "*.tmp"));
    }

    private CertificateProvider CreateProvider(
        string pfxPath,
        string password = "",
        CertificateMode mode = CertificateMode.Auto,
        bool autoGenerateSelfSigned = true,
        bool useDevelopmentCertificate = false)
    {
        var options = new SmtpMockupOptions
        {
            Certificate = new CertificateOptions
            {
                Mode = mode.ToString(),
                Path = pfxPath,
                Password = password,
                AutoGenerateSelfSigned = autoGenerateSelfSigned,
                UseDevelopmentCertificate = useDevelopmentCertificate,
            },
        };

        return new CertificateProvider(
            Options.Create(options),
            LoggerFactory
                .Create(builder => builder.SetMinimumLevel(LogLevel.None))
                .CreateLogger<CertificateProvider>());
    }

    [Fact]
    public void The_development_certificate_store_is_only_consulted_when_the_option_allows_it()
    {
        // Con UseDevelopmentCertificate=false el resolver nunca mira el almacén personal, así
        // que el resultado depende sólo de la configuración, no de la máquina que ejecuta el test.
        var resolver = CreateProvider(
            Path.Combine(_directory, "dev.pfx"),
            useDevelopmentCertificate: false);

        Assert.False(SelfSignedCertificateFactory.IsDevelopmentCertificate(resolver.Resolve()));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Un temporal que no se puede borrar no debe fallar el test.
        }

        GC.SuppressFinalize(this);
    }
}
