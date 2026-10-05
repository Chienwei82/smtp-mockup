using System.Text;
using SmtpMockup.Core.Models;
using SmtpMockup.Web.Services;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// Descargas: los bytes deben ser <em>exactos</em> y el nombre nunca debe poder escapar del
/// directorio del navegador (SPEC §9.5 y §10.3).
/// </summary>
public sealed class AttachmentDownloaderTests
{
    [Fact]
    public void TryCreate_decodes_the_exact_bytes_of_the_attachment()
    {
        var content = Encoding.UTF8.GetBytes("PDF-CONTENT-123");

        var payload = AttachmentDownloader.TryCreate(TestData.Attachment(content: content));

        Assert.NotNull(payload);
        Assert.Equal(content, payload.Content);
        Assert.Equal("application/pdf", payload.ContentType);
        Assert.Equal("reporte.pdf", payload.FileName);
    }

    [Fact]
    public void TryCreate_returns_null_for_an_attachment_that_was_not_stored()
    {
        Assert.Null(AttachmentDownloader.TryCreate(TestData.Attachment(omitted: true)));
        Assert.Null(AttachmentDownloader.TryCreate(TestData.Attachment() with { ContentBase64 = null }));
    }

    [Fact]
    public void TryCreate_returns_null_when_the_stored_base64_is_corrupt()
    {
        var broken = TestData.Attachment() with { ContentBase64 = "no es base64!!" };

        Assert.Null(AttachmentDownloader.TryCreate(broken));
    }

    [Theory]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("..\\..\\windows\\system32\\evil.dll", "evil.dll")]
    [InlineData("informe final.pdf", "informe final.pdf")]
    [InlineData("   ", AttachmentDownloader.FallbackFileName)]
    [InlineData(null, AttachmentDownloader.FallbackFileName)]
    public void SanitizeFileName_removes_any_path_from_the_name(string? fileName, string expected)
        => Assert.Equal(expected, AttachmentDownloader.SanitizeFileName(fileName));

    [Fact]
    public void TryCreate_uses_a_generic_content_type_when_the_client_sent_none()
    {
        var attachment = TestData.Attachment(contentType: " ") with { ContentType = "  " };

        var payload = AttachmentDownloader.TryCreate(attachment);

        Assert.Equal("application/octet-stream", payload?.ContentType);
    }

    [Fact]
    public void TryCreateRawMime_returns_the_decoded_mime_named_after_the_message()
    {
        var payload = AttachmentDownloader.TryCreateRawMime(TestData.Message(), includeInUi: true);

        Assert.NotNull(payload);
        Assert.Equal($"{TestData.Id}.eml", payload.FileName);
        Assert.Equal(AttachmentDownloader.RawMimeContentType, payload.ContentType);
        Assert.Equal(Encoding.UTF8.GetBytes("Return To"), payload.Content);
    }

    [Fact]
    public void TryCreateRawMime_is_disabled_by_configuration()
        => Assert.Null(AttachmentDownloader.TryCreateRawMime(TestData.Message(), includeInUi: false));

    [Fact]
    public void TryCreateRawMime_returns_null_when_the_raw_was_not_kept()
        => Assert.Null(AttachmentDownloader.TryCreateRawMime(TestData.Message(rawMimeBase64: null), includeInUi: true));

    [Fact]
    public void TryCreateRawMime_returns_null_when_the_raw_was_truncated()
    {
        var message = TestData.Message() with { Raw = new RawMimeInfo(10, Truncated: true, "UmV0") };

        Assert.Null(AttachmentDownloader.TryCreateRawMime(message, includeInUi: true));
    }
}
