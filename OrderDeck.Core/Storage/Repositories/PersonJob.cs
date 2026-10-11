using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>Kişi birleştirmesinde miras kapsamına indirilen iş. <see cref="OriginalScopeKey"/> aynı zamanda
/// satırın <c>PriorScopeKey</c> kolonundadır (göç 047): banka beklentisi (W2) kaybeden işin
/// (CustomerId, OriginalScopeKey) beklentisini iptal eder. <see cref="KeptOpen"/> = para taşıyordu,
/// açık miras olarak kaldı ve uzlaştırma onu geri alır.</summary>
public sealed record DemotedJob(string JobId, string CustomerId, string OriginalScopeKey, bool KeptOpen);

/// <summary><see cref="IPaymentJobStore.FindOrCreateForPerson"/> isteği.</summary>
/// <param name="LeasedId">Kiralanan (tıklanan) satır — üye kümesi işlemin içinde bundan yeniden okunur.</param>
/// <param name="MemberKey">Akışın gördüğü üye kümesinin anahtarı (<c>PaymentPerson.MemberKey</c>);
/// işlemin içindeki okuma tutmazsa <see cref="PersonJobStatus.MembershipChanged"/>.</param>
/// <param name="MemberIds">Akışın gördüğü üyeler (üye okuması olmayan taklit depo için).</param>
/// <param name="OwnerId">Kişinin hiç işi yoksa yeni işin sahip satırı.</param>
/// <param name="ConfirmedTokens">Operatörün onayladığı soruların belirteçleri
/// (<see cref="PersonScopePlan.Token"/>). Durum soru sorulduğundan beri değiştiyse belirteç tutmaz ve
/// yeniden sorulur.</param>
public sealed record PersonJobRequest(
    string LeasedId,
    string MemberKey,
    IReadOnlyCollection<string> MemberIds,
    string OwnerId,
    string ScopeKey,
    decimal ProductTotal,
    IReadOnlySet<string>? ConfirmedTokens = null);

public enum PersonJobStatus
{
    /// <summary>İş hazır (bulundu ya da açıldı); varsa fazla işler indirildi.</summary>
    Ready,

    /// <summary>Üye kümesi akışın okumasından sonra değişti — HİÇBİR ŞEY yazılmadı; akış kişiyi
    /// yeniden okuyup (yeni üyeleri kiralayarak) tekrar dener.</summary>
    MembershipChanged,

    /// <summary>Teslim edilmiş istek ya da para taşıyan/teslim edilmiş fazla iş var ve operatör
    /// onaylamadı — HİÇBİR ŞEY yazılmadı (indirme, geri alma, ağ, mesaj yok).</summary>
    ConfirmationRequired,
}

/// <summary><see cref="IPaymentJobStore.FindOrCreateForPerson"/> sonucu.</summary>
public sealed record PersonJob(
    PersonJobStatus Status,
    PaymentJob? Job,
    IReadOnlyList<DemotedJob> Demoted,
    PersonScopePlan? Plan = null)
{
    public static PersonJob Ready(PaymentJob job, IReadOnlyList<DemotedJob> demoted) =>
        new(PersonJobStatus.Ready, job, demoted);

    public static readonly PersonJob MembershipChanged =
        new(PersonJobStatus.MembershipChanged, null, Array.Empty<DemotedJob>());
}

/// <summary>Operatöre sorulacak sorunun türü.</summary>
public enum PersonScopePromptKind
{
    /// <summary>Bu kişi ve kapsam için teslim edilmiş bir istek var — tekrar gönderilsin mi?</summary>
    Resend,

    /// <summary>Bu kişi ve kapsam için birden çok istek var (biri para taşıyor ya da teslim edilmiş) ya
    /// da iade edilecek teslim edilmiş miras isteği var — tek istekte birleştirilsin mi? (Varsa önceki
    /// düşümler iade edilir, kişinin toplamı yeniden istenir.)</summary>
    Merge,
}

