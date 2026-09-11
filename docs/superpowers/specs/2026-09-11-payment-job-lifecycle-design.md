# Ödeme işi yaşam döngüsü (PaymentJob) — tasarım

**Tarih:** 2026-09-11
**Kaynak:** Codex R3 denetimi (84/100) — R2-01, R2-02, R2-03, R2-04 bulguları;
kabul senaryoları A1–A11 (rapor bölüm 38). Rapor: derin analiz
2026-09-11, bölüm 13–16.
**Karar sahibi:** Burak (2026-09-11 tasarım konuşması).

## 1. Problem

`PaymentRequestService.OpenWhatsAppAsync` bugün kalıcı kayıtta yalnız
müşteri + tutar + idempotency anahtarı taşıyor (`PendingBalanceApply`,
migration 033). Dört yapısal sonuç:

1. **R2-01 — yanıt kaybında yanlış kesinleşme:** apply çağrısının ağ
   hatası yutulur, mesaj bakiyesiz brüt tutarla gider; pencere açılınca
   `ResolvePendingApply` işi kapatır → ikinci deneme yeni anahtar üretir,
   bakiye **ikinci kez** düşer.
2. **R2-02 — iş kimliği yok:** "aynı isteği yeniden paylaş" ile "yeni
   satış" ayırt edilemiyor; pencerenin açılması finansal işin bittiği
   sayılıyor.
3. **R2-03 — revizyon uzlaştırması yok:** 250 uygulanmış işin toplamı
   50'ye düşünce replay eski 250'yi döner, `50 − 250 = −200` net tutarlı
   mesaj üretilir.
4. **R2-04 — açık işte yarış:** `IX_PendingBalanceApply_Unresolved`
   unique değil; `GetUnresolved` → `Create` arası TOCTOU ile iki anahtar
   + iki düşüm üretilebiliyor (denetim deneyiyle doğrulandı).

Çözümün merkezi (raporla mutabık): kenar durum başına bool değil,
**kalıcı iş kimliği + durum makinesi**.

## 2. Kararlar (soru-cevap ile netleşti)

| # | Karar |
|---|---|
| K1 | İş kapsamı `(CustomerId, ScopeKey)`; aynı yayında tutar değişimi **revizyon**dur, yeni yayın **yeni iş**tir. |
| K2 | Revizyon uzlaştırması **geri al + yeniden uygula**: eski düşüm reversal ile iade edilir, yeni toplam üzerinden taze düşüm yapılır. Reversal mantığı sunucuda mevcut ve A10 ile test edilmiş — ancak yalnız panel yüzeyinde; WPF yüzeyine aynı guard'larla küçük bir uç eklenir (bkz. §6). |
| K3 | Bakiye sonucu belirsizse önce **bir kez anında replay**; hâlâ belirsizse **mesaj oluşturulmaz**, operatöre "bakiye doğrulanamadı — tekrar deneyin" denir. |
| K4 | A11 sunucu sözleşmesi **pakete dahil** (ayrı küçük PR): aynı anahtar farklı içerikle gelirse 409 `content-conflict`. |
| K5 | İşin yaşam döngüsü "mesaj müşteriye ulaştı + bakiye sonucu kesin" ile biter. Ödemenin fiilen tahsili (dekont eşleştirme) kapsam **dışı**. |

## 3. Veri modeli — migration 034

`PaymentJob` tablosu (yerel SQLite):

```sql
CREATE TABLE PaymentJob (
    Id            TEXT NOT NULL PRIMARY KEY,  -- iş kimliği (Guid "N")
    CustomerId    TEXT NOT NULL,
    ScopeKey      TEXT NOT NULL,              -- "session:{id}" | "cumulative" | "legacy"
    ProductTotal  TEXT NOT NULL,              -- güncel revizyonun toplamı (invariant decimal)
    Revision      INTEGER NOT NULL DEFAULT 0, -- toplam her değiştiğinde +1
    ApplyKey      TEXT NULL,                  -- güncel bakiye düşüm idempotency anahtarı
    AppliedAmount TEXT NULL,                  -- sunucudan DOĞRULANMIŞ düşüm; NULL = yok/bilinmiyor
    State         TEXT NOT NULL,              -- bkz. §4
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL,
    ClosedAt      INTEGER NULL
);

CREATE UNIQUE INDEX UX_PaymentJob_Scope ON PaymentJob(CustomerId, ScopeKey);
```

