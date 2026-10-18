using System;
using System.Collections.Generic;
using System.Linq;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;

namespace OrderDeck.Core.Sales;

public sealed class LabelService
{
    private readonly LabelRepository _labels;
    private readonly CustomerService _customers;
    private readonly IDbConnectionFactory _factory;
    private readonly IClock _clock;
    private readonly ShipmentRepository _shipments;

    /// <param name="shipments">Fiyat düzeltmesi bağlı kargo dosyasının toplamını aynı pakette düzeltir.
    /// DI tekil örneği verir; verilmezse (testler) aynı bağlantı fabrikası üstünde kurulur — depo
    /// durumsuzdur.</param>
    public LabelService(
        LabelRepository labels,
        CustomerService customers,
        IDbConnectionFactory factory,
        IClock clock,
        ShipmentRepository? shipments = null)
    {
        _labels = labels;
        _customers = customers;
        _factory = factory;
        _clock = clock;
        _shipments = shipments ?? new ShipmentRepository(factory);
    }

    /// <summary>
    /// Queues a new (unprinted) label for the given chat message + price snapshot.
    /// Auto-creates the Customer on first encounter.
    /// <paramref name="isBackupPromoted"/> drives the small "Y" stamp on the
    /// printed sticker.
    /// </summary>
    public Label Add(string sessionId, ChatMessage message, decimal price, string? code,
        bool isBackupPromoted = false,
        string? parentLabelId = null,
        bool isTentativeBackup = false,
        string? productId = null, string? productVariantId = null)
    {
        // U12: müşteri çözümü/açılışı ve etiket TEK IMMEDIATE işlemde. Ayrı yazımlar arasında
        // yerel taşıma (push yanıtı) müşteriyi silebilir → FK hatası = kayıp satış. Paket
        // açıkken her okuma ve yazma paketin bağlantısıyla (U17).
        using var write = DbWrite.Begin(_factory);
        var customer = _customers.GetOrCreate(
            message.Platform, message.Username, message.DisplayName, message.AvatarUrl, write);

        var label = new Label(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            CustomerId: customer.Id,
            Platform: message.Platform,
            Username: message.Username,
            MessageText: message.Text,
            Code: code,
            Price: price,
            AddedAt: _clock.UnixNow(),
            PrintedAt: null,
            IsBackupPromoted: isBackupPromoted,
            ParentLabelId: parentLabelId,
            IsTentativeBackup: isTentativeBackup,
            DisplayName: message.DisplayName,
            ProductId: productId,
            ProductVariantId: productVariantId);

        _labels.Insert(label, write);
        write.Commit();
        return label;
    }

    /// <summary>
    /// Kargo PR B (2026-05-11): Müşterinin sipariş toplamı kargo eşiğinin
    /// altında kaldığında otomatik kargo ücreti satırı ekler. Normal Add()
    /// gibi customer ekosistemine bağlanır (CustomerService.GetOrCreate yapılmaz
    /// çünkü customer zaten mevcut — bu helper varolan müşteri için çağrılır).
    ///
    /// PR C'de DekontEkleDialog kaydet sonrası matcher service bunu çağıracak;
    /// şu an public API olarak sadece test edilebilirlik için duruyor.
    /// </summary>
    /// <param name="sessionId">Hangi yayında ekleniyor (aktif yayın id'si).</param>
    /// <param name="customer">Kime kargo eklenecek (lookup'lı çağrıdan gelir).</param>
    /// <param name="amount">Kargo ücreti (TL) — AppSettings.Shipping.ShippingFee.</param>
    public Label AddShippingFee(string sessionId, Customers.Customer customer, decimal amount)
    {
        var label = new Label(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            CustomerId: customer.Id,
            Platform: customer.Platform,
            Username: customer.Username,
            MessageText: $"Kargo Ücreti — {amount:0.##} TL",
            Code: null,
            Price: amount,
            AddedAt: _clock.UnixNow(),
            PrintedAt: null,
            IsBackupPromoted: false,
            ParentLabelId: null,
            IsTentativeBackup: false,
            DisplayName: customer.DisplayName,
            IsShippingFee: true);

        _labels.Insert(label);
        return label;
    }

    public IReadOnlyList<Label> GetQueue(string sessionId) =>
        _labels.GetUnprintedBySession(sessionId);

