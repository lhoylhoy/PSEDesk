using PseSignalDesk.WebApi.Services;

namespace PseSignalDesk.Tests.WebApi;

/// <summary>
/// The upload allowlist keeps a binary the knowledge base cannot parse out of the prompt. The
/// service used to decode anything that was not a PDF as UTF-8 text, so the controller is the
/// place that has to say no.
/// </summary>
public sealed class KnowledgeUploadPolicyTests
{
    [Theory]
    [InlineData("note.md")]
    [InlineData("NOTE.MD")]
    [InlineData("brief.markdown")]
    [InlineData("memo.txt")]
    [InlineData("report.PDF")]
    public void Accepted_documents(string fileName) =>
        Assert.True(KnowledgeUploadPolicy.IsSupported(fileName));

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("chart.png")]
    [InlineData("archive.zip")]
    [InlineData("no-extension")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejected_documents(string? fileName) =>
        Assert.False(KnowledgeUploadPolicy.IsSupported(fileName));

    [Fact]
    public void The_message_names_every_supported_format()
    {
        Assert.Contains(".md", KnowledgeUploadPolicy.SupportedFormats, StringComparison.Ordinal);
        Assert.Contains(".markdown", KnowledgeUploadPolicy.SupportedFormats, StringComparison.Ordinal);
        Assert.Contains(".txt", KnowledgeUploadPolicy.SupportedFormats, StringComparison.Ordinal);
        Assert.Contains(".pdf", KnowledgeUploadPolicy.SupportedFormats, StringComparison.Ordinal);
    }
}