/// <summary>
/// Kişi × kapsamdaki işlerin kararı — saf; SQLite deposu ve taklit depo AYNI planı uygular.
///
/// <para><b>Kalan iş:</b> önce para taşıyan, sonra en son teslim edilen, sonra belirlenimli sahibin
/// işi, sonra en eski (CreatedAt, Id). Sahibin el değmemiş işi para taşıyan ya da teslim edilmiş bir
/// işin önüne GEÇMEZ: bakiye satır başına tutulur; başka üyenin bakiyesinden düşmüş işi geri alıp
/// bakiyesi olmayan satırda yeniden uygulamak müşterinin indirimini yok ederdi. Eşitlikte sahibinki
/// kalır: iki bilgisayar aynı (WpfCustomerId, ScopeKey) anahtarında buluşur. Kalan iş kendi sahip
/// satırını korur. Kapsamın işi yoksa kalan iş de yoktur (onayda sahip satırda açılır).</para>
///
/// <para><b>Bu kapsamın miras işleri</b> (asıl kapsamı bu olan, <see cref="PaymentJob.PriorScopeKey"/>)
/// kalan iş olamaz — zaten indirilmişlerdir. Açık olanlar (<see cref="PendingLegacies"/>: taşımanın
/// indirdiği ya da geri alması kesinleşmemiş kaybeden) bu tıklamada geri alınır ya da benimsenir
/// (<see cref="AdoptedLegacy"/>); iade edilecek olan müşteriye gitmişse (teslim edilmiş) birleştirme
/// sorusunu tetikler — iadesi sessiz olmasın. Benimsenecek olanın düşümü kalan işte sürer: isteği
/// gitmişse soru tekrar göndermedir (aynı istek yeniden gider, iade yok). Gitmemiş açık miras (müşteri
/// o düşümü hiç görmedi) eskisi gibi sorusuz uzlaşır. Kapalı ama teslim edilmiş olanlar yalnız "istek
/// gitti" bilgisidir (taşıma teslim edilmiş parasız kopya işini kapatır — iz kaybolmasın).</para>
///
/// <para><b>Ne zaman sorulur:</b> kaybedenlerden biri para taşıyor ya da teslim edilmişse, ya da iade
/// edilecek açık miras işlerden biri teslim edilmişse birleştirme (<see cref="PersonScopePromptKind.Merge"/>);
/// yoksa ama herhangi bir iş (miras dahil) teslim edilmişse tekrar gönderme
/// (<see cref="PersonScopePromptKind.Resend"/>). Parasız ve teslim edilmemiş kaybedenler sorusuz
/// indirilir.</para>
///
/// <para><b>Yarım kalmış istek sorulmaz</b> (<see cref="Resumes"/>): kalan iş belirsiz ya da geri alma
/// bekliyorsa (<c>apply_uncertain</c>/<c>reverse_pending</c>) önceki bir tıklama para adımını başlatmış
/// ve mesaj göndermeden durmuştur (BalanceUncertain — "tekrar deneyin"). O tıklamanın kapıda gördüğü
/// izler zaten sorulup onaylanmıştı; akış kaldığı yerden sürer (önceki sürüm gibi). Yeniden "tekrar
/// gönderilsin mi?" sormak geçersizleşmiş eski tutarı "istendi" diye gösterirdi; "Hayır" da yarım
/// birleştirmenin iadelerini yeni istek olmadan bırakırdı. Muafiyet DAR: yalnız kalan işin kendi teslimi
/// ve kapının o tıklamada gördüğü izler (onay damgası, <see cref="PaymentJob.ResumeCovers"/>). Yarım
/// adımdan SONRA kapsama giren teslim izi — ör. taşımanın (MoveReferences) kapalı mirasa indirdiği
/// teslim edilmiş kopya işi — o tıklamada hiç sorulmamıştır: yine sorulur. Birleştirme sorusu (yeni
/// kaybeden) her zaman sorulur.</para>
/// </summary>
public sealed class PersonScopePlan
{
    private PersonScopePlan(IReadOnlyList<PaymentJob> jobs, IReadOnlyList<PaymentJob> legacies, string? ownerId)
    {
        var ordered = jobs
            .OrderByDescending(j => j.CarriesMoney)
            .ThenByDescending(DeliveryRank)
            .ThenByDescending(j => string.Equals(j.CustomerId, ownerId, StringComparison.Ordinal))
            .ThenBy(j => j.CreatedAt)
            .ThenBy(j => j.Id, StringComparer.Ordinal)
            .ToList();
        Jobs = Oldest(jobs);
        Legacies = Oldest(legacies);
        Survivor = ordered.FirstOrDefault();
        Losers = ordered.Skip(1).ToList();
        PendingLegacies = Legacies.Where(l => l.ClosedAt is null).ToList();

        // Servisin benimseme kuralı (PaymentRequestService, miras adımı): hedef iş taze (kalan iş taze ya da
        // yok — onayda sahip satırda taze açılır), miras aynı sahip satırda; açık miraslar en yeniden
        // eskiye taranır, ilk uyan benimsenir.
        var survivorRow = Survivor?.CustomerId ?? ownerId;
        AdoptedLegacy = Survivor is null or { State: PaymentJobState.Created, ApplyKey: null }
            ? PendingLegacies
                .Where(l => string.Equals(l.CustomerId, survivorRow, StringComparison.Ordinal))
                .OrderByDescending(l => l.CreatedAt)
                .ThenByDescending(l => l.Id, StringComparer.Ordinal)
                .FirstOrDefault()
            : null;
        var refundedLegacies = PendingLegacies
            .Where(l => !string.Equals(l.Id, AdoptedLegacy?.Id, StringComparison.Ordinal)).ToList();

        var settled = Losers.Concat(refundedLegacies).ToList();
        IsLoudMerge = Losers.Any(j => j.CarriesMoney || j.IsDelivered) || refundedLegacies.Any(j => j.IsDelivered);
        Resumes = Survivor is { State: PaymentJobState.ApplyUncertain or PaymentJobState.ReversePending };
        var covered = Resumes ? CoveredBy(Survivor!) : null;
        NeedsConfirmation = IsLoudMerge || jobs.Concat(legacies).Any(j => j.IsDelivered && covered?.Contains(j.Id) != true);
        RefundsDeductions = settled.Any(j => j.CarriesMoney);
        RefundsToOtherRow = IsLoudMerge && settled.Any(j =>
            j.CarriesMoney && !string.Equals(j.CustomerId, survivorRow, StringComparison.Ordinal));
        Token = TokenOf(jobs.Concat(legacies));
        Covers = string.Join(";", jobs.Concat(legacies).Select(j => j.Id).Distinct().OrderBy(id => id, StringComparer.Ordinal));
    }

