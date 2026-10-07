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
/// <para><b>Gönderim (I-3):</b> çekme iyiyken bekleyen kayıt varsa ve kayıtlı gönderim
/// servislerinden biri üç dakikadır başarılı tur yazmadıysa (hiç yazmadıysa izlemenin başından
/// beri) satır "Gönderilemiyor — N değişiklik bekliyor" (sağlıksız) olur; yoksa "Gönderiliyor (N)".</para>
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

    /// <summary>Deneme sürümü (lisans yok): senkron hiç koşmaz, sayım da yapılmaz. Nötr (sağlıklı) —
    /// yoksa deneme kullanıcısı kalıcı turuncu "Çevrimdışı — N" görürdü (D3).</summary>
    public static readonly Status NoLicense = new("Senkron kapalı (lisans yok)", Healthy: true);

    /// <summary>İzleyicinin tek kilit altındaki anlık görüntüsüyle.</summary>
    public static Status Format(int pending, SyncStatusSnapshot snapshot, DateTimeOffset now,
        SyncAttention attention = default)
        => Format(() => pending, snapshot, now, attention);

    /// <summary>Durum satırının olağan girişi (D3): izleyicinin tek kilit altındaki anlık görüntüsü;
    /// bekleyen sayı YALNIZ metinde gösterilecekse sayılır — takılı ya da yetişen satırda sayım SQL'i
    /// (UI iş parçacığında, 5 sn'de bir) koşmaz.</summary>
    public static Status Format(Func<int> pendingCount, SyncStatusSnapshot snapshot, DateTimeOffset now,
        SyncAttention attention = default)
        => WithAttention(Decide(pendingCount, snapshot.LastPullOkAt, now, snapshot.BlockedOn,
            snapshot.LastCatchUpProgressAt, snapshot.TrackingSince, snapshot.PushOkAt), attention);

    /// <summary>Durum satırının ipucu (D3): metnin tamamı (dar kenar çubuğunda kırpılır) ve kalıcı
    /// uyarılarda operatörün ne yapacağı — kendiliğinden geçmeyebilecek durum yalnız sayıyla
    /// bırakılmaz. Satırlar <c>\n</c> ile ayrılır.</summary>
    public static string Tooltip(Status status, SyncAttention attention = default)
    {
        if (!attention.Any) return status.Text;
        var lines = new List<string>(3) { status.Text };
        if (attention.SkippedFeedItems > 0)
            lines.Add("Diğer bilgisayarlardan gelen bazı müşteri değişiklikleri bu bilgisayara uygulanamadı; " +
                      "uyarı geçmezse destekle iletişime geç.");
        if (attention.OpenLegacyPaymentJobs > 0)
            lines.Add("Bekleyen ödeme işleri, ilgili müşteriye internet varken \"Ödeme iste\" denince uzlaşır; " +
                      "hangi müşteri olduğunu bilmiyorsan destekle iletişime geç.");
        return string.Join("\n", lines);
    }

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
    /// <param name="pushOkAt">Kayıtlı gönderim servislerinin son başarılı turu
    /// (<see cref="SyncStatusSnapshot.PushOkAt"/>; null = hiç, izleme başından ölçülür).</param>
    public static Status Format(int pending, DateTimeOffset? lastPullOk, DateTimeOffset now,
        SyncAttention attention = default, SyncBlock? blockedOn = null, DateTimeOffset? catchUpProgressAt = null,
        DateTimeOffset? trackingSince = null, IReadOnlyDictionary<string, DateTimeOffset?>? pushOkAt = null)
        => WithAttention(Decide(() => pending, lastPullOk, now, blockedOn, catchUpProgressAt, trackingSince, pushOkAt),
            attention);

    private static Status WithAttention(Status status, SyncAttention attention)
    {
        if (!attention.Any) return status;
        var notes = new List<string>(2);
        if (attention.SkippedFeedItems > 0)
            notes.Add($"{attention.SkippedFeedItems} müşteri değişikliği uygulanamadı");
        if (attention.OpenLegacyPaymentJobs > 0)
            notes.Add($"{attention.OpenLegacyPaymentJobs} ödeme işi uzlaştırma bekliyor");
        return new(status.Text + " · " + string.Join(" · ", notes), Healthy: false);
    }

    /// <param name="pendingCount">Yalnız metinde sayı gösterilecekse ve en çok bir kez çağrılır.</param>
    private static Status Decide(Func<int> pendingCount, DateTimeOffset? lastPullOk, DateTimeOffset now,
        SyncBlock? blockedOn, DateTimeOffset? catchUpProgressAt, DateTimeOffset? trackingSince,
        IReadOnlyDictionary<string, DateTimeOffset?>? pushOkAt)
    {
        // 1) Akış bir öğede takılı (ve takılma taze): çevrimdışı değil, "bekliyor".
        if (blockedOn is { } block && IsFresh(block.LastSeenAt, now)) return new(Blocked(block), false);

        // 2) Yetişiyor: son turlar akışı ilerletti ama boş sayfaya varmadı.
        if (IsFresh(catchUpProgressAt, now)) return new(Updating, false);

        // 3) Yaş: son tam yetişme, yoksa son ilerleme, yoksa izlemenin başı (I-1).
        var reference = lastPullOk ?? catchUpProgressAt ?? trackingSince;
        if (reference is null) return new(Updating, false);      // dayanak verilmedi
        if (!IsFresh(reference, now))
        {
            var offline = pendingCount();
            return new(offline > 0 ? $"Çevrimdışı — {offline} değişiklik bekliyor" : "Çevrimdışı", false);
        }
        if (lastPullOk is null) return new(Updating, false);      // izleme yeni, ilk tam akış bekleniyor

        var pending = pendingCount();

        // 4) Çekme iyi; bekleyen var ama bir gönderim üç dakikadır başaramıyor (I-3).
        if (pending > 0 && AnyPushStale(pushOkAt, trackingSince, now))
            return new($"Gönderilemiyor — {pending} değişiklik bekliyor", false);
        if (pending > 0) return new($"Gönderiliyor ({pending})", true);
        return new($"Güncel ✓ (son: {lastPullOk.Value.ToLocalTime():HH:mm})", true);
    }

    /// <summary>Kayıtlı bir gönderimin son başarısı (yoksa izlemenin başı) bayat mı. Dayanak yoksa
    /// (izleme başı verilmedi) hiç başarmamış gönderim yargılanmaz.</summary>
    private static bool AnyPushStale(IReadOnlyDictionary<string, DateTimeOffset?>? pushOkAt,
        DateTimeOffset? trackingSince, DateTimeOffset now)
    {
        if (pushOkAt is null) return false;
        foreach (var lastOk in pushOkAt.Values)
            if ((lastOk ?? trackingSince) is { } reference && !IsFresh(reference, now))
                return true;
        return false;
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
