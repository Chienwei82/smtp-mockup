using System.Net;
using System.Net.Sockets;

namespace SmtpMockup.Core.Options;

/// <summary>
/// Composición y clasificación de las direcciones de binding de la aplicación.
/// </summary>
/// <remarks>
/// Existe como función pura y testeable, y no repartida por los validadores y el composition
/// root, por un motivo concreto: la decisión «esta dirección es de loopback o no» y la regla
/// «así se escribe una URL con esta dirección» tienen que dar <em>la misma</em> respuesta en el
/// log de arranque, en el binding de Kestrel y en el texto de la UI. Con tres copias, un
/// <c>::1</c> produce una URL inválida en uno de los tres y nadie se entera hasta que el host no
/// arranca.
/// </remarks>
public static class EndpointAddress
{
    /// <summary>
    /// Indica si la dirección es de loopback. Vacía o no parseable ⇒ <see langword="false"/>,
    /// que es el peor caso a efectos de seguridad: lo que no se entiende se trata como expuesto.
    /// </summary>
    /// <param name="bindAddress">Dirección de binding.</param>
    public static bool IsLoopback(string? bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
        {
            return false;
        }

        if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(bindAddress, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Comprueba que la cadena sea un literal IPv4 o IPv6 utilizable como dirección de binding.
    /// </summary>
    /// <param name="bindAddress">Dirección de binding.</param>
    public static bool IsValidLiteral(string? bindAddress)
        => !string.IsNullOrWhiteSpace(bindAddress) && IPAddress.TryParse(bindAddress, out _);

    /// <summary>
    /// Mensaje de error de una dirección inválida, compartido por los validadores de
    /// <c>Web:*</c> y de <c>Smtp:*</c>. El <paramref name="key"/> es el prefijo de configuración
    /// (<c>Web:BindAddress</c>, <c>Smtp:Plain:BindAddress</c>); sin él se nombra la clave a secas.
    /// </summary>
    /// <param name="value">Valor leído de la configuración.</param>
    /// <param name="key">Prefijo de la clave, si se conoce.</param>
    public static string InvalidLiteralMessage(string? value, string? key = null)
        => $"'{key}:BindAddress' must be a valid IP address (current value: '{value}'). "
            + "Use an IPv4 or IPv6 literal such as '127.0.0.1' or '::1'.";

    /// <summary>
    /// Escribe <c>host:port</c> entre corchetes si el host es un literal IPv6.
    /// </summary>
    /// <remarks>
    /// Sin esto, <c>http://::1:8080</c> es una autoridad ambigua —el parser lee el puerto
    /// <c>8080</c> y el host <c>::1</c> como dos cosas distintas y Kestrel falla al arrancar—,
    /// mientras que <c>http://[::1]:8080</c> es la URL correcta. Un literal IPv6 en
    /// <c>BindAddress</c> es válido y lo valida el validador, así que la URL tiene que salir bien.
    /// </remarks>
    /// <param name="bindAddress">Dirección de binding.</param>
    /// <param name="port">Puerto TCP.</param>
    public static string FormatHostPort(string? bindAddress, int port)
        => IPAddress.TryParse(bindAddress, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{bindAddress}]:{port}"
            : $"{bindAddress}:{port}";

    /// <summary>
    /// URL HTTP de la UI para la dirección y el puerto dados.
    /// </summary>
    /// <param name="bindAddress">Dirección de binding.</param>
    /// <param name="port">Puerto TCP.</param>
    public static string FormatHttpUrl(string? bindAddress, int port)
        => $"http://{FormatHostPort(bindAddress, port)}";
}
