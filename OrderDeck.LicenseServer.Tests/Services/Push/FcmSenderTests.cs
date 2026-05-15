using FirebaseAdmin.Messaging;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Push;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Push;

/// <summary>
/// FCM sender'ın hata kodu sınıflandırma logic'i — bu pure fonksiyon
/// gerçek FCM HTTP çağrısı olmadan test edilebilir. End-to-end gönderim
/// testi gerçek Firebase project + service account JSON gerektirir,
/// onu manuel smoke test yapacağız (deploy doc).
/// </summary>
public class FcmSenderTests
{
    [Fact]
    public void Unregistered_is_stale_token()
    {
        FcmNotificationSender.IsStaleTokenError(MessagingErrorCode.Unregistered, null)
            .Should().BeTrue();
    }

    [Fact]
    public void SenderIdMismatch_is_stale_token()
    {
        FcmNotificationSender.IsStaleTokenError(MessagingErrorCode.SenderIdMismatch, null)
            .Should().BeTrue();
    }

    [Fact]
    public void InvalidArgument_with_registration_token_in_message_is_stale()
    {
        FcmNotificationSender.IsStaleTokenError(
            MessagingErrorCode.InvalidArgument,
            "The registration token is not a valid FCM registration token")
            .Should().BeTrue();
    }

    [Fact]
    public void InvalidArgument_with_unrelated_message_is_not_stale()
    {
        // Örneğin payload boyutu veya format hatası — token sağlam, sil!ma.
        FcmNotificationSender.IsStaleTokenError(
            MessagingErrorCode.InvalidArgument,
            "Message payload exceeds 4KB limit")
            .Should().BeFalse();
    }

    [Fact]
    public void Internal_error_is_not_stale_just_transient()
    {
        FcmNotificationSender.IsStaleTokenError(MessagingErrorCode.Internal, null)
            .Should().BeFalse();
    }

    [Fact]
    public void Unavailable_is_not_stale_just_transient()
    {
        FcmNotificationSender.IsStaleTokenError(MessagingErrorCode.Unavailable, null)
            .Should().BeFalse();
    }

    [Fact]
    public void QuotaExceeded_is_not_stale()
    {
        FcmNotificationSender.IsStaleTokenError(MessagingErrorCode.QuotaExceeded, null)
            .Should().BeFalse();
    }

    [Fact]
    public void Null_code_is_not_stale()
    {
        FcmNotificationSender.IsStaleTokenError(null, "some random error").Should().BeFalse();
    }

    [Fact]
    public void Case_insensitive_match_for_registration_token_phrase()
    {
        FcmNotificationSender.IsStaleTokenError(
            MessagingErrorCode.InvalidArgument,
            "REGISTRATION TOKEN is malformed")
            .Should().BeTrue();
    }
}
