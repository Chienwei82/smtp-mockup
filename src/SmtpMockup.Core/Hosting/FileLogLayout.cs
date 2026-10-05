using SmtpMockup.Core.Options;

namespace SmtpMockup.Core.Hosting;

/// <summary>
/// Nombres y rutas de los archivos de log del modo servicio. Es lógica <b> pura</b> (no escribe
/// nada) para poder testearla sin un Windows Service ni permisos de escritura.
/// </summary>
public static class FileLogLayout
{
    /// <summary>Marcador que se sustituye por la fecha en <see cref="HostingOptions.LogFileName"/>.</summary>
    public const string DateToken = "{Date}";

    /// <summary>
    /// Ruta absoluta del archivo de log del día indicado. Una carpeta relativa cuelga del
    /// directorio del ejecutable, no del directorio de trabajo.
    /// </summary>
    /// <param name="options">Opciones de <c>Hosting:*</c>.</param>
    /// <param name="date">Día cuyo nombre va dentro del archivo.</param>
    /// <returns>Ruta absoluta, o cadena vacía si el log en archivo está desactivado.</returns>
    public static string ResolveFile(HostingOptions options, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.LogDirectory) || string.IsNullOrWhiteSpace(options.LogFileName))
        {
            return string.Empty;
        }

        var fileName = options.LogFileName.Replace(DateToken, date.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
        return HostPath.Combine(options.LogDirectory, fileName);
    }

    /// <summary>
    /// Qué archivos de log se pueden borrar por retención. No borra nada: devuelve la lista para
    /// que el llamador decida, lo que mantiene el método testeable y el borrado explícito.
    /// </summary>
    /// <remarks>
    /// Se comparan las <b>fechas del nombre</b> y no la fecha de modificación: un log de hace tres
    /// días que acaba de ser tocado (un servicio que se reinicia) no debe desaparecer.
    /// </remarks>
    /// <param name="options">Opciones de <c>Hosting:*</c>.</param>
    /// <param name="existingFiles">Rutas de los archivos presentes en la carpeta de logs.</param>
    /// <param name="today">Fecha de referencia.</param>
    /// <returns>Las rutas a borrar, ordenadas.</returns>
    public static IReadOnlyList<string> SelectExpired(
        HostingOptions options,
        IEnumerable<string> existingFiles,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(existingFiles);

        if (options.LogRetentionDays <= 0 || string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            return [];
        }

        var directory = HostPath.Resolve(options.LogDirectory);
        var prefix = PrefixOf(options.LogFileName);
        var cutoff = today.AddDays(-options.LogRetentionDays);
        var expired = new List<string>();

        foreach (var file in existingFiles)
        {
            var name = Path.GetFileName(file);

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !TryReadDate(name, prefix, out var date)
                || date >= cutoff)
            {
                continue;
            }

            var fullPath = Path.GetFullPath(file);

            // Nunca se sale de la carpeta de logs, aunque la ruta recibida sea rara.
            if (Path.GetDirectoryName(fullPath)?.Equals(directory, StringComparison.OrdinalIgnoreCase) == true)
            {
                expired.Add(fullPath);
            }
        }

        expired.Sort(StringComparer.OrdinalIgnoreCase);
        return expired;
    }

    /// <summary>Prefijo común antes de <see cref="DateToken"/>, usado para reconocer nuestros archivos.</summary>
    private static string PrefixOf(string? fileName)
        => string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : fileName.Split(DateToken, 2, StringSplitOptions.None)[0];

    /// <summary>Lee la fecha del nombre de archivo <c>prefijo-yyyy-MM-dd.log</c>.</summary>
    private static bool TryReadDate(string fileName, string prefix, out DateOnly date)
    {
        date = default;

        if (prefix.Length == 0)
        {
            return false;
        }

        var tail = fileName[prefix.Length..];

        // yyyy-MM-dd al principio del sufijo; el resto es la extensión.
        if (tail.Length < 10
            || !DateOnly.TryParseExact(tail[..10], "yyyy-MM-dd", out date))
        {
            return false;
        }

        return true;
    }
}