    // ─── Müşteri toplamları (spec 2026-10-13 §6.1, W1) ─────────────────────
    // Toplam yazan beş olay (iptal, iptali geri alma, baskı, fiyat düzeltme, yedek onayı) müşterinin
    // toplamını fark sayacıyla değil, etiketin yeni hâli yazıldıktan SONRA aynı pakette etiketlerden
    // türeterek yazar (CustomerAggregateSql.Recompute): iki kez uygulanan ya da kaçan bir olay toplamı
    // artık kaydıramaz, eski kayma da ilk olayda düzelir. Olayın etiketlerinin bütün müşterileri yeniden
    // hesaplanır (yalnız değeri değişen satır yazılır). LastSeenAt ayrı ifadedir ve BUGÜNKÜ anlamını
    // korur: yalnız eskiden sayaca dokunan müşteriler (TouchLastSeen).

    // ─── Kargo dosyası payı (sahip kararı Y5, spec 2026-10-13 §7.4, W1) ──────
    // İptal BAĞI ÇÖZMEZ: etiket dosyaya bağlı kalır (ShipmentId "dekontla ödendi"nin tek işareti, §7.2),
    // dosyanın tutarı ve eşik havuzu etiketin fiyatı kadar düşer; iptali geri almak geri ekler. Geçici
    // yedek dosya tutarında hiç yoktur; kargoya verilmiş dosyanın tutarı donmuştur; başka bilgisayarın
    // (yerelde olmayan) dosyası atlanır; mod On'da blok atlanır, tutarı sunucu türetir; fark tabanı
    // henüz türetilmemişse de yazmaz (ShipmentAmountAdjuster). Etiket, dosya ve müşteri toplamı AYNI pakette. Pay, paketin içinde
    // okunan hâlden hesaplanır: okuma ile yazma arasında başka bir yol etiketi iptal ettiyse (ya da geri
    // aldıysa) o etiket ne çevrilir ne de dosyadan ikinci kez düşülür.

    /// <summary>Soft-cancel: marks labels as cancelled with a reason code (one of the
    /// preset codes from CancelReasonCodes). Already-cancelled labels are silently
    /// ignored. The customer's printed-label totals are re-derived from the labels in
    /// the same transaction (§6.1); an attached, non-tentative label's price leaves its
    /// open shipment's amount (Y5, §7.4) — the link stays.
    ///
    /// LastSeenAt moves only for customers of printed, non-tentative rows — the rows
    /// that ever contributed to the totals.</summary>
    public void Cancel(IReadOnlyList<string> labelIds, string reason)
    {
        if (labelIds.Count == 0) return;

        var customers = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var idsToFlip = new List<string>(labelIds.Count);
        foreach (var id in labelIds)
        {
            var lbl = _labels.GetById(id);
            if (lbl is null || lbl.CancelledAt.HasValue) continue;
            idsToFlip.Add(id);
            customers.Add(lbl.CustomerId);
            // Only printed AND non-tentative rows ever touched the totals.
            if (lbl.PrintedAt.HasValue && !lbl.IsTentativeBackup) seen.Add(lbl.CustomerId);
        }
        if (idsToFlip.Count == 0) return;

        // Etiket damgası ve müşteri toplamı AYNI pakette. Buradaki okumalar
        // (yukarıdaki GetById döngüsü) paket açılmadan önce bitti — bu kasıtlı:
        // paket açıkken ikinci bir bağlantıya gitmek SQLite'ta kilide düşer.
        //
        // Burada eskiden elle yazılmış bir telafi vardı: müşteri toplamı
        // patlarsa etiketleri geri çevirip toplamları geri alıyordu. Silindi,
        // çünkü telafinin patlama sebebi asıl hatanınkiyle aynı olma
        // eğilimindedir (kilitli dosya, dolu disk) — yani tam gerektiği anda
        // çalışmıyordu, üstelik hatası yutulduğu için sessizce. Süreç ortada
        // ölürse zaten hiç çalışmıyordu. Geri sarmayı artık veritabanı yapıyor.
        using var write = DbWrite.Begin(_factory);
        var now = _clock.UnixNow();
        var flipping = _labels.GetByIds(idsToFlip, write).Where(l => !l.CancelledAt.HasValue).ToList();
        if (flipping.Count == 0) return;
        _labels.MarkCancelled(flipping.Select(l => l.Id).ToList(), now, reason, write);
        ShipmentAmountAdjuster.Apply(ShipmentAmountAdjuster.ShareDeltas(flipping, sign: -1), write);
        CustomerAggregateSql.Recompute(customers, write);
        CustomerAggregateSql.TouchLastSeen(seen, now, write);
        write.Commit();
    }

