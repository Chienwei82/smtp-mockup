using SmtpMockup.Core.Storage;

namespace SmtpMockup.Web.Services;

/// <summary>
/// Estado del panel de filtros del listado. Es un objeto mutable a propósito: se ata a los
/// campos del formulario con <c>@bind</c> y se convierte a <see cref="MailQuery"/> al buscar.
/// Al ser un record con <c>init</c>, cada cambio de filtro genera uno nuevo y la comparación
/// con el anterior decide si hay que recargar.
/// </summary>
public sealed class MessageFilterModel
{
    /// <summary>Búsqueda libre sobre asunto, remitente y destinatario.</summary>
    public string? Search { get; set; }

    /// <summary>Filtra por remitente.</summary>
    public string? From { get; set; }

    /// <summary>Filtra por destinatario.</summary>
    public string? To { get; set; }

    /// <summary>Filtra por asunto.</summary>
    public string? Subject { get; set; }

    /// <summary>Sólo mensajes recibidos a partir de esta fecha (inclusive).</summary>
    public DateTime? ReceivedAfter { get; set; }

    /// <summary>Sólo mensajes recibidos hasta esta fecha (inclusive).</summary>
    public DateTime? ReceivedBefore { get; set; }

    /// <summary><see langword="true"/> = sólo con adjuntos, <see langword="false"/> = sólo sin ellos.</summary>
    public bool? HasAttachments { get; set; }

    /// <summary>Si hay algún filtro activo, para poder ofrecer el botón "limpiar".</summary>
    public bool HasAnyFilter => !string.IsNullOrWhiteSpace(Search)
        || !string.IsNullOrWhiteSpace(From)
        || !string.IsNullOrWhiteSpace(To)
        || !string.IsNullOrWhiteSpace(Subject)
        || ReceivedAfter.HasValue
        || ReceivedBefore.HasValue
        || HasAttachments.HasValue;

    /// <summary>Deja todos los filtros sin valor.</summary>
    public void Clear()
    {
        Search = null;
        From = null;
        To = null;
        Subject = null;
        ReceivedAfter = null;
        ReceivedBefore = null;
        HasAttachments = null;
    }

    /// <summary>
    /// Convierte el estado del formulario en la consulta del store. Los textos vacíos se
    /// normalizan a <see langword="null"/> para que <c>MailQuery.Matches</c> no los trate como
    /// un filtro de cadena vacía que no coincide con nada.
    /// </summary>
    /// <param name="page">Página 1-based.</param>
    /// <param name="pageSize">Filas por página pedidas.</param>
    public MailQuery ToQuery(int page, int pageSize) => new()
    {
        Search = Normalize(Search),
        From = Normalize(From),
        To = Normalize(To),
        Subject = Normalize(Subject),

        // Un MudDatePicker da una fecha sin hora; "hasta el 10/10" tiene que incluir todo el
        // día, así que ReceivedBefore se lleva al final del día en UTC.
        ReceivedAfter = ReceivedAfter.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(ReceivedAfter.Value, DateTimeKind.Utc))
            : null,
        ReceivedBefore = ReceivedBefore.HasValue
            ? new DateTimeOffset(ReceivedBefore.Value.Date.AddDays(1).AddTicks(-1), TimeSpan.Zero)
            : null,
        HasAttachments = HasAttachments,
        Page = page,
        PageSize = pageSize,
    };

    /// <summary>Copia el estado actual; permite trabajar sobre un borrador sin tocar el activo.</summary>
    public MessageFilterModel Clone() => new()
    {
        Search = Search,
        From = From,
        To = To,
        Subject = Subject,
        ReceivedAfter = ReceivedAfter,
        ReceivedBefore = ReceivedBefore,
        HasAttachments = HasAttachments,
    };

    /// <summary>Un filtro sin nada escrito.</summary>
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}