- `ProductTotal`/`AppliedAmount` TEXT: invariant kültürle yazılan ondalık
  (033'teki gerekçe aynen geçerli — REAL eşitlik karşılaştırmasını bozar).
- İş **silinmez**: `closed` satır, tekrar paylaşım ve revizyon
  aramalarının bulacağı kayıttır.
- `UNIQUE(CustomerId, ScopeKey)` + `INSERT OR IGNORE` → atomik
  bul-veya-oluştur; R2-04'ün TOCTOU'su şemada imkânsızlaşır.

**Eski tablodan taşıma:** migration, `PendingBalanceApply` içindeki
`ResolvedAt IS NULL` satırlarını `ScopeKey='legacy'`,
`State='apply_uncertain'`, `ApplyKey=IdempotencyKey` olarak taşır
(müşteri başına en yeni satır; birden fazlası fiilen oluşmuyor, oluşmuşsa
en yenisi taşınır, gerisi çözülmüş sayılır). Çözülmüş satırlar
taşınmaz. Eski tablo migration'da düşürülür; kod artık okumaz.
`legacy` işi ilk yeni istekte replay ile çözülüp devralınır (§5.6).

## 4. Durum makinesi

```
created          → apply denenmedi (bakiye yok/0 ise doğrudan mesaja geçilebilir)
apply_uncertain  → apply gönderildi, sonuç BİLİNMİYOR (yanıt kaybı)
applied          → sunucu düşümü doğruladı; AppliedAmount dolu
no_balance       → sunucu "bakiye yok" dedi (409 no-balance/nothing-to-apply)
closed           → mesaj müşteriye ulaştı + bakiye sonucu kesin
```

Geçiş kuralları — her durumdan hangi eylem güvenli:

| Durum | Mesaj oluşturulabilir mi? | Yeni finansal çağrı? |
|---|---|---|
| `created` | bakiye adımı bittikten sonra | preview + apply (ApplyKey ilk burada üretilir ve **önce diske iner**) |
| `apply_uncertain` | **HAYIR** | yalnız aynı ApplyKey ile replay |
| `applied` | evet (AppliedAmount'tan) | hayır (revizyon hariç) |
| `no_balance` | evet (düşümsüz) | hayır (revizyon hariç) |
| `closed` | evet (kayıtlı değerlerden yeniden kurulur) | hayır (revizyon hariç) |

Mesajdaki net tutar **her zaman** `AppliedAmount` kolonundan gelir —
uçuştaki bir çağrının varsayımından asla (A1'in özü).

## 5. Servis akışı (`PaymentRequestService`)

`OpenWhatsAppAsync` imzasına **scope** eklenir; `overridePendingConflict`
parametresi ve `PendingApplyConflict` sonucu **kaldırılır**. Yeni sonuç:
`BalanceUncertain`.

Çağıranların scope'u:
- `StreamReportViewModel` → `session:{aktif rapor oturumunun Id'si}`
- `CustomerSearchViewModel` → yayın-içi tutar kullanılıyorsa
  (`streamSum > 0`) o oturumun `session:{id}`'si; kümülatif tutar
  kullanılıyorsa `cumulative`

Akış adımları:

1. **Bul-veya-oluştur** `(CustomerId, ScopeKey)` — tek SQL turu
   (`INSERT OR IGNORE` + `SELECT`). Eşzamanlı iki giriş aynı satırı ve
   aynı `ApplyKey`'i görür → sunucu replay eder → tek düşüm (A9).
2. **Tekrar paylaşım** (`ProductTotal` eşit, durum `closed` /
   `applied` / `no_balance`): finansal çağrı YOK; mesaj kayıtlı
   `AppliedAmount`'tan kurulur ve gönderilir (A5).
3. **Revizyon** (`ProductTotal` farklı):
   a. Durum `apply_uncertain` ise önce replay ile kesinleştir.
   b. `AppliedAmount > 0` ise eski `ApplyKey`'in hareketi **reversal**
      ile iade edilir (reversal çağrısı da yanıt kaybına karşı kendi
      idempotency'siyle korunur; sunucu tarafındaki unique kısıt A10 ile
      kanıtlı).
   c. Yeni `ApplyKey = Guid.NewGuid()` **önce diske**, sonra yeni toplam
      üzerinden apply. `Revision+1`, `ProductTotal` güncellenir.
   d. Reversal veya apply belirsiz kalırsa → `apply_uncertain`, mesaj yok.
   Böylece A7 (düşen toplam) ve A8 (artan toplam) tek kuralla çözülür;
   negatif net tutar matematiksel olarak imkânsız.
4. **Belirsizlik protokolü** (K3): apply/replay/reversal çağrısı ağ
   hatası verirse **bir kez anında** aynı anahtarla replay dene; hâlâ
   belirsizse `State=apply_uncertain` yaz, `BalanceUncertain` dön —
   mesaj OLUŞTURULMAZ (R2-01/A2/A3). Uygulama yeniden başlasa bile iş
   diskte; sonraki tık kaldığı yerden sürer (A4).
5. **Teslim:** Cloud API `Sent` ya da wa.me `Opened` → `State=closed`,
   `ClosedAt` yazılır. `LaunchFailed`/`SendPending` → iş açık kalır.
6. **Legacy devralma:** müşterinin `legacy` işi varsa yeni istek önce
   onu replay ile kesinleştirir; sonuç yeni scope'lu işe devredilir
   (AppliedAmount + ApplyKey taşınır, legacy satır `closed`), akış
   normal devam eder.

Hata sınıflandırması netleşir: `ValidationException`
(no-balance/nothing-to-apply/content-conflict) = **kesin sonuç**;
ağ/zaman aşımı = **belirsiz**. Bugünkü "her hatayı yut, bakiyesiz devam
et" davranışı kalkar.

## 6. Sunucu sözleşmesi — A11 + WPF reversal ucu (ayrı küçük PR)

**A11 — apply ucu:** gelen idempotency anahtarı kayıtlıysa, kayıtlı
satırın `(customerId, amount, totalAmount)` üçlüsü istekle
karşılaştırılır:

- Uyuşuyorsa: bugünkü davranış — ilk sonucu replay et.
- Uyuşmuyorsa: **409 `content-conflict`**; yan etki yok.

İstemci `content-conflict`'i programlama hatası sinyali sayar: loglar,
operatöre hata gösterir, otomatik tekrar DENEMEZ. (Yeni istemci tasarımı
bu durumu üretemez — revizyon hep yeni anahtar kullanır; kontrol savunma
katmanıdır.)

**WPF reversal ucu:** reversal bugün yalnız panel yüzeyinde
(`PanelCustomerBalanceController` POST `transactions/{id}/reverse`);
WPF'in kullandığı `LicenseApiClient`'ta karşılığı yok. Aynı guard'larla
(already-reversed 409, negatif bakiye koruması, DB unique kısıtı — A10)
lisanslı-kullanıcı yüzeyine bir reverse ucu eklenir ve
`LicenseApiClient`'a metodu açılır. Apply anahtarı ledger satırının
PK'sı olduğu için (mig. 033) `transactionId = ApplyKey` — istemci ek
kimlik saklamaz.

Not: sunucu PR'ı merge = otomatik prod deploy; merge zamanı yayın
durumuna göre kullanıcıda.

## 7. UI değişiklikleri

- İki VM'de `PendingApplyConflict` onay dialogu ve `overridePendingConflict`
  yeniden çağrısı silinir.
- `BalanceUncertain` için yeni uyarı: "Bakiye doğrulanamadı — tekrar
  deneyin." (mesaj atılmadı bilgisi net verilir).
- `SendPending` davranışı aynen kalır.

## 8. Test ve kabul eşlemesi

Tümü TDD (red-green); InMemorySqlite + FakeHttpMessageHandler; A9 gerçek
yarışla (fake handler'da bariyer — `Task.WhenAll` tesadüfi sıralaması
kabul değil, rapor bölüm 38/A9).

| Kabul | Test |
|---|---|
| A1 | ilk istek: tek iş, tek düşüm, mesaj net tutarı `AppliedAmount`'tan |
| A2 | kayıt öncesi kesinti: tekrar → tek uygulama, aynı iş kimliği |
| A3 | kayıt sonrası yanıt kaybı: anında replay sonucu öğrenir; öğrenemezse mesaj YOK; ikinci hareket YOK |
| A4 | A3 + tüm nesneler yeniden kurulur: iş diskten bulunur, akış sürer |
| A5 | başarılı işin tekrar paylaşımı: hareket sayısı ve toplam düşüm DEĞİŞMEZ |
| A6 | iki farklı scope, aynı müşteri+tutar: iki iş, iki düşüm (A5 ile AYNI dosyada, birlikte geçer) |
| A7 | 250 uygulanmış, toplam 50: reversal + 50 üzerinden yeni apply; negatif mesaj imkânsız |
| A8 | toplam artışı: reversal + yeni toplam üzerinden apply, `Revision+1` kayıtta görünür |
| A9 | eşzamanlı iki giriş: tek iş, tek düşüm (bariyerli yarış) |
| A11 | sunucu: aynı anahtar farklı içerik → 409, yan etki yok (LicenseServer testleri) |

A10 sunucuda zaten yeşil (mevcut testler korunur).

## 9. Kapsam dışı (bilerek)

- **A12** — yerel yedeğe dönüş sonrası uzak ödeme işleriyle uzlaştırma:
  ayrı tur (90+ hedefi için sıradaki büyük kabul).
- Ödemenin fiilen tahsili / dekont eşleştirme: mevcut sistem aynen kalır.
- R3-04 arama performansı: ayrı paket (rapor paket 5).

## 10. PR paketlemesi

| PR | İçerik | Taraf |
|---|---|---|
| 1 | Migration 034 + `PaymentJobRepository` (atomik bul-veya-oluştur, durum geçişleri) | WPF/Core |
| 2 | `PaymentRequestService` durum makinesi + replay/reversal/revizyon akışı | WPF |
| 3 | VM/dialog uyarlaması (`PendingApplyConflict` kalkar, `BalanceUncertain` girer) | WPF |
| 4 | A11 `content-conflict` + WPF reversal ucu | LicenseServer (**merge = prod deploy**) |

Her PR ayrı branch (master'dan), red-green test, Türkçe commit.

**Sıralama bağımlılığı:** PR 2'nin revizyon akışı sunucudaki WPF reverse
ucunu çağırır → PR 4 prod'a, WPF fix'lerini taşıyan Velopack sürümünden
ÖNCE inmiş olmalı. (Velopack sürümü zaten ayrı adım; doğal sıra bunu
sağlar, yine de yayın öncesi kontrol listesine yazılmalı.)
