using SmtpMockup.Core.Models;

namespace SmtpMockup.Core.Storage;

/// <summary>
/// Filtros y paginación del listado. Todos los filtros son opcionales y se combinan
/// con AND; el orden es <c>receivedAtUtc</c> descendente con desempate por id.
/// </summary>
public sealed record MailQuery
{
    /// <summary>Cantidad máxima de elementos por página.</summary>
    public const int HardMaxPageSize = 1000;

    /// <summary>Búsqueda libre sobre asunto, remitente y destinatarios.</summary>
    public string? Search { get; init; }

    /// <summary>Filtra por remitente (coincidencia parcial, sin distinguir mayúsculas).</summary>
    public string? From { get; init; }

    /// <summary>Filtra por destinatario (coincidencia parcial).</summary>
    public string? To { get; init; }

    /// <summary>Filtra por asunto (coincidencia parcial).</summary>
    public string? Subject { get; init; }

    /// <summary>Sólo mensajes recibidos en este instante o después.</summary>
    public DateTimeOffset? ReceivedAfter { get; init; }

    /// <summary>Sólo mensajes recibidos en este instante o antes.</summary>
    public DateTimeOffset? ReceivedBefore { get; init; }

    /// <summary>Si es <see langword="true"/>, sólo mensajes con adjuntos.</summary>
    public bool? HasAttachments { get; init; }

    /// <summary>Tamaño mínimo del mensaje, en bytes.</summary>
    public long? SizeMinBytes { get; init; }

    /// <summary>Tamaño máximo del mensaje, en bytes.</summary>
    public long? SizeMaxBytes { get; init; }

    /// <summary>Página 1-based. Un valor menor que 1 se trata como 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>
    /// Elementos por página. Se acota a <see cref="HardMaxPageSize"/> para que un
    /// valor absurdo no provoque un barrido de memoria.
    /// </summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// La página normalizada (siempre &gt;= 1).
    /// </summary>
    /// <remarks>
    /// Se acota a <see cref="MaxPage"/> porque el desplazamiento es <c>(página - 1) * tamaño</c>:
    /// sin tope, una página enorme multiplicada por 200 da un entero negativo y
    /// <c>Enumerable.Skip</c> lanza <see cref="ArgumentOutOfRangeException"/> en vez de devolver una
    /// lista vacía. La página viene de la URL, así que el valor llega desde fuera.
    /// </remarks>
    public int NormalizedPage => Page switch
    {
        < 1 => 1,
        > MaxPage => MaxPage,
        _ => Page,
    };

    /// <summary>
    /// Tope de página antes de multiplicar por el tamaño. Está muy por encima de
    /// <see cref="HardMaxPageSize"/> porque el desplazamiento tiene que caber en un <see cref="int"/>.
    /// </summary>
    public const int MaxPage = 1_000_000;

    /// <summary>El tamaño de página acotado al máximo permitido.</summary>
    public int NormalizedPageSize => PageSize switch
    {
        < 1 => 1,
        > HardMaxPageSize => HardMaxPageSize,
        _ => PageSize,
    };

    /// <summary>
    /// Indica si el rango de fechas está invertido. La UI lo muestra como error de
    /// validación visible en vez de devolver silenciosamente cero resultados (SPEC §10.3).
    /// </summary>
    public bool HasInvertedDateRange
        => ReceivedAfter.HasValue && ReceivedBefore.HasValue && ReceivedAfter > ReceivedBefore;

    /// <summary>Evalúa el filtro contra un resumen.</summary>
    public bool Matches(MessageSummary summary)
    {
        if (HasInvertedDateRange)
        {
            return false;
        }

        if (ReceivedAfter.HasValue && summary.ReceivedAtUtc < ReceivedAfter.Value)
        {
            return false;
        }

        if (ReceivedBefore.HasValue && summary.ReceivedAtUtc > ReceivedBefore.Value)
        {
            return false;
        }

        if (HasAttachments.HasValue && summary.HasAttachments != HasAttachments.Value)
        {
            return false;
        }

        if (SizeMinBytes.HasValue && summary.SizeBytes < SizeMinBytes.Value)
        {
            return false;
        }

        if (SizeMaxBytes.HasValue && summary.SizeBytes > SizeMaxBytes.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(From)
            && !Contains(summary.From, From))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(To)
            && !Contains(summary.FirstRecipient, To))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Subject)
            && !Contains(summary.Subject, Subject))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var matchesFreeText =
                Contains(summary.Subject, Search)
                || Contains(summary.From, Search)
                || Contains(summary.FirstRecipient, Search);

            if (!matchesFreeText)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Ordena y pagina un conjunto ya filtrado.</summary>
    public IReadOnlyList<MessageSummary> Apply(IEnumerable<MessageSummary> summaries)
    {
        var ordered = summaries
            .Where(Matches)
            .OrderByDescending(summary => summary.ReceivedAtUtc)
            .ThenBy(summary => summary.Id, StringComparer.Ordinal)
            .ToList();

        var skip = (NormalizedPage - 1) * NormalizedPageSize;

        return ordered
            // El Skip se hace sobre un long acotado: aun con NormalizedPage acotada, el producto
            // tiene que poder representarse, y una lista con menos elementos que el desplazamiento
            // debe devolver vacío, no explotar.
            .Skip((int)Math.Min(skip, ordered.Count))
            .Take(NormalizedPageSize)
            .ToList();
    }

    private static bool Contains(string? candidate, string? needle)
        => candidate is not null
            && needle is not null
            && candidate.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
