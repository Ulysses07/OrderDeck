using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dapper;
using OrderDeck.Core.Customers;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>PaymentJob durumları. String sabitler — SQLite'ta TEXT saklanır,
/// enum sıra değişikliği veri bozamasın.</summary>
public static class PaymentJobState
{
    /// <summary>İş var, henüz anahtar diske inmedi — sunucuya hiçbir şey gitmedi.</summary>
    public const string Created = "created";
    /// <summary>Anahtar diskte; sunucu cevabı KESİNLEŞMEDİ. Bu durumda mesaj gönderilmez.</summary>
    public const string ApplyUncertain = "apply_uncertain";
    /// <summary>Düşüm uygulandı; AppliedAmount dolu.</summary>
    public const string Applied = "applied";
    /// <summary>Sunucu kesin cevap verdi: bakiye yok / uygulanacak şey yok. AppliedAmount = 0.</summary>
    public const string NoBalance = "no_balance";

    /// <summary>R4-02: eski düşümü geri alma NİYETİ diske indi, geri almanın
    /// sonucu KESİNLEŞMEDİ. ApplyKey hâlâ geri alınacak işlemin anahtarıdır;
    /// PendingTotal, geri alma kesinleşince geçilecek toplamdır.
    ///
    /// <para><see cref="ApplyUncertain"/>'den ayrı olmak zorunda: orada anahtar
    /// replay edilmelidir, burada anahtar ARTIK GEÇERSİZ sayılıp önce geri alma
    /// uzlaştırılmalıdır. İkisi aynı kutuda olduğu sürece, yeni isteğin toplamı
    /// eski değere dönerse yarım kalmış geri alma görünmez olur ve geri alınmış
    /// bir işlemin tutarı geçerli düşüm sayılır.</para></summary>
    public const string ReversePending = "reverse_pending";
}

/// <summary>Bir "Ödeme iste" satışının kalıcı kimliği ve düşüm sonucu.
/// Fiziksel anahtar: (CustomerId, ScopeKey) — "session:{id}" | "cumulative" | "legacy".
/// İşler ASLA silinmez; teslimatta teslim kaydı (<see cref="DeliveredAt"/>) yazılır ve sonucu
/// kesin iş kapanır (ClosedAt).
///
/// <para>Mantıksal kimlik (kişi, ScopeKey)'dir: iş, sahip satırının (<see cref="CustomerId"/> — para
/// anahtarı, sunucuya giden WpfCustomerId) kişisindeki bütün üyeler taranarak bulunur
/// (<see cref="IPaymentJobStore.FindOrCreateForPerson"/>). Kart birincili değişse de aynı iş.</para></summary>
public sealed record PaymentJob(
    string Id,
    string CustomerId,
    string ScopeKey,
    decimal ProductTotal,
    int Revision,
    Guid? ApplyKey,
    decimal? AppliedAmount,
    string State,
    long CreatedAt,
    long UpdatedAt,
    long? ClosedAt,
    /// <summary>R4-02: yalnız <see cref="PaymentJobState.ReversePending"/>
    /// durumunda dolu — geri alma kesinleşince geçilecek toplam.</summary>
    decimal? PendingTotal = null,
    /// <summary>Göç 047: iş miras kapsamına indirildiyse ASIL kapsamı (<see cref="PaymentJobSql.DemoteToLegacy"/>).</summary>
    string? PriorScopeKey = null,
    /// <summary>Göç 047: SON teslimin anı — <see cref="IPaymentJobStore.MarkDelivered"/> yazar, hiçbir
    /// yol silmez (revizyon, geri alma, indirme ClosedAt'ı siler; bunu değil).</summary>
    long? DeliveredAt = null,
    /// <summary>Göç 047: son teslim edilen mesajdaki net tutar — DONMUŞ: göç öncesi teslimlerde 047
    /// işin alanlarından doldurur; işi yeniden açan ya da tutarını değiştiren her yazım önce dondurur
    /// (<see cref="PaymentJobSql.FreezeDelivery"/>).</summary>
    decimal? DeliveredNet = null,
    /// <summary>Göç 047: teslim sayısı — onay belirtecine girer (aynı saniyedeki iki gönderimi de ayırır).</summary>
    int Deliveries = 0,
    /// <summary>Göç 047: gönderim denemesi sayacı — her gönderimden ÖNCE artar
    /// (<see cref="IPaymentJobStore.BeginSend"/>), onay belirtecine girer.</summary>
    int SendAttempts = 0,
    /// <summary>Göç 047: SON gönderim denemesinin mesajındaki net tutar (<see cref="IPaymentJobStore.BeginSend"/>
    /// yazar) — sonucu yazılamamış denemenin sorusunda gösterilen tutar; işin alanları sonradan değişebilir.</summary>
    decimal? AttemptNet = null,
    /// <summary>Göç 047: bir önceki denemenin neti — <see cref="IPaymentJobStore.AbandonSend"/> başlamamış
    /// denemeyi geri alırken <see cref="AttemptNet"/>'i buna döndürür.</summary>
    decimal? PriorAttemptNet = null,
    /// <summary>Göç 047: SON gönderim denemesinin anı (<see cref="IPaymentJobStore.BeginSend"/> yazar) —
    /// sonucu yazılamamış deneme bu anla sıralanır ve soruda bu saatle görünür.</summary>
    long? AttemptAt = null,
    /// <summary>Göç 047: bir önceki denemenin anı — <see cref="IPaymentJobStore.AbandonSend"/>
    /// <see cref="AttemptAt"/>'i buna döndürür (<see cref="PriorAttemptNet"/> gibi).</summary>
    long? PriorAttemptAt = null,
    /// <summary>Göç 047: onay damgası — kişi × kapsamın kapısı (<see cref="IPaymentJobStore.FindOrCreateForPerson"/>)
    /// bu işi kalan iş olarak SON geçirdiğinde (operatör onayladı ya da soru gerekmedi) planda gördüğü işlerin
    /// ve mirasların Id'leri (<see cref="PersonScopePlan.Covers"/>). Yarım kalan adım (<see cref="PersonScopePlan.Resumes"/>)
    /// yalnız bu izleri soru dışında tutar; sonradan kapsama giren teslim izi yine sordurur.</summary>
    string? ResumeCovers = null)
{
    /// <summary>İş para taşıyor mu — <see cref="PaymentJobSql.CarriesMoney"/>'nin C# karşılığı
    /// (ikisi aynı kalmalı).</summary>
    public bool CarriesMoney =>
        State is PaymentJobState.ApplyUncertain or PaymentJobState.ReversePending
        || (State == PaymentJobState.Applied && (AppliedAmount is null || AppliedAmount > 0m));

    /// <summary>Miras kapsamı ('legacy' ya da 'legacy:…').</summary>
    public bool IsLegacyScope =>
        ScopeKey == "legacy" || ScopeKey.StartsWith("legacy:", StringComparison.Ordinal);

    /// <summary>Önceki sürümün (Velopack geri dönüşü) teslimi: legacy olmayan kapsamda ClosedAt'ı iki
    /// sürümde de yalnız teslim yazar, önceki sürüm teslim kaydı yazmaz. ClosedAt teslim kaydından
    /// yeniyse o kapanış en son teslimdir. SQL karşılığı <see cref="PaymentJobSql.ClosedByLaterDelivery"/>
    /// (ikisi aynı kalmalı).</summary>
    private bool ClosedByLaterDelivery =>
        !IsLegacyScope && ClosedAt is long closed && (DeliveredAt is not long delivered || closed > delivered);

    /// <summary>Son teslimin anı: teslim kaydı ya da (önceki sürümün teslimiyse) kapanış.</summary>
    public long? LastDeliveredAt => ClosedByLaterDelivery ? ClosedAt : DeliveredAt;

    /// <summary>Gönderim başladı ama sonucu yazılamadı (süreç düştü, teslim yazımı hata verdi) — mesaj
    /// müşteriye gitmiş olabilir.</summary>
    public bool HasUnconfirmedSend => SendAttempts > Deliveries;

    /// <summary>Bu iş için müşteriye en az bir mesaj ulaştı (teslim edildi) ya da ulaşmış olabilir
    /// (<see cref="HasUnconfirmedSend"/>) — tekrar göndermeden önce sorulur.</summary>
    public bool IsDelivered => LastDeliveredAt is not null || HasUnconfirmedSend;

    /// <summary>Müşteriden istenen net tutar (kargo dahil toplam − düşülen bakiye).</summary>
    public decimal RequestedNet => ProductTotal - (AppliedAmount ?? 0m);

    /// <summary>Son teslim edilen mesajın net tutarı — donmuş teslim neti. Önceki sürümün henüz
    /// dondurulmamış teslimi (<see cref="ClosedByLaterDelivery"/>) iş kapalıyken okunur: kapalı işte alanlar
    /// gönderilen mesajın alanlarıdır, işi açan ya da alanlarını değiştiren her yazım önce dondurur.</summary>
    public decimal LastRequestedNet => ClosedByLaterDelivery ? RequestedNet : DeliveredNet ?? RequestedNet;

    /// <summary><see cref="PaymentJobSql.FreezeDelivery"/>'nin C# karşılığı (taklit depo; ikisi aynı
    /// kalmalı): önceki sürümün teslimi teslim kaydına geçer, neti eksik teslim kaydının neti işin şu anki
    /// alanlarından yazılır — yazım alanları değiştirmeden ÖNCE çağrılır.</summary>
    public PaymentJob FreezeDelivery()
    {
        if (ClosedByLaterDelivery) return this with { DeliveredAt = ClosedAt, DeliveredNet = RequestedNet };
        return DeliveredAt is not null && DeliveredNet is null ? this with { DeliveredNet = RequestedNet } : this;
    }

    /// <summary>Soruda "son istek" olarak gösterilecek an: sonucu yazılamamış gönderim varsa O denemenin
    /// anı (<see cref="AttemptAt"/>), yoksa son teslim.</summary>
    public long? PromptAt => HasUnconfirmedSend ? AttemptAt : LastDeliveredAt;

    /// <summary>Soruda gösterilecek net: sonucu yazılamamış gönderimde O denemenin neti
    /// (<see cref="AttemptNet"/> — işin alanları denemeden sonra değişmiş olabilir: revizyon, geri alma),
    /// yoksa son teslimin.</summary>
    public decimal PromptNet => HasUnconfirmedSend ? AttemptNet ?? RequestedNet : LastRequestedNet;
}

