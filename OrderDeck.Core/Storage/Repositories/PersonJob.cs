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

    /// <summary>Bu kişi ve kapsam için birden çok istek var (biri para taşıyor ya da teslim edilmiş) —
    /// tek istekte birleştirilsin mi? (Fazladan düşülen bakiye iade edilir.)</summary>
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
/// satırını korur.</para>
///
/// <para><b>Ne zaman sorulur:</b> kaybedenlerden biri para taşıyor ya da teslim edilmişse birleştirme
/// (<see cref="PersonScopePromptKind.Merge"/>); yoksa ama herhangi bir iş teslim edilmişse tekrar
/// gönderme (<see cref="PersonScopePromptKind.Resend"/>). Parasız ve teslim edilmemiş kaybedenler
/// sorusuz indirilir.</para>
/// </summary>
public sealed class PersonScopePlan
{
    private PersonScopePlan(IReadOnlyList<PaymentJob> jobs, string? ownerId)
    {
        var ordered = jobs
            .OrderByDescending(j => j.CarriesMoney)
            .ThenByDescending(j => j.ClosedAt ?? long.MinValue)
            .ThenByDescending(j => string.Equals(j.CustomerId, ownerId, StringComparison.Ordinal))
            .ThenBy(j => j.CreatedAt)
            .ThenBy(j => j.Id, StringComparer.Ordinal)
            .ToList();
        Jobs = jobs.OrderBy(j => j.CreatedAt).ThenBy(j => j.Id, StringComparer.Ordinal).ToList();
        Survivor = ordered[0];
        Losers = ordered.Skip(1).ToList();
        IsLoudMerge = Losers.Any(l => l.CarriesMoney || l.IsDelivered);
        NeedsConfirmation = IsLoudMerge || jobs.Any(j => j.IsDelivered);
        Token = TokenOf(jobs);
    }

    /// <summary>Kişi × kapsamın bütün işleri (eskiden yeniye).</summary>
    public IReadOnlyList<PaymentJob> Jobs { get; }

    public PaymentJob Survivor { get; }

    /// <summary>İndirilecek işler.</summary>
    public IReadOnlyList<PaymentJob> Losers { get; }

    /// <summary>Kaybedenlerden biri para taşıyor ya da teslim edilmiş — birleştirme sessiz olamaz.</summary>
    public bool IsLoudMerge { get; }

    public bool NeedsConfirmation { get; }

    public PersonScopePromptKind Kind => IsLoudMerge ? PersonScopePromptKind.Merge : PersonScopePromptKind.Resend;

    /// <summary>Soruda gösterilecek istekler: birleştirmede para taşıyan ya da teslim edilmiş işler,
    /// tekrar göndermede teslim edilmiş işler (eskiden yeniye).</summary>
    public IReadOnlyList<PaymentJob> PromptJobs => IsLoudMerge
        ? Jobs.Where(j => j.CarriesMoney || j.IsDelivered).ToList()
        : Jobs.Where(j => j.IsDelivered).ToList();

    /// <summary>Durumun parmak izi: onay bu belirteçle verilir. Soru ile onay arasında herhangi bir iş
    /// değişirse (başka pencere gönderdi, revizyon) belirteç tutmaz ve yeniden sorulur.</summary>
    public string Token { get; }

    /// <param name="jobs">Kişi × kapsamın bütün işleri (en az bir).</param>
    /// <param name="ownerId">Kişinin belirlenimli sahibi (<c>PaymentPerson.OwnerId</c>) — eşitlik bozucu.</param>
    public static PersonScopePlan For(IReadOnlyList<PaymentJob> jobs, string? ownerId)
    {
        if (jobs.Count == 0) throw new ArgumentException("En az bir iş gerekli", nameof(jobs));
        return new PersonScopePlan(jobs, ownerId);
    }

    private static string TokenOf(IEnumerable<PaymentJob> jobs)
        => "kisi:" + string.Join(";", jobs
            .OrderBy(j => j.Id, StringComparer.Ordinal)
            .Select(j => string.Join("/",
                j.Id, j.CustomerId, j.Revision.ToString(CultureInfo.InvariantCulture), j.State,
                j.ApplyKey?.ToString("N") ?? "-",
                j.ProductTotal.ToString(CultureInfo.InvariantCulture),
                j.ClosedAt?.ToString(CultureInfo.InvariantCulture) ?? "-")));

    /// <summary>Kaybedeni miras kapsamına indirirken yazılacak kayıt (<see cref="PaymentJobSql.DemoteToLegacy"/>
    /// ile aynı karar: para taşıyan açık kalır).</summary>
    public static DemotedJob Demotion(PaymentJob loser)
        => new(loser.Id, loser.CustomerId, loser.ScopeKey, loser.CarriesMoney);
}
