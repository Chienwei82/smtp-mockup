using Bunit;
using SmtpMockup.Web.Components;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// El borrado nunca es inmediato: la UI pide confirmación y sin ella no se toca el disco
/// (SPEC §9.3). El diálogo es propio, sin JavaScript, justo para que esto sea comprobable.
/// </summary>
public sealed class ConfirmDialogTests : WebTestContext
{
    [Fact]
    public void Nothing_is_shown_while_it_is_not_visible()
    {
        var component = Render<ConfirmDialog>(parameters => parameters.Add(dialog => dialog.Visible, false));

        Assert.Empty(component.FindAll("[role='alertdialog']"));
    }

    [Fact]
    public void It_shows_the_title_the_message_and_both_buttons()
    {
        var component = Render<ConfirmDialog>(parameters => parameters
            .Add(dialog => dialog.Visible, true)
            .Add(dialog => dialog.Title, "Borrar todos")
            .Add(dialog => dialog.Message, "Se borrarán 3 mensajes.")
            .Add(dialog => dialog.ConfirmText, "Borrar"));

        var dialog = component.Find("[role='alertdialog']");

        Assert.Contains("Borrar todos", dialog.TextContent, StringComparison.Ordinal);
        Assert.Contains("Se borrarán 3 mensajes.", dialog.TextContent, StringComparison.Ordinal);
        Assert.Equal("Borrar", component.Find("[data-testid='confirm-accept']").TextContent.Trim());
        Assert.Equal("Cancelar", component.Find("[data-testid='confirm-cancel']").TextContent.Trim());
    }

    [Fact]
    public void Confirming_and_cancelling_raise_their_own_callback()
    {
        var confirmed = 0;
        var cancelled = 0;
        var component = Render<ConfirmDialog>(parameters => parameters
            .Add(dialog => dialog.Visible, true)
            .Add(dialog => dialog.OnConfirm, () => confirmed++)
            .Add(dialog => dialog.OnCancel, () => cancelled++));

        component.Find("[data-testid='confirm-accept']").Click();
        Assert.Equal(1, confirmed);
        Assert.Equal(0, cancelled);

        component.Find("[data-testid='confirm-cancel']").Click();
        Assert.Equal(1, cancelled);
    }
}
