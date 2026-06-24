using FluentAssertions;
using OrderDeck.Chat.Ingestors.YouTube;
using OrderDeck.Chat.Ingestors.YouTube.Grpc;
using Xunit;

namespace OrderDeck.Tests.Chat.YouTube;

/// <summary>
/// Resmi gRPC streamList proto mesajının OrderDeck ChatMessage'a eşlenmesi
/// (YouTubeOfficialChatIngestor.MapToChatMessage). Wire field'ları → kullanıcı,
/// metin, rozetler.
/// </summary>
public class YouTubeOfficialMapperTests
{
    [Fact]
    public void Maps_text_message_with_author_and_badges()
    {
        var item = new LiveChatMessage
        {
            Id = "msg1",
            Snippet = new LiveChatMessageSnippet { Type = 1, DisplayMessage = "siyah m aldım" },
            AuthorDetails = new LiveChatMessageAuthorDetails
            {
                DisplayName = "Ayşe",
                ChannelId = "UC_abc",
                ProfileImageUrl = "https://x/p.jpg",
                IsChatModerator = true,
            },
        };

        var msg = YouTubeOfficialChatIngestor.MapToChatMessage(item)!;

        msg.Should().NotBeNull();
        msg.Platform.Should().Be("youtube");
        msg.ExternalId.Should().Be("msg1");
        msg.Text.Should().Be("siyah m aldım");
        msg.DisplayName.Should().Be("Ayşe");
        msg.Username.Should().Be("UC_abc");          // channelId tercih edilir
        msg.AvatarUrl.Should().Be("https://x/p.jpg");
        msg.Badges.Should().Contain("moderator");
        msg.Id.Should().NotBeNullOrEmpty();           // yeni GUID
    }

    [Fact]
    public void Falls_back_to_displayName_when_no_channelId()
    {
        var item = new LiveChatMessage
        {
            Id = "m2",
            Snippet = new LiveChatMessageSnippet { Type = 1, DisplayMessage = "selam" },
            AuthorDetails = new LiveChatMessageAuthorDetails { DisplayName = "Veli" },
        };

        var msg = YouTubeOfficialChatIngestor.MapToChatMessage(item)!;

        msg.Username.Should().Be("Veli");
    }

    [Fact]
    public void Uses_text_message_details_when_display_message_empty()
    {
        var item = new LiveChatMessage
        {
            Id = "m3",
            Snippet = new LiveChatMessageSnippet
            {
                Type = 1,
                TextMessageDetails = new LiveChatTextMessageDetails { MessageText = "ham metin" },
            },
            AuthorDetails = new LiveChatMessageAuthorDetails { DisplayName = "Can", ChannelId = "UC_c" },
        };

        var msg = YouTubeOfficialChatIngestor.MapToChatMessage(item)!;

        msg.Text.Should().Be("ham metin");
    }

    [Fact]
    public void Super_chat_gets_badge_even_with_empty_text()
    {
        var item = new LiveChatMessage
        {
            Id = "m4",
            Snippet = new LiveChatMessageSnippet { Type = 15 }, // SUPER_CHAT_EVENT, display_message yok
            AuthorDetails = new LiveChatMessageAuthorDetails { DisplayName = "Zengin", ChannelId = "UC_z" },
        };

        var msg = YouTubeOfficialChatIngestor.MapToChatMessage(item)!;

        msg.Should().NotBeNull();
        msg.Badges.Should().Contain("superchat");
        msg.Text.Should().Be("[Super Chat]");
    }

    [Fact]
    public void Owner_and_member_badges()
    {
        var item = new LiveChatMessage
        {
            Id = "m5",
            Snippet = new LiveChatMessageSnippet { Type = 1, DisplayMessage = "hi" },
            AuthorDetails = new LiveChatMessageAuthorDetails
            {
                DisplayName = "Kadir", ChannelId = "UC_k",
                IsChatOwner = true, IsChatSponsor = true,
            },
        };

        var msg = YouTubeOfficialChatIngestor.MapToChatMessage(item)!;

        msg.Badges.Should().Contain("owner");
        msg.Badges.Should().Contain("member");
    }

    [Fact]
    public void Returns_null_when_no_id()
    {
        var item = new LiveChatMessage
        {
            Snippet = new LiveChatMessageSnippet { Type = 1, DisplayMessage = "x" },
        };

        YouTubeOfficialChatIngestor.MapToChatMessage(item).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_empty_text_and_not_super_chat()
    {
        var item = new LiveChatMessage
        {
            Id = "m6",
            Snippet = new LiveChatMessageSnippet { Type = 1 }, // metin yok, superchat değil
            AuthorDetails = new LiveChatMessageAuthorDetails { DisplayName = "Bos" },
        };

        YouTubeOfficialChatIngestor.MapToChatMessage(item).Should().BeNull();
    }
}