/// <summary>R2-01..04: ödeme işi yaşam döngüsünün disk katmanı. Tüm geçişler
/// koşullu UPDATE'lerle yarışa dayanıklı; servis katmanı false dönüşünde
/// satırı yeniden okuyup kazananın yazdığını kullanır.</summary>
public interface IPaymentJobStore
{
    /// <summary>Atomik find-or-create: UNIQUE (CustomerId, ScopeKey) +
    /// INSERT OR IGNORE. İki eşzamanlı çağrı aynı satırı görür (R2-04).
    /// Tek satırlık kişide <see cref="FindOrCreateForPerson"/> ile eşdeğerdir (soru ve üye
    /// doğrulaması hariç); ödeme akışı artık kişi biçimini kullanır.</summary>
    PaymentJob FindOrCreate(string customerId, string scopeKey, decimal productTotal);

    /// <summary>
    /// Kişi × kapsam için TEK yazma işleminde (SQLite: <c>BEGIN IMMEDIATE</c>) ara ya da oluştur.
    /// <list type="number">
    /// <item>Üye kümesi işlemin İÇİNDE <see cref="PersonJobRequest.LeasedId"/>'den yeniden okunur;
    ///   anahtar tutmazsa <see cref="PersonJobStatus.MembershipChanged"/> (hiçbir şey yazılmaz).</item>
    /// <item>Üyelerin bu kapsamdaki işleri okunur — kapalılar dahil (tekrar paylaşım), miras kapsamları
    ///   hariç. Ayrıca asıl kapsamı bu kapsam olan miras işler (<see cref="PaymentJob.PriorScopeKey"/>):
    ///   açık olanlar (uzlaştırma bekliyor) ve teslim edilmiş olanlar — kalan iş olamazlar, yalnız
    ///   sorunun kararına ve metnine girerler.</item>
    /// <item><see cref="PersonScopePlan"/>: bir iş kalır, ötekiler ortak
    ///   <see cref="PaymentJobSql.DemoteToLegacy"/> ile indirilir; hiç iş yoksa sahip satırda açılır.
    ///   Soru gerekiyorsa (<see cref="PersonScopePlan.NeedsConfirmation"/>) ve belirteci onaylanmamışsa
    ///   <see cref="PersonJobStatus.ConfirmationRequired"/> — hiçbir şey yazılmaz.</item>
    /// <item>Hazır dönüşte kalan işe (ya da açılan işe) onay damgası yazılır
    ///   (<see cref="PaymentJob.ResumeCovers"/> = <see cref="PersonScopePlan.Covers"/>).</item>
    /// </list>
    /// Sunucu çağrısı yok; kararlar yerel. Kalan iş sahibini korur (para anahtarı değişmez).
    /// </summary>
    PersonJob FindOrCreateForPerson(PersonJobRequest request);

