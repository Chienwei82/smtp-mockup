using System.Text;
using System.Text.RegularExpressions;

namespace SmtpMockup.Web.Services;

/// <summary>
/// Decodifica los <em>encoded words</em> de RFC 2047 (<c>=?UTF-8?B?...?=</c>) para mostrar el
/// asunto en la lista. El valor crudo se conserva en el JSON: esto es sólo presentación, y si
/// algo no se decodifica se devuelve el original (SPEC §7 y §10.3).
/// </summary>
public static partial class SubjectDecoder
{
    /// <summary>Asunto a mostrar cuando el mensaje no tiene ninguno.</summary>
    public const string EmptySubject = "(sin asunto)";

    /// <inheritdoc cref="Decode"/>
    public static string ForDisplay(string? rawSubject)
    {
        if (string.IsNullOrWhiteSpace(rawSubject))
        {
            return EmptySubject;
        }

        var decoded = Decode(rawSubject);
        return decoded.Length == 0 ? EmptySubject : decoded;
    }

    /// <summary>
    /// Sustituye cada <c>=?charset?B|Q?texto?=</c> por su texto plano y compacta los espacios
    /// que quedan entre palabras codificadas.
    /// </summary>
    public static string Decode(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("=?", StringComparison.Ordinal))
        {
            return value ?? string.Empty;
        }

        return EncodedWordRegex().Replace(
            value,
            match => DecodeWord(
                match.Groups["encoding"].Value,
                match.Groups["text"].Value) ?? match.Value);
    }

    /// <summary>Indica si el asunto trae al menos un encoded word sin decodificar.</summary>
    public static bool HasEncodedWords(string? value)
        => !string.IsNullOrEmpty(value) && value.Contains("=?", StringComparison.Ordinal);

    [GeneratedRegex(
        @"=\?(?<charset>[^?]+)\?(?<encoding>[BbQq])\?(?<text>[^?]*)\?=",
        RegexOptions.CultureInvariant)]
    private static partial Regex EncodedWordRegex();

    /// <summary>
    /// Decodifica un encoded word según su esquema. Si el texto no es válido para ese esquema
    /// devuelve <see langword="null"/> y quien llama conserva el original entero: es preferible
    /// mostrar <c>=?utf-8?B?roto?=</c> que perder el asunto o enseñar basura (SPEC §10.3).
    /// </summary>
    private static string? DecodeWord(string encoding, string text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return encoding.ToUpperInvariant() switch
        {
            "B" => TryBase64(text),
            _ => TryQuotedPrintable(text),
        };
    }

    private static string? TryBase64(string text)
    {
        var normalized = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalized.Length == 0 || normalized.Length % 4 != 0)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? TryQuotedPrintable(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character == '_')
            {
                bytes.Add((byte)' ');
                continue;
            }

            if (character != '=')
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(character.ToString()));
                continue;
            }

            if (index + 2 >= text.Length)
            {
                return null;
            }

            var high = HexValue(text[index + 1]);
            var low = HexValue(text[index + 2]);
            if (high < 0 || low < 0)
            {
                // '=' suelto o no hexadecimal: no es quoted-printable válido.
                return null;
            }

            bytes.Add((byte)((high << 4) | low));
            index += 2;
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static int HexValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'A' and <= 'F' => character - 'A' + 10,
        >= 'a' and <= 'f' => character - 'a' + 10,
        _ => -1,
    };
}