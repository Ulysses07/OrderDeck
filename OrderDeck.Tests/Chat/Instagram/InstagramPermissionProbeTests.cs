using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// `GET /me/permissions` yanıtının ayrıştırılması. HTTP'siz — saf metin girdisi.
/// </summary>
public class InstagramPermissionProbeTests
{
    private const string GrantedBoth =
        """
        {"data":[
          {"permission":"pages_show_list","status":"granted"},
          {"permission":"instagram_basic","status":"granted"},
          {"permission":"instagram_manage_comments","status":"granted"}
        ]}
        """;

    private const string OldConnection =
        """
        {"data":[
          {"permission":"pages_show_list","status":"granted"},
          {"permission":"pages_read_engagement","status":"granted"}
        ]}
        """;

    private const string Declined =
        """
        {"data":[
          {"permission":"instagram_basic","status":"granted"},
          {"permission":"instagram_manage_comments","status":"declined"}
        ]}
        """;

    [Fact]
    public void Both_permissions_granted_returns_true()
    {
        InstagramPermissionProbe.HasInstagramPermissions(GrantedBoth).Should().BeTrue();
    }

    [Fact]
    public void Pre_instagram_connection_returns_false()
    {
        // IG izinleri eklenmeden önce bağlanmış kullanıcı — uyarıyı görmeli.
        InstagramPermissionProbe.HasInstagramPermissions(OldConnection).Should().BeFalse();
    }

    [Fact]
    public void Declined_is_not_granted()
    {
        // Kullanıcı izin ekranında IG'yi kaldırdı → "granted" değil.
        InstagramPermissionProbe.HasInstagramPermissions(Declined).Should().BeFalse();
    }

    [Fact]
    public void Basic_without_manage_comments_returns_false()
    {
        // instagram_manage_comments olmadan yorumlarda `username` gelmiyor
        // (Meta 27 Ağu 2024 değişikliği) — yarım izin işe yaramaz.
        const string json =
            """{"data":[{"permission":"instagram_basic","status":"granted"}]}""";
        InstagramPermissionProbe.HasInstagramPermissions(json).Should().BeFalse();
    }

    [Fact]
    public void Malformed_json_returns_false()
    {
        InstagramPermissionProbe.HasInstagramPermissions("not json").Should().BeFalse();
        InstagramPermissionProbe.HasInstagramPermissions("").Should().BeFalse();
        InstagramPermissionProbe.HasInstagramPermissions("{}").Should().BeFalse();
    }

    [Fact]
    public void Error_response_returns_false()
    {
        // Token süresi dolmuş → `data` yok, `error` var. Uyarı göstermek doğru
        // davranış: kullanıcı zaten yeniden bağlanmalı.
        const string json =
            """{"error":{"message":"Session has expired","code":190}}""";
        InstagramPermissionProbe.HasInstagramPermissions(json).Should().BeFalse();
    }
}