    /// <summary>SALT OKUMA: <see cref="FindOrCreateForPerson"/>'ın baktığı küme — üyelerin bu kapsamdaki
    /// işleri (kapalılar dahil, miras kapsamları hariç) ve asıl kapsamı bu olan miras işler (açık ya da
    /// teslim edilmiş). Yazma, kilit, üye doğrulaması YOK. Fiyat düzeltmesi (K5) "bu kişiye istek gitti
    /// mi, ne kadar, bakiyeden düşüldü mü" sorusunu ödeme sorusuyla AYNI planla
    /// (<see cref="PersonScopePlan"/>) yanıtlar.</summary>
    (IReadOnlyList<PaymentJob> Jobs, IReadOnlyList<PaymentJob> Legacies) GetPersonScopeJobs(
        IReadOnlyCollection<string> memberIds, string scopeKey);

    PaymentJob? Get(string id);

    /// <summary>033'ten taşınan, henüz kapanmamış TÜM miras işleri — en yeniden
    /// eskiye. R4-04: müşteri başına birden fazla olabilir; hepsinin sonucu
    /// öğrenilmeden yeni satışa geçilemez.</summary>
    IReadOnlyList<PaymentJob> GetOpenLegacies(string customerId);

    /// <summary><see cref="GetOpenLegacies(string)"/>'in kişi biçimi: verilen satırların HEPSİNİN açık
    /// miras işleri, en yeniden eskiye. Kişinin başka üyesinde kalmış miras iş de uzlaşır.</summary>
    IReadOnlyList<PaymentJob> GetOpenLegacies(IReadOnlyCollection<string> customerIds);

    /// <summary>Taze işin sahip satırını değiştirir — yalnız iş hiç denenmemişse (created + anahtarsız
    /// + açık) ve hedefte aynı kapsamlı iş yoksa. Kişi düzeyindeki kapsam uzlaştırması (R4-03) sunucuda bu
    /// kapsamdaki düşümü BAŞKA bir üyede bulduğunda iş o üyeye geçer: sunucu anahtarı farklı asıl kayıtla
    /// oynatmayı içerik çakışması sayar. false = koşul tutmadı; yeniden oku.
    ///
    /// <para>Teslim edilmiş taze iş de taşınır (çevrimdışı teslim, geri alma ya da dış iade sonrası yeniden
    /// taze): taşınmasaydı servis o kişi × kapsamda her tıklamada BalanceUncertain dönerdi, uygulama içinden
    /// çıkışı olmazdı. W2: teslimin beklentisi eski sahibin (CustomerId, ScopeKey) anahtarındaysa, teslim
    /// edilmiş işi taşıyan bu yazım beklentiyi yeni sahibe yeniden anahtarlatmalı (eski sahipte
    /// Superseded).</para></summary>
    bool ReassignOwner(string id, string newCustomerId);

    /// <summary>Anahtarı diske indirir ve işi apply_uncertain'e geçirir —
    /// yalnız anahtar HENÜZ yoksa ve iş KAPANMAMIŞSA. false = yarışı kaybettik
    /// (yeniden oku, kazananın anahtarıyla devam et) ya da iş kapandı.
    ///
    /// <para>U8 (Bölüm C): yerel taşıma kopyanın anahtarsız işini miras kapsamına
    /// alıp kapatabilir; o işi uçuşta tutan bir akışın düşümü kapalı işe yazılsaydı
    /// hiç uzlaşmazdı (sonraki tıklama ikinci kez düşerdi).</para></summary>
    bool BeginApply(string id, Guid applyKey);

    /// <summary>Sunucudan kesin "uygulandı" cevabı geldi.
    ///
    /// R4-01: yazım KOŞULLUDUR. Bir cevap, gönderildiği denemeye aittir; o
    /// deneme (<paramref name="expectedRevision"/> + <paramref name="expectedKey"/>)
    /// hâlâ diskteki deneme değilse cevap bayattır ve yazılmaz. Ağda takılıp
    /// geç dönen bir cevabın, araya giren revizyonun sonucunu ezmesi para
    /// hatasıdır: eski tutar yeni satışın düşümü sayılır, müşteriye eksi net
    /// gider. false = yok sayıldı; çağıran satırı yeniden okumalıdır.</summary>
    bool MarkApplied(string id, Guid expectedKey, int expectedRevision, decimal appliedAmount);

    /// <summary>Kesin "bakiye yok" cevabı — AppliedAmount 0 yazılır.
    /// <paramref name="expectedKey"/> null olabilir: önizleme 0 döndüğünde iş
    /// hâlâ anahtarsızdır (created). Koşul kuralı <see cref="MarkApplied"/>
    /// ile aynıdır.</summary>
    bool MarkNoBalance(string id, Guid? expectedKey, int expectedRevision);

    /// <summary>Sonuç yeniden belirsizleşti (ör. geri alma ağda kayboldu).
    /// Koşul kuralı <see cref="MarkApplied"/> ile aynıdır — bayat bir akışın
    /// güncel bir sonucu "bilinmiyor"a düşürmesi de aynı sınıftan hatadır.</summary>
    bool MarkUncertain(string id, Guid? expectedKey, int expectedRevision);

    /// <summary>İşi kapatır (miras uzlaştırması, GUID olmayan miras). Idempotent (ilk zaman korunur).
    /// Teslim kaydı YAZMAZ — mesaj teslimi <see cref="MarkDelivered"/>'dır.</summary>
    void Close(string id);

