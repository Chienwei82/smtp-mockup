using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;
using SmtpMockup.Storage;

namespace SmtpMockup.Storage.Tests;

/// <summary>
/// Marca esta clase para que no se ejecute en paralelo con nada: cambiar el directorio de trabajo
/// es un estado global del proceso y podría corromper otro test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    public const string Name = "current-directory";
}

/// <summary>
/// Dónde acaba <c>Storage:Directory</c>. Es el detalle que decide si el servicio funciona: como
/// Windows Service el proceso arranca con <c>C:\Windows\System32</c> como directorio de trabajo, y
/// una ruta relativa resuelta contra el CWD escribiría los correos (y el PFX) fuera del despliegue.
/// </summary>
[Collection(CurrentDirectoryCollection.Name)]
public sealed class StorageDirectoryResolutionTests : IDisposable
{
    /// <summary>
    /// Carpeta única y relativa al directorio del ejecutado, de modo que el test escriba junto al
    /// binario de los tests y no en el CWD del runner. Se borra al terminar.
    /// </summary>
    private readonly string _relativeDirectory =
        Path.Combine("tests-tmp-storage", Guid.NewGuid().ToString("N"));

    private readonly string _originalWorkingDirectory = Directory.GetCurrentDirectory();

    private string ExpectedRoot => Path.Combine(AppContext.BaseDirectory, _relativeDirectory);

    public StorageDirectoryResolutionTests()
    {
        // vstest arranca los tests con el CWD en la carpeta de salida, que es justo la del
        // ejecutable: sin moverlo, "resolver contra el ejecutable" y "resolver contra el CWD"
        // darían el mismo resultado y el test no probaría nada. Se mueve el CWD a un temporal
        // para reproducir la situación real de un servicio.
        Directory.SetCurrentDirectory(Path.GetTempPath());
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalWorkingDirectory);

        if (Directory.Exists(ExpectedRoot))
        {
            Directory.Delete(ExpectedRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private FileSystemMessageStore CreateStore(string directory)
        => new(
            Options.Create(new StorageOptions { Directory = directory }),
            new JsonSerializerOptions { WriteIndented = true },
            NullLogger<FileSystemMessageStore>.Instance);

    [Fact]
    public void A_relative_directory_is_created_next_to_the_executable()
    {
        using var store = CreateStore(_relativeDirectory);

        Assert.True(
            Directory.Exists(ExpectedRoot),
            $"The store did not create '{ExpectedRoot}'. A relative 'Storage:Directory' must hang from " +
            "AppContext.BaseDirectory, not from the working directory.");
    }

    [Fact]
    public void The_working_directory_is_not_the_executable_directory_so_the_test_would_not_pass_by_accident()
    {
        // Sin esta garantía, el test anterior probaría Path.GetFullPath y no la resolución contra
        // el directorio del ejecutable.
        Assert.NotEqual(Path.GetFullPath(_relativeDirectory), ExpectedRoot);
    }

    [Fact]
    public void An_absolute_directory_is_used_as_is()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "smtp-mockup-storage-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var store = CreateStore(absolute);

            Assert.True(Directory.Exists(absolute));
        }
        finally
        {
            if (Directory.Exists(absolute))
            {
                Directory.Delete(absolute, recursive: true);
            }
        }
    }
}