using SmtpMockup.Web.Services;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// El panel de filtros es la puerta de entrada al listado: de aquí sale el
/// <see cref="Core.Storage.MailQuery"/> que decide qué se ve y qué se borra en masa.
/// </summary>
public sealed class MessageFilterModelTests
{
    [Fact]
    public void ToQuery_normalizes_blank_text_to_null()
    {
        var filter = new MessageFilterModel { Search = "   ", From = string.Empty };

        var query = filter.ToQuery(page: 1, pageSize: 50);

        Assert.Null(query.Search);
        Assert.Null(query.From);
    }

    [Fact]
    public void ToQuery_trims_the_text_it_keeps()
    {
        var filter = new MessageFilterModel { Subject = "  informe  " };

        Assert.Equal("informe", filter.ToQuery(1, 50).Subject);
    }

    [Fact]
    public void ToQuery_carries_the_pagination_asked_for()
    {
        var query = new MessageFilterModel().ToQuery(page: 3, pageSize: 25);

        Assert.Equal(3, query.Page);
        Assert.Equal(25, query.PageSize);
    }

    [Fact]
    public void ToQuery_makes_the_end_of_the_range_include_the_whole_day()
    {
        var filter = new MessageFilterModel { ReceivedBefore = new DateTime(2026, 2, 10) };

        var query = filter.ToQuery(1, 50);

        Assert.Equal(
            new DateTimeOffset(new DateTime(2026, 2, 10, 23, 59, 59, 999).AddTicks(9_999), TimeSpan.Zero),
            query.ReceivedBefore);
    }

    [Fact]
    public void ToQuery_treats_the_start_of_the_range_as_the_beginning_of_that_day()
    {
        var filter = new MessageFilterModel { ReceivedAfter = new DateTime(2026, 2, 10) };

        Assert.Equal(
            new DateTimeOffset(new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc)),
            filter.ToQuery(1, 50).ReceivedAfter);
    }

    [Fact]
    public void HasAnyFilter_is_false_for_a_clean_form()
    {
        Assert.False(new MessageFilterModel().HasAnyFilter);
        Assert.False(new MessageFilterModel { Search = "  ", HasAttachments = null }.HasAnyFilter);
    }

    [Fact]
    public void HasAnyFilter_is_true_as_soon_as_one_criterion_is_set()
    {
        Assert.True(new MessageFilterModel { HasAttachments = false }.HasAnyFilter);
        Assert.True(new MessageFilterModel { ReceivedBefore = DateTime.UtcNow }.HasAnyFilter);
    }

    [Fact]
    public void Clear_removes_every_criterion()
    {
        var filter = new MessageFilterModel
        {
            Search = "x",
            From = "a@b",
            To = "c@d",
            Subject = "s",
            ReceivedAfter = DateTime.UtcNow,
            ReceivedBefore = DateTime.UtcNow,
            HasAttachments = true,
        };

        filter.Clear();

        Assert.False(filter.HasAnyFilter);
    }

    [Fact]
    public void Clone_copies_every_criterion_without_sharing_state()
    {
        var filter = new MessageFilterModel { Search = "original" };

        var clone = filter.Clone();
        clone.Search = "otro";

        Assert.Equal("original", filter.Search);
        Assert.Equal("otro", clone.Search);
    }
}