    /// <summary>Mesaj müşteriye ulaştı: teslim kaydı her teslimde yenilenir — DeliveredAt = şimdi,
    /// DeliveredNet = <paramref name="requestedNet"/>, Deliveries + 1 (tekrar paylaşımda da: "tekrar
    /// gönderilsin mi?" sorusu SON gönderimi gösterir, eski onay belirteci tutmaz). Sonucu kesin iş
    /// (applied / no_balance) ayrıca kapanır (ilk kapanış zamanı korunur). Çevrimdışı gönderilen
    /// taze (created) iş AÇIK kalır: kapalı işe anahtar yazılmaz (U8), onaylı sonraki tıklama onu
    /// çevrimiçi düşümle sürdürür.
    ///
    /// <para>Teslim o ana kadarki BÜTÜN gönderim denemelerini karşılar: SendAttempts = yeni Deliveries.
    /// Daha önce sonucu yazılamamış bir deneme varsa sıradaki tıklama onu operatöre "gitmiş olabilir"
    /// diye sormuş, operatör onaylamıştır; kişi kilidi denemeleri sıraya soktuğu için bu teslim en son
    /// denemenin teslimidir. Fark kapanmasaydı iş kalıcı olarak "sonucu bilinmeyen gönderim" sayılırdı:
    /// soruda saat hiç görünmez, iş her birleştirmede "en son teslim edilen" sayılırdı.</para></summary>
    void MarkDelivered(string id, decimal requestedNet);

    /// <summary>Gönderim denemesi başlıyor: SendAttempts + 1 — mesaj ağa/WhatsApp'a çıkmadan ÖNCE — ve
    /// denemenin neti ve anı (<see cref="PaymentJob.AttemptNet"/>, <see cref="PaymentJob.AttemptAt"/>; öncekiler
    /// <see cref="PaymentJob.PriorAttemptNet"/>/<see cref="PaymentJob.PriorAttemptAt"/>'e). Sonucu
    /// (<see cref="MarkDelivered"/>) yazılamasa da iş "gitmiş olabilir" görünür
    /// (<see cref="PaymentJob.HasUnconfirmedSend"/>), soru o denemenin tutarını ve saatini gösterir ve
    /// bekleyen eski onay belirteci tutmaz.</summary>
    void BeginSend(string id, decimal requestedNet);

    /// <summary>Deneme hiç başlamadı (wa.me açılamadı, mesaj kesin gitmedi): <see cref="BeginSend"/>'in
    /// artışı geri alınır, deneme neti ve anı bir öncekine döner — yalnız sonucu yazılmamış bir deneme varsa.
    /// (Daha eski bir yetim deneme duruyorsa soru onun tutarını ve saatini göstermeli, gitmemiş olanınkini değil.)</summary>
    void AbandonSend(string id);

    /// <summary>Revizyon: yeni toplam + yeni anahtar + Revision+1, eski sonuç
    /// sıfırlanır, durum apply_uncertain. Yalnız Revision == expectedRevision
    /// ise — false = eşzamanlı revizyon kazandı, yeniden oku.</summary>
    bool BeginRevision(string id, decimal newProductTotal, Guid newApplyKey, int expectedRevision);

    /// <summary>R4-02 adım 1: geri alma NİYETİNİ diske indirir — istek tele
    /// çıkmadan önce. Durum <see cref="PaymentJobState.ReversePending"/> olur,
    /// <paramref name="targetTotal"/> PendingTotal'a yazılır, ApplyKey
    /// DEĞİŞMEZ (geri alınacak işlem odur).
    ///
    /// <para>Niyetin önce yazılması meselenin tamamıdır: geri alma cevabı
    /// kaybolsa bile sonraki deneme, isteğin toplamı eski değere dönmüş olsa
    /// dahi, yarım kalmış geri almayı görür.</para>
    ///
    /// <para>Koşul <see cref="MarkApplied"/> ile aynı — bayat bir akış güncel
    /// bir denemeyi geri almaya sokamaz. false = yeniden oku.</para></summary>
    bool BeginReversal(string id, int expectedRevision, Guid expectedKey, decimal targetTotal);

    /// <summary>R4-02 adım 2: geri alma KESİNLEŞTİ (200 ya da 409
    /// already-reversed). İş "taze"ye döner: ProductTotal=PendingTotal,
    /// Revision+1, ApplyKey=NULL, AppliedAmount=NULL, State=created,
    /// PendingTotal=NULL, ClosedAt=NULL.
    ///
    /// <para>Revision artışı şart: uçuştaki eski apply cevapları R4-01 koşuluyla
    /// bayatlar. ApplyKey'in NULL'lanması da şart — geri alınmış bir işlemin
    /// anahtarı bir daha replay edilmemelidir; sunucu onun TARİHSEL sonucunu
    /// döndürür ve geri alınmış tutar geçerli düşüm sanılır (§8'in ta kendisi).</para>
    ///
    /// <para>Yalnız iş hâlâ aynı denemenin reverse_pending'inde ise. false =
    /// yeniden oku.</para></summary>
    bool CompleteReversal(string id, int expectedRevision, Guid expectedKey);

    /// <summary>Legacy işin sonucunu (ApplyKey/AppliedAmount/State/ProductTotal)
    /// TAZE hedefe kopyalar ve legacy'yi kapatır — tek transaction. Hedef taze
    /// değilse (anahtarı varsa) hedefe dokunmaz, yine de legacy'yi kapatır.
    ///
    /// <para>İkinci kat koruma: iki iş AYNI sahip satıra (CustomerId) ait değilse hiçbir şey yazılmaz.
    /// Başka satıra ait düşüm benimsenseydi sunucuya o anahtarla başka WpfCustomerId gider (içerik
    /// çakışması — iş kalıcı belirsiz kalır) ve legacy kapatıldığı için düşüm hiç geri alınmazdı.
    /// Asıl kapsamı bilinen (<see cref="PaymentJob.PriorScopeKey"/>) miras iş de yalnız AYNI kapsamın
    /// işine benimsenir: başka yayının düşümü bu satışa taşınıp revizyonla geri alınsaydı o yayının
    /// ürünleri için kullanılan bakiye iade edilip burada yeniden harcanırdı.</para></summary>
    void AdoptLegacyResult(string targetId, string legacyId);

