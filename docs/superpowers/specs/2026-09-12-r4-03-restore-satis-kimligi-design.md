# R4-03 — Eski yedek sonrası satış kimliği: tasarım önerisi

**Tarih:** 2026-09-12
**Durum:** 🟢 **KARARLAR ALINDI** (2026-09-12) — uygulamaya geçildi.
Cevaplar §5'e işlendi. **S1'in premisi kod okumasıyla çürüdü**; gerekçe
düzeltildi ve "kapsam kapanışı" fikri tamamen elendi (§5.1).
**Kaynak:** Codex R4 denetimi (87/100), bölüm 9 (R4-03), bölüm 39–40 ve
bölüm 52 karar tablosu.
**Kardeş bulgular:** R4-01 (PR #417) ve R4-02 (PR #418) aynı finansal
sözleşmenin ilk iki parçasıydı ve ikisi de ürün kararı gerektirmiyordu.
**Bu üçüncüsü gerektiriyor** — bu belge nedenini ve seçenekleri gösteriyor.

---

## 1. Kanıtlanmış açık

Denetim gerçek `BackupService` + gerçek `RestoreService` ile çalıştırdı:

1. Ödeme işi **oluşmadan** yedek alındı.
2. Aynı müşteri, `session:test` kapsamında **250 uygulandı** (uzak defterde
   `purchase-deduction`, PK = istemcinin ürettiği rastgele anahtar).
3. Eski yedek geri yüklendi — yerel SQLite o işi **hiç bilmiyor**.
4. Aynı istek tekrarlandı: `FindOrCreate` yeni satır, `BeginApply`
   **yeni rastgele anahtar** üretti.
5. Sunucu bunu farklı bir işlem saydı. **Toplam uzak düşüm: 500.**

Yedekleme başarılı, geri yükleme başarılı, sonuç yanlış.

### Kök neden tek cümlede

**Satışın kimliği yalnız yerelde var.** Sunucuya giden tek kimlik,
istemcinin `Guid.NewGuid()` ile ürettiği idempotency anahtarı; o anahtar da
yerel SQLite satırında yaşıyor. Yerel dosya geri sarılınca kimlik yok
oluyor, uzak defter ise hatırlamaya devam ediyor.

Bugünkü ledger satırında (`CustomerBalanceTransaction`) satışa dair
**hiçbir alan yok**: `Id`, `LicenseId`, `WpfCustomerId`, `Amount`, `Kind`,
`OriginalAmount`, `ReversesTransactionId`, `CreatedByCustomerId`,
`CreatedAt`. "Bu düşüm hangi satışa aitti?" sorusunun sunucuda cevabı yok.

### Neden "müşteri + tutar" ile eşleştirmek YANLIŞ

Raporun da altını çizdiği gibi: aynı müşterinin iki farklı gerçek satışı
aynı tutarda olabilir. Tutar üzerinden dedup, gerçek ikinci satışı sessizce
yutardı — çift düşümden daha sinsi bir hata, çünkü bu sefer para **eksik**
düşer ve kimse fark etmez.

---

## 2. Kabul ölçütü (rapor bölüm 9)

Çözüm dört şeyi aynı anda sağlamalı. Yalnız birincisi "ikinci düşümü
engelle" — kolay olan o:

| # | Ölçüt |
|---|---|
| K1 | Geri yükleme sonrası **aynı satış ikinci kez düşmez** |
| K2 | **Yeni gerçek satış hâlâ uygulanır** (akış kilitlenmez) |
| K3 | Eski işlemin **revizyon ve reversal geçmişi korunur** |
| K4 | Yeniden kurulumdan sonra **mesajdaki rakam doğru** olur |

K2 seçeneklerin yarısını eliyor: bir şeyi bloklamak K1'i bedavaya çözer ama
K2'yi öldürür.

---

## 3. Seçenekler

### Seçenek A — Anahtarı satış kimliğinden türet (ucuz)

`BeginApply(job.Id, Guid.NewGuid())` yerine anahtar deterministik:

```
key = UUIDv5( licenseId + wpfCustomerId + scopeKey + revision )
```

Geri yükleme sonrası aynı kapsam aynı anahtarı üretir → sunucu replay →
tarihsel sonucu döner → **ikinci düşüm olmaz**. Sunucuda tek satır kod
değişmez, göç yok, dağıtım yok.

**Neden yeterli değil:** revizyon sayacı da yerelde. Yedek alındıktan sonra
satış 250 → 50'ye revize edildiyse, uzak defterde `rev=1` anahtarı 50 ile
kullanılmıştır. Geri yüklenen istemci `rev=0`'dan başlar; ama `rev=0`
anahtarı da 250 ile kullanılmıştır ve yeni sepet 80 TL'yse sunucu
**A11 `content-conflict` (409)** döner. Servis bunu kesin cevap saymıyor,
haklı olarak — iş `apply_uncertain`'de kalır.

Ve **her deneme aynı anahtarı üretir**: durum kalıcı kilit. K2 düşer.
Operatörün önünde "tekrar deneyin" yazar, tekrar denemek hiçbir zaman
işe yaramaz.

> Ucuz-vs-doğru: A, çift düşümü çift kilide çeviriyor. Para kaybı yok ama
> müşteri yayın ortasında hizmet dışı kalıyor ve geri dönüş yolu yok —
> kurtarmak için elle DB düzenlemek gerekir. Tek başına **önermiyorum**.

### Seçenek B — Satış kapsamını sunucuya taşı (doğru)

Kimliği, kaybolabilen tarafta değil, **kaybolmayan tarafta** tut.

**Sunucu:**
1. `CustomerBalanceTransaction`'a `SaleScope TEXT NULL` — `"session:{id}"` /
   `"cumulative"` / `"legacy:{key}"`, istemcinin zaten kullandığı metin.
2. `ApplyRequest`'e `SaleScope` alanı (opsiyonel — eski istemciler null
   gönderir, davranışları değişmez).
