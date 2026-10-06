using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;

namespace SmtpMockup.Core.Tests;

/// <summary>
/// Binding de las claves de <c>appsettings.json</c> a las opciones tipadas y fallo rápido
/// al arrancar (<c>ValidateOnStart</c>). Cubre el prefijo común de la configuración.
/// </summary>
public sealed class OptionsBindingTests
{
    private static IConfiguration BuildConfiguration(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider BuildHost(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSmtpMockupOptions(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Binds_the_hosting_section()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Hosting:Mode"] = "WindowsService",
            ["Hosting:LogDirectory"] = "var/logs",
            ["Hosting:LogFileName"] = "mockup-{Date}.log",
            ["Hosting:LogRetentionDays"] = "30",
            ["Hosting:EventLogSource"] = "smtp-mockup-test",
            ["Hosting:EventLogLevel"] = "Warning",
        });

        using var host = BuildHost(configuration);
        var options = host.GetRequiredService<IOptions<HostingOptions>>().Value;

        Assert.Equal(HostingMode.WindowsService, options.ParsedMode);
        Assert.Equal("var/logs", options.LogDirectory);
        Assert.Equal("mockup-{Date}.log", options.LogFileName);
        Assert.Equal(30, options.LogRetentionDays);
        Assert.Equal("smtp-mockup-test", options.EventLogSource);
        Assert.Equal("Warning", options.EventLogLevel);
    }

    [Fact]
    public void An_invalid_hosting_section_fails_the_whole_configuration()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?> { ["Hosting:Mode"] = "Daemon" });

        var exception = Assert.Throws<OptionsValidationException>(
            () => SmtpMockupConfiguration.BindAndValidate(configuration));

        Assert.Contains("Hosting:Mode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Binds_every_documented_key()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Smtp:Plain:Enabled"] = "false",
            ["Smtp:Plain:Port"] = "9025",
            ["Smtp:Plain:Security"] = "None",
            ["Smtp:StartTls:Enabled"] = "true",
            ["Smtp:StartTls:Port"] = "9443",
            ["Smtp:StartTls:Security"] = "Implicit",  // overridable: 8443 defaults to StartTls
            ["Smtp:MaxMessageSizeMb"] = "10",
            ["Certificate:Mode"] = "File",
            ["Certificate:Path"] = "certs/custom.pfx",
            ["Certificate:Password"] = "s3cret",
            ["Certificate:AutoGenerateSelfSigned"] = "false",
            ["Certificate:UseDevelopmentCertificate"] = "false",
            ["Storage:Directory"] = "/tmp/smtp-messages",
            ["Web:Enabled"] = "true",
            ["Web:Port"] = "9090",
            ["Web:BindAddress"] = "0.0.0.0",
        });

        using var provider = BuildHost(configuration);
        var options = provider.GetRequiredService<IOptions<SmtpMockupOptions>>().Value;

        Assert.False(options.Smtp.Plain.Enabled);
        Assert.Equal(9025, options.Smtp.Plain.Port);
        Assert.Equal(SmtpSecurityMode.None, options.Smtp.Plain.ParsedSecurity);
        Assert.True(options.Smtp.StartTls.Enabled);
        Assert.Equal(9443, options.Smtp.StartTls.Port);
        Assert.Equal(SmtpSecurityMode.Implicit, options.Smtp.StartTls.ParsedSecurity);
        Assert.Equal(10, options.Smtp.MaxMessageSizeMb);
        Assert.Equal(10 * 1024 * 1024, options.Smtp.MaxMessageSizeBytes);
        Assert.Equal(CertificateMode.File, options.Certificate.ParsedMode);
        Assert.Equal("certs/custom.pfx", options.Certificate.Path);
        Assert.Equal("s3cret", options.Certificate.Password);
        Assert.False(options.Certificate.AutoGenerateSelfSigned);
        Assert.False(options.Certificate.UseDevelopmentCertificate);
        Assert.Equal("/tmp/smtp-messages", options.Storage.Directory);
        Assert.True(options.Web.Enabled);
        Assert.Equal(9090, options.Web.Port);
        Assert.Equal("0.0.0.0", options.Web.BindAddress);
    }

    [Fact]
    public void Defaults_apply_when_configuration_is_empty()
    {
        using var provider = BuildHost(BuildConfiguration(new Dictionary<string, string?>()));
        var options = provider.GetRequiredService<IOptions<SmtpMockupOptions>>().Value;

        Assert.Equal(8025, options.Smtp.Plain.Port);
        Assert.Equal(8443, options.Smtp.StartTls.Port);
        Assert.Equal(25, options.Smtp.MaxMessageSizeMb);
        Assert.Equal(CertificateMode.Auto, options.Certificate.ParsedMode);
        Assert.True(options.Certificate.AutoGenerateSelfSigned);
        Assert.Equal("data/messages", options.Storage.Directory);
        Assert.Equal(8888, options.Web.Port);
        Assert.Equal("127.0.0.1", options.Web.BindAddress);
    }

    [Fact]
    public void Unknown_certificate_mode_fails_fast_with_a_clear_message()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Certificate:Mode"] = "Magic",
        });

        using var provider = BuildHost(configuration);

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CertificateOptions>>().Value);
        Assert.Contains("Certificate:Mode", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_smtp_port_fails_fast()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Smtp:Plain:Port"] = "70000",
        });

        using var provider = BuildHost(configuration);

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SmtpOptions>>().Value);
        Assert.Contains("Smtp:Plain:Port", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_storage_directory_fails_at_startup_not_at_first_use()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Directory"] = "",
        });

        var services = new ServiceCollection();
        services.AddSmtpMockupOptions(configuration);
        await using var provider = services.BuildServiceProvider();

        using var hosted = new HostBuilder()
            .ConfigureServices(svc => svc.AddSmtpMockupOptions(configuration))
            .Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => hosted.StartAsync());
        Assert.Contains("Storage:Directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Valid_configuration_starts_the_host()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddSmtpMockupOptions(
                BuildConfiguration(new Dictionary<string, string?>())))
            .Build();

        await host.StartAsync();

        Assert.True(host.Services.GetRequiredService<IOptions<SmtpMockupOptions>>().Value.Web.Enabled);
        await host.StopAsync();
    }

    [Fact]
    public void Bind_and_validate_reports_every_invalid_key_at_once()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            // El 0 ya no es un puerto inválido: es la petición de un puerto efímero. Para seguir
            // cubriendo el caso, el valor mal formado aquí es uno negativo.
            ["Smtp:Plain:Port"] = "-1",
            ["Smtp:MaxMessageSizeMb"] = "0",
            ["Certificate:Mode"] = "Magic",
            ["Storage:Directory"] = "",
            ["Web:BindAddress"] = "nope",
        });

        var exception = Assert.Throws<OptionsValidationException>(
            () => SmtpMockupConfiguration.BindAndValidate(configuration));

        Assert.Contains("Smtp:Plain:Port", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Smtp:MaxMessageSizeMb", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Certificate:Mode", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Storage:Directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Web:BindAddress", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bind_and_validate_returns_options_when_valid()
    {
        var options = SmtpMockupConfiguration.BindAndValidate(BuildConfiguration(
            new Dictionary<string, string?> { ["Web:Port"] = "9090" }));

        Assert.Equal(9090, options.Web.Port);
        Assert.Equal(8025, options.Smtp.Plain.Port);
    }
}