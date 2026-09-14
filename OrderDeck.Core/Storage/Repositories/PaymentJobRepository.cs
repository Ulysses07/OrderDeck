using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dapper;

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
/// Kapsam: (CustomerId, ScopeKey) — "session:{id}" | "cumulative" | "legacy".
/// İşler ASLA silinmez; teslimatta ClosedAt dolar.</summary>
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
    decimal? PendingTotal = null);

/// <summary>R2-01..04: ödeme işi yaşam döngüsünün disk katmanı. Tüm geçişler
/// koşullu UPDATE'lerle yarışa dayanıklı; servis katmanı false dönüşünde
/// satırı yeniden okuyup kazananın yazdığını kullanır.</summary>
public interface IPaymentJobStore
{
    /// <summary>Atomik find-or-create: UNIQUE (CustomerId, ScopeKey) +
    /// INSERT OR IGNORE. İki eşzamanlı çağrı aynı satırı görür (R2-04).</summary>
    PaymentJob FindOrCreate(string customerId, string scopeKey, decimal productTotal);

    PaymentJob? Get(string id);

    /// <summary>033'ten taşınan, henüz kapanmamış TÜM miras işleri — en yeniden
    /// eskiye. R4-04: müşteri başına birden fazla olabilir; hepsinin sonucu
    /// öğrenilmeden yeni satışa geçilemez.</summary>
    IReadOnlyList<PaymentJob> GetOpenLegacies(string customerId);

    /// <summary>Anahtarı diske indirir ve işi apply_uncertain'e geçirir —
    /// yalnız anahtar HENÜZ yoksa. false = yarışı kaybettik; yeniden oku,
    /// kazananın anahtarıyla devam et.</summary>
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

    /// <summary>Mesaj müşteriye ulaştı — iş kapanır. Idempotent (ilk zaman korunur).</summary>
    void Close(string id);

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
    /// değilse (anahtarı varsa) hedefe dokunmaz, yine de legacy'yi kapatır.</summary>
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
            @"INSERT OR IGNORE INTO PaymentJob
              (Id, CustomerId, ScopeKey, ProductTotal, Revision, ApplyKey,
               AppliedAmount, State, CreatedAt, UpdatedAt, ClosedAt)
              VALUES (@id, @customerId, @scopeKey, @total, 0, NULL,
                      NULL, @state, @now, @now, NULL)",
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

    public PaymentJob? Get(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(SelectSql + " WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }

    public IReadOnlyList<PaymentJob> GetOpenLegacies(string customerId)
    {
        using var conn = _factory.Open();
        // 'legacy' (anahtar başına benzersizleştirmeden önce göç edilmiş
        // geliştirme veritabanları) ve 'legacy:{anahtar}' birlikte taranır.
        return conn.Query<Row>(
            SelectSql + @" WHERE CustomerId=@customerId AND ClosedAt IS NULL
                             AND (ScopeKey='legacy' OR ScopeKey LIKE 'legacy:%')
                           ORDER BY CreatedAt DESC, Id DESC",
            new { customerId })
            .Select(Map).ToList();
    }

    public bool BeginApply(string id, Guid applyKey)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            @"UPDATE PaymentJob
              SET ApplyKey=@key, State=@state, UpdatedAt=@now
              WHERE Id=@id AND ApplyKey IS NULL",
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
            "UPDATE PaymentJob SET State=@state, AppliedAmount=@amt, UpdatedAt=@now" + ResultGuard,
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
            "UPDATE PaymentJob SET State=@state, AppliedAmount=@amt, UpdatedAt=@now" + ResultGuard,
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

    public bool BeginRevision(string id, decimal newProductTotal, Guid newApplyKey, int expectedRevision)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            @"UPDATE PaymentJob
              SET ProductTotal=@total, Revision=Revision+1, ApplyKey=@key,
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
            @"UPDATE PaymentJob
              SET ProductTotal=COALESCE(PendingTotal, ProductTotal), Revision=Revision+1,
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
        conn.Execute(
            @"UPDATE PaymentJob SET
                  ProductTotal  = (SELECT ProductTotal  FROM PaymentJob WHERE Id=@legacyId),
                  ApplyKey      = (SELECT ApplyKey      FROM PaymentJob WHERE Id=@legacyId),
                  AppliedAmount = (SELECT AppliedAmount FROM PaymentJob WHERE Id=@legacyId),
                  State         = (SELECT State         FROM PaymentJob WHERE Id=@legacyId),
                  UpdatedAt     = @now
              WHERE Id=@targetId AND ApplyKey IS NULL AND State=@created",
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
            @"UPDATE PaymentJob
              SET ApplyKey=@key, AppliedAmount=@amt, ProductTotal=@total,
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
            @"UPDATE PaymentJob
              SET Revision=Revision+1, ApplyKey=NULL, AppliedAmount=NULL,
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
                 AppliedAmount, State, CreatedAt, UpdatedAt, ClosedAt, PendingTotal
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
            : decimal.Parse(r.PendingTotal, CultureInfo.InvariantCulture));

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
    }
}
