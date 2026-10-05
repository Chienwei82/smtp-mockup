namespace SmtpMockup.Core.Hosting;

/// <summary>
/// Resolución de rutas relativas del proceso. <b>Todo</b> lo que la aplicación coloca junto al
/// ejecutable (<c>appsettings.json</c>, <c>data</c>, <c>certs</c>, <c>logs</c>, <c>wwwroot</c>)
/// cuelga del directorio del binario y nunca del directorio de trabajo.
/// </summary>
/// <remarks>
/// <para>
/// Es la diferencia entre que un Windows Service funcione y no funcione: el SCM arranca el
/// proceso con <c>C:\Windows\System32</c> como directorio de trabajo, así que una ruta relativa
/// como <c>data/messages</c> resolvería a <c>C:\Windows\System32\data\messages</c> — o fallaría
/// por permisos — y los correos desaparecerían en el reinicio. Un contenedor arranca con <c>/</c>
/// y tiene el mismo problema.
/// </para>
/// <para>
/// <see cref="AppContext.BaseDirectory"/> es el ancla correcta: es la carpeta que contiene el
/// ensamblado, que es justamente lo que uno copia junto al <c>.exe</c>.
/// </para>
/// </remarks>
public static class HostPath
{
    /// <summary>
    /// Directorio del ejecutable, con barra final. Es el ancla de todas las rutas relativas.
    /// </summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// Resuelve una ruta de configuración a una ruta absoluta.
    /// </summary>
    /// <remarks>
    /// Una ruta vacía o nula se devuelve como cadena vacía (para que el llamador pueda distinguir
    /// "sin ruta" de "la raíz"); una ruta con raíz se normaliza tal cual; y una ruta relativa se
    /// combina con <see cref="BaseDirectory"/>.
    /// </remarks>
    /// <param name="path">Ruta de configuración, relativa o absoluta.</param>
    /// <returns>Ruta absoluta normalizada.</returns>
    public static string Resolve(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(BaseDirectory, path));

    /// <summary>
    /// Combina un segmento de nombre (o de ruta relativa) con el ancla del ejecutable.
    /// </summary>
    /// <param name="parts">Segmentos a combinar.</param>
    /// <returns>Ruta absoluta normalizada.</returns>
    public static string Combine(params string[] parts)
        => Resolve(Path.Combine(parts));
}