    /// <summary>R4-03: sunucuda bu kapsam için duran düşümü TAZE işe benimsetir
    /// — iş <see cref="PaymentJobState.Applied"/> olur, ApplyKey sunucudaki
    /// ledger satırının kimliği, AppliedAmount/ProductTotal da sunucunun
    /// bildiği değerler.
    ///
    /// <para>Neden gerekiyor: yedek geri yüklenince satışın yerel kimliği yok
    /// olur, uzak defter düşümü hatırlamaya devam eder ve aynı satış ikinci kez
    /// düşülürdü. Benimseme, yereli sunucunun bildiğiyle hizalar; sepet tutarı
    /// değişmişse normal revizyon dalı (K2) devralır.</para>
    ///
    /// <para>Yalnız iş hiç denenmemişse (created + ApplyKey NULL). Anahtarı olan
    /// bir işi ezmek, hâlâ uçuşta olan bir denemenin cevabını sahipsiz
    /// bırakırdı. false = yeniden oku.</para></summary>
    bool AdoptRemoteResult(string id, Guid transactionId, decimal appliedAmount, decimal productTotal);

    /// <summary>R6-02: sunucudaki düşüm DIŞARIDAN (panel iadesi ya da sunucu
    /// tarafı geri yükleme) geçersizleşti — uygulanmış işi taze duruma döndürür:
    /// State=created, ApplyKey=NULL, AppliedAmount=NULL, Revision+1,
    /// PendingTotal=NULL, ClosedAt=NULL. <see cref="CompleteReversal"/>'ın
    /// aynası; farkı, geri almayı bizim değil dışarının yapmış olması —
    /// ProductTotal bu yüzden DEĞİŞMEZ (hedef toplam diye bir şey yok).
    ///
    /// <para>Revision artışı şart: uçuştaki eski cevaplar R4-01 koşuluyla
    /// bayatlar. Yalnız iş hâlâ aynı denemenin <see cref="PaymentJobState.Applied"/>
    /// hâlindeyse — reverse_pending'e dokunmak R6-03 niyet korumasını deler.
    /// false = araya başka akış girdi; yeniden oku.</para></summary>
    bool AdoptExternalReversal(string id, Guid expectedKey, int expectedRevision);
}