    /// <summary>Reverses Cancel — the customer's totals are re-derived from the
    /// labels so they reflect the un-cancelled rows again (§6.1); an attached,
    /// non-tentative label's price returns to its open shipment's amount (Y5, §7.4).</summary>
    public void Uncancel(IReadOnlyList<string> labelIds)
    {
        if (labelIds.Count == 0) return;

        var customers = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var idsToFlip = new List<string>(labelIds.Count);
        foreach (var id in labelIds)
        {
            var lbl = _labels.GetById(id);
            if (lbl is null || !lbl.CancelledAt.HasValue) continue;
            idsToFlip.Add(id);
            customers.Add(lbl.CustomerId);
            // Mirror Cancel: LastSeenAt only for rows that contribute again.
            if (lbl.PrintedAt.HasValue && !lbl.IsTentativeBackup) seen.Add(lbl.CustomerId);
        }
        if (idsToFlip.Count == 0) return;

        // Türetme etiketin YENİ hâlinden okur: önce geri alma, sonra hesap — aynı pakette.
        using var write = DbWrite.Begin(_factory);
        var flipping = _labels.GetByIds(idsToFlip, write).Where(l => l.CancelledAt.HasValue).ToList();
        if (flipping.Count == 0) return;
        _labels.Uncancel(flipping.Select(l => l.Id).ToList(), write);
        ShipmentAmountAdjuster.Apply(ShipmentAmountAdjuster.ShareDeltas(flipping, sign: +1), write);
        CustomerAggregateSql.Recompute(customers, write);
        CustomerAggregateSql.TouchLastSeen(seen, _clock.UnixNow(), write);
        write.Commit();
    }

    private static long _lastPrintBatchTicks;

