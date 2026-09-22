# Panel Netgsm/İYS Kurulum Ekranı + Otomatik İYS Aynası — Tasarım

**Tarih:** 2026-09-22 · **Karar sahibi:** Burak · **Durum:** onaylı (sohbette, 2026-09-22)

## 1. Amaç

Yayıncı, kendi Netgsm hesabını ve İYS marka kodunu **panelden** girip doğrulatabilsin.
Bugün sunucu ucu (`PUT /api/panel/netgsm/account`, Plan 2, prod'da) var ama onu çağıran
arayüz yok; tek yol `curl`. Bu, çok yayıncılı SMS'in önündeki son blokör. Ek olarak
İYS'de zaten var olan onaylar (başka sağlayıcıdan geçen yayıncı, elle yükleyen, geri
dönen yayıncı) **kimse düğmeye basmadan** yerel tabloya gelsin.

Kapsam dışı: sihirbaz akışı, genel "Ayarlar" ekranı, Netgsm başlık başvurusunun panelden
yapılması, kampanya gönderim ekranı.

## 2. Sunucu (LiveDeck, küçük PR — önce bu deploy olur)

### 2.1 Doğrulama başarısında otomatik ayna
`PanelNetgsmAccountController.SaveAsync`: doğrulama sonucu `Verified` yazıldıktan ve tek
`SaveChanges` **başarıyla** bittikten sonra, yalnız **GEÇİŞTE** `_jobs.Enqueue<IysMirrorImportJob>(j =>
j.RunAsync(licenseId, CancellationToken.None))`. Geçiş üç hâlden biri: ilk kurulum (önceden
hesap yok), doğrulanmamış→doğrulanmış (`existing` yok ya da `Verified` değildi) ya da marka
kodu değişti. **Salt şifre yenilemede** (hesap zaten `Verified`, marka aynı) kuyruğa
ATILMAZ — ayna yalnız ONAY satırı yazar (§2.2), yani "zaten doğrulanmış hesabı yeniden
kaydetmek no-op'a yakın" varsayımı YANLIŞ: ONAY'sız numaralar `known` kümesine hiç girmez ve
koşulsuz tetiklemek yayıncının Netgsm kotasını boşa harcar, tam bir tarama daha açardı.
Karar, `UpsertAsync`'ten ÖNCE okunmuş `existing` görüntüsüyle verilir (bayat çıkarsa bedeli
bir fazla ya da bir eksik kuyruk — ikisini de `POST …/iys-mirror` ucu (yalnız destek —
panelde düğme yok) ve günlük eşitleme (spec §2.2) telafi eder).

