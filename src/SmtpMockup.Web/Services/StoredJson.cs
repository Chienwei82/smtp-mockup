using System.Text.Json;
using System.Text.Json.Serialization;
using SmtpMockup.Core.Models;

namespace SmtpMockup.Web.Services;

/// <summary>
/// Vuelve a serializar un mensaje con las mismas reglas que el store usa al guardarlo, para que
/// la pestaña "JSON crudo" de la UI muestre el documento tal como está en el archivo.
/// </summary>
public static class StoredJson
{
    /// <summary>
    /// Serializa el mensaje. El formato (camelCase, sin omitir nulos, enums como texto) es el
    /// contrato de la SPEC §7; sólo cambia el sangrado, que es configurable.
    /// </summary>
    /// <param name="message">Mensaje a serializar.</param>
    /// <param name="writeIndented">Valor de <c>Storage:WriteIndented</c>.</param>
    public static string Format(ReceivedMessage message, bool writeIndented)
    {
        ArgumentNullException.ThrowIfNull(message);

        return JsonSerializer.Serialize(message, Options(writeIndented));
    }

    /// <summary>Las opciones de serialización del contrato de disco.</summary>
    internal static JsonSerializerOptions Options(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = writeIndented,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        return options;
    }
}