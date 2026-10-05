using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using SmtpMockup.Core.Options;
using SmtpMockup.Web.Components.Layout;
using SmtpMockup.Web.Components.Pages;
using SmtpMockup.Web.Components.Shared;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// La UI no debe enseñar puertos ni direcciones escritos a mano: los lee de la configuración.
/// </summary>
/// <remarks>
/// Antes, el listado y la página «acerca de» tenían el 8025 literal. Con
/// <c>--Smtp:Plain:Port=9025</c> la UI seguía invitando a enviar por el puerto equivocado, que es
/// justo el puerto por el que no se puede enviar nada. Estos tests fijan que el texto siga la
/// configuración.
/// </remarks>
public sealed class EndpointDisplayTests
{
    private sealed class ListContext(WebOptions? webOptions = null, SmtpMockupOptions? smtpOptions = null)
        : WebTestContext(webOptions, smtpOptions);

    [Fact]
    public async Task The_empty_state_names_the_configured_plain_port()
    {
        await using var context = new ListContext(smtpOptions: new SmtpMockupOptions
        {
            Smtp = new SmtpOptions { Plain = new SmtpEndpointOptions { Port = 9025 } },
        });

        var component = context.Render<MessageListPage>();

        Assert.Contains("9025", component.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("8025", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_empty_state_falls_back_to_the_default_port_with_default_options()
    {
        await using var context = new ListContext();

        var component = context.Render<MessageListPage>();

        Assert.Contains("8025", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_about_page_shows_the_configured_endpoints()
    {
        await using var context = new ListContext(smtpOptions: new SmtpMockupOptions
        {
            Smtp = new SmtpOptions
            {
                Plain = new SmtpEndpointOptions { Port = 9025, BindAddress = "127.0.0.1" },
                StartTls = new SmtpEndpointOptions { Port = 9443, BindAddress = "127.0.0.1" },
            },
            Web = new WebOptions { Enabled = true, Port = 9080, BindAddress = "127.0.0.1" },
        });

        var component = context.Render<AboutPage>();
        var markup = component.Markup;

        Assert.Contains("127.0.0.1:9025", markup, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:9443", markup, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:9080", markup, StringComparison.Ordinal);

        // El ejemplo de swaks también, que es donde el usuario copia y pega el comando.
        Assert.Contains("--server 127.0.0.1:9025", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("8025", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_about_page_brackets_an_ipv6_web_address()
    {
        await using var context = new ListContext(smtpOptions: new SmtpMockupOptions
        {
            Web = new WebOptions { Enabled = true, Port = 8080, BindAddress = "::1" },
        });

        var component = context.Render<AboutPage>();

        // 'http://::1:8080' no es una URL: la autoridad es ambigua y ni el navegador ni
        // Kestrel la entenderían.
        Assert.Contains("http://[::1]:8080", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_settings_page_shows_the_configured_endpoints()
    {
        await using var context = new ListContext(smtpOptions: new SmtpMockupOptions
        {
            Smtp = new SmtpOptions
            {
                Plain = new SmtpEndpointOptions { Port = 9025, BindAddress = "127.0.0.1" },
                StartTls = new SmtpEndpointOptions { Port = 9443, BindAddress = "127.0.0.1" },
            },
            Web = new WebOptions { Enabled = true, Port = 9080, BindAddress = "127.0.0.1" },
        });

        var markup = context.Render<SettingsPage>().Markup;

        Assert.Contains("127.0.0.1:9025", markup, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:9443", markup, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:9080", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_about_page_says_so_when_the_plain_listener_is_disabled()
    {
        await using var context = new ListContext(smtpOptions: new SmtpMockupOptions
        {
            Smtp = new SmtpOptions
            {
                Plain = new SmtpEndpointOptions { Enabled = false, Port = 8025 },
                StartTls = new SmtpEndpointOptions { Enabled = true, Port = 8443 },
            },
        });

        var markup = context.Render<AboutPage>().Markup;

        // Con el listener en claro apagado, sugerir un comando swaks contra él es mandar al
        // usuario a una conexión rechazada.
        Assert.DoesNotContain("--server", markup, StringComparison.Ordinal);
        Assert.Contains("deshabilitado", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_app_bar_shows_the_configured_title()
    {
        await using var context = new ListContext(
            webOptions: new WebOptions { Title = "Buzón de pruebas" });

        var markup = context.Render<MainLayout>().Markup;

        Assert.Contains("Buzón de pruebas", markup, StringComparison.Ordinal);
        Assert.DoesNotContain(">smtp-mockup<", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_title_is_the_product_name()
    {
        await using var context = new ListContext();

        Assert.Contains("smtp-mockup", context.Render<MainLayout>().Markup, StringComparison.Ordinal);
    }

    // Regresión de Web:Title. Cada página componía su <PageTitle> con el literal «— smtp-mockup»,
    // así que el nombre configurado se perdía en cuanto se navegaba dentro de la app: el <head>
    // inicial decía el valor correcto y a la primera navegación lo pisaba el literal. PageHeader
    // es ahora el único sitio donde se compone ese sufijo.
    [Theory]
    [InlineData("Mensajes", "Buzón de pruebas", "Mensajes — Buzón de pruebas")]
    [InlineData("Mensajes", "smtp-mockup", "Mensajes — smtp-mockup")]
    [InlineData("Estadísticas", "Buzón", "Estadísticas — Buzón")]
    // Vacío o en blanco: el nombre del producto no puede desaparecer por un valor mal escrito.
    [InlineData("Mensajes", "", "Mensajes — smtp-mockup")]
    [InlineData("Mensajes", "   ", "Mensajes — smtp-mockup")]
    [InlineData("Mensajes", null, "Mensajes — smtp-mockup")]
    // Sin texto de página el título no se queda en « — smtp-mockup».
    [InlineData("", "Buzón", "Buzón — Buzón")]
    [InlineData(null, "smtp-mockup", "smtp-mockup — smtp-mockup")]
    public void The_page_header_composes_the_title_from_the_configuration(
        string? text,
        string? configured,
        string expected)
        => Assert.Equal(expected, PageHeader.Compose(text, configured));
}