3. Yeni uç:
   `GET .../customer-balance/scope?wpfCustomerId=..&saleScope=..`
   → o kapsamdaki **geri alınmamış** en güncel `purchase-deduction`:
   `{ transactionId, appliedAmount, productTotal, createdAt }` ya da 204.

**İstemci:** taze bir iş `created` durumundayken, apply'dan **önce** bu ucu
sorar:

- Sunucuda kayıt **yok** → bugünkü akış, hiçbir değişiklik.
- Sunucuda kayıt **var** → `AdoptRemoteResult`: iş `applied` olur,
  `ApplyKey = transactionId`, `AppliedAmount = appliedAmount`,
  `ProductTotal = productTotal`. Düşüm **tekrar edilmez**; sepet tutarı
  farklıysa normal revizyon dalı (geri al + yeniden uygula) devreye girer.

**Ölçütler:** K1 ✅ (kapsam sunucuda tanınır) · K2 ✅ (yeni kapsam =
sunucuda kayıt yok = normal akış) · K3 ✅ (geçmiş uzak defterde, hiç
silinmiyordu) · K4 ✅ (mesajdaki rakam benimsenen gerçek tutardır).

**Bedeli:** SQL Server göçü + endpoint + istemci modeli + iki tarafta test.
Sunucu para yolunda değişiklik, yani `master`'a merge = **otomatik prod
deploy**. Geri dönüş: kolon nullable ve uç salt-okunur olduğu için eski
sürüme dönmek veri kaybı yaratmaz; asıl risk deploy penceresinin kendisi.

**Neden A'nın deterministik anahtarı B ile birlikte de gerekmiyor:**
gerekmiyor. B kapsamı sorgulayıp benimsiyor; anahtarın rastgele kalması
zararsız, hatta tercih edilir (çakışma yüzeyi yok).

### Seçenek C — Geri yükleme karantinası (B'nin tamamlayıcısı)

`RestoreAsync` başarıyla bittiğinde yerel bir damga bırakılır
("bu DB {tarih} yedeğinden geri yüklendi"). Damga varken finansal akış,
her müşteri için B'nin uzlaştırması **tamamlanana kadar** açılmaz ve
operatöre "geri yükleme sonrası uzlaşma bekleniyor" listesi gösterilir.

Tek başına K2'yi çiğner (her şey bloke). B'nin **yanında** anlamlı: B
zaten müşteri bazında uzlaştırıyor, C bunu operatöre **görünür** kılıyor —
rapor bölüm 52'nin *"hangi işlemlerin uzlaşma beklediği ekranda görünür"*
ölçütü tam olarak bu.

---

## 4. Önerim

**B'yi uygula, C'yi B'nin üstüne ince bir katman olarak ekle. A'yı alma.**

Gerekçe: R4-03'ün kalbi "kimlik kaybolabilen tarafta tutuluyor". A bunu
kabul edip sonucunu yönetmeye çalışıyor ve kilitle bitiyor; B kuralı
ihlal edilemez kılıyor — yerel dosya ne kadar geri sararsa sarsın, satışın
kimliği sunucuda duruyor.

---

## 5. Kararlar (2026-09-12 alındı)