    /// <summary>Basış kimliği: basış SIRASIYLA artar (sıralı metin karşılaştırmasında da). "Basılanlar"
    /// grupları önce PrintedAt'e (saniye) göre sıralar; aynı saniyedeki iki "Yazdır"ın sırasını bu kimlik
    /// verir — rastgele kimlikte eskisi yarı yarıya üstte çıkardı (K1: en yeni basış en üstte). Önek UTC
    /// tik sayısı (sabit 16 onaltılık hane; süreç içinde her çağrıda en az bir artar), sonek rastgele.</summary>
    public static string NewPrintBatchId()
    {
        long ticks;
        while (true)
        {
            var last = System.Threading.Interlocked.Read(ref _lastPrintBatchTicks);
            ticks = Math.Max(DateTime.UtcNow.Ticks, last + 1);
            if (System.Threading.Interlocked.CompareExchange(ref _lastPrintBatchTicks, ticks, last) == last) break;
        }
        return ticks.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) +
               Guid.NewGuid().ToString("N")[..16];
    }

    /// <summary>
    /// Marks the given labels as printed and re-derives the printed customers' totals
    /// (§6.1). Tentative backup labels still get PrintedAt set (they physically come out of
    /// the printer) but DO NOT contribute to customer totals — the sale isn't
    /// real until the operator confirms the backup via <see cref="ConfirmBackup"/>.
    ///
    /// <para>Göç 048: <paramref name="labelIds"/> KÂĞIT SIRASIDIR (yazıcıya giden liste). Çağrı bir
    /// "Yazdır" basışıdır: satırlar yeni bir basış kimliği ve kâğıttaki konumlarıyla (1'den)
    /// damgalanır — "Basılanlar" sekmesi bunlarla gruplar ve sıralar. Atlanan satır (zaten basılmış
    /// ya da iptal) konumunu korur: kâğıt yığınındaki yeri değişmez.</para>
    /// </summary>
    public void MarkPrintedAndRecord(IReadOnlyList<string> labelIds)
    {
        if (labelIds.Count == 0) return;
        MarkPrintedAndRecord(labelIds, NewPrintBatchId(), Enumerable.Range(1, labelIds.Count).ToList());
    }

    /// <summary>
    /// <see cref="MarkPrintedAndRecord(IReadOnlyList{string})"/>'ın basışı dışarıdan verilen hâli (spec 2026-10-13 §5.6-4,
    /// §5.5): karışık kâğıtta (canlı yayının satırları + devredilmiş yayının kalıntısı) yerel satırlar burada, sunucu
    /// satırları <c>mark-printed</c> komutuyla AYNI basış kimliği ve kesintisiz sırayla damgalanır; mod Off'a dönüşte
    /// bekleyen <c>mark-printed</c> da aynı kimlik ve sırayla yerel yazıma çevrilir. Atlanan satır (zaten basılmış ya da
    /// iptal) yazılmaz, öbürlerinin konumu değişmez.
    /// </summary>
    /// <param name="labelIds">Yerelde damgalanacak etiketler, kâğıt sırasıyla.</param>
    /// <param name="batchId">Basışın kimliği (<see cref="NewPrintBatchId"/>).</param>
    /// <param name="seqs">Her etiketin kâğıttaki konumu (1'den), <paramref name="labelIds"/> ile aynı sırada.</param>
    public void MarkPrintedAndRecord(IReadOnlyList<string> labelIds, string batchId, IReadOnlyList<int> seqs)
    {
        if (labelIds.Count == 0) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        if (seqs.Count != labelIds.Count)
            throw new ArgumentException("Her etiketin kâğıttaki konumu verilmeli.", nameof(seqs));

        var customers = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var idsToPrint = new List<(string Id, int Seq)>(labelIds.Count);

        for (var i = 0; i < labelIds.Count; i++)
        {
            var id = labelIds[i];
            var seq = seqs[i];
            var lbl = _labels.GetById(id);
            if (lbl is null) continue;

            // R7-04: bu metot "işi tamamla" demek, "kâğıdı yeniden çıkar"
            // değil. Baskı arka planda beklerken EndStream/ClearQueue aynı
            // etiketi tekrar gönderebiliyordu; transaction atomikti ama iki
            // kez BAŞARILI olması ciroyu ikinci kez artırıyordu. İptal edilmiş
            // satır daha da kötüsüydü: Cancel ciroyu düşürdükten sonra baskı
            // onu geri ekliyor, satır hem iptalli hem basılı kalıyordu.
            // Ekonomik tamamlanma etiketin kendisine ait — satır zaten
            // sonuçlanmışsa paketin geri kalanı bundan etkilenmez.
            if (lbl.PrintedAt.HasValue || lbl.CancelledAt.HasValue) continue;

            idsToPrint.Add((id, seq));
            customers.Add(lbl.CustomerId);
            if (!lbl.IsTentativeBackup) seen.Add(lbl.CustomerId); // tentative rows: print only, no total
        }
        if (idsToPrint.Count == 0) return;

        // Bu metot dördü içinde en kötü durumdaydı: telafisi hiç yoktu.
        // Müşteri toplamı patladığında etiketler "basıldı" olarak kalıyor,
        // toplamlar geri alınıyordu — ve kod bunu "accept the divergence"
        // diyerek kabul ediyordu, çünkü geri almak için UnmarkPrinted diye bir
        // ilkel gerekiyordu. Pakete alınca o ilkele hiç gerek kalmıyor:
        // yazılmamış bir UPDATE'i geri almak gerekmez.
        using var write = DbWrite.Begin(_factory);
        var now = _clock.UnixNow();
        _labels.MarkPrintedBatch(idsToPrint, now, batchId, write);
        CustomerAggregateSql.Recompute(customers, write);
        CustomerAggregateSql.TouchLastSeen(seen, now, write);
        write.Commit();
    }

    // ─── Fiyat düzeltme (sahip kararları K4/K5, 2026-10-12) ──────────────────
    // Fiyat etiket yazılırken her etikete kopyalanıyor (Add); yanlış girilmiş
    // fiyatı sonradan değiştiren tek akış bu. Etiketin kimliği aynı kalır —
    // iptal + yeniden yazma yolunun ürettiği ikinci "yeni sipariş", Shopper'daki
    // iptal satırları ve kargo dosyasındaki çift sayım oluşmaz.

    /// <summary>Seçilen etiketleri sınıflar (yazma yok): düzeltilebilir / kargoya verilmiş (değişmez) /
    /// başka bilgisayarın dosyasında (değişmez, §7.4) / konu dışı (iptal, kargo ücreti). Bulunamayan Id
    /// atlanır.</summary>
    public PriceCorrectionPlan PlanPriceCorrection(IReadOnlyCollection<string> labelIds)
        => Classify(_labels.GetByIds(labelIds.ToList()));

    /// <summary>Bir yayında bir kodun fiyatı (sahip kararı F1: bir yayında bir kodun TEK fiyatı vardır). Kaynak o
    /// yayındaki o kodun iptal edilmemiş etiketleri; null = kodun bu yayında etiketi yok. FİYAT kutusu kod
    /// değişince bunu gösterir — uygulama yeniden açılınca da aynı sonuç.
    ///
    /// <para>Fiyat kaynağı düzeltilebilir etiketlerdir; yoksa değişmeyenler: kargoya verilmiş dosyadakiler ve
    /// başka bilgisayarın dosyasındakiler (§7.4) — ikisi de "değiştirilemiyor ama kodun bu yayındaki fiyatı",
    /// yazılış sırasıyla tek kaynak. Ortak kural (<c>LabelRules.CodePrice</c>) bilmediği dosyayı düzeltilebilir
    /// sayar: sunucuda her dosya bilinir. Masaüstünde dosyası yerelde olmayan etiket düzeltilemediği için kaynakta
    /// kargoya verilmiş gibi durur (<c>LabelRulesCoreParityTests</c> bu farkı sabitler).</para></summary>
    public CodePrice? GetCodePrice(string sessionId, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var labels = _labels.GetActiveBySessionAndCode(sessionId, code.Trim());
        if (labels.Count == 0) return null;
        var plan = Classify(labels);
        IReadOnlyList<Label> source = plan.Correctable;
        if (source.Count == 0)
        {
            var unchangeable = plan.Shipped.Concat(plan.Elsewhere).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            source = labels.Where(l => unchangeable.Contains(l.Id)).ToList();
        }
        if (source.Count == 0) return null;
        // En çok tekrarlanan fiyat; eşitlikte en son yazılanınki (liste yazılış sırasında).
        var price = source.Select((l, i) => (l.Price, Order: i))
            .GroupBy(x => x.Price)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(x => x.Order))
            .First().Key;
        return new CodePrice(price, plan.Correctable.Count, plan.PriceGroups, plan.Shipped.Count);
    }

    /// <summary>Kod düzeyinde düzeltmenin planı (sahip kararı F3): seçimdeki etiketlerin KODLARI, her biri için
    /// kodun yayındaki BÜTÜN etiketleri (artı seçimde o koddan olanlar — ör. önceki yayından kuyruğa taşınmış
    /// satır), sınıflanmış. Kodsuz seçilen etiketler tek grupta, yalnız kendileri (genişletilecek kod yok). Sıra:
    /// kodların seçimde ilk görüldüğü sıra. Yazma yok.</summary>
    public IReadOnlyList<CodeCorrectionPlan> PlanCodeCorrection(string sessionId, IReadOnlyCollection<string> selectedIds)
    {
        var selected = _labels.GetByIds(selectedIds.ToList());
        var plans = new List<CodeCorrectionPlan>();
        foreach (var group in selected.GroupBy(l => CodeKey(l.Code), StringComparer.OrdinalIgnoreCase))
        {
            var code = group.Key.Length == 0 ? null : group.First().Code!.Trim();
            plans.Add(code is null
                ? new CodeCorrectionPlan(null, Classify(group.ToList()))
                : CodePlan(sessionId, code, group));
        }
        return plans;
    }

    /// <summary>Tek kodun planı (FİYAT kutusundaki fiyat değişikliği, F2): kodun yayındaki bütün etiketleri.</summary>
    public CodeCorrectionPlan PlanCodeCorrection(string sessionId, string code)
        => CodePlan(sessionId, code.Trim(), Array.Empty<Label>());

    private CodeCorrectionPlan CodePlan(string sessionId, string code, IEnumerable<Label> alsoSelected)
    {
        var labels = _labels.GetActiveBySessionAndCode(sessionId, code).ToList();
        var known = labels.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        labels.AddRange(alsoSelected.Where(l => known.Add(l.Id)));
        return new CodeCorrectionPlan(code, Classify(labels));
    }

    private static string CodeKey(string? code) => code?.Trim() ?? "";

    /// <summary>Fiyat düzeltmesinin sınıflaması. Ortak kuraldan (<c>LabelRules.Classify</c>) tek farkı bilinçli
    /// (§7.4, §15-57): bağlı olduğu dosya bu bilgisayarda YOKSA etiket başka bilgisayarın dosyasındadır (mod
    /// Off'ta <c>disable</c> turunun sildiği karışık dosya). Dosyanın durumu bilinmez — kargoya verilmiş olabilir —
    /// bu yüzden fiyatı düzeltilmez ve ayrıca listelenir. Sunucuda her dosya bilinir; ortak kural bu durumu
    /// düzeltilebilir sayar.</summary>
    private PriceCorrectionPlan Classify(IReadOnlyList<Label> labels)
    {
        var shipmentStatus = new Dictionary<string, ShipmentStatus?>(StringComparer.Ordinal);
        foreach (var sid in labels.Select(l => l.ShipmentId).OfType<string>().Distinct(StringComparer.Ordinal))
            shipmentStatus[sid] = _shipments.GetById(sid)?.Status;

        var correctable = new List<Label>();
        var shipped = new List<Label>();
        var excluded = new List<Label>();
        var elsewhere = new List<Label>();
        foreach (var l in labels)
        {
            if (l.CancelledAt.HasValue || l.IsShippingFee) excluded.Add(l);
            else if (l.ShipmentId is not { } sid) correctable.Add(l);
            else if (shipmentStatus[sid] is null) elsewhere.Add(l);
            else if (shipmentStatus[sid] == ShipmentStatus.Shipped) shipped.Add(l);
            else correctable.Add(l);
        }
        return new PriceCorrectionPlan(correctable, shipped, excluded, elsewhere);
    }

    /// <summary>
    /// Seçilen etiketlerin fiyatını <paramref name="newPrice"/> yapar — TEK paket (<see cref="ConfirmBackup"/>
    /// deseni): etiket fiyatları (Revision+1, SyncedAt=NULL — sunucuya mevcut senkronla gider; stok
    /// DEĞİŞMEZ), düzeltilen etiketlerin müşterilerinin toplamı etiketlerden yeniden türetilir
    /// (<see cref="CustomerAggregateSql.Recompute(IEnumerable{string}, DbWrite)"/>; sonuçtaki
    /// <see cref="PriceCorrectionResult.CustomerDeltas"/> yalnız basılmış ve onaylı satırların farkıdır, bilgi
    /// için döner), kargoya verilmemiş bağlı dosyanın toplamı fark kadar (Y5 dosya bloğu,
    /// <see cref="ShipmentAmountAdjuster"/>: mod On'da atlanır, yerelde olmayan dosya atlanır). Ödenmiş etiketin — dosyaya bağlı
    /// (dekont) ya da <paramref name="paidFromBalance"/>'ta — ödendiği fiyat ödenmişken ilk düzeltmede saklanır (<see cref="LabelRepository.UpdatePriceForCorrection"/>):
    /// "ödendi, fark" aynı etiketin sonraki düzeltmelerinde de ödenene göre hesaplansın.
    ///
    /// <para>Değişmeyenler: iptal edilmiş etiket, kargo ücreti satırı, kargoya verilmiş dosyadaki etiket ve
    /// başka bilgisayarın dosyasındaki etiket (ikisi sonuçta ayrıca döner), zaten bu fiyattaki etiket. Kuyruktaki etiketin ekrandaki kopyasını
    /// güncellemek çağıranın işidir — yoksa kâğıda eski fiyat basılır.</para>
    ///
    /// <para>Okumalar paket açılmadan önce; paket içinde her satır KOŞULLU yazılır. Okuma ile yazma
    /// arasında satır değiştiyse <see cref="PriceCorrectionConflictException"/> — paket geri sarılır,
    /// hiçbir şey yazılmaz.</para>
    /// </summary>
    /// <param name="paidFromBalance">Onları içeren teslim edilmiş istek tamamen bakiyeden düşülmüş etiketler
    /// (ödeme tarafı belirler — paket açılmadan önce okunur); null = yok.</param>
    public PriceCorrectionResult CorrectPrices(
        IReadOnlyCollection<string> labelIds, decimal newPrice, IReadOnlySet<string>? paidFromBalance = null)
    {
        if (newPrice < 0m) throw new ArgumentOutOfRangeException(nameof(newPrice), "Fiyat eksi olamaz.");

        var plan = PlanPriceCorrection(labelIds);
        var targets = plan.Correctable.Where(l => l.Price != newPrice).ToList();
        var corrected = targets.Select(l => new CorrectedLabel(l, newPrice)).ToList();

        var customerDeltas = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var shipmentDeltas = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var c in corrected)
        {
            // Müşteri toplamı yalnız basılmış ve onaylı satırlardan beslenir (MarkPrintedAndRecord,
            // Cancel ile aynı kural): kuyruktaki satır basılınca zaten yeni fiyatla yazılır, geçici
            // yedek ancak onaylanınca (ConfirmBackup) yeni fiyatıyla yazılır.
            if (c.WasPrinted && !c.Before.IsTentativeBackup)
                customerDeltas[c.Before.CustomerId] = customerDeltas.GetValueOrDefault(c.Before.CustomerId) + c.Delta;
            // Dosya toplamı bağlanan etiketlerin fiyat toplamıdır (AttachLabels) — bağlı her etiket.
            if (c.Before.ShipmentId is { } sid)
                shipmentDeltas[sid] = shipmentDeltas.GetValueOrDefault(sid) + c.Delta;
        }

        if (corrected.Count > 0)
        {
            using var write = DbWrite.Begin(_factory);
            var now = _clock.UnixNow();
            foreach (var c in corrected)
            {
                var l = c.Before;
                if (_labels.UpdatePriceForCorrection(
                        l.Id, l.Price, l.PrintedAt.HasValue, l.IsTentativeBackup,
                        paidFromBalance?.Contains(l.Id) == true, newPrice, now, write) != 1)
                    throw new PriceCorrectionConflictException(
                        "Etiketlerden biri bu arada değişti (basıldı, iptal edildi ya da fiyatı değişti); hiçbir şey yazılmadı.");
            }
            // Toplam etiketlerin yeni fiyatından türetilir (§6.1); CustomerDeltas yalnız bilgi için döner.
            // LastSeenAt bugünkü gibi yalnız sıfır olmayan farkı olan müşteride ilerler.
            CustomerAggregateSql.Recompute(corrected.Select(c => c.Before.CustomerId), write);
            CustomerAggregateSql.TouchLastSeen(
                customerDeltas.Where(kv => kv.Value != 0m).Select(kv => kv.Key), now, write);
            // Dosya bloğu Y5 bloğunun kendisidir (ShipmentAmountAdjuster): mod On'da atlanır (tutarı sunucu
            // türetir), bu arada yerelden kalkmış dosya atlanır. Plan dosyayı açık görmüştü; şimdi kargoya
            // verilmişse yarıştır — paket geri sarılır.
            var adjusted = ShipmentAmountAdjuster.Apply(shipmentDeltas, write);
            if (adjusted.Shipped.Count > 0)
                throw new PriceCorrectionConflictException(
                    "Bir kargo dosyası bu arada kargoya verildi; hiçbir şey yazılmadı.");
            write.Commit();
            shipmentDeltas = new Dictionary<string, decimal>(adjusted.Applied, StringComparer.Ordinal);
        }

        return new PriceCorrectionResult(corrected, plan.Shipped, customerDeltas, shipmentDeltas, plan.Elsewhere);
    }

    /// <summary>Yeniden baskı (K3/K5) için etiketlerin GÜNCEL hâli — verilen sırayla, yalnız basılmış ve
    /// iptal edilmemiş olanlar. Yazma YOK: yeniden baskı ciroyu ikinci kez artırmaz, ilk PrintedAt'i ve
    /// basış grubunu bozmaz. <see cref="Reprint.CorrectionIds"/> fiyatı düzeltilmiş olanlar ("DÜZELTME"
    /// işareti).</summary>
    public Reprint PrepareReprint(IReadOnlyList<string> labelIds)
    {
        var labels = _labels.GetByIds(labelIds)
            .Where(l => l.PrintedAt.HasValue && !l.CancelledAt.HasValue)
            .ToList();
        return new Reprint(labels, _labels.GetPriceCorrectedIds(labels.Select(l => l.Id).ToList()));
    }

    // ─── Backup buyers ──────────────────────────────────────────────────────
    // Backups are first-class Labels (rev 2 — see migration 011) with
    // ParentLabelId pointing at the original sale and IsTentativeBackup = 1.
    // They're physically printed during the live (Y stamp from
    // IsBackupPromoted) but kept out of revenue + customer aggregates until
    // the operator confirms one via ConfirmBackup.

    /// <summary>
    /// Creates a new tentative-backup label for the given parent. The new
    /// label inherits the parent's price + code by default and goes straight
    /// into the print queue (PrintedAt = null) — the operator clicks Yazdır
    /// to physically produce the spare sticker.
    /// </summary>
    public Label AddBackup(
        string parentLabelId, string platform, string username,
        string displayName, string? messageText,
        decimal? priceOverride = null, string? codeOverride = null)
    {
        var parent = _labels.GetById(parentLabelId)
            ?? throw new InvalidOperationException(
                $"Cannot add backup: parent label {parentLabelId} not found.");

        var msg = new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            Platform: platform,
            ExternalId: null,
            Username: username,
            DisplayName: displayName,
            AvatarUrl: null,
            Text: messageText ?? string.Empty,
            ReceivedAt: _clock.UnixNow(),
            Badges: System.Array.Empty<string>());

        return Add(
            sessionId: parent.SessionId,
            message: msg,
            price: priceOverride ?? parent.Price,
            code: codeOverride ?? parent.Code,
            isBackupPromoted: true,
            parentLabelId: parent.Id,
            isTentativeBackup: true);
    }

    /// <summary>
    /// Yedeğin müşterisi, etiketsiz (spec 2026-10-13 §4.6 <c>add-backup</c>): devredilmiş yayına yedeği sunucu ekler,
    /// komut müşterinin yerel kaydını taşır. Müşteri <see cref="AddBackup"/>'ın (<see cref="Add"/>) açtığıyla aynı
    /// kuralla bulunur ya da açılır (<c>CustomerService.GetOrCreate</c>, avatar yok), tek pakette.
    /// </summary>
    public Customers.Customer EnsureBackupCustomer(string platform, string username, string? displayName)
    {
        using var write = DbWrite.Begin(_factory);
        var customer = _customers.GetOrCreate(platform, username, displayName, avatarUrl: null, write);
        write.Commit();
        return customer;
    }

    /// <summary>Returns active (non-cancelled) tentative backups for a parent.
    /// Confirmed backups are excluded — they're already real labels visible in
    /// the normal queue/history.</summary>
    public IReadOnlyList<Label> GetBackups(string parentLabelId) =>
        _labels.GetTentativeBackupsByParent(parentLabelId);

    public IReadOnlyDictionary<string, int> GetBackupCounts(IEnumerable<string> parentLabelIds) =>
        _labels.GetTentativeBackupCounts(parentLabelIds);

    /// <summary>Geçici yedeği geri alır (operatör yayında yanlış kişiyi
    /// seçti). Onaylı yedekler gerçek satış — onlar <see cref="Cancel"/>
    /// akışından geçmeli; burada no-op.
    ///
    /// N07 (=F06'nın burada kalan kalıntısı): eskiden fiziksel DELETE'ti.
    /// Satır sunucuya bir kez push edildiyse silmek mezar taşını da yok
    /// eder — sunucu kopyası ömür boyu aktif geçici yedek olarak kalır.
    /// SyncedAt=NULL "hiç gitmedi"yi KANITLAMAZ (her durum değişikliği onu
    /// sıfırlar), yani güvenle silinebilecek satır ayırt edilemez. Bu
    /// yüzden her geri alma soft-cancel: satır yerinde kalır, SyncedAt
    /// düşer, iptal bir sonraki push'ta sunucuya gider. Cancel() tentative
    /// satırlarda ciro/stok toplamlarına zaten dokunmuyor; bütün yedek
    /// listeleri de CancelledAt IS NULL filtreli — operatör için görünür
    /// fark yok.</summary>
    public void RemoveBackup(string backupLabelId)
    {
        var lbl = _labels.GetById(backupLabelId);
        if (lbl is null || !lbl.IsTentativeBackup) return;
        Cancel(new[] { backupLabelId }, CancelReasonCodes.BackupRemoved);
    }

    /// <summary>
    /// Confirms a tentative backup as a real sale: flips IsTentativeBackup→0,
    /// then re-derives the customer's totals — a spare sticker that was already
    /// printed now counts (the common path — operator printed during the live,
    /// original cancelled next-day, now confirming).
    /// Returns the updated Label.
    /// </summary>
    public Label ConfirmBackup(string backupLabelId, decimal? newPrice = null)
    {
        var lbl = _labels.GetById(backupLabelId)
            ?? throw new InvalidOperationException(
                $"Cannot confirm backup: label {backupLabelId} not found.");

        if (!lbl.IsTentativeBackup)
            return lbl; // already confirmed — caller can ignore.

        // Üç yazma da tek pakette: fiyat güncellemesi, onay damgası, müşteri
        // toplamı. Fiyat de pakete dahil — eskiden onay adımı patladığında
        // fiyat değişmiş, yedek hâlâ "geçici" kalmış oluyordu.
        using var write = DbWrite.Begin(_factory);

        // Optional price update: operator may negotiate a different number
        // when promoting (e.g. discount because the spare buyer hesitated).
        if (newPrice.HasValue && newPrice.Value != lbl.Price)
        {
            _labels.UpdatePrice(backupLabelId, newPrice.Value, write);
            lbl = lbl with { Price = newPrice.Value };
        }

        _labels.ConfirmTentativeBackups(new[] { backupLabelId }, write);
        // Toplam onaydan SONRA etiketlerden türetilir (§6.1): basılmış yedek artık katkı verir.
        // LastSeenAt bugünkü gibi yalnız sticker zaten basılmışsa ilerler.
        CustomerAggregateSql.Recompute(new[] { lbl.CustomerId }, write);
        if (lbl.PrintedAt.HasValue)
            CustomerAggregateSql.TouchLastSeen(new[] { lbl.CustomerId }, _clock.UnixNow(), write);
        write.Commit();

        return lbl with { IsTentativeBackup = false };
    }
}