    /// <summary>Yarım kalan kalan işin muaf tuttuğu izler: kendisi ve onay damgasındakiler.</summary>
    private static HashSet<string> CoveredBy(PaymentJob survivor)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { survivor.Id };
        if (survivor.ResumeCovers is { Length: > 0 } stamp) set.UnionWith(stamp.Split(';'));
        return set;
    }

    /// <summary>Teslim sırası: son teslim (önceki sürümün teslimi dahil — <see cref="PaymentJob.LastDeliveredAt"/>)
    /// ile sonucu yazılamamış gönderimin kendi anından (<see cref="PaymentJob.AttemptAt"/>) yenisi. Yetim
    /// deneme "hep en son" sayılmaz: başka bir işte kalmış yetim (ör. birleştirmenin mirasa indirdiği iş —
    /// miraslara teslim yazılmaz, fark hiç kapanmaz) daha yeni onaylı teslimi gizlerdi. Anı bilinmeyen
    /// deneme (BeginSend anı hep yazar) temkinle en son sayılır.</summary>
    private static long DeliveryRank(PaymentJob j)
    {
        var delivered = j.LastDeliveredAt ?? long.MinValue;
        return j.HasUnconfirmedSend ? Math.Max(delivered, j.AttemptAt ?? long.MaxValue) : delivered;
    }

    private static List<PaymentJob> Oldest(IEnumerable<PaymentJob> jobs)
        => jobs.OrderBy(j => j.CreatedAt).ThenBy(j => j.Id, StringComparer.Ordinal).ToList();

    /// <summary>Kişi × kapsamın işleri (eskiden yeniye).</summary>
    public IReadOnlyList<PaymentJob> Jobs { get; }

    /// <summary>Asıl kapsamı bu kapsam olan miras işler — açık ya da teslim edilmiş (eskiden yeniye).</summary>
    public IReadOnlyList<PaymentJob> Legacies { get; }

    /// <summary>Uzlaştırma bekleyen (açık) miras işler: onaylı tıklama onları geri alır ya da benimser.</summary>
    public IReadOnlyList<PaymentJob> PendingLegacies { get; }

    /// <summary>Kalan işe benimsenecek açık miras iş (düşümü kalan işte sürer — iade edilmez); yoksa
    /// <c>null</c>. Asıl kapsamı bilinmeyen (047 öncesi) miras işler plana girmediği için servis nadiren
    /// başka birini benimseyebilir; o zaman bu iş iade edilir ve soru tekrar gönderme olarak kalır.</summary>
    public PaymentJob? AdoptedLegacy { get; }

    /// <summary>Kalan işin para adımı yarım kalmış (<c>apply_uncertain</c>/<c>reverse_pending</c>): akış
    /// kaldığı yerden sürer — kalan işin kendi teslimi ve onay damgasındaki izler için tekrar gönderme
    /// sorulmaz (sınıf belgesi).</summary>
    public bool Resumes { get; }

    /// <summary>Onay damgası: plandaki bütün işlerin ve mirasların Id'leri (sıralı, ';' ayraçlı). Depo kapı
    /// geçilince (Hazır) kalan işe yazar (<see cref="PaymentJob.ResumeCovers"/>).</summary>
    public string Covers { get; }

    /// <summary>Kalan iş; kapsamın işi yoksa <c>null</c> (onayda sahip satırda açılır).</summary>
    public PaymentJob? Survivor { get; }

    /// <summary>İndirilecek işler.</summary>
    public IReadOnlyList<PaymentJob> Losers { get; }

    /// <summary>Kaybedenlerden biri para taşıyor ya da teslim edilmiş, ya da iade edilecek açık miras
    /// işlerden biri teslim edilmiş — birleştirme sessiz olamaz.</summary>
    public bool IsLoudMerge { get; }

    public bool NeedsConfirmation { get; }

    /// <summary>Birleştirme bir düşümü iade ediyor (kaybedenlerden ya da iade edilecek miras işlerden biri
    /// para taşıyor). Yoksa soru "önceki düşümler iade edilir" demez.</summary>
    public bool RefundsDeductions { get; }

    /// <summary>Birleştirme, kalan işin satırından BAŞKA bir satırdan yapılmış düşümü iade ediyor.
    /// Bakiye satır başına tutulur: iade o satıra döner, kalan işin revizyonu onu kullanamaz. İki ayrı
    /// kartken ayrı ürünler için yapılmış meşru düşümler de böyle görünür — operatör soruda görmeli.</summary>
    public bool RefundsToOtherRow { get; }

    public PersonScopePromptKind Kind => IsLoudMerge ? PersonScopePromptKind.Merge : PersonScopePromptKind.Resend;

    /// <summary>Soruda "gitmiş" diye gösterilecek istekler — yalnız TESLİM edilmişler (ya da gitmiş
    /// olabilecekler), teslim sırasıyla. Birleştirmede kapsamın işleri ve açık miras işler (uzlaşmış miras
    /// isteği kalan işin mesajıyla geçersizleşmiştir); tekrar göndermede bunlara ek olarak kapalı miras
    /// işler (en sonuncusu sorulur).</summary>
    public IReadOnlyList<PaymentJob> PromptJobs => (IsLoudMerge ? Jobs.Concat(PendingLegacies) : Jobs.Concat(Legacies))
        .Where(j => j.IsDelivered)
        .OrderBy(DeliveryRank)
        .ThenBy(j => j.Id, StringComparer.Ordinal)
        .ToList();

    /// <summary>Birleştirmede para taşıyan ama müşteriye hiç gitmemiş istekler (belirsiz düşüm, geri alma
    /// bekleyen, gönderilemeden kalmış uygulanmış iş) — "gitmiş" sayılmaz, ayrıca belirtilir.</summary>
    public int UnsentDeductions => IsLoudMerge
        ? Jobs.Concat(PendingLegacies).Count(j => j.CarriesMoney && !j.IsDelivered)
        : 0;

    /// <summary>Durumun parmak izi: onay bu belirteçle verilir. Soru ile onay arasında herhangi bir iş
    /// değişirse (başka pencere gönderdi — gönderim denemesi ağdan ÖNCE sayılır, sonucu yazılamasa da —,
    /// revizyon, indirme) belirteç tutmaz ve yeniden sorulur.</summary>
    public string Token { get; }

    /// <param name="jobs">Kişi × kapsamın işleri (boş olabilir; o zaman <paramref name="legacies"/> dolu).</param>
    /// <param name="legacies">Asıl kapsamı bu kapsam olan miras işler (açık ya da teslim edilmiş).</param>
    /// <param name="ownerId">Kişinin belirlenimli sahibi (<c>PaymentPerson.OwnerId</c>) — eşitlik bozucu;
    /// iş yoksa yeni işin satırı.</param>
    public static PersonScopePlan For(
        IReadOnlyList<PaymentJob> jobs, IReadOnlyList<PaymentJob> legacies, string? ownerId)
    {
        if (jobs.Count + legacies.Count == 0) throw new ArgumentException("En az bir iş gerekli", nameof(jobs));
        return new PersonScopePlan(jobs, legacies, ownerId);
    }

    /// <summary>Yalnız kapsamın işleriyle (miras iş yok).</summary>
    public static PersonScopePlan For(IReadOnlyList<PaymentJob> jobs, string? ownerId)
        => For(jobs, Array.Empty<PaymentJob>(), ownerId);

    private static string TokenOf(IEnumerable<PaymentJob> jobs)
        => "kisi:" + string.Join(";", jobs
            .OrderBy(j => j.Id, StringComparer.Ordinal)
            .Select(j => string.Join("/",
                j.Id, j.CustomerId, j.ScopeKey, j.Revision.ToString(CultureInfo.InvariantCulture), j.State,
                j.ApplyKey?.ToString("N") ?? "-",
                j.ProductTotal.ToString(CultureInfo.InvariantCulture),
                j.ClosedAt?.ToString(CultureInfo.InvariantCulture) ?? "-",
                j.Deliveries.ToString(CultureInfo.InvariantCulture),
                j.DeliveredAt?.ToString(CultureInfo.InvariantCulture) ?? "-",
                j.SendAttempts.ToString(CultureInfo.InvariantCulture))));

    /// <summary>Kaybedeni miras kapsamına indirirken yazılacak kayıt (<see cref="PaymentJobSql.DemoteToLegacy"/>
    /// ile aynı karar: para taşıyan açık kalır).</summary>
    public static DemotedJob Demotion(PaymentJob loser)
        => new(loser.Id, loser.CustomerId, loser.ScopeKey, loser.CarriesMoney);
}
