using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using SmtpMockup.Web.Components.Pages;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// El listado es la pantalla que el desarrollador deja abierta mientras su aplicación envía
/// correos: de aquí salen la búsqueda, los filtros, la paginación y los tres borrados
/// (SPEC §9.3, §9.6 y §10.3).
/// </summary>
public sealed class MessageListPageTests : WebTestContext
{
    private const string FirstId = "01JQ8Z3K7F9A2B3C4D5E6F7G01";
    private const string SecondId = "01JQ8Z3K7F9A2B3C4D5E6F7G02";

    private MessageListPage List()
    {
        return Services.GetRequiredService<MessageListPage>();
    }

    private IRenderedComponent<MessageListPage> RenderList()
        => Render<MessageListPage>();

    [Fact]
    public void An_empty_store_shows_the_explanatory_empty_state()
    {
        var component = RenderList();

        Assert.NotNull(component.Find("[data-testid='empty-list']"));
        Assert.Contains("Todavía no llegó ningún correo", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_newest_message_comes_first()
    {
        Store.Seed(TestData.Message(id: FirstId, receivedAt: TestData.ReceivedAt, subject: "antiguo"));
        Store.Seed(TestData.Message(id: SecondId, receivedAt: TestData.ReceivedAt.AddHours(1), subject: "nuevo"));

        var component = RenderList();

        var rows = component.FindAll("[data-testid='message-table'] tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("nuevo", rows[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Typing_in_the_search_box_filters_the_list()
    {
        Store.Seed(TestData.Message(id: FirstId, subject: "factura de marzo"));
        Store.Seed(TestData.Message(id: SecondId, subject: "bienvenida"));

        var component = RenderList();
        component.Find("[data-testid='filter-search']").Change("factura");

        Assert.Contains("factura de marzo", component.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("bienvenida", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_inverted_date_range_is_reported_instead_of_returning_nothing()
    {
        Store.Seed(TestData.Message(id: FirstId));

        var component = RenderList();
        await SetDateAsync(component, "Desde", new DateTime(2026, 2, 10));
        await SetDateAsync(component, "Hasta", new DateTime(2026, 1, 1));

        Assert.NotNull(component.Find("[data-testid='filter-validation']"));
    }

    [Fact]
    public void A_storage_failure_is_shown_in_the_list_instead_of_a_blank_page()
    {
        Store.FailReads = true;

        var component = RenderList();

        Assert.NotNull(component.Find("[data-testid='list-error']"));
    }

    [Fact]
    public void A_message_that_arrives_while_the_list_is_open_appears_without_a_reload()
    {
        // Es el requisito de actualización en vivo: la página ya montada se re-renderiza sola.
        Store.Seed(TestData.Message(id: FirstId, subject: "primero"));
        var component = RenderList();
        Assert.DoesNotContain("llegó tarde", component.Markup, StringComparison.Ordinal);

        Store.Seed(TestData.Message(id: SecondId, subject: "llegó tarde"));
        RaiseStoredChange();
        component.WaitForAssertion(
            () => Assert.Contains("llegó tarde", component.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_message_deleted_elsewhere_disappears_from_the_list()
    {
        Store.Seed(TestData.Message(id: FirstId, subject: "se va"));
        var component = RenderList();

        await Store.DeleteAsync(FirstId);
        RaiseDeletedChange();

        component.WaitForAssertion(
            () => Assert.Contains("No hay mensajes", component.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Deleting_one_message_asks_for_confirmation_first()
    {
        Store.Seed(TestData.Message(id: FirstId));
        var component = RenderList();

        component.Find($"[data-testid='delete-{FirstId}']").Click();

        var dialog = component.Find("[role='alertdialog']");
        Assert.Contains("Se borrará", dialog.TextContent, StringComparison.Ordinal);
        Assert.Single(Store.Messages);
    }

    [Fact]
    public void Cancelling_the_confirmation_does_not_delete_anything()
    {
        Store.Seed(TestData.Message(id: FirstId));
        var component = RenderList();

        component.Find($"[data-testid='delete-{FirstId}']").Click();
        component.Find("[data-testid='confirm-cancel']").Click();

        Assert.Single(Store.Messages);
        Assert.Empty(component.FindAll("[role='alertdialog']"));
    }

    [Fact]
    public void Confirming_deletes_the_selected_message_from_the_store()
    {
        Store.Seed(TestData.Message(id: FirstId));
        Store.Seed(TestData.Message(id: SecondId));
        var component = RenderList();

        component.Find($"[data-testid='delete-{FirstId}']").Click();
        component.Find("[data-testid='confirm-accept']").Click();

        component.WaitForAssertion(() => Assert.Equal(SecondId, Store.Messages.Single().Id));
    }

    [Fact]
    public void Delete_selected_is_disabled_until_something_is_selected()
    {
        Store.Seed(TestData.Message(id: FirstId));
        var component = RenderList();

        Assert.Contains("disabled", component.Find("[data-testid='delete-selected']").OuterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Selecting_rows_enables_the_bulk_delete_and_asks_before_deleting()
    {
        Store.Seed(TestData.Message(id: FirstId));
        Store.Seed(TestData.Message(id: SecondId));
        var component = RenderList();

        component.Find($"[data-testid='select-{FirstId}']").Change(true);
        component.Find("[data-testid='delete-selected']").Click();
        component.Find("[data-testid='confirm-accept']").Click();

        component.WaitForAssertion(() => Assert.Equal(SecondId, Store.Messages.Single().Id));
    }

    [Fact]
    public void Delete_all_applies_the_active_filter_and_not_the_visible_page()
    {
        Store.Seed(TestData.Message(id: FirstId, from: "quiere@example.com"));
        Store.Seed(TestData.Message(id: SecondId, from: "otro@example.com"));
        var component = RenderList();

        component.Find("[data-testid='filter-from']").Change("quiere");
        component.Find("[data-testid='delete-all']").Click();
        component.Find("[data-testid='confirm-accept']").Click();

        component.WaitForAssertion(() => Assert.Equal(SecondId, Store.Messages.Single().Id));
    }

    [Fact]
    public void The_component_unsubscribes_from_the_notifier_when_it_is_disposed()
    {
        // Sin esto el notificador (singleton) acumularía un handler por cada recarga de página.
        Store.Seed(TestData.Message(id: FirstId));
        var component = RenderList();

        component.Instance.DisposeAsync();

        Store.Seed(TestData.Message(id: SecondId, subject: "huérfano"));
        RaiseStoredChange();

        component.WaitForAssertion(
            () => Assert.DoesNotContain("huérfano", component.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Dispara el <c>DateChanged</c> del MudDatePicker indicado. Su input es readonly en el
    /// navegador, así que escribir en él no es una operación real: lo que llega al padre en
    /// producción es el callback, y eso es lo que se ejercita aquí.
    /// </summary>
    private static async Task SetDateAsync(
        IRenderedComponent<MessageListPage> component,
        string label,
        DateTime value)
    {
        var picker = component.FindComponents<MudDatePicker>()
            .First(p => p.Instance.Label == label);

        // El callback se dispara desde el hilo del test, no desde el dispatcher de Blazor.
        await component.InvokeAsync(() => picker.Instance.DateChanged.InvokeAsync(value));
    }

    private void RaiseDeletedChange()
        => Store.Raise(new Core.Storage.MessageStoreChangedEventArgs(
            Core.Storage.MessageStoreChangeKind.Deleted, null));
}
