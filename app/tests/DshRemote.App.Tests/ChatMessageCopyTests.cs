using DshRemote.App.ViewModels;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// Projection tests for the transcript copy action. The clipboard itself lives in the view (TopLevel), but
/// the decision of <em>what</em> to copy belongs to the row view model: a message copies its full body even
/// while the reveal pump is still typing, a tool row copies its result (arguments before the result arrives),
/// and a compaction card copies its summary.
/// </summary>
public sealed class ChatMessageCopyTests
{
    [Fact]
    public void AssistantRow_CopiesFullBodyWhileStillStreaming()
    {
        var message = ChatMessageViewModel.CreateStreamingAssistant(1, 1, 0);
        message.AppendTextDelta("第一段");
        message.AppendTextDelta(" + `code`");

        // The visible Text may still be behind the received body; copy is the authoritative full text.
        Assert.Equal("第一段 + `code`", message.CopyText);
        Assert.Equal("复制", message.CopyLabel);
        Assert.True(message.HasCopyableText);
        Assert.True(message.ShowFooter);
    }

    [Fact]
    public void UserRow_CopiesPromptText()
    {
        var message = ChatMessageViewModel.CreateUser("你好，复制我", 0);

        Assert.Equal("你好，复制我", message.CopyText);
        Assert.True(message.HasCopyableText);
    }

    [Fact]
    public void ToolRow_CopiesArgumentsBeforeResult_ThenResult()
    {
        var message = ChatMessageViewModel.CreateTool("read_file", "call-1", "{\"path\":\"a.txt\"}", 0);

        Assert.Equal("复制参数", message.CopyLabel);
        Assert.Equal(message.ArgumentsJson, message.CopyText);

        message.ApplyToolResult("file body", 1000, false);

        Assert.Equal("复制结果", message.CopyLabel);
        Assert.Equal("file body", message.CopyText);
    }

    [Fact]
    public void EmptyRowHasNoCopyAction()
    {
        var message = ChatMessageViewModel.CreateStreamingAssistant(1, 1, 0);

        Assert.False(message.HasCopyableText);
        Assert.False(message.ShowFooter);
    }

    [Fact]
    public void CompactionCard_CopiesSummary()
    {
        var message = ChatMessageViewModel.CreateCompaction("c1", "历史摘要", 2, 1200, manual: false, failed: false, 0);

        Assert.Equal("历史摘要", message.CopyText);
        Assert.Equal("复制摘要", message.CopyLabel);
        Assert.True(message.HasCopyableText);
    }

    [Fact]
    public void CopyState_NotifiesWhenStreamedTextArrives()
    {
        var message = ChatMessageViewModel.CreateStreamingAssistant(1, 1, 0);
        var changed = new List<string?>();
        message.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        message.AppendTextDelta("hello");

        Assert.Contains(nameof(ChatMessageViewModel.CopyText), changed);
        Assert.Contains(nameof(ChatMessageViewModel.HasCopyableText), changed);
        Assert.Contains(nameof(ChatMessageViewModel.ShowFooter), changed);
    }
}
