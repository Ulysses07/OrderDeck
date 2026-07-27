# WhatsApp Cloud API + AI Müşteri Asistanı — Entegrasyon Planı

_Taslak: 2026-07-24. Sahibi: Burak. Durum: PLAN (kod yok)._

## 0. Amaç ve kapsam

OrderDeck'e WhatsApp Business **Cloud API** entegrasyonu:

1. **Faz 0 — Bağlantı (tesisat):** Meta Cloud API'ye bağlan; gelen mesajları
   webhook ile al, giden mesajları API'den gönder. Numara **Coexistence** ile
   hem Business App'te hem API'de çalışır.
2. **Faz 1 — Panel (gelen kutusu):** OrderDeck içinde WhatsApp konuşmalarını
   gösteren, müşteri kaydına bağlı, "Kontrol Bekliyor" kuyruğu olan bir arayüz.
3. **Faz 2 — AI triage:** Gelen sorulara AI yanıt versin; bilemediğini (stok/ürün)
   "kontrol sağlanacak" deyip insana devretsin (co-pilot → yarı-otonom).

**Kapsam dışı (şimdilik):** toplu marketing kampanya motoru, katalog/ödeme
(WhatsApp Pay), sesli arama (Calling API — coexistence'ta zaten desteklenmiyor).

---

## 1. Ön koşullar — Meta tarafı (kullanıcı aksiyonu, kod değil)

> Sıra önemli: Coexistence numaranın **önce WhatsApp Business App'te** kayıtlı
> ve aktif olmasını ister (sende var).

1. **Meta Developer** hesabı + bir **App** (type: Business) oluştur, **WhatsApp**
   ürününü ekle.
2. **Coexistence Embedded Signup** akışıyla mevcut Business App numarasını bağla.
   - Eligibility: hesap yaşı + mesaj kalitesi değerlendirilir; yepyeni hesap hemen
     uygun olmayabilir.
   - Onboarding'de **profil fotoğrafı önceden** set edilmeli (sonra değişmez).
   - Bağlı cihazlar kopar (yalnız Windows/WearOS relink).
3. **Kalıcı Access Token** (System User token — 24 saatlik geçici token DEĞİL),
   **Phone Number ID**, **WABA ID**, **App Secret**, ve kendi seçtiğin bir
   **Verify Token** (rastgele string) al.
4. Meta panelinde **Webhook**: URL = `https://license.orderdeckapp.com/api/whatsapp/webhook`,
   Verify Token'ı gir, **`messages`** alanına abone ol (coexistence için
   `smb_message_echoes` dahil). Gerekirse `message_template_status_update`.
5. **Business Verification**: düşük hacimde şart değil ama messaging tier'ı
   yükseltmek (günlük limit) için gerekir. Sonra yapılabilir.
6. **Template'ler** (Faz 0 sonrası, pencere-dışı gönderim için): ödeme isteği
   (utility), kargo bildirimi (utility), OTP (authentication) — Meta onayına
   gönder. Onay 1–24 saat, ret olursa düzelt.
7. **Operasyonel kural:** Business App'i **13 günde bir aç** yoksa coexistence
   hesabı kopar. (Panel/AI çalışsa bile bu geçerli.)

---

## 2. Mimari genel bakış

```
Müşteri (WhatsApp)
   │  gelen mesaj
   ▼
Meta Cloud API  ──webhook POST──►  LicenseServer /api/whatsapp/webhook
   ▲                                   │ (imza doğrula, 200 dön, işi kuyruğa at)
   │  giden (API)                      ▼
   │                              Hangfire job: parse → DB'ye yaz → (Faz 2) AI
   │                                   │
   └────CloudApiWhatsAppSender◄────────┤ (yanıt gönder: free-form / template)
                                       ▼
                              SQL Server: Conversation / WaMessage
                                       │
                          ┌────────────┴─────────────┐
                     WPF panel (polling)        Web panel (polling)
```

- **Backend:** LicenseServer (webhook + secret'lar + DB zaten orada; provider
  pattern SMS/Push/Email gibi).
