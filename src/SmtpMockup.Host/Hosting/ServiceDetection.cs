using Microsoft.Extensions.Hosting.WindowsServices;

namespace SmtpMockup.Host.Hosting;

/// <summary>
/// Detecta si el proceso fue arrancado por el Service Control Manager de Windows.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WindowsServiceHelpers.IsWindowsService"/> funciona inspeccionando el nombre del
/// proceso padre, así que en Linux devolvería <see langword="false"/> pero es una API de Windows:
/// aislarla aquí evita que el resto del Host tenga que compilar condicionalmente y deja el
/// punto de sustitución (una detección real) en un solo archivo.
/// </para>
/// <para>
/// La detección es heurística por diseño: un proceso de consola lanzado desde una terminal que se
/// llamara <c>services.exe</c> sería tomado por un servicio. Es el mismo compromiso que asume la
/// propia API de Microsoft, y no tiene consecuencias aquí porque el único efecto es elegir el
/// destino del log.
/// </para>
/// </remarks>
public static class ServiceDetection
{
    /// <summary>
    /// Si el SCM de Windows arrancó este proceso. Siempre <see langword="false"/> fuera de Windows.
    /// </summary>
    public static bool IsWindowsService()
        => OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();

    /// <summary>Si el proceso corre en Windows.</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();
}