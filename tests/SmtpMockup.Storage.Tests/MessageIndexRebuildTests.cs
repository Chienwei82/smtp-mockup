using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Options;
using SmtpMockup.Storage;

namespace SmtpMockup.Storage.Tests;

/// <summary>
/// Qué entra en el índice cuando se reconstruye. La UI lo consulta para pintar el listado y para
/// decidir qué borra un «borrar todos», así que cualquier archivo ajeno que se colara aparecería
/// como un correo más y sería borrable por error (SPEC §10.2).
/// </summary>
public sealed class MessageIndexRebuildTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "smtp-mockup-index-tests", Guid.NewGuid().ToString("N"));

    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Saved_messages_come_back_after_a_rebuild()
    {
        using var store = CreateStore();
        await store.SaveAsync(Message("01JQ8Z3K7F9A2B3C4D5E6F7G01", "hola"));

        var indexed = await store.RebuildIndexAsync();

        Assert.Equal(1, indexed);
        Assert.Equal(1, await store.CountAsync());
    }

    [Fact]
    public async Task Temporary_files_and_foreign_names_are_ignored()
    {
        using var store = CreateStore();
        await store.SaveAsync(Message("01JQ8Z3K7F9A2B3C4D5E6F7G01", "hola"));

        var dayDirectory = Directory.GetDirectories(_directory, "*", SearchOption.AllDirectories)
            .First();

        // Todo lo que un usuario (o una copia de seguridad) puede dejar en la carpeta.
        await File.WriteAllTextAsync(Path.Combine(dayDirectory, "01JQ8Z3K7F9A2B3C4D5E6F7G01.json.tmp"), "{}");
        await File.WriteAllTextAsync(Path.Combine(dayDirectory, "notas.txt.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(dayDirectory, "01JQ8Z3K7F9A2B3C4D5E6F7G01.json.bak"), "{}");
        await File.WriteAllTextAsync(Path.Combine(dayDirectory, "leeme.md"), "# hola");

        var indexed = await store.RebuildIndexAsync();

        Assert.Equal(1, indexed);
    }

    [Fact]
    public async Task A_deleted_file_disappears_from_the_index_after_a_rebuild()
    {
        using var store = CreateStore();
        await store.SaveAsync(Message("01JQ8Z3K7F9A2B3C4D5E6F7G01", "hola"));
        await store.SaveAsync(Message("01JQ8Z3K7F9A2B3C4D5E6F7G02", "adios"));

        // Es lo que hace el vigilante del directorio cuando alguien borra un JSON a mano: sin
        // rebuild, la fila se quedaría colgada en el navegador. El archivo se localiza por
        // nombre porque las carpetas del día salen de ReceivedAtUtc, no del timestamp del ULID.
        var path = Directory.GetFiles(_directory, "01JQ8Z3K7F9A2B3C4D5E6F7G01.json", SearchOption.AllDirectories)
            .Single();
        File.Delete(path);
        await store.RebuildIndexAsync();

        Assert.Equal(1, await store.CountAsync());
        Assert.Equal("01JQ8Z3K7F9A2B3C4D5E6F7G02", (await store.QueryAsync(new Core.Storage.MailQuery())).Single().Id);
    }

    [Fact]
    public async Task Filtering_counts_ignore_pagination()
    {
        using var store = CreateStore();
        for (var index = 0; index < 5; index++)
        {
            await store.SaveAsync(Message(
                $"01JQ8Z3K7F9A2B3C4D5E6F7G0{index}",
                $"mensaje {index}"));
        }

        var query = new Core.Storage.MailQuery { PageSize = 2 };

        Assert.Equal(5, await store.CountAsync(query));
        Assert.Equal(2, (await store.QueryAsync(query)).Count);
    }

    private FileSystemMessageStore CreateStore()
        => new(
            Options.Create(new StorageOptions { Directory = _directory }),
            Json,
            NullLogger<FileSystemMessageStore>.Instance);

    private static ReceivedMessage Message(string id, string subject)
        => new()
        {
            Id = id,
            ReceivedAtUtc = new DateTimeOffset(2026, 2, 10, 18, 4, 5, TimeSpan.Zero),
            Size = new SizeInfo(100, 4, 0),
            Envelope = new EnvelopeInfo("localhost", "127.0.0.1", 1234, "a@b.com", ["c@d.com"], false, MailTransport.Plain, false),
            Headers = [new HeaderInfo("Subject", subject)],
            Subject = subject,
            MessageId = id + "@test",
            Body = new BodyInfo("hola", null),
        };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        return options;
    }
}
