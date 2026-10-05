using SmtpMockup.Core.Options;

namespace SmtpMockup.Core.Tests;

/// <summary>
/// Composición y clasificación de direcciones de binding (D-1, D-2). El caso que más importa es
/// el de IPv6: sin las corchetes, <c>http://::1:8080</c> es una autoridad ambigua y Kestrel no
/// arranca, así que estos tests fijan la diferencia entre las dos formas.
/// </summary>
public sealed class EndpointAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.53", true)]
    [InlineData("::1", true)]
    [InlineData("localhost", true)]
    [InlineData("LocalHost", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("::", false)]
    // ::ffff:127.0.0.1 es loopback mapeado a IPv4: sigue siendo la máquina local, así que se
    // clasifica como loopback. Tratarlo como expuesto sería el error que más cuesta.
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not an address", false)]
    public void IsLoopback_only_for_loopback(string? bindAddress, bool expected)
        => Assert.Equal(expected, EndpointAddress.IsLoopback(bindAddress));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("192.168.0.1", true)]
    // 'localhost' es un nombre, no un literal: sirve como URL para swaks pero no puede
    // bindearse, así que la validación lo rechaza y obliga a escribir la dirección.
    [InlineData("localhost", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("999.1.1.1", false)]
    public void IsValidLiteral_only_for_ip_literals(string? bindAddress, bool expected)
        => Assert.Equal(expected, EndpointAddress.IsValidLiteral(bindAddress));

    [Theory]
    [InlineData("127.0.0.1", 8080, "127.0.0.1:8080")]
    [InlineData("0.0.0.0", 25, "0.0.0.0:25")]
    // El motivo de que esta función exista: sin corchetes, '::1:8080' se lee como un host y un
    // puerto que no cuadran.
    [InlineData("::1", 8080, "[::1]:8080")]
    [InlineData("::", 8443, "[::]:8443")]
    public void FormatHostPort_brackets_ipv6_literals(string bindAddress, int port, string expected)
        => Assert.Equal(expected, EndpointAddress.FormatHostPort(bindAddress, port));

    [Theory]
    [InlineData("127.0.0.1", 8080, "http://127.0.0.1:8080")]
    [InlineData("::1", 8080, "http://[::1]:8080")]
    public void FormatHttpUrl_is_parsable_by_system_uri(string bindAddress, int port, string expected)
    {
        var url = EndpointAddress.FormatHttpUrl(bindAddress, port);

        Assert.Equal(expected, url);
        // System.Uri es el criterio: si esto falla, Kestrel no puede entender la URL.
        var uri = new Uri(url);
        Assert.Equal(port, uri.Port);
        Assert.Equal(expected.Contains('['), uri.Host.Contains(':', StringComparison.Ordinal));
    }
}
