using System.Net;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;

namespace SmtpMockup.Core.Tests;

/// <summary>
/// Validación fail-fast de la configuración (SPEC §6). Cada test comprueba que una opción
/// inválida produce un <see cref="OptionsValidationException"/> con un mensaje claro que
/// menciona la clave de configuración afectada.
/// </summary>
public sealed class OptionsValidationTests
{
    private static readonly SmtpOptionsValidator SmtpValidator = new();
    private static readonly CertificateOptionsValidator CertificateValidator = new();
    private static readonly StorageOptionsValidator StorageValidator = new();
    private static readonly WebOptionsValidator WebValidator = new();
    private static readonly SmtpMockupOptionsValidator RootValidator = new();

    private static string Validate<T>(T options, IValidateOptions<T> validator)
        where T : class
    {
        var result = validator.Validate(null, options);
        Assert.True(result.Failed, "Expected the options to be invalid.");
        return result.FailureMessage ?? string.Join("; ", result.Failures ?? []);
    }

    private static void AssertValid<T>(T options, IValidateOptions<T> validator)
        where T : class
    {
        var result = validator.Validate(null, options);
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    private static void AssertFails<T>(T options, IValidateOptions<T> validator, string fragment)
        where T : class
        => Assert.Contains(fragment, Validate(options, validator), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Defaults_are_valid()
    {
        var options = new SmtpMockupOptions();

        AssertValid(options, RootValidator);
        AssertValid(options.Smtp, SmtpValidator);
        AssertValid(options.Certificate, CertificateValidator);
        AssertValid(options.Storage, StorageValidator);
        AssertValid(options.Web, WebValidator);

        Assert.True(options.Smtp.Plain.Enabled);
        Assert.Equal(8025, options.Smtp.Plain.Port);
        Assert.True(options.Smtp.StartTls.Enabled);
        Assert.Equal(8443, options.Smtp.StartTls.Port);
        Assert.Equal(25, options.Smtp.MaxMessageSizeMb);
        Assert.Equal(SmtpSecurityMode.None, options.Smtp.Plain.ParsedSecurity);
        Assert.Equal(SmtpSecurityMode.StartTls, options.Smtp.StartTls.ParsedSecurity);
        Assert.Equal("Auto", options.Certificate.Mode);
        Assert.Equal(CertificateMode.Auto, options.Certificate.ParsedMode);
        Assert.Equal("certs/dev.pfx", options.Certificate.Path);
        Assert.Equal(string.Empty, options.Certificate.Password);
        Assert.True(options.Certificate.AutoGenerateSelfSigned);
        Assert.True(options.Certificate.UseDevelopmentCertificate);
        Assert.Equal("data/messages", options.Storage.Directory);
        Assert.True(options.Web.Enabled);
        Assert.Equal(8080, options.Web.Port);
        Assert.Equal("127.0.0.1", options.Web.BindAddress);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(70_000)]
    public void Plain_port_out_of_range_fails(int port)
    {
        var options = new SmtpOptions();
        options.Plain.Port = port;

        AssertFails(options, SmtpValidator, "Smtp:Plain:Port");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(70_000)]
    public void StartTls_port_out_of_range_fails(int port)
    {
        var options = new SmtpOptions();
        options.StartTls.Port = port;

        AssertFails(options, SmtpValidator, "Smtp:StartTls:Port");
    }

    [Fact]
    public void Same_port_on_both_enabled_listeners_fails()
    {
        var options = new SmtpOptions();
        options.StartTls.Port = options.Plain.Port;

        AssertFails(options, SmtpValidator, "same port");
    }

    [Fact]
    public void Same_port_is_allowed_when_one_listener_is_disabled()
    {
        var options = new SmtpOptions();
        options.StartTls.Port = options.Plain.Port;
        options.StartTls.Enabled = false;

        AssertValid(options, SmtpValidator);
    }

    [Fact]
    public void All_listeners_disabled_fails()
    {
        var options = new SmtpOptions();
        options.Plain.Enabled = false;
        options.StartTls.Enabled = false;

        AssertFails(options, SmtpValidator, "at least one SMTP listener");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(5_000)]
    public void Max_message_size_out_of_range_fails(int sizeMb)
    {
        var options = new SmtpOptions { MaxMessageSizeMb = sizeMb };

        AssertFails(options, SmtpValidator, "Smtp:MaxMessageSizeMb");
    }

    [Fact]
    public void Max_message_size_is_exposed_in_bytes()
    {
        var options = new SmtpOptions { MaxMessageSizeMb = 3 };

        Assert.Equal(3 * 1024 * 1024, options.MaxMessageSizeBytes);
    }
    [Fact]
    public void Unknown_certificate_mode_fails()
    {
        var options = new CertificateOptions { Mode = "Magic" };

        AssertFails(options, CertificateValidator, "Certificate:Mode");
    }

    [Fact]
    public void File_mode_requires_path()
    {
        var options = new CertificateOptions { Mode = "File", Path = "  " };

        AssertFails(options, CertificateValidator, "Certificate:Path");
    }

    [Fact]
    public void Auto_mode_with_empty_path_is_valid()
    {
        var options = new CertificateOptions { Mode = "Auto", Path = string.Empty };

        AssertValid(options, CertificateValidator);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("StartTls")]
    [InlineData("Implicit")]
    [InlineData("implicit")]
    public void Known_security_modes_are_valid(string security)
    {
        var options = new SmtpOptions();
        options.StartTls.Security = security;

        AssertValid(options, SmtpValidator);
    }

    [Fact]
    public void Unknown_security_mode_fails_naming_the_key()
    {
        var options = new SmtpOptions();
        options.StartTls.Security = "Quantum";

        AssertFails(options, SmtpValidator, "Smtp:StartTls:Security");
    }

    [Fact]
    public void Unknown_security_mode_on_the_plain_endpoint_fails_too()
    {
        var options = new SmtpOptions();
        options.Plain.Security = "Implicit-TLS";

        AssertFails(options, SmtpValidator, "Smtp:Plain:Security");
    }

    [Fact]
    public void The_secure_endpoint_defaults_to_announcing_starttls_not_implicit_tls()
    {
        var options = new SmtpOptions();

        // El default de 8443 es STARTTLS anunciado y opcional; Implicit (SMTPS) es opt-in.
        Assert.Equal(SmtpSecurityMode.StartTls, options.StartTls.ParsedSecurity);
        Assert.Equal(8443, options.StartTls.Port);
        Assert.True(options.StartTls.Enabled);

        // 8025 sigue en claro: no pide ni certificado ni TLS.
        Assert.Equal(SmtpSecurityMode.None, options.Plain.ParsedSecurity);
    }

    [Fact]
    public void Parsed_security_throws_a_clear_error_when_the_value_is_unknown()
    {
        var endpoint = new SmtpEndpointOptions { Security = "nope" };

        var exception = Assert.Throws<OptionsValidationException>(() => endpoint.ParsedSecurity);
        Assert.Contains("Security", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_storage_directory_fails()
    {
        var options = new StorageOptions { Directory = "   " };

        AssertFails(options, StorageValidator, "Storage:Directory");
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData("127.0.0.1:8080")]
    public void Invalid_web_bind_address_fails(string bindAddress)
    {
        var options = new WebOptions { BindAddress = bindAddress };

        AssertFails(options, WebValidator, "Web:BindAddress");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void Web_port_out_of_range_fails(int port)
    {
        var options = new WebOptions { Port = port };

        AssertFails(options, WebValidator, "Web:Port");
    }

    [Fact]
    public void Web_port_zero_is_valid_as_an_ephemeral_port()
    {
        // 0 no es un puerto: es la petición de "asígname uno libre". Los listeners SMTP ya lo
        // admitían, así que se admitía aquí también en lugar de dejar la asimetría (D-6).
        var options = new WebOptions { Port = 0 };

        AssertValid(options, WebValidator);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void Web_max_page_size_out_of_range_fails(int maxPageSize)
    {
        var options = new WebOptions { MaxPageSize = maxPageSize };

        AssertFails(options, WebValidator, "Web:MaxPageSize");
    }

    [Fact]
    public void A_default_page_size_above_the_maximum_fails()
    {
        // El listado arrancaría con un tamaño de página que su propio selector no puede elegir.
        var options = new WebOptions { DefaultPageSize = 500, MaxPageSize = 200 };

        AssertFails(options, WebValidator, "Web:DefaultPageSize");
    }

    [Fact]
    public void A_zero_page_size_fails()
    {
        var options = new WebOptions { DefaultPageSize = 0 };

        AssertFails(options, WebValidator, "Web:DefaultPageSize");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_html_preview_limit_fails(int maxHtmlPreviewBytes)
    {
        var options = new WebOptions { MaxHtmlPreviewBytes = maxHtmlPreviewBytes };

        AssertFails(options, WebValidator, "Web:MaxHtmlPreviewBytes");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void A_live_update_debounce_out_of_range_fails(int milliseconds)
    {
        // Por encima de unos segundos la UI "parece colgada" tras un envío masivo, que es justo
        // lo contrario de lo que promete la actualización en vivo.
        var options = new WebOptions { LiveUpdateDebounceMilliseconds = milliseconds };

        AssertFails(options, WebValidator, "Web:LiveUpdateDebounceMilliseconds");
    }

    [Fact]
    public void The_default_web_options_are_valid()
    {
        AssertValid(new WebOptions(), WebValidator);
    }

    [Fact]
    public void Ipv6_loopback_bind_address_is_valid()
    {
        var options = new WebOptions { BindAddress = "::1" };

        AssertValid(options, WebValidator);
    }

    [Fact]
    public void Web_port_colliding_with_enabled_smtp_port_fails()
    {
        var options = new SmtpMockupOptions();
        options.Web.Port = options.Smtp.Plain.Port;

        AssertFails(options, RootValidator, "Web:Port");
    }

    [Fact]
    public void Web_port_colliding_with_disabled_smtp_port_is_valid()
    {
        var options = new SmtpMockupOptions();
        options.Smtp.Plain.Enabled = false;
        options.Web.Port = options.Smtp.Plain.Port;

        AssertValid(options, RootValidator);
    }

    [Fact]
    public void Web_port_zero_does_not_collide_with_smtp_ports()
    {
        // Con puerto efímero no puede haber choque: se compara contra un puerto que nadie va a
        // usar. Sin esta excepción, Web:Port=0 fallaría si un listener SMTP tuviera el 0.
        var options = new SmtpMockupOptions();
        options.Smtp.Plain.Port = 0;
        options.Web.Port = 0;

        AssertValid(options, RootValidator);
    }

    // ---- BindAddress de los listeners SMTP (D-1) ----

    [Fact]
    public void Smtp_endpoints_default_to_loopback()
    {
        var options = new SmtpMockupOptions();

        Assert.Equal("127.0.0.1", options.Smtp.Plain.BindAddress);
        Assert.Equal("127.0.0.1", options.Smtp.StartTls.BindAddress);
        Assert.Equal(IPAddress.Loopback, options.Smtp.Plain.ParsedBindAddress);
        Assert.True(EndpointAddress.IsLoopback(options.Smtp.Plain.BindAddress));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("localhost")]
    [InlineData("999.1.1.1")]
    [InlineData("not an address")]
    public void Smtp_endpoint_bind_address_must_be_an_ip_literal(string? bindAddress)
    {
        var options = new SmtpMockupOptions();
        options.Smtp.Plain.BindAddress = bindAddress!;

        AssertFails(options.Smtp, SmtpValidator, "Smtp:Plain:BindAddress");
    }

    [Fact]
    public void Smtp_starttls_bind_address_error_names_its_own_key()
    {
        var options = new SmtpMockupOptions();
        options.Smtp.StartTls.BindAddress = "nope";

        AssertFails(options.Smtp, SmtpValidator, "Smtp:StartTls:BindAddress");
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.1.50")]
    public void Smtp_endpoint_bind_address_outside_loopback_is_valid(string bindAddress)
    {
        // Fuera de loopback es un warning en el log, no un error: levantar el SMTP en una red
        // (CI, contenedor, integración desde otra máquina) es legítimo.
        var options = new SmtpMockupOptions();
        options.Smtp.Plain.BindAddress = bindAddress;

        AssertValid(options.Smtp, SmtpValidator);
    }

    [Fact]
    public void Smtp_endpoint_bind_address_defaults_to_loopback_when_unparsable()
    {
        // El validador corre antes y ya rechaza lo inválido; el default es sólo para que
        // ParsedBindAddress nunca devuelva null y rompa el binding con un NRE.
        var options = new SmtpEndpointOptions { BindAddress = "nope" };

        Assert.Equal(IPAddress.Loopback, options.ParsedBindAddress);
    }

    // ---- Topes de almacenamiento (D-6) ----

    [Theory]
    [InlineData(-1)]
    [InlineData(-1024)]
    public void Storage_negative_limits_fail(int limit)
    {
        // Un tope negativo no significa "sin tope": comparando contra él, `length > -1` es
        // siempre cierto y todo se omite sin dejar rastro.
        var inline = new StorageOptions { MaxInlineAttachmentBytes = limit };
        var raw = new StorageOptions { MaxRawMimeBytes = limit };

        AssertFails(inline, StorageValidator, "Storage:MaxInlineAttachmentBytes");
        AssertFails(raw, StorageValidator, "Storage:MaxRawMimeBytes");
    }

    // ---- Aritmética del tamaño de mensaje (D-3) ----

    [Fact]
    public void Max_message_size_mb_limit_stays_inside_int_range()
    {
        // El tope del validador tiene que ser el del tipo: 2048 * 1024 * 1024 es 2.147.483.648,
        // una unidad por encima de int.MaxValue, y el producto en int se desbordaría a negativo.
        var options = new SmtpOptions { MaxMessageSizeMb = SmtpOptionsValidator.MaxMessageSizeMbLimit };

        AssertValid(options, SmtpValidator);
        Assert.True(options.MaxMessageSizeBytes > 0);
        Assert.Equal(SmtpOptionsValidator.MaxMessageSizeMbLimit * 1024 * 1024L, options.MaxMessageSizeBytes);
    }

    [Fact]
    public void Max_message_size_bytes_is_never_negative_at_the_limit()
    {
        foreach (var mb in Enumerable.Range(1, SmtpOptionsValidator.MaxMessageSizeMbLimit))
        {
            var bytes = new SmtpOptions { MaxMessageSizeMb = mb }.MaxMessageSizeBytes;

            Assert.True(bytes > 0, $"MaxMessageSizeBytes was {bytes} for {mb} MB.");
            Assert.Equal(mb * 1024 * 1024L, bytes);
        }
    }

    [Fact]
    public void Max_message_size_above_the_limit_fails()
    {
        var options = new SmtpOptions { MaxMessageSizeMb = SmtpOptionsValidator.MaxMessageSizeMbLimit + 1 };

        AssertFails(options, SmtpValidator, "Smtp:MaxMessageSizeMb");
    }

    [Fact]
    public void Smtp_endpoint_port_zero_is_valid_as_an_ephemeral_port()
    {
        // El servicio ya resolvía el puerto efímero (SmtpListenerService.ResolveEphemeralPortAsync),
        // pero el validador lo rechazaba: pedir 0 mataba el host en el arranque. Estos tests
        // existían porque los tests de Host no podían ni levantar dos instancias en paralelo.
        var options = new SmtpMockupOptions();
        options.Smtp.Plain.Port = 0;
        options.Smtp.StartTls.Port = 0;

        AssertValid(options.Smtp, SmtpValidator);
        AssertValid(options, RootValidator);
    }

    [Fact]
    public void Smtp_endpoint_negative_port_fails()
    {
        var options = new SmtpMockupOptions();
        options.Smtp.Plain.Port = -1;

        AssertFails(options.Smtp, SmtpValidator, "Smtp:Plain:Port");
    }

    [Fact]
    public void Two_smtp_listeners_with_the_same_real_port_still_collide()
    {
        // La excepción del puerto 0 no debe relajar el caso real: dos listeners en 8025 siguen
        // siendo un conflicto, y es el error que el usuario tiene que ver.
        var options = new SmtpMockupOptions();
        options.Smtp.Plain.Port = 8025;
        options.Smtp.StartTls.Port = 8025;

        AssertFails(options.Smtp, SmtpValidator, "cannot use the same port");
    }

    [Fact]
    public void Web_port_does_not_collide_with_an_ephemeral_smtp_port()
    {
        var options = new SmtpMockupOptions();
        options.Smtp.Plain.Port = 0;
        options.Smtp.StartTls.Port = 0;
        options.Web.Port = 0;

        AssertValid(options, RootValidator);
    }

    [Fact]
    public void Storage_limits_accept_zero_meaning_no_limit()
    {
        // 0 = sin tope en las dos, como documentan. Antes MaxInlineAttachmentBytes=0 descartaba
        // el contenido de todos los adjuntos: el valor que debería desactivarlo los eliminaba.
        var options = new StorageOptions { MaxInlineAttachmentBytes = 0, MaxRawMimeBytes = 0 };

        AssertValid(options, StorageValidator);
    }

    [Fact]
    public void Web_title_defaults_to_the_product_name()
    {
        Assert.Equal("smtp-mockup", new WebOptions().Title);
    }
}
