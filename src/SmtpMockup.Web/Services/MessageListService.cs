using Microsoft.Extensions.Options;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Web.Services;

/// <summary>
/// Una página del listado ya resuelta: filas, total filtrado y estado de paginación.
/// </summary>
/// <param name="Items">Filas de la página, de la más reciente a la más antigua.</param>
/// <param name="TotalCount">Mensajes que cumplen el filtro, en toda la colección.</param>
/// <param name="Page">Página mostrada (siempre &gt;= 1).</param>
/// <param name="PageSize">Filas por página efectivamente usadas.</param>
/// <param name="ValidationError">
/// Aviso a mostrar en la UI (tamaño de página inválido, rango de fechas invertido). El
/// resultado sigue siendo usable: la UI enseña el error y el listado vacío, no una excepción.
/// </param>
public sealed record MessageListResult(
    IReadOnlyList<MessageSummary> Items,
    int TotalCount,
    int Page,
    int PageSize,
    string? ValidationError)
{
    /// <summary>Un resultado sin filas ni error.</summary>
    public static MessageListResult Empty { get; } = new([], 0, 1, 50, null);

    /// <summary>Indica si la UI debe mostrar un aviso de validación.</summary>
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    /// <summary>Cantidad de páginas del filtro actual; 0 si no hay resultados.</summary>
    public int PageCount => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    /// <summary>Si hay una página anterior a la actual.</summary>
    public bool HasPreviousPage => Page > 1;

    /// <summary>Si hay una página posterior a la actual.</summary>
    public bool HasNextPage => Page < PageCount;

    /// <summary>Número de la primera fila mostrada (0 si la lista está vacía).</summary>
    public int FirstRowNumber => TotalCount == 0 ? 0 : (int)Math.Min(((long)Page - 1) * PageSize, TotalCount) + 1;

    /// <summary>
    /// Número de la última fila mostrada.
    /// </summary>
    /// <remarks>
    /// El producto se calcula en <see cref="long"/>: con <c>Page</c> traída de la URL, el
    /// <c>int</c> podría desbordarse a negativo y la UI mostraría un rango como "-3 - 0 de 12".
    /// </remarks>
    public int LastRowNumber
        => TotalCount == 0 ? 0 : (int)Math.Min((long)Page * PageSize, TotalCount);
}

/// <summary>
/// Resuelve la página del listado aplicando el filtro, la paginación y las reglas de validación
/// de la UI (SPEC §9.3 y §10.3). Vive en un servicio —y no en el componente— para que la
/// lógica de paginación se pruebe sin renderizar nada.
/// </summary>
public sealed class MessageListService(IMessageStore store, IOptions<WebOptions> webOptions)
{
    private readonly WebOptions _webOptions = webOptions.Value;

    /// <summary>El tamaño de página por defecto configurado en <c>Web:DefaultPageSize</c>.</summary>
    public int DefaultPageSize => _webOptions.DefaultPageSize;

    /// <summary>El tope de filas por página (<c>Web:MaxPageSize</c>).</summary>
    public int MaxPageSize => _webOptions.MaxPageSize;

    /// <summary>
    /// Devuelve la página pedida. Si la página pedida queda más allá del último resultado
    /// (por ejemplo, tras un borrado masivo) se retrocede a la última con contenido en vez de
    /// mostrar un listado vacío que parece un fallo.
    /// </summary>
    /// <param name="query">Filtro y paginación solicitados.</param>
    /// <param name="cancellationToken">Token del circuito de Blazor.</param>
    public async Task<MessageListResult> GetPageAsync(
        MailQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var pageSize = query.PageSize;
        var validationError = Validate(query);

        if (pageSize < 1 || pageSize > _webOptions.MaxPageSize)
        {
            // Con un pageSize inválido no se consulta nada: aplicar 1000 filas para después
            // descartar el resultado sería el peor de los dos mundos.
            return new MessageListResult([], 0, Math.Max(query.Page, 1), pageSize, validationError);
        }

        var totalCount = await store.CountAsync(query, cancellationToken).ConfigureAwait(false);
        var pageCount = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        var page = pageCount == 0 ? 1 : Math.Clamp(query.Page, 1, pageCount);

        var paged = page == query.Page
            ? query
            : query with { Page = page };

        var items = await store.QueryAsync(paged, cancellationToken).ConfigureAwait(false);

        return new MessageListResult(items, totalCount, page, pageSize, validationError);
    }

    /// <summary>
    /// Devuelve el mensaje de validación de la consulta, o <see langword="null"/> si es válida.
    /// El rango de fechas invertido se reporta en vez de devolver cero resultados en silencio.
    /// </summary>
    private string? Validate(MailQuery query)
    {
        if (query.HasInvertedDateRange)
        {
            return "'Desde' es posterior a 'Hasta': corrige el rango de fechas.";
        }

        if (query.SizeMinBytes.HasValue
            && query.SizeMaxBytes.HasValue
            && query.SizeMinBytes > query.SizeMaxBytes)
        {
            return "El tamaño mínimo es mayor que el máximo.";
        }

        return null;
    }
}