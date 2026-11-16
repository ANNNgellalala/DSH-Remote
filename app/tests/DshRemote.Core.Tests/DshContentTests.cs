using System.Text.Json;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class DshContentTests
{
    private static SessionEventRecord Event(string type, string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        var data = document.RootElement.Clone();
        return new SessionEventRecord(1, 1000, type, data, dataJson);
    }

    private const string AttachmentOne = """
    {"attachmentId":"sha256:aaa","mediaType":"image/png","bytes":1024,"width":640,"height":480,"name":"one.png"}
    """;

    private const string AttachmentTwo = """
    {"attachmentId":"sha256:bbb","mediaType":"image/jpeg","bytes":2048,"width":800,"height":600}
    """;

    [Fact]
    public void UserMessage_TextThenImage_KeepsOrder()
    {
        var record = Event("user/message",
            "{\"message\":{\"role\":\"user\",\"content\":[" +
            "{\"type\":\"text\",\"text\":\"看这张图\"}," +
            "{\"type\":\"image\",\"attachment\":" + AttachmentOne + "}]}}");

        var parts = DshContent.PartsOf(record);
        Assert.Equal(2, parts.Count);
        Assert.IsType<DshTextPart>(parts[0]);
        Assert.Equal("看这张图", ((DshTextPart)parts[0]).Text);
        var image = Assert.IsType<DshImagePart>(parts[1]).Image;
        Assert.Equal("sha256:aaa", image.AttachmentId);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(1024, image.Bytes);
        Assert.Equal(640, image.Width);
        Assert.Equal(480, image.Height);
        Assert.Equal("one.png", image.Name);

        Assert.Equal("看这张图", DshContent.TextOf(record));
        Assert.Single(DshContent.ImagesOf(record));
    }

    [Fact]
    public void AssistantMessage_MultipleImages_PreserveContentOrder()
    {
        var record = Event("assistant/message",
            "{\"message\":{\"role\":\"assistant\",\"content\":[" +
            "{\"type\":\"image\",\"attachment\":" + AttachmentTwo + "}," +
            "{\"type\":\"text\",\"text\":\"第一张\"}," +
            "{\"type\":\"image\",\"attachment\":" + AttachmentOne + "}," +
            "{\"type\":\"tool-call\",\"name\":\"read_image\"}]}}");

        var parts = DshContent.PartsOf(record);
        Assert.Equal(3, parts.Count); // the tool-call block is skipped
        Assert.Equal("sha256:bbb", Assert.IsType<DshImagePart>(parts[0]).Image.AttachmentId);
        Assert.Equal("第一张", Assert.IsType<DshTextPart>(parts[1]).Text);
        Assert.Equal("sha256:aaa", Assert.IsType<DshImagePart>(parts[2]).Image.AttachmentId);

        var images = DshContent.ImagesOf(record);
        Assert.Equal(new[] { "sha256:bbb", "sha256:aaa" }, images.Select(i => i.AttachmentId));
        Assert.Equal("第一张", DshContent.TextOf(record));
    }

    [Fact]
    public void ToolResult_ContentInsideMessage_IsRead()
    {
        var record = Event("tool/result",
            "{\"turn\":1,\"step\":2,\"message\":{\"role\":\"tool\",\"content\":[" +
            "{\"type\":\"text\",\"text\":\"read_image 结果\"}," +
            "{\"type\":\"image\",\"attachment\":" + AttachmentOne + "}]}}");

        Assert.Equal("read_image 结果", DshContent.TextOf(record));
        Assert.Equal("sha256:aaa", Assert.Single(DshContent.ImagesOf(record)).AttachmentId);
    }

    [Fact]
    public void TopLevelContentArray_IsRead()
    {
        var record = Event("compaction/summary",
            "{\"content\":[{\"type\":\"text\",\"text\":\"summary\"}," +
            "{\"type\":\"image\",\"attachment\":" + AttachmentTwo + "}]}");

        Assert.Equal("summary", DshContent.TextOf(record));
        Assert.Equal("sha256:bbb", Assert.Single(DshContent.ImagesOf(record)).AttachmentId);
    }

    [Fact]
    public void NoContentOrNoImages_ReturnsEmptyTextAndImages()
    {
        var noContent = Event("turn/start", """{"turn":1}""");
        Assert.Empty(DshContent.PartsOf(noContent));
        Assert.Empty(DshContent.ImagesOf(noContent));
        Assert.Equal(string.Empty, DshContent.TextOf(noContent));

        var textOnly = Event("user/message", """
        {"message":{"role":"user","content":[{"type":"text","text":"hi"}]}}
        """);
        Assert.Empty(DshContent.ImagesOf(textOnly));
        Assert.Equal("hi", DshContent.TextOf(textOnly));

        var nonObject = new SessionEventRecord(1, 1, "user/message", default, "{}");
        Assert.Empty(DshContent.PartsOf(nonObject));
        Assert.Equal(string.Empty, DshContent.TextOf(nonObject));
    }

    [Fact]
    public void TextParts_AreJoinedWithNewlines()
    {
        var record = Event("user/message", """
        {"message":{"role":"user","content":[
          {"type":"text","text":"第一行"},{"type":"text","text":"第二行"}
        ]}}
        """);
        Assert.Equal("第一行\n第二行", DshContent.TextOf(record));
    }

    [Fact]
    public void MalformedAttachments_AreSkipped()
    {
        var record = Event("user/message", """
        {"message":{"role":"user","content":[
          {"type":"image","attachment":{"mediaType":"image/png","bytes":1,"width":1,"height":1}},
          {"type":"image","attachment":{"attachmentId":"sha256:x","bytes":1,"width":1,"height":1}},
          {"type":"image","attachment":"not-an-object"},
          {"type":"image"},
          {"type":"text","text":"still here"}
        ]}}
        """);

        Assert.Equal("still here", DshContent.TextOf(record));
        Assert.Empty(DshContent.ImagesOf(record));
    }

    [Fact]
    public void ParseImageRef_ParsesAndRejectsMalformedObjects()
    {
        using var good = JsonDocument.Parse(AttachmentOne);
        var image = DshContent.ParseImageRef(good.RootElement);
        Assert.NotNull(image);
        Assert.Equal("sha256:aaa", image!.AttachmentId);

        using var noId = JsonDocument.Parse("""{"mediaType":"image/png"}""");
        Assert.Null(DshContent.ParseImageRef(noId.RootElement));

        using var noMedia = JsonDocument.Parse("""{"attachmentId":"sha256:a"}""");
        Assert.Null(DshContent.ParseImageRef(noMedia.RootElement));

        using var notObject = JsonDocument.Parse("[]");
        Assert.Null(DshContent.ParseImageRef(notObject.RootElement));
    }
}
