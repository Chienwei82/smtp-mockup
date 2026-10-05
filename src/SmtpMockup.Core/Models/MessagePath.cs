using System.Globalization;

namespace SmtpMockup.Core.Models;

/// <summary>
/// Traduce entre un id ULID y su ruta en disco, particionada por día (SPEC §8):
/// <c>data/messages/2026/02/10/01JQ8Z3K7F9A2B3C4D5E6F7G8H.json</c>.
/// </summary>
public static class MessagePath
{
    /// <summary>Extensión del archivo JSON.</summary>
    public const string FileExtension = ".json";

    /// <summary>Extensión temporal antes del rename atómico.</summary>
    public const string TempExtension = ".json.tmp";

    /// <summary>Valida el id y devuelve su ruta completa bajo <paramref name="rootDirectory"/>.</summary>
    /// <param name="rootDirectory">Directorio raíz configurado en <c>Storage:Directory</c>.</param>
    /// <param name="id">Id ULID del mensaje.</param>
    /// <param name="receivedAtUtc">Fecha que determina las carpetas de partición.</param>
    /// <exception cref="ArgumentException">
    /// El id no es un ULID válido, o la ruta resultante se sale del directorio raíz.
    /// </exception>
    public static string For(string rootDirectory, string id, DateTimeOffset receivedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ThrowIfInvalid(id);

        var utc = receivedAtUtc.UtcDateTime;
        var relative = Path.Combine(
            utc.ToString("yyyy", CultureInfo.InvariantCulture),
            utc.ToString("MM", CultureInfo.InvariantCulture),
            utc.ToString("dd", CultureInfo.InvariantCulture),
            id + FileExtension);

        return EnsureInsideRoot(rootDirectory, relative);
    }

    /// <summary>
    /// Reconstruye la ruta de un id a partir de los dígitos del propio ULID, sin
    /// necesitar la fecha de recepción. Útil al leer o borrar un mensaje por id.
    /// </summary>
    /// <exception cref="ArgumentException">El id no es un ULID válido.</exception>
    public static string For(string rootDirectory, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ThrowIfInvalid(id);

        var milliseconds = DecodeTimestamp(id);
        var received = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

        return For(rootDirectory, id, received);
    }

    /// <summary>Devuelve la ruta del temporal <c>&lt;id&gt;.json.tmp</c> usada antes del rename.</summary>
    public static string TempFor(string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        return finalPath + ".tmp";
    }

    /// <summary>
    /// Indica si el nombre de un archivo corresponde a un JSON de mensaje válido.
    /// </summary>
    /// <remarks>
    /// El índice ignora lo que no cumple el patrón (SPEC §10.2): un <c>.json.tmp</c> a medio
    /// escribir, un <c>.bak</c> de una copia de seguridad o un archivo arbitrario soltado en el
    /// directorio no pueden aparecer en el listado ni entrar en un borrado masivo.
    /// </remarks>
    /// <param name="fileName">Nombre de archivo, sin directorio.</param>
    public static bool IsMessageFileName(string? fileName)
        => fileName is not null
            && fileName.Length == MessageId.Length + FileExtension.Length
            && fileName.EndsWith(FileExtension, StringComparison.Ordinal)
            && MessageId.IsValid(fileName[..MessageId.Length]);

    /// <summary>
    /// Comprueba que el id sea un ULID válido, con un mensaje de error explícito.
    /// Se usa antes de leer o borrar, para impedir path traversal (SPEC §8).
    /// </summary>
    /// <exception cref="ArgumentException">El id no es válido.</exception>
    public static void Validate(string id) => ThrowIfInvalid(id);

    /// <summary>Lanza si el id no es un ULID válido (SPEC §8).</summary>
    /// <exception cref="ArgumentException">El id no es válido.</exception>
    private static void ThrowIfInvalid(string id)
    {
        if (!MessageId.IsValid(id))
        {
            throw new ArgumentException(
                $"'{id}' is not a valid message id; expected 26 Crockford base32 characters.",
                nameof(id));
        }
    }

    /// <summary>
    /// Lee los 10 primeros caracteres del ULID y devuelve el timestamp Unix en
    /// milisegundos que codifican.
    /// </summary>
    public static long DecodeTimestamp(string id)
    {
        Validate(id);

        long value = 0;
        for (var index = 0; index < 10; index++)
        {
            value = (value << 5) | DecodeDigit(id[index]);
        }

        return value;
    }

    /// <summary>
    /// Garantiza que <paramref name="relativePath"/> no escape de
    /// <paramref name="rootDirectory"/>. defense-in-depth: el id ya se validó como
    /// ULID, así que no debería poder conter separadores.
    /// </summary>
    private static string EnsureInsideRoot(string rootDirectory, string relativePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The resolved path '{full}' escapes the storage root '{root}'.",
                nameof(relativePath));
        }

        return full;
    }

    /// <summary>Decodifica un dígito Crockford base32 (I, L, O y U equivalen a 1).</summary>
    private static long DecodeDigit(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'A' and <= 'H' => character - 'A' + 10,
        >= 'J' and <= 'K' => character - 'J' + 18,
        >= 'M' and <= 'N' => character - 'M' + 20,
        >= 'P' and <= 'T' => character - 'P' + 22,
        >= 'V' and <= 'Z' => character - 'V' + 27,
        _ => throw new ArgumentException(
            $"'{character}' is not a valid Crockford base32 digit.", nameof(character)),
    };
}
