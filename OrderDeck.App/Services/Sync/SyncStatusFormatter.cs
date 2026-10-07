using System;
using System.Collections.Generic;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Kenar çubuğu senkron durum satırının metni (Faz 0, D2). Saf işlev — girdileri
/// <see cref="SyncStatusTracker.Snapshot"/> ve <see cref="SyncPendingCounter"/> verir.
///
/// <para><b>Sıra (C7 incelemesi):</b> önce akışın takıldığı öğe
/// (<see cref="SyncStatusTracker.BlockedOn"/>: "müşteri güncellemeleri bekliyor"), sonra yetişme
/// (ilk tam akış henüz yok ya da turlar akışı ilerletiyor ama boş sayfaya varamıyor), en son
/// "Çevrimdışı". Takılı ya da yetişen bilgisayar çevrimdışı gösterilmez — ama yalnız o bilgi
/// TAZEYKEN (<see cref="OfflineAfter"/>; D2 incelemesi I-2, M-2): turlar sunucuya hiç
/// ulaşamazken eski bir takılma ya da eski bir ilerleme çevrimdışını gizlemez. Ortam hatası
/// (disk dolu, salt okunur, G/Ç) turları "Çevrimdışı"ya düşer (bilinen sınır).</para>
///
/// <para><b>Yaş dayanağı (I-1):</b> son tam yetişme, yoksa son yetişme ilerlemesi, yoksa izlemenin
/// başladığı an (<see cref="SyncStatusTracker.TrackingSince"/>) — hiç yetişemeyen süreç de üç
/// dakika sonra çevrimdışı görünür. Bir dakikadan fazla GELECEKTEKİ damga bayat sayılır (M-4: saat
/// geri alındıysa eski başarı sonsuza dek "taze" kalmasın).</para>
///
/// <para><see cref="SyncStatusTracker.LastPullOkAt"/> yalnız akışı boş sayfaya kadar DURMADAN
/// uygulayan turda yazılır (C7: <c>CaughtUp</c>). Kalıcı durma ya da hata 3 dk sonra
/// "Çevrimdışı — N değişiklik bekliyor" gösterir — doğru: o bilgisayar diğerlerine yetişmiyor.</para>
///
/// <para><b>Kalıcı uyarılar</b> (D1) her durumun arkasına eklenir ve satırı sağlıksız (sarı)
/// gösterir: atlanan akış öğesinin kaydı aynı Id'nin sonraki başarılı değişikliğinde silinir
/// (U10); miras ödeme işi o müşterinin "Ödeme iste"siyle kapanır (U8).</para>
///
/// <para>Metinde kişisel veri yok: takılan öğe yalnız sunucu Id'sinin ilk 8 karakteriyle anılır
/// ("kod" — günlükteki uyarının tam Id'siyle eşleşir); sayılar sayıdır.</para>
/// </summary>
public static class SyncStatusFormatter
{
    /// <summary>Çekme 30 sn'de bir; üç dakika başarısızlık = gerçekten bağlantı yok. Akışın takılma
    /// eşiği (5 tur) bunun altında kalır.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(3);

    /// <summary>M-4: bu kadardan fazla gelecekteki damga bayat (saat geri alındı).</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(1);

    private const string Updating = "Güncelleniyor…";

    public readonly record struct Status(string Text, bool Healthy);

    /// <summary>Durum satırının olağan girişi (D3): izleyicinin tek kilit altındaki anlık görüntüsü.</summary>
    public static Status Format(int pending, SyncStatusSnapshot snapshot, DateTimeOffset now,
        SyncAttention attention = default)
        => Format(pending, snapshot.LastPullOkAt, now, attention, snapshot.BlockedOn,
            snapshot.LastCatchUpProgressAt, snapshot.TrackingSince);

