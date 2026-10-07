using System;
using System.Collections.Generic;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Kenar çubuğu senkron durum satırının metni (Faz 0, D2). Saf işlev — girdileri
/// <see cref="SyncStatusTracker"/> ve <see cref="SyncPendingCounter"/> verir.
///
/// <para><b>Sıra (C7 incelemesi):</b> önce akışın uzun süredir takıldığı öğe
/// (<see cref="SyncStatusTracker.BlockedOn"/>: "akış X için bekliyor"), sonra yetişme (ilk tam akış
/// henüz yok ya da turlar sayfa sınırına takılıyor — <see cref="CustomerPullOutcome.MorePending"/>),
/// en son "Çevrimdışı". Takılı ya da yetişen bilgisayar çevrimdışı gösterilmez. Ortam hatası
/// (disk dolu, salt okunur, G/Ç) turları "Çevrimdışı"ya düşer (bilinen sınır).</para>
///
/// <para><see cref="SyncStatusTracker.LastPullOkAt"/> yalnız akışı boş sayfaya kadar DURMADAN
/// uygulayan turda yazılır (C7: <c>CaughtUp</c>; <c>Stalled</c>, <c>Busy</c>, <c>Failed</c>
/// yazmaz). Kalıcı durma ya da hata 3 dk sonra "Çevrimdışı — N değişiklik bekliyor" gösterir —
/// doğru: o bilgisayar diğerlerine yetişmiyor.</para>
///
/// <para><b>Kalıcı uyarılar</b> (D1) her durumun arkasına eklenir ve satırı sağlıksız (sarı)
/// gösterir: atlanan akış öğesinin kaydı aynı Id'nin sonraki başarılı değişikliğinde silinir
/// (U10); miras ödeme işi o müşterinin "Ödeme iste"siyle kapanır (U8).</para>
///
/// <para>Metinde kişisel veri yok: takılan öğe yalnız sunucu Id'siyle anılır (günlükteki uyarıyla
/// eşleşir); sayılar sayıdır.</para>
/// </summary>
public static class SyncStatusFormatter
{
    /// <summary>Çekme 30 sn'de bir; üç dakika başarısızlık = gerçekten bağlantı yok. Akışın takılma
    /// eşiğiyle (6 tur) aynı.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(3);

    public readonly record struct Status(string Text, bool Healthy);

    /// <param name="pending">Gönderilmemiş kayıt sayısı (<see cref="SyncPendingCounter.Count"/>).</param>
    /// <param name="lastPullOk">Son tam yetişme (<see cref="SyncStatusTracker.LastPullOkAt"/>).</param>
    /// <param name="attention">Kalıcı uyarılar (D1): varsa metnin sonuna eklenir ve satır
    /// sağlıksız (sarı) görünür — kendiliğinden geçmeyen durumlar operatörden saklanmaz.</param>
    /// <param name="blockedOn">Akışın takıldığı öğe (<see cref="SyncStatusTracker.BlockedOn"/>).</param>
    /// <param name="catchUpProgressAt">Son sayfa sınırlı tur
    /// (<see cref="SyncStatusTracker.LastCatchUpProgressAt"/>); <see cref="OfflineAfter"/>'dan
    /// eskiyse yetişme durmuş sayılır.</param>
    public static Status Format(int pending, DateTimeOffset? lastPullOk, DateTimeOffset now,
        SyncAttention attention = default, SyncBlock? blockedOn = null, DateTimeOffset? catchUpProgressAt = null)
    {
        var status = Decide(pending, lastPullOk, now, blockedOn, catchUpProgressAt);
        if (!attention.Any) return status;
        var notes = new List<string>(2);
        if (attention.SkippedFeedItems > 0)
            notes.Add($"{attention.SkippedFeedItems} müşteri değişikliği uygulanamadı");
        if (attention.OpenLegacyPaymentJobs > 0)
            notes.Add($"{attention.OpenLegacyPaymentJobs} ödeme işi uzlaştırma bekliyor");
        return new(status.Text + " · " + string.Join(" · ", notes), Healthy: false);
    }

    private static Status Decide(int pending, DateTimeOffset? lastPullOk, DateTimeOffset now,
        SyncBlock? blockedOn, DateTimeOffset? catchUpProgressAt)
    {
        if (blockedOn is { } block) return new(Blocked(block), false);
        if (lastPullOk is null) return new("Güncelleniyor…", false);
        if (catchUpProgressAt is { } progress && now - progress <= OfflineAfter)
            return new("Güncelleniyor…", false);
        if (now - lastPullOk.Value > OfflineAfter)
            return new(pending > 0 ? $"Çevrimdışı — {pending} değişiklik bekliyor" : "Çevrimdışı", false);
        if (pending > 0) return new($"Gönderiliyor ({pending})", true);
        return new($"Güncel ✓ (son: {lastPullOk.Value.ToLocalTime():HH:mm})", true);
    }

    /// <summary>Akış servisinin uyarı günlüğüyle aynı sebepler (öğe Id'si + sebep).</summary>
    private static string Blocked(SyncBlock block) => block.Reason switch
    {
        SyncBlockReason.Stalled => $"Akış {block.ItemId} için bekliyor: gönderilemeyen yerel kopya",
        SyncBlockReason.Busy => $"Akış {block.ItemId} için bekliyor: müşteri ödeme akışında",
        _ => $"Akış {block.ItemId} için bekliyor",
    };
}
