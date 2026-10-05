using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;
using SmtpMockup.Web.Services;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// Paginación y validación del listado. Es la lógica que decide qué ve el usuario y qué
/// "borrar todos"BORRARÍA (SPEC §9.3 y §10.3), así que se prueba sin renderizar nada.
/// </summary>
public sealed class MessageListServiceTests
{
    private readonly FakeMessageStore _store = new();
    private readonly WebOptions _options = new();

    private MessageListService CreateService()
        => new(_store, Options.Create(_options));

    private void Seed(int count)
    {
        for (var index = 0; index < count; index++)
        {
            _store.Seed(TestData.Message(
                id: $"01JQ8Z3K7F9A2B3C4D5E6F7G{index:D2}",
                receivedAt: TestData.ReceivedAt.AddMinutes(index),
                subject: $"Mensaje {index}"));
        }
    }

    [Fact]
    public async Task GetPageAsync_returns_the_newest_messages_first()
    {
        Seed(3);

        var result = await CreateService().GetPageAsync(new MailQuery());

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(
            ["Mensaje 2", "Mensaje 1", "Mensaje 0"],
            result.Items.Select(summary => summary.Subject));
    }

    [Fact]
    public async Task GetPageAsync_cuts_the_page_at_the_requested_size()
    {
        Seed(10);
        _options.DefaultPageSize = 10;

        var result = await CreateService().GetPageAsync(new MailQuery { Page = 2, PageSize = 3 });

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(10, result.TotalCount);
        Assert.Equal(4, result.PageCount);
        Assert.True(result.HasPreviousPage);
        Assert.True(result.HasNextPage);
        Assert.Equal(4, result.FirstRowNumber);
        Assert.Equal(6, result.LastRowNumber);
    }

    [Fact]
    public async Task GetPageAsync_steps_back_when_the_requested_page_no_longer_exists()
    {
        // Tras un borrado masivo la página pedida puede quedar más allá del final: mostrar un
        // listado vacío parece un fallo, así que se retrocede a la última con contenido.
        Seed(5);

        var result = await CreateService().GetPageAsync(new MailQuery { Page = 9, PageSize = 2 });

        Assert.Equal(3, result.Page);
        Assert.Single(result.Items);
        Assert.Equal(5, result.TotalCount);
    }

    [Fact]
    public async Task GetPageAsync_reports_an_inverted_date_range_instead_of_returning_nothing()
    {
        Seed(2);

        var result = await CreateService().GetPageAsync(new MailQuery
        {
            ReceivedAfter = TestData.ReceivedAt,
            ReceivedBefore = TestData.ReceivedAt.AddDays(-1),
        });

        Assert.True(result.HasValidationError);
        Assert.Contains("rango de fechas", result.ValidationError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPageAsync_reports_an_inverted_size_range()
    {
        var result = await CreateService().GetPageAsync(new MailQuery { SizeMinBytes = 100, SizeMaxBytes = 10 });

        Assert.True(result.HasValidationError);
    }

    [Fact]
    public async Task GetPageAsync_does_not_query_anything_when_the_page_size_is_invalid()
    {
        Seed(3);
        _options.MaxPageSize = 200;

        var result = await CreateService().GetPageAsync(new MailQuery { PageSize = 5_000 });

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(5_000, result.PageSize);
    }

    [Fact]
    public async Task GetPageAsync_applies_the_filters_of_the_query()
    {
        _store.Seed(TestData.Message(id: TestData.Id, from: "sender@example.com"));
        _store.Seed(TestData.Message(id: TestData.OtherId, subject: "Otro", from: "otro@example.com"));

        var result = await CreateService().GetPageAsync(new MailQuery { From = "OTRO@" });

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(TestData.OtherId, result.Items.Single().Id);
    }

    [Fact]
    public async Task GetPageAsync_returns_an_empty_first_page_when_there_is_nothing_stored()
    {
        var result = await CreateService().GetPageAsync(new MailQuery());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.PageCount);
        Assert.Equal(1, result.Page);
        Assert.False(result.HasNextPage);
        Assert.False(result.HasPreviousPage);
    }

    [Fact]
    public async Task A_page_far_beyond_the_end_falls_back_to_the_last_page_with_content()
    {
        Seed(3);

        var result = await CreateService().GetPageAsync(new MailQuery { Page = 9_999, PageSize = 2 });

        // 3 mensajes con 2 por página son 2 páginas: no se muestra una tercera vacía.
        Assert.Equal(2, result.Page);
        Assert.Single(result.Items);
        Assert.Equal(3, result.LastRowNumber);
    }

    [Fact]
    public void Row_numbers_never_overflow_with_an_extreme_page_from_the_url()
    {
        Seed(3);

        // Con Page = int.MaxValue el producto (page - 1) * pageSize desbordaba a negativo y la UI
        // habría mostrado "mostrando -199 - 0 de 3".
        var result = new MessageListResult([], TotalCount: 3, Page: int.MaxValue, PageSize: 200, ValidationError: null);

        Assert.Equal(3, result.LastRowNumber);
        Assert.True(result.FirstRowNumber >= 0);
    }
}
