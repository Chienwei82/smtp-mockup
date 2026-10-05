using SmtpMockup.Core.Models;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Core.Tests;

/// <summary>
/// Paginación de <see cref="MailQuery"/> contra valores hostiles que llegan de la URL.
/// </summary>
/// <remarks>
/// El desplazamiento es <c>(página - 1) * tamaño</c>. Sin acotar la página, un
/// <c>?page=2147483647</c> multiplicado por 200 desborda el <see cref="int"/>, queda negativo y
/// <c>Enumerable.Skip</c> lanza <see cref="ArgumentOutOfRangeException"/>: un 500 por un parámetro de
/// navegación, no una lista vacía.
/// </remarks>
public sealed class MailQueryPaginationTests
{
    private static readonly MessageSummary[] ThreeMessages =
    [
        Summary("01M44BPCT98JEECWFVYCV2EC0D", 3),
        Summary("01M44BPCZ4DNZHMSQK9TQZQSRZ", 2),
        Summary("01M44BPYTJCW4CNM0RE49KWDQP", 1),
    ];

    [Fact]
    public void Page_is_clamped_instead_of_overflowing()
    {
        var query = new MailQuery { Page = int.MaxValue, PageSize = 50 };

        Assert.Equal(MailQuery.MaxPage, query.NormalizedPage);

        // Lo importante: devuelve vacío en vez de lanzar.
        Assert.Empty(query.Apply(ThreeMessages));
    }

    [Fact]
    public void A_huge_page_beyond_the_clamp_still_returns_an_empty_page()
    {
        var query = new MailQuery { Page = int.MaxValue / 2, PageSize = MailQuery.HardMaxPageSize };

        var result = query.Apply(ThreeMessages);

        Assert.Empty(result);
    }

    [Fact]
    public void A_negative_page_falls_back_to_the_first_one()
        => Assert.Equal(1, new MailQuery { Page = -5 }.NormalizedPage);

    [Fact]
    public void Page_size_is_clamped_to_the_hard_maximum()
        => Assert.Equal(MailQuery.HardMaxPageSize, new MailQuery { PageSize = 10_000 }.NormalizedPageSize);

    [Fact]
    public void A_negative_page_size_becomes_one()
        => Assert.Equal(1, new MailQuery { PageSize = 0 }.NormalizedPageSize);

    [Fact]
    public void The_clamped_page_still_paginates_real_results()
    {
        // La primera fila de la última página, con la página ya normalizada.
        var query = new MailQuery { Page = MailQuery.MaxPage, PageSize = 1 };

        Assert.Empty(query.Apply(ThreeMessages));
        Assert.Single(new MailQuery { Page = 3, PageSize = 1 }.Apply(ThreeMessages));
    }

    [Fact]
    public void Ordering_is_newest_first_with_the_id_as_tiebreaker()
    {
        var sameInstant = new[]
        {
            Summary("01M44BPCT98JEECWFVYCV2EC0D", 5),
            Summary("01M44BPCZ4DNZHMSQK9TQZQSRZ", 5),
        };

        // Desempate por id ascendente: 'T' (0x54) va antes que 'Z' (0x5A).
        var ordered = new MailQuery().Apply(sameInstant).Select(summary => summary.Id);

        Assert.Equal(["01M44BPCT98JEECWFVYCV2EC0D", "01M44BPCZ4DNZHMSQK9TQZQSRZ"], ordered);
    }

    private static MessageSummary Summary(string id, int minute) => new(
        id,
        new DateTimeOffset(2026, 10, 4, 12, minute, 0, TimeSpan.Zero),
        $"asunto {minute}",
        SizeBytes: 100,
        AttachmentCount: 0,
        HasAttachments: false,
        RemoteIp: "127.0.0.1",
        MailTransport.Plain,
        From: "sender@example.com",
        FirstRecipient: "to@example.com");
}