    /// <param name="pending">Gönderilmemiş kayıt sayısı (<see cref="SyncPendingCounter.Count"/>).</param>
    /// <param name="lastPullOk">Son tam yetişme (<see cref="SyncStatusTracker.LastPullOkAt"/>).</param>
    /// <param name="attention">Kalıcı uyarılar (D1): varsa metnin sonuna eklenir ve satır
    /// sağlıksız (sarı) görünür — kendiliğinden geçmeyen durumlar operatörden saklanmaz.</param>
    /// <param name="blockedOn">Akışın takıldığı öğe (<see cref="SyncStatusTracker.BlockedOn"/>);
    /// son görülmesi bayatsa yok sayılır.</param>
    /// <param name="catchUpProgressAt">Akışı ilerletip boş sayfaya varmayan son tur
    /// (<see cref="SyncStatusTracker.LastCatchUpProgressAt"/>).</param>
    /// <param name="trackingSince">İzlemenin başladığı an (<see cref="SyncStatusTracker.TrackingSince"/>);
    /// verilmezse ve hiç yetişilmediyse satır "Güncelleniyor…"da kalır.</param>
    public static Status Format(int pending, DateTimeOffset? lastPullOk, DateTimeOffset now,
        SyncAttention attention = default, SyncBlock? blockedOn = null, DateTimeOffset? catchUpProgressAt = null,
        DateTimeOffset? trackingSince = null)
    {
        var status = Decide(pending, lastPullOk, now, blockedOn, catchUpProgressAt, trackingSince);
        if (!attention.Any) return status;
        var notes = new List<string>(2);
        if (attention.SkippedFeedItems > 0)
            notes.Add($"{attention.SkippedFeedItems} müşteri değişikliği uygulanamadı");
        if (attention.OpenLegacyPaymentJobs > 0)
            notes.Add($"{attention.OpenLegacyPaymentJobs} ödeme işi uzlaştırma bekliyor");
        return new(status.Text + " · " + string.Join(" · ", notes), Healthy: false);
    }

    private static Status Decide(int pending, DateTimeOffset? lastPullOk, DateTimeOffset now,
        SyncBlock? blockedOn, DateTimeOffset? catchUpProgressAt, DateTimeOffset? trackingSince)
    {
        // 1) Akış bir öğede takılı (ve takılma taze): çevrimdışı değil, "bekliyor".
        if (blockedOn is { } block && IsFresh(block.LastSeenAt, now)) return new(Blocked(block), false);

        // 2) Yetişiyor: son turlar akışı ilerletti ama boş sayfaya varmadı.
        if (IsFresh(catchUpProgressAt, now)) return new(Updating, false);

        // 3) Yaş: son tam yetişme, yoksa son ilerleme, yoksa izlemenin başı (I-1).
        var reference = lastPullOk ?? catchUpProgressAt ?? trackingSince;
        if (reference is null) return new(Updating, false);      // dayanak verilmedi
        if (!IsFresh(reference, now))
            return new(pending > 0 ? $"Çevrimdışı — {pending} değişiklik bekliyor" : "Çevrimdışı", false);
        if (lastPullOk is null) return new(Updating, false);      // izleme yeni, ilk tam akış bekleniyor

        if (pending > 0) return new($"Gönderiliyor ({pending})", true);
        return new($"Güncel ✓ (son: {lastPullOk.Value.ToLocalTime():HH:mm})", true);
    }

    /// <summary>Son <see cref="OfflineAfter"/> içinde (eşik dahil) ve en çok
    /// <see cref="FutureTolerance"/> kadar gelecekte.</summary>
    private static bool IsFresh(DateTimeOffset? at, DateTimeOffset now)
        => at is { } t && t - now <= FutureTolerance && now - t <= OfflineAfter;

    /// <summary>Sebep önce, kısa kod sonda (D2 incelemesi I-5): operatör ne beklendiğini okur,
    /// destek kodu günlükteki uyarının tam Id'siyle eşleştirir.</summary>
    private static string Blocked(SyncBlock block)
    {
        var code = block.ItemId.Length > 8 ? block.ItemId[..8] : block.ItemId;
        var reason = block.Reason switch
        {
            SyncBlockReason.Busy => "ödemesi süren bir müşteri",
            SyncBlockReason.Stalled => "bu bilgisayardaki bir kayıt henüz gönderilemedi",
            _ => "bir müşteri kaydı",
        };
        return $"Müşteri güncellemeleri bekliyor: {reason} (kod {code})";
    }
}
