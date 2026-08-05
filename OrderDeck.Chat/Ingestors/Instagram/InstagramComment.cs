using System.Collections.Generic;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// Graph API'den gelen tek bir canlı yayın yorumu, wire şeklinden arındırılmış
/// hali. <paramref name="TimestampUnix"/> <b>Meta'nın</b> zaman damgasıdır —
/// watermark karşılaştırmaları daima Meta zamanı ile Meta zamanı arasında
/// yapılır, yerel saat hiç karışmaz (kullanıcının sistem saati yanlış olabilir).
/// </summary>
public sealed record InstagramComment(
    string Id,
    string Text,
    long TimestampUnix,
    string? Username);

/// <summary>
/// Bir <c>live_media</c> çekiminin sonucu.
///
/// <para><b>Kritik ayrım:</b> <see cref="Comments"/> <c>null</c> ise alan
/// yanıtta <b>hiç gelmedi</b> — bu bir izin/hata sinyalidir. Boş liste ise
/// yayın var ama henüz yorum yok — bu normaldir. İkisi aynı muamele
/// görmemeli.</para>
/// </summary>
public sealed record InstagramLiveMediaPage(
    string MediaId,
    IReadOnlyList<InstagramComment>? Comments);