| # | Soru | **Karar** |
|---|---|---|
| S1 | `cumulative` kapsamının ömrü | **Kalıcı, müşteri başına tek.** Kapanış mekanizması YOK — bkz. §5.1, sorunun premisi çürüdü. |
| S2 | Cihaz/kurulum değişimi aynı satış mı? | **Evet, kapsam cihazdan bağımsız.** Kimlik makinede değil sunucuda; R4-03'ün çözümü zaten bu. |
| S3 | Geri yükleme sonrası akış | **Açık kalsın**, müşteri bazında uzlaştırılsın. Karantina (Seçenek C) **uygulanmayacak**. |
| S4 | Uzlaştırma kayıt bulunca mesaj | **Sessizce doğru rakam.** Benimsenen tutar zaten gerçek tutardır; müşteriye iç uzlaştırmayı anlatmak gereksiz ve kafa karıştırıcı. |
| S5 | Sıfır toplam eski düşümü iade etsin mi? | **Evet, iade edilsin.** `totalAmount > 0` kapısı kalkacak; R4-02'nin `reverse_pending` altyapısı kullanılacak. |

### 5.1 S1'in premisi neden çürüdü

Soruyu sorarken iki şey varsaydım; ikisi de yanlış çıktı.

**(a) "Ödendi" diye bir an yok.** Dekont (`Payment`) onayının bakiyeye
hiçbir etkisi yok; bakiyeye para yalnız `manual-adjustment` ve `refund-*`
ile giriyor. Dönem/ekstre/kapanış kavramı şemada hiç yok — bakiye tüm
zamanların koşan toplamı. Yani kapsamı kapatacak bir olay mevcut değil.

**(b) Daha önemlisi, kapanışa gerek de yok.** `Customer.TotalAmount`
etiket bastıkça artan, **hiç sıfırlanmayan ömürlük toplam**
(`CustomerRepository.IncrementLabelStats`). "İkinci bir kümülatif satış"
diye bir şey yok — her yeni istek aynı kapsamın **yukarı revizyonu**, ve
kod bunu bugün de böyle işliyor.

Belgenin ilk hâlinde "sonsuz ömürlü kapsam ikinci kümülatif satışı
sonsuza dek engeller" yazıyordu. **Yanlıştı.** Benimseme akışı bitirmiyor,
revizyon dalına devrediyor:

| Geri yükleme sonrası | Sonuç |
|---|---|
| Toplam aynı (250) | Benimser, ikinci düşüm yok → net **250** ✅ |
| Sonra yeni etiketler (400) | Benimser → 250≠400 → revizyon → eskiyi geri al, 400 uygula → net **400** ✅ |

Kapanış sayacı eklemek zararlı bile olurdu: sayaç **yerelde** yaşamak
zorunda kalırdı, ve geri yüklemede kaybolan tam da yerel durum. Seçenek
A'nın revizyon sayacında düştüğü tuzağın aynısı.

---

## 6. Uygulama sırası

| PR | İçerik | Merge riski |
|---|---|---|
| 1 | Sunucu: `SaleScope` kolonu + göç + `apply` alanı (yazma, okuma yok) | Düşük — nullable kolon, davranış değişmez |
| 2 | Sunucu: `GET scope` ucu + testler | Düşük — salt okunur |
| 3 | İstemci: `AdoptRemoteResult` + uzlaştırma dalı + kabul testleri | Orta — para yolu, Velopack sürümüne biner |
| 4 | S5: sıfır toplamda eski düşümün iadesi | Düşük — istemci, R4-02 altyapısı hazır |

S3 kararı gereği **Seçenek C (karantina + "uzlaşma bekliyor" listesi)
uygulanmayacak**; akış açık kalıyor.

1 ve 2 önce merge edilmeli ve **sahada bir süre çalışmalı**: istemci uca
güvenmeden önce sunucunun kapsamları biriktirmiş olması gerekiyor. Aksi
halde ilk uzlaştırma sorgusu, yalnızca kolonun yeni olması yüzünden boş
döner ve hiçbir şey kazanmayız. Bu boş dönüş **zararsız** — bugünkü
davranışa düşer, yani R4-03 öncesi duruma; yanlış bir şey yapmaz.

---

## 7. Bu belgenin kapsamadıkları

- **R4-04** (miras anahtarları) kapandı, göç 034 artık hepsini koruyor.
- **R4-01 / R4-02** ayrı PR'larda hazır (#417, #418) ve bu belgeden
  bağımsız — B uygulanmasa da ikisi doğru.
- Ödemenin fiilen tahsili (dekont eşleştirme) yine kapsam dışı; 2026-09-11
  tasarımındaki K5 geçerli.