public sealed class PaymentJobRepository : IPaymentJobStore
{
    private readonly IDbConnectionFactory _factory;
    public PaymentJobRepository(IDbConnectionFactory factory) => _factory = factory;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static string Dec(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    public PaymentJob FindOrCreate(string customerId, string scopeKey, decimal productTotal)
    {
        using var conn = _factory.Open();
        var now = Now();
        conn.Execute(
            InsertSql,
            new
            {
                id = Guid.NewGuid().ToString("N"),
                customerId,
                scopeKey,
                total = Dec(productTotal),
                state = PaymentJobState.Created,
                now,
            });
        var row = conn.QuerySingle<Row>(
            SelectSql + " WHERE CustomerId=@customerId AND ScopeKey=@scopeKey",
            new { customerId, scopeKey });
        return Map(row);
    }

    public PersonJob FindOrCreateForPerson(PersonJobRequest request)
    {
        // BEGIN IMMEDIATE (DbWrite): yazma kilidi OKUMADAN önce alınır — iki pencereden aynı kişiye
        // gelen iki akış üye/iş okumasını sırayla yapar; ikincisi birincinin açtığı işi görür.
        // U17: çağıran (ödeme akışı) kiralarını bu paketten ÖNCE almıştır; içeride yalnız paketin
        // bağlantısı kullanılır.
        using var write = DbWrite.Begin(_factory);
        var conn = write.Connection;
        var tx = write.Transaction;

        // Satır Customer tablosunda hiç yoksa kişi yalnız o satırdır (PaymentPerson.Single ile aynı
        // tanım): grup yok, doğrulanacak başka üye de yok. Kiralanan satırı senkron silemez (U13).
        var members = PaymentPersonSql.ReadMembers(conn, tx, request.LeasedId);
        var ids = members.Count > 0 ? members.Select(m => m.Id).ToList() : new List<string> { request.LeasedId };
        if (!string.Equals(PaymentPerson.KeyOf(ids), request.MemberKey, StringComparison.Ordinal))
            return PersonJob.MembershipChanged;                     // Dispose: hiçbir şey yazılmadı

        var jobs = conn.Query<Row>(
                SelectSql + " WHERE CustomerId IN @ids AND ScopeKey=@scopeKey",
                new { ids, scopeKey = request.ScopeKey }, tx)
            .Select(Map).ToList();
        var legacies = conn.Query<Row>(
                SelectSql + " WHERE CustomerId IN @ids AND " + ScopeLegacyCondition,
                new { ids, scopeKey = request.ScopeKey }, tx)
            .Select(Map).ToList();
        var now = Now();

        var plan = jobs.Count + legacies.Count == 0 ? null : PersonScopePlan.For(jobs, legacies, request.OwnerId);
        if (plan is { NeedsConfirmation: true } && request.ConfirmedTokens?.Contains(plan.Token) != true)
            return new PersonJob(PersonJobStatus.ConfirmationRequired, plan.Survivor,
                Array.Empty<DemotedJob>(), plan);                      // Dispose: hiçbir şey yazılmadı

        // Onay damgası: kapı bu işi şimdi geçirdi — gördüğü izler (operatörün onayladığı soru ya da soru
        // gerektirmeyen durum) kalan işin yarım kalabilecek adımında yeniden sorulmaz; sonradan gelenler sorulur.
        var covers = plan?.Covers;

        if (jobs.Count == 0)
        {
            conn.Execute(InsertSql, new
            {
                id = Guid.NewGuid().ToString("N"),
                customerId = request.OwnerId,
                scopeKey = request.ScopeKey,
                total = Dec(request.ProductTotal),
                state = PaymentJobState.Created,
                now,
            }, tx);
            conn.Execute(StampSql + " WHERE CustomerId=@customerId AND ScopeKey=@scopeKey",
                new { covers, customerId = request.OwnerId, scopeKey = request.ScopeKey }, tx);
            var created = Map(conn.QuerySingle<Row>(
                SelectSql + " WHERE CustomerId=@customerId AND ScopeKey=@scopeKey",
                new { customerId = request.OwnerId, scopeKey = request.ScopeKey }, tx));
            write.Commit();
            return PersonJob.Ready(created, Array.Empty<DemotedJob>());
        }

        var demoted = new List<DemotedJob>(plan!.Losers.Count);
        foreach (var loser in plan.Losers)
        {
            conn.Execute(PaymentJobSql.DemoteToLegacy + " WHERE Id=@id", new { id = loser.Id, now }, tx);
            demoted.Add(PersonScopePlan.Demotion(loser));
        }
        conn.Execute(StampSql + " WHERE Id=@id", new { covers, id = plan.Survivor!.Id }, tx);
        write.Commit();
        return PersonJob.Ready(plan.Survivor with { ResumeCovers = covers }, demoted);
    }

    /// <summary>Onay damgası (<see cref="PaymentJob.ResumeCovers"/>) — UpdatedAt'e dokunmaz: işin durumu
    /// değişmedi, yalnız kapıdan geçti.</summary>
    private const string StampSql = "UPDATE PaymentJob SET ResumeCovers=@covers";

    /// <summary>Asıl kapsamı <c>@scopeKey</c> olan miras işler: açık olanlar (uzlaştırma bekliyor) ve
    /// teslim edilmiş olanlar (bu kapsama istek gitmiş ya da gitmiş olabilir — <see cref="PaymentJob.IsDelivered"/>;
    /// miras kapsamında ClosedAt teslim sayılmaz). Kapalı ve teslim edilmemiş miras işin söyleyecek bir
    /// şeyi yok.</summary>
    private const string ScopeLegacyCondition =
        "PriorScopeKey=@scopeKey AND (ScopeKey='legacy' OR ScopeKey LIKE 'legacy:%') " +
        "AND (ClosedAt IS NULL OR DeliveredAt IS NOT NULL OR SendAttempts > Deliveries)";

    private const string InsertSql =
        @"INSERT OR IGNORE INTO PaymentJob
          (Id, CustomerId, ScopeKey, ProductTotal, Revision, ApplyKey,
           AppliedAmount, State, CreatedAt, UpdatedAt, ClosedAt)
          VALUES (@id, @customerId, @scopeKey, @total, 0, NULL,
                  NULL, @state, @now, @now, NULL)";

    public (IReadOnlyList<PaymentJob> Jobs, IReadOnlyList<PaymentJob> Legacies) GetPersonScopeJobs(
        IReadOnlyCollection<string> memberIds, string scopeKey)
    {
        if (memberIds.Count == 0) return (Array.Empty<PaymentJob>(), Array.Empty<PaymentJob>());
        using var conn = _factory.Open();
        var ids = memberIds.ToArray();
        var jobs = conn.Query<Row>(SelectSql + " WHERE CustomerId IN @ids AND ScopeKey=@scopeKey",
            new { ids, scopeKey }).Select(Map).ToList();
        var legacies = conn.Query<Row>(SelectSql + " WHERE CustomerId IN @ids AND " + ScopeLegacyCondition,
            new { ids, scopeKey }).Select(Map).ToList();
        return (jobs, legacies);
    }

    public PaymentJob? Get(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(SelectSql + " WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }

    public IReadOnlyList<PaymentJob> GetOpenLegacies(string customerId)
        => GetOpenLegacies(new[] { customerId });

    public IReadOnlyList<PaymentJob> GetOpenLegacies(IReadOnlyCollection<string> customerIds)
    {
        if (customerIds.Count == 0) return Array.Empty<PaymentJob>();
        using var conn = _factory.Open();
        // 'legacy' (anahtar başına benzersizleştirmeden önce göç edilmiş
        // geliştirme veritabanları) ve 'legacy:{anahtar}' birlikte taranır.
        return conn.Query<Row>(
            SelectSql + @" WHERE CustomerId IN @customerIds AND ClosedAt IS NULL
                             AND (ScopeKey='legacy' OR ScopeKey LIKE 'legacy:%')
                           ORDER BY CreatedAt DESC, Id DESC",
            new { customerIds })
            .Select(Map).ToList();
    }

    public bool ReassignOwner(string id, string newCustomerId)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            @"UPDATE PaymentJob SET CustomerId=@newCustomerId, UpdatedAt=@now
              WHERE Id=@id AND State=@created AND ApplyKey IS NULL AND ClosedAt IS NULL
                AND NOT EXISTS (SELECT 1 FROM PaymentJob t
                                WHERE t.CustomerId=@newCustomerId AND t.ScopeKey=PaymentJob.ScopeKey)",
            new { id, newCustomerId, created = PaymentJobState.Created, now = Now() }) == 1;
    }

    public bool BeginApply(string id, Guid applyKey)
    {
        using var conn = _factory.Open();
        // U8 (Bölüm C): kapalı işe anahtar yazılmaz. Taşıma kopyanın anahtarsız işini
        // legacy:{Id}:{Id} yapıp kapatabilir; uçuştaki bir akış onu tutuyorsa düşüm kapalı
        // miras işine yazılır ve hiç uzlaşmazdı (sonraki tıklama ikinci kez düşerdi). Normal
        // akışta FindOrCreate'in döndürdüğü kapalı iş hep applied/no_balance'tır — buraya
        // hiç gelmez. Asıl koruma kiradır (U13, CustomerBusySet); bu koşul ikinci katman.
        return conn.Execute(
            @"UPDATE PaymentJob
              SET ApplyKey=@key, State=@state, UpdatedAt=@now
              WHERE Id=@id AND ApplyKey IS NULL AND ClosedAt IS NULL",
            new
            {
                id,
                key = applyKey.ToString("N"),
                state = PaymentJobState.ApplyUncertain,
                now = Now(),
            }) == 1;
    }

    // R4-01: sonucu yazan üç metnin ortak koşulu. "ApplyKey IS @key" — SQLite'ın
    // null-güvenli karşılaştırması; anahtarsız (created) işte de doğru çalışır,
    // "= NULL" ise her zaman false dönerdi. Durum (State) koşula BİLEREK
    // girmiyor: bayat cevabı bayat yapan şey denemenin kimliğidir (Revision +
    // ApplyKey), o denemenin şu anki durumu değil — aynı denemenin sonucunu
    // ikinci kez yazmak zararsız tekrardır (replay bunu yapar).
    private const string StaleGuard = " WHERE Id=@id AND Revision=@rev AND ApplyKey IS @key";

    // R6-03: sonucu yazan üç metin için StaleGuard TEK BAŞINA yetmiyor. R4-02
    // reverse_pending aşamasını ekledi ve BeginReversal, geri alınacak işlemi
    // adresleyebilmek için Revision/ApplyKey'i bilerek koruyor. Yani aynı
    // denemenin uçuştaki cevabı geri alma niyetiyle AYNI kimliği taşıyor ve
    // StaleGuard'ı geçiyordu: niyet 'applied'a eziliyor, FinishReversalAsync
    // bir daha çalışmıyor, sunucudaki düşüm iade edilmeden kalıyordu. Aşama
    // koşulu R4-01'i bozmaz — niyet yokken aynı sonucu tekrar yazmak hâlâ
    // serbest (replay bunu yapar); yalnız geri alma bekleyen iş dokunulmaz
    // olur. Niyeti kaldırmanın tek yolu CompleteReversal'dır.
    private const string ResultGuard = StaleGuard + " AND State<>@reversePending";

    private static string? KeyText(Guid? key) => key?.ToString("N");

    public bool MarkApplied(string id, Guid expectedKey, int expectedRevision, decimal appliedAmount)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            "UPDATE PaymentJob SET " + PaymentJobSql.FreezeDelivery + "State=@state, AppliedAmount=@amt, UpdatedAt=@now" + ResultGuard,
            new
            {
                id,
                rev = expectedRevision,
                key = KeyText(expectedKey),
                state = PaymentJobState.Applied,
                amt = Dec(appliedAmount),
                now = Now(),
                reversePending = PaymentJobState.ReversePending,
            }) == 1;
    }

    public bool MarkNoBalance(string id, Guid? expectedKey, int expectedRevision)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            "UPDATE PaymentJob SET " + PaymentJobSql.FreezeDelivery + "State=@state, AppliedAmount=@amt, UpdatedAt=@now" + ResultGuard,
            new
            {
                id,
                rev = expectedRevision,
                key = KeyText(expectedKey),
                state = PaymentJobState.NoBalance,
                amt = Dec(0m),
                now = Now(),
                reversePending = PaymentJobState.ReversePending,
            }) == 1;
    }

    public bool MarkUncertain(string id, Guid? expectedKey, int expectedRevision)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            "UPDATE PaymentJob SET State=@state, UpdatedAt=@now" + ResultGuard,
            new
            {
                id,
                rev = expectedRevision,
                key = KeyText(expectedKey),
                state = PaymentJobState.ApplyUncertain,
                now = Now(),
                reversePending = PaymentJobState.ReversePending,
            }) == 1;
    }

    public void Close(string id)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE PaymentJob SET ClosedAt=COALESCE(ClosedAt,@now), UpdatedAt=@now WHERE Id=@id",
            new { id, now = Now() });
    }

    public void MarkDelivered(string id, decimal requestedNet)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE PaymentJob
              SET DeliveredAt=@now, DeliveredNet=@net, Deliveries=Deliveries+1, UpdatedAt=@now,
                  SendAttempts=Deliveries+1,
                  ClosedAt=CASE WHEN State IN (@applied, @noBalance) THEN COALESCE(ClosedAt,@now) ELSE ClosedAt END
              WHERE Id=@id",
            new
            {
                id,
                net = Dec(requestedNet),
                now = Now(),
                applied = PaymentJobState.Applied,
                noBalance = PaymentJobState.NoBalance,
            });
    }

    public void BeginSend(string id, decimal requestedNet)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE PaymentJob
              SET SendAttempts=SendAttempts+1, PriorAttemptNet=AttemptNet, AttemptNet=@net,
                  PriorAttemptAt=AttemptAt, AttemptAt=@now, UpdatedAt=@now
              WHERE Id=@id",
            new { id, net = Dec(requestedNet), now = Now() });
    }

    public void AbandonSend(string id)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE PaymentJob SET SendAttempts=SendAttempts-1, AttemptNet=PriorAttemptNet, AttemptAt=PriorAttemptAt,
                                    UpdatedAt=@now
              WHERE Id=@id AND SendAttempts > Deliveries",
            new { id, now = Now() });
    }

    public bool BeginRevision(string id, decimal newProductTotal, Guid newApplyKey, int expectedRevision)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            "UPDATE PaymentJob SET " + PaymentJobSql.FreezeDelivery + @"
                  ProductTotal=@total, Revision=Revision+1, ApplyKey=@key,
                  AppliedAmount=NULL, State=@state, UpdatedAt=@now, ClosedAt=NULL
              WHERE Id=@id AND Revision=@expectedRevision",
            new
            {
                id,
                total = Dec(newProductTotal),
                key = newApplyKey.ToString("N"),
                state = PaymentJobState.ApplyUncertain,
                now = Now(),
                expectedRevision,
            }) == 1;
    }

    public bool BeginReversal(string id, int expectedRevision, Guid expectedKey, decimal targetTotal)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            "UPDATE PaymentJob SET State=@state, PendingTotal=@pending, UpdatedAt=@now" + StaleGuard,
            new
            {
                id,
                rev = expectedRevision,
                key = KeyText(expectedKey),
                state = PaymentJobState.ReversePending,
                pending = Dec(targetTotal),
                now = Now(),
            }) == 1;
    }

    public bool CompleteReversal(string id, int expectedRevision, Guid expectedKey)
    {
        using var conn = _factory.Open();
        // COALESCE: PendingTotal'ın boş olması imkânsız (BeginReversal onu yazar),
        // ama boş olsaydı ProductTotal'ı NULL'lamak satırı okunamaz hâle
        // getirirdi — eski toplamda kalmak tek güvenli düşüştür.
        return conn.Execute(
            "UPDATE PaymentJob SET " + PaymentJobSql.FreezeDelivery + @"
                  ProductTotal=COALESCE(PendingTotal, ProductTotal), Revision=Revision+1,
                  ApplyKey=NULL, AppliedAmount=NULL, State=@state,
                  PendingTotal=NULL, ClosedAt=NULL, UpdatedAt=@now
              WHERE Id=@id AND Revision=@rev AND ApplyKey IS @key AND State=@pending",
            new
            {
                id,
                rev = expectedRevision,
                key = KeyText(expectedKey),
                state = PaymentJobState.Created,
                pending = PaymentJobState.ReversePending,
                now = Now(),
            }) == 1;
    }

    public void AdoptLegacyResult(string targetId, string legacyId)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();
        var now = Now();
        // İkinci kat koruma (servis zaten yalnız aynı sahipte ve aynı kapsamda çağırır): başka satıra
        // ya da başka kapsama ait düşüm benimsenmez ve legacy de KAPATILMAZ — kapatmak geri alınmamış
        // düşümü gömerdi.
        var adoptable = conn.ExecuteScalar<long>(
            @"SELECT COUNT(*) FROM PaymentJob t JOIN PaymentJob l ON l.Id=@legacyId
              WHERE t.Id=@targetId AND t.CustomerId=l.CustomerId
                AND (l.PriorScopeKey IS NULL OR l.PriorScopeKey=t.ScopeKey)",
            new { targetId, legacyId }, tx) == 1;
        if (!adoptable) return;
        conn.Execute(
            "UPDATE PaymentJob SET " + PaymentJobSql.FreezeDelivery + @"
                  ProductTotal  = (SELECT ProductTotal  FROM PaymentJob WHERE Id=@legacyId),
                  ApplyKey      = (SELECT ApplyKey      FROM PaymentJob WHERE Id=@legacyId),
                  AppliedAmount = (SELECT AppliedAmount FROM PaymentJob WHERE Id=@legacyId),
                  State         = (SELECT State         FROM PaymentJob WHERE Id=@legacyId),
                  UpdatedAt     = @now
              WHERE Id=@targetId AND ApplyKey IS NULL AND State=@created
                AND (SELECT CustomerId FROM PaymentJob WHERE Id=@legacyId) = PaymentJob.CustomerId",
            new { targetId, legacyId, now, created = PaymentJobState.Created },
            tx);
        conn.Execute(
            "UPDATE PaymentJob SET ClosedAt=COALESCE(ClosedAt,@now), UpdatedAt=@now WHERE Id=@legacyId",
            new { legacyId, now },
            tx);
        tx.Commit();
    }

    public bool AdoptRemoteResult(
        string id, Guid transactionId, decimal appliedAmount, decimal productTotal)
    {
        using var conn = _factory.Open();
        // Revision ARTMIYOR: benimseme yeni bir deneme açmıyor, var olan
        // sonucu yerele taşıyor. Artırmak, uçuştaki bir cevabı bayatlatırdı —
        // oysa koşul zaten "hiç deneme yok" (ApplyKey IS NULL) diyor.
        return conn.Execute(
            "UPDATE PaymentJob SET " + PaymentJobSql.FreezeDelivery + @"
                  ApplyKey=@key, AppliedAmount=@amt, ProductTotal=@total,
                  State=@state, UpdatedAt=@now
              WHERE Id=@id AND ApplyKey IS NULL AND State=@created",
            new
            {
                id,
                key = transactionId.ToString("N"),
                amt = Dec(appliedAmount),
                total = Dec(productTotal),
                state = PaymentJobState.Applied,
                created = PaymentJobState.Created,
                now = Now(),
            }) == 1;
    }

    public bool AdoptExternalReversal(string id, Guid expectedKey, int expectedRevision)
    {
        using var conn = _factory.Open();
        // ProductTotal korunur: dış iade "toplam değişti" demek değildir —
        // sıradaki tıklama aynı toplamla taze apply yapar (karar operatörün).
        return conn.Execute(
            "UPDATE PaymentJob SET " + PaymentJobSql.FreezeDelivery + @"
                  Revision=Revision+1, ApplyKey=NULL, AppliedAmount=NULL,
                  State=@created, PendingTotal=NULL, ClosedAt=NULL, UpdatedAt=@now
              WHERE Id=@id AND Revision=@rev AND ApplyKey IS @key AND State=@applied",
            new
            {
                id,
                rev = expectedRevision,
                key = KeyText(expectedKey),
                created = PaymentJobState.Created,
                applied = PaymentJobState.Applied,
                now = Now(),
            }) == 1;
    }

    private const string SelectSql =
        @"SELECT Id, CustomerId, ScopeKey, ProductTotal, Revision, ApplyKey,
                 AppliedAmount, State, CreatedAt, UpdatedAt, ClosedAt, PendingTotal, PriorScopeKey,
                 DeliveredAt, DeliveredNet, Deliveries, SendAttempts, AttemptNet, PriorAttemptNet,
                 AttemptAt, PriorAttemptAt, ResumeCovers
          FROM PaymentJob";

    private static PaymentJob Map(Row r) => new(
        r.Id,
        r.CustomerId,
        r.ScopeKey,
        decimal.Parse(r.ProductTotal, CultureInfo.InvariantCulture),
        r.Revision,
        r.ApplyKey is null ? null : Guid.ParseExact(r.ApplyKey, "N"),
        r.AppliedAmount is null
            ? null
            : decimal.Parse(r.AppliedAmount, CultureInfo.InvariantCulture),
        r.State,
        r.CreatedAt,
        r.UpdatedAt,
        r.ClosedAt,
        r.PendingTotal is null
            ? null
            : decimal.Parse(r.PendingTotal, CultureInfo.InvariantCulture),
        r.PriorScopeKey,
        r.DeliveredAt,
        r.DeliveredNet is null ? null : decimal.Parse(r.DeliveredNet, CultureInfo.InvariantCulture),
        r.Deliveries,
        r.SendAttempts,
        DecOrNull(r.AttemptNet),
        DecOrNull(r.PriorAttemptNet),
        r.AttemptAt,
        r.PriorAttemptAt,
        r.ResumeCovers);

    private static decimal? DecOrNull(string? text)
        => text is null ? null : decimal.Parse(text, CultureInfo.InvariantCulture);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string ScopeKey { get; init; } = "";
        public string ProductTotal { get; init; } = "0";
        public int Revision { get; init; }
        public string? ApplyKey { get; init; }
        public string? AppliedAmount { get; init; }
        public string State { get; init; } = "";
        public long CreatedAt { get; init; }
        public long UpdatedAt { get; init; }
        public long? ClosedAt { get; init; }
        public string? PendingTotal { get; init; }
        public string? PriorScopeKey { get; init; }
        public long? DeliveredAt { get; init; }
        public string? DeliveredNet { get; init; }
        public int Deliveries { get; init; }
        public int SendAttempts { get; init; }
        public string? AttemptNet { get; init; }
        public string? PriorAttemptNet { get; init; }
        public long? AttemptAt { get; init; }
        public long? PriorAttemptAt { get; init; }
        public string? ResumeCovers { get; init; }
    }
}
