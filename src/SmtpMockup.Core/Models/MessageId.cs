using System.Security.Cryptography;

namespace SmtpMockup.Core.Models;

/// <summary>
/// Identificador ULID de un mensaje recibido: 10 caracteres de timestamp en milisegundos
/// + 16 aleatorios, base32 Crockford en mayúsculas (SPEC §8). El orden lexicográfico
/// coincide con el orden de recepción, lo que permite listar por nombre de archivo.
/// </summary>
public readonly record struct MessageId
{
    /// <summary>Longitud exacta de un ULID en caracteres.</summary>
    public const int Length = 26;

    /// <summary>Patrón de validación; Crockford base32 excluye I, L, O y U.</summary>
    private const string Pattern = "^[0-9A-HJKMNP-TV-Z]{26}$";

    private static readonly char[] Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

    private readonly string? _value;

    /// <summary>Crea un id a partir de su texto. Lanza si no es un ULID válido.</summary>
    /// <exception cref="ArgumentException">El texto no cumple el patrón ULID.</exception>
    public MessageId(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid ULID message id (26 Crockford base32 characters).",
                nameof(value));
        }

        _value = value;
    }

    /// <summary>El id como texto; cadena vacía si el id por defecto no fue inicializado.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Indica si el id fue inicializado con un ULID válido.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>Genera un ULID nuevo a partir del reloj actual.</summary>
    /// <param name="timeProvider">Reloj a usar; permite tests deterministas.</param>
    public static MessageId New(TimeProvider? timeProvider = null)
    {
        var clock = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return New(clock.UtcDateTime);
    }

    /// <summary>
    /// Genera un ULID para un instante dado. La parte aleatoria usa
    /// <see cref="RandomNumberGenerator"/> para que dos correos del mismo milisegundo
    /// no colisionen.
    /// </summary>
    public static MessageId New(DateTime utcDateTime)
    {
        var milliseconds = new DateTimeOffset(
            DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utcDateTime), utcDateTime, "ULID timestamps cannot precede the Unix epoch.");
        }

        Span<char> buffer = stackalloc char[Length];
        var timestamp = (ulong)milliseconds;

        // Los 10 primeros caracteres llevan el timestamp en base32, big-endian.
        for (var index = 9; index >= 0; index--)
        {
            buffer[index] = Alphabet[(int)(timestamp & 0x1F)];
            timestamp >>= 5;
        }

        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);

        for (var index = 0; index < 16; index++)
        {
            buffer[10 + index] = Alphabet[random[index] & 0x1F];
        }

        return new MessageId(new string(buffer));
    }

    /// <summary>Indica si el texto tiene la forma de un ULID (no valida el timestamp).</summary>
    public static bool IsValid(string? value)
        => value is { Length: Length } && System.Text.RegularExpressions.Regex.IsMatch(value, Pattern);

    /// <summary>
    /// Intenta crear un id sin lanzar. Devuelve <see langword="false"/> si el texto no es válido.
    /// </summary>
    public static bool TryParse(string? value, out MessageId id)
    {
        if (IsValid(value))
        {
            id = new MessageId(value!);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>El id como texto.</summary>
    public override string ToString() => Value;
}
