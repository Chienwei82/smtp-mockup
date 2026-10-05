using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Extensions;
using SmtpMockup.Web.Components.Pages;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// El detalle es donde el desarrollador comprueba qué envió su aplicación: sobre, cabeceras, los
/// dos cuerpos, el JSON crudo y los adjuntos (SPEC §9.4 y §9.5).
/// </summary>
public sealed class MessageDetailPageTests : WebTestContext
{
    private IRenderedComponent<MessageDetailPage> RenderDetail(string id, string? tab = null)
    {
        if (tab is not null)
        {
            // La pestaña viaja en la query; así también se puede enlazar /messages/ID?tab=HTML.
            Navigation.NavigateTo($"/messages/{id}?tab={tab}");
        }

        return Render<MessageDetailPage>(parameters => parameters.Add(page => page.Id, id));
    }

    /// <summary>La navegación falsa de bUnit: registra a dónde fue el componente.</summary>
    private Microsoft.AspNetCore.Components.NavigationManager Navigation
        => Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

    [Fact]
    public void The_envelope_shows_the_real_recipients_including_the_hidden_bcc()
    {
        Store.Seed(TestData.Message());

        var component = RenderDetail(TestData.Id);

        Assert.Contains("hidden@example.com", component.Markup, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:54321", component.Markup, StringComparison.Ordinal);
        Assert.Contains("StartTls", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_invalid_id_in_the_route_shows_not_found_instead_of_throwing()
    {
        var component = RenderDetail("no-es-un-ulid");

        Assert.NotNull(component.Find("[data-testid='message-not-found']"));
    }

    [Fact]
    public void An_id_that_does_not_exist_shows_not_found()
    {
        var component = RenderDetail(TestData.Id);

        Assert.NotNull(component.Find("[data-testid='message-not-found']"));
    }

    [Fact]
    public void The_subject_is_decoded_for_display()
    {
        Store.Seed(TestData.Message(subject: "=?UTF-8?B?UHJ1ZWJhIMOhw6nDrcOzw7o=?="));

        var component = RenderDetail(TestData.Id);

        Assert.Contains("Prueba áéíóú", component.Find("[data-testid='detail-subject']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_required_tab_is_present()
    {
        Store.Seed(TestData.Message());

        var component = RenderDetail(TestData.Id);

        var tabs = component.FindComponents<MudTabPanel>()
            .Select(panel => panel.Instance?.Text ?? string.Empty)
            .ToArray();

        // Las pestañas llevan contadores ("Texto (1.234 car.)"), así que se compara el prefijo.
        Assert.Contains(tabs, tab => tab.StartsWith("Resumen", StringComparison.Ordinal));
        Assert.Contains(tabs, tab => tab.StartsWith("Texto", StringComparison.Ordinal));
        Assert.Contains(tabs, tab => tab.StartsWith("HTML", StringComparison.Ordinal));
        Assert.Contains(tabs, tab => tab.StartsWith("Headers", StringComparison.Ordinal));
        Assert.Contains(tabs, tab => tab.StartsWith("JSON", StringComparison.Ordinal));
        Assert.Contains(tabs, tab => tab.StartsWith("Adjuntos", StringComparison.Ordinal));
    }

    [Fact]
    public void The_text_body_is_shown_as_text()
    {
        Store.Seed(TestData.Message(text: "Hola en texto plano"));

        var component = RenderOnTab(TestData.Id, "Texto");

        Assert.Equal("Hola en texto plano", component.Find("[data-testid='body-text']").TextContent);
    }

    [Fact]
    public void The_headers_are_listed_raw()
    {
        Store.Seed(TestData.Message());

        var component = RenderOnTab(TestData.Id, "Headers");

        Assert.Contains("X-Custom", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_raw_json_tab_shows_the_stored_document()
    {
        Store.Seed(TestData.Message());

        var component = RenderOnTab(TestData.Id, "JSON");

        var json = component.Find("[data-testid='raw-json']").TextContent;
        Assert.Contains("\"id\": \"" + TestData.Id + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_html_body_goes_to_the_sandboxed_frame_not_to_the_page()
    {
        Store.Seed(TestData.Message(html: "<p>Hola</p><script>alert(1)</script>"));

        var component = RenderOnTab(TestData.Id, "HTML");

        var frame = component.Find("iframe");
        Assert.Equal(string.Empty, frame.GetAttribute("sandbox"));
        Assert.Contains("<script>", frame.GetAttribute("srcdoc")!, StringComparison.Ordinal);
        Assert.Empty(component.FindAll("script"));
    }

    [Fact]
    public void Attachments_are_listed_with_a_download_link()
    {
        Store.Seed(TestData.Message(attachments: [TestData.Attachment("reporte.pdf")]));

        var component = RenderOnTab(TestData.Id, "Adjuntos");

        var link = component.Find("[data-testid='download-attachment']");
        Assert.Equal($"/download/message/{TestData.Id}/attachment/0", link.GetAttribute("href"));
    }

    [Fact]
    public void An_attachment_that_was_not_stored_has_no_download_link()
    {
        Store.Seed(TestData.Message(attachments: [TestData.Attachment("enorme.zip", omitted: true)]));

        var component = RenderOnTab(TestData.Id, "Adjuntos");

        Assert.Empty(component.FindAll("[data-testid='download-attachment']"));
        Assert.Contains("no guardado", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_attachment_named_with_a_path_is_sanitized_before_being_offered()
    {
        Store.Seed(TestData.Message(attachments: [TestData.Attachment("../../evil.exe", contentType: "text/plain")]));

        var component = RenderOnTab(TestData.Id, "Adjuntos");

        Assert.Contains("evil.exe", component.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("../../evil.exe", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_raw_mime_download_is_hidden_when_it_was_not_kept()
    {
        Store.Seed(TestData.Message(rawMimeBase64: null));

        var component = RenderOnTab(TestData.Id, "Adjuntos");

        Assert.Empty(component.FindAll("[data-testid='download-raw']"));
    }

    [Fact]
    public void The_raw_mime_download_is_available_when_it_was_kept()
    {
        Store.Seed(TestData.Message());

        var component = RenderOnTab(TestData.Id, "Adjuntos");

        Assert.Equal(
            $"/download/message/{TestData.Id}/raw",
            component.Find("[data-testid='download-raw']").GetAttribute("href"));
    }

    [Fact]
    public void Deleting_from_the_detail_asks_for_confirmation_and_goes_back_to_the_list()
    {
        Store.Seed(TestData.Message());
        var component = RenderDetail(TestData.Id);

        component.Find("[data-testid='detail-delete']").Click();

        Assert.NotNull(component.Find("[role='alertdialog']"));
        Assert.Single(Store.Messages);

        component.Find("[data-testid='confirm-accept']").Click();

        component.WaitForAssertion(() => Assert.Empty(Store.Messages));
        Assert.EndsWith("/", Navigation.Uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// Abre una pestaña. MudTabs sólo renderiza el panel activo, así que la prueba pide la
    /// pestaña por su parámetro <c>Tab</c> —la misma vía que usaría un enlace /messages/ID?tab=—
    /// en lugar de manipular el estado interno de MudBlazor.
    /// </summary>
    private IRenderedComponent<MessageDetailPage> RenderOnTab(string id, string tab)
    {
        var component = RenderDetail(id, tab);

        Assert.Equal(
            MessageDetailPage.TabIndex(tab),
            component.FindComponent<MudTabs>().Instance.GetState(state => state.ActivePanelIndex));

        return component;
    }
}