- **Gerçek-zamanlı:** mevcut kalıp **polling** (SignalR yok). WPF/web panel
  "son mesajdan beri yenileri" endpoint'ini periyodik çeker. (Gelecekte SignalR
  opsiyonel — bkz. §6.2.)
- **Coexistence:** Business App'te elle atılan mesajlar `smb_message_echoes`
  webhook'uyla DB'ye yansır → panel de görür. Business App paralel çalışmaya
  devam eder.

---

## 3. Faz 0 — Cloud API bağlantısı

### 3.1 Config / secrets (provider pattern)
- `WhatsAppOptions`: `Provider` (`log`|`cloud`), `PhoneNumberId`, `WabaId`,
  `AccessToken`, `AppSecret`, `VerifyToken`, `GraphApiVersion` (ör. `v21.0`),
  `MediaStorage` (R2 reuse).
- `builder.Configuration.GetSection("OrderDeck:WhatsApp").Bind(...)`; secret'lar
  VPS `.env`'den (`OrderDeck__WhatsApp__AccessToken` vb.). Repo'da boş/`log`.
- **Graph API sürümü** pinlenir (Meta sürümleri deprecate eder → kurulumda
  güncel sürüm teyit edilir; sabit bir versiyon string'i).

### 3.2 Webhook — inbound
- `WhatsAppWebhookController` `[AllowAnonymous]`:
  - **GET** `/api/whatsapp/webhook`: `hub.mode=subscribe` &
    `hub.verify_token == VerifyToken` ise `hub.challenge`'ı düz metin döndür,
    yoksa 403.
  - **POST** `/api/whatsapp/webhook`:
    1. **İmza doğrula:** `X-Hub-Signature-256` = `sha256=HMAC(appSecret, rawBody)`.
       Ham body üzerinden (model-binding'den ÖNCE) hesapla; eşleşmezse **401**.
    2. **Hızlı 200 dön** (< 5 sn; yoksa Meta retry eder) → gerçek işlemi
       **Hangfire job**'a at (async). Webhook thread'inde ağır iş yapma.
    3. Payload'ı ham JSON olarak sakla (audit + replay).
- **Idempotency:** her mesajın `wamid`'i benzersiz; DB'de unique index → aynı
  webhook tekrar gelirse (Meta retry) **çift işleme yok** (dedup).
- **Sıra dışılık:** mesajlar sıra dışı gelebilir → `timestamp`'e göre sırala,
  `wamid` ile tekilleştir.

### 3.3 Payload türleri (hepsi ele alınacak)
- **messages:** `text`, `image`, `audio`, `video`, `document`, `sticker`,
  `location`, `contacts`, `interactive` (button/list reply), `reaction`,
  `button` (template quick-reply), `order`/`system`. Bilinmeyen tür → "desteklenmeyen
  mesaj türü" placeholder + ham JSON sakla, çökme yok.
- **statuses:** `sent`/`delivered`/`read`/`failed` → giden mesajın durumunu
  güncelle. `failed` içindeki error code'u sakla + operatöre göster.
- **`smb_message_echoes`:** Business App'ten insan tarafından atılan mesaj →
  DB'ye "giden (app)" olarak yaz (çift sayma yok; kaynak = app).
- **`message_template_status_update`:** template onay/ret durumunu güncelle.
- **errors:** account/phone hataları → logla + uyarı.

### 3.4 Media
- Gelen medya = **media ID** (byte değil). İşleyici: Graph API'den media URL al
  (token'lı) → indir → **R2'ye** kaydet (mevcut `R2BroadcastMediaStorage` reuse) →
  DB'de kalıcı URL. Meta'nın media URL'i **kısa ömürlü** (indirmeyi geciktirme).
- Boyut limitleri (Meta: image 5MB, video 16MB, doc 100MB vb.) — aşılırsa
  metadata sakla, indirmeyi atla.

### 3.5 Sender — outbound
- `IWhatsAppSender`: `SendTextAsync`, `SendTemplateAsync`, `SendMediaAsync`,
  `MarkReadAsync`. `CloudApiWhatsAppSender` (Graph `POST /{phoneId}/messages`) +
  `LogWhatsAppSender` (dev stub).
- **24 saat penceresi zorlaması:** gönderimden önce konuşmanın "son gelen mesaj"
  zamanına bak → pencere **açık** ise free-form serbest; **kapalı** ise free-form
  **reddet**, çağırana "template gerekli" dön (ya da otomatik template'e düş).
- **Idempotency:** giden mesaja client-side `idempotency key` → retry'da çift
  gönderim yok. (Meta message id dönene kadar "pending" state.)
- **Hata kodları:** geçersiz numara / WhatsApp'ta değil / re-engagement
  (#131047) / template paused / rate limit (#130429) → yapısal hata, operatöre
  anlaşılır mesaj, gerekiyorsa retry/backoff.
- **Rate / messaging tier:** günlük business-initiated limiti (250/1K/10K/…
  kaliteye göre) → limit dolunca kuyrukta beklet + uyar.

### 3.6 Veri modeli (SQL Server, EF Core)
- **`WaConversation`**: Id, CustomerId (nullable → bilinmeyen numara), WaId
  (telefon/wa_id), DisplayName (profil adı), LastInboundAt (pencere hesabı),
  LastMessageAt, WindowState (computed), AssignedTo (agent), Status
  (open/pending-check/closed), Labels, CreatedAt.
- **`WaMessage`**: Id, ConversationId, Wamid (unique), Direction
  (in/out/app-echo), Type, Text, MediaRef, TemplateName, Status
  (sent/delivered/read/failed), ErrorCode, Timestamp, RawJson, SentByAgentId.
- **Müşteri eşleme:** gelen `wa_id` (telefon) → `WpfCustomerProjection` /
  Customer telefonuyla eşle (E.164 normalize). Eşleşme yoksa hafif "contact"
  oluştur, profil adını kullan.

### 3.7 Testler (Faz 0)
- Webhook imza doğrulama (geçerli/geçersiz), GET verify challenge.
- Idempotency (aynı wamid iki kez → tek kayıt).
- Her payload türü parse (text/media/status/echo/interactive/bilinmeyen).
- Sender: pencere-içi free-form OK, pencere-dışı free-form reddi, template gönderim.
- Media indir→R2 (stub).

---

## 4. Faz 1 — Panel (gelen kutusu)

### 4.1 Backend API (LicenseServer)
- `GET /api/whatsapp/conversations?since=…&status=…` — liste (sayfalı).
- `GET /api/whatsapp/conversations/{id}/messages?since=…` — mesajlar.
- `POST /api/whatsapp/conversations/{id}/send` — free-form/template gönder.
- `POST …/{id}/assign`, `…/{id}/label`, `…/{id}/close`, `…/{id}/read`.
- Yetki: mevcut operatör/panel auth. Rate limit.

### 4.2 Gerçek-zamanlı
- **Seçenek A (önerilen, mevcut kalıp):** WPF/web **polling** — 2–5 sn'de bir
  "son mesajdan beri". Basit, mevcut `*SyncHostedService` kalıbıyla aynı.
- **Seçenek B:** SignalR hub (server→client push) — daha akıcı ama yeni altyapı.
  Latency önemliyse sonra eklenebilir; API aynı kalır.

### 4.3 UI (karar: WPF / web / ikisi)
- **WPF sekmesi** (yayın operatörü — müşteri/sipariş verisi elinin altında).
- **Web panel** (her yerden erişim, ofis dışı arkadaşlar).
- Backend ortak → biriyle başlanır, diğeri sonra.

### 4.4 Panel özellikleri
- Sohbet listesi (okunmadı, müşteri adı, pencere durumu rozeti).
- Sohbet görünümü + müşteri kartına geçiş.
- Yanıt kutusu: pencere **açık**→serbest metin (ücretsiz); **kapalı**→template
  seçici (kategori/ücret göstergesi).
- **"Kontrol Bekliyor" kuyruğu** (AI/insan devri).
- Etiketler (Yeni / Ödeme bekliyor / Kontrol bekliyor / Kapandı) — opsiyonel
  WhatsApp **Label API** ile Business App'e de yansıt.
- **Eş-zamanlı yanıt çakışması:** iki agent aynı sohbete yazarsa → "claim/assign"
  ile kilitle; assign edilmemişse uyar.

---

## 5. Faz 2 — AI triage (tasarım şimdi, kod sonra)

- **Akış:** gelen mesaj → Claude API (sistem prompt + SSS/politika bilgi tabanı +
  **tool-use**). AI karar verir:
  - Cevaplayabildiği (kargo/ödeme/iade/genel) → yanıt taslağı.
  - Bilemediği (stok/ürün/fiyat) → **`escalate_to_human`** tool → "Kontrol edip
    döneceğiz 🙏" + "Kontrol Bekliyor" kuyruğu/etiket.
- **Model:** Haiku 4.5 (ucuz/hızlı triage) veya Sonnet 4.6 (zor akıl yürütme).
- **Kademeli:** Faz 2a **co-pilot** (AI taslak → operatör tek-tık gönder, risk
  sıfır) → 2b yarı-otonom (güvenli kategoriler + holding mesajı oto) → 2c tam
  otonom.
- **Edge-case / güvenlik:**
  - **Prompt injection:** müşteri metni AI'a **güvenilmez girdi** — jailbreak
    denemeleri; sistem prompt'ta veri/talimat ayrımı, "asla stok/fiyat uydurma,
    şüphede devret", çıktı doğrulama.
  - **Halüsinasyon kontrolü:** doğrulayamadığı bilgiyi söyleme → devret.
  - **Kill switch:** AI'ı anında kapat (provider flag).
  - **Token/bütçe limiti**, konuşma başına cap.
  - **Dil:** Türkçe; ton/marka rehberi.
  - **Loglama:** her AI kararı + gönderilen yanıt audit'e.
  - **Bağlayıcı taahhüt yok** (fiyat/teslim sözü verme).

---

## 6. Güvenlik & KVKK

- **Webhook imza doğrulama** (App Secret) — zorunlu; imzasız/yanlış → 401.
- **Token depolama:** VPS `.env`, repo'da yok; log'da maskeli.
- **KVKK/opt-in:** business-initiated için **onay** (mevcut `WhatsAppConsent`).
  **Opt-out:** "DUR"/"STOP" → consent kapat, marketing gönderme.
- **Veri saklama:** mesaj içeriği kişisel veri → retention politikası; müşteri
  silinince WhatsApp mesajları da sil (right to deletion).
- **Bloklar/şikayetler:** kalite düşüşü izle; numara kısıtlanabilir.
- **Audit log:** gelen/giden/AI kararları.

---

## 7. Test & operasyon

- **Dev:** `Provider=log` (gerçek API'siz), Meta **test numarası**/sandbox.
- **Webhook lokal test:** ngrok/geçici tünel veya staging.
- **Monitoring:** webhook fail, send fail, template ret, quality rating,
  messaging tier — uyarı (mevcut alert e-postası kalıbı).
- **Deploy:** yeni public endpoint; VPS `.env` secret'ları; Meta panelinde
  webhook URL kaydı; `Provider=cloud`'a çevir.
- **Rollback:** `Provider=log`/flag off → entegrasyon anında pasif.

---

## 8. Kararlar (2026-07-24, kilitlendi)

1. **Panel:** hem **WPF sekmesi** hem **web panel** — backend ortak, iki UI.
2. **Gerçek-zamanlı:** **SignalR** (otomatik reconnect + transport fallback →
   hem akıcı hem güvenilir). Polling'e düşmek gerekirse SignalR bunu zaten
   kendi içinde yapıyor.
3. **AI:** **erken otomatik**, ama güvenli kapsamla — "kontrol sağlanacak"
   holding mesajı 1. günden otomatik (sıfır risk), küratörlü SSS otomatik,
   riskli/serbest yanıtlar (stok/fiyat/taahhüt) insana devir. Guardrail +
   kill-switch hep açık.
4. **Sıra: OUTBOUND önce.** Önce biz-müşteriye gönderim otomasyonu (ödeme
   isteği, kargo bildirimi) — mevcut `wa.me` elle-tıkla akışının yerini alır.
   Sonra INBOUND panel + AI triage.
5. **Kuyruk/etiket:** OrderDeck kendi kuyruğu (birincil) + opsiyonel WhatsApp
   Label API ile Business App'e yansıtma.
6. **Meta hesabı:** **Business Verification + Tech Provider onaylı** → onboarding
   sürtünmesi düşük. Tech Provider olması ileride **çok-tenant** (başka
   yayıncıların numaralarını Embedded Signup ile bağlama, reseller) kapısını açar.
7. **Çok-tenant BİRİNCİL (ürün gereği):** Her yayıncı kendi WhatsApp numarasını
   **Embedded Signup**'la bağlar. **App Review gerekmiyor** — `whatsapp_business_messaging`
   Advanced Access'i **Tech Provider onayıyla zaten alınmış** (App Review, Tech
   Provider olmanın ön koşulu). Mimari baştan **tenant-aware** (her tenant: WABA
   Id + Phone Number ID + kendi token/PNID; secret'lar tenant başına şifreli
   saklanır). **Kendi numaran = ilk tenant** (uçtan test için).
   - Ödeme modeli: Tech Provider'da her tenant kendi ödeme yöntemini Meta'ya
     girer (kredi hattı yok). İleride "sen faturalandır" istersen Solution
     Partner yükseltmesi — ayrı ticari adım, engel değil.

### Sıralamanın etkileri (outbound-first)
- **Template'ler erken gerekir:** pencere-dışı gönderim için ödeme/kargo utility
  template'leri Meta onayından geçmeli → Meta aksiyonu (kullanıcı) erken.
- **Ücret:** pencere kapalıysa utility ~$0.0053/mesaj; müşteri 24s içinde
  yazdıysa ücretsiz.
- Faz 0 (webhook dahil) yine gerekir: giden mesajın **teslim durumu**
  (sent/delivered/read/failed) + müşteri **yanıtı** (pencereyi açar) webhook'tan
  gelir.

### Revize faz sırası
- **Faz 0** — tesisat (webhook + sender + config + veri modeli). _Her iki yön için._
- **Faz 1 — OUTBOUND otomasyon:** ödeme isteği / kargo bildirimi API'den
  gönderim (template + pencere-içi free-form) + teslim durumu + panelde giden
  mesaj/durum. `wa.me` elle akışının yerini alır.
- **Faz 2 — INBOUND panel:** tam gelen kutusu (mesaj al, yanıtla, kuyruk/etiket).
- **Faz 3 — AI triage:** erken-otomatik (güvenli kapsam) → yarı/tam otonom.

---

## 9. Görev kırılımı (üst düzey)

**Faz 0 (tesisat):**
- [ ] `WhatsAppOptions` + provider wiring (log/cloud) + Program.cs
- [ ] `WhatsAppWebhookController` (GET verify + POST + imza)
- [ ] Async işleme (Hangfire job) + idempotency (wamid unique)
- [ ] `IWhatsAppSender` + `CloudApiWhatsAppSender` + `LogWhatsAppSender`
- [ ] Veri modeli (`WaConversation`/`WaMessage`) + EF migration
- [ ] Payload parser (tüm türler + echo + status)
- [x] Media indir→R2 — `WhatsAppMediaDownloader` + `IWhatsAppMediaStore`
      (R2 / in-memory). İndirme inbound job içinde **senkron**: Meta'nın medya
      URL'i 5 dk sonra ölüyor. Hata/limit aşımı mesajı düşürmez, metadata kalır.
- [ ] Testler
- [ ] Deploy + VPS `.env` + Meta webhook kaydı

**Faz 1 (panel):**
- [ ] Backend API (list/messages/send/assign/label/close)
- [ ] Polling (veya SignalR) real-time
- [ ] UI (WPF ve/veya web) — inbox + pencere göstergesi + kuyruk
- [ ] Testler

**Faz 2 (AI):**
- [ ] AI servisi (Claude + tool-use + bilgi tabanı) — co-pilot
- [ ] Guardrail/prompt-injection/kill-switch
- [ ] Kademeli otonomi + audit
- [ ] Testler
