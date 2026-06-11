using System.Text.Json;
using MongoDB.Bson;

namespace DiscordIndexer.Tests;

public class AttachmentExtractionTests
{
    [Fact]
    public void ExtractAttachments_ReturnsEmptyArrayWhenMessageHasNoAttachments()
    {
        using var doc = JsonDocument.Parse("""{"id":"1","content":"hello"}""");

        var attachments = Program.ExtractAttachments(doc.RootElement);

        Assert.Empty(attachments);
    }

    [Fact]
    public void ExtractAttachments_NormalizesDiscordAttachmentLinksAndMetadata()
    {
        using var doc = JsonDocument.Parse("""
        {
          "id": "123",
          "attachments": [
            {
              "id": "456",
              "filename": "slime.png",
              "content_type": "image/png",
              "size": 12345,
              "width": 640,
              "height": 480,
              "url": "https://cdn.discordapp.com/attachments/1/2/slime.png",
              "proxy_url": "https://media.discordapp.net/attachments/1/2/slime.png",
              "ephemeral": false,
              "flags": 0
            }
          ]
        }
        """);

        var attachments = Program.ExtractAttachments(doc.RootElement);

        var attachment = Assert.IsType<BsonDocument>(Assert.Single(attachments));
        Assert.Equal("456", attachment["id"].AsString);
        Assert.Equal("slime.png", attachment["filename"].AsString);
        Assert.Equal("image/png", attachment["content_type"].AsString);
        Assert.Equal(12345, attachment["size"].AsInt64);
        Assert.Equal(640, attachment["width"].AsInt32);
        Assert.Equal(480, attachment["height"].AsInt32);
        Assert.Equal("https://cdn.discordapp.com/attachments/1/2/slime.png", attachment["url"].AsString);
        Assert.Equal("https://media.discordapp.net/attachments/1/2/slime.png", attachment["proxy_url"].AsString);
        Assert.False(attachment["ephemeral"].AsBoolean);
        Assert.Equal(0, attachment["flags"].AsInt32);
    }

    [Fact]
    public void ExtractAttachments_PreservesMultipleAttachments()
    {
        using var doc = JsonDocument.Parse("""
        {
          "id": "123",
          "attachments": [
            {"id": "1", "filename": "a.jpg", "url": "https://cdn.discordapp.com/a.jpg"},
            {"id": "2", "filename": "b.jpg", "url": "https://cdn.discordapp.com/b.jpg"}
          ]
        }
        """);

        var attachments = Program.ExtractAttachments(doc.RootElement);

        Assert.Equal(2, attachments.Count);
        Assert.Equal("a.jpg", attachments[0].AsBsonDocument["filename"].AsString);
        Assert.Equal("b.jpg", attachments[1].AsBsonDocument["filename"].AsString);
    }
}
