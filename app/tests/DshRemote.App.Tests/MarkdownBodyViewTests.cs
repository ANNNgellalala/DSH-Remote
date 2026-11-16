using DshRemote.App.Controls;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// Host-side Markdown policy that survives the Arc.Avalonia / PuraMarkdown 0.2 refactor. Parsing, block
/// layout, links, autolinks and large-document framing now belong to Arc's PuraMarkdown.UI engine, so the only
/// behavior left in <see cref="MarkdownBodyView"/> is the size clamp (and the plain-text streaming preview,
/// which needs a live visual tree and is covered by the desktop probe instead).
/// </summary>
public sealed class MarkdownBodyViewTests
{
    [Fact]
    public void ShortMessage_IsPassedThroughUnchanged()
    {
        const string markdown = """
            下载页：https://203.0.113.10:9443/__gateway/download

            - 安装文档见 [install.md](https://example.com/docs/install.md)
            - 裸 URL 现在由 Arc 的 pipeline 自动链接（含中文冒号后）
            """;

        Assert.Equal(markdown, MarkdownBodyView.Clamp(markdown));
    }

    [Fact]
    public void NullOrEmpty_BecomesEmpty()
    {
        Assert.Equal(string.Empty, MarkdownBodyView.Clamp(null));
        Assert.Equal(string.Empty, MarkdownBodyView.Clamp(string.Empty));
    }

    [Fact]
    public void VeryLongMessage_IsTruncatedAtTheHostLimit()
    {
        var markdown = new string('长', 60_000 + 1);

        var clamped = MarkdownBodyView.Clamp(markdown);

        Assert.Equal(60_000, clamped.Length);
        Assert.Equal(markdown[..60_000], clamped);
    }

    [Fact]
    public void MessageExactlyAtTheLimit_IsNotTruncated()
    {
        var markdown = new string('a', 60_000);

        Assert.Equal(markdown, MarkdownBodyView.Clamp(markdown));
    }
}