Kuyruğa atma SaveChanges'ten SONRA (commit olmamış hesap için iş koşarsa "doğrulanmış hesap
yok" diye çıkar — zararsız ama boşa çağrı). `Enqueue` kendi try/catch'inde: hesap zaten
`Verified` olarak COMMIT EDİLMİŞTİR, bir Hangfire depolama arızası (prod'da aynı SQL Server)
bu satırı geri almaz — hata `ILogger` ile loglanır ve PUT yine 200 döner; 500'e çevirmek
yayıncıya yalan söylemek olurdu. `POST …/iys-mirror` ucu (yalnız destek — panelde düğme
yok) ve günlük eşitleme (spec §2.2) kaçırılan turu telafi eder. Lisans başına
`DisableConcurrentExecution("iys-mirror:{0}")`
zaten var.

### 2.2 Günlük eşitleme işi
Yeni `IysMirrorSyncJob` (Hangfire recurring `iys-mirror-sync`, `"52 4 * * *"` — 5 dakikalık
ızgara dışı, 04:47'deki saklama işinden sonra): `NetgsmAccountService.ListVerifiedAsync`
ile her doğrulanmış hesap için `IysMirrorImportJob` kuyruğa atar (kendisi ayna
KOŞTURMAZ; kilit ve yeniden deneme lisans başına işte kalır). **Maliyet artımlı DEĞİL:**
ayna yalnız ONAY satırı yazar (RET/Unknown/kayıt-yok satırsız kalır —
`IysMirrorImportJob` sınıf yorumu), yani ONAY'sız her numara `known` kümesine hiç girmez ve
HER gece yeniden sorulur. Günlük yük = (yayıncının ONAY'sız müşteri sayısı / 20) adet
`/iys/search` çağrısı — yayıncının KENDİ Netgsm kotasında (~10 istek/dk) harcanır, 04:52'de
başka planlı iş koşmazken. 30 günlük "zaten soruldu" hafızası (tekrar sorguyu gerçekten
azaltacak) gerekirse AYRI bir iş — bu PR'ın kapsamı dışında.
`[AutomaticRetry(Attempts = 0)]`, `[DisableConcurrentExecution(60)]`. Sınıf-seviyesi
öznitelikler kardeşlerle aynı.

`POST /api/panel/netgsm/account/iys-mirror` ucu KALIR (panelde düğme yok; destek/ileri
kullanım için).

### 2.3 Testler (sunucu)
- `PanelNetgsmAccountMirrorEnqueueTests`: doğrulama başarılı PUT sonrası Hangfire monitoring
  API'de `IysMirrorImportJob` + `licenseId` argümanlı iş var; doğrulama BAŞARISIZ PUT'ta
  yok.
- `IysMirrorSyncJobTests`: iki Verified + bir Failed hesap → yalnız iki iş kuyrukta,
  argümanları doğru lisanslar; hiç Verified yoksa hiçbir iş yok.
- Program.cs DI + recurring kayıt (`Job_DI_kapsamindan_cozulur` kalıbı).

## 3. Panel (OrderDeck-Mobile, `apps/panel`)

### 3.1 API modülü — `src/api/netgsmAccount.ts` (kalıp: `whatsappAccount.ts`)
```ts
export type NetgsmAccountView = {
  status: "none" | "failed" | "verified" | "disabled";
  smsEnabled: boolean;
  userCode: string | null; header: string | null; brandCode: string | null;
  passwordSet: boolean; lastError: string | null; lastVerifiedAt: string | null;
};
export type NetgsmSaveInput = { userCode: string; password?: string; header: string; brandCode: string };
useNetgsmAccount()      // GET /api/panel/netgsm/account; 400/403 kalıcı → retry yok
useSaveNetgsmAccount()  // PUT; başarıda qc.setQueryData(["netgsm-account"], resp.data)
```
`password` alanı boş string ise gövdeye **yazılmaz** (sunucu `null` = "değiştirme").
Hata: `problemMessage(e, fallback)` — sunucunun Türkçe `detail`'i olduğu gibi gösterilir
(`invalid-user-code`, `invalid-header`, `invalid-brand-code`, `password-required`,
`brand-code-taken`, `netgsm-account-disabled`, `verification-superseded`,
`netgsm-account-concurrent-create`, `no-active-license`). Slug'a göre DAVRANIŞ dallanmaz;
`verification-superseded`/`concurrent-create`'te ek olarak sorgu invalidate edilir
(ekran güncel hâli çeker).

### 3.2 Ekran — `src/screens/NetgsmKurulumScreen.tsx`, rota `/netgsm-kurulum`
Başlık "SMS ve İYS Kurulumu", alt metin: "Kampanya SMS'leri senin Netgsm hesabından,
senin başlığınla gider; onaylar senin İYS markana yazılır."

**Durum kartı** (sorgu sonucuna göre, `WhatsAppBaglaScreen.ConnectedCard` kalıbı):

| status | görünüm |
|---|---|
| `none` | nötr: "Henüz kurulum yok" |
| `failed` | sarı (Notice): "Doğrulanmadı" + `lastError` (varsa) — "Bilgileri düzeltip tekrar kaydet" |
| `verified` | yeşil: "Doğrulandı — kampanya gönderimi açık", `lastVerifiedAt` TR tarih, abone no / başlık / marka |
| `disabled` | kırmızı: "Kurulum yönetici tarafından kapatıldı. Destekle iletişime geç." — form gizli |

**Form** (`none`/`failed`/`verified`'da görünür; `verified`'da başlık "Bilgileri güncelle"):
- Netgsm abone numarası — zorunlu, `maxLength=32`, `inputMode="numeric"`.
- Netgsm API şifresi — `type="password"`, `autoComplete="off"`; `passwordSet` ise
  placeholder "Değiştirmek için doldur", boş bırakılırsa gönderilmez; ilk kayıtta zorunlu
  (istemci de kontrol eder, sunucu `password-required` ile yine reddeder).
- Gönderici başlığı — zorunlu, `maxLength=11`, ipucu "Netgsm'de onaylı başlığın".
- İYS marka kodu — zorunlu, yalnız rakam (`pattern`), ipucu "6 haneli İYS marka kodu".
- Kaydet düğmesi: gönderim sırasında "Doğrulanıyor…" (PUT senkron doğrular, birkaç saniye
  sürebilir), `disabled`. Başarıda durum kartı sunucunun döndürdüğü görünümle yenilenir;
  şifre alanı temizlenir.
- Hata kutusu (kırmızı, `WhatsAppBaglaScreen` ile aynı stil) `detail` metnini gösterir.

**Rehber** (form altında, `<details>` ile kapanabilir, üç madde):
1. Netgsm aboneliği: abone numarası ve API şifresi Netgsm panelinden (API erişimi açık
   olmalı).
2. Gönderici başlığı: Netgsm'de onaylanmış başlık (en fazla 11 karakter). Marka tescili
   şart değil; Netgsm başvuruda kabul ettiği belgeleri listeler.
3. İYS marka kodu: İYS'de (iys.org.tr) markanı kaydet, marka kodunu buraya yaz;
   Netgsm'in İYS entegrasyonu bu markaya bağlanır.

Kapanış cümlesi: "Doğrulama başarılı olunca İYS'deki mevcut onayların otomatik olarak
eşitlenir; ayrıca her gece yeni müşteriler için tekrar sorgulanır."

**Erişim:** yalnız owner. "Daha Fazla" menüsünde `principal !== "operator"` koşuluyla
`NavRow to="/netgsm-kurulum" label="SMS ve İYS Kurulumu" hint="Netgsm hesabını bağla,
kampanya SMS'leri senden gitsin"` (WhatsApp Bağla satırının hemen altı). Sunucu operatöre
403 döner; ekran yine de `owner-only` hatasını `problemMessage` ile gösterir.
Native'de de çalışır (tarayıcı/SDK şartı yok).

### 3.3 Testler (vitest, `NetgsmKurulumScreen.test.tsx`; kalıp `WhatsAppBaglaScreen.test.tsx`)
- Dört durum kartı; `disabled`'da form yok.
- Kaydet: PUT gövdesi `{ userCode, password, header, brandCode }`; `passwordSet` + boş
  şifre → gövdede `password` yok; ilk kayıtta boş şifre → istemci hatası, PUT çağrılmaz.
- Sunucu `detail` metni hata kutusunda; başarıda kart "Doğrulandı".
- `DahaFazlaScreen.test.tsx`: owner satırı görür, operatör görmez.
- `api/netgsmAccount.test.tsx`: 403'te yeniden deneme yok (kalıp `whatsappAccount.test.tsx`).

## 4. Sıra ve dağıtım
1. Sunucu PR (2.1 + 2.2 + testler) → merge → otomatik deploy (yayın günleri Paz/Pzt/Çar/Per
   20:00–01:00 dışında).
2. Panel PR → merge → panel deploy (`panel.orderdeckapp.com`).
3. İlk gerçek kurulum (Burak): PUT artık panelden; doğrulama sonrası logda
   `no-brand oynatma` + ayna işi kuyruğu; ertesi gün `iys-mirror-sync` 04:52 UTC.

## 5. Bilinçli sınırlar
- Ayna işi ONAY-only, `Unknown`/RET yazmaz (mevcut karar, değişmiyor).
- Günlük eşitleme kotası: 10 istek/dk paylaşımlı; ayna 6 sn/parti; birkaç yayıncı için
  dakikalar sürer, kabul edilebilir. Yayıncı sayısı büyürse marka başına gün-içi sıralama
  (push/verify gibi) gerekir — o gün gelince.
- `password` istemcide asla saklanmaz/loglanmaz; sunucu görünümde yalnız `passwordSet`.
