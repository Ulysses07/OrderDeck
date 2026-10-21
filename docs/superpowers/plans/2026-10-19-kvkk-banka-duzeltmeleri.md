# KVKK Banka Verisi Düzeltmeleri (sunucu kodu) — Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 19.10.2026 hukuki ön inceleme notunun (§5.1) sunucu tarafı kod önerilerini uygulamak: amaçsız banka verisini hiç kaydetmemek, kalanı kısa sürede ve kayıt altında silmek, ilgili kişinin silme/erişim talebini banka verisinde de karşılayabilmek, yayıncının veri sorumlusu kimliğini toplayıp göstermek.

**Architecture:** Değişikliklerin çoğu `OrderDeck.LicenseServer/Services/Bank` içinde: çekim işi (`ObifinPollJob`) giden/sıfır/çapa öncesi/onaysız hesap hareketini yazmadan atar, açıklamayı ve ham kaydı maskeleyip beyaz listeye indirir; saklama işi (`BankDataRetentionJob`) süreleri işlem tarihinden işletir ve her imhayı yeni `DataDestructionRecords` tablosuna yazar; bağlantının durdurulması/kaldırılması ve hizmet sonu silmesi `BankHasher` istemeyen yeni `BankConnectionTerminator`'da; KVKK silmesi ve yeni IBAN talep aracı ortak `BankSubjectScrub` kurallarını kullanır. Durdurulmuş ya da yerel verisi silinmiş bağlantının verisi eşleştirilmez (`ObifinConnectionService.MatchingActive`); Obifin'deki kopyanın yazılı silme teyidi bağlantıdan bağımsız bir kayıtla (`ObifinCopyDeletions`) izlenir. Mevcut veriyi yeni kurallara indiren tek seferlik `bank-data-cleanup` komutu varsayılan kuru koşar. Her şema değişikliği genişletmedir (yalnız sütun/tablo ekler): deploy'da `/ready` 60 sn'de gelmezse imaj geri alınır, göç alınmaz — eski imaj yeni şemayla çalışır.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server prod / InMemory test), Hangfire, Razor Pages (AdminCookie), xunit + FluentAssertions + AngleSharp, Testcontainers (SQL Server), bash (yedek betikleri).

**Kaynak:** hukuki ön inceleme notu (scratchpad `kvkk_hukuki_inceleme.md`, §3 bulgular, §5.1 kod listesi, §6 kararlar). Bu plan notun "kod değişirse" metin işaretlerini ([S1]–[S16]) karşılar; metnin kendisi (politika, form paragrafı, `KvkkNotice.TextVersion`) **bu planın kapsamı dışında**, ayrı metin işinde.

---

## Kapsam

**İçinde (notun §5.1 numaralarıyla):** P0 1–7, mevcut veri için tek seferlik temizlik komutu, P1 8–14, 15'in yalnız denetim kısmı, 16, 17, 19, 22 ve P2 25. Plan incelemesinin (19.10) eklediği parçalar da içinde: Obifin kaldırmasının lisansa özgü olması ve paylaşılan kullanıcı koruması (Görev 13), lisans yenilenince silmeme (Görev 14), Obifin bağlantılı yayıncı silmesinin reddi (Görev 13), imha kayıtlarının veritabanı dışındaki saatlik kopyası ve geri yükleme prosedürü (Görev 9, 23), kimlik değişimindeki gölge veri silmesinin (`ResetShadowData`) imha kaydı (Görev 9), eski kısa maskelerin ve eski denetim satırlarındaki kullanıcı adının temizliği (Görev 8, 16), talep doğrulamasının kaydı (Görev 17). Görev 10–22'nin ikinci plan incelemesinin (20.10) ekledikleri: durdurulmuş ya da yerel verisi silinmiş bağlantıda eşleştirmenin, dekont bağdaştırmasının ve IBAN öğrenmenin de durması; Obifin tarafının günlük hatırlatma ve kişisel veri içermeyen yazılı silme teyidi kaydıyla izlenmesi; onaysız hesabın reddi ve P1'den önce yazılmış verisinin silinmesi, onayın geri alınması; "Kaldır ve sil"de lisans anahtarıyla yazılı onay; yerel silmenin partili yapılması ve gerçek SQL Server testi (Görev 12–14). Banka veri talebinin kişisel veri içermeyen sonuç kategorileri (yanıtlandı, doğrulanamadı, kanuni gerekçeyle ret, geri çekildi), silmenin yalnız silme talebinde yapılması ve CSV kaçışının sekme/satır başını kapsaması (Görev 17–18). Veri sorumlusu bilgisi olmadan banka modülünün açılmaması (fail-closed, pilot için bayrak) ve formda gösterilen bilginin sürümünün kayda yazılması (Görev 21–22). Güvenlik saklama işinin imha ve koşu kaydı (Görev 20). Yeni yönetici sütunlarında kullanıcı adı yerine kimlik (Görev 12, 17, 18, 21). P1'in ilk koşuları için deploy öncesi beklenen sayımlar (Prod adımları).

**Kapsam dışı (gerekçesi, takibi ve sahibiyle):** "Yarım iş yapmayalım" isteği gereği dışarıdaki her madde için takip planı ve kararı veren kişi yazılı. Bu tablodaki maddeler kapanmadan KVKK düzeltmeleri "tamam" sayılmaz.

| # | Konu | Neden dışarıda | Takip / sahibi |
|---|---|---|---|
| 15 (kısmen) — Ö-4 öneri 3 | Yönetici girişine iki aşamalı doğrulama (AdminCookie + Bearer-Admin) | Kurul 2018/10 karar 3-e: özel nitelikli veri için m. 6/4'ün **zorunlu** önlemi (açıklamada tesadüfen geçen özel nitelikli veri, Ö-4). Ayrı bir kimlik doğrulama işi (TOTP, kurtarma kodları, oturum yenileme); görev tanımı bu planın dışında bıraktı. Denetim kısmı Görev 16'da. | Ayrı plan `docs/superpowers/plans/<tarih>-admin-iki-asamali-dogrulama.md`. Sahibi **Burak**. Kapı P1 ile aynı: Faz 2'den ve ikinci yayıncıdan önce yayında. |
| 18 / **K-8 (Kritik)** — kardeş repolar | Shopper kayıt ve dekont ekranı metinleri, Profil'de "Gizlilik Politikası ve haklarım", bir kerelik duyuru (OrderDeck-Shopper); panelde politika bağlantısı (OrderDeck-Mobile) | Başka repolar; metinler §4.6'dan, avukat onayına bağlı. Shopper'ın göstereceği veri sorumlusu alanının **sunucu ucu** Görev 22'de. | Her repoda ayrı plan (`OrderDeck-Shopper` ve `OrderDeck-Mobile` `docs/superpowers/plans/<tarih>-kvkk-k8-metinler.md`). Sahibi **Burak**; metin **avukat** onayından sonra, P1 ile aynı sürüm penceresinde. |
| 18 / **K-8**, 17 / **K-3** — bu repo, WPF | WhatsApp ödeme şablonuna tek satır: `OrderDeck.Core/Settings/AppSettings.cs:180-184` (`PaymentSettings.WhatsAppMessageTemplate` varsayılanı) ve mevcut kurulumların kayıtlı şablonu için bir kerelik ayar göçü. Aynı mesaja **veri sorumlusunun unvanı ve KVKK başvuru e-postası** da basılır (K-3 öneri 3, not §5.1/17 "ödeme mesajına basılması"). Değer sunucudaki `DataControllerProfiles`'tan (Görev 21) gelir; WPF'in onu okuyacağı uç (lisans kimliğiyle) takip planında yazılır. Profil yoksa satır basılmaz ve WPF uyarır. | WPF (OrderDeck.Core/App) değişikliği, Velopack sürümüyle çıkar; bu plan yalnız sunucu. Satırın vereceği bağlantının sayfası ve metni avukat onaylı olmalı; şablon Meta onaylıysa yeniden onaya gider. | Ayrı plan `docs/superpowers/plans/<tarih>-kvkk-k8-wpf-odeme-sablonu.md`. Sahibi **Burak**; metin **avukat**. |
| K-8 — politika | Dekont işlemenin politikada alt bölümü (PDF ayrıştırma, `PayerName`, R2 saklama, süre); kapsam cümlesinin genişletilmesi | Metin işi. | Metin işi (v2) / **avukat**. |
| 20 | Formdaki zorunlu "okudum" kutusunu kaldırma | Avukat onayına bağlı (not §4.5 Seçenek 1/2). | Metin işi; karar **avukat**. |
| 21 | Yönetici formundaki banka kimlik bilgisi girişini kaldırma **ya da işletenin kendi lisansıyla sınırlama** | Kaldırma, Faz 2 Obifin kiracılık modeli kararına bağlı (not §6 karar 9). Notun ikinci seçeneği ("işletenin kendi lisansıyla sınırla": banka kimliği yalnız OrderDeck'i işletenin lisansında girilebilir) Faz 2'ye bağlı DEĞİL, bugün de uygulanabilir; Ö-8'in amacı (yayıncının banka şifresinin OrderDeck'te durmaması) ikinci yayıncı gelmeden karşılanır. | Kaldırma: Faz 2 spec §13. Sınırlama: ayrı küçük plan (`docs/superpowers/plans/<tarih>-obifin-banka-kimligi-sinirla.md`), ikinci yayıncıdan önce. Karar ve sahibi **Burak**. |
| 23 | Lisansa özgü HMAC, maskede kontrol hanelerini gizleme | P2; maske biçimi değişmez, politika metni "ilk 4 ve son 3 karakter" ile yazılır ([S7] "kod değişmezse" satırı). | P2 listesi; **Burak**. |
| 24 | Gönderen adını Faz 2'ye kadar hiç yazmama | Burak kararı (not §6 karar 4). Kod bugünkü gibi yazmaya devam eder. | Karar verildi (**Burak**). |
| 26 | Açıklama sütununu şifreleme | P2 değerlendirme. | P2 listesi; **Burak**. |
| 27 | Geri alma gerekçelerine "İlgili kişi itirazı" | Faz 2 (otomatik onay) işi. | Faz 2 spec. |
| — | Faz 2 işleri (havale onayı, otomatik onay, `BankIbanState`, `PaymentExpectation`) | Ayrı spec (`2026-10-10-banka-havale-onay-design.md`). | Faz 2 spec. |
| — | `WpfCustomerProjection.ScrubPersonal`'ın kullanıcı adını da boşaltması; `Payment.ShopperId` bağının kaderi | Avukat kararı (not K-7 öneri 1–2, §7 soru 8). Görev 15 bu ikisine dokunmaz. | Karar **avukat**; kod tek satır (`ScrubPersonal`; Görev 15'in uygulama notu: silmenin banka adımı kullanıcı adı anahtarını boşaltmadan önce alır, satır eklenince de çalışır). |
| — | Sipariş bildiriminin (yeni sipariş push'u) gövdesindeki tutar | Not §5.1/19 yalnız dekont onay/ret bildirimini sayıyor; sipariş push'u ayrıca değerlendirilmeli (aşağıda karar listesinde). | Karar **Burak** (karar 9). |
| — | R2 yaşam döngüsü kuralı (`sql-bak/` önekine 32 gün), R2 kova konumu, Hostinger veri merkezi tespiti | Cloudflare/Hostinger panosu işi. | **Burak** (Prod adımları 7). |
| — | `BankTransactions.CounterpartyTaxIdHash` sütununu düşürme (daraltma adımı) | Genişlet/daralt kuralı: P0 (Görev 3) yazmayı kesti, Görev 8 değerleri boşalttı. Sütun ancak eski imaja dönme ihtimali kalmayınca düşürülür; varlık özeti "bir sonraki sürümde düşürülür" diyor. Bu plan yalnız genişletme yapar (`DropColumn` yasak). | Ayrı küçük plan (`DropColumn` göçü + varlıktan alan), P1 prod'da bir sürüm döndükten sonra. Sahibi **Burak**. |
| — | R2'de duran, HashKey içeren eski `.env` yedekleri; HashKey'i döndürme kararı | Görev 23 yeni `.env` yedeğine anahtarı koymaz. Ama eski `env/` nesneleri uzak budama süresi dolana kadar R2'de kalır: SQL yedeğiyle aynı kovada, aynı parolayla (Ö-6'nın kaygısı o süre boyunca sürer). Döndürmek bütün özetleri ve IBAN hafızasını geçersiz kılar: eski özetler yeni anahtarla eşleşmez; hafıza sıfırlanır, hareket özetleri yeniden hesaplanamaz (ham IBAN saklanmıyor). | Karar **Burak**: (a) Görev 23'ün betikleri kopyalanıp ilk temiz `.env` yedeği alındıktan sonra eski `env/` nesnelerini elle silmek (önerilen, ucuz); (b) döndürmenin gerekli olup olmadığı (eski yedekler parolayla şifreli, parola Bitwarden'da). Prod adımları 6'ya bağlı; sonuç tutanağa. |

## Burak / avukat kararı gerekenler (plan varsayılanıyla ilerler, değiştirmek tek satır)

1. **Saklama süreleri** (Görev 5, 10, 13, 14, 20): ham kayıt 30 gün; eşleşmeyen havalenin açıklaması/adı 30 gün; hareketin tamamı + eşleştirme satırı + gap 180 gün; IBAN bağı son doğrulamadan 12 ay; çekimden sonra en az 7 gün; hizmet sonu + 30 gün; silme talebindeki telefon işlendikten, kapanmış banka veri talebinin kısaltılmış IBAN'ı kapanıştan 30 gün sonra; imha kaydı süresiz (en az 3 yıl), kişisel veri içermeyen imha denetim olayları 36 ay (satırdaki silmeyi yapan yöneticinin kullanıcı adı ve IP'si yine 24 ayda maskelenir: ad yöneticinin takma kimliğine çevrilir, IP boşalır; 24–36 ay arasında satırda yalnız takma kimlikler ve sayılar kalır — avukat paketinde "denetim kaydı 24 ay" satırının yanına bu not girer). Hepsi `BankOptions` / `SecurityDataRetentionOptions` / sabit; not "süreler öneri, avukat onaylamalı" diyor.
2. **İlk çekim çapası** (Görev 7): bağlantı anı − 2 gün (dekont bağdaştırmasının aday penceresi). Gerekçe Görev 7'de. Alternatif 30 gün. Avukata metindeki karşılığıyla birlikte sunulur (karar 20).
3. **Eşleştirme kaydının 180. günde silinmesi** (Görev 10): not "sil ya da sayılara indir" diyor; plan **siler**, ölçüm ekranı zaten son 30 güne bakıyor. Ölçüm geçmişi istenirse ayrı iş.
4. **Onaylı hesap listesi fail-closed** (Görev 12): mevcut hesaplar göçle **onaylanmaz**. Deploy'dan hemen sonra admin/obifin'de EMAR hesapları onaylanmalı; onaylanana kadar gelen hareket kaydedilmez ve imleç ilerlediği için sonradan gelmez. Alternatif: göçte `Active = 1` hesapları otomatik onaylamak (not §5.9 "EMAR dışı hesap var mı" kontrolünden sonra). Kapı ileriye dönüktür: P1'den önce onaysız hesaplardan yazılmış hareketler, yönetici o hesapta "Reddet ve verisini sil"e basana (Görev 13) ya da 180 günlük kurala kadar kalır; EMAR dışı hesap varsa P1'den hemen sonra reddedilir (Prod adımları 3). Aynı eylem onaylı hesabın onayını geri alır.
5. **Maskeli kaynaklı özetler** (Görev 11): mevcut özetlerin maskeli IBAN'dan üretilip üretilmediği saklı veriden tam belirlenemez. `bank-data-cleanup --reset-iban-memory` IBAN hafızasını siler (not Ö-7: "belirlenemezse IBAN hafızası sıfırlanıp yeniden öğrenilsin"). Özeti aynı lisansta geçerli IBAN'lı bir harekette (Görev 11'den sonra çekilmiş, `CounterpartyIbanHashValid`) görünen bağ **kalır**: maskeli değerin özeti geçerli IBAN'ınkine eşit olamaz, böyle bir bağ kanıtlanmış biçimde geçerli IBAN'dandır (Görev 11 incelemesi). Hareketlerdeki özetler **kalır** (IBAN talep aracı onlarla arar). Silinen bağ yalnız Görev 11'den sonra çekilmiş, geçerli IBAN'lı bir havalenin yeni kararıyla yeniden öğrenilir; kararı verilmiş eski hareketten öğrenilmez. Koşturup koşturmamak Burak'ın kararı; plan önerisi: Faz 2'den önce koştur. Geç koşmak P1'den sonra öğrenilmiş bağları kaybettirmez; yalnız son 180 günde o IBAN'dan geçerli havale gelmemiş bağ gider (kanıt hareketle yaşar).
6. **Silme talebinde harekette kalan özet ve maske** (Görev 15, 17): plan özet ve maskeyi **tutar** (Faz 2 tasarımı güvenlik için istiyor); açıklama, ad, ham kayıt ve eşleştirme kimlikleri silinir. Avukat "özet de silinsin" derse `BankSubjectScrub.Transaction`'a iki satır.
7. **IBAN talep aracında eşleşme kimliği** (Görev 17): IBAN hafızası yoksa eşleştirme satırındaki müşteri kimliği silinmez (havaleyi gönderen ile ödemesi sayılan müşteri farklı kişi olabilir); yalnız kanıt ve hareket metni silinir.
8. **HashKey yedeği** (Görev 23): `.env` gece yedeğinden çıkarılır, yalnız Bitwarden'da kalır. Ön koşul: Burak HashKey'in Bitwarden'da güncel olduğunu doğrular.
9. **Sipariş push'undaki tutar**: dokunulmadı (yukarıda).
10. **Veri sorumlusu bilgileri** (Görev 21–22): müşteri (yayıncı hesabı) düzeyinde, kayıt formunda ve Shopper kod sorgusunda **anonim** gösterilir. Şahıs yayıncının ev adresinin bu yolla açılması avukata sorulmalı (not §7 soru 9).
11. **Veri sorumlusu kimliği çelişkisi** (politikada "Musa Sevinç", Meta'da "Emar Global Ltd. Şti."): bu plan metne dokunmaz; avukat paketinde.
12. **Obifin'de kaldırmayı yalnız yönetici yapar** (Görev 13–14): otomatik hizmet sonu işi yerel veriyi siler, `bankaapi/sil` çağırmaz; paylaşılan Obifin kullanıcısında "Kaldır ve sil" de Obifin'e gitmez. Alternatif (paylaşılmayan kullanıcıda otomatik kaldırma) insan onayı olmadan dış sistemde geri dönüşsüz iş yapacağı için seçilmedi. Bu karar Obifin tarafını tek bir e-postaya bırakmaz: Obifin tarafı karar 21'deki takiple izlenir.
13. **Obifin bağlantılı yayıncının silinmesi reddedilir** (Görev 13): önce "Kaldır ve sil". Alternatif (purge içinde kaldırma) silme isteğine ağ çağrısı ve ikinci bir Obifin yolu eklerdi.
14. **Lisans yenilenince** (Görev 14) kendiliğinden durdurulmuş bağlantı `Unverified`'a döner ve silinmez; yöneticinin durdurduğu açılmaz, 30. günde yerel verisi silinir.
15. **`customer.purged` denetim satırındaki `preEmail`** 24 ay kalır (mevcut davranış; 36 ay muafiyetinden çıkarıldı). Avukat "hiç yazılmasın" derse `Purge`'te tek satır.
16. **Banka veri talebinde doğrulama yöntemleri** (Görev 17): ıslak imzalı dilekçe / KEP–güvenli e-imza–mobil imza / kayıtlı iletişim kanalı, her birinde IBAN sahipliği banka belgesiyle. Liste avukata sorulmalı (Başvuru Tebliği kanallarıyla yeterli mi).
17. **Geri yükleme sonrası yeniden uygulama** (Görev 9, 23): dış kopya saatlik e-posta ve durum tutar (`DigestedAt`, Görev 9 incelemesi): gönderim düşerse (SMTP, boş `Admin:AlertEmail`) iş Failed olur ve kayıtlar sonraki saatte yeniden denenir, kesinti birikimi ilk koşuda gider. Boşluk = son başarılı özetten sonraki kayıtlar: olağan durumda en çok ~1 saat 13 dk (saat başı +13'te önceki saatler gider); e-posta saatlerce düşmüşse daha uzun, ama kayıtlar tabloda bekler ve Hangfire'da `data-destruction-digest` Failed görünür. Bu boşluk için canlı veritabanı gerekir. Avukata: bu boşluk ve elle yeniden uygulama prosedürü yeterli mi.
18. **[S4] açıklama maskesinin metni** (Görev 4; metin işi / **avukat**): kod açıklamada *tanınabilir biçimde yazılmış* TR IBAN'ı ve 11 haneli numarayı maskeler, her yazımı değil. Bilinçli olarak düz kalanlar: kontrol hanesi tutmayan (yanlış yazılmış) IBAN; TR dışı IBAN; `-`, `.` ya da birden çok boşlukla bölünmüş IBAN; boşluk, nokta ya da tireyle bölünmüş 11 hane; daha uzun bir rakam dizisinin içindeki 11 hane; `+90…` ile (12 hane) ya da boşluklu yazılmış telefon; harfe bitişik ama kontrol hanesi tutmayan 11 hane (yanlış yazılmış TCKN, kullanıcı adındaki "0" ile başlayan telefon). Metindeki mutlak "kayda alınırken maskelenir" cümlesi Ö-16'nın "metin mutlak güvence veriyor, kod tutmuyor" sorununu küçük ölçekte tekrarlar. Öneri: "Açıklamada tanınabilir biçimde yazılmış TR IBAN'ları ve 11 haneli kimlik numaraları kayda alınırken maskelenir" gibi niteleyici bir ifade; ya da bu sınırlar avukat paketinde listelensin. Kod değişmez; karar metinde.
19. **Ham kaydın kopyasında "işlem açıklaması" (`IslemAciklama`)** (Görev 5; metin işi / **avukat**): kod kopyada **yedi** alan tutar (açıklama, işlem açıklaması, gönderen adı, gönderen banka, işlem kodu, referans, makbuz no); notun v2 metni (§4.3 [S3]), §3 Ö-2 öneri 2 ve avukat paketi **altı** sayıyor, işlem açıklaması eksik. Metin bu hâliyle onaylanırsa saklanan kopyayı eksik anlatır (Ö-16). Plan alanı **tutar**: açıklama sütunu `Aciklama` yoksa `IslemAciklama`'dan okunur (`ObifinClient.cs`: `Str(row, "Aciklama") ?? Str(row, "IslemAciklama")`), yani teknik arıza incelemesinde sütunun kaynağı odur; alan `Aciklama` ile aynı maskeden geçer. Metin işinde "işlem açıklaması" eklenecek yerler: notun §4.3 [S3] satırı ve §3 Ö-2 öneri 2; avukat paketinde `01-veri-isleme-sozlesmesi-yayinci.md` (teknik önlemler madde c ve saklama tablosunun "kaydın kopyası" satırı), `05-imha-proseduru.md` (süre tablosunun "kaydın kopyası" satırı), `09-karar-kaydi.md` K3 ("altı alan" → yedi); metin yayımlanınca politikanın iki kopyası (`Pages/Public/_GizlilikPolitikasi.cshtml`, `web/app/(tr)/gizlilik-politikasi/page.tsx`). Alternatif (alanı atmak): `BankRawJsonRedactor.AllowedFields` ve `DescriptionFields`'tan birer kelime + `BankRawJsonRedactorTests.PolicyFieldList`; o zaman açıklaması `IslemAciklama`'dan gelen satırda kopya sütunun kaynağını göstermez. Liste `BankRawJsonRedactorTests.PolicyFieldList`'te metin olarak sabit: kod ya da metin değişirse öbürü de değişir.
20. **[S5] ilk çekimin metni** (Görev 7; metin işi / **avukat**): kod ilk çekimi bağlantı gününün **iki takvim günü öncesinin başından** başlatır. Çapa, bağlantının ilk çekim turunun TR günü − `InitialGraceDays` (2). Tur çapayı Obifin'e gitmeden kaydeder; tur yarıda kalsa ya da hiç kayıt yapmadan düşse de (geçici hata, Obifin reddi) çapa kalır, sonraki turlar aynı çapadan sürer, kimlik değişene kadar değişmez. İlk tur doğrulanmış bağlantıda en geç 5 dakika içinde koşar (çekim işi gecikirse çapa yalnız daha ileri düşer), yani bağlantı anından en çok yaklaşık üç gün geriye gidilir. Hiçbir turda çapadan eski gün Obifin'den istenmez; Obifin istenen aralığın dışından hareket döndürse de çapadan eskisi yazılmaz. Notun [S5] seçenekleri kodla uyuşmaz. "Son 30 günün" kodu yanlış anlatır. "Bağlantının kurulduğu andan itibaren" ise kodun tutmadığı mutlak bir güvence verir: Ö-16'nın "metin mutlak güvence veriyor, kod tutmuyor" sorunu küçük ölçekte yeniden doğar. Kodla birebir uyan ifade: "bağlantı ilk kurulduğunda, kurulduğu günden en çok iki gün öncesine kadar uzanan hareketler bir kez alınır; daha eski hareketler alınmaz." Metin işinde değişecek yerler: notun §4.3 metnindeki "son {30} günün hareketleri bir kez alınır [S5]" cümlesi ve §4.3 tablosunun [S5] satırı; avukat paketinde `09-karar-kaydi.md` K6, şöyle doldurulur: "İlk bağlantıda geçmişe dönük çekim 90 günden bağlantı gününün iki gün öncesine iner; …". Metin yayımlanınca politikanın iki kopyası da değişir (`Pages/Public/_GizlilikPolitikasi.cshtml` ve `web/app/(tr)/gizlilik-politikasi/page.tsx`; ikisi bugün "son 90 günün" diyor). Karar 2'deki "Alternatif 30 gün" avukata bu ifadeyle yan yana sunulur. 30 gün seçilirse `InitialGraceDays` ile onu aday penceresine bağlayan test (`Ilk_cekim_payi_dekont_aday_penceresine_esit`) değişir; metin de "son 30 günün" olur. Mevcut bağlantılarda (Görev 8) çapa = ilk çekimin günü − 2. Çapadan eski kararsız satırlar çapası olan her bağlantıda silinir (P0 anında yarıda kalmış eski ilk çekim dahil). Çapadan eski olup bir karara, IBAN öğrenmesine ya da gap'e bağlı satırlar kalır ve 180 günlük kuralla silinir (Görev 10); K6'nın ikinci yarısı bunu anlatır. **Sıra:** Görev 7 kodu metinden önce ya da metinle aynı anda canlıya çıkar. [S5] cümlesi (politikanın iki kopyası) ancak P0 deploy'u ve `bank-data-cleanup --apply` bittikten sonra yayımlanır. Kod önce çıkarsa metin bir süre fazlasını ("son 90 gün") anlatır: işleme metinden dardır, ihlal değildir. Ters sıra (önce metin) Ö-16'nın kalıbını kurardı: 90 günlük kod çalışırken ve eski satırlar silinmeden metin mutlak bir güvence vermiş olurdu. Kod değişmez; karar metinde.
21. **Obifin tarafının takibi** (Görev 13–14; K-6 öneri 1, Ö-9 öneri 3): otomatik iş Obifin'e gitmediği için (karar 12) Obifin'in bankadan çekmeyi ve saklamayı bırakması iki adımda izlenir. (a) Yerel verisi hizmet sonunda silinmiş ama "Kaldır ve sil" bekleyen bağlantı (`BankDataPurgedAt` dolu). Yerel silme "Kaldır ve sil"in sahiplik kanıtını bırakır: Obifin kimliği, yerel banka bağlantıları ve onaylı hesap satırları (IBAN, IBAN özeti ve bildirim notu boşaltılmış; hesap numarası, onay anı ve onaylayanın takma kimliği kalır) "Kaldır ve sil"e kadar durur. Avukat paketinde hizmet sonu silmesinin kapsamı bu cümleyle anlatılır. (b) "Kaldır ve sil"in açtığı, Obifin'den yazılı silme teyidi bekleyen kayıt (`ObifinCopyDeletions`: lisans, istek anı ve yapan, teyit anı ve yapan, Obifin'de elle yapılacak iş — kaldırılamayan ya da sahibi doğrulanamayan banka bağlantısı numaraları, ulaşılamadı, paylaşılan kullanıcı; kişisel veri yok; lisansa ve bağlantıya FK yok, budanmaz). Hizmet sonu işi ikisini her gün tek bir hatırlatma e-postasında listeler. Biri 14 günü (`BankConnectionTerminator.ObifinFollowUpOverdue`) geçtiyse konu "GECİKTİ" olur. Yönetici teyit yazısı gelince admin/obifin'de "Teyit alındı"ya basar; bu bir imha kaydı yazar (`Obifin.HareketKopyasi`, tetikleyici `processor-confirmation`). 14 gün bir öneridir: Obifin sözleşmesindeki silme süresi netleşince ona çekilir. Avukat paketindeki iç imha prosedürüne (`05-imha-proseduru.md`; metin işi) aynı adım girer: "Obifin'e yazılı silme talebi, teyit yazısı KVKK klasöründe 3 yıl, sistemde 'Teyit alındı'".
22. **Banka veri talebi satırının saklama süresi** (Görev 17, 20): talep satırı başvurunun belgesidir: alış, son tarih, tür, doğrulama yöntemi, sonuç kategorisi ve yöneticilerin takma kimlikleri. Kapanıştan 30 gün sonra satırda kişisel veri kalmaz (özet kapanışta, kısaltılmış IBAN +30 günde boşalır; ikisi de imha kaydıyla), ama satır süresiz durur. Plan satırı silmez. Önerilen varsayılan: kapanıştan 3 yıl sonra silinsin (imha kaydıyla aynı süre, Silme Yönetmeliği md. 7/3 kalıbı); kod tek sorgudur (`SecurityDataRetentionJob`, Görev 20'nin kalıbı, imha kaydıyla). Aynı soru `ShopperDeletionRequests` için de geçerli (telefonu 30 günde boşalıyor, satır süresiz). Karar **avukat**; onaydan sonra küçük bir iş.
23. **Veri sorumlusu bilgisi olmadan banka modülü açılmaz** (Görev 21; K-3 öneri 3 "banka modülü açılmadan önce"): ilk Obifin bağlantı kaydı, Doğrula ve Banka ekle, lisansın müşterisinde `DataControllerProfile` yoksa reddedilir ve Obifin'e gidilmez. `Obifin:RequireDataControllerProfile` varsayılan `true` (fail-closed). Yalnız pilot geçişinde `.env`'e `Obifin__RequireDataControllerProfile=false` yazılabilir: metin işi (form paragrafı v2) yayımlanıp EMAR'ın bilgisi girilene kadar bir Doğrula/Banka ekle gerekirse (Prod adımları 8). Mevcut Verified bağlantının çekimi bu kapıya takılmaz (veri kaybı olmasın).
24. **Gösterilen veri sorumlusu bilgisinin ispatı** (Görev 21–22; m. 10/1-a, ispat yükü veri sorumlusunda): profil her içerik değişikliğinde yeni bir sürüm alır; sürümlerin içeriği `DataControllerProfileVersions`'ta tutulur (yalnız eklenir). Kayıt formu gönderimi sayfanın gösterdiği sürümü `IntakeFormSubmission.DataControllerVersion`'a yazar (gizli alan, yalnız o yayıncının var olan bir sürümü). Bir kayıt anında hangi kimliğin gösterildiği böylece sonradan ispatlanır. Sınırlar avukat paketine not olarak girer: (a) sürüm satırları şahıs yayıncıda kişisel veridir ve yayıncının KVKK silmesinde profille birlikte silinir; o andan sonra eski gönderimin hangi kimliği gördüğü ispatlanamaz. (b) Shopper ekranlarında (kardeş repo) gösterilen sürüm kaydedilmez. Avukat "sürümler yayıncı silindikten sonra da şu süre saklansın" derse purge satırı değişir.
25. **Eşleme kaldırılınca öğreten hareketin IBAN bağının silinmesi imha kaydına girsin mi** (Görev 10 ikinci incelemesi): kaldırma, o hareketin öğrettiği bağı siler (spec §3: yanlış öğrenmenin geri alınması, iptal bayrağı değil silme). Plan bunu **kayda almaz**: yanlış karardan türemiş verinin düzeltmesidir (KVKK m. 4/2-b doğru ve güncel olma), saklama gerekçeli imha değildir. Kalan doğrulaması olmayan başka bağın silinmesi ise kayda girer (Görev 10). Silme Yönetmeliği md. 7/3'ün "her silme işlemi" ifadesi düzeltmeleri de kapsıyor sayılırsa: `PaymentMatchReconciler.UnmatchOnceAsync`'teki `learned` satırına aynı `DataDestruction.Record` (ölçüt `kaldırma, öğreten karar geri alındı`, tetikleyici `admin-remove`). Karar **Burak** / avukata sorulabilir.
26. **TR IBAN'ının hesap kısmında yalnız rakam mı** (Görev 11 incelemesi, Ö-7): `X` ile maskelenmiş değer harf/rakam, uzunluk ve TR biçimi denetimlerinden geçer, yalnız mod-97 eler (~1/97 tutar). Plan varsayılanı, standarda uyumlu asgari kural: TR'de banka kodu ve rezerv hane rakam (SWIFT IBAN kaydı `TR2!n5!n1!n16!c`), BBAN'da aynı harften 3 ya da daha uzun dizi maske sayılır (`BankHasher.NormalizeIban`). 16 karakterlik hesap kısmında meşru harf kabul edilir. Alternatif (daha sıkı): TR'de hesap kısmı da yalnız rakam. Türk bankaları pratikte sayısal hesap numarası kullanıyor; bu kural harfle maskelemenin dizi olmayan biçimlerini de (`X1X2…`) kapatır, ama Görev 11'in "TR BBAN harf içerebilir" kararını tersine çevirir. Kod: `NormalizeIban`'daki TR döngüsünün sınırı `10` → `TrIbanLength`, `Harf_iceren_TR_BBAN_…` testi tersine döner. Hata yönü iki seçenekte de güvenli: geçersiz sayılan değer özetlenmez, maskesi `…` olur. Rakamla maskeleyen banka (ortayı 0 yapan) iki kuralla da ayırt edilemez: değerlerin ~96/97'si mod-97'de düşer, hesabın maskeli sayacı o bankayı gösterir. Karar **Burak**.
27. **Yerel verisi silinmiş bağlantıda kimlik değişimi** (Görev 13 incelemesi; karar 21(a)): plan varsayılanı **reddetmek** ("önce Kaldır ve sil"). Kimlik değişimi gölge veriyle "Kaldır ve sil"in sahiplik kanıtını siler, lisans geçerliyse `BankDataPurgedAt`'i de boşaltırdı; takip zinciri kopar. Alternatif: değişime izin verip eski bağlantıda yerel banka bağlantısı ya da onaylı hesap varsa bir `ObifinCopyDeletions` kaydı açmak ve eski Obifin kullanıcısındaki bağlantıların elle kaldırılması gerektiğini bildirmek. Kod: `ObifinConnectionService.UpsertWithResultAsync`'teki tek denetim. Karar **Burak**.
28. **Yerel verisi silinmiş bağlantı yeniden açılınca ilk çekim çapası** (Görev 13 ikinci incelemesi; Ö-5, karar 20): Etkinleştir çapayı (`InitialCollectFrom`) ve imleci olduğu gibi bırakıyordu. Yerel verisi silinmiş (en az 30 gün durmuş) bağlantıda Etkinleştir ve Doğrula'dan sonraki ilk artımlı çekim imleçten sonraki 31 günü bir kerede çeker; hepsi durdurulmuş dönemin hareketleridir ve silinmiş verinin yerine geçer. Karar 20'deki "(yeniden) bağlantıda en çok iki gün geriye" ilkesine aykırı. Plan varsayılanı: `BankDataPurgedAt` doluyken açılışta çapa bugün (TR) − `ObifinPollJob.InitialGraceDays`'e çekilir (yalnız ileri; `ObifinConnectionService.ReanchorAfterLocalPurge`, `ClearServiceEnd`'den önce). İmleç kalır; çekim penceresi ve kayıt koruması çapayı zaten uygular. Yerel verisi silinmemiş kısa yönetici durdurmasında çapa değişmez, aradaki havaleler gelir. Aynı kural Görev 14'teki lisans yenilenmesine de uygulanır. Alternatif: çapaya dokunmamak (durdurulmuş dönemin son 31 günü de gelir); kod `ResumeAsync`'teki tek çağrı (ve Görev 14'te `ReviveRenewedLicensesAsync`'teki). Metin işinde [S5] cümlesi yeniden açılışı da kapsayacak biçimde yazılabilir: "bağlantı kurulduğunda ya da hizmet sonu silmesinden sonra yeniden açıldığında, o günden en çok iki gün öncesine kadar uzanan hareketler alınır". Karar **Burak** / metin **avukat**.

---

## Genel kurallar

- **Worktree / dal:** `C:/Users/burak/source/repos/LiveDeck/.claude/worktrees/kvkk-banka-kod`, dal `fix/kvkk-banka-veri` (origin/master `a2a011fb`'den). Ana checkout'a (`docs/coklu-bilgisayar-senkron`, yerel değişiklikli `web/tsconfig.json`) ve başka worktree'lere (özellikle `ortak-bakiye-s1`) dokunma. Bütün komutlar worktree kökünden.
- **PR:** Push ve PR yalnız Burak isterse. Görev 8 bitince Burak'a sor: P0 (Görev 1–8) ayrı PR olarak öne alınacaksa, PR-A merge edildikten **sonra** P1 için `origin/master`'dan yeni dal açılır (`fix/kvkk-banka-veri-p1`); yığılmış PR açılmaz (master'a girmeme tuzağı). Merge, amend, rebase, çıplak `git stash` YOK. Merge yalnız Burak'ta; yayın penceresinde (Paz/Pzt/Çar/Per 20:00–01:00 TR) merge yok.
- **Düzenleme:** Kaynak dosyalar yalnız Edit/Write araçlarıyla (PowerShell 5.1 BOM'suz UTF-8'i bozar). Kodda Türkçe yorum, test adları Türkçe (`Alt_cizgili_cumle`).
- **Derleme:** sunucu `TreatWarningsAsErrors` — 0 uyarı. `DOTNET_CLI_UI_LANGUAGE=en` her dotnet komutunda.
- **Filtreli test (Bash, worktree kökünden):**
  `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~<Sınıf>"`
- **Göç:** `cd OrderDeck.LicenseServer && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add <Ad> --context LicenseDbContext --output-dir Data/Migrations` (dotnet-ef 10 global). Göç + Designer + `LicenseDbContextModelSnapshot.cs` birlikte commit'lenir. Üretilen `Up` YALNIZ `AddColumn`/`CreateTable`/`CreateIndex` içermeli (genişletme); `DropColumn`, `AlterColumn` (nullable → not null), `RenameColumn` görürsen dur ve sor. `ortak-bakiye-s1` dalı da göç ekliyor: hangisi ikinci merge edilirse onun göçü yeniden üretilir (snapshot çakışması) — bu planın işi değil, PR açıklamasına not.
- **Testcontainers:** önce `docker version`. "NamedPipeClientStream … ConnectInternal" hatasında koşuyu PowerShell'den yap: `$env:DOCKER_HOST='npipe://./pipe/dockerDesktopLinuxEngine'` (İKİ eğik çizgi), ardından aynı `dotnet test`. Başka bir oturum da Docker kullanıyor: `StartContainerAsync`/`TaskCanceledException` zaman aşımları koddan değildir, o sınıfı tek başına yeniden koştur.
- **Göçün gerçek SQL Server'da kurulduğunun kanıtı:** göç ekleyen her görevin sonunda bir Testcontainers sınıfı koşulur (`RelationalApiFactory` göçleri `Migrate()` ile kurar; model ile snapshot ayrışırsa EF 10 açılışta düşer). Görev 8'den önce `GroupCustomersRelationalTests`, sonra `BankDataCleanupRelationalTests`.
- **Public repo hijyeni:** testte kimlik bilgisi/parola/anahtar literali yok (`$"{önek}-{Guid.NewGuid():N}"`), nötr değişken adları, telefon `TestPhone`, TCKN `TestTckn.NewValid()`, IBAN `BankHasherTests.TestIban()` (Görev 4'ten sonra mod-97 geçerli üretilir; bozuk kontrol hanesi `WrongCheckDigits`). Gerçek IBAN/TCKN/müşteri verisi yok. `OneDrive/Masaüstü/obifin-api-key.txt` ve `/root/.obifin.env` ASLA okunmaz.
- **Commit:** emir kipi, `type(scope): özet`, Türkçe; gövdenin son satırı:
  `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`
- **Prod'a bağlanma, prod veri yazımı, VPS işlemi YOK** — Burak yapar (aşağıdaki "Prod adımları").

## Dosya haritası

| Görev | Dosyalar |
|---|---|
| 1 | `Domain/Bank/ObifinConnection.cs`; `Services/Bank/ObifinConnectionService.cs`; `Services/Bank/ObifinPollJob.cs`; `Services/Bank/ObifinAccountRefreshJob.cs`; göç `*_ObifinConnectionDisabledAt`; test `Services/Bank/ObifinPollJobTests.cs`, `Services/Bank/ObifinAccountRefreshJobTests.cs` |
| 2 | `Services/Bank/ObifinPollJob.cs`, `ObifinClient.cs` (işaretli tutar); testler `ObifinPollJobTests.cs`, `ObifinClientTests.cs` |
| 3 | `Services/Bank/IObifinClient.cs`, `ObifinClient.cs`, `ObifinPollJob.cs`, `BankHasher.cs`; `Domain/Bank/BankTransaction.cs`; testler `BankHasherTests.cs`, `ObifinClientTests.cs`, `ObifinPollJobTests.cs` |
| 4 | YENİ `Services/Bank/BankDescriptionMasker.cs`; `BankHasher.cs` (`HasValidIbanChecksum`); `ObifinPollJob.cs`; YENİ test `Services/Bank/BankDescriptionMaskerTests.cs`; `BankHasherTests.cs` (mod-97 geçerli test IBAN'ı); `ObifinPollJobTests.cs` |
| 5 | `Services/Bank/BankRawJsonRedactor.cs`, `BankOptions.cs`; `appsettings.json`; testler `BankRawJsonRedactorTests.cs` (yeniden), `ObifinPollJobTests.cs`, `BankDataRetentionJobTests.cs` |
| 6 | `Services/Bank/BankHasher.cs`; test `BankHasherTests.cs` |
| 7 | `Domain/Bank/ObifinConnection.cs`; `ObifinPollJob.cs`; `ObifinConnectionService.cs`; `Pages/Admin/Obifin/Index.cshtml(.cs)`; göç `*_ObifinInitialCollectFrom`; test `ObifinPollJobTests.cs` |
| 8 | YENİ `Services/Bank/BankDataCleanupJob.cs` (kısa maske adımı ve imha kaydı kancası dahil), `Tools/BankDataCleanup.cs`; `Program.cs`; YENİ testler `Services/Bank/BankDataCleanupJobTests.cs`, `Tools/BankDataCleanupTests.cs`, `Tools/BankDataCleanupRelationalTests.cs` |
| 9 | YENİ `Domain/DataDestructionRecord.cs`, `Services/Privacy/DataDestruction.cs`, `Services/Privacy/DataDestructionDigestJob.cs`, `Services/Observability/AdminJobAlerts.cs`; `Data/LicenseDbContext.cs`; `Services/Audit/AuditEvents.cs`, `AuditRetentionJobs.cs`; `Services/Bank/BankDataRetentionJob.cs`, `BankDataCleanupJob.cs`, `ObifinConnectionService.cs` (`ResetShadowData`, `actor`); `Pages/Admin/Obifin/Index.cshtml.cs`; `Controllers/Customers/AdminCustomersController.cs`; `Tools/BankDataCleanup.cs`; `Program.cs`; göç `*_DataDestructionRecords`; testler `AuditRetentionJobsTests.cs`, `AuditRetentionRelationalTests.cs`, `BankDataRetentionJobTests.cs`, `BankDataCleanupJobTests.cs`, `ObifinPollJobTests.cs`, `AdminObifinPageTests.cs`, `GdprFlowTests.cs`, `CustomerPurgeRelationalTests.cs`, `BankJobsDiTests.cs`, YENİ `Services/Privacy/DataDestructionDigestJobTests.cs`, `Services/Observability/AdminJobAlertsTests.cs` |
| 10 | `Services/Bank/BankDataRetentionJob.cs` (yeniden), `BankOptions.cs`; `appsettings.json`; `Domain/Bank/CustomerIbanMemory.cs`, `PaymentMatch.cs` (`IbanConfirmedAt`); `Services/Bank/PaymentMatchReconciler.cs`; `Pages/Admin/BankaEslestirme/Index.cshtml.cs`; `BankDataCleanupJob.cs`, `Tools/BankDataCleanup.cs` (P1 öncesi bağ başlangıcı); göçler `*_IbanMemoryLastConfirmedAt`, `*_PaymentMatchIbanConfirmedAt`; testler `BankDataRetentionJobTests.cs` (yeniden), YENİ `BankDataRetentionRelationalTests.cs`, YENİ `IbanBondRewindRelationalTests.cs`, `PaymentMatchReconcilerTests.cs`, `PaymentMatchRaceTests.cs`, `AdminBankaEslestirmePageTests.cs`, `BankDataCleanupJobTests.cs`, `Tools/BankDataCleanupRelationalTests.cs` |
| 11 | `Services/Bank/BankHasher.cs`; `Domain/Bank/BankAccount.cs`, `BankTransaction.cs`; `ObifinPollJob.cs`; `PaymentMatchReconciler.cs`; `Pages/Admin/Obifin/Index.cshtml(.cs)`; `BankDataCleanupJob.cs`, `Tools/BankDataCleanup.cs`; göç `*_IbanQualityCountersAndHashValid`; testler `BankHasherTests.cs`, `ObifinPollJobTests.cs`, `PaymentMatchReconcilerTests.cs`, `BankDataCleanupJobTests.cs`, `Tools/BankDataCleanupTests.cs`, `Tools/BankDataCleanupRelationalTests.cs`, `AdminObifinPageTests.cs` (uygulama notu) |
| 12 | `Domain/Bank/BankAccount.cs`; `Data/LicenseDbContext.cs`; `ObifinPollJob.cs`; `Pages/Admin/Obifin/Index.cshtml(.cs)` (onaylayan = yönetici kimliği); `AuditEvents.cs`; göç `*_BankAccountApproval`; testler `ObifinPollJobTests.cs` (sayaç kalıcılığı dahil), `Pages/Admin/AdminObifinPageTests.cs` |
| 13 | YENİ `Services/Bank/BankConnectionTerminator.cs` (partili yerel silme, Obifin teyidi, hesap reddi), YENİ `Domain/Bank/ObifinCopyDeletion.cs`; `Domain/Bank/ObifinConnection.cs`; `Data/LicenseDbContext.cs`; `Services/Privacy/DataDestruction.cs` (iki tetikleyici); `ObifinConnectionService.cs` (statik kimlik çözümü, `IsLicenseActiveAsync`, `ClearServiceEnd`, `MatchingActive`, lisans denetimi); `PaymentMatchReconcileJob.cs`, `BankMatchSweepJob.cs`, `PaymentMatchReconciler.cs` (eşleştirme kapısı); `Pages/Admin/Obifin/Index.cshtml(.cs)`; `AuditEvents.cs`; `Controllers/Customers/AdminCustomersController.cs` (purge reddi); `Program.cs`; göçler `*_ObifinConnectionServiceEnd`, `*_ObifinCopyDeletionManualAction`; YENİ testler `Services/Bank/BankConnectionTerminatorTests.cs`, `Services/Bank/BankConnectionTerminatorRelationalTests.cs`; `PaymentMatchReconcileJobTests.cs`, `BankMatchSweepJobTests.cs`, `PaymentMatchReconcilerTests.cs`, `AdminObifinPageTests.cs`, `Controllers/Compliance/GdprFlowTests.cs` |
| 14 | YENİ `Services/Bank/BankServiceEndPurgeJob.cs` (hizmet sonu silmesi + Obifin takibi hatırlatması); `ObifinConnectionService.cs` (`ReviveRenewedLicensesAsync`); `Program.cs`; YENİ test `Services/Bank/BankServiceEndPurgeJobTests.cs`; `BankJobsDiTests.cs` |
| 15 | YENİ `Services/Bank/BankSubjectScrub.cs`; `Services/Shoppers/ShopperPurgeService.cs`, `OrphanedMediaCleanupJob.cs` (geciken dekont silmesinin imha kaydı); `Services/Bank/PaymentMatchReconciler.cs` (karar kilidi `DecidedAt`); `Pages/Admin/Shoppers/DeletionRequests.cshtml(.cs)` (`pageNumber`, talep kimliği); testler `ShopperPurgeServiceTests.cs`, `ShopperPurgeServiceConcurrencyTests.cs`, `OrphanedMediaCleanupJobTests.cs`, `PaymentMatchReconcilerTests.cs`, `PaymentMatchRaceTests.cs`, YENİ `Pages/Admin/AdminDeletionRequestsPageTests.cs` |
| 16 | `Pages/Admin/BankaEslestirme/Index.cshtml.cs`; `Pages/Admin/Obifin/Index.cshtml.cs`; `AuditEvents.cs`; `BankDataCleanupJob.cs`, `Tools/BankDataCleanup.cs` (eski denetim satırları); testler `AdminBankaEslestirmePageTests.cs`, `AdminObifinPageTests.cs`, `BankDataCleanupJobTests.cs` |
| 17 | YENİ `Domain/Bank/BankSubjectRequest.cs`, `Services/Bank/BankSubjectRequestService.cs`; `LicenseDbContext.cs`; `BankConnectionTerminator.cs`, `Controllers/Customers/AdminCustomersController.cs` (açık talepleri kapat); `Program.cs`; göç `*_BankSubjectRequests`; YENİ test `Services/Bank/BankSubjectRequestServiceTests.cs` (sonuç kategorileri, tür denetimi, CSV kaçışı); `BankConnectionTerminatorTests.cs`, `GdprFlowTests.cs`; uygulama notu: `BankSubjectScrub.cs` (ortak imha hedefleri, kopya izleme), `ShopperPurgeService.cs`, YENİ `Services/Bank/BankSubjectRequestRelationalTests.cs`, `BankJobsDiTests.cs` |
| 18 | YENİ `Pages/Admin/BankaVeriTalepleri/Index.cshtml(.cs)`; `Pages/Shared/_AdminLayout.cshtml`; `Pages/Admin/Shoppers/DeletionRequests.cshtml(.cs)`; `AuditEvents.cs`; YENİ test `Pages/Admin/AdminBankaVeriTalepleriPageTests.cs`; `Pages/Admin/AdminDeletionRequestsPageTests.cs` (Görev 15'te açıldı, ekleme) |
| 19 | `Controllers/Panel/PanelPaymentsController.cs`; test `Services/Push/ShopperOrderPaymentPushTests.cs` |
| 20 | `Services/Audit/SecurityDataRetentionJob.cs` (telefon + banka talebi maskesi; her hedefin imha kaydı ve koşu kaydı); `Program.cs` (`JobId`); `Pages/Admin/Shoppers/DeletionRequests.cshtml`; testler `Services/Audit/SecurityDataRetentionJobTests.cs`, YENİ `Services/Audit/SecurityDataRetentionRelationalTests.cs` |
| 21 | YENİ `Domain/DataControllerProfile.cs`, `Domain/DataControllerProfileVersion.cs`, `Services/Privacy/DataControllerProfiles.cs`, `Controllers/Panel/PanelDataControllerController.cs`; `Domain/IntakeFormSubmission.cs` (`DataControllerVersion`); `LicenseDbContext.cs`; `Services/Bank/ObifinConnectionService.cs`, `ObifinOptions.cs`, `appsettings.json` (veri sorumlusu kapısı); `Pages/Admin/Customers/Detail.cshtml(.cs)`; `Controllers/Customers/AdminCustomersController.cs`; `AuditEvents.cs`; göç `*_DataControllerProfiles`; YENİ `OrderDeck.LicenseServer.Tests/TestHelpers/TestDataController.cs`; YENİ testler `Services/Privacy/DataControllerProfilesTests.cs`, `Controllers/Panel/PanelDataControllerControllerTests.cs`, `Pages/Admin/AdminCustomerDataControllerTests.cs`, `Services/Bank/ObifinDataControllerGateTests.cs`; banka bağlantısı kuran testlerin tohum yardımcıları (`ObifinConnectionServiceTests`, `ObifinConnectionServiceConcurrencyTests`, `ObifinPollJobTests`, `ObifinAccountRefreshJobTests`, `AdminObifinPageTests`, `BankConnectionTerminatorTests`); `GdprFlowTests.cs` |
| 22 | `Services/IntakeForm/IntakeFormService.cs` (gösterilen sürüm); `Pages/Public/IntakeForm.cshtml(.cs)`; `Controllers/Shopper/ShopperBroadcastersController.cs`; testler `Pages/Public/IntakeFormDataControllerTests.cs` (YENİ), `Controllers/Shopper/ShopperBroadcastersCodeLookupTests.cs`, YENİ `Controllers/Shopper/ShopperDataControllerTests.cs` |
| 23 | `deploy/scripts/backup-sql-to-r2.sh`, `deploy/scripts/backup-env-to-r2.sh`, `deploy/README.md` |
| 24 | `web/app/(en)/en/privacy-policy/page.tsx`; YENİ test `OrderDeck.LicenseServer.Tests/Pages/Public/PolicyEffectiveDateParityTests.cs` |
| 25 | kod yok — tam sunucu paketi (ayrık) |

(Yollar `OrderDeck.LicenseServer/` ve `OrderDeck.LicenseServer.Tests/` altında; tabloda kök önek atlandı.)

## Prod adımları (Burak; plan bunları YAPMAZ)

**P0 deploy'undan ÖNCE (Görev 1):** Görev 1 lisansı biten bağlantıyı ilk çekim koşusunda (5 dakika içinde) `Disabled`'a çeker; bağlantıyı lisans yenilenince geri açan yol (`ReviveRenewedLicensesAsync`) Görev 14'te, yani P1'de. Lisans uzatma (`AdminLicensesController.Extend`, Licenses/Detail → Uzat) yalnız `ExpiresAt`'i ilerletir, bağlantıya dokunmaz; aynı kimlikle Kaydet `Disabled`'ı korur, kimlik değişimi ise gölge veriyi siler. P0 ile P1 arasında bitip uzatılan lisansta banka çekimi ve otomatik eşleştirme **sessizce durur** (admin/obifin'de yalnız `Disabled` ve "Lisans sona erdi…" görünür).
- a) Hangi bağlantıların duracağına bak; EMAR lisansının P1 deploy'undan önce bitmeyeceğini doğrula (bitecekse lisansı P0'dan önce uzat ya da P0 ile P1'i aynı pencerede çıkar):
  ```sql
  SELECT c.LicenseId, c.Status, l.ExpiresAt, l.RevokedAt
  FROM ObifinConnections c JOIN Licenses l ON l.Id = c.LicenseId;
  ```
- b) Elle durdurulmuş eski satır var mı: `SELECT COUNT(*) FROM ObifinConnections WHERE Status = 'Disabled';` — **0 beklenir** (kod Görev 1'e kadar `Disabled` hiç yazmadı). 0 değilse o satırların `DisabledAt`'i `NULL` kalır: Görev 1 zaten Disabled olanı atlar, tarih yazmaz. Görev 14'ün ilk koşusu onları bugünle damgalar ve yönetici durdurması sayar (aşağıda "P1 deploy'undan ÖNCE"). Göçe veri `UPDATE`'i eklenmez (genişletme kuralı).

**P0 ile P1 arasında (P0 ayrı çıktıysa):** Bir lisans uzatılır ya da iptali geri alınırsa bağlantısı P1'e kadar kendiliğinden açılmaz. Durma 30 günden kısaysa arada gelen hareketler kaybolmaz: çekim son 31 günü imleçten sorar.
- Elle kurtarma, **lisans uzatıldıktan SONRA** (önce yapılırsa sonraki çekim koşusu bağlantıyı yeniden durdurur):
  ```sql
  UPDATE ObifinConnections
  SET Status = 'Unverified', DisabledAt = NULL, LastError = NULL, UpdatedAt = SYSDATETIMEOFFSET()
  WHERE LicenseId = '<lisans>' AND Status = 'Disabled';
  ```
  Sonra admin/obifin → **Doğrula**. Kimliği değiştirerek Kaydet'e basılmaz (gölge veri silinir).
- **P1'e kadar Obifin'e yeni banka ya da hesap ekleme.** İlk çekim çapası (Görev 7) banka hesabı başına değil, Obifin bağlantısı başına tutulur. Obifin'e sonradan eklenen bir bankanın geçmişi yeni (büyük) Id'lerle gelebilir. Çapa daha eski olduğu için o hesabın son 30 günlük hareketleri yazılır. Bunu P1'deki onaysız hesap süzgeci (Görev 12; fail-closed, imleç ilerler) kapatır.
- Alternatif: P0 ile P1 aynı pencerede çıkar; o zaman bu boşluklar hiç açılmaz.

**P0 deploy'undan sonra (Görev 1–8):**
- **Ön denetim (deploy'dan en az 5 dakika sonra, kuru koşudan önce; 0 satır beklenir):** çapası da ilk çekimi de olmayan ama hareketi olan bağlantı.
  ```sql
  SELECT c.LicenseId, c.Status, COUNT(t.Id) AS Hareket
  FROM ObifinConnections c JOIN BankTransactions t ON t.LicenseId = c.LicenseId
  WHERE c.InitialCollectFrom IS NULL AND c.BackfillCompletedAt IS NULL
  GROUP BY c.LicenseId, c.Status;
  ```
  Eski kodun 90 günlük ilk çekimi P0 anında yarıda kalmış olabilir. Bağlantı `Verified` ise çekim işi deploy'dan sonraki ilk turda ona çapa yazar, komut da çapadan eski kararsız satırlarını siler (Görev 8): bu sorguya düşmez. Düşen bağlantı `Verified` değildir (Failed/Unverified/Disabled); komut onun çapasını bilemez, atlar. 0 değilse: bağlantı doğrulanıp ilk turu koşunca (çapa yazılır) komutu yeniden `--apply` ile koştur (idempotent). Doğrulanmayacaksa satırlarını 180 günlük kural (Görev 10) ya da hizmet sonu silmesi (Görev 14) siler; sonucu tutanağa yaz.
1. Kuru koşu, çıktıyı tutanağa kaydet: `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll bank-data-cleanup`
2. Sayılar beklendiği gibiyse: `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll bank-data-cleanup --apply` (çıkış 1 = bazı partiler eşzamanlı yazıyla geri alındı → aynı komutu yeniden çalıştır; idempotent). Çıktıyı tutanağa ekle. P0'da `DataDestructionRecords` yok: bu koşunun Silme Yönetmeliği md. 7/3 kaydı (en az 3 yıl) **tutanağın kendisidir**. P1'den sonraki koşular tabloya da yazar (Görev 9: tetikleyici `cleanup`, yapan `cli`).
   **Ne zaman koşturulmaz:** 04:57 UTC saklama işi (P1) ya da bir shopper silmesi / banka veri talebi silmesi sürerken. Komut oku-değiştir-yaz yapar, `BankTransaction`'da eşzamanlılık jetonu yok: arada boşaltılan bir açıklama ya da ham kayıt maskelenmiş eski hâliyle geri yazılabilirdi. Gündüz, başka silme işlemi yapmadan koştur.
   **İlk saklama koşusu da tutanağa girer:** Görev 5 ham kayıt süresini 90 günden 30 güne indirdiği için deploy'dan sonraki ilk 04:57 UTC (07:57 TR) `bank-data-retention` koşusu, `FetchedAt`'i 30–90 gün önce olan bütün satırların ham kaydını **bir kerede** boşaltır. Silme Yönetmeliği md. 7/3 (Ö-13) her imhanın kaydını ister ve P0'da `DataDestructionRecords` yok: sayım bu koşudan ÖNCE alınır (deploy'dan önce ya da sonra; deploy 01:00–07:57 TR arasındaysa ilk koşu aynı sabahtır), koşudan sonra boşaltılanın 0 kaldığı yeniden sayılır. Kalıbın "İlk saklama koşusu" satırları bunun için. **P0 ile P1 aynı pencerede çıkıyorsa** bu bölüm uygulanmaz: ilk koşu P1 kuralıyla (Görev 10: işlem tarihinden 30 gün, çekimden en az 7 gün) boşaltır ve aşağıdaki `FetchedAt` sorgusu onu saymaz. Sayım "P1 ilk koşu tutanağı"ndaki ham kayıt sorgusuyla alınır.
   **Tutanak kalıbı** (repo DIŞINDA, KVKK klasöründe; 3 yıl saklanır):
   ```text
   bank-data-cleanup tutanağı — KVKK ön inceleme §5.1 (P0)
   Tarih/saat (TR): ____          Koşturan: ____          İmaj: master-<sha>
   Ön denetim (çapasız yarım ilk çekim; 0 satır beklenir):  ____
   Komut ve çıkış kodu (kuru):    bank-data-cleanup  → __
   Kuru koşu çıktısı (satır aynen):  ____
   Komut ve çıkış kodu (uygula):  bank-data-cleanup --apply  → __   Yeniden koşuldu mu: __
   Uygulama çıktısı (satır aynen):  ____
   Not: çıktı kişisel veri içermez (yalnız sayılar ve lisans kimliği). Yedek: aynı gece 03:00 UTC yedeği
   bu silmelerden ÖNCE alınmışsa, geri yüklemede komut yeniden --apply ile koşturulur (deploy/README.md, Geri yükleme).

   İlk saklama koşusu (ham kayıt 90 → 30 gün; deploy'dan sonraki ilk 04:57 UTC koşusu, bir kerelik toplu boşaltma)
   Koşudan ÖNCE sayım — anı (TR): ____   Sonuç: ____
     SELECT COUNT(*) FROM BankTransactions
     WHERE RawJson IS NOT NULL AND FetchedAt < DATEADD(day, -30, SYSDATETIMEOFFSET());
   Koşunun günü (UTC): ____   Günlük satırı (aynen; docker compose logs license-server | grep "Banka veri saklama"):  ____
     (Sayım ile satırdaki "ham JSON" sayısı arasındaki fark, sayımdan koşuya kadar 30 günü dolan satırlardır.)
   Koşudan SONRA sayım — anı (TR): ____   Sonuç (0 beklenir): ____
     SELECT COUNT(*) FROM BankTransactions
     WHERE RawJson IS NOT NULL AND FetchedAt < DATEADD(day, -30, CAST('<koşu günü>T04:57:00+00:00' AS datetimeoffset));
   Bilerek kabul: P0 ile P1 arasındaki sonraki günlük saklama koşuları (ham kayıt 30 gün, açıklama 180 gün) eskiden
   olduğu gibi yalnız uygulama günlüğünde kalır; imha kaydı tablosu (DataDestructionRecords) P1'de (Görev 9) gelir.
   P1 deploy tarihi: ____   Kabul eden: ____
   ```

**P1 deploy'undan ÖNCE:**
- admin/obifin'de durumu `Disabled` olan bağlantılara bak (P0'dan beri lisans bitişiyle durdurulanlar). `DisabledAt`'i 30 günü geçmiş olanların banka verisi P1'in ilk 05:17 UTC koşusunda **yalnız yerelde** silinir (Obifin'e gidilmez; Görev 14). Lisansı uzatılacak yayıncı varsa önce lisansı uzat: iş, lisansı yeniden geçerli bağlantıyı silmez, `Unverified`'a çeker ve e-postayla "Doğrula" der.
- P0 öncesi kontrolde (b) `Disabled` sayısı 0 değildiyse: o tarihsiz satırlar ilk koşuda bugünle damgalanır ve yönetici durdurması sayılır (`DisabledByAdmin`; lisans geçerli olsa da kendiliğinden açılmaz, 30. günde yerel verisi silinir). Açılması gereken varsa P1'den sonra admin/obifin → Etkinleştir (Görev 13).
- Aynı Obifin kullanıcısı (BaseUrl + kullanıcı kodu) birden çok lisansta kayıtlı mı bak (Faz 2 spec §13'teki paylaşılan kullanıcı). Kayıtlıysa o lisanslardan birinde "Kaldır ve sil" Obifin'e hiç gitmez; Obifin tarafı Obifin panelinden **yalnız o lisansın** bağlantısı için elle kapatılır (Görev 13).
- **Merge'den ÖNCE** prod `.env`'de `Admin__AlertEmail` dolu mu bak (`deploy/HA-PLAYBOOK.md`: disk uyarısı da msmtp/Brevo ile buraya gidiyor). Saklama, hizmet sonu ve denetim budaması işlerinin uyarı adresi bu; boşsa `data-destruction-digest` deploy'dan sonraki ilk :13'ten başlayarak **her saat Failed** olur — tasarım gereği: dış kopya gönderilmeden kayıt işaretlenmez, kayıtlar tabloda bekler (Görev 9 incelemesi).
- **Veri sorumlusu kapısı (karar 23, Görev 21):** P1'den sonra lisansın müşterisinde veri sorumlusu bilgisi yoksa ilk bağlantı kaydı, Doğrula ve Banka ekle reddedilir. EMAR'ın Verified bağlantısının çekimi sürer. Ama metin işi (form paragrafı v2) P1'den önce yayımlanmayacaksa ve P1 ile metin arasında bir Doğrula gerekebilirse (lisans yenilenmesi, Etkinleştir, kimlik değişimi), **merge'den önce** `.env`'e `Obifin__RequireDataControllerProfile=false` yaz. Metin yayımlanıp EMAR'ın bilgisi girilince satırı sil ve yeniden başlat (Prod adımları 8). `.env` yazımını Burak yapar.
- **İlk koşuların beklenen sayıları (P0'daki ilk saklama koşusunun kalıbı; aşağıdaki "P1 ilk koşu tutanağı"):** P1 üç günlük işin ilk koşusunu değiştirir ve hiçbirinin kuru koşusu yok:
  - `security-data-retention` 03:40 UTC: işlenmiş silme talebinin telefonu (Görev 20).
  - `bank-data-retention` 04:57 UTC: eşleşmeyen açıklama 180 → 30 gün; hareket, eşleşme ve gap 180 gün işlem tarihinden; ham kayıt çekimden 30 gün → işlem tarihinden 30 gün; gerekçe metni eşleştirme satırının oluşturulmasından 180 gün → hareketin açıklamasıyla (eşleşmeyende işlem tarihinden 30 gün: ilk koşu 30–180 günlük bütün eşleşmeyen gerekçe metinlerini bir kerede boşaltır); IBAN bağı 12 ay (Görev 10).
  - `bank-service-end-purge` 05:17 UTC: 30 günü geçmiş Disabled bağlantının yerel verisi (Görev 14).

  İlk koşu 30–180 günlük geçmişi bir kerede siler. Sayımlar deploy'dan ÖNCE alınır (deploy 01:00–06:40 TR arasındaysa ilk koşu aynı sabahtır). Koşudan sonra işin `DataDestructionRecords` satırlarıyla karşılaştırılır, koşul yeniden sayılır (0 beklenir). P1'de imha kaydı tablosu var, ama beklenen sayı tutanakta olmazsa "fazla mı silindi" sorusu cevapsız kalır.
- **Deploy geri alınırsa** (`/ready` 60 sn'de gelmez, imaj geri alınır, göç kalır): Hangfire panosu → Recurring jobs'ta eski imajın tanımadığı P1 işlerini (`data-destruction-digest`, Görev 14'ten sonra `bank-service-end-purge`) sil. Kalırlarsa eski imaj tipi yükleyemez ve her tetiklemede günlüğe iş yükleme hatası düşer (zararsız ama gürültülü). Yeni imaj yeniden çıkınca işler açılışta yeniden kaydedilir.

**P1 deploy'undan sonra (Görev 9–25):**
3. **Hemen** admin/obifin → hesaplar → EMAR hesaplarını "Onayla" (Görev 12; onaylanmayan hesabın hareketi kaydedilmez ve sonra gelmez). Onay ayrıca "Kaldır ve sil"in hangi Obifin bağlantısının bu lisansa ait olduğunu bilmesini sağlar; hizmet sonu yerel silmesinden sonra da (onaylı hesap satırı IBAN'sız kalır, Görev 13). **Önce onay, sonra ret:** EMAR hesapları onaylandıktan SONRA, not §5.9 kontrolünde EMAR dışı (şahsi ya da başka şirketin) bir hesap çıktıysa o hesabın satırında hesap numarasını yazıp "Reddet ve verisini sil"e bas (Görev 13). Bu, o hesaptan P1'den önce yazılmış hareketleri, eşleşmelerini ve bu hareketlerden öğrenilmiş IBAN bağlarını siler ve imha kaydı yazar. Kaç hareket silineceğini önce say, sonucu tutanağa yaz:
   ```sql
   SELECT a.LicenseId, a.ObifinAccountId, a.BankaKodu, a.ApprovedAt, COUNT(t.Id) AS Hareket
   FROM BankAccounts a LEFT JOIN BankTransactions t ON t.LicenseId = a.LicenseId AND t.ObifinAccountId = a.ObifinAccountId
   GROUP BY a.LicenseId, a.ObifinAccountId, a.BankaKodu, a.ApprovedAt;
   ```
4. `Admin__AlertEmail`'in dolu olduğu merge'den önce doğrulandı (yukarıda); deploy'dan sonraki ilk :13 koşusunun Hangfire'da Succeeded olduğuna bak (9. madde).
5. Karar 5'e ve Ö-14'e göre: `bank-data-cleanup` (kuru) → maskeli kaynaklı özet, kısa maske ve denetimde kullanıcı adı sayısı; ardından `--apply` **zorunlu**, `--license` vermeden (lisanssız) koşturulur. Ö-14'ün mevcut satırları (Görev 16'dan önce yazılmış elle eşleme denetim satırlarındaki müşteri kullanıcı adı, 24 ay durur) yalnız P1'den sonraki bir `--apply` ile kayıt numarasına çevrilir ya da boşaltılır. Hareketi 180 günde silinmiş satırları yalnız lisanssız koşu işler. Koşmazsa kullanıcı adı denetimde kalır, KVKK silmesi de ona dokunmaz (silme yalnız yeni biçimi boşaltır). Çıktıdaki `denetimde kullanıcı adı çevrilen=` tutanağa girer (kuru koşu da aynı sayıyı verir). Faz 2'den önce `--reset-iban-memory` (önce kuru, sonra `--apply`). Sıfırlama, özeti aynı lisansta geçerli IBAN'lı bir harekette (`CounterpartyIbanHashValid`, P1'den sonra çekilmiş) görünen bağı tutar: P1'den sonra öğrenilmiş bağlar kalır, komut ne zaman koşulursa koşulsun güvenlidir ve idempotenttir (Görev 11 incelemesi). Çıktıdaki `sıfırlanan IBAN hafızası=` ve `geçerli IBAN'lı kalan=` tutanağa girer. Geç koşmanın tek bedeli: kanıt hareketle 180 gün yaşar, o IBAN'dan son 180 günde geçerli havale gelmemiş bağ da gider (fazlası silinir). P1'den sonraki ilk `--apply` P1 öncesi IBAN bağlarının saklama başlangıcını öğreten havalenin işlem tarihine de yazar (Görev 10; `IBAN bağı başlangıcı yazılan=`, kuru koşu sayar): öğreten havaleler 180 günlük kurala düşmeden, yani P1'den sonraki ilk haftalarda koştur; sayımı tutanağa yaz. Koşmazsa o bağların süresi öğrenme anından işler (politikadaki "son doğrulanmış ödemeden 12 ay"ı ~90 güne kadar aşabilir). `--reset-iban-memory` aynı komutta koşturulursa P1 öncesi bağların çoğu silinir; başlangıç adımı yalnız kalanlara (P1'den sonra aynı IBAN'dan geçerli havale gelmiş olanlara) yazar. Sınır: sıfırlama yalnız `CustomerIbanMemories`'i siler; hareketlerdeki özetler 180 güne kadar kalır (IBAN ile erişim/silme talebi onlarla bulunur, Görev 17) ve P1 öncesinde çekilmiş hareketlerden hafıza yeniden öğrenilmez (`CounterpartyIbanHashValid`). Tutanak kalıbı P0'dakiyle aynı; P1'den sonra komut `DataDestructionRecords`'a da yazar. Saklama işi ya da bir silme sürerken koşturma (1. madde).
6. Yedek betikleri CI ile **kopyalanmaz** (CI yalnız `rollback-license-server.sh`'ı kopyalıyor): HashKey'in Bitwarden'da olduğunu doğruladıktan sonra `deploy/scripts/backup-sql-to-r2.sh` ve `backup-env-to-r2.sh`'ı `/opt/orderdeck/scripts/`'e kopyala, birer kez elle çalıştırıp günlüğe bak (Görev 23).
7. R2'de `sql-bak/` için yaşam döngüsü kuralı (32 gün) ve tarihsiz eski nesne kontrolü (not Ö-12).
8. Yayıncıların veri sorumlusu bilgileri (Görev 21–22) ancak metin işinin (form paragrafı v2 ve yeni `KvkkNotice.TextVersion`) yayımlanmasından SONRA girilir: form bloğu metin sürümünün özetinin dışında, bilgi girilene kadar çizilmez. Bilgi girilene kadar banka modülü yeni bağlantı, Doğrula ve Banka ekle için kapalıdır (karar 23). EMAR'ın bilgisi girilince, P1 öncesinde `.env`'e geçici `Obifin__RequireDataControllerProfile=false` yazıldıysa o satır silinir ve konteyner yeniden başlatılır; admin/obifin'de bir Doğrula ile kapının açık olduğu görülür. Her bilgi değişikliği yeni bir sürüm yazar; form gönderimi gösterilen sürümü kaydeder (karar 24).
9. İlk saat başı +13'ten sonra `Admin__AlertEmail` kutusuna "[OrderDeck] İmha kaydı özeti" düştüğünü gör (Görev 9; ilk özet büyük olasılıkla 03:30 `audit-retention` koşusundan sonraki saatte — her başarılı `audit-retention` ve `bank-data-retention` koşusu silecek satır olmasa da bir `run-completed` satırı bırakır, yani özet her gün gelir). Bu e-postalar **silinmez**: yedekten geri yüklemeden sonra silmeler bunlardan yeniden uygulanır. Posta kutusunda bunlar için bir etiket/klasör ve "silme" kuralı dışı tutma ayarla. Haftalık "son başarılı çalışma" kontrolü (iç imha prosedürü §4.3): son günlerin özetlerinde `bank-data-retention`, `audit-retention`, `security-data-retention` (Görev 20'den sonra) ve `bank-service-end-purge` (Görev 14'ten sonra) `run-completed` satırı var mı; yoksa Hangfire panosuna bak. Hangfire'da `data-destruction-digest` Failed ise e-posta gitmiyor demektir: kayıtlar tabloda bekler, SMTP/adres düzelince sonraki koşu (ya da "Trigger now") hepsini gönderir.
10. **Obifin tarafı (karar 21, her "Kaldır ve sil"den ve her hizmet sonu silmesinden sonra):** hizmet sonunda yerel verisi silinen bağlantı için admin/obifin'de "Kaldır ve sil". O ana kadar onaylı hesap satırları IBAN'sız kalır: "Kaldır ve sil" Obifin panelinden eklenmiş bağlantıyı (pilotta QNB) onlardan tanır. Arada o hesapların onayını geri alma; alınırsa bağlantı "bu lisansa ait olduğu doğrulanamadı" diye Obifin'de kalır ve Obifin panelinden elle kapatılır. Sonuç bildiriminde "doğrulanamadı" satırı çıkarsa her birini Obifin panelinden kontrol et, bu lisansınsa elle kaldır, tutanağa yaz. Her "Kaldır ve sil"den sonra Obifin'e yazılı silme talebi gönder: hareket kopyası silinsin, banka bağlantısı kapatılsın. Teyit yazısı gelince admin/obifin → "Obifin silme teyidi bekleyenler" → "Teyit alındı". Teyit yazısını KVKK klasöründe 3 yıl sakla. `bank-service-end-purge` bunlar kapanana kadar her gün "[OrderDeck] Obifin tarafı bekliyor" e-postası gönderir. 14 günü geçen satır "GECİKTİ" konusuyla gelir: Obifin'i yeniden yazılı uyar, durumu tutanağa yaz.

**P1 ilk koşu tutanağı** (repo DIŞINDA, KVKK klasöründe; 3 yıl; "P1 deploy'undan ÖNCE" maddesine bağlı):
```text
P1 ilk koşu tutanağı — KVKK ön inceleme §5.1 (P1)
Deploy (TR): ____   İmaj: master-<sha>   Sayımı alan: ____

Deploy'dan ÖNCE sayım — anı (TR): ____
  -- security-data-retention (03:40 UTC): işlenmiş silme talebinin telefonu
  SELECT COUNT(*) FROM ShopperDeletionRequests
  WHERE HandledAt IS NOT NULL AND HandledAt < DATEADD(day, -30, SYSDATETIMEOFFSET()) AND PhoneAtRequest <> '';      → ____
  -- bank-data-retention (04:57 UTC); koşullar BankDataRetentionJob'ınkilerin aynısı, adımların sırasıyla
  SELECT COUNT(*) FROM BankTransactions                                               -- 1. silinecek hareket
  WHERE OccurredAt < DATEADD(day, -180, SYSDATETIMEOFFSET()) AND FetchedAt < DATEADD(day, -7, SYSDATETIMEOFFSET());  → ____
  SELECT COUNT(*) FROM PaymentMatches m JOIN BankTransactions t ON t.Id = m.BankTransactionId   -- 1. hareketle silinecek eşleşme
  WHERE t.OccurredAt < DATEADD(day, -180, SYSDATETIMEOFFSET()) AND t.FetchedAt < DATEADD(day, -7, SYSDATETIMEOFFSET());  → ____
  SELECT COUNT(*) FROM PaymentMatchGaps WHERE CreatedAt < DATEADD(day, -180, SYSDATETIMEOFFSET());  -- 1. gap  → ____
  SELECT COUNT(*) FROM BankTransactions                                               -- 2. boşaltılacak ham kayıt (P0'daki FetchedAt ölçütü değil)
  WHERE RawJson IS NOT NULL AND OccurredAt < DATEADD(day, -30, SYSDATETIMEOFFSET())
    AND OccurredAt >= DATEADD(day, -180, SYSDATETIMEOFFSET()) AND FetchedAt < DATEADD(day, -7, SYSDATETIMEOFFSET());  → ____
  SELECT COUNT(*) FROM BankTransactions t                                             -- 3. boşaltılacak eşleşmeyen açıklama/ad
  WHERE t.DescriptionPurgedAt IS NULL AND t.OccurredAt < DATEADD(day, -30, SYSDATETIMEOFFSET())
    AND t.OccurredAt >= DATEADD(day, -180, SYSDATETIMEOFFSET()) AND t.FetchedAt < DATEADD(day, -7, SYSDATETIMEOFFSET())
    AND NOT EXISTS (SELECT 1 FROM PaymentMatches m WHERE m.BankTransactionId = t.Id
                    AND (m.ActualWpfCustomerId IS NOT NULL OR m.PaymentId IS NOT NULL));                              → ____
  SELECT COUNT(*) FROM PaymentMatches m JOIN BankTransactions t ON t.Id = m.BankTransactionId   -- 4. boşaltılacak gerekçe metni
  WHERE m.Evidence IS NOT NULL                                                        --    (eskiden satırın CreatedAt+180 günü)
    AND NOT (t.OccurredAt < DATEADD(day, -180, SYSDATETIMEOFFSET()) AND t.FetchedAt < DATEADD(day, -7, SYSDATETIMEOFFSET()))
    AND (t.DescriptionPurgedAt IS NOT NULL                                            --    açıklaması önce boşaltılmış
         OR (t.OccurredAt < DATEADD(day, -30, SYSDATETIMEOFFSET())                    --    ya da bu koşuda boşaltılacak (3.)
             AND t.FetchedAt < DATEADD(day, -7, SYSDATETIMEOFFSET())
             AND NOT EXISTS (SELECT 1 FROM PaymentMatches x WHERE x.BankTransactionId = t.Id
                             AND (x.ActualWpfCustomerId IS NOT NULL OR x.PaymentId IS NOT NULL))));            → ____
  SELECT COUNT(*) FROM CustomerIbanMemories                                           -- 5. IBAN bağı; P1 öncesi LastConfirmedAt yok
  WHERE CreatedAt < DATEADD(month, -12, SYSDATETIMEOFFSET());                                                          → ____
    -- (P1 öncesi bağın başlangıcı bank-data-cleanup'ın 5. maddedeki koşusuna kadar öğrenme anıdır; o koşu öğreten
    --  havalenin işlem tarihini yazar. Sonraki koşularda koşul COALESCE(LastConfirmedAt, CreatedAt) olur.)
  -- bank-service-end-purge (05:17 UTC): yerelde silinecek bağlantılar (lisansı yenilenmiş olan kurtulur)
  SELECT c.LicenseId, c.DisabledAt, l.ExpiresAt, l.RevokedAt,
         (SELECT COUNT(*) FROM BankTransactions t WHERE t.LicenseId = c.LicenseId) AS Hareket
  FROM ObifinConnections c JOIN Licenses l ON l.Id = c.LicenseId
  WHERE c.Status = 'Disabled' AND c.DisabledAt <= DATEADD(day, -30, SYSDATETIMEOFFSET());                         → ____

İlk koşuların günü (UTC): ____
İşlerin kayıtları (sayımla karşılaştır; fark = sayımdan koşuya kadar süresi dolanlar):
  SELECT Trigger, Target, Action, SUM(RowCount) AS Satir FROM DataDestructionRecords
  WHERE OccurredAt >= '<gün>T03:30:00+00:00' AND OccurredAt < '<gün>T05:30:00+00:00'
    AND Trigger IN ('retention', 'service-end')
  GROUP BY Trigger, Target, Action;                                                                                   → ____
Koşudan SONRA aynı sorgular (SYSDATETIMEOFFSET() yerine koşu anı) — 0 beklenir:  ____
Not: çıktı kişisel veri içermez (sayılar ve lisans kimliği). Kabul eden: ____
```

**Yedekten geri yükleme yapılırsa (her zaman):** `deploy/README.md` → "Geri yükleme" → "Silmeleri yeniden uygula" (Görev 23). Kısaca: yedek anını not et → `bank-data-retention`, `security-data-retention`, `audit-retention`'ı elle tetikle → yedek anından sonraki "İmha kaydı özeti" e-postalarındaki olay kaynaklı silmeleri tetikleyicisine göre yeniden uygula → son başarılı özetten sonraki kayıtlar (olağan durumda en çok ~1 saat 13 dk; özet işi düşmüşse daha fazla) için canlı veritabanına hâlâ erişiliyorsa `DataDestructionRecords`'u (`DigestedAt IS NULL` ya da OccurredAt > yedek anı) dışa al.

---

## PR-A — P0: kaydetme, maskeleme, ilk çekim, temizlik komutu

### Görev 1: Lisansı biten yayıncının bağlantısını durdur (P0-1)

**Neden (not K-6):** çekim ve hesap yenileme yalnız `Status == Verified`'a bakıyor; süresi dolmuş ya da iptal edilmiş lisansın bankasından her 5 dakikada yeni hareket toplanmaya devam ediyor. `Disabled` hiçbir yerde atanmıyor. Bundan sonra lisans bitince bağlantı `Disabled` olur ve `DisabledAt` yazılır; hizmet sonu silmesi (Görev 14) bu tarihten 30 gün sonra koşar.

**Files:**
- Modify: `OrderDeck.LicenseServer/Domain/Bank/ObifinConnection.cs`
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs` (sabit + statik metot, `TrToUtc`'nin hemen altına)
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs:78-108` (`RunAsync` başı, `PollConnectionAsync` başı)
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinAccountRefreshJob.cs:30-33`
- Create: göç `OrderDeck.LicenseServer/Data/Migrations/*_ObifinConnectionDisabledAt.cs` (+ Designer, snapshot)
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs`, `OrderDeck.LicenseServer.Tests/Services/Bank/ObifinAccountRefreshJobTests.cs`

- [ ] **Adım 1: Çekim testlerini yaz (kırmızı — derlenmez)**

`ObifinPollJobTests.cs` sınıfının sonuna (son `}`'dan önce) ekle:

```csharp
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lisansi_biten_baglanti_cekilmez_Disabled_ve_DisabledAt_yazilir(bool expired)
    {
        // KVKK K-6: hizmeti bitmiş yayıncının hesabından hareket toplanmaz. Lisans bitince bağlantı Disabled'a çekilir,
        // DisabledAt hizmet sonu silmesinin (30 gün) sayacıdır.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var license = await db.Licenses.SingleAsync(l => l.Id == lic);
        if (expired) license.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        else license.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 9, 0, 0), 100m));

        await job.RunAsync(CancellationToken.None);

        client.Calls.Should().BeEmpty("lisansı biten yayıncının hesabı Obifin'den sorgulanmaz");
        sink.Received.Should().BeEmpty();
        (await db.BankTransactions.CountAsync()).Should().Be(0);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.DisabledAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        conn.LastError.Should().Be(ObifinConnectionService.LicenseEndedMessage);
    }

    [Fact]
    public async Task Simdi_cek_yolu_da_lisansi_biten_baglantiyi_cekmez()
    {
        // Admin'in "Şimdi çek"i PollConnectionAsync'i doğrudan kuyruğa atar; RunAsync'in başındaki denetimi atlamaz.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        (await db.Licenses.SingleAsync(l => l.Id == lic)).RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var connId = (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).Id;

        await job.PollConnectionAsync(connId, CancellationToken.None);

        client.Calls.Should().BeEmpty();
        (await db.ObifinConnections.SingleAsync(c => c.Id == connId)).Status.Should().Be(ObifinConnectionStatus.Disabled);
    }

    [Fact]
    public async Task Zaten_Disabled_baglantinin_DisabledAt_tarihi_degismez()
    {
        // 30 günlük sayaç her koşuda yeniden başlamasın.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var first = DateTimeOffset.UtcNow.AddDays(-10);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status = ObifinConnectionStatus.Disabled; conn.DisabledAt = first;
        (await db.Licenses.SingleAsync(l => l.Id == lic)).ExpiresAt = DateTimeOffset.UtcNow.AddDays(-11);
        await db.SaveChangesAsync();

        await job.RunAsync(CancellationToken.None);

        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).DisabledAt.Should().Be(first);
    }
```

- [ ] **Adım 2: Hesap yenileme testini yaz**

`ObifinAccountRefreshJobTests.cs` sınıfının sonuna ekle (sınıfın kendi `PerUserObifin`, `SeedVerifiedAsync`, `NewDb` yardımcılarını kullanır; job'ı dosyadaki diğer testlerin kurduğu gibi kur — `new ObifinAccountRefreshJob(db, svc, NullLogger<ObifinAccountRefreshJob>.Instance)`, `svc` dosyanın mevcut servis kurulumuyla):

```csharp
    [Fact]
    public async Task Lisansi_biten_baglantinin_hesaplari_yenilenmez_Disabled_yazilir()
    {
        using var db = NewDb();
        var client = new PerUserObifin();
        var svc = new ObifinConnectionService(db, client, Protection,
            new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" })),
            Options.Create(new ObifinOptions()), NullLogger<ObifinConnectionService>.Instance);
        var userCode = $"u-{Guid.NewGuid():N}@x";
        var lic = await SeedVerifiedAsync(db, svc, userCode);
        client.Accounts[userCode] = () => Array.Empty<ObifinAccountDto>();
        (await db.Licenses.SingleAsync(l => l.Id == lic)).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var ok = await new ObifinAccountRefreshJob(db, svc, NullLogger<ObifinAccountRefreshJob>.Instance)
            .RunAsync(CancellationToken.None);

        ok.Should().Be(0);
        client.Calls.Should().BeEmpty();
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.DisabledAt.Should().NotBeNull();
    }
```

(Dosyanın üstünde `using Microsoft.Extensions.Logging.Abstractions;` ve `using Microsoft.Extensions.Options;` zaten var; yoksa ekle.)

- [ ] **Adım 3: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ObifinPollJobTests|FullyQualifiedName~ObifinAccountRefreshJobTests"`
Expected: FAIL — `ObifinConnection.DisabledAt` ve `ObifinConnectionService.LicenseEndedMessage` yok.

- [ ] **Adım 4: Varlığa alanı ekle**

`Domain/Bank/ObifinConnection.cs` — `public string? LastError { get; set; }` satırının üstüne:

```csharp
    /// <summary>Bağlantının <see cref="ObifinConnectionStatus.Disabled"/>'a çekildiği an: lisans bitti ya da yönetici durdurdu
    /// (KVKK K-6). Hizmet sonu silmesi (BankServiceEndPurgeJob) bu andan 30 gün sonra koşar; yeniden etkinleştirmede
    /// boşaltılır. Zaten Disabled olan bağlantıda yeniden yazılmaz: sayaç baştan başlamaz.</summary>
    public DateTimeOffset? DisabledAt { get; set; }
```

- [ ] **Adım 5: Servise lisans denetimini ekle**

`Services/Bank/ObifinConnectionService.cs` — `ConcurrentMatchWriteMessage` sabitinin altına:

```csharp
    /// <summary>Lisansı biten bağlantının <c>LastError</c> metni (admin ekranında görünür).</summary>
    public const string LicenseEndedMessage = "Lisans sona erdi ya da iptal edildi; banka çekimi durduruldu.";
```

`TrToUtc` metodunun hemen altına (statik: `BankHasher` istemez — çekim, hesap yenileme ve hizmet sonu işi aynı kuralı kullanır):

```csharp
    /// <summary>KVKK K-6: süresi dolmuş ya da iptal edilmiş lisansın bağlantısı <see cref="ObifinConnectionStatus.Disabled"/>'a
    /// çekilir, <see cref="ObifinConnection.DisabledAt"/> ve <see cref="LicenseEndedMessage"/> yazılır. Zaten Disabled olan
    /// atlanır (DisabledAt korunur). <c>UpdatedAt</c> ilerler: eşzamanlı bir çekim koşusu kimlik denetiminde bunu görür ve
    /// yazmadan biter. Değişen bağlantı sayısını döner.</summary>
    public static async Task<int> DisableEndedLicensesAsync(LicenseDbContext db, DateTimeOffset now, ILogger log,
        CancellationToken ct)
    {
        var ended = await db.ObifinConnections
            .Where(c => c.Status != ObifinConnectionStatus.Disabled
                && db.Licenses.Any(l => l.Id == c.LicenseId && (l.RevokedAt != null || l.ExpiresAt <= now)))
            .ToListAsync(ct);
        foreach (var c in ended)
        {
            c.Status = ObifinConnectionStatus.Disabled;
            c.DisabledAt = now;
            c.LastError = LicenseEndedMessage;
            c.UpdatedAt = now;
        }
        if (ended.Count == 0) return 0;
        await db.SaveChangesAsync(ct);
        log.LogWarning("Obifin: lisansı biten {Count} bağlantı durduruldu (Disabled)", ended.Count);
        return ended.Count;
    }
```

- [ ] **Adım 6: Çekim işine bağla**

`Services/Bank/ObifinPollJob.cs` — `RunAsync`'in ilk satırının (`var ids = await _db.ObifinConnections…`) ÜSTÜNE:

```csharp
        // KVKK K-6: lisansı biten bağlantı önce Disabled'a çekilir; aşağıdaki Verified listesine girmez.
        await ObifinConnectionService.DisableEndedLicensesAsync(_db, DateTimeOffset.UtcNow, _log, ct);
```

`PollConnectionAsync`'te `if (conn is null || conn.Status != ObifinConnectionStatus.Verified) return;` satırının ALTINA:

```csharp
        // "Şimdi çek" bu metodu doğrudan kuyruğa atar: RunAsync'teki denetimi burada da yap.
        var nowUtc = DateTimeOffset.UtcNow;
        if (!await _db.Licenses.AnyAsync(l => l.Id == conn.LicenseId && l.RevokedAt == null && l.ExpiresAt > nowUtc, ct))
        {
            await ObifinConnectionService.DisableEndedLicensesAsync(_db, nowUtc, _log, ct);
            return;
        }
```

- [ ] **Adım 7: Hesap yenileme işine bağla**

`Services/Bank/ObifinAccountRefreshJob.cs` — `RunAsync`'in ilk satırının ÜSTÜNE:

```csharp
        // KVKK K-6: lisansı bitenin hesap listesi de sorgulanmaz.
        await ObifinConnectionService.DisableEndedLicensesAsync(_db, DateTimeOffset.UtcNow, _log, ct);
```

- [ ] **Adım 8: Göçü üret ve incele**

Run: `cd OrderDeck.LicenseServer && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add ObifinConnectionDisabledAt --context LicenseDbContext --output-dir Data/Migrations`
Expected: `Up` yalnız `migrationBuilder.AddColumn<DateTimeOffset>(name: "DisabledAt", table: "ObifinConnections", … nullable: true)`. Başka değişiklik varsa dur.

- [ ] **Adım 9: Testleri koştur**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ObifinPollJobTests|FullyQualifiedName~ObifinAccountRefreshJobTests|FullyQualifiedName~AdminObifinPageTests"`
Expected: PASS (mevcut testlerin lisansları 30 gün geçerli, etkilenmez).

- [ ] **Adım 10: Göçün SQL Server'da kurulduğunu kanıtla**

`docker version` → çalışıyorsa:
Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~GroupCustomersRelationalTests"`
Expected: PASS.

- [ ] **Adım 11: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/Bank/ObifinConnection.cs OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs OrderDeck.LicenseServer/Services/Bank/ObifinAccountRefreshJob.cs OrderDeck.LicenseServer/Data/Migrations OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/ObifinAccountRefreshJobTests.cs
git commit -m "fix(bank-kvkk): lisansı biten yayıncının banka çekimini durdur

Süresi dolan ya da iptal edilen lisansın Obifin bağlantısı Disabled'a
çekilir ve DisabledAt yazılır; çekim, Şimdi çek ve hesap yenileme artık
o bankayı sorgulamaz (KVKK ön inceleme K-6, P0-1).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

**Uygulama notu (Görev 1 incelemesi, 20.10):** kod yukarıdaki parçalardan şu noktalarda ayrılır; sonraki görevler koddakine göre yazıldı.
- Lisans kuralı tek ifadede: `ObifinConnectionService.LicenseActiveAt(now)` (`Expression<Func<License, bool>>`). `DisableEndedLicensesAsync` bunun değilini alt sorguyla süzer (SQL'de `NOT IN`); Görev 13'ün `IsLicenseActiveAsync`'i, Görev 14'ün yenilenme ve silme sorguları da onu kullanır. "Tek yer" yalnız banka modülü için doğrudur: lisans denetleyen controller'lar aynı yüklemi ayrıca yazar.
- `PollConnectionAsync` ayrı lisans sorgusu yapmaz: `DisableEndedLicensesAsync(..., onlyConnectionId: conn.Id)` çağırır ve izlenen `conn`'un durumuna bakar. Tek bağlantılık çağrı yalnız kendini denetler (kiracı yalıtımı); bütün kiracılar `RunAsync`'in başında ve hesap yenilemede denetlenir.
- Testler: başlangıç durumu ekseni (Verified/Failed/Unverified × süre bitti/iptal), iki lisanslı çekimde yalnız bitenin durması, "Şimdi çek"in başka lisansın satırına yazmaması.
- P1'e kalan davranış belgede "Görev 13/14 ile" diye işaretli; Görev 13 Adım 3 ve Görev 14 Adım 3 işaretleri kaldırır. P0 tek başına çıkarsa: Prod adımları → "P0 deploy'undan ÖNCE" ve "P0 ile P1 arasında".

---

### Görev 2: Giden ve sıfır tutarlı hareketi kaydetme (P0-2)

**Neden (not K-4):** giden hareketlerin (maaş, kira, tedarikçi, şahsi harcama) okuyan bir kod yolu yok ve bu kişiler için aydınlatma kanalı yok. Obifin isteğinde yön süzgeci olmadığı için hareket sunucuya gelir; artık kaydedilmeden atılır. Sıfır tutarlı satır da ödeme olamaz. İmleç sıralı listeden hesaplandığı için (`FetchWindowWithMetaAsync`, `ordered[^1].Id`) atlanan satırlar imleci yine ilerletir. **Yön işaretten okunur:** `ObifinClient` bugün `TutarEksiArti` yoksa işaretsiz `Tutar`'a düşüyor — o durumda giden hareket pozitif okunur ve süzgeçten geçerdi. Artık işaretli tutar yoksa sayfa `ObifinProtocolException` ile düşer (fail-closed; virgüllü tutardaki "sessiz hata yerine gürültülü hata" kuralının aynısı): hiçbir satır yazılmaz, imleç ilerlemez, hata bağlantının `LastError`'unda görünür.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs:279-320` (`UpsertAsync`)
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinClient.cs` (`ListTransactionsAsync`, işaretli tutar)
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs`, `ObifinClientTests.cs`

- [ ] **Adım 1: Mevcut üç testi yeni kurala çevir (kırmızı)**

`Artimli_cekim_31_gunluk_pencere_ve_imlecle_yalniz_yeni_hareketleri_yazar` içinde:

```csharp
        (await db.BankTransactions.CountAsync()).Should().Be(2);
```
satırını şununla değiştir:
```csharp
        (await db.BankTransactions.CountAsync()).Should().Be(1, "giden hareket (202) kaydedilmeden atılır");
```

`Tr_saat_utc_ye_cevrilir_yon_isaretten_iban_yalniz_hash_ve_maske` testinin adını `Tr_saat_utc_ye_cevrilir_iban_yalniz_hash_ve_maske_giden_atilir` yap ve son satırı:

```csharp
        (await db.BankTransactions.SingleAsync(t => t.ObifinId == 8)).Direction.Should().Be(BankTransactionDirection.Outgoing);
```
şununla değiştir:
```csharp
        (await db.BankTransactions.AnyAsync(t => t.ObifinId == 8)).Should().BeFalse("giden hareket kaydedilmeden atılır (KVKK K-4)");
```

`Sifir_tutarli_hareket_saklanir_ama_eslestiriciye_gitmez` testini bütünüyle şununla değiştir:

```csharp
    [Fact]
    public async Task Giden_ve_sifir_tutarli_hareket_yazilmaz_imlec_yine_ilerler()
    {
        // KVKK K-4: giden hareketin ve sıfır tutarlı satırın (Obifin demo verisinde TutarEksiArti "0.00") amacı yok.
        // Obifin listesinde gelir, kaydedilmeden atılır; imleç sıralı listeden hesaplandığı için yine ilerler.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 0m));
        client.Transactions.Add(Tx(2, new DateTime(2026, 9, 25, 8, 2, 0), 25m));
        client.Transactions.Add(Tx(3, new DateTime(2026, 9, 25, 8, 3, 0), -40m));

        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.Select(t => t.ObifinId).ToListAsync()).Should().BeEquivalentTo(new[] { 2L });
        (await db.BankTransactions.SingleAsync()).Direction.Should().Be(BankTransactionDirection.Incoming);
        sink.Received.Should().ContainSingle().Which.ObifinId.Should().Be(2);
        (await db.ObifinConnections.SingleAsync()).LastObifinTransactionId.Should().Be(3, "atlanan hareketler de görüldü");
    }
```

`ObifinClientTests.cs` sınıfının sonuna:

```csharp
    [Fact]
    public async Task Isaretli_tutar_yoksa_protokol_istisnasi_isaretsiz_Tutar_kullanilmaz()
    {
        // KVKK K-4: yön TutarEksiArti'nın işaretinden okunur. İşaretsiz Tutar'a düşülseydi giden hareket pozitif okunur, "yalnız
        // gelen" süzgecinden geçip kaydedilirdi. Fail-closed: sayfa düşer, hiçbir satır yazılmaz, imleç ilerlemez.
        var (client, _) = Build("""
        {"Hata":[],"Liste":[{"Id":"1","HesapId":"6","IslemZamaniDT":"2022-10-10 12:07:30",
          "Tutar":"10.00","ParaBirimi":"TL","BankaKodu":"garanti"}]}
        """);
        var act = () => client.ListTransactionsAsync(Creds(), new DateOnly(2022, 10, 1), new DateOnly(2022, 10, 17), null, 1, 1000);
        await act.Should().ThrowAsync<ObifinProtocolException>();
    }
```

- [ ] **Adım 2: Koştur — kırmızı bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ObifinPollJobTests|FullyQualifiedName~ObifinClientTests"`
Expected: FAIL — üç çekim testi (giden/sıfır satır hâlâ yazılıyor) ve istemci testi (işaretsiz `Tutar` kabul ediliyor).

- [ ] **Adım 3: Uygula**

`Services/Bank/ObifinClient.cs` — `ListTransactionsAsync`'te:

```csharp
            var signed = Dec(row, "TutarEksiArti") ?? Dec(row, "Tutar")
                ?? throw new ObifinProtocolException("hareket tutarı yok");
```
şununla değiştir:
```csharp
            // KVKK K-4: yön yalnız işaretli tutardan okunur. İşaretsiz "Tutar"a düşülseydi giden hareket pozitif okunur ve çekim
            // işinin "yalnız gelen" süzgecinden geçerdi. Fail-closed: sayfa düşer, imleç ilerlemez, hata LastError'da görünür.
            var signed = Dec(row, "TutarEksiArti")
                ?? throw new ObifinProtocolException("işaretli hareket tutarı (TutarEksiArti) yok");
```

`ObifinPollJob.UpsertAsync` içinde `foreach (var dto in items)` döngüsünün ilk satırı olan yorum + `if (!existing.Add(dto.Id)) continue;` ikilisinin ÜSTÜNE ekle:

```csharp
            // KVKK K-4: yalnız hesaba GELEN ve tutarı sıfırdan büyük hareket kaydedilir. Giden hareket (maaş, kira, tedarikçi,
            // şahsi harcama) ve sıfır tutarlı satır hiçbir amaca hizmet etmez; Obifin'de yön süzgeci yok, burada atılır. İmleç
            // sıralı listeden hesaplanır (FetchWindowWithMetaAsync): atlanan satır imleci yine ilerletir.
            if (dto.SignedAmount <= 0) continue;
```

Aynı metotta `Direction = dto.SignedAmount >= 0 ? BankTransactionDirection.Incoming : BankTransactionDirection.Outgoing,` satırını şununla değiştir:

```csharp
                Direction = BankTransactionDirection.Incoming,
```

Ve `Amount = Math.Abs(dto.SignedAmount),` satırını `Amount = dto.SignedAmount,` yap.

Metodun sonundaki:

```csharp
        // Sıfır tutarlı satır (Obifin demo verisinde görüldü: TutarEksiArti "0.00") saklanır ama ödeme olamaz.
        foreach (var tx in fresh.Where(t => t.Direction == BankTransactionDirection.Incoming && t.Amount != 0))
            await _sink.OnNewIncomingAsync(tx, ct);
```
bloğunu şununla değiştir:
```csharp
        // Yalnız gelen ve sıfırdan büyük satır yazıldı (yukarıdaki süzgeç): hepsi eşleştiriciye gider.
        foreach (var tx in fresh)
            await _sink.OnNewIncomingAsync(tx, ct);
```

Sınıf özetindeki (`/// <summary>`) ilk paragrafın sonuna şu cümleyi ekle: `Yalnız gelen ve tutarı sıfırdan büyük hareket kaydedilir (KVKK K-4).`

- [ ] **Adım 4: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ObifinPollJobTests|FullyQualifiedName~Services.Bank"`
Expected: PASS. (`ObifinClientTests`'in diğer hareket örneklerinin hepsinde `TutarEksiArti` var; yoksa o örneğe ekle — test verisi gerçek yanıt biçimini taşımalı.)

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs OrderDeck.LicenseServer/Services/Bank/ObifinClient.cs OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/ObifinClientTests.cs
git commit -m "fix(bank-kvkk): giden ve sıfır tutarlı banka hareketini kaydetme

Obifin listesindeki giden ve sıfır tutarlı hareketler BankTransaction
olarak yazılmadan atılır; imleç yine ilerler. Yön yalnız TutarEksiArti'dan
okunur: işaretli tutar yoksa sayfa düşer, işaretsiz Tutar'a düşülmez
(KVKK ön inceleme K-4, P0-2). Mevcut satırlar bank-data-cleanup
komutuyla silinir (Görev 8).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 3: Karşı tarafın VKN/TCKN kodunu kaydetmeyi bırak (P0-3)

**Neden (not Ö-2):** `CounterpartyTaxIdHash` yazılıyor ama okuyan üretim kodu yok; TCKN'den türetilmiş veri amaçsız ve süresiz. Giden harekette seçim sırası (`BorcluVKN → AmirVKN → LehdarTCKN`) yayıncının kendi numarasını da yakalayabilir. İstemci bu alanları artık okumaz, iş yazmaz. **Sütun kalır** (genişlet/daralt: eski imaj geri alınırsa sütuna yazmaya devam edebilsin); eski değerleri Görev 8'deki komut boşaltır, sütun sonraki bir sürümde düşürülür.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Bank/IObifinClient.cs:19-22`
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinClient.cs:114`
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs:310`
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankHasher.cs:8-16, 48-56`
- Modify: `OrderDeck.LicenseServer/Domain/Bank/BankTransaction.cs:27`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankHasherTests.cs`, `ObifinClientTests.cs`, `ObifinPollJobTests.cs`

- [ ] **Adım 1: Çekim testini yaz (kırmızı)**

`ObifinPollJobTests.cs` içindeki `Ham_json_kimlik_alanlari_redakte_edilerek_saklanir_hash_ve_maske_ham_degerden_uretilir` testinde `client.Transactions.Add(new ObifinTransactionDto(9, 6, …, iban, "Ad Soyad", vkn, raw));` çağrısını (iki satır) şununla değiştir:

```csharp
        client.Transactions.Add(new ObifinTransactionDto(9, 6, "qnb", new DateTime(2026, 9, 25, 12, 0, 0), 10m, "TL",
            "EFT", "FT", "EFT", "ref-9", iban, "Ad Soyad", raw));
```

ve `tx.CounterpartyTaxIdHash.Should().HaveLength(64);` satırını şununla değiştir:

```csharp
        tx.CounterpartyTaxIdHash.Should().BeNull("VKN/TCKN kodu amaçsız: yazılmaz (KVKK Ö-2)");
```

Aynı dosyada `Tx` yardımcısını:

```csharp
        => new(id, 6, "qnb", whenTr, signed, "TL", desc, "FT", "EFT", $"ref-{id}", iban, null, null, $"{{\"Id\":\"{id}\"}}");
```
şununla değiştir:
```csharp
        => new(id, 6, "qnb", whenTr, signed, "TL", desc, "FT", "EFT", $"ref-{id}", iban, null, $"{{\"Id\":\"{id}\"}}");
```

`Bilinmeyen_hesap_icin_yer_tutucu_acilir_bilinen_hesaba_baglanir` testindeki `new ObifinTransactionDto(2, 99, … null, null, null, null, null, null, null, "{}")` çağrısından bir `null` çıkar (altı `null` kalır):

```csharp
        client.Transactions.Add(new ObifinTransactionDto(2, 99, "qnb", new DateTime(2026, 9, 25, 8, 2, 0), 10m, "TL",
            null, null, null, null, null, null, "{}"));                       // hesap 99 bilinmiyor
```

- [ ] **Adım 2: İstemci ve hasher testlerini güncelle**

`ObifinClientTests.cs` — `page.Items[0].CounterpartyTaxId.Should().BeNull("boş string null sayılır");` satırını SİL.

`BankHasherTests.cs` — `Vkn_hashinde_yalniz_rakamlar_sayilir_ayni_vkn_ayni_hash`, `Vkn_bos_rakamsiz_veya_kalinti_ise_hash_uretmez` ve `Vkn_hashi_yalniz_10_veya_daha_fazla_rakamda_uretilir` (`[Theory]` ve dört `[InlineData]` ile birlikte) testlerini SİL. Sınıf özetini şöyle güncelle:

```csharp
/// <summary>IBAN düz saklanmaz; HMAC anahtarı olmadan özetten IBAN elde edilemez, aynı değer aynı özeti verir (hafıza
/// araması), maskeleme yalnız görüntü içindir. VKN/TCKN artık özetlenmez (KVKK Ö-2).</summary>
```

- [ ] **Adım 3: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Services.Bank"`
Expected: FAIL — `ObifinTransactionDto` 14 argüman bekliyor.

- [ ] **Adım 4: DTO'dan alanı çıkar**

`Services/Bank/IObifinClient.cs`:

```csharp
/// <summary>Tek hareket. Tarih TR yerel (dönüşüm çağıranda); tutar işaretli. Karşı tarafın VKN/TCKN alanları
/// (BorcluVKN, AmirVKN, LehdarTCKN) BİLEREK okunmaz (KVKK Ö-2): amaçsız veri.</summary>
public sealed record ObifinTransactionDto(
    long Id, long AccountId, string BankaKodu, DateTime OccurredAtTr, decimal SignedAmount, string Currency,
    string? Description, string? TransactionCode, string? CommonType, string? BankReference,
    string? CounterpartyIban, string? CounterpartyName, string RawJson);
```

`Services/Bank/ObifinClient.cs` — şu satırı SİL:

```csharp
                CounterpartyTaxId: Str(row, "BorcluVKN") ?? Str(row, "AmirVKN") ?? Str(row, "LehdarTCKN"),
```

`Services/Bank/ObifinPollJob.cs` — `UpsertAsync`'teki şu satırı SİL:

```csharp
                CounterpartyTaxIdHash = _hasher.HashTaxId(dto.CounterpartyTaxId),
```

ve sınıf özetindeki `<para><b>Ham JSON</b> saklanmadan önce IBAN/VKN/TCKN alanları redakte edilir` paragrafını şu hâle getir:

```csharp
/// <para><b>Ham JSON</b> saklanmadan önce redakte edilir (<see cref="BankRawJsonRedactor"/>); IBAN özeti + maske DTO'nun
/// ham değerinden üretilir. Karşı tarafın VKN/TCKN'si okunmaz ve yazılmaz (KVKK Ö-2).</para>
```

- [ ] **Adım 5: Hasher'dan VKN özetini kaldır**

`Services/Bank/BankHasher.cs` — `MinTaxIdDigits` sabitini ve `HashTaxId` metodunu (özetiyle birlikte) SİL. Sınıf özetini şöyle değiştir:

```csharp
/// <summary>
/// IBAN için HMAC-SHA256 (spec §7). Düz SHA yetersiz: IBAN'ın entropisi düşük, sızan tablo sözlükle çözülür. Anahtar yalnız
/// sunucuda; özet yalnız "aynı mı" sorusuna cevap verir (hafıza araması). Anahtarı bilmeyen özetten IBAN elde edemez; anahtarı
/// bilen aday IBAN'ları deneyerek bulabilir — özet ve maske bu yüzden kişisel veri sayılır (KVKK Ö-6). VKN/TCKN özetlenmez.
/// </summary>
```

- [ ] **Adım 6: Varlıktaki sütunu belgeleyerek bırak**

`Domain/Bank/BankTransaction.cs` — `public string? CounterpartyTaxIdHash { get; set; }` satırının üstüne:

```csharp
    /// <summary>YAZILMAZ (KVKK Ö-2, 2026-10). Sütun genişlet/daralt kuralıyla bir sonraki sürümde düşürülür; eski değerleri
    /// <c>bank-data-cleanup</c> boşaltır. Yeni kod bu alanı okumamalı.</summary>
```

- [ ] **Adım 7: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Services.Bank|FullyQualifiedName~AdminBankaEslestirmePageTests|FullyQualifiedName~BankModelTests"`
Expected: PASS (`AdminBankaEslestirmePageTests` hâlâ sütuna elle yazıyor, model sütunu tanıyor — doğru).

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank OrderDeck.LicenseServer/Domain/Bank/BankTransaction.cs OrderDeck.LicenseServer.Tests/Services/Bank
git commit -m "fix(bank-kvkk): karşı tarafın VKN/TCKN kodunu okuma ve yazma

Obifin istemcisi BorcluVKN/AmirVKN/LehdarTCKN alanlarını okumaz, çekim
işi CounterpartyTaxIdHash yazmaz; BankHasher.HashTaxId kaldırıldı.
Sütun genişlet/daralt kuralıyla şimdilik kalır (KVKK ön inceleme Ö-2, P0-3).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 4: Açıklamadaki IBAN ve 11 haneli numarayı maskele (P0-5)

**Neden (not Ö-16, Ö-4):** havale açıklaması serbest metin; gönderen IBAN'ını ya da T.C. kimlik numarasını yazabiliyor ve bu düz saklanıyor. Kayda alınmadan önce TR IBAN `BankHasher.MaskIban` biçimine (`TR12…345`), tek başına duran 11 haneli rakam dizisi (TCKN, cep telefonu) yıldıza çevrilir.
- **IBAN:** kalıp `TR` + 2 rakam + 5 rakam (banka kodu) + 17 alfasayısal, harf/rakam **sınırı olmadan** (açıklamada IBAN'ın önüne ya da arkasına bitişik yazılmış harf — `IBANTR…`, `…1234AHMET` — eşleşmeyi engellemesin); yanlış eşleşmeyi **ISO 7064 mod-97** eler (`BankHasher.HasValidIbanChecksum`, bu görevde eklenir; Görev 11 aynı denetimi `NormalizeIban`'da kullanır). "TRANSFER ODEME…" gibi sözcük dizileri ve kullanıcı adları eşleşmez (eşleştiricinin baktığı kullanıcı adı bozulmaz). Kontrol hanesi tutmayan IBAN biçimli dizi (yanlış yazılmış IBAN) maskelenmez — bilinçli sınır: kalıbı sınırsız bırakmanın bedeli.
- **11 hane:** kalıbın sınırı yalnız rakam (`(?<!\p{N})\d{11}(?!\p{N})`). Tek başına duran dizi (komşusu boşluk, noktalama ya da metin sınırı) her zaman maskelenir. Harfe bitişik dizi (`user` + 11 hane) kullanıcı adının parçası olabilir; yalnız TCKN kontrol hanelerini tutuyorsa (`TcknValidator.Validate(v) is null`) maskelenir — kimlik numarasının en doğal yazımları harfe bitişik: `TC`/`TCKN`/`TCNO` + 11 hane, isme bitişik 11 hane + `AHMET`. TCKN 0 ile başlamaz: kullanıcı adındaki "0" ile başlayan telefon hiç etkilenmez. Bedeli: harfe bitişik rastgele 11 hane yaklaşık %1 olasılıkla (9/1000) TCKN gibi görünüp maskelenir (IBAN'daki mod-97 ile aynı türden, gizlilik yönünde). `_` / `.` harf değildir: onlarla ayrılmış 11 hane maskelenir, o kullanıcı adı açıklamadan eşleşmez (iş IBAN hafızasına ya da ad katmanına düşer). İnceleme (20.10): ilk sürüm harfe bitişik diziyi hiç maskelemiyordu; `TC` önekli TCKN düz kalıyordu. Aşağıdaki kod blokları ilk sürümdür; güncel hâli `BankDescriptionMasker.cs` ve `BankDescriptionMaskerTests.cs`. Metnin söz verdiği kapsam ve bilinçli sınırlar: karar listesi madde 18.
- **Test IBAN'ı:** `BankHasherTests.TestIban()` bu görevde mod-97 geçerli üretilir (`WithCheckDigits`); repo public, gerçek IBAN yazılmaz.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Bank/BankDescriptionMasker.cs`
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankHasher.cs` (`HasValidIbanChecksum`)
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs` (`Description = …` satırı)
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankDescriptionMaskerTests.cs` (YENİ), `BankHasherTests.cs` (geçerli test IBAN'ı), `ObifinPollJobTests.cs`

- [ ] **Adım 0: Test IBAN üretecini mod-97 geçerli yap**

`BankHasherTests.cs` — `TestIban()` yardımcısını şununla değiştir:

```csharp
    /// <summary>Mod-97 geçerli TR IBAN üretir — repo public, gerçek IBAN yazılmaz (rastgele 22 haneli BBAN; geçerli kontrol
    /// haneli rastgele bir değerin gerçek bir hesaba denk gelmesi için banka kodu ve hesap numarasının da tutması gerekir).</summary>
    public static string TestIban() => WithCheckDigits("TR", TestPhone.Digits(22));

    /// <summary>ISO 13616 kontrol haneleriyle IBAN üretir. Üretim kodundan bağımsız hesap: test doğrulayıcının kendisini sınar.</summary>
    public static string WithCheckDigits(string country, string bban)
    {
        var remainder = 0;
        foreach (var c in bban + country + "00")
            remainder = char.IsAsciiDigit(c) ? (remainder * 10 + (c - '0')) % 97 : (remainder * 100 + (c - 'A' + 10)) % 97;
        return country + (98 - remainder).ToString("00") + bban;
    }

    /// <summary>Kontrol hanelerini bozar (her zaman geçersiz: tek hanenin değişmesi mod-97 kalanını değiştirir).</summary>
    public static string WrongCheckDigits(string iban)
    {
        var check = int.Parse(iban[2..4]);
        return iban[..2] + (check == 98 ? 2 : check + 1).ToString("00") + iban[4..];
    }
```

Dosyanın üstüne `using OrderDeck.LicenseServer.Tests.TestHelpers;` ekle (yoksa). Sınıfın sonuna:

```csharp
    [Fact]
    public void Kontrol_hanesi_denetimi_gecerli_IBANi_kabul_eder_bozugu_reddeder()
    {
        var iban = TestIban();
        BankHasher.HasValidIbanChecksum(iban).Should().BeTrue();
        BankHasher.HasValidIbanChecksum(WrongCheckDigits(iban)).Should().BeFalse();
        BankHasher.HasValidIbanChecksum(iban[..10] + "******" + iban[16..]).Should().BeFalse("harf/rakam dışı karakter");
    }
```

- [ ] **Adım 1: Maskeleyici testini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Bank/BankDescriptionMaskerTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK Ö-16: açıklamaya yazılmış IBAN ve 11 haneli numara kayda düz girmez. Değerler üretilir (repo public).</summary>
public sealed class BankDescriptionMaskerTests
{
    private static string Spaced(string iban)
        => string.Join(" ", iban.Chunk(4).Select(c => new string(c)));

    [Fact]
    public void Bitisik_IBAN_maskelenir()
    {
        var iban = BankHasherTests.TestIban();
        var masked = BankDescriptionMasker.Mask($"HAVALE {iban} ornek_musteri34");

        masked.Should().Be($"HAVALE {BankHasher.MaskIban(iban)} ornek_musteri34");
        masked.Should().NotContain(iban[4..12]);
    }

    [Fact]
    public void Dortlu_gruplarla_yazilmis_ve_kucuk_harfli_IBAN_maskelenir()
    {
        var iban = BankHasherTests.TestIban();
        var masked = BankDescriptionMasker.Mask("EFT " + Spaced(iban).ToLowerInvariant() + " SIPARIS");

        masked.Should().Be($"EFT {BankHasher.MaskIban(iban)} SIPARIS");
    }

    [Fact]
    public void Harfe_bitisik_IBAN_de_maskelenir()
    {
        // Sınır yok: "IBANTR…" ya da "…1234AHMET" gibi bitişik yazımda da IBAN maskelenir; mod-97 yanlış eşleşmeyi eler.
        var iban = BankHasherTests.TestIban();
        BankDescriptionMasker.Mask($"IBAN{iban}AHMET").Should().Be($"IBAN{BankHasher.MaskIban(iban)}AHMET");
    }

    [Fact]
    public void Kontrol_hanesi_tutmayan_IBAN_bicimli_dizi_maskelenmez()
    {
        var wrong = BankHasherTests.WrongCheckDigits(BankHasherTests.TestIban());
        BankDescriptionMasker.Mask($"REF {wrong}").Should().Be($"REF {wrong}");
    }

    [Fact]
    public void On_bir_haneli_numara_yildizlanir()
    {
        var tckn = TestTckn.NewValid();
        BankDescriptionMasker.Mask($"FAST {tckn} AD SOYAD").Should().Be($"FAST {BankDescriptionMasker.ElevenDigitMask} AD SOYAD");
    }

    [Theory]
    [InlineData("user")]
    [InlineData("ş")]
    public void Harfe_bitisik_on_bir_hane_maskelenmez(string prefix)
    {
        // Kullanıcı adının parçası olabilir (eşleştirici açıklamadaki kullanıcı adına bakıyor).
        var digits = TestPhone.Digits(11);
        BankDescriptionMasker.Mask($"HAVALE {prefix}{digits}").Should().Be($"HAVALE {prefix}{digits}");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    public void On_bir_haneden_farkli_rakam_dizisi_degismez(int length)
    {
        var digits = TestPhone.Digits(length);
        BankDescriptionMasker.Mask($"REF {digits}").Should().Be($"REF {digits}");
    }

    [Fact]
    public void Kullanici_adi_ve_TR_ile_baslayan_sozcukler_degismez()
    {
        const string text = "TRANSFER ODEME KODU ABC123 ornek_musteri34 TR12 HAVALE";
        BankDescriptionMasker.Mask(text).Should().Be(text);
    }

    [Fact]
    public void Bos_ve_null_aynen_doner_maskeleme_idempotent()
    {
        BankDescriptionMasker.Mask(null).Should().BeNull();
        BankDescriptionMasker.Mask("").Should().Be("");
        var once = BankDescriptionMasker.Mask($"{BankHasherTests.TestIban()} {TestTckn.NewValid()}");
        BankDescriptionMasker.Mask(once).Should().Be(once);
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDescriptionMaskerTests|FullyQualifiedName~BankHasherTests"`
Expected: FAIL — `BankDescriptionMasker`, `HasValidIbanChecksum` yok.

- [ ] **Adım 3: Kontrol hanesi denetimi ve maskeleyici**

`Services/Bank/BankHasher.cs` — `MaskIban`'ın ALTINA:

```csharp
    /// <summary>ISO 13616 / ISO 7064 MOD 97-10: boşluksuz, büyük harfli değerin ilk dört karakteri sona alınır, harfler 10–35'e
    /// çevrilir, kalan 1 olmalı. Biçim (ülke, uzunluk) denetlemez — çağıran yapar; ASCII harf/rakam dışı karakterde false.</summary>
    public static bool HasValidIbanChecksum(string compactUpper)
    {
        if (compactUpper.Length < 5) return false;
        var remainder = 0;
        foreach (var c in compactUpper[4..] + compactUpper[..4])
        {
            if (char.IsAsciiDigit(c)) remainder = (remainder * 10 + (c - '0')) % 97;
            else if (char.IsAsciiLetterUpper(c)) remainder = (remainder * 100 + (c - 'A' + 10)) % 97;
            else return false;
        }
        return remainder == 1;
    }
```

`OrderDeck.LicenseServer/Services/Bank/BankDescriptionMasker.cs`:

```csharp
using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// KVKK Ö-16/Ö-4: havale açıklaması serbest metindir; gönderen içine IBAN ya da T.C. kimlik numarası yazabilir. Kayda alınmadan
/// önce (açıklama sütunu ve ham kayıttaki açıklama alanları):
/// <list type="bullet">
/// <item>TR IBAN — bitişik ya da tek boşluklu gruplarla, büyük/küçük harf, önünde ya da arkasında bitişik harf olsa da —
/// <see cref="BankHasher.MaskIban"/> biçimine (<c>TR12…345</c>) çevrilir. Kalıp <c>TR</c> + 2 rakam + 5 rakam (banka kodu) + 17
/// alfasayısal; sınır yok, yanlış eşleşmeyi mod-97 (<see cref="BankHasher.HasValidIbanChecksum"/>) eler. Sözcük dizileri ve
/// kullanıcı adları eşleşmez — eşleştirici açıklamadaki kullanıcı adına bakıyor. Kontrol hanesi tutmayan dizi maskelenmez.</item>
/// <item>Harf ya da rakama bitişik OLMAYAN 11 haneli rakam dizisi (TCKN, cep telefonu) <see cref="ElevenDigitMask"/> olur.
/// Harfe bitişik dizi kullanıcı adının parçası olabilir: maskelenmez.</item>
/// </list>
/// Saf ve idempotent: maskelenmiş metin yeniden maskelenince değişmez (bank-data-cleanup buna dayanır).
/// </summary>
public static partial class BankDescriptionMasker
{
    public const string ElevenDigitMask = "***********";

    [GeneratedRegex(@"TR\s?\d\s?\d(?:\s?\d){5}(?:\s?[0-9A-Za-z]){17}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrIban();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d{11}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex ElevenDigits();

    public static string? Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var withoutIban = TrIban().Replace(text, m =>
        {
            var compact = new string(m.Value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
            return BankHasher.HasValidIbanChecksum(compact) ? BankHasher.MaskIban(compact) : m.Value;
        });
        return ElevenDigits().Replace(withoutIban, ElevenDigitMask);
    }
}
```

- [ ] **Adım 4: Koştur — maskeleyici yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDescriptionMaskerTests|FullyQualifiedName~BankHasherTests"`
Expected: PASS.

- [ ] **Adım 5: Çekim testini yaz (kırmızı)**

`ObifinPollJobTests.cs` sınıfının sonuna:

```csharp
    [Fact]
    public async Task Aciklamadaki_IBAN_ve_on_bir_haneli_numara_maskelenerek_yazilir()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var iban = BankHasherTests.TestIban();
        var tckn = TestTckn.NewValid();
        client.Transactions.Add(Tx(21, new DateTime(2026, 9, 25, 10, 0, 0), 50m, $"HAVALE {iban} {tckn} ornek_musteri34"));

        await job.RunAsync(CancellationToken.None);

        var tx = await db.BankTransactions.SingleAsync();
        tx.Description.Should().Be($"HAVALE {BankHasher.MaskIban(iban)} {BankDescriptionMasker.ElevenDigitMask} ornek_musteri34");
    }
```

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ObifinPollJobTests.Aciklamadaki"`
Expected: FAIL — açıklama düz.

- [ ] **Adım 6: Çekim işinde uygula**

`ObifinPollJob.UpsertAsync` içinde:

```csharp
                Description = Trim(dto.Description, 512), TransactionCode = Trim(dto.TransactionCode, 32),
```
satırını şununla değiştir:
```csharp
                // KVKK Ö-16: açıklamadaki IBAN ve 11 haneli numara maskelenerek yazılır (kırpmadan önce).
                Description = Trim(BankDescriptionMasker.Mask(dto.Description), 512), TransactionCode = Trim(dto.TransactionCode, 32),
```

- [ ] **Adım 7: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Services.Bank"`
Expected: PASS.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/BankDescriptionMasker.cs OrderDeck.LicenseServer/Services/Bank/BankHasher.cs OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankDescriptionMaskerTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankHasherTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs
git commit -m "fix(bank-kvkk): havale açıklamasındaki IBAN ve kimlik numarasını maskele

TR IBAN (bitişik harfe rağmen, mod-97 ile doğrulanmış) kısaltılmış
biçime, harfe bitişik olmayan 11 haneli numara yıldıza çevrilerek
kaydedilir; kullanıcı adları ve sözcükler eşleşmez. Test IBAN'ı mod-97
geçerli üretilir (KVKK ön inceleme Ö-16, P0-5).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 5: Ham kayıt beyaz listesi ve 30 günlük süre (P0-4)

**Neden (not Ö-2, Ö-4, karar 5):** ham kayıtta `Bakiye`, `MusteriAciklamasi`, `EkDetay1-4`, `AliciAdi`, `GonderenSube` 90 gün kalıyor; hiçbiri kullanılmıyor. Kara liste (adı IBAN/VKN/TCKN içeren alan) yerine beyaz liste: yalnız açıklama, işlem açıklaması, gönderen adı, gönderen banka, işlem kodu, referans ve makbuz no kalır; açıklama alanları Görev 4'teki maskeleyiciden geçer; iç içe nesne/dizi alınmaz. Süre 90 → 30 gün. **Beyaz liste değişirse politikadaki "bankanın gönderdiği kaydın kopyası" listesi de değişmeli** (sınıf özetine yazılır). İnceleme sonrası (19.10): liste testte kendisiyle değil metin olarak sabit (`BankRawJsonRedactorTests.PolicyFieldList`), 30 günün yapılandırmadan geldiği `BankJobsDiTests.Ham_kayit_saklama_suresi_yapilandirmayla_30_gun`'de sınanır; notun ve avukat paketinin altı alan saymasıyla (işlem açıklaması eksik) çelişki karar 19'da; ilk saklama koşusunun bir kerelik toplu boşaltması P0 tutanağında (Prod adımları).

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankRawJsonRedactor.cs` (bütünüyle)
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankOptions.cs:16`
- Modify: `OrderDeck.LicenseServer/appsettings.json:52`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankRawJsonRedactorTests.cs` (bütünüyle), `ObifinPollJobTests.cs`, `BankDataRetentionJobTests.cs`

- [ ] **Adım 1: Beyaz liste testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Bank/BankRawJsonRedactorTests.cs` içeriğini bütünüyle şununla değiştir:

```csharp
using System.Text.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK Ö-2: ham hareket kaydının yalnız beyaz listedeki alanları saklanır; açıklama alanları maskelenir. Değerler
/// üretilir (repo public).</summary>
public sealed class BankRawJsonRedactorTests
{
    private static string Kept(string redacted)
        => string.Join(",", JsonDocument.Parse(redacted).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

    [Fact]
    public void Yalniz_beyaz_listedeki_alanlar_kalir()
    {
        var iban = BankHasherTests.TestIban();
        var vkn = TestPhone.Digits(10);
        var json = $$"""
        {"Id":"1","HesapId":"6","IslemNo":"X1","Aciklama":"HAVALE ornek_musteri34","IslemAciklama":"EFT","GonderenAdi":"Ad Soyad",
         "GonderenBanka":"Banka","GonderenSube":"Sube","AliciAdi":"Alici","MusteriAciklamasi":"not","EkDetay1":"a","EkDetay2":"b",
         "EkDetay3":"c","EkDetay4":"d","Bakiye":"123.45","KarsiHesapIBAN":"{{iban}}","BorcluVKN":"{{vkn}}","IslemKodu":"FT",
         "ReferansNo":"R1","MakbuzNo":"M1","Tutar":"10.00","TutarEksiArti":"10.00"}
        """;

        var redacted = BankRawJsonRedactor.Redact(json);

        Kept(redacted).Should().Be(string.Join(",", BankRawJsonRedactor.AllowedFields.Order(StringComparer.Ordinal)));
        redacted.Should().NotContain(iban).And.NotContain(vkn).And.NotContain("123.45").And.NotContain("Alici").And.NotContain("Sube");
    }

    [Fact]
    public void Aciklama_alanlarindaki_IBAN_ve_on_bir_haneli_numara_maskelenir()
    {
        var iban = BankHasherTests.TestIban();
        var tckn = TestTckn.NewValid();
        var json = $$"""{"Aciklama":"HAVALE {{iban}}","IslemAciklama":"FAST {{tckn}}","GonderenAdi":"Ad Soyad"}""";

        var root = JsonDocument.Parse(BankRawJsonRedactor.Redact(json)).RootElement;

        root.GetProperty("Aciklama").GetString().Should().Be($"HAVALE {BankHasher.MaskIban(iban)}");
        root.GetProperty("IslemAciklama").GetString().Should().Be($"FAST {BankDescriptionMasker.ElevenDigitMask}");
        root.GetProperty("GonderenAdi").GetString().Should().Be("Ad Soyad");
    }

    [Fact]
    public void Ic_ice_nesne_ve_dizi_alinmaz_null_ve_sayi_aynen_kalir()
    {
        var json = """{"Aciklama":{"x":"y"},"GonderenAdi":["a"],"ReferansNo":null,"MakbuzNo":42}""";

        var root = JsonDocument.Parse(BankRawJsonRedactor.Redact(json)).RootElement;

        root.TryGetProperty("Aciklama", out _).Should().BeFalse();
        root.TryGetProperty("GonderenAdi", out _).Should().BeFalse();
        root.GetProperty("ReferansNo").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("MakbuzNo").GetInt32().Should().Be(42);
    }

    [Fact]
    public void Turkce_karakterler_kacislanmaz_ve_islem_idempotent()
    {
        const string json = """{"Aciklama":"ÖDEME çğışü","GonderenAdi":"Şükrü"}""";

        var once = BankRawJsonRedactor.Redact(json);

        once.Should().Be(json);
        BankRawJsonRedactor.Redact(once).Should().Be(once);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("\"metin\"")]
    public void Json_nesne_degilse_protokol_hatasi(string raw)
    {
        var act = () => BankRawJsonRedactor.Redact(raw);
        act.Should().Throw<ObifinProtocolException>();
    }
}
```

- [ ] **Adım 2: Çekim testini çevir**

`ObifinPollJobTests.cs` içindeki `Ham_json_kimlik_alanlari_redakte_edilerek_saklanir_hash_ve_maske_ham_degerden_uretilir` testinin adını `Ham_kayit_beyaz_listeyle_saklanir_hash_ve_maske_ham_degerden_uretilir` yap; içindeki şu satırı:

```csharp
        tx.RawJson.Should().NotContain(iban).And.NotContain(vkn).And.Contain("[redakte]").And.Contain("Ad Soyad");
```
şununla değiştir:
```csharp
        tx.RawJson.Should().Be("""{"GonderenAdi":"Ad Soyad"}""", "yalnız beyaz listedeki alanlar kalır (KVKK Ö-2)");
```
ve yorum satırını `// KVKK Ö-2: ham kayıt beyaz listeyle saklanır; özet + maske DTO'nun ham değerinden (redaksiyondan ÖNCE) çıkar.` yap.

- [ ] **Adım 3: Saklama testlerini 30 güne çevir**

`BankDataRetentionJobTests.cs`:
- `Ham_json_90_gun_aciklama_180_gun_sonra_bosaltilir_tutar_ve_hash_kalir` → adı `Ham_json_30_gun_aciklama_180_gun_sonra_bosaltilir_tutar_ve_hash_kalir` (gövde aynı: 10/100/200 günlük satırlar yeni kuralda da aynı sonucu verir).
- `Sinirda_90_ve_180_gunun_hemen_icindeki_kalir_hemen_disindaki_bosaltilir` → adı `Sinirda_30_ve_180_gunun_hemen_icindeki_kalir_hemen_disindaki_bosaltilir`; içinde
  `var rawIn = Row(TimeSpan.FromDays(90) - minute); var rawOut = Row(TimeSpan.FromDays(90) + minute);`
  satırını
  `var rawIn = Row(TimeSpan.FromDays(30) - minute); var rawOut = Row(TimeSpan.FromDays(30) + minute);`
  yap; `"90 günün bir dakika içinde"` → `"30 günün bir dakika içinde"`, `"90 günü bir dakika geçti"` → `"30 günü bir dakika geçti"`.

- [ ] **Adım 4: Koştur — kırmızı bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankRawJsonRedactorTests|FullyQualifiedName~ObifinPollJobTests|FullyQualifiedName~BankDataRetentionJobTests"`
Expected: FAIL — `AllowedFields` yok, saklama 90 gün.

- [ ] **Adım 5: Beyaz listeyi yaz**

`OrderDeck.LicenseServer/Services/Bank/BankRawJsonRedactor.cs` içeriğini bütünüyle şununla değiştir:

```csharp
using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Ham hareket kaydını saklamadan önce BEYAZ LİSTEYE indirir (KVKK Ö-2, not karar 5): yalnız <see cref="AllowedFields"/>
/// kalır; bakiye, müşteri açıklaması, ek detaylar, alıcı adı, gönderen şubesi, IBAN/VKN/TCKN ve diğer her alan atılır. Açıklama
/// alanları <see cref="BankDescriptionMasker"/>'dan geçer. İç içe nesne ve dizi alınmaz (içeriği bilinmiyor). Kayıt yalnız
/// teknik arıza incelemesi içindir; saklama süresi <see cref="BankOptions.RawJsonRetentionDays"/>.
/// <para><b>Liste değişirse</b> gizlilik politikasındaki "bankanın gönderdiği kaydın kopyası" alan listesi de (iki kopya:
/// <c>Pages/Public/_GizlilikPolitikasi.cshtml</c>, <c>web/app/(tr)/gizlilik-politikasi/page.tsx</c>) değişmeli.</para>
/// <para>Özet + maske DTO'nun ham değerinden, bu işlemden ÖNCE üretilir. Saf ve idempotent (bank-data-cleanup eski kayıtları
/// bununla yeniden yazar).</para>
/// </summary>
public static class BankRawJsonRedactor
{
    /// <summary>Saklanan alanlar: açıklama, işlem açıklaması, gönderen adı, gönderen banka, işlem kodu, referans ve makbuz no.</summary>
    public static readonly IReadOnlyList<string> AllowedFields =
        ["Aciklama", "IslemAciklama", "GonderenAdi", "GonderenBanka", "IslemKodu", "ReferansNo", "MakbuzNo"];

    private static readonly HashSet<string> Allowed = new(AllowedFields, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DescriptionFields = new(["Aciklama", "IslemAciklama"], StringComparer.OrdinalIgnoreCase);

    // Türkçe karakterler \uXXXX diye kaçışlanmaz: saklanan kopya admin tanısı için insan gözüyle okunur; HTML'e değil DB'ye gider.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>İstemci satırı zaten ayrıştırdı; JSON nesnesi olmayan metin sözleşme ihlalidir →
    /// <see cref="ObifinProtocolException"/> (ham hâliyle sessizce saklanmaz).</summary>
    public static string Redact(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ObifinProtocolException("Ham hareket JSON'u ayrıştırılamadı."); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ObifinProtocolException("Ham hareket JSON'u nesne değil.");
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (!Allowed.Contains(property.Name)) continue;
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
                    writer.WritePropertyName(property.Name);
                    if (property.Value.ValueKind == JsonValueKind.String && DescriptionFields.Contains(property.Name))
                        writer.WriteStringValue(BankDescriptionMasker.Mask(property.Value.GetString()));
                    else
                        property.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }
}
```

- [ ] **Adım 6: Süreyi 30 güne indir**

`Services/Bank/BankOptions.cs` — `public int RawJsonRetentionDays { get; set; } = 90;` satırını şununla değiştir:

```csharp
    /// <summary>Ham kayıt (beyaz listeli, <see cref="BankRawJsonRedactor"/>) saklama süresi — KVKK karar 5: 30 gün.</summary>
    public int RawJsonRetentionDays { get; set; } = 30;
```

`OrderDeck.LicenseServer/appsettings.json` — `"RawJsonRetentionDays": 90,` → `"RawJsonRetentionDays": 30,`

- [ ] **Adım 7: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Services.Bank|FullyQualifiedName~AdminBankaEslestirmePageTests"`
Expected: PASS.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/BankRawJsonRedactor.cs OrderDeck.LicenseServer/Services/Bank/BankOptions.cs OrderDeck.LicenseServer/appsettings.json OrderDeck.LicenseServer.Tests/Services/Bank
git commit -m "fix(bank-kvkk): ham banka kaydını beyaz listeye indir, süreyi 30 güne çek

Ham kayıtta yalnız açıklama, işlem açıklaması, gönderen adı ve bankası,
işlem kodu, referans ve makbuz no kalır; açıklamalar maskelenir; bakiye,
alıcı adı, şube, ek detaylar atılır. Saklama 90 günden 30 güne iner
(KVKK ön inceleme Ö-2, P0-4).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 6: Kısa değerin maskesi açık kalmasın (P0-6)

**Neden (not Ö-6):** `MaskIban` 8 karakterden kısa değeri olduğu gibi döndürüyor; 8–14 karakteri ise `1234…890` biçiminde maskeliyor — 10 haneli bir hesap numarasının 7 hanesi açık kalıyor. Banka IBAN yerine hesap numarası yazarsa "maske" sütununda açık kalır ve "IBAN açık hâliyle saklanmaz" cümlesi yanlışlanır. P0'da en kısa geçerli IBAN uzunluğundan (15) kısa her değer `"…"` döner (bir değerin geldiği görünür, kendisi görünmez); Görev 11 bunu "geçerli IBAN değilse `…`" kuralına sıkılaştırır. Mevcut satırlardaki ham kısa değerleri ve eski biçimli maskeleri Görev 8'deki komut `…`'a çevirir.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankHasher.cs` (`MaskIban`)
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankHasherTests.cs`

- [ ] **Adım 1: Testleri çevir (kırmızı)**

`BankHasherTests.cs` — `Bos_veya_kisa_deger_hash_ve_maske_uretmez` içindeki
`BankHasher.MaskIban("TR12").Should().Be("TR12");`
satırını şununla değiştir:
```csharp
        BankHasher.MaskIban("TR12").Should().Be(BankHasher.ShortValueMask, "kısa değer maske sütununda açık kalmaz (KVKK Ö-6)");
```

`Maske_ham_girdiyi_asla_dondurmez` içindeki
`BankHasher.MaskIban(" tr 12 ").Should().Be("TR12", "kısa değer normalize hâliyle döner");`
satırını şununla değiştir:
```csharp
        BankHasher.MaskIban(" tr 12 ").Should().Be("…", "kısa değer hiçbir karakteriyle görünmez");
        BankHasher.MaskIban(TestPhone.Digits(7)).Should().Be("…", "7 haneli hesap numarası da");
        BankHasher.MaskIban(TestPhone.Digits(10)).Should().Be("…", "10 haneli hesap numarasının 7 hanesi açık kalmaz");
        BankHasher.MaskIban(TestPhone.Digits(14)).Should().Be("…", "en kısa geçerli IBAN 15 karakter");
```

(`using OrderDeck.LicenseServer.Tests.TestHelpers;` Görev 4'te eklendi.)

- [ ] **Adım 2: Koştur — kırmızı**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankHasherTests"`
Expected: FAIL — `ShortValueMask` yok.

- [ ] **Adım 3: Uygula**

`Services/Bank/BankHasher.cs` — `MaskIban` metodunu (özetiyle) şununla değiştir:

```csharp
    /// <summary>IBAN olmayan değerin maskesi: bir değerin geldiği görünür, hiçbir karakteri görünmez (KVKK Ö-6).</summary>
    public const string ShortValueMask = "…";

    /// <summary>En kısa geçerli IBAN uzunluğu (ISO 13616; ör. Norveç 15). Bundan kısa değer IBAN olamaz.</summary>
    public const int MinIbanLength = 15;

    /// <summary>`TR12…345` — yalnız görüntü. Ham girdi ASLA dönmez: boş/boşluk → "", <see cref="MinIbanLength"/>'ten kısa değer
    /// (hesap numarası vb.) → <see cref="ShortValueMask"/>: 10 haneli bir hesap numarasının 7 hanesi "maske" sütununda açık
    /// kalmasın. Görev 11 kuralı "geçerli IBAN değilse <see cref="ShortValueMask"/>"a sıkılaştırır.</summary>
    public static string MaskIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return "";
        var compact = Compact(iban);
        return compact.Length < MinIbanLength ? ShortValueMask : compact[..4] + "…" + compact[^3..];
    }
```

- [ ] **Adım 4: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Services.Bank|FullyQualifiedName~Pages.Admin"`
Expected: PASS. (Maskeyi bir test IBAN'ından ya da `BankHasher.MaskIban`'dan alan testler etkilenmez; kısa sabit bir maskeyi beklenen değer olarak yazan test varsa `ShortValueMask`'a çevir.)

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/BankHasher.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankHasherTests.cs
git commit -m "fix(bank-kvkk): kısa hesap numarasını maske sütununda açık bırakma

MaskIban en kısa geçerli IBAN uzunluğundan (15) kısa değer için ham
değeri ya da 7 karakterini değil '…' döndürür (KVKK ön inceleme Ö-6,
P0-6). Mevcut satırları bank-data-cleanup çevirir (Görev 8).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 7: İlk çekimi bağlantı anına indir (P0-7)

**Neden (not Ö-5):** ilk çekim 90 gün geriye gidiyor; ama bağdaştırma yalnız bağlantıdan sonraki dekont onaylarında koşuyor, yönetim sayfası ve ölçüm son 30 güne bakıyor — geri çekilen veri işleniyor ama sonucu kullanılmıyor (ölçülülük). Not "bağlantı anına (tercih) ya da 30 güne" diyor.

**Karar ve gerekçe:** bağlantı anına, **iki gün payla**. Çapa = bağlantının ilk çekim turunun TR günü − `InitialGraceDays` (2); tur çapayı Obifin'e gitmeden kaydeder, tur sonra yarıda kalsa ya da hiç kayıt yapmadan düşse de çapa kalır (karar 20). İki gün, dekont bağdaştırmasının aday penceresi (`PaymentMatchReconciler.CandidateWindow`, ±2 gün): bağlantıdan hemen önce gönderilmiş ama dekontu bağlantıdan sonra onaylanan havale ölçüme girebilsin; daha eskisinin hiçbir kullanımı yok. 30 gün seçeneği reddedildi: o 30 günün dekontları bağlantıdan önce onaylanmış ve bir daha bağdaştırılmayacak. Çapa bağlantıya yazılır (`InitialCollectFrom`) ve **her** turda uygulanır: ilk çekimde hiç hareket gelmezse imleç boş kalır, sonraki artımlı tur 31 günü geriye sorgular — çapa olmasa bağlantı öncesi 31 gün yine yazılırdı. Kimlik değişince çapa boşalır (yeni hesap, yeni bağlantı anı). Mevcut bağlantıların çapasını ve çapa öncesi kararsız satırlarını Görev 8'deki komut yazar/siler. Metindeki karşılığı ve yayın sırası karar 20'de; P0 ile P1 arasında yeni banka/hesap eklenmemesi Prod adımlarında.

**Files:**
- Modify: `OrderDeck.LicenseServer/Domain/Bank/ObifinConnection.cs`
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs:42, 123-135, UpsertAsync`
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs` (`ResetShadowData`)
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Obifin/Index.cshtml.cs:49-51, 93-101`, `Index.cshtml` (tablo başlığı ve hücre)
- Create: göç `*_ObifinInitialCollectFrom`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs`

- [ ] **Adım 1: İlk çekim testlerini yaz (kırmızı)**

`ObifinPollJobTests.cs` içindeki `Ilk_cekim_90_gunu_31_gunluk_dilimlerle_alir_ve_imleci_kurar` testini bütünüyle şununla değiştir:

```csharp
    [Fact]
    public async Task Ilk_cekim_baglanti_aninin_iki_gun_gerisinden_baslar_capa_yazilir()
    {
        // KVKK Ö-5: 90 günlük geçmiş çekilmez. Çapa = ilk turun TR günü − 2 (dekont bağdaştırmasının aday penceresi).
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        client.Transactions.Add(Tx(100, new DateTime(2026, 7, 1, 10, 0, 0), 50m));    // 86 gün önce
        client.Transactions.Add(Tx(150, new DateTime(2026, 9, 22, 10, 0, 0), 60m));   // çapadan bir gün önce
        client.Transactions.Add(Tx(200, new DateTime(2026, 9, 23, 0, 30, 0), 75m));  // çapa günü

        await job.RunAsync(CancellationToken.None);

        var call = client.Calls.Should().ContainSingle().Subject;
        (call.From, call.To, call.Since).Should().Be((new DateOnly(2026, 9, 23), today, (long?)null));
        (await db.BankTransactions.Select(t => t.ObifinId).ToListAsync()).Should().BeEquivalentTo(new[] { 200L });
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.InitialCollectFrom.Should().Be(new DateOnly(2026, 9, 23));
        conn.BackfillCompletedAt.Should().NotBeNull();
        conn.LastObifinTransactionId.Should().Be(200);
    }

    [Fact]
    public async Task Ilk_cekim_bos_donerse_sonraki_artimli_tur_capadan_eskiyi_yazmaz()
    {
        // İlk turda hareket yoksa imleç boş kalır ve artımlı tur 31 günü geriye sorgular: çapa olmasa bağlantı öncesi yazılırdı.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        await job.RunAsync(CancellationToken.None);
        client.Transactions.Add(Tx(300, new DateTime(2026, 9, 10, 10, 0, 0), 40m));
        client.Transactions.Add(Tx(301, new DateTime(2026, 9, 25, 10, 0, 0), 45m));

        await job.RunAsync(CancellationToken.None);

        client.Calls.Last().Since.Should().BeNull("ilk turda imleç kurulamadı");
        (await db.BankTransactions.Select(t => t.ObifinId).ToListAsync()).Should().BeEquivalentTo(new[] { 301L });
        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).LastObifinTransactionId.Should().Be(301);
    }

    [Fact]
    public async Task Kimlik_degisince_ilk_cekim_capasi_bosalir()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        await job.RunAsync(CancellationToken.None);
        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).InitialCollectFrom.Should().NotBeNull();

        await svc.UpsertAsync(lic, "", $"api-{Guid.NewGuid():N}@x", null, null, CancellationToken.None, allowShadowReset: true);

        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).InitialCollectFrom.Should().BeNull("yeni hesap, yeni bağlantı anı");
    }

    [Fact]
    public void Ilk_cekim_payi_dekont_aday_penceresine_esit()
        => ObifinPollJob.InitialGraceDays.Should().Be((int)PaymentMatchReconciler.CandidateWindow.TotalDays);
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ObifinPollJobTests"`
Expected: FAIL — `InitialCollectFrom`, `InitialGraceDays` yok.

- [ ] **Adım 3: Varlığa çapayı ekle**

`Domain/Bank/ObifinConnection.cs` — `public DateTimeOffset? BackfillCompletedAt { get; set; }` satırının ALTINA:

```csharp
    /// <summary>İlk çekim çapası (TR takvim günü, KVKK Ö-5): ilk çekim turunun günü − <c>ObifinPollJob.InitialGraceDays</c>.
    /// Bu günden eski işlem tarihli hareket hiçbir turda yazılmaz. Kimlik değişince boşalır. Eski bağlantılara
    /// <c>bank-data-cleanup</c> yazar.</summary>
    public DateOnly? InitialCollectFrom { get; set; }
```

(Uygulanan özet ayrıca: çapa turun başında, Obifin'e gitmeden kaydedilir ve tur düşse de kalır; hiçbir turda çapadan eski gün Obifin'den istenmez. Aşağıdaki "Uygulamada ayrıca".)

- [ ] **Adım 4: Çekim işini değiştir**

`Services/Bank/ObifinPollJob.cs` — `public const int BackfillDays = 90;` satırını şununla değiştir:

```csharp
    /// <summary>İlk çekimin bağlantı anından geriye gittiği gün sayısı (KVKK Ö-5). Dekont bağdaştırmasının aday penceresine
    /// (<see cref="PaymentMatchReconciler.CandidateWindow"/>) eşit: bağlantıdan hemen önce gönderilmiş, dekontu sonra onaylanan
    /// havale ölçüme girebilsin; daha eskisinin kullanımı yok.</summary>
    public const int InitialGraceDays = 2;
```

`PollConnectionAsync` içindeki ilk çekim bloğunu:

```csharp
            if (conn.BackfillCompletedAt is null)
            {
                long? maxId = conn.LastObifinTransactionId;
                var start = today.AddDays(-(BackfillDays - 1));
                for (var from = start; from <= today; from = from.AddDays(WindowDays))
```
şununla değiştir:
```csharp
            if (conn.BackfillCompletedAt is null)
            {
                // KVKK Ö-5: geçmiş 90 gün çekilmez. Çapa ilk turda yazılır (yarım kalan tur bir sonrakinde aynı çapayla sürer)
                // ve UpsertAsync'te her turda uygulanır.
                conn.InitialCollectFrom ??= today.AddDays(-InitialGraceDays);
                long? maxId = conn.LastObifinTransactionId;
                var start = conn.InitialCollectFrom.Value;
                for (var from = start; from <= today; from = from.AddDays(WindowDays))
```

`UpsertAsync` içinde Görev 2'de eklenen `if (dto.SignedAmount <= 0) continue;` satırının ALTINA:

```csharp
            // KVKK Ö-5: çapadan eski işlem tarihli hareket yazılmaz (artımlı pencere 31 gün geriye bakar).
            if (conn.InitialCollectFrom is { } collectFrom && DateOnly.FromDateTime(dto.OccurredAtTr) < collectFrom) continue;
```

Uygulamada ayrıca:
- **Çapa turun başında kaydedilir.** `??=` yerine: çapa boşsa yazılır ve `SaveIfIdentityUnchangedAsync` ile Obifin'e gitmeden kaydedilir. Yalnız ilk sayfanın kaydına bırakılsaydı hata yolundaki `ChangeTracker.Clear()` onu atardı: hiç kayıt yapmadan düşen ilk turdan sonra çapa sonraki turun gününden hesaplanır, Obifin ilk bağlantıda iki günden uzun kapalı kaldığında bağlantıdan SONRA gelen havaleler hiç alınmazdı. Test: `Ilk_tur_hic_kayit_yapmadan_duserse_capa_kalir_sonraki_tur_ayni_capadan_surer` (geçici hata, Obifin reddi, beklenmeyen hata).
- **Artımlı pencere** `[bugün−30, bugün]`, yalnız daralır: çapa pencerenin içindeyse çapada başlar (`anchor > from`); başı bugünü aşarsa (saat geri gitti) Obifin'e gidilmez, tur başarılı biter, imleç yerinde kalır (döngü koşulu `from <= today`). İki koşul da şarttır. Biri düşerse pencere 31 günü aşar ya da ters döner, gerçek istemci `ArgumentOutOfRangeException` fırlatır ve çekim her turda düşer. Pencere bugünü aşan çapada `[bugün−30, bugün]` kalsaydı çapadan eski 31 gün istenirdi. Testler: `Eski_capa_artimli_pencereyi_degistirmez` (−60, −31) ve `Saat_geri_gidip_capa_bugunu_asarsa_Obifine_gidilmez_imlec_yerinde_kalir` (ilk çekim ve artımlı yol).
- **Çok dilimli ilk çekim:** çapa 31 günden eskiye düşebilir (ilk tur çapayı yazar, Obifin reddiyle Failed, admin günler sonra aynı kimlikle yeniden doğrular). Dilimler en çok 31 gün ve bitişik. Test: `Capasi_31_gunden_eski_ilk_cekim_bitisik_31_gunluk_dilimlerle_surer`.

Sınıf özetinin ilk paragrafındaki `ilk çekimde 90 gün 31'lik dilimlerle` ifadesini `ilk çekimde bağlantı anından InitialGraceDays gün geriye (çapa)` yap.

- [ ] **Adım 5: Kimlik değişiminde çapayı boşalt**

`Services/Bank/ObifinConnectionService.cs` — `ResetShadowData` içinde `conn.BackfillCompletedAt = null;` satırının ALTINA:

```csharp
        conn.InitialCollectFrom = null;
```

- [ ] **Adım 6: Yönetim sayfasında göster**

`Pages/Admin/Obifin/Index.cshtml.cs` — `ConnectionRow` kaydını:

```csharp
    public sealed record ConnectionRow(Guid LicenseId, string CustomerEmail, string BaseUrl, string UserCode,
        ObifinConnectionStatus Status, string? LastError, DateTimeOffset? LastVerifiedAt, DateTimeOffset? LastPolledAt,
        long? Cursor, DateTimeOffset? BackfillCompletedAt, int BankConnections, int Accounts, int Transactions);
```
şununla değiştir:
```csharp
    public sealed record ConnectionRow(Guid LicenseId, string CustomerEmail, string BaseUrl, string UserCode,
        ObifinConnectionStatus Status, string? LastError, DateTimeOffset? LastVerifiedAt, DateTimeOffset? LastPolledAt,
        long? Cursor, DateTimeOffset? BackfillCompletedAt, DateOnly? InitialCollectFrom, int BankConnections, int Accounts,
        int Transactions);
```

`OnGetAsync`'teki `Select`'te `c.BackfillCompletedAt,` satırını `c.BackfillCompletedAt, c.InitialCollectFrom,` yap.

`Pages/Admin/Obifin/Index.cshtml` — başlıktaki `<th>İlk çekim (90 gün)</th>` → `<th>İlk çekim (çapa)</th>`; hücredeki
`<td>@(r.BackfillCompletedAt?.ToString("yyyy-MM-dd HH:mm") ?? "bekliyor")</td>`
satırını şununla değiştir:
```cshtml
            <td>@(r.BackfillCompletedAt?.ToString("yyyy-MM-dd HH:mm") ?? "bekliyor")@(r.InitialCollectFrom is { } anchor ? $" (≥ {anchor:yyyy-MM-dd})" : "")</td>
```

- [ ] **Adım 7: Göçü üret ve incele**

Run: `cd OrderDeck.LicenseServer && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add ObifinInitialCollectFrom --context LicenseDbContext --output-dir Data/Migrations`
Expected: `Up` yalnız `AddColumn<DateOnly>(name: "InitialCollectFrom", table: "ObifinConnections", type: "date", nullable: true)`.

- [ ] **Adım 8: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Services.Bank|FullyQualifiedName~AdminObifinPageTests"`
Expected: PASS. (`Kaydedilen_hareketler_izleyicide_birikmez…` testi bugünün hareketlerini kullanıyor, çapa içinde; `SeedVerifiedAsync(…, backfilled: true)` kullanan testlerde çapa boş — süzgeç yok.)

Run (Docker): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~GroupCustomersRelationalTests"`
Expected: PASS.

- [ ] **Adım 9: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/Bank/ObifinConnection.cs OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs OrderDeck.LicenseServer/Pages/Admin/Obifin OrderDeck.LicenseServer/Data/Migrations OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs
git commit -m "fix(bank-kvkk): ilk banka çekimini 90 gün yerine bağlantı anına indir

İlk çekim bağlantı anının iki gün gerisinden başlar (dekont aday
penceresi); çapa ObifinConnection.InitialCollectFrom'a yazılır ve her
turda uygulanır, kimlik değişince boşalır (KVKK ön inceleme Ö-5, P0-7).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 8: Mevcut veri için tek seferlik temizlik komutu (`bank-data-cleanup`)

**Neden:** Görev 2–7 yalnız yeni gelen veriyi düzeltir. Prod'daki mevcut satırlar (giden/sıfır hareketler, VKN/TCKN kodları, eski ham kayıtlar, düz IBAN/TCKN içeren açıklamalar, 90 günlük ilk çekimin çapa öncesi satırları) tek seferlik, açıkça tetiklenen bir komutla yeni kurala indirilir. Varsayılan **kuru koşu** (yalnız sayar); `--apply` yazar. Prod'da Burak koşturur (yukarıdaki "Prod adımları"). Kalıp: `group-customers` (katı argüman ayrıştırma, çıkış kodları 0/1/2, çıktıda yalnız sayılar).

Silme kuralları:
- **Giden/sıfır:** eşleşme satırlarıyla birlikte silinir. İnsan kararına bağlı (karar anı, dekont ya da gerçek müşteri dolu) olan atlanır ve sayılır — olması beklenmez (elle eşleme yalnız geleni kabul ediyor), varsa Burak bakar.
- **Çapa öncesi:** çapası olmayan ve ilk çekimi bitmiş bağlantıya çapa = ilk çekimin TR günü − 2 yazılır. Çapadan eski, kararsız, IBAN öğretmemiş (`CustomerIbanMemory.SourceBankTransactionId`) ve bir gap'i çözmemiş (`PaymentMatchGap.ResolvedBankTransactionId`) hareketler çapası olan **her** bağlantıda silinir: komutun yazdığı çapa da, çekim işinin yazdığı çapa da. Neden: P0 anında eski kodun 90 günlük ilk çekimi yarıda kalmış bir bağlantı olabilir (`BackfillCompletedAt` boş, sayfa sayfa kaydedilmiş satırlar var). Yeni kod ilk turda ona çapa yazar (bugün − 2); silme yalnız çapasız bağlantıya bakarsa bu satırlar 180 günlük kurala kadar kalırdı. Çapası da ilk çekimi de olmayan bağlantı (Verified değil, çekim işi ona çapa yazmadı) atlanır; Prod adımlarındaki ön denetim onu sayar. Bağlı olanlar kalır (180 günlük kural onları sonra siler — Görev 10).
- **Eski maskeler (Ö-6):** eski `MaskIban` 8 karakterden kısa değeri ham, 8–14 karakteri `1234…890` biçiminde yazdı. Üç tabloda (`BankTransactions.CounterpartyIbanMasked`, `CustomerIbanMemories.IbanMasked`, `BankAccounts.IbanMasked`) IBAN maskesi biçiminde (`AA00…XXX`) olmayan maske `BankHasher.ShortValueMask` olur; yer tutucu `?` ve boş kalır. (Görev 11'deki `ClearMaskedSourceHashes` yalnız özeti olan satırlara bakar; kısa değerin özeti hiç yoktu — bu adım onları kapsar.)
- Partiler 500'lük; eşzamanlı bir eşleştirme yazısı (`PaymentMatch.UpdatedAt` jetonu) partiyi geri aldırır → o adım biter, çıkış 1, komut yeniden çalıştırılır (idempotent).
- Bağlantının `UpdatedAt`'i BİLEREK değişmez: çekim işinin kimlik denetimi onu karşılaştırır.
- Maskeleme/daraltma adımları oku-değiştir-yaz yapar ve `BankTransaction`'da jeton yok: saklama işi (04:57 UTC), shopper silmesi ya da banka veri talebi silmesi sürerken koşturulmaz (Prod adımları). Komutu Burak gündüz ve elle koşturur; çakışma penceresi bu kuralla kapanır, koşullu güncellemeye gerek kalmaz.
- Her uygulama partisi kayıt kancasını (`RecordBatch`) çağırır: P0'da yalnız günlük (tutanak esastır), Görev 9'dan sonra kalıcı imha kaydı.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Bank/BankDataCleanupJob.cs`
- Create: `OrderDeck.LicenseServer/Tools/BankDataCleanup.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs:62-67` (CLI dağıtımı)
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankDataCleanupJobTests.cs`, `OrderDeck.LicenseServer.Tests/Tools/BankDataCleanupTests.cs`, `OrderDeck.LicenseServer.Tests/Tools/BankDataCleanupRelationalTests.cs` (hepsi YENİ)

- [ ] **Adım 1: İş testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Bank/BankDataCleanupJobTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Tek seferlik KVKK temizliği (bank-data-cleanup): kuru koşu yazmaz, uygulama yeni kurala indirir, ikinci uygulama
/// hiçbir şeyi değiştirmez. Değerler üretilir.</summary>
public sealed class BankDataCleanupJobTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-cleanup-{Guid.NewGuid():N}").Options);

    private static BankDataCleanupJob Job(LicenseDbContext db) => new(db, NullLogger<BankDataCleanupJob>.Instance);

    private static BankTransaction Tx(Guid lic, decimal amount, DateTimeOffset? at = null,
        BankTransactionDirection direction = BankTransactionDirection.Incoming, string? description = null,
        string? rawJson = null, string? taxIdHash = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 6,
        BankaKodu = "qnb", Direction = direction, Amount = amount, Currency = "TL", OccurredAt = at ?? DateTimeOffset.UtcNow,
        FetchedAt = at ?? DateTimeOffset.UtcNow, Description = description, RawJson = rawJson, CounterpartyTaxIdHash = taxIdHash,
    };

    private static PaymentMatch Match(BankTransaction tx, Guid? actual = null, DateTimeOffset? decidedAt = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = tx.LicenseId, BankTransactionId = tx.Id, ActualWpfCustomerId = actual, DecidedAt = decidedAt,
        Status = actual is null ? PaymentMatchStatus.NoProposal : PaymentMatchStatus.ManualOnly,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static string Hex64() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

    [Fact]
    public async Task Kuru_kosu_sayar_hicbir_sey_yazmaz()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var outgoing = Tx(lic, 30m, direction: BankTransactionDirection.Outgoing);
        db.BankTransactions.AddRange(outgoing, Tx(lic, 0m), Tx(lic, 10m, taxIdHash: Hex64()));
        db.PaymentMatches.Add(Match(outgoing));
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(licenseId: null, apply: false, CancellationToken.None);

        r.OutgoingOrZeroDeleted.Should().Be(2);
        r.TaxIdHashesCleared.Should().Be(1);
        (await db.BankTransactions.CountAsync()).Should().Be(3);
        (await db.PaymentMatches.CountAsync()).Should().Be(1);
        (await db.BankTransactions.CountAsync(t => t.CounterpartyTaxIdHash != null)).Should().Be(1);
    }

    [Fact]
    public async Task Uygulama_gideni_ve_sifiri_eslesmesiyle_siler_insan_kararina_bagliyi_atlar()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var outgoing = Tx(lic, 30m, direction: BankTransactionDirection.Outgoing);
        var zero = Tx(lic, 0m);
        var decidedOutgoing = Tx(lic, 15m, direction: BankTransactionDirection.Outgoing);
        var incoming = Tx(lic, 100m);
        db.BankTransactions.AddRange(outgoing, zero, decidedOutgoing, incoming);
        db.PaymentMatches.AddRange(Match(outgoing), Match(decidedOutgoing, actual: Guid.NewGuid(), decidedAt: DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.OutgoingOrZeroDeleted.Should().Be(2);
        r.OutgoingOrZeroSkippedDecided.Should().Be(1);
        r.MatchesDeleted.Should().Be(1);
        (await db.BankTransactions.Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(new[] { decidedOutgoing.Id, incoming.Id });
        (await db.PaymentMatches.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Capa_yazilir_capadan_eski_kararsiz_satir_silinir_bagli_olan_kalir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        // İlk çekim 20.09 12:00 UTC (TR 15:00) → çapa 18.09 (TR); 18.09 00:00 TR = 17.09 21:00 UTC.
        var backfill = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = lic, BaseUrl = "https://example.invalid",
            UserCode = $"u-{Guid.NewGuid():N}@x", Status = ObifinConnectionStatus.Verified, BackfillCompletedAt = backfill,
            CreatedAt = backfill, UpdatedAt = backfill });
        var old = Tx(lic, 10m, new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var justBefore = Tx(lic, 11m, new DateTimeOffset(2026, 9, 17, 20, 59, 0, TimeSpan.Zero));
        var onAnchor = Tx(lic, 12m, new DateTimeOffset(2026, 9, 17, 21, 0, 0, TimeSpan.Zero));
        var oldDecided = Tx(lic, 13m, new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero));
        var oldTaughtIban = Tx(lic, 14m, new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero));
        var oldResolvedGap = Tx(lic, 15m, new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero));
        db.BankTransactions.AddRange(old, justBefore, onAnchor, oldDecided, oldTaughtIban, oldResolvedGap);
        db.PaymentMatches.AddRange(Match(old), Match(oldDecided, actual: Guid.NewGuid(), decidedAt: DateTimeOffset.UtcNow));
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(),
            IbanHash = Hex64(), IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch,
            SourceBankTransactionId = oldTaughtIban.Id, CreatedAt = DateTimeOffset.UtcNow });
        db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow, ResolvedBankTransactionId = oldResolvedGap.Id, ResolvedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.AnchorsSet.Should().Be(1);
        r.PreAnchorDeleted.Should().Be(2);
        r.PreAnchorSkippedLinked.Should().Be(3);
        (await db.BankTransactions.Select(t => t.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { onAnchor.Id, oldDecided.Id, oldTaughtIban.Id, oldResolvedGap.Id });
        var conn = await db.ObifinConnections.SingleAsync();
        conn.InitialCollectFrom.Should().Be(new DateOnly(2026, 9, 18));
        conn.UpdatedAt.Should().Be(backfill, "çekim işinin kimlik denetimi UpdatedAt'e bakar; değişmemeli");
    }

    [Fact]
    public async Task Capasi_olan_her_baglantida_capadan_eski_kararsiz_satir_silinir_capa_degismez()
    {
        // P0 anında eski kodun 90 günlük ilk çekimi yarıda kalmış bağlantı: BackfillCompletedAt boş, sayfa sayfa kaydedilmiş
        // eski satırları var; yeni çekim işi ilk turda çapayı yazdı (18.09). Komut çapa yazmaz ama çapadan eski kararsız
        // satırı siler; ikinci koşu silecek bir şey bulmaz.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = lic, BaseUrl = "https://example.invalid",
            UserCode = $"u-{Guid.NewGuid():N}@x", Status = ObifinConnectionStatus.Verified, BackfillCompletedAt = null,
            InitialCollectFrom = new DateOnly(2026, 9, 18), CreatedAt = created, UpdatedAt = created });
        var old = Tx(lic, 10m, new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        var oldDecided = Tx(lic, 13m, new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero));
        var onAnchor = Tx(lic, 12m, new DateTimeOffset(2026, 9, 17, 21, 0, 0, TimeSpan.Zero)); // 18.09 00:00 TR
        db.BankTransactions.AddRange(old, oldDecided, onAnchor);
        db.PaymentMatches.AddRange(Match(old), Match(oldDecided, actual: Guid.NewGuid(), decidedAt: DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var dry = await Job(db).RunAsync(null, apply: false, CancellationToken.None);
        (dry.AnchorsSet, dry.PreAnchorDeleted, dry.PreAnchorSkippedLinked).Should().Be((0, 1, 1));

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        (r.AnchorsSet, r.PreAnchorDeleted, r.PreAnchorSkippedLinked, r.MatchesDeleted).Should().Be((0, 1, 1, 1));
        (await db.BankTransactions.Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(new[] { oldDecided.Id, onAnchor.Id });
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync();
        conn.InitialCollectFrom.Should().Be(new DateOnly(2026, 9, 18));
        conn.BackfillCompletedAt.Should().BeNull();
        conn.UpdatedAt.Should().Be(created);
        (await Job(db).RunAsync(null, apply: true, CancellationToken.None)).PreAnchorDeleted.Should().Be(0);
    }

    [Fact]
    public async Task Vkn_kodu_bosaltilir_ham_kayit_daraltilir_aciklama_maskelenir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var iban = BankHasherTests.TestIban();
        var tckn = TestTckn.NewValid();
        var tx = Tx(lic, 10m, description: $"HAVALE {iban} {tckn}", taxIdHash: Hex64(),
            rawJson: $$"""{"Aciklama":"HAVALE {{iban}}","KarsiHesapIBAN":"[redakte]","Bakiye":"10.00","GonderenAdi":"Ad Soyad"}""");
        var broken = Tx(lic, 11m, rawJson: "{bozuk");
        db.BankTransactions.AddRange(tx, broken);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.TaxIdHashesCleared.Should().Be(1);
        r.RawJsonRewritten.Should().Be(1);
        r.RawJsonUnreadableCleared.Should().Be(1);
        r.DescriptionsMasked.Should().Be(1);
        var row = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == tx.Id);
        row.CounterpartyTaxIdHash.Should().BeNull();
        row.RawJson.Should().Be($$"""{"Aciklama":"HAVALE {{BankHasher.MaskIban(iban)}}","GonderenAdi":"Ad Soyad"}""");
        row.Description.Should().Be($"HAVALE {BankHasher.MaskIban(iban)} {BankDescriptionMasker.ElevenDigitMask}");
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == broken.Id)).RawJson.Should().BeNull();
    }

    [Fact]
    public async Task Kisa_ya_da_eski_bicimli_maske_ShortValueMask_olur_IBAN_maskesi_ve_yer_tutucu_kalir()
    {
        // KVKK Ö-6: eski MaskIban kısa değeri ham, 8–14 karakteri "1234…890" yazdı (10 hanenin 7'si açık).
        using var db = NewDb(); var lic = Guid.NewGuid();
        var account10 = TestPhone.Digits(10);
        var oldFormat = account10[..4] + "…" + account10[^3..];
        var shortRaw = Tx(lic, 10m); shortRaw.CounterpartyIbanMasked = TestPhone.Digits(7);
        var oldMasked = Tx(lic, 11m); oldMasked.CounterpartyIbanMasked = oldFormat;
        var ibanMasked = Tx(lic, 12m); ibanMasked.CounterpartyIbanMasked = BankHasher.MaskIban(BankHasherTests.TestIban());
        db.BankTransactions.AddRange(shortRaw, oldMasked, ibanMasked);
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(),
            IbanHash = Hex64(), IbanMasked = oldFormat, LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = DateTimeOffset.UtcNow });
        var placeholder = new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 1, BankaKodu = "qnb",
            IbanMasked = "?", Currency = "TL", RefreshedAt = DateTimeOffset.UtcNow };
        var oldAccount = new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 2, BankaKodu = "qnb",
            IbanMasked = oldFormat, Currency = "TL", RefreshedAt = DateTimeOffset.UtcNow };
        db.BankAccounts.AddRange(placeholder, oldAccount);
        await db.SaveChangesAsync();

        var dry = await Job(db).RunAsync(null, apply: false, CancellationToken.None);
        dry.ShortMasksReplaced.Should().Be(4);
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == shortRaw.Id)).CounterpartyIbanMasked
            .Should().HaveLength(7, "kuru koşu yazmaz");

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.ShortMasksReplaced.Should().Be(4);
        var masks = await db.BankTransactions.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.CounterpartyIbanMasked);
        masks[shortRaw.Id].Should().Be(BankHasher.ShortValueMask);
        masks[oldMasked.Id].Should().Be(BankHasher.ShortValueMask);
        masks[ibanMasked.Id].Should().Be(ibanMasked.CounterpartyIbanMasked, "IBAN maskesi biçimi kalır");
        (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).IbanMasked.Should().Be(BankHasher.ShortValueMask);
        (await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == placeholder.Id)).IbanMasked.Should().Be("?");
        (await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == oldAccount.Id)).IbanMasked.Should().Be(BankHasher.ShortValueMask);
    }

    [Theory]
    [InlineData("TR12…345", false)]
    [InlineData("…", false)]
    [InlineData("?", false)]
    [InlineData(null, false)]
    [InlineData("1234…890", true)]
    [InlineData("TR12", true)]
    [InlineData("1234567", true)]
    public void IBAN_maskesi_bicimi_disindaki_maske_yeniden_maskelenir(string? mask, bool expected)
        => BankDataCleanupJob.NeedsRemask(mask).Should().Be(expected);

    [Fact]
    public async Task Ikinci_uygulama_hicbir_satiri_degistirmez()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var withShortMask = Tx(lic, 12m); withShortMask.CounterpartyIbanMasked = TestPhone.Digits(7);
        db.BankTransactions.AddRange(
            Tx(lic, 30m, direction: BankTransactionDirection.Outgoing),
            Tx(lic, 10m, description: $"EFT {TestTckn.NewValid()}", rawJson: """{"Aciklama":"x","Bakiye":"1"}""", taxIdHash: Hex64()),
            withShortMask);
        await db.SaveChangesAsync();
        await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        (r.OutgoingOrZeroDeleted, r.TaxIdHashesCleared, r.RawJsonRewritten, r.DescriptionsMasked, r.PreAnchorDeleted, r.AnchorsSet,
            r.ShortMasksReplaced).Should().Be((0, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task Lisans_verilirse_yalniz_o_lisans_islenir()
    {
        using var db = NewDb(); var target = Guid.NewGuid(); var other = Guid.NewGuid();
        db.BankTransactions.AddRange(Tx(target, 0m), Tx(other, 0m));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(target, apply: true, CancellationToken.None);

        (await db.BankTransactions.Select(t => t.LicenseId).ToListAsync()).Should().BeEquivalentTo(new[] { other });
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataCleanupJobTests"`
Expected: FAIL — `BankDataCleanupJob` yok.

- [ ] **Adım 3: İşi yaz**

`OrderDeck.LicenseServer/Services/Bank/BankDataCleanupJob.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// KVKK düzeltmelerinden (2026-10-19 hukuki ön inceleme §5.1) önce yazılmış banka verisini yeni kurallara indirir. Tek
/// seferlik; <c>bank-data-cleanup</c> komutu çağırır. Kuru koşu aynı sayıları hesaplar, hiçbir şey yazmaz.
/// <list type="number">
/// <item>Giden ve sıfır tutarlı hareketler eşleşme satırlarıyla silinir; insan kararına bağlı olan atlanır ve sayılır.</item>
/// <item>Çapası olmayan, ilk çekimi bitmiş bağlantıya çapa (<see cref="AnchorFor"/>) yazılır; çapası olan HER bağlantıda
/// (çekim işinin yazdığı dahil) çapadan eski, kararsız, IBAN öğretmemiş ve gap çözmemiş hareketler silinir.</item>
/// <item><c>CounterpartyTaxIdHash</c> boşaltılır.</item>
/// <item>Ham kayıt beyaz listeye indirilir (<see cref="BankRawJsonRedactor"/>); okunamayan ham kayıt boşaltılır.</item>
/// <item>Açıklamadaki IBAN ve 11 haneli numara maskelenir (<see cref="BankDescriptionMasker"/>).</item>
/// <item>IBAN maskesi biçiminde olmayan eski maske (ham kısa değer, <c>1234…890</c>) <see cref="BankHasher.ShortValueMask"/>
/// olur (<see cref="NeedsRemask"/>; KVKK Ö-6).</item>
/// </list>
/// Yeniden koşu idempotent. Partiler <see cref="BatchSize"/>'lık; eşzamanlı bir eşleştirme yazısıyla çakışan silme partisi geri
/// alınır, <see cref="Report.FailedBatches"/> artar ve o adım biter — komut yeniden çalıştırılır. Maskeleme adımları
/// oku-değiştir-yaz yapar (<c>BankTransaction</c>'da jeton yok): saklama işi ya da bir silme sürerken koşturulmaz. Bağlantının
/// <c>UpdatedAt</c>'i BİLEREK değişmez (çekim işinin kimlik denetimi). Günlüğe yalnız sayılar yazılır.
/// </summary>
public sealed class BankDataCleanupJob
{
    public const int BatchSize = 500;

    public sealed class Report
    {
        public int OutgoingOrZeroDeleted { get; set; }
        public int OutgoingOrZeroSkippedDecided { get; set; }
        public int AnchorsSet { get; set; }
        public int PreAnchorDeleted { get; set; }
        public int PreAnchorSkippedLinked { get; set; }
        public int MatchesDeleted { get; set; }
        public int TaxIdHashesCleared { get; set; }
        public int RawJsonRewritten { get; set; }
        public int RawJsonUnreadableCleared { get; set; }
        public int DescriptionsMasked { get; set; }
        public int ShortMasksReplaced { get; set; }
        public int FailedBatches { get; set; }
    }

    private readonly LicenseDbContext _db;
    private readonly ILogger<BankDataCleanupJob> _log;

    public BankDataCleanupJob(LicenseDbContext db, ILogger<BankDataCleanupJob> log) { _db = db; _log = log; }

    public async Task<Report> RunAsync(Guid? licenseId, bool apply, CancellationToken ct)
    {
        var r = new Report();
        await DeleteOutgoingOrZeroAsync(licenseId, apply, r, ct);
        await ApplyInitialAnchorAsync(licenseId, apply, r, ct);
        await ClearTaxIdHashesAsync(licenseId, apply, r, ct);
        await RewriteRawJsonAsync(licenseId, apply, r, ct);
        await MaskDescriptionsAsync(licenseId, apply, r, ct);
        await RemaskShortValuesAsync(licenseId, apply, r, ct);
        _log.LogInformation("bank-data-cleanup ({Mode}): giden/sıfır={Outgoing} (atlanan {Skipped}), çapa={Anchors}, " +
            "çapa öncesi={PreAnchor} (bağlı {Linked}), eşleşme={Matches}, VKN kodu={TaxIds}, ham kayıt={Raw} (okunamayan {Unreadable}), " +
            "açıklama={Descriptions}, kısa maske={ShortMasks}, düşen parti={Failed}", apply ? "uygula" : "kuru", r.OutgoingOrZeroDeleted,
            r.OutgoingOrZeroSkippedDecided, r.AnchorsSet, r.PreAnchorDeleted, r.PreAnchorSkippedLinked, r.MatchesDeleted,
            r.TaxIdHashesCleared, r.RawJsonRewritten, r.RawJsonUnreadableCleared, r.DescriptionsMasked, r.ShortMasksReplaced,
            r.FailedBatches);
        return r;
    }

    // İmha kaydı eylemleri (Görev 9'daki DataDestruction.Actions ile aynı değerler; Görev 9 onlara bağlar).
    private const string Deleted = "deleted", Nulled = "nulled", Masked = "masked";

    /// <summary>Bir UYGULAMA partisinin imha kaydı (KVKK Ö-13): partinin SaveChanges'inden hemen önce çağrılır, kayıt silmeyle
    /// aynı işlemde yazılır. Kuru koşuda çağrılmaz. P0'da kalıcı imha tablosu yok: yalnız günlüğe (sayılar) yazar, P0 koşusu
    /// tutanakla kayda geçer (Prod adımları); Görev 9 gövdeyi <c>DataDestruction.Record</c>'a bağlar (tetikleyici
    /// <c>cleanup</c>, yapan <c>cli</c>).</summary>
    private void RecordBatch(Guid? licenseId, string target, string action, int count, string criterion)
    {
        if (count > 0)
            _log.LogDebug("bank-data-cleanup partisi: {Target} {Action} {Count} ({Criterion}), lisans={LicenseId}",
                target, action, count, criterion, licenseId);
    }

    /// <summary>İlk çekimin TR takvim günü − <see cref="ObifinPollJob.InitialGraceDays"/> (çekim işiyle aynı kural).</summary>
    public static DateOnly AnchorFor(DateTimeOffset backfillCompletedAt)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(backfillCompletedAt, ObifinConnectionService.TrZone).DateTime)
            .AddDays(-ObifinPollJob.InitialGraceDays);

    private IQueryable<BankTransaction> Transactions(Guid? licenseId)
        => licenseId is { } l ? _db.BankTransactions.Where(t => t.LicenseId == l) : _db.BankTransactions;

    /// <summary>İnsan kararına bağlı hareketler: karar anı, dekont ya da gerçek müşteri dolu.</summary>
    private IQueryable<Guid> DecidedTransactionIds()
        => _db.PaymentMatches.Where(m => m.DecidedAt != null || m.PaymentId != null || m.ActualWpfCustomerId != null)
            .Select(m => m.BankTransactionId);

    private async Task DeleteOutgoingOrZeroAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var decided = DecidedTransactionIds();
        var candidates = Transactions(licenseId)
            .Where(t => t.Direction == BankTransactionDirection.Outgoing || t.Amount == 0);
        r.OutgoingOrZeroSkippedDecided = await candidates.CountAsync(t => decided.Contains(t.Id), ct);
        var deletable = candidates.Where(t => !decided.Contains(t.Id));
        if (!apply)
        {
            r.OutgoingOrZeroDeleted = await deletable.CountAsync(ct);
            return;
        }
        var (deleted, matches, completed) = await DeleteInBatchesAsync(deletable, licenseId, "giden ya da sıfır tutarlı (K-4)", ct);
        r.OutgoingOrZeroDeleted = deleted;
        r.MatchesDeleted += matches;
        if (!completed) r.FailedBatches++;
    }

    /// <summary>Çapa yalnız çapası olmayan, ilk çekimi bitmiş bağlantıya (eski kodun 90 günlük ilk çekimi) yazılır. Çapadan eski
    /// kararsız hareketler ise çapası olan HER bağlantıda silinir: P0 anında eski kodun ilk çekimi yarıda kalmış bir bağlantıya
    /// yeni çekim işi ilk turda çapa yazar (<c>BackfillCompletedAt</c> o an boş); sayfa sayfa kaydedilmiş eski satırları yalnız
    /// bu adım siler. Yeniden koşu idempotent: çapa yazılmıştır, silinecek satır kalmamıştır.</summary>
    private async Task ApplyInitialAnchorAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var connections = _db.ObifinConnections.AsNoTracking()
            .Where(c => c.InitialCollectFrom != null || c.BackfillCompletedAt != null);
        if (licenseId is { } l) connections = connections.Where(c => c.LicenseId == l);
        foreach (var c in await connections.Select(c => new { c.Id, c.LicenseId, c.InitialCollectFrom, c.BackfillCompletedAt })
                     .ToListAsync(ct))
        {
            var anchor = c.InitialCollectFrom ?? AnchorFor(c.BackfillCompletedAt!.Value);
            if (c.InitialCollectFrom is null)
            {
                r.AnchorsSet++;
                if (apply)
                {
                    // Önce çapa: silme partisi geri alınırsa yeniden koşu bu bağlantıyı yazılmış çapasıyla yeniden dener.
                    var conn = await _db.ObifinConnections.SingleAsync(x => x.Id == c.Id, ct);
                    conn.InitialCollectFrom = anchor; // UpdatedAt BİLEREK değişmez (sınıf özeti)
                    await _db.SaveChangesAsync(ct);
                    _db.ChangeTracker.Clear();
                }
            }
            var anchorUtc = ObifinConnectionService.TrToUtc(anchor.ToDateTime(TimeOnly.MinValue));
            var decided = DecidedTransactionIds();
            var taught = _db.CustomerIbanMemories.Where(m => m.SourceBankTransactionId != null)
                .Select(m => m.SourceBankTransactionId!.Value);
            var resolved = _db.PaymentMatchGaps.Where(g => g.ResolvedBankTransactionId != null)
                .Select(g => g.ResolvedBankTransactionId!.Value);
            var before = _db.BankTransactions.Where(t => t.LicenseId == c.LicenseId && t.OccurredAt < anchorUtc);
            var deletable = before.Where(t => !decided.Contains(t.Id) && !taught.Contains(t.Id) && !resolved.Contains(t.Id));
            var deletableCount = await deletable.CountAsync(ct);
            r.PreAnchorSkippedLinked += await before.CountAsync(ct) - deletableCount;
            if (!apply)
            {
                r.PreAnchorDeleted += deletableCount;
                continue;
            }
            var (deleted, matches, completed) = await DeleteInBatchesAsync(deletable, c.LicenseId, "ilk çekim çapasından eski (Ö-5)", ct);
            r.PreAnchorDeleted += deleted;
            r.MatchesDeleted += matches;
            if (!completed) r.FailedBatches++; // çapa yazıldı: yeniden koşu aynı bağlantının kalanını siler
        }
    }

    /// <summary>Sorgunun seçtiği hareketleri eşleşme satırlarıyla partiler hâlinde siler. Silinen satır sorgudan düşer: her
    /// parti baştan sorgulanır. Eşzamanlı eşleştirme yazısı (jeton) partiyi geri aldırır: Completed = false.</summary>
    private async Task<(int Transactions, int Matches, bool Completed)> DeleteInBatchesAsync(
        IQueryable<BankTransaction> query, Guid? licenseId, string criterion, CancellationToken ct)
    {
        int transactions = 0, matches = 0;
        while (true)
        {
            var batch = await query.OrderBy(t => t.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return (transactions, matches, true);
            var ids = batch.Select(t => t.Id).ToList();
            var rows = await _db.PaymentMatches.Where(m => ids.Contains(m.BankTransactionId)).ToListAsync(ct);
            _db.PaymentMatches.RemoveRange(rows);
            _db.BankTransactions.RemoveRange(batch);
            RecordBatch(licenseId, "BankTransactions", Deleted, batch.Count, criterion);
            RecordBatch(licenseId, "PaymentMatches", Deleted, rows.Count, criterion);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
                return (transactions, matches, false);
            }
            _db.ChangeTracker.Clear();
            transactions += batch.Count;
            matches += rows.Count;
            if (batch.Count < BatchSize) return (transactions, matches, true);
        }
    }

    private async Task ClearTaxIdHashesAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var query = Transactions(licenseId).Where(t => t.CounterpartyTaxIdHash != null);
        if (!apply)
        {
            r.TaxIdHashesCleared = await query.CountAsync(ct);
            return;
        }
        while (true)
        {
            var batch = await query.OrderBy(t => t.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return;
            foreach (var t in batch) t.CounterpartyTaxIdHash = null;
            RecordBatch(licenseId, "BankTransactions.CounterpartyTaxIdHash", Nulled, batch.Count, "VKN/TCKN kodu amaçsız (Ö-2)");
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            r.TaxIdHashesCleared += batch.Count;
            if (batch.Count < BatchSize) return;
        }
    }

    /// <summary>Kimlik anlık görüntüsüyle gezilir: yeniden yazılan satır sorgudan düşmediği için "baştan sorgula" kalıbı
    /// burada döngüye girerdi.</summary>
    private async Task RewriteRawJsonAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var ids = await Transactions(licenseId).Where(t => t.RawJson != null).OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(ct);
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var rows = await _db.BankTransactions.Where(t => chunk.Contains(t.Id) && t.RawJson != null).ToListAsync(ct);
            int rewrittenHere = 0, unreadableHere = 0;
            foreach (var t in rows)
            {
                string? rewritten;
                try
                {
                    rewritten = BankRawJsonRedactor.Redact(t.RawJson!);
                }
                catch (ObifinProtocolException)
                {
                    // Okunamayan ham kayıt tanıya da yaramaz: boşaltılır.
                    unreadableHere++;
                    t.RawJson = null;
                    continue;
                }
                if (rewritten == t.RawJson) continue;
                rewrittenHere++;
                t.RawJson = rewritten;
            }
            r.RawJsonRewritten += rewrittenHere;
            r.RawJsonUnreadableCleared += unreadableHere;
            if (apply)
            {
                RecordBatch(licenseId, "BankTransactions.RawJson", Masked, rewrittenHere, "ham kayıt beyaz listesi (Ö-2)");
                RecordBatch(licenseId, "BankTransactions.RawJson", Nulled, unreadableHere, "okunamayan ham kayıt");
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();
        }
    }

    private async Task MaskDescriptionsAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var ids = await Transactions(licenseId).Where(t => t.Description != null).OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(ct);
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var rows = await _db.BankTransactions.Where(t => chunk.Contains(t.Id) && t.Description != null).ToListAsync(ct);
            var maskedHere = 0;
            foreach (var t in rows)
            {
                var masked = BankDescriptionMasker.Mask(t.Description);
                if (masked == t.Description) continue;
                maskedHere++;
                t.Description = masked;
            }
            r.DescriptionsMasked += maskedHere;
            if (apply)
            {
                RecordBatch(licenseId, "BankTransactions.Description", Masked, maskedHere, "açıklamada IBAN/11 haneli numara (Ö-16)");
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>Saklı maske IBAN maskesi biçiminde (<c>AA00…XXX</c>, <see cref="BankHasher.MaskIban"/>) değil mi? Eski
    /// <c>MaskIban</c>'ın ham kısa değeri ve <c>1234…890</c> biçimi evet. Yer tutucu "?", boş ve
    /// <see cref="BankHasher.ShortValueMask"/> hayır (dokunulmaz).</summary>
    public static bool NeedsRemask(string? masked)
    {
        if (string.IsNullOrEmpty(masked) || masked == "?" || masked == BankHasher.ShortValueMask) return false;
        var parts = masked.Split('…');
        var ibanStyle = parts.Length == 2 && parts[0].Length == 4 && parts[1].Length == 3
            && char.IsAsciiLetterUpper(parts[0][0]) && char.IsAsciiLetterUpper(parts[0][1])
            && char.IsAsciiDigit(parts[0][2]) && char.IsAsciiDigit(parts[0][3])
            && parts[1].All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));
        return !ibanStyle;
    }

    /// <summary>KVKK Ö-6: üç tablodaki eski biçimli ya da kısa maskeyi <see cref="BankHasher.ShortValueMask"/>'a çevirir. Maske
    /// SQL'de süzülemediği için hareketler kimlik anlık görüntüsüyle partiler hâlinde, IBAN hafızası ve hesaplar (küçük tablolar)
    /// tek seferde gezilir.</summary>
    private async Task RemaskShortValuesAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        const string criterion = "eski kısa ya da hesap numarası maskesi (Ö-6)";
        var ids = await Transactions(licenseId).Where(t => t.CounterpartyIbanMasked != null).OrderBy(t => t.Id)
            .Select(t => t.Id).ToListAsync(ct);
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var rows = await _db.BankTransactions.Where(t => chunk.Contains(t.Id)).ToListAsync(ct);
            var changed = 0;
            foreach (var t in rows.Where(t => NeedsRemask(t.CounterpartyIbanMasked)))
            {
                t.CounterpartyIbanMasked = BankHasher.ShortValueMask;
                changed++;
            }
            r.ShortMasksReplaced += changed;
            if (apply && changed > 0)
            {
                RecordBatch(licenseId, "BankTransactions.CounterpartyIbanMasked", Masked, changed, criterion);
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();
        }

        var memories = await (licenseId is { } ml ? _db.CustomerIbanMemories.Where(m => m.LicenseId == ml) : _db.CustomerIbanMemories)
            .ToListAsync(ct);
        var accounts = await (licenseId is { } al ? _db.BankAccounts.Where(a => a.LicenseId == al) : _db.BankAccounts)
            .ToListAsync(ct);
        var memoriesChanged = 0;
        foreach (var m in memories.Where(m => NeedsRemask(m.IbanMasked)))
        {
            m.IbanMasked = BankHasher.ShortValueMask;
            memoriesChanged++;
        }
        var accountsChanged = 0;
        foreach (var a in accounts.Where(a => NeedsRemask(a.IbanMasked)))
        {
            a.IbanMasked = BankHasher.ShortValueMask;
            accountsChanged++;
        }
        r.ShortMasksReplaced += memoriesChanged + accountsChanged;
        if (apply && memoriesChanged + accountsChanged > 0)
        {
            RecordBatch(licenseId, "CustomerIbanMemories.IbanMasked", Masked, memoriesChanged, criterion);
            RecordBatch(licenseId, "BankAccounts.IbanMasked", Masked, accountsChanged, criterion);
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
    }
}
```

- [ ] **Adım 4: Koştur — iş yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataCleanupJobTests"`
Expected: PASS.

- [ ] **Adım 5: Komut ayrıştırma testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Tools/BankDataCleanupTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>bank-data-cleanup argümanları katı (group-customers kalıbı): yazım hatalı --apply kuru koşuya dönüşmez,
/// tanınmayan ya da yinelenen argüman reddedilir.</summary>
public sealed class BankDataCleanupTests
{
    [Fact]
    public void Argumansiz_kuru_kosu_butun_lisanslar()
    {
        BankDataCleanup.TryParseArgs(["bank-data-cleanup"], out var o).Should().BeTrue();
        o.Should().Be(new BankDataCleanup.Options(License: null, Apply: false));
    }

    [Fact]
    public void Lisans_ve_apply_kabul_edilir()
    {
        var license = Guid.NewGuid();
        BankDataCleanup.TryParseArgs(["bank-data-cleanup", "--license", license.ToString(), "--apply"], out var o).Should().BeTrue();
        o.Should().Be(new BankDataCleanup.Options(license, Apply: true));
    }

    [Theory]
    [InlineData("--aply")]
    [InlineData("--license")]
    [InlineData("--license", "x")]
    [InlineData("--apply", "--apply")]
    [InlineData("--license", "00000000-0000-0000-0000-000000000001", "--license", "00000000-0000-0000-0000-000000000002")]
    public void Bozuk_arguman_reddedilir(params string[] rest)
        => BankDataCleanup.TryParseArgs(["bank-data-cleanup", .. rest], out _).Should().BeFalse();
}
```

- [ ] **Adım 6: Komutu yaz**

`OrderDeck.LicenseServer/Tools/BankDataCleanup.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Bank;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll bank-data-cleanup [--license &lt;guid&gt;] [--apply]
/// KVKK düzeltmelerinden önce yazılmış banka verisini yeni kurallara indirir; kurallar <see cref="BankDataCleanupJob"/>'ta.
/// --apply yoksa kuru koşu: aynı sayıları hesaplar, hiçbir şey yazmaz. Önce kuru koşu, çıktı tutanağa. Saklama işi (04:57 UTC),
/// shopper silmesi ya da banka veri talebi silmesi sürerken koşturulmaz: maskeleme adımları oku-değiştir-yaz yapar, arada
/// boşaltılan metni eski hâliyle geri yazabilirdi.
/// <para>Çıkış kodları: 0 tamam; 1 bazı partiler eşzamanlı bir eşleştirme yazısı yüzünden geri alındı — yeniden çalıştır
/// (idempotent); 2 kullanım/yapılandırma.</para>
/// <para>Argümanlar katı (<see cref="GroupCustomers"/> kalıbı): tanınmayan, yinelenen ya da değeri eksik argüman → 2,
/// veritabanına dokunmadan. Çıktıda kişisel veri yok — yalnız sayılar ve lisans kimliği.</para>
/// </summary>
public static class BankDataCleanup
{
    private const string Usage = "Kullanım: bank-data-cleanup [--license <guid>] [--apply]";

    public sealed record Options(Guid? License, bool Apply);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out var options))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var conn = config.GetConnectionString("LicenseDb");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.Error.WriteLine("ConnectionStrings:LicenseDb bulunamadı.");
            return 2;
        }
        var dbOptions = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(conn).Options;
        await using var db = new LicenseDbContext(dbOptions);
        using var logs = LoggerFactory.Create(b => b.AddSimpleConsole());
        return await RunAsync(db, options, Console.Out, logs, CancellationToken.None);
    }

    /// <summary>Komutun gövdesi; testler gerçek SQL Server'a buradan girer.</summary>
    public static async Task<int> RunAsync(LicenseDbContext db, Options options, TextWriter output, ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var job = new BankDataCleanupJob(db, loggerFactory.CreateLogger<BankDataCleanupJob>());
        var r = await job.RunAsync(options.License, options.Apply, ct);
        output.WriteLine($"{(options.Apply ? "UYGULANDI" : "KURU ÇALIŞTIRMA")} — lisans={options.License?.ToString() ?? "hepsi"}: {Format(r)}");
        if (r.FailedBatches > 0)
        {
            output.WriteLine($"{r.FailedBatches} adım eşzamanlı bir eşleştirme yazısı yüzünden yarıda kaldı; komutu yeniden çalıştırın (idempotent).");
            return 1;
        }
        return 0;
    }

    /// <summary>args[0] komut adıdır. <c>public</c>: sunucu projesinde InternalsVisibleTo yok (GroupCustomers kalıbı).</summary>
    public static bool TryParseArgs(string[] args, out Options options)
    {
        Guid? license = null;
        var apply = false;
        options = new Options(null, false);
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply" when !apply:
                    apply = true;
                    break;
                case "--license" when license is null:
                    if (i + 1 >= args.Length || !Guid.TryParse(args[i + 1], out var parsed)) return false;
                    license = parsed;
                    i++;
                    break;
                default:
                    return false;
            }
        }
        options = new Options(license, apply);
        return true;
    }

    public static string Format(BankDataCleanupJob.Report r)
        => $"giden/sıfır silinen={r.OutgoingOrZeroDeleted} (kararlı atlanan={r.OutgoingOrZeroSkippedDecided}) | " +
           $"çapa yazılan={r.AnchorsSet} çapa öncesi silinen={r.PreAnchorDeleted} (bağlı kalan={r.PreAnchorSkippedLinked}) | " +
           $"eşleşme silinen={r.MatchesDeleted} | VKN/TCKN kodu boşaltılan={r.TaxIdHashesCleared} | " +
           $"ham kayıt daraltılan={r.RawJsonRewritten} (okunamayan boşaltılan={r.RawJsonUnreadableCleared}) | " +
           $"açıklama maskelenen={r.DescriptionsMasked} | kısa maske çevrilen={r.ShortMasksReplaced} | düşen parti={r.FailedBatches}";
}
```

- [ ] **Adım 7: Program.cs'e bağla**

`Program.cs` — `if (args.Length > 0 && args[0] == "sales-sync")` bloğunun ALTINA:

```csharp
        if (args.Length > 0 && args[0] == "bank-data-cleanup")
        {
            var exit = await OrderDeck.LicenseServer.Tools.BankDataCleanup.RunAsync(args);
            Environment.Exit(exit);
            return;
        }
```

- [ ] **Adım 8: Gerçek SQL Server testini yaz**

`OrderDeck.LicenseServer.Tests/Tools/BankDataCleanupRelationalTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>bank-data-cleanup gerçek SQL Server'da (göçlerle kurulmuş şema, FK'ler, CHECK kısıtları): kuru koşu yazmaz,
/// uygulama gideni eşleşmesiyle siler; çıktıda kişisel veri yok.</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankDataCleanupRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    public BankDataCleanupRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Kuru_kosu_yazmaz_uygulama_gideni_eslesmesiyle_siler()
    {
        Guid outgoingId, incomingId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = new Customer { Id = Guid.NewGuid(), Email = $"bc-{Guid.NewGuid():N}@example.test", Name = "Temizlik",
                PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow };
            var license = new License { Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = "bc-" + Guid.NewGuid().ToString("N")[..12],
                SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddYears(1) };
            db.Customers.Add(customer); db.Licenses.Add(license);
            BankTransaction Tx(BankTransactionDirection d, decimal amount) => new()
            {
                Id = Guid.NewGuid(), LicenseId = license.Id, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 6,
                BankaKodu = "qnb", Direction = d, Amount = amount, Currency = "TL", OccurredAt = DateTimeOffset.UtcNow,
                FetchedAt = DateTimeOffset.UtcNow,
            };
            var outgoing = Tx(BankTransactionDirection.Outgoing, 30m);
            var incoming = Tx(BankTransactionDirection.Incoming, 100m);
            db.BankTransactions.AddRange(outgoing, incoming);
            db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = license.Id, BankTransactionId = outgoing.Id,
                Status = PaymentMatchStatus.NoProposal, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            (outgoingId, incomingId) = (outgoing.Id, incoming.Id);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var dry = new StringWriter();
            (await BankDataCleanup.RunAsync(db, new BankDataCleanup.Options(null, Apply: false), dry, NullLoggerFactory.Instance,
                CancellationToken.None)).Should().Be(0);
            dry.ToString().Should().StartWith("KURU ÇALIŞTIRMA").And.Contain("giden/sıfır silinen=1");
            (await db.BankTransactions.CountAsync()).Should().Be(2);

            var applied = new StringWriter();
            (await BankDataCleanup.RunAsync(db, new BankDataCleanup.Options(null, Apply: true), applied, NullLoggerFactory.Instance,
                CancellationToken.None)).Should().Be(0);
            applied.ToString().Should().StartWith("UYGULANDI").And.Contain("eşleşme silinen=1");
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.BankTransactions.Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(new[] { incomingId });
            (await db.PaymentMatches.AnyAsync(m => m.BankTransactionId == outgoingId)).Should().BeFalse();
        }
    }
}
```

- [ ] **Adım 9: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataCleanup"`
Expected: PASS (Docker gerekli; zaman aşımında sınıfı tek başına yeniden koştur).

- [ ] **Adım 10: P0 için banka + KVKK alt paketini koştur**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Bank|FullyQualifiedName~Obifin|FullyQualifiedName~Kvkk|FullyQualifiedName~Purge"`
Expected: PASS.

- [ ] **Adım 11: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/BankDataCleanupJob.cs OrderDeck.LicenseServer/Tools/BankDataCleanup.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankDataCleanupJobTests.cs OrderDeck.LicenseServer.Tests/Tools/BankDataCleanupTests.cs OrderDeck.LicenseServer.Tests/Tools/BankDataCleanupRelationalTests.cs
git commit -m "feat(bank-kvkk): mevcut banka verisi için tek seferlik temizlik komutu ekle

bank-data-cleanup [--license] [--apply]: varsayılan kuru koşu; uygulamada
giden/sıfır hareketleri ve çapa öncesi kararsız ilk çekim satırlarını
siler, VKN/TCKN kodlarını boşaltır, ham kaydı beyaz listeye indirir,
açıklamadaki IBAN/TCKN'yi maskeler, eski kısa/hesap numarası maskelerini
'…'ya çevirir. Prod'da Burak koşturur.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

- [ ] **Adım 12: Burak'a sor (PR kararı)**

Görev 1–8 bitti. P0 ayrı PR olarak öne alınacak mı? (Genel kurallar → PR.) Cevaba göre devam.

Soruyla birlikte Burak'a şunu da söyle: P0 tek başına çıkarsa lisans bitişiyle `Disabled` olan bağlantıyı geri açan yol P1'e (Görev 14) kadar yok; lisans uzatma bağlantıya dokunmuyor, P0 ile P1 arasında bitip uzatılan lisansta çekim ve otomatik eşleştirme sessizce durur. Seçenekler: (1) P0 ayrı çıkar, "Prod adımları → P0 deploy'undan ÖNCE" (a, b) deploy'dan önce koşulur, "P0 ile P1 arasında" elle kurtarma SQL'i geçerli olur; (2) P0 ile P1 aynı pencerede çıkar. P0 ayrı PR olursa PR-A gövdesine "Prod adımları"nın bu iki bloğu aynen girer.

---

## PR-B — P1: imha kaydı, saklama, silme ve erişim araçları, veri sorumlusu, yedekler

### Görev 9: Kalıcı imha kaydı, denetim budaması muafiyeti, saklama işi uyarısı (P1-14)

**Neden (not Ö-13, Ö-12):** periyodik banka silmesi, purge'ün banka adımları, yayıncı silmesi, kimlik değişiminde gölge veri silmesi ve tek seferlik temizlik kalıcı olarak kayda geçmiyor; denetim kayıtları 24 ayda budanıyor, oysa imha kayıtları en az 3 yıl saklanmalı (Silme Yönetmeliği md. 7/3); saklama işi haftalarca düşerse süreler sessizce aşılır; yedekten geri yükleme silineni geri getirir ve hangi silmenin yeniden uygulanacağı hiçbir yerde kalmaz. Beş parça:
1. **`DataDestructionRecords`** tablosu: kişisel veri İÇERMEZ (tarih, lisans, silinen kaydın takma kimliği `SubjectId` — ShopperId/CustomerId, tablo/alan, eylem, satır sayısı, ölçüt, tetikleyici, talep kimliği, yapan). Hiçbir iş budamaz. Kayıt, silmeyle **aynı** `SaveChanges`'te yazılır (biri olup öbürü olmasın). Yardımcı: `DataDestruction.Record(...)`. Bu görev saklama işine, yayıncı silmesine (`customer-purge`), kimlik değişimindeki gölge veri silmesine (`credential-change`) ve `bank-data-cleanup`'a (`cleanup`, yapan `cli`) bağlar; sonraki görevler (10, 11, 13, 14, 15, 16, 17) de kullanır.
2. **Denetim budaması:** yalnız ayrıntısı kişisel veri İÇERMEYEN imha olayları (`shopper.purged` ve sonra eklenecek banka silme olayları) 24 ayda değil `DestructionRetentionMonths` (36) ayda budanır; satırın kendi sütunlarındaki yöneticinin kullanıcı adı ve IP'si yine 24 ayda maskelenir (inceleme sonrası 6. madde). `customer.purged` BİLEREK muaf değil: ayrıntısında silinen yayıncının e-postası (`preEmail`) var; o satır 24 ayda gider, yayıncı silmesinin 3 yıllık kaydı kişisel veri içermeyen `DataDestructionRecords` satırlarıdır.
3. **Uyarı:** banka saklama işi düşerse `Admin:AlertEmail`'e e-posta (`AdminJobAlerts` — inceleme sonrası 10. madde; `BackupRestoreDrillJob` kalıbı) ve istisna yine Hangfire'a çıkar.
4. **Veritabanı dışında kopya (Ö-12):** imha kayıtları aynı veritabanında durduğu için yedekten geri yükleme onları da yedek anına sarar; tablo tek başına geri yüklemeden sonra silmeleri yeniden uygulamanın dayanağı OLAMAZ. Saatlik `DataDestructionDigestJob` bir önceki tam saatin kayıtlarını (kişisel veri yok) yöneticiye CSV metni olarak e-postalar; geri yükleme prosedürü (Görev 23, `deploy/README.md` "Geri yükleme") bu e-postalardan yürür. Bir saatlik boşluk kalabilir; prosedür onu da anlatır.
5. Talep silmesinin (Görev 17) yeniden uygulanması IBAN'ı ister ama talep kapanınca özet boşalır: prosedür başvurunun aslına (yazılı başvuru ya da e-postası) dayanır; imha kaydı yalnız talep kimliğini taşır.

**İnceleme sonrası (20.10; aşağıdaki adımların kodu bu maddelerle değişti, güncel hâli dalda):**
1. **Özet durum tutar.** Pencereli özet SMTP hatasını yutuyor (iş Succeeded, o saat bir daha gitmiyor), `:13`'teki DB hatasında pencereyi kaybediyor, saatlerce süren kesintiden sonra yalnız son saati gönderiyordu; `RunForWindowEndingAsync`'i çağıran bir yol da yoktu. Artık `DataDestructionRecords.DigestedAt` (göç yeniden üretildi: `*_DataDestructionRecords`, süzgeçli dizin `IX_DataDestructionRecords_Undigested`) yalnız e-posta gidince yazılır; her koşu UTC saat başından önceki BÜTÜN gönderilmemiş kayıtları zaman sırasıyla, en çok `MaxRowsPerEmail` (1000) satırlık e-postalarla gönderir. Gönderim `AdminJobAlerts.SendOrThrowAsync` ile: SMTP hatası ya da boş `Admin:AlertEmail` işi Failed yapar, işaret yazılmaz, sonraki saat telafi eder. `RunForWindowEndingAsync` kalktı (elle telafi: Hangfire'da `data-destruction-digest` → "Trigger now"). Saat `TimeProvider`'dan (testte UTC+3 yerel dilimle). E-posta gidip işaret yazılamazsa ya da geri yüklemeden sonra yedekte gönderilmemiş kayıtlar yeniden gider: çift satır zararsız, yeniden uygulama idempotent.
2. **Lisans başına kayıt.** Saklama işi ve `--license` verilmeden koşan `bank-data-cleanup` partileri lisansları karıştırıp `LicenseId=null` yazıyordu; yayıncı kendi verisinin imha kanıtını isteyince (VİS md. 10 denetim ve bilgi verme) cevap verilemezdi. Her parti satırın lisansına göre gruplanır, lisans başına bir kayıt (aynı SaveChanges).
3. **Denetim budaması ve koşu kaydı.** `audit-retention` kişisel veri taşıyan denetim satırlarını siliyor ama kayıt bırakmıyor, düşünce uyarmıyordu: artık her parti `AuditLogs` imha kaydını ExecuteDelete ile AYNI açık işlemde yazar, düşerse `AuditRetentionJobs.FailureSubject` e-postası gider. `bank-data-retention` ve `audit-retention` her başarılı koşuda, silecek satır olmasa da, `run-completed` kaydı (satır 0, `DataDestruction.RecordRun`) bırakır: iç imha prosedürünün "her çalışma kayıt bırakır" ve haftalık "son başarılı çalışma" kontrolü tablodan ve dış kopyadan okunur. İşin hiç koşmaması (kilit zaman aşımı, Hangfire durması) ayrıca uyarı üretmez; onu bu haftalık kontrol yakalar.
4. **Yapan = kimlik.** Yönetici eyleminde `Actor`'e kullanıcı adı değil yöneticinin `sub`'ı yazılır: `DataDestruction.ActorOf(User)` (tablo budanmaz; adı aynı işlemin 24 aylık denetim satırında).
5. **Yayıncı silmesi `PushDevices`'ı da siler** (cihaz kimliği + FCM/APNS jetonu; müşteri satırı kaldığı için FK cascade'i tetiklenmiyordu) ve kayda geçirir.

**İkinci inceleme sonrası (20.10):**

6. **Muaf satırın kişisel verisi 24 ayda maskelenir (Ö-14).** Muafiyet yalnız ayrıntıya bakıyordu; `shopper.purged` satırının kendi sütunlarında silmeyi yapan yöneticinin `AdminUsername`'i (ve `security-data-retention` kapalıysa `IpAddress`'i — o iş açıkken IP zaten 90 günde boşalıyor) 36 ay kalıyordu. `audit-retention` silmeden SONRA, `RetentionMonths`'u geçmiş imha olayı satırlarında adı `AdminId`'nin metnine (küçük harf; SQL'de `LOWER(CONVERT(…))`) çevirir, IP'yi boşaltır: relational'da ExecuteUpdate, imha kaydı (`AuditRetentionJobs.MaskedTarget` = `AuditLogs.AdminUsername+IpAddress`, `masked`, ölçüt `OccurredAt+24ay, imha olayı`) aynı açık işlemde. Maskelenmiş satır koşula girmez (koşul ve yazılan değer aynı ifade): her koşu aynı satırı yeniden saymaz; `AuditRetentionRelationalTests` iki koşuyla gerçek SQL Server'da sınar. 24–36 ay arasında satırda yalnız takma kimlikler (yönetici kimliği, ShopperId, talep kimliği) ve sayılar kalır; karar 1'deki not.
7. **Kimlik değişiminde yapan = yönetici kimliği.** `ObifinConnectionService.UpsertAsync`/`UpsertWithResultAsync` isteğe bağlı `actor` alır (boşsa `DataDestruction.AdminActor`); admin/obifin Kaydet `DataDestruction.ActorOf(User)` geçirir.
8. **Purge sayımı izleyiciden.** Satır sayıları ayrı COUNT sorgularıyla DB'den alınıyordu: `RemoveRange`'in okumasından sonra eklenen satır (girişin jetonu, formdan gelen gönderim) sayılıyor ama silinmiyordu. Artık `Deleted<T>()` = izleyicideki `Deleted` girdileri: kayıt tam olarak bu SaveChanges'in sildiğini söyler (`CustomerPurgeRelationalTests` yarışı sırayla kurar).
9. **Ölçütte CSV ayracı yok.** `;` dış kopyanın (CSV) ayracı; ToCsv onu `,` yaptığı için e-postadaki değer tablodakinden farklıydı. Kaynakta `, ` ya da ` / `; `DataDestruction.Record`/`RecordRun` hedef, ölçüt ve yapanı `DataDestruction.CsvSafe`'ten geçirir (`;` → `,`, satır sonu → boşluk), ToCsv'nin kaçışlaması savunma.
10. **`BankJobAlerts` → `Services.Observability.AdminJobAlerts`** (banka dışı işlerin de yönetici e-postası; günlük mesajı "İş uyarısı"). Bu görevin aşağıdaki adım kodu eski adı taşır; güncel hâli dalda. Görev 10 ve 14'ün kodu yeni adla güncellendi.

**Sonraki görevler için kural:** birden çok lisansa dokunan bir parti imha kaydını satırın lisansı başına yazar (Görev 10'un kodu buna göre güncellendi); yönetici eyleminin `actor`'ü `DataDestruction.ActorOf(User)` (Görev 13 "Kaldır ve sil", Görev 17 "Sil ve kapat"); günlük imha işi başarılı koşu sonunda `DataDestruction.RecordRun` yazar (Görev 10, 14). Ölçüt metninde `;` kullanılmaz (`, ` ya da ` / `; Görev 10'un koşu ölçütü buna göre güncellendi). İmha kaydının satır sayısı silinen kümeden (yüklenen liste ya da izleyicideki `Deleted` girdileri) gelir, silmeden ayrı bir COUNT sorgusundan değil (Görev 21'in `Purge` satırı `Deleted<DataControllerProfile>()`). Yönetici uyarısı `AdminJobAlerts` (`using OrderDeck.LicenseServer.Services.Observability;`). `DestructionEvents`'e eklenen olayın satırı 24 ayda kendiliğinden maskelenir; ayrıntısına yine kişisel veri girmez.

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/DataDestructionRecord.cs`
- Create: `OrderDeck.LicenseServer/Services/Privacy/DataDestruction.cs`, `OrderDeck.LicenseServer/Services/Privacy/DataDestructionDigestJob.cs`
- Create: `OrderDeck.LicenseServer/Services/Observability/AdminJobAlerts.cs` (ilk hâli `Services/Bank/BankJobAlerts.cs`; inceleme sonrası 10. madde)
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (DbSet + model)
- Modify: `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs`, `OrderDeck.LicenseServer/Services/Audit/AuditRetentionJobs.cs`
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankDataRetentionJob.cs`, `BankDataCleanupJob.cs`, `ObifinConnectionService.cs` (`ResetShadowData`, `actor`)
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Obifin/Index.cshtml.cs` (Kaydet `actor`)
- Modify: `OrderDeck.LicenseServer/Controllers/Customers/AdminCustomersController.cs` (`Purge`)
- Modify: `OrderDeck.LicenseServer/Program.cs` (DI + takvim)
- Create: göç `*_DataDestructionRecords`
- Test: `OrderDeck.LicenseServer.Tests/Services/Audit/AuditRetentionJobsTests.cs`, `AuditRetentionRelationalTests.cs`, `OrderDeck.LicenseServer.Tests/Services/Bank/BankDataRetentionJobTests.cs`, `BankDataCleanupJobTests.cs`, `ObifinPollJobTests.cs`, `Pages/Admin/AdminObifinPageTests.cs`, `Controllers/Compliance/GdprFlowTests.cs`, `Controllers/Customers/CustomerPurgeRelationalTests.cs`, YENİ `Services/Privacy/DataDestructionDigestJobTests.cs`, YENİ `Services/Observability/AdminJobAlertsTests.cs`

- [ ] **Adım 1: Denetim budaması testini yaz (kırmızı)**

`AuditRetentionJobsTests.cs` sınıfının sonuna:

```csharp
    [Fact]
    public async Task Kisisel_veri_icermeyen_imha_olaylari_36_ayda_customer_purged_24_ayda_budanir()
    {
        // Silme Yönetmeliği md. 7/3: imha kayıtları en az üç yıl. Normal olay 24 ayda gider. customer.purged ayrıntısında
        // silinen yayıncının e-postası var: muafiyet onu 36 ay tutmasın (kalıcı kaydı DataDestructionRecords'ta).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        AuditLogEntry Entry(string type, int monthsAgo) => new()
        {
            Id = Guid.NewGuid(), EventType = type, OccurredAt = DateTimeOffset.UtcNow.AddMonths(-monthsAgo).AddDays(-1),
            AdminId = Guid.Empty, AdminUsername = "test",
        };
        var purge30 = Entry(AuditEvents.ShopperPurged, 30);
        var purge37 = Entry(AuditEvents.ShopperPurged, 37);
        var customerPurge30 = Entry(AuditEvents.CustomerPurged, 30);
        var normal30 = Entry("test-" + Guid.NewGuid().ToString("N")[..8], 30);
        db.AuditLogs.AddRange(purge30, purge37, customerPurge30, normal30);
        await db.SaveChangesAsync();

        await Make(db).PruneAsync(CancellationToken.None);

        var ids = new[] { purge30.Id, purge37.Id, customerPurge30.Id, normal30.Id };
        (await db.AuditLogs.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { purge30.Id });
        AuditEvents.DestructionEvents.Should().NotContain(AuditEvents.CustomerPurged);
    }
```

- [ ] **Adım 2: Saklama işi testlerini yaz (kırmızı)**

`BankDataRetentionJobTests.cs` — dosyanın üstüne şu `using`'leri ekle:

```csharp
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
```

`Job` yardımcısını şununla değiştir:

```csharp
    /// <summary>Kurulan her kaydı düşürür (yalnız <see cref="Armed"/> iken): DB kesintisi benzetimi.</summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Armed ? throw new DbUpdateException("DB kesintisi benzetimi") : ValueTask.FromResult(result);
    }

    private static BankJobAlerts Alerts(TestEmailSender email)
        => new(email, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BankJobAlerts.AlertEmailKey] = $"ops-{Guid.NewGuid():N}@example.test",
        }).Build(), NullLogger<BankJobAlerts>.Instance);

    private static BankDataRetentionJob Job(LicenseDbContext db, TestEmailSender? email = null)
        => new(db, Options.Create(new BankOptions { HashKey = new string('k', 32) }), Alerts(email ?? new TestEmailSender()),
            NullLogger<BankDataRetentionJob>.Instance);
```

Sınıfın sonuna:

```csharp
    [Fact]
    public async Task Her_bosaltma_partisi_kisisel_veri_icermeyen_imha_kaydi_yazar()
    {
        using var db = NewDb();
        db.BankTransactions.AddRange(Row(200), Row(200));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Should().Contain(r => r.Target == "BankTransactions.RawJson" && r.RowCount == 2
            && r.Action == DataDestruction.Actions.Nulled && r.Trigger == DataDestruction.Triggers.Retention
            && r.Actor == DataDestruction.SystemActor && r.LicenseId == null);
        records.Should().Contain(r => r.Target == "BankTransactions.Description+CounterpartyName" && r.RowCount == 2);
    }

    [Fact]
    public async Task Is_duserse_yoneticiye_e_posta_gider_ve_hata_yukari_cikar()
    {
        // KVKK Ö-13: iş sessizce düşerse saklama süreleri fark edilmeden aşılır.
        var failing = new FailingSaveInterceptor();
        var email = new TestEmailSender();
        using var db = NewDb(failing);
        db.BankTransactions.Add(Row(200));
        await db.SaveChangesAsync();
        failing.Armed = true;

        var act = () => Job(db, email).RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        var sent = email.Sent.Should().ContainSingle().Subject;
        sent.Subject.Should().Be(BankDataRetentionJob.FailureSubject);
        sent.PlainBody.Should().Contain(nameof(DbUpdateException));
    }
```

- [ ] **Adım 3: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~AuditRetentionJobsTests|FullyQualifiedName~BankDataRetentionJobTests"`
Expected: FAIL — `BankJobAlerts`, `DataDestruction`, `DataDestructionRecords` yok.

- [ ] **Adım 4: Varlık ve yardımcı**

`OrderDeck.LicenseServer/Domain/DataDestructionRecord.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Kişisel verinin silinmesi, boşaltılması ya da maskelenmesinin kalıcı kaydı (Silme Yönetmeliği md. 5/3, 7/3: en az üç yıl;
/// KVKK ön inceleme Ö-13). Kişisel veri İÇERMEZ: ne, ne zaman, kaç satır, hangi ölçütle, ne tetikledi, kim. Lisansa FK YOK:
/// lisans silinse de kayıt yaşar. Hiçbir iş bu tabloyu budamaz.
/// <para><b>Geri yükleme:</b> tablo aynı veritabanındadır; yedekten geri yükleme onu da yedek anına sarar. Geri yüklemeden
/// sonra silmeleri yeniden uygulamanın dayanağı bu tablo DEĞİL, veritabanı dışındaki kopyasıdır
/// (<c>DataDestructionDigestJob</c>'ın saatlik e-postası; prosedür <c>deploy/README.md</c> "Geri yükleme").</para>
/// </summary>
public sealed class DataDestructionRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? LicenseId { get; set; }
    /// <summary>Silmenin konusu olan kaydın takma kimliği (ShopperId, CustomerId): kişinin kendi verisi silinmiş ya da
    /// anonimleştirilmiştir, kimlik tek başına kimseyi göstermez. Geri yüklemeden sonra silme bununla yeniden uygulanır.</summary>
    public Guid? SubjectId { get; set; }
    /// <summary>Tablo ya da tablo.alan, ör. <c>BankTransactions.RawJson</c>.</summary>
    public string Target { get; set; } = "";
    /// <summary><c>DataDestruction.Actions</c> değerlerinden biri.</summary>
    public string Action { get; set; } = "";
    public int RowCount { get; set; }
    /// <summary>Ölçüt, ör. <c>OccurredAt+30g</c>. Kişisel veri yazılmaz.</summary>
    public string Criterion { get; set; } = "";
    /// <summary><c>DataDestruction.Triggers</c> değerlerinden biri.</summary>
    public string Trigger { get; set; } = "";
    /// <summary>Talep üzerine yapıldıysa talebin kimliği (banka veri talebi).</summary>
    public Guid? RequestId { get; set; }
    /// <summary><c>system</c> ya da yönetici kullanıcı adı.</summary>
    public string Actor { get; set; } = "";
}
```

`OrderDeck.LicenseServer/Services/Privacy/DataDestruction.cs`:

```csharp
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>Kalıcı imha kaydının (<see cref="DataDestructionRecord"/>) sabitleri ve yazıcısı.</summary>
public static class DataDestruction
{
    public static class Actions
    {
        public const string Deleted = "deleted";
        public const string Nulled = "nulled";
        /// <summary>Değer daha az tanımlayıcı bir biçimle değiştirildi (maske, beyaz liste, kimliğe çevirme).</summary>
        public const string Masked = "masked";
    }

    public static class Triggers
    {
        public const string Retention = "retention";
        public const string ShopperPurge = "shopper-purge";
        public const string CustomerPurge = "customer-purge";
        public const string SubjectRequest = "subject-request";
        public const string ServiceEnd = "service-end";
        public const string AdminRemove = "admin-remove";
        /// <summary>Obifin kimliği değişti, eski hesabın gölge verisi silindi (<c>ObifinConnectionService.ResetShadowData</c>).</summary>
        public const string CredentialChange = "credential-change";
        /// <summary>Tek seferlik <c>bank-data-cleanup</c> komutu.</summary>
        public const string Cleanup = "cleanup";
    }

    public const string SystemActor = "system";
    /// <summary>Konteynerde elle koşulan komut (<c>bank-data-cleanup</c>).</summary>
    public const string CliActor = "cli";
    /// <summary>Yönetici eylemi, kullanıcı adı servise ulaşmadığında (adı aynı işlemin denetim satırında).</summary>
    public const string AdminActor = "admin";

    /// <summary>Kaydı izleyiciye EKLER, kaydetmez: silmeyle aynı SaveChanges'te yazılsın. Sıfır satırlık işlem yazılmaz.</summary>
    public static void Record(LicenseDbContext db, Guid? licenseId, string target, string action, int rowCount,
        string criterion, string trigger, string actor, Guid? requestId = null, Guid? subjectId = null)
    {
        if (rowCount <= 0) return;
        db.DataDestructionRecords.Add(new DataDestructionRecord
        {
            Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, LicenseId = licenseId, SubjectId = subjectId, Target = target,
            Action = action, RowCount = rowCount, Criterion = criterion, Trigger = trigger, RequestId = requestId, Actor = actor,
        });
    }
}
```

- [ ] **Adım 5: Modeli kaydet**

`Data/LicenseDbContext.cs` — `public DbSet<PaymentMatchGap> PaymentMatchGaps => Set<PaymentMatchGap>();` satırının ALTINA:

```csharp
    public DbSet<DataDestructionRecord> DataDestructionRecords => Set<DataDestructionRecord>();
```

`OnModelCreating`'de `mb.Entity<PaymentMatchGap>(b => { … });` bloğunun ALTINA:

```csharp
        // Kalıcı imha kaydı (Silme Yönetmeliği md. 7/3). Lisansa FK BİLEREK yok: lisans silinse de kayıt yaşar.
        mb.Entity<DataDestructionRecord>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Target).HasMaxLength(100).IsRequired();
            b.Property(r => r.Action).HasMaxLength(16).IsRequired();
            b.Property(r => r.Criterion).HasMaxLength(100).IsRequired();
            b.Property(r => r.Trigger).HasMaxLength(32).IsRequired();
            b.Property(r => r.Actor).HasMaxLength(256).IsRequired();
            b.HasIndex(r => r.OccurredAt);
            b.HasIndex(r => r.LicenseId);
            b.HasIndex(r => r.SubjectId);
        });
```

- [ ] **Adım 6: Denetim budaması muafiyeti**

`Services/Audit/AuditEvents.cs` — `AuditEvents` sınıfının sonuna (son `}`'dan önce):

```csharp
    /// <summary>İmha olayları (Silme Yönetmeliği md. 7/3: en az üç yıl). <see cref="AuditRetentionJobs"/> bunları
    /// <see cref="AuditRetentionOptions.RetentionMonths"/>'ta değil <see cref="AuditRetentionOptions.DestructionRetentionMonths"/>'ta
    /// budar. YALNIZ ayrıntısı kişisel veri İÇERMEYEN (sayılar, kimlikler) olaylar girer: muafiyet kişisel veriyi daha uzun
    /// tutmamalı. <see cref="CustomerPurged"/> BİLEREK yok — ayrıntısında silinen yayıncının e-postası var, 24 ayda gider;
    /// yayıncı silmesinin 3 yıllık kaydı kişisel veri içermeyen <c>DataDestructionRecords</c>. Yeni bir silme olayı eklenirse
    /// ayrıntısı kişisel veri içermiyorsa buraya da eklenir.</summary>
    public static readonly IReadOnlySet<string> DestructionEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        ShopperPurged,
    };
```

`Services/Audit/AuditRetentionJobs.cs` — `AuditRetentionOptions`'a `BatchSize`'ın altına:

```csharp
    /// <summary>İmha olaylarının (<see cref="AuditEvents.DestructionEvents"/>) saklama süresi, ay. Silme Yönetmeliği md. 7/3
    /// en az üç yıl istiyor; <see cref="RetentionMonths"/>'tan kısa verilirse onun değeri kullanılır.</summary>
    public int DestructionRetentionMonths { get; set; } = 36;
```

Dosyanın üstüne `using System.Linq.Expressions;` ve `using OrderDeck.LicenseServer.Domain;` ekle. `PruneAsync` metodunu (gövdesiyle) şununla değiştir:

```csharp
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task PruneAsync(CancellationToken ct)
    {
        if (_opt.RetentionMonths <= 0)
        {
            _log.LogInformation("[AuditRetentionJobs] retention disabled (RetentionMonths=0)");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMonths(-_opt.RetentionMonths);
        // İmha olayları kendi, daha uzun süresiyle budanır (Silme Yönetmeliği md. 7/3).
        var destructionCutoff = now.AddMonths(-Math.Max(_opt.DestructionRetentionMonths, _opt.RetentionMonths));
        // Dizi (HashSet değil): SQL Server'da parametre listesi olarak çevrilir.
        var exempt = AuditEvents.DestructionEvents.ToArray();

        var totalDeleted = await PruneWhereAsync(a => a.OccurredAt < cutoff && !exempt.Contains(a.EventType), ct)
            + await PruneWhereAsync(a => a.OccurredAt < destructionCutoff && exempt.Contains(a.EventType), ct);

        if (totalDeleted > 0)
            _log.LogInformation("[AuditRetentionJobs] pruned {Count} audit rows (cutoff {Cutoff}, destruction cutoff {DestructionCutoff})",
                totalDeleted, cutoff, destructionCutoff);
        else
            _log.LogDebug("[AuditRetentionJobs] no rows older than {Cutoff} — nothing to prune", cutoff);
    }

    /// <summary>Koşula uyan satırları <see cref="AuditRetentionOptions.BatchSize"/>'lık partilerle siler. Relational'da
    /// ExecuteDelete, InMemory'de (testler) yükle+sil — eski tek döngünün aynısı.</summary>
    private async Task<int> PruneWhereAsync(Expression<Func<AuditLogEntry, bool>> predicate, CancellationToken ct)
    {
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await _db.AuditLogs
                .Where(predicate)
                .OrderBy(a => a.OccurredAt)
                .Take(_opt.BatchSize)
                .Select(a => a.Id)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            if (_db.Database.IsRelational())
            {
                await _db.AuditLogs.Where(a => batch.Contains(a.Id)).ExecuteDeleteAsync(ct);
            }
            else
            {
                var entries = await _db.AuditLogs.Where(a => batch.Contains(a.Id)).ToListAsync(ct);
                _db.AuditLogs.RemoveRange(entries);
                await _db.SaveChangesAsync(ct);
            }

            total += batch.Count;
            if (batch.Count < _opt.BatchSize) break;
        }
        return total;
    }
```

- [ ] **Adım 7: Uyarı servisi**

`OrderDeck.LicenseServer/Services/Bank/BankJobAlerts.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.Email;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Banka saklama ve hizmet sonu işlerinin yöneticiye e-postası (KVKK Ö-13; <c>BackupRestoreDrillJob</c> kalıbı, adres
/// <see cref="AlertEmailKey"/>). Adres boşsa yalnız günlüğe uyarı. Gönderim hatası yutulur ve günlüğe düşer: uyarının düşmesi
/// işi düşürmesin. Gövdeye kişisel veri girmez — yalnız iş adı, istisna türü, sayılar, lisans kimliği. Alıcı adresi günlüğe
/// yazılmaz.
/// </summary>
public sealed class BankJobAlerts
{
    public const string AlertEmailKey = "Admin:AlertEmail";

    private readonly IEmailSender _email;
    private readonly IConfiguration _config;
    private readonly ILogger<BankJobAlerts> _log;

    public BankJobAlerts(IEmailSender email, IConfiguration config, ILogger<BankJobAlerts> log)
    { _email = email; _config = config; _log = log; }

    public async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        var to = _config[AlertEmailKey];
        if (string.IsNullOrWhiteSpace(to))
        {
            _log.LogWarning("Banka iş uyarısı gönderilemedi: {Key} boş — {Subject}", AlertEmailKey, subject);
            return;
        }
        try
        {
            var html = $"<pre style=\"font-family:monospace\">{WebUtility.HtmlEncode(body)}</pre>";
            await _email.SendAsync(to, "OrderDeck Admin", subject, html, body, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.LogError(ex, "Banka iş uyarısı e-postası gönderilemedi — {Subject}", subject);
        }
    }
}
```

- [ ] **Adım 8: Saklama işine kayıt ve uyarıyı bağla**

`Services/Bank/BankDataRetentionJob.cs`:

Üste `using OrderDeck.LicenseServer.Services.Privacy;` ekle. Alanlara `private readonly BankJobAlerts _alerts;` ekle, kurucuyu şununla değiştir:

```csharp
    public const string FailureSubject = "[OrderDeck] Banka veri saklama işi düştü";

    public BankDataRetentionJob(LicenseDbContext db, IOptions<BankOptions> opt, BankJobAlerts alerts,
        ILogger<BankDataRetentionJob> log)
    { _db = db; _opt = opt.Value; _alerts = alerts; _log = log; }
```

`public async Task<(int RawPurged, int DescriptionPurged)> RunAsync(CancellationToken ct = default)` metodunun adını `RunCoreAsync` yap ve `private` yap; üstüne yeni giriş noktasını ekle:

```csharp
    /// <summary>Düşerse yöneticiye e-posta (KVKK Ö-13) ve istisna Hangfire'a çıkar (koşu Failed).</summary>
    public async Task<(int RawPurged, int DescriptionPurged)> RunAsync(CancellationToken ct = default)
    {
        try
        {
            return await RunCoreAsync(ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.LogError(ex, "Banka veri saklama işi düştü");
            await _alerts.SendAsync(FailureSubject,
                $"bank-data-retention düştü: {ex.GetType().Name}. Hangfire panosuna bakın; iş ertesi gün yeniden koşar, " +
                "o güne kadar saklama süreleri aşılmış olur.", CancellationToken.None);
            throw;
        }
    }
```

`RunCoreAsync` içindeki üç `PurgeInBatchesAsync` çağrısına iki argüman ekle (hedef ve ölçüt; `ct`'den önce):

```csharp
        var raw = await PurgeInBatchesAsync(
            _db.BankTransactions.Where(t => t.RawJson != null && t.FetchedAt < rawCutoff).OrderBy(t => t.Id),
            t => t.RawJson = null, "BankTransactions.RawJson", $"FetchedAt+{_opt.RawJsonRetentionDays}g", ct);
        var desc = await PurgeInBatchesAsync(
            _db.BankTransactions.Where(t => t.DescriptionPurgedAt == null && t.FetchedAt < descCutoff).OrderBy(t => t.Id),
            t => { t.Description = null; t.CounterpartyName = null; t.DescriptionPurgedAt = now; },
            "BankTransactions.Description+CounterpartyName", $"FetchedAt+{_opt.DescriptionRetentionDays}g", ct);
        // UpdatedAt eşzamanlılık jetonudur, her yazan ilerletir: arada okuyup yazan bir bağdaştırma çakışmayı görsün.
        var evidence = await PurgeInBatchesAsync(
            _db.PaymentMatches.Where(m => m.Evidence != null && m.CreatedAt < descCutoff).OrderBy(m => m.Id),
            m => { m.Evidence = null; m.UpdatedAt = now; }, "PaymentMatches.Evidence",
            $"CreatedAt+{_opt.DescriptionRetentionDays}g", ct);
```

`PurgeInBatchesAsync`'i şununla değiştir:

```csharp
    /// <summary><paramref name="expired"/>'i <see cref="BatchSize"/>'lık partilerle boşaltır; her parti kendi imha kaydıyla
    /// AYNI SaveChanges'te yazılır. Sorgu, boşaltılan satırı artık seçmeyecek biçimde yazılmalı (boşaltılan alan filtrenin
    /// içinde): her parti baştan sorgulanır.</summary>
    private async Task<int> PurgeInBatchesAsync<T>(IQueryable<T> expired, Action<T> purge, string target, string criterion,
        CancellationToken ct) where T : class
    {
        var total = 0;
        while (true)
        {
            var batch = await expired.Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            foreach (var row in batch) purge(row);
            DataDestruction.Record(_db, null, target, DataDestruction.Actions.Nulled, batch.Count, criterion,
                DataDestruction.Triggers.Retention, DataDestruction.SystemActor);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize) return total;
        }
    }
```

- [ ] **Adım 9: DI**

`Program.cs` — `builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.BankDataRetentionJob>();` satırının ÜSTÜNE:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.BankJobAlerts>();
```

- [ ] **Adım 10: Göçü üret ve incele**

Run: `cd OrderDeck.LicenseServer && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add DataDestructionRecords --context LicenseDbContext --output-dir Data/Migrations`
Expected: `Up` yalnız `CreateTable("DataDestructionRecords")` + iki `CreateIndex`.

- [ ] **Adım 11: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~AuditRetentionJobsTests|FullyQualifiedName~BankDataRetentionJobTests|FullyQualifiedName~BankJobsDiTests"`
Expected: PASS.

Run (Docker): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataCleanupRelationalTests"`
Expected: PASS (yeni tablo SQL Server'da kuruldu).

- [ ] **Adım 12: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/DataDestructionRecord.cs OrderDeck.LicenseServer/Services/Privacy/DataDestruction.cs OrderDeck.LicenseServer/Services/Bank/BankJobAlerts.cs OrderDeck.LicenseServer/Services/Bank/BankDataRetentionJob.cs OrderDeck.LicenseServer/Services/Audit OrderDeck.LicenseServer/Data OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Audit/AuditRetentionJobsTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankDataRetentionJobTests.cs
git commit -m "feat(kvkk): kalıcı imha kaydı tut, kişisel veri içermeyen imha denetimini 36 ay sakla

DataDestructionRecords (kişisel veri içermez, budanmaz) her banka
saklama partisinde aynı işlemle yazılır; shopper purge denetim olayı 24
değil 36 ayda budanır (customer.purged e-posta taşıdığı için muaf değil);
saklama işi düşerse Admin:AlertEmail'e e-posta gider (KVKK ön inceleme
Ö-13, P1-14).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

- [ ] **Adım 13: Yayıncı silmesinin kalıcı imha kaydı (test önce, kırmızı)**

`Controllers/Compliance/GdprFlowTests.cs` — `Purge_via_admin_anonymises_customer_and_deletes_data` testinin sonuna (`purgeAudit.Should().BeGreaterThan(0);`'dan sonra):

```csharp
        // KVKK Ö-13: kalıcı imha kaydı yayıncı kimliğiyle (anonimleştirilmiş satır kalır) ve kişisel veri İÇERMEZ — silinen
        // e-posta yalnız 24 ayda budanan customer.purged denetim satırında.
        var records = await verifyDb.DataDestructionRecords.AsNoTracking().Where(r => r.SubjectId == customerId).ToListAsync();
        records.Should().Contain(r => r.Target == "CustomerBackups" && r.RowCount == 1
            && r.Trigger == OrderDeck.LicenseServer.Services.Privacy.DataDestruction.Triggers.CustomerPurge);
        records.Should().Contain(r => r.Target == "Customers.Email+Name+PasswordHash+Notes" && r.RowCount == 1);
        System.Text.Json.JsonSerializer.Serialize(records).Should().NotContain(originalEmail);
```

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~GdprFlowTests"`
Expected: FAIL — yayıncı silmesi imha kaydı yazmıyor.

`Controllers/Customers/AdminCustomersController.cs` — üste `using OrderDeck.LicenseServer.Services.Privacy;` ekle. `Purge`'te `customer.Notes = null;` satırının ALTINA (SaveChanges'ten önce; sayımlar silme henüz yazılmadığı için DB'den doğru gelir):

```csharp
        // KVKK Ö-13: kişisel veri İÇERMEYEN kalıcı imha kaydı, silmeyle aynı SaveChanges'te. preEmail BURAYA girmez: e-posta
        // yalnız 24 ayda budanan customer.purged denetim satırında. SubjectId = yayıncı hesabının kimliği (satır anonimleştirilmiş
        // kalır): yedekten geri yüklemeden sonra silme bununla yeniden uygulanır.
        var actor = User.FindFirst("username")?.Value ?? User.Identity?.Name ?? DataDestruction.AdminActor;
        const string criterion = "yayıncı silme talebi";
        void Record(string target, string action, int count) => DataDestruction.Record(_db, null, target, action, count, criterion,
            DataDestruction.Triggers.CustomerPurge, actor, subjectId: id);
        Record("Licenses", DataDestruction.Actions.Deleted, licenseIds.Count);
        Record("Activations", DataDestruction.Actions.Deleted,
            await _db.Activations.CountAsync(a => licenseIds.Contains(a.LicenseId), ct));
        Record("CustomerBackups", DataDestruction.Actions.Deleted, backupRows.Count);
        Record("EmailLogs", DataDestruction.Actions.Deleted, await _db.EmailLogs.CountAsync(e => e.CustomerId == id, ct));
        Record("IntakeFormConfigs", DataDestruction.Actions.Deleted, configIds.Count);
        Record("IntakeFormSubmissions", DataDestruction.Actions.Deleted, configIds.Count == 0 ? 0
            : await _db.IntakeFormSubmissions.CountAsync(s => configIds.Contains(s.IntakeFormConfigId), ct));
        Record("Customers.Email+Name+PasswordHash+Notes", DataDestruction.Actions.Nulled, 1);
```

Metodun özetindeki "Audit row is written with the pre-purge email so the action remains traceable." cümlesinin ALTINA: `/// The audit row (with the e-mail) is pruned at 24 months like any other; the 3-year destruction record is the PII-free DataDestructionRecords rows written in the same SaveChanges (KVKK Ö-13).`

- [ ] **Adım 14: Kimlik değişiminde silinen gölge verinin imha kaydı (test önce, kırmızı)**

`ObifinPollJobTests.cs` — üste `using OrderDeck.LicenseServer.Services.Privacy;` ekle; sınıfın sonuna:

```csharp
    [Fact]
    public async Task Kimlik_degisince_silinen_golge_veri_imha_kaydina_yazilir()
    {
        // KVKK Ö-13: ResetShadowData'nın toplu silmesi de kalıcı kayda geçer (yapan: aynı işlemin denetim satırındaki yönetici).
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 9, 0, 0), 100m));
        await job.RunAsync(CancellationToken.None);

        await svc.UpsertAsync(lic, "", $"api-{Guid.NewGuid():N}@x", null, null, CancellationToken.None, allowShadowReset: true);

        (await db.DataDestructionRecords.AsNoTracking().Where(r => r.LicenseId == lic).ToListAsync())
            .Should().Contain(r => r.Target == "BankTransactions" && r.RowCount == 1 && r.Action == DataDestruction.Actions.Deleted
                && r.Trigger == DataDestruction.Triggers.CredentialChange);
    }
```

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ObifinPollJobTests.Kimlik_degisince_silinen"`
Expected: FAIL.

`Services/Bank/ObifinConnectionService.cs` — üste `using OrderDeck.LicenseServer.Services.Privacy;` ekle. `ResetShadowData`'da `_db.BankConnections.RemoveRange(bankConnections);` satırının ALTINA:

```csharp
        // KVKK Ö-13: eski hesabın gölge verisi bu işlemde silinir; kişisel veri içermeyen imha kaydı aynı SaveChanges'te (SaveAsync
        // jeton çakışmasında yeniden dener, ikinci çakışmada izleyiciyi boşaltır: kayıt silmeyle birlikte düşer).
        const string criterion = "Obifin kimliği değişti";
        void Record(string target, int count) => DataDestruction.Record(_db, licenseId, target, DataDestruction.Actions.Deleted,
            count, criterion, DataDestruction.Triggers.CredentialChange, DataDestruction.AdminActor);
        Record("PaymentMatches", matches.Count);
        Record("BankTransactions", transactions.Count);
        Record("BankAccounts", accounts.Count);
        Record("CustomerIbanMemories", memories.Count);
        Record("PaymentMatchGaps", gaps.Count);
        Record("BankConnections", bankConnections.Count);
```

- [ ] **Adım 15: `bank-data-cleanup` imha kaydı (test önce, kırmızı)**

`BankDataCleanupJobTests.cs` — üste `using OrderDeck.LicenseServer.Services.Privacy;` ekle; sınıfın sonuna:

```csharp
    [Fact]
    public async Task Uygulama_her_partide_imha_kaydi_yazar_kuru_kosu_yazmaz()
    {
        // KVKK Ö-13: tek seferlik temizlik de kalıcı kayda geçer (tetikleyici cleanup, yapan cli), silmeyle aynı işlemde.
        using var db = NewDb(); var lic = Guid.NewGuid();
        db.BankTransactions.AddRange(Tx(lic, 30m, direction: BankTransactionDirection.Outgoing),
            Tx(lic, 10m, rawJson: """{"Aciklama":"x","Bakiye":"1"}""", taxIdHash: Hex64()));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(null, apply: false, CancellationToken.None);
        (await db.DataDestructionRecords.CountAsync()).Should().Be(0, "kuru koşu hiçbir şey yazmaz");

        await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.Cleanup && x.Actor == DataDestruction.CliActor);
        records.Should().Contain(x => x.Target == "BankTransactions" && x.Action == DataDestruction.Actions.Deleted && x.RowCount == 1);
        records.Should().Contain(x => x.Target == "BankTransactions.CounterpartyTaxIdHash" && x.Action == DataDestruction.Actions.Nulled);
        records.Should().Contain(x => x.Target == "BankTransactions.RawJson" && x.Action == DataDestruction.Actions.Masked);
    }
```

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataCleanupJobTests"`
Expected: FAIL — `RecordBatch` (Görev 8) yalnız günlüğe yazıyor.

`Services/Bank/BankDataCleanupJob.cs` — üste `using OrderDeck.LicenseServer.Services.Privacy;` ekle; yerel sabitleri ve `RecordBatch`'i şununla değiştir:

```csharp
    private const string Deleted = DataDestruction.Actions.Deleted, Nulled = DataDestruction.Actions.Nulled,
        Masked = DataDestruction.Actions.Masked;

    /// <summary>Bir UYGULAMA partisinin kalıcı imha kaydı (KVKK Ö-13): partinin SaveChanges'inden hemen önce çağrılır, kayıt
    /// silmeyle aynı işlemde yazılır (parti eşzamanlılıkla geri alınırsa kayıt da düşer). Kuru koşuda çağrılmaz.</summary>
    private void RecordBatch(Guid? licenseId, string target, string action, int count, string criterion)
        => DataDestruction.Record(_db, licenseId, target, action, count, criterion, DataDestruction.Triggers.Cleanup,
            DataDestruction.CliActor);
```

Sınıf özetinin sonuna: `/// Her uygulama partisi kalıcı imha kaydını aynı işlemde yazar (tetikleyici cleanup, yapan cli; KVKK Ö-13).` `Tools/BankDataCleanup.cs` özetindeki "Önce kuru koşu, çıktı tutanağa." cümlesinin sonuna: ` P1'den sonra uygulama DataDestructionRecords'a da yazar.`

- [ ] **Adım 16: Saatlik imha özeti — veritabanı dışında kopya (test önce, kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Privacy/DataDestructionDigestJobTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Privacy;

/// <summary>KVKK Ö-12/Ö-13: imha kayıtlarının veritabanı dışındaki kopyası — saatlik e-posta, kişisel veri yok, pencereler
/// örtüşmez.</summary>
public sealed class DataDestructionDigestJobTests
{
    private static LicenseDbContext NewDb() => new(new DbContextOptionsBuilder<LicenseDbContext>()
        .UseInMemoryDatabase($"destruction-digest-{Guid.NewGuid():N}").Options);

    private static DataDestructionDigestJob Job(LicenseDbContext db, TestEmailSender email)
        => new(db, new BankJobAlerts(email, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BankJobAlerts.AlertEmailKey] = $"ops-{Guid.NewGuid():N}@example.test",
            }).Build(), NullLogger<BankJobAlerts>.Instance));

    private static DataDestructionRecord Record(DateTimeOffset at, string target) => new()
    {
        Id = Guid.NewGuid(), OccurredAt = at, LicenseId = Guid.NewGuid(), SubjectId = Guid.NewGuid(), Target = target,
        Action = DataDestruction.Actions.Deleted, RowCount = 3, Criterion = "OccurredAt+30g;eşleşmeyen",
        Trigger = DataDestruction.Triggers.ShopperPurge, Actor = DataDestruction.SystemActor,
    };

    [Fact]
    public async Task Onceki_tam_saatin_kayitlari_CSV_olarak_gider_pencere_disindakiler_gitmez()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var end = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var inside = Record(end.AddMinutes(-59), "Shoppers.FullName+Phone+Address+Email+TcProtected");
        var atStart = Record(end.AddHours(-1), "PaymentMatches.WpfCustomerId+Evidence");
        var atEnd = Record(end, "BankTransactions");
        var beforeStart = Record(end.AddHours(-1).AddTicks(-1), "CustomerIbanMemories");
        db.DataDestructionRecords.AddRange(inside, atStart, atEnd, beforeStart);
        await db.SaveChangesAsync();

        var sent = await Job(db, email).RunForWindowEndingAsync(end, CancellationToken.None);

        sent.Should().Be(2, "pencere [end − 1 saat, end)");
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(DataDestructionDigestJob.Subject);
        mail.PlainBody.Should().Contain(inside.SubjectId.ToString()).And.Contain(atStart.Target).And.Contain("shopper-purge")
            .And.NotContain(atEnd.Target).And.NotContain(beforeStart.Target);
        mail.PlainBody.Should().Contain("OccurredAt+30g,eşleşmeyen", "ayraç ölçüt içinde kaçışlanır");
    }

    [Fact]
    public async Task Kayit_yoksa_e_posta_gitmez()
    {
        using var db = NewDb(); var email = new TestEmailSender();

        (await Job(db, email).RunForWindowEndingAsync(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero),
            CancellationToken.None)).Should().Be(0);
        email.Sent.Should().BeEmpty();
    }
}
```

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~DataDestructionDigestJobTests"`
Expected: FAIL — `DataDestructionDigestJob` yok.

`OrderDeck.LicenseServer/Services/Privacy/DataDestructionDigestJob.cs`:

```csharp
using System.Globalization;
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Bank;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>
/// İmha kayıtlarının veritabanı DIŞINDAKİ kopyası (KVKK Ö-12, Ö-13): her saat, bir önceki tam saatin
/// <see cref="DataDestructionRecord"/> satırlarını yöneticiye (<c>Admin:AlertEmail</c>) CSV metni olarak e-postalar. Yedekten geri
/// yükleme tabloyu yedek anına sarar; geri yüklemeden sonra olay kaynaklı silmeler (shopper ve yayıncı silmesi, banka veri talebi,
/// bağlantı kaldırma, kimlik değişimi, temizlik komutu) bu e-postalardan yeniden uygulanır (<c>deploy/README.md</c> "Geri
/// yükleme"). Kişisel veri YOK: tarih, lisans/konu/talep kimliği, hedef, eylem, sayı, ölçüt, tetikleyici, yapan. Kayıt yoksa
/// e-posta gitmez. Pencere <c>[önceki saat başı, bu saat başı)</c>: durum tutmaz, pencereler örtüşmez. Gönderim düşerse
/// <see cref="BankJobAlerts"/> günlüğe yazar; o saatin kayıtları tabloda durur, <see cref="RunForWindowEndingAsync"/> elle
/// yeniden koşulabilir.
/// </summary>
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class DataDestructionDigestJob
{
    public const string JobId = "data-destruction-digest";
    /// <summary>Saat başı +13 dk: 5 dakikalık ızgara, :02 (hesap yenileme) ve :07 (eşleşme taraması) dışında.</summary>
    public const string Cron = "13 * * * *";
    public const string Subject = "[OrderDeck] İmha kaydı özeti";

    private readonly LicenseDbContext _db;
    private readonly BankJobAlerts _alerts;

    public DataDestructionDigestJob(LicenseDbContext db, BankJobAlerts alerts) { _db = db; _alerts = alerts; }

    public Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return RunForWindowEndingAsync(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero), ct);
    }

    /// <summary><c>[end − 1 saat, end)</c> penceresinin kayıtlarını gönderir; gönderilen satır sayısını döner.</summary>
    public async Task<int> RunForWindowEndingAsync(DateTimeOffset end, CancellationToken ct)
    {
        var start = end.AddHours(-1);
        var rows = await _db.DataDestructionRecords.AsNoTracking()
            .Where(r => r.OccurredAt >= start && r.OccurredAt < end).OrderBy(r => r.OccurredAt).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        await _alerts.SendAsync(Subject, ToCsv(start, end, rows), CancellationToken.None);
        return rows.Count;
    }

    public static string ToCsv(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<DataDestructionRecord> rows)
    {
        static string Clean(string? value) => (value ?? "").Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"# İmha kayıtları {start.UtcDateTime:yyyy-MM-dd HH:mm}–{end.UtcDateTime:HH:mm} UTC, {rows.Count} satır. " +
            "Kişisel veri içermez. SİLMEYİN: yedekten geri yüklemeden sonra silmeler buradan yeniden uygulanır " +
            "(deploy/README.md, Geri yükleme).");
        sb.AppendLine("zaman_utc;lisans;konu;hedef;eylem;satir;olcut;tetikleyici;talep;yapan");
        foreach (var r in rows)
            sb.AppendLine(string.Join(';',
                r.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", inv), r.LicenseId?.ToString() ?? "",
                r.SubjectId?.ToString() ?? "", Clean(r.Target), Clean(r.Action), r.RowCount.ToString(inv), Clean(r.Criterion),
                Clean(r.Trigger), r.RequestId?.ToString() ?? "", Clean(r.Actor)));
        return sb.ToString();
    }
}
```

`Program.cs` — `BankJobAlerts` kaydının ALTINA:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Privacy.DataDestructionDigestJob>();
```

Genel takvimde `security-data-retention` kaydının ALTINA:

```csharp
            // İmha kayıtlarının veritabanı dışındaki kopyası (KVKK Ö-12/Ö-13): saatlik, :13. Yedekten geri yüklemeden sonra
            // silmeler bu e-postalardan yeniden uygulanır (deploy/README.md, Geri yükleme).
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Privacy.DataDestructionDigestJob>(
                OrderDeck.LicenseServer.Services.Privacy.DataDestructionDigestJob.JobId,
                j => j.RunAsync(CancellationToken.None),
                OrderDeck.LicenseServer.Services.Privacy.DataDestructionDigestJob.Cron);
```

`BankJobsDiTests.cs` — `Banka_isleri_ve_servisleri_DI_kapsamindan_cozulur`'a: `sp.GetRequiredService<OrderDeck.LicenseServer.Services.Privacy.DataDestructionDigestJob>().Should().NotBeNull();`

- [ ] **Adım 17: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~GdprFlowTests|FullyQualifiedName~ObifinPollJobTests|FullyQualifiedName~ObifinConnectionServiceTests|FullyQualifiedName~BankDataCleanup|FullyQualifiedName~DataDestructionDigestJobTests|FullyQualifiedName~BankJobsDiTests|FullyQualifiedName~Audit"`
Expected: PASS (Docker gerekli: `BankDataCleanupRelationalTests`).

Run (Docker): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerPurgeRelationalTests"`
Expected: PASS (imha kaydı purge'ün SaveChanges'inde; gerçek SQL'de de tek işlem).

- [ ] **Adım 18: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Customers/AdminCustomersController.cs OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs OrderDeck.LicenseServer/Services/Bank/BankDataCleanupJob.cs OrderDeck.LicenseServer/Tools/BankDataCleanup.cs OrderDeck.LicenseServer/Services/Privacy/DataDestructionDigestJob.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Controllers/Compliance/GdprFlowTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankDataCleanupJobTests.cs OrderDeck.LicenseServer.Tests/Services/Privacy/DataDestructionDigestJobTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankJobsDiTests.cs
git commit -m "feat(kvkk): yayıncı silmesi, kimlik değişimi ve temizlik de imha kaydı yazsın; saatlik dış kopya

Yayıncı silmesi (e-postasız, yayıncı kimliğiyle), Obifin kimliği
değişimindeki gölge veri silmesi ve bank-data-cleanup partileri kalıcı
imha kaydını silmeyle aynı işlemde yazar. data-destruction-digest her
saat bir önceki saatin kayıtlarını yöneticiye e-postalar: yedekten geri
yüklemeden sonra silmeler buradan yeniden uygulanır (KVKK ön inceleme
Ö-12, Ö-13).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 10: Saklama süreleri işlem tarihinden; eşleşmeyen 30 gün, hareket 180 gün, IBAN bağı 12 ay (P1-11)

**Neden (not K-6, Ö-14, K-4 öneri 2, karar 5):** tutar, tarih, maske, özet ve eşleştirme sonucu fiilen süresiz; süreler çekim anından (`FetchedAt`) işliyor, kanıt metni eşleştirme satırının oluşturulduğu andan; müşteri olmayan gönderenlerin açıklaması 180 gün kalıyor; IBAN bağı süresiz. Yeni tablo (kodda `BankOptions`, hepsi öneri — karar 1):

| Veri | Süre | Başlangıç |
|---|---|---|
| Ham kayıt | 30 gün | işlem tarihi |
| Eşleşmeyen havalenin açıklaması + gönderen adı | 30 gün | işlem tarihi |
| Gerekçe metni | hareketin açıklamasıyla birlikte | — |
| Hareketin tamamı + eşleştirme satırı | 180 gün | işlem tarihi |
| Gap satırı | 180 gün | oluşturma |
| IBAN–müşteri bağı | 12 ay | son doğrulanmış ödeme (`LastConfirmedAt`, yoksa oluşturma) |
| Alt sınır | çekimden sonra en az 7 gün | çekim |

"Eşleşen" = eşleştirme satırında gerçek müşteri ya da dekont bağı var (insan kararı). Eşleşenin açıklaması hareketle birlikte 180'de gider. Not "eşleştirme satırını sil ya da sayılara indir" diyor: plan **siler** (karar 3).

**Uygulama notu (Görev 10 incelemesi, 20.10):** kod aşağıdaki adımlardan şu noktalarda ayrılır. Metin işi, avukat paketi ve sonraki görevler koddakine göre yazılır.
- **`LastConfirmedAt` = doğrulanan havalenin işlem tarihi, doğrulama anı değil.** Adım 4'teki `now` yerine `ConfirmedAt(tx, now)`: `OccurredAt`, ileri tarihliyse (valör) şimdiye kırpılır. Öğrenmede de böyle: `CreatedAt` öğrenme anı, `LastConfirmedAt` öğreten havalenin işlem tarihi. Tazeleme yalnız ileri gider. Eski bir havalenin geç doğrulanması başlangıcı geri çekmez, eşleme anı da süreyi uzatmaz. Bağ "o IBAN'dan gelen son doğrulanmış havalenin işlem tarihinden 12 ay" yaşar; metin ve avukat paketi süreyi "doğrulama anından" diye yazmaz (politikanın bütün banka süreleri gibi işlem tarihinden). Adım 1'in testi `BeAfter(learnedAt)` yerine `Be(second.OccurredAt)` der; geç doğrulama, dekont onayıyla tazeleme ve ileri tarih kırpması için birer test daha var.
- **Kaldırma süreyi geri çeker (inceleme sonrası).** Tazeleme tek yönlüydü: B havalesinin eşlemesi kaldırılınca süre B'de kalıyordu. Artık eşlemesi kaldırılan havale bağın son doğrulamasıysa (`PaymentMatchReconciler.RewindIbanConfirmationAsync`, `UnmatchAsync` içinde, aynı SaveChanges) başlangıç kalan insan kararlı havalelerin en yenisine çekilir: aynı lisans ve IBAN özeti, bu müşteriye verilmiş, dışlanmamış. Kalan yoksa (öğreten havale 180 günde silinmiş, tarihi bilinmiyor) bağ silinir; öğrenme anına çekmek süreyi politikanın ötesine taşıyabilirdi (öğrenme havaleden aylar sonra olabilir). Öğreten havalenin kaldırılması bağı zaten siliyordu (spec §3). P1 öncesi, hiç tazelenmemiş bağ (`LastConfirmedAt` boş) öğrenme anından sayılır; kaldırma onu değiştirmez.
- **Ölçüt metinleri** `;` içermez (Görev 9 kuralı) ve alt sınırı yazar: hareket ve eşleşme `OccurredAt+180g, FetchedAt+7g`; ham kayıt `OccurredAt+30g, FetchedAt+7g`; eşleşmeyen `OccurredAt+30g, FetchedAt+7g, eşleşmeyen`; gerekçe metni `hareketin açıklamasıyla`; gap `CreatedAt+180g`; IBAN bağı `LastConfirmedAt+12ay`; koşu kaydı `işlem tarihi+180g / ham +30g / eşleşmeyen +30g / IBAN bağı +12ay / FetchedAt+7g`.
- **`RunAsync`** `BankRetentionResult` döndürür; kayıtta `Total` özelliği var, sıfırdan büyükse günlüğe yazılır.
- **Yapılandırma testi:** `BankJobsDiTests.Saklama_tablosunun_kalan_sureleri_yapilandirmayla_politikadaki_gibi` prod'un okuduğu `appsettings.json` değerlerini (30/180/12/7) sınar, sınıf varsayılanını değil.
- **Testler adımlardakinden geniş:** gerekçe metni testi satırın `CreatedAt`'ine bakılmadığını, geç çekim testi alt sınırın yalnız çekimden 7 gün olduğunu (`max(işlem tarihi + 180, çekim + 7)`) da sınar. İnceleme sonrası: işlem tarihi 40, çekim 10 gün önceki satırlar 2. ve 3. adımın koşulu çekim anına dönerse InMemory'de ve SQL Server'da düşer. `BankDataRetentionRelationalTests` imha kayıtlarını hedef ve satır sayısıyla birebir karşılaştırır.
- **Göç komutu** `--context LicenseDbContext` ister: `LicenseReadOnlyDbContext` de bir `LicenseDbContext`. Genel kurallardaki ve görevlerdeki komutlar buna göre güncellendi.
- **Özet düzeltmeleri:** `Program.cs` takvim yorumu; `PaymentMatcher` ve `PaymentMatchMetrics` özetlerindeki "kanıt 180 gün" artık "hareketin açıklamasıyla".
- **Prod adımları:** P1 ilk koşu tutanağına hareketle silinecek eşleşme, ham kayıt (işlem tarihinden) ve gerekçe metni sayımları eklendi; sorgular gerçek SQL Server'da koşu sonucuyla karşılaştırıldı. P0 ile P1 aynı pencerede çıkarsa P0'ın `FetchedAt` sayımı yerine bunlar kullanılır.
- **Kalan doğrulama yoksa silme imha kaydına girer (ikinci inceleme, 21.10).** Bu silme bir düzeltme değil, saklama gerekçeli imhadır: başlangıç artık belirlenemiyor (Silme Yönetmeliği md. 7/3). Kayıt silmeyle aynı SaveChanges'te yazılır: hedef `CustomerIbanMemories`, `deleted`, 1 satır, ölçüt `LastConfirmedAt+12ay, kaldırma, kalan doğrulama yok` (ay sayısı `BankOptions`'tan; bağdaştırıcının kurucusu artık `IOptions<BankOptions>` alır), tetikleyici `retention`. Yapan: `UnmatchAsync(…, ct, actor)`'ın isteğe bağlı `actor`'ü; BankaEslestirme sayfası `DataDestruction.ActorOf(User)` geçirir, boşsa `admin`. `Discard()` Added `DataDestructionRecord` girdilerini de ayırır: çakışmadan sonraki yeniden deneme kaydı iki kez yazmaz. InMemory işlemsel olmadığı için (düşen kaydın eklemeleri depoda kalır) depodaki tek kayıt `IbanBondRewindRelationalTests`'te (Testcontainers) sınanır; InMemory yarış testi izleyiciyi sınar. Öğreten hareketin bağının kaldırmayla silinmesi (spec §3, düzeltme) kayda alınmaz: Burak kararı (aşağıda karar 25).
- **Doğrulama kalıcı işaretle tutulur (ikinci inceleme).** Geri çekme kaldırılan havalenin son doğrulama olup olmadığına tarihine bakarak karar veriyor, kalanları her insan kararlı satırdan sayıyordu. Oysa yalnız tutar ve zamanla kurulan dekont bağı (`LinkAsync`'te öneri başka müşteride ya da yok, gönderen adı açıklamada yok) bağı ne öğretir ne tazeler: kalan sayılınca bağ son gerçek doğrulamadan ~90 gün fazla yaşıyor, kaldırılan böyle bir bağ son doğrulama sanılınca hâlâ geçerli bağ siliniyordu. Yeni nullable sütun `PaymentMatch.IbanConfirmedAt` (genişletme göçü `*_PaymentMatchIbanConfirmedAt`): `LearnIbanAsync` bağı öğrettiğinde ya da aynı müşteriye tazelediğinde (başlangıç ileri gitmese de) `LastConfirmedAt`'e yazdığı değeri satıra da yazar; kaldırma boşaltır. Geri çekme "son doğrulama mıydı"yı kaldırılan satırın işaretinden (işaretsizse süreyi taşımadı, dokunulmaz), kalanları `RemainingConfirmationsAsync`'ten hesaplar: işaretli satırlar ve öğreten havale (`SourceBankTransactionId`; tanımı gereği doğrulama, P1 öncesi öğrenilmişse işaretsiz, değeri işlem tarihi öğrenme anına kırpılmış). Diğer P1 öncesi kararlar işaretsiz kalır: hata yönü erken silmedir. Ucuz alternatif (kalanları durum/öneri/ad süzgeciyle seçmek) "elle eşleme, sonra dekont iliştirme" ile doğrulamasız dekont bağını ayırt edemediği için seçilmedi.
- **Kaldır onay metinleri** artık "IBAN ile müşteri bağının süresi yalnız bu havaleye dayanıyorsa bağ da silinir" der (kesme işaretsiz: JS dizesine gömülür).
- **P1 öncesi bağların başlangıcı (`bank-data-cleanup`, tek seferlik adım).** `LastConfirmedAt`'i boş bağ öğrenme anından sayılıyordu; öğrenme havaleden sonra olur (90 günlük ilk çekim, geç elle eşleme), bağ politikadaki süreyi ~90 güne kadar aşabilirdi. P1 öncesi kod bağı tazelemiyordu: öğreten havale bilinen tek doğrulamadır ve bugün hepsi duruyor (P0'da 180 günlük silme yok, Görev 8 IBAN öğreten satırı tutuyor). Komut, öğreten havale duruyorsa `LastConfirmedAt` = işlem tarihi (öğrenme anına kırpılmış; hiçbir zaman `CreatedAt`'ten sonra değil, süre uzamaz) yazar; yoksa `CreatedAt` kalır. Veri silinmez, imha kaydına girmez; sayım çıktıda (`IBAN bağı başlangıcı yazılan=`) ve tutanakta (Prod adımları 5). Öğreten havaleler 180 günlük kurala düşmeden koşturulmalı.

**Sonraki görevler için kural:** bir havaleyi kararıyla birlikte silen ya da kararını boşaltan yol, o havalenin tazelediği ama başka havalenin öğrettiği IBAN bağının başlangıcını aynı kuralla geri çeker; yoksa süre artık geçersiz bir doğrulamada kalır. Kural: yalnız `IbanConfirmedAt` işaretli kararlar süreyi taşır. Son doğrulama sorusu silinen kararların işaretinden, kalanlar `RemainingConfirmationsAsync`'ten (işaretli satırlar + öğreten havale; silinen küme dışlanır) hesaplanır, insan kararı tek başına doğrulama sayılmaz. Görev 13'ün hesap reddinde (`RejectAccountAsync`) "başka hareketten gelen bağ kalır": reddedilen hesabın havalesi o bağı tazelemişse (işaretliyse) başlangıç kalan doğrulamalardan yeniden hesaplanır (`RewindIbanConfirmationAsync`/`RemainingConfirmationsAsync` gerekirse ortak yardımcıya taşınır). Kalan yoksa bağ silinir ve silmeyle aynı SaveChanges'te imha kaydına girer: hedef `CustomerIbanMemories`, ölçüt `LastConfirmedAt+12ay, hesap reddi, kalan doğrulama yok`, tetikleyici ve yapan reddin kendisininki (`account-rejected`, yöneticinin kimliği); yeniden denenen yol düşen denemenin Added kaydını izden ayırır. Karar yazan her yeni yol IBAN'ı yalnız `LearnIbanAsync`'ten öğretir (işaret orada yazılır); kararı boşaltan her yol `IbanConfirmedAt`'i de boşaltır. Bağı doğrudan silen yollar (hizmet sonu silmesi, yayıncı silmesi, kimlik değişimi, Görev 15/17'nin kişi ve IBAN silmesi) bundan etkilenmez.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankDataRetentionJob.cs` (bütünüyle)
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankOptions.cs`
- Modify: `OrderDeck.LicenseServer/appsettings.json` (`OrderDeck:Bank`)
- Modify: `OrderDeck.LicenseServer/Domain/Bank/CustomerIbanMemory.cs`
- Modify: `OrderDeck.LicenseServer/Services/Bank/PaymentMatchReconciler.cs:333-348` (`LearnIbanAsync`)
- Create: göç `*_IbanMemoryLastConfirmedAt`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankDataRetentionJobTests.cs` (bütünüyle), YENİ `BankDataRetentionRelationalTests.cs` (Testcontainers), `PaymentMatchReconcilerTests.cs`
- İkinci inceleme (uygulama notu): Modify `Domain/Bank/PaymentMatch.cs` (`IbanConfirmedAt`), `Pages/Admin/BankaEslestirme/Index.cshtml.cs` (yapan, onay metni), `Services/Bank/BankDataCleanupJob.cs` + `Tools/BankDataCleanup.cs` (P1 öncesi bağ başlangıcı); Create göç `*_PaymentMatchIbanConfirmedAt`, YENİ `IbanBondRewindRelationalTests.cs` (Testcontainers); Test `PaymentMatchRaceTests.cs`, `AdminBankaEslestirmePageTests.cs`, `BankDataCleanupJobTests.cs`, `Tools/BankDataCleanupRelationalTests.cs`

- [ ] **Adım 1: IBAN bağı tazeleme testini yaz (kırmızı)**

`PaymentMatchReconcilerTests.cs` sınıfının sonuna:

```csharp
    [Fact]
    public async Task Ayni_musteriye_yeniden_dogrulanan_IBAN_baginin_LastConfirmedAt_tazelenir()
    {
        // KVKK K-6: IBAN bağı son doğrulanmış ödemeden 12 ay yaşar; her doğrulama süreyi tazeler.
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var first = Tx(db, s.LicenseId, 100m, DateTimeOffset.UtcNow.AddDays(-2), "EFT GELEN", hash);
        var second = Tx(db, s.LicenseId, 120m, DateTimeOffset.UtcNow.AddHours(-1), "EFT GELEN", hash);
        var recon = Recon(db);
        await recon.ManualMatchAsync(s.LicenseId, first.Id, s.WpfCustomerId, CancellationToken.None);
        var learnedAt = (await db.CustomerIbanMemories.AsNoTracking().SingleAsync()).LastConfirmedAt;
        learnedAt.Should().NotBeNull();
        await Task.Delay(20);

        await recon.ManualMatchAsync(s.LicenseId, second.Id, s.WpfCustomerId, CancellationToken.None);

        var memory = await db.CustomerIbanMemories.AsNoTracking().SingleAsync();
        memory.LastConfirmedAt.Should().BeAfter(learnedAt!.Value);
        memory.SourceBankTransactionId.Should().Be(first.Id, "ilk öğreten hareket değişmez");
    }
```

- [ ] **Adım 2: Saklama testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Bank/BankDataRetentionJobTests.cs` içeriğini bütünüyle şununla değiştir:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Banka verisi saklama tablosu (KVKK K-6, Ö-14, karar 5): süreler işlem tarihinden, çekimden en az 7 gün; her imha
/// kalıcı kayıt yazar; iş düşerse e-posta.</summary>
public sealed class BankDataRetentionJobTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>Her SaveChanges'te kaç satırın güncellendiğini kaydeder: parti sınırı kanıtı.</summary>
    private sealed class ModifiedCountInterceptor : SaveChangesInterceptor
    {
        public List<int> ModifiedPerSave { get; } = new();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ModifiedPerSave.Add(eventData.Context!.ChangeTracker.Entries().Count(e => e.State == EntityState.Modified));
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Kurulan her kaydı düşürür (yalnız <see cref="Armed"/> iken): DB kesintisi benzetimi.</summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Armed ? throw new DbUpdateException("DB kesintisi benzetimi") : ValueTask.FromResult(result);
    }

    private static LicenseDbContext NewDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-ret-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    private static AdminJobAlerts Alerts(TestEmailSender email)
        => new(email, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AdminJobAlerts.AlertEmailKey] = $"ops-{Guid.NewGuid():N}@example.test",
        }).Build(), NullLogger<AdminJobAlerts>.Instance);

    private static BankDataRetentionJob Job(LicenseDbContext db, TestEmailSender? email = null)
        => new(db, Options.Create(new BankOptions { HashKey = new string('k', 32) }), Alerts(email ?? new TestEmailSender()),
            NullLogger<BankDataRetentionJob>.Instance);

    /// <summary>İşlem tarihi <paramref name="age"/> önce; çekim aynı an ya da <paramref name="fetchedAge"/> önce.</summary>
    private static BankTransaction Row(TimeSpan age, TimeSpan? fetchedAge = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), ObifinId = Random.Shared.NextInt64(1, 1_000_000), ObifinAccountId = 1,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL",
        OccurredAt = DateTimeOffset.UtcNow - age, FetchedAt = DateTimeOffset.UtcNow - (fetchedAge ?? age),
        Description = "aciklama", CounterpartyName = "ad", RawJson = "{}",
    };

    private static BankTransaction Row(int ageDays) => Row(TimeSpan.FromDays(ageDays));

    /// <summary><paramref name="linked"/>: insan kararıyla bir müşteriye bağlı (gerçek müşteri dolu).</summary>
    private static PaymentMatch MatchFor(BankTransaction tx, bool linked, string? evidence = "kanıt metni") => new()
    {
        Id = Guid.NewGuid(), LicenseId = tx.LicenseId, BankTransactionId = tx.Id, Evidence = evidence,
        ActualWpfCustomerId = linked ? Guid.NewGuid() : null, DecidedAt = linked ? DateTimeOffset.UtcNow : null,
        Layer = PaymentMatchLayer.NameAmount, Status = linked ? PaymentMatchStatus.ManualOnly : PaymentMatchStatus.Proposed,
        CreatedAt = tx.FetchedAt, UpdatedAt = tx.FetchedAt,
    };

    private static CustomerIbanMemory Memory(DateTimeOffset createdAt, DateTimeOffset? lastConfirmedAt) => new()
    {
        Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), WpfCustomerId = Guid.NewGuid(), IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
        IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = createdAt, LastConfirmedAt = lastConfirmedAt,
    };

    [Fact]
    public async Task Ham_kayit_islem_tarihinden_30_gun_sonra_bosaltilir()
    {
        using var db = NewDb();
        var inside = Row(TimeSpan.FromDays(30) - Minute); var outside = Row(TimeSpan.FromDays(30) + Minute);
        db.BankTransactions.AddRange(inside, outside);
        db.PaymentMatches.AddRange(MatchFor(inside, linked: true), MatchFor(outside, linked: true));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        (await db.BankTransactions.FindAsync(inside.Id))!.RawJson.Should().NotBeNull("30 günün bir dakika içinde");
        var o = (await db.BankTransactions.FindAsync(outside.Id))!;
        o.RawJson.Should().BeNull("30 günü bir dakika geçti");
        o.Description.Should().Be("aciklama", "eşleşen havalenin açıklaması 180 güne kadar kalır");
    }

    [Fact]
    public async Task Eslesmeyen_havalenin_aciklamasi_ve_adi_30_gunde_eslesenin_kalir()
    {
        // KVKK K-4 öneri 2: müşteri olmayan gönderenin (pazaryeri, iade, yakın) açıklaması kısa sürede silinir.
        using var db = NewDb();
        var unmatched = Row(TimeSpan.FromDays(30) + Minute);
        var proposedOnly = Row(TimeSpan.FromDays(30) + Minute);
        var linked = Row(TimeSpan.FromDays(30) + Minute);
        var young = Row(TimeSpan.FromDays(30) - Minute);
        db.BankTransactions.AddRange(unmatched, proposedOnly, linked, young);
        db.PaymentMatches.AddRange(MatchFor(proposedOnly, linked: false), MatchFor(linked, linked: true));
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(CancellationToken.None);

        r.UnmatchedDescriptionsPurged.Should().Be(2, "öneri eşleşme değildir");
        foreach (var id in new[] { unmatched.Id, proposedOnly.Id })
        {
            var t = (await db.BankTransactions.FindAsync(id))!;
            t.Description.Should().BeNull(); t.CounterpartyName.Should().BeNull(); t.DescriptionPurgedAt.Should().NotBeNull();
            t.Amount.Should().Be(10m, "tutar ve tarih 180 güne kadar kalır");
        }
        (await db.BankTransactions.FindAsync(linked.Id))!.Description.Should().Be("aciklama");
        (await db.BankTransactions.FindAsync(young.Id))!.Description.Should().Be("aciklama");
    }

    [Fact]
    public async Task Gerekce_metni_hareketin_aciklamasiyla_birlikte_gider()
    {
        using var db = NewDb();
        var old = Row(TimeSpan.FromDays(30) + Minute); var young = Row(TimeSpan.FromDays(10));
        var oldMatch = MatchFor(old, linked: false); var youngMatch = MatchFor(young, linked: false);
        db.BankTransactions.AddRange(old, young);
        db.PaymentMatches.AddRange(oldMatch, youngMatch);
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        var o = (await db.PaymentMatches.FindAsync(oldMatch.Id))!;
        o.Evidence.Should().BeNull();
        o.Status.Should().Be(PaymentMatchStatus.Proposed, "yalnız gerekçe metni boşaltılır, satır kalır");
        o.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), "her yazan eşzamanlılık jetonunu ilerletir");
        (await db.PaymentMatches.FindAsync(youngMatch.Id))!.Evidence.Should().Be("kanıt metni");
    }

    [Fact]
    public async Task Yalniz_dekont_bagi_olan_hareket_eslesen_sayilir_aciklamasi_ve_gerekcesi_kalir()
    {
        // "Eşleşen" = gerçek müşteri YA DA dekont bağı (PaymentId). Gap çözümüyle dekonta bağlanmış ama gerçek müşterisi boş satır
        // 30 günde açıklamasını kaybetmemeli; "eşleşmeyen" sorgusunun ikinci koşulu başka hiçbir testte sınanmıyor.
        using var db = NewDb();
        var tx = Row(TimeSpan.FromDays(30) + Minute);
        var match = MatchFor(tx, linked: false);
        match.PaymentId = Guid.NewGuid();
        match.Status = PaymentMatchStatus.ConfirmedByHuman;
        db.BankTransactions.Add(tx);
        db.PaymentMatches.Add(match);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(CancellationToken.None);

        r.UnmatchedDescriptionsPurged.Should().Be(0);
        var t = (await db.BankTransactions.FindAsync(tx.Id))!;
        (t.Description, t.CounterpartyName, t.DescriptionPurgedAt).Should().Be(("aciklama", "ad", (DateTimeOffset?)null));
        (await db.PaymentMatches.FindAsync(match.Id))!.Evidence.Should().Be("kanıt metni");
    }

    [Fact]
    public async Task Hareket_eslesme_ve_gap_180_gunde_silinir()
    {
        using var db = NewDb();
        var old = Row(TimeSpan.FromDays(180) + Minute); var young = Row(TimeSpan.FromDays(180) - Minute);
        db.BankTransactions.AddRange(old, young);
        db.PaymentMatches.AddRange(MatchFor(old, linked: true), MatchFor(young, linked: true));
        var oldGap = new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), PaymentId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-181) };
        var youngGap = new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), PaymentId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-179) };
        db.PaymentMatchGaps.AddRange(oldGap, youngGap);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(CancellationToken.None);

        (r.TransactionsDeleted, r.MatchesDeleted, r.GapsDeleted).Should().Be((1, 1, 1));
        (await db.BankTransactions.Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(new[] { young.Id });
        (await db.PaymentMatches.Select(m => m.BankTransactionId).ToListAsync()).Should().BeEquivalentTo(new[] { young.Id });
        (await db.PaymentMatchGaps.Select(g => g.Id).ToListAsync()).Should().BeEquivalentTo(new[] { youngGap.Id });
    }

    [Fact]
    public async Task Gec_cekilen_eski_hareket_cekimden_7_gun_gecmeden_dokunulmaz()
    {
        // Banka geç muhasebeleştirdi: işlem tarihi 200 gün önce, çekim dün. Eşleştirme ve dekont bağdaştırması yetişsin.
        using var db = NewDb();
        var late = Row(TimeSpan.FromDays(200), fetchedAge: TimeSpan.FromDays(1));
        db.BankTransactions.Add(late);
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        var t = (await db.BankTransactions.FindAsync(late.Id))!;
        t.RawJson.Should().NotBeNull(); t.Description.Should().Be("aciklama");
    }

    [Fact]
    public async Task Iban_bagi_son_dogrulamadan_12_ay_sonra_silinir()
    {
        using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        var staleConfirmed = Memory(now.AddMonths(-20), now.AddMonths(-12).AddDays(-1));
        var recentlyConfirmed = Memory(now.AddMonths(-20), now.AddMonths(-1));
        var staleNeverConfirmed = Memory(now.AddMonths(-12).AddDays(-1), null);
        var youngNeverConfirmed = Memory(now.AddMonths(-1), null);
        db.CustomerIbanMemories.AddRange(staleConfirmed, recentlyConfirmed, staleNeverConfirmed, youngNeverConfirmed);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(CancellationToken.None);

        r.IbanMemoriesDeleted.Should().Be(2);
        (await db.CustomerIbanMemories.Select(m => m.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { recentlyConfirmed.Id, youngNeverConfirmed.Id });
    }

    [Fact]
    public async Task Her_imha_partisi_kisisel_veri_icermeyen_kalici_kaydi_lisans_basina_yazar()
    {
        // Görev 9 incelemesi: yayıncı kendi verisinin imha kanıtını isteyebilir — lisansları karıştıran parti satırın lisansı
        // başına ayrı kayıt yazar, LicenseId null kalmaz.
        using var db = NewDb();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        BankTransaction For(Guid lic, int ageDays) { var t = Row(ageDays); t.LicenseId = lic; return t; }
        var old = For(a, 200);
        db.BankTransactions.AddRange(old, For(a, 100), For(a, 100), For(b, 100));
        db.PaymentMatches.Add(MatchFor(old, linked: true));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        var records = await db.DataDestructionRecords.AsNoTracking()
            .Where(r => r.Action != DataDestruction.Actions.RunCompleted).ToListAsync();
        records.Should().OnlyContain(r => r.Trigger == DataDestruction.Triggers.Retention && r.Actor == DataDestruction.SystemActor
            && r.LicenseId != null);
        records.Should().Contain(r => r.Target == "BankTransactions" && r.Action == DataDestruction.Actions.Deleted && r.RowCount == 1
            && r.LicenseId == a);
        records.Should().Contain(r => r.Target == "PaymentMatches" && r.Action == DataDestruction.Actions.Deleted && r.RowCount == 1
            && r.LicenseId == a);
        records.Where(r => r.Target == "BankTransactions.RawJson").Select(r => (r.LicenseId, r.RowCount))
            .Should().BeEquivalentTo(new[] { ((Guid?)a, 2), ((Guid?)b, 1) });
        records.Where(r => r.Target == "BankTransactions.Description+CounterpartyName").Select(r => (r.LicenseId, r.RowCount))
            .Should().BeEquivalentTo(new[] { ((Guid?)a, 2), ((Guid?)b, 1) });
    }

    [Fact]
    public async Task Silinecek_satir_olmayan_kosu_da_kosu_kaydi_birakir()
    {
        // İç imha prosedürü §4.2-3: her çalışma kayıt bırakır, "son başarılı çalışma" tablodan (ve dış kopyadan) okunur.
        using var db = NewDb();
        db.BankTransactions.Add(Row(1));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        (await db.DataDestructionRecords.AsNoTracking().ToListAsync()).Should().ContainSingle()
            .Which.Should().Match<OrderDeck.LicenseServer.Domain.DataDestructionRecord>(r =>
                r.Action == DataDestruction.Actions.RunCompleted && r.Target == BankDataRetentionJob.JobId && r.RowCount == 0
                && r.Trigger == DataDestruction.Triggers.Retention && r.Actor == DataDestruction.SystemActor && r.LicenseId == null);
    }

    [Fact]
    public async Task Parti_boyutunu_asan_satirlar_parti_parti_bosaltilir_izleyici_birikmez()
    {
        var counter = new ModifiedCountInterceptor();
        using var db = NewDb(counter);
        var total = BankDataRetentionJob.BatchSize * 2 + 1;
        db.BankTransactions.AddRange(Enumerable.Range(0, total).Select(_ => Row(100)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        counter.ModifiedPerSave.Clear();

        var r = await Job(db).RunAsync(CancellationToken.None);

        r.RawPurged.Should().Be(total); r.UnmatchedDescriptionsPurged.Should().Be(total);
        counter.ModifiedPerSave.Should().HaveCountGreaterThan(1);
        counter.ModifiedPerSave.SkipLast(1).Should().OnlyContain(n => n > 0 && n <= BankDataRetentionJob.BatchSize);
        counter.ModifiedPerSave[^1].Should().Be(0, "son kayıt yalnız koşu kaydını ekler");
        db.ChangeTracker.Entries().Should().BeEmpty("partiler arasında ve sonda izleyici boşaltılır");
        (await db.BankTransactions.CountAsync(t => t.RawJson != null || t.Description != null || t.CounterpartyName != null))
            .Should().Be(0);
    }

    [Fact]
    public async Task Is_duserse_yoneticiye_e_posta_gider_ve_hata_yukari_cikar()
    {
        var failing = new FailingSaveInterceptor();
        var email = new TestEmailSender();
        using var db = NewDb(failing);
        db.BankTransactions.Add(Row(100));
        await db.SaveChangesAsync();
        failing.Armed = true;

        var act = () => Job(db, email).RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        var sent = email.Sent.Should().ContainSingle().Subject;
        sent.Subject.Should().Be(BankDataRetentionJob.FailureSubject);
        sent.PlainBody.Should().Contain(nameof(DbUpdateException));
        failing.Armed = false;
        (await db.DataDestructionRecords.AsNoTracking().CountAsync(r => r.Action == DataDestruction.Actions.RunCompleted))
            .Should().Be(0, "düşen koşu başarılı koşu kaydı bırakmaz");
    }
}
```

- [ ] **Adım 3: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataRetentionJobTests|FullyQualifiedName~PaymentMatchReconcilerTests"`
Expected: FAIL — `LastConfirmedAt`, `BankRetentionResult` alanları yok.

- [ ] **Adım 4: IBAN bağına son doğrulama tarihi**

(Koddaki hâli farklı: `LastConfirmedAt` = doğrulanan havalenin işlem tarihi, şimdiye kırpılır, yalnız ileri gider; kaldırma geri çeker. Bkz. görevin başındaki uygulama notu. Aşağıdaki `now` ilk taslaktır.)

`Domain/Bank/CustomerIbanMemory.cs` — `public DateTimeOffset CreatedAt { get; set; }` satırının ALTINA:

```csharp
    /// <summary>Bu bağı doğrulayan son ödemenin anı (elle eşleme ya da dekont onayı). Saklama: bundan (yoksa
    /// <see cref="CreatedAt"/>'ten) 12 ay (KVKK K-6, BankDataRetentionJob).</summary>
    public DateTimeOffset? LastConfirmedAt { get; set; }
```

`Services/Bank/PaymentMatchReconciler.cs` — `LearnIbanAsync`'i şununla değiştir:

```csharp
    private async Task LearnIbanAsync(BankTransaction tx, Guid wpfCustomerId, IbanMemorySource source, CancellationToken ct)
    {
        if (tx.CounterpartyIbanHash is null) return;
        var now = DateTimeOffset.UtcNow;
        var existing = await _db.CustomerIbanMemories.FirstOrDefaultAsync(m => m.LicenseId == tx.LicenseId && m.IbanHash == tx.CounterpartyIbanHash, ct);
        if (existing is not null)
        {
            if (existing.WpfCustomerId != wpfCustomerId)
            {
                _log.LogWarning("IBAN hafızası çelişkisi: hareket {TransactionId} başka müşteriye kayıtlı IBAN'dan geldi", tx.Id);
                return;
            }
            // KVKK K-6: bağ son doğrulanmış ödemeden 12 ay yaşar; aynı müşteriye her doğrulama süreyi tazeler.
            existing.LastConfirmedAt = now;
            return;
        }
        _db.CustomerIbanMemories.Add(new CustomerIbanMemory
        {
            Id = Guid.NewGuid(), LicenseId = tx.LicenseId, WpfCustomerId = wpfCustomerId, IbanHash = tx.CounterpartyIbanHash,
            IbanMasked = tx.CounterpartyIbanMasked ?? "?", LearnedFrom = source, SourceBankTransactionId = tx.Id,
            CreatedAt = now, LastConfirmedAt = now,
        });
    }
```

- [ ] **Adım 5: Seçenekleri değiştir**

`Services/Bank/BankOptions.cs` — `RawJsonRetentionDays` ve `DescriptionRetentionDays` satırlarını (özetleriyle) şununla değiştir:

```csharp
    // Saklama tablosu (KVKK K-6, Ö-14, not karar 5) — BankDataRetentionJob. Süreler hareketin işlem tarihinden işler.
    // Değiştirirsen gizlilik politikasının Bölüm 5'ini (iki kopya) de değiştir.

    /// <summary>Ham kayıt (beyaz listeli): 30 gün.</summary>
    public int RawJsonRetentionDays { get; set; } = 30;

    /// <summary>Bir müşteriyle eşleşmeyen havalenin açıklaması ve gönderen adı: 30 gün.</summary>
    public int UnmatchedDescriptionRetentionDays { get; set; } = 30;

    /// <summary>Hareketin tamamı, eşleştirme satırı ve gap: 180 gün.</summary>
    public int TransactionRetentionDays { get; set; } = 180;

    /// <summary>IBAN–müşteri bağı, son doğrulanmış ödemeden: 12 ay.</summary>
    public int IbanMemoryRetentionMonths { get; set; } = 12;

    /// <summary>Geç muhasebeleşen hareket çekildikten en az bu kadar gün kalır (eşleştirme ve dekont bağdaştırması yetişsin).</summary>
    public int MinDaysAfterFetch { get; set; } = 7;
```

`appsettings.json` — `"Bank"` bölümünü şununla değiştir:

```json
    "Bank": {
      "HashKey": "",
      "RawJsonRetentionDays": 30,
      "UnmatchedDescriptionRetentionDays": 30,
      "TransactionRetentionDays": 180,
      "IbanMemoryRetentionMonths": 12,
      "MinDaysAfterFetch": 7
    }
```

- [ ] **Adım 6: Saklama işini yeniden yaz**

`OrderDeck.LicenseServer/Services/Bank/BankDataRetentionJob.cs` içeriğini bütünüyle şununla değiştir:

```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Bir saklama koşusunun sayıları.</summary>
public sealed record BankRetentionResult(int RawPurged, int UnmatchedDescriptionsPurged, int EvidencePurged,
    int TransactionsDeleted, int MatchesDeleted, int GapsDeleted, int IbanMemoriesDeleted);

/// <summary>
/// Banka verisinin saklama tablosu (KVKK K-6, Ö-14, K-4 öneri 2; not karar 5). Süreler hareketin İŞLEM tarihinden
/// (<c>OccurredAt</c>) işler; geç muhasebeleşen hareket çekildikten en az <see cref="BankOptions.MinDaysAfterFetch"/> gün kalır.
/// <list type="bullet">
/// <item>Hareketin tamamı, eşleştirme satırı ve gap: <see cref="BankOptions.TransactionRetentionDays"/>. Ölçüm ekranı son 30
/// güne bakar; 180 günü geçen kayıt sayılara indirgenmeden silinir.</item>
/// <item>Ham kayıt: <see cref="BankOptions.RawJsonRetentionDays"/>.</item>
/// <item>Eşleşmeyen havalenin (gerçek müşteri ya da dekont bağı yok — öneri eşleşme değildir) açıklaması ve gönderen adı:
/// <see cref="BankOptions.UnmatchedDescriptionRetentionDays"/>. Eşleşeninki hareketle birlikte gider.</item>
/// <item>Eşleştirmenin gerekçe metni: hareketin açıklamasıyla aynı anda.</item>
/// <item>IBAN–müşteri bağı: <see cref="CustomerIbanMemory.LastConfirmedAt"/>'ten (yoksa oluşturmadan)
/// <see cref="BankOptions.IbanMemoryRetentionMonths"/> ay.</item>
/// </list>
/// Her imha partisi kişisel veri içermeyen kalıcı kaydını (<see cref="DataDestruction"/>) satırın lisansı başına, AYNI
/// SaveChanges'te yazar; başarılı koşu sonda bir koşu kaydı (<see cref="DataDestruction.RecordRun"/>) bırakır. İş düşerse
/// yöneticiye e-posta gider (<see cref="AdminJobAlerts"/>) ve istisna Hangfire'a çıkar. Partiler <see cref="BatchSize"/>'lık;
/// InMemory ExecuteUpdate/Delete desteklemediği için izlenen parti kalıbı. <c>PaymentMatch.UpdatedAt</c> eşzamanlılık
/// jetonudur: arada yazılmış eşleşmenin silinmesi düşer, iş ertesi gün yeniden dener.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 600)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class BankDataRetentionJob
{
    /// <summary>Tek SaveChanges'te en fazla bu kadar satır; partiler arasında izleyici boşaltılır.</summary>
    public const int BatchSize = 500;

    public const string JobId = "bank-data-retention";
    public const string FailureSubject = "[OrderDeck] Banka veri saklama işi düştü";

    private readonly LicenseDbContext _db;
    private readonly BankOptions _opt;
    private readonly AdminJobAlerts _alerts;
    private readonly ILogger<BankDataRetentionJob> _log;

    public BankDataRetentionJob(LicenseDbContext db, IOptions<BankOptions> opt, AdminJobAlerts alerts,
        ILogger<BankDataRetentionJob> log)
    { _db = db; _opt = opt.Value; _alerts = alerts; _log = log; }

    /// <summary>Düşerse yöneticiye e-posta (KVKK Ö-13) ve istisna Hangfire'a çıkar (koşu Failed).</summary>
    public async Task<BankRetentionResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            return await RunCoreAsync(ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.LogError(ex, "Banka veri saklama işi düştü");
            await _alerts.SendAsync(FailureSubject,
                $"bank-data-retention düştü: {ex.GetType().Name}. Hangfire panosuna bakın; iş ertesi gün yeniden koşar, " +
                "o güne kadar saklama süreleri aşılmış olur.", CancellationToken.None);
            throw;
        }
    }

    private async Task<BankRetentionResult> RunCoreAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var fetchFloor = now.AddDays(-_opt.MinDaysAfterFetch);
        var transactionCutoff = now.AddDays(-_opt.TransactionRetentionDays);
        var rawCutoff = now.AddDays(-_opt.RawJsonRetentionDays);
        var unmatchedCutoff = now.AddDays(-_opt.UnmatchedDescriptionRetentionDays);
        var memoryCutoff = now.AddMonths(-_opt.IbanMemoryRetentionMonths);

        // 1. Önce süresi dolan hareketlerin tamamı: aşağıdaki boşaltmalar onlar için boşuna koşmasın.
        var (transactions, matches) = await DeleteExpiredTransactionsAsync(transactionCutoff, fetchFloor, ct);
        var gaps = await DeleteInBatchesAsync(
            _db.PaymentMatchGaps.Where(g => g.CreatedAt < transactionCutoff).OrderBy(g => g.Id), g => g.LicenseId,
            "PaymentMatchGaps", $"CreatedAt+{_opt.TransactionRetentionDays}g", ct);

        // 2. Ham kayıt.
        var raw = await PurgeInBatchesAsync(
            _db.BankTransactions.Where(t => t.RawJson != null && t.OccurredAt < rawCutoff && t.FetchedAt < fetchFloor)
                .OrderBy(t => t.Id), t => t.LicenseId,
            t => t.RawJson = null, "BankTransactions.RawJson", $"OccurredAt+{_opt.RawJsonRetentionDays}g", ct);

        // 3. Eşleşmeyen havalenin açıklaması ve gönderen adı.
        var linked = _db.PaymentMatches.Where(m => m.ActualWpfCustomerId != null || m.PaymentId != null)
            .Select(m => m.BankTransactionId);
        var unmatched = await PurgeInBatchesAsync(
            _db.BankTransactions.Where(t => t.DescriptionPurgedAt == null && t.OccurredAt < unmatchedCutoff
                    && t.FetchedAt < fetchFloor && !linked.Contains(t.Id))
                .OrderBy(t => t.Id), t => t.LicenseId,
            t => { t.Description = null; t.CounterpartyName = null; t.DescriptionPurgedAt = now; },
            "BankTransactions.Description+CounterpartyName", $"OccurredAt+{_opt.UnmatchedDescriptionRetentionDays}g;eşleşmeyen", ct);

        // 4. Gerekçe metni hareketin açıklamasıyla gider. UpdatedAt jetondur, her yazan ilerletir.
        var evidence = await PurgeInBatchesAsync(
            _db.PaymentMatches.Where(m => m.Evidence != null
                    && _db.BankTransactions.Any(t => t.Id == m.BankTransactionId && t.DescriptionPurgedAt != null))
                .OrderBy(m => m.Id), m => m.LicenseId,
            m => { m.Evidence = null; m.UpdatedAt = now; }, "PaymentMatches.Evidence", "hareketin açıklamasıyla", ct);

        // 5. IBAN–müşteri bağı.
        var memories = await DeleteInBatchesAsync(
            _db.CustomerIbanMemories.Where(m => (m.LastConfirmedAt ?? m.CreatedAt) < memoryCutoff).OrderBy(m => m.Id),
            m => m.LicenseId, "CustomerIbanMemories", $"LastConfirmedAt+{_opt.IbanMemoryRetentionMonths}ay", ct);

        var result = new BankRetentionResult(raw, unmatched, evidence, transactions, matches, gaps, memories);
        if (raw + unmatched + evidence + transactions + matches + gaps + memories > 0)
            _log.LogInformation("Banka veri saklama: {Result}", result);
        // Başarılı koşunun kaydı, silecek satır olmasa da (Görev 9 incelemesi; iç imha prosedürü §4.2-3).
        DataDestruction.RecordRun(_db, JobId, $"işlem tarihi+{_opt.TransactionRetentionDays}g / ham, eşleşmeyen " +
            $"+{_opt.RawJsonRetentionDays}g / IBAN bağı +{_opt.IbanMemoryRetentionMonths}ay");
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return result;
    }

    /// <summary>Süresi dolan hareketleri eşleşme satırlarıyla birlikte siler; her parti imha kaydını satırın lisansı başına
    /// yazar.</summary>
    private async Task<(int Transactions, int Matches)> DeleteExpiredTransactionsAsync(DateTimeOffset cutoff,
        DateTimeOffset fetchFloor, CancellationToken ct)
    {
        int transactions = 0, matches = 0;
        while (true)
        {
            var batch = await _db.BankTransactions.Where(t => t.OccurredAt < cutoff && t.FetchedAt < fetchFloor)
                .OrderBy(t => t.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            var ids = batch.Select(t => t.Id).ToList();
            var rows = await _db.PaymentMatches.Where(m => ids.Contains(m.BankTransactionId)).ToListAsync(ct);
            _db.PaymentMatches.RemoveRange(rows);
            _db.BankTransactions.RemoveRange(batch);
            var criterion = $"OccurredAt+{_opt.TransactionRetentionDays}g";
            RecordPerLicense(batch.Select(t => t.LicenseId), "BankTransactions", DataDestruction.Actions.Deleted, criterion);
            RecordPerLicense(rows.Select(m => m.LicenseId), "PaymentMatches", DataDestruction.Actions.Deleted, criterion);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            transactions += batch.Count;
            matches += rows.Count;
            if (batch.Count < BatchSize) break;
        }
        return (transactions, matches);
    }

    /// <summary>Partinin imha kaydı satırın lisansı başına (Görev 9 incelemesi: yayıncı kendi verisinin imha kanıtını
    /// isteyebilir). İzleyiciye ekler; partinin SaveChanges'i yazar.</summary>
    private void RecordPerLicense(IEnumerable<Guid> rowLicenses, string target, string action, string criterion)
    {
        foreach (var license in rowLicenses.GroupBy(l => l))
            DataDestruction.Record(_db, license.Key, target, action, license.Count(), criterion,
                DataDestruction.Triggers.Retention, DataDestruction.SystemActor);
    }

    /// <summary>Sıralı sorgunun seçtiği satırları partiler hâlinde siler; silinen satır sorgudan düşer.</summary>
    private async Task<int> DeleteInBatchesAsync<T>(IQueryable<T> expired, Func<T, Guid> licenseOf, string target,
        string criterion, CancellationToken ct) where T : class
    {
        var total = 0;
        while (true)
        {
            var batch = await expired.Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            _db.RemoveRange(batch);
            RecordPerLicense(batch.Select(licenseOf), target, DataDestruction.Actions.Deleted, criterion);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize) return total;
        }
    }

    /// <summary>Sorgunun seçtiği satırları boşaltır. Sorgu, boşaltılan satırı artık seçmeyecek biçimde yazılmalı (boşaltılan
    /// alan filtrenin içinde): her parti baştan sorgulanır.</summary>
    private async Task<int> PurgeInBatchesAsync<T>(IQueryable<T> expired, Func<T, Guid> licenseOf, Action<T> purge,
        string target, string criterion, CancellationToken ct) where T : class
    {
        var total = 0;
        while (true)
        {
            var batch = await expired.Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            foreach (var row in batch) purge(row);
            RecordPerLicense(batch.Select(licenseOf), target, DataDestruction.Actions.Nulled, criterion);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize) return total;
        }
    }
}
```

- [ ] **Adım 7: Takvim yorumunu güncelle**

`Program.cs` — `ScheduleBankJobs` içindeki `// Ham JSON 90 gün, açıklama 180 gün (spec §5).` yorumunu `// Saklama tablosu: BankDataRetentionJob özeti (KVKK karar 5).` yap; özetteki "90/180 günlük silme" ifadesini "saklama tablosundaki silme" yap.

- [ ] **Adım 8: Göçü üret ve incele**

Run: `cd OrderDeck.LicenseServer && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add IbanMemoryLastConfirmedAt --context LicenseDbContext --output-dir Data/Migrations`
Expected: `Up` yalnız `AddColumn<DateTimeOffset>(name: "LastConfirmedAt", table: "CustomerIbanMemories", nullable: true)`.

- [ ] **Adım 9: Gerçek SQL Server testi**

Bu iş her gece prod verisi siler; alt sorgular ("eşleşmeyen" = bağlı eşleşmesi olmayan; gerekçe metni için hareket üzerinde
EXISTS), `COALESCE(LastConfirmedAt, CreatedAt)`, jetonlu eşleşme silmesi ve imha kaydının aynı işlemde yazılması InMemory'de
SQL'e çevrilmez. `OrderDeck.LicenseServer.Tests/Services/Bank/BankDataRetentionRelationalTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>BankDataRetentionJob gerçek SQL Server'da (göçlerle kurulmuş şema, FK'ler, CHECK kısıtları): saklama tablosu
/// uygulanır, her imha partisi kaydını aynı işlemde yazar.</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankDataRetentionRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    public BankDataRetentionRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Saklama_tablosu_gercek_SQL_Serverda_uygulanir_ve_imha_kaydi_yazar()
    {
        var now = DateTimeOffset.UtcNow;
        var lic = Guid.NewGuid();
        long obifinId = 1;
        BankTransaction Tx(int ageDays) => new()
        {
            Id = Guid.NewGuid(), LicenseId = lic, ObifinId = obifinId++, ObifinAccountId = 6, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now.AddDays(-ageDays),
            FetchedAt = now.AddDays(-ageDays), Description = "aciklama", CounterpartyName = "ad", RawJson = "{}",
        };
        PaymentMatch Match(BankTransaction tx, Guid? actual) => new()
        {
            Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id, ActualWpfCustomerId = actual, Evidence = "kanıt",
            DecidedAt = actual is null ? null : now, Layer = PaymentMatchLayer.NameAmount,
            Status = actual is null ? PaymentMatchStatus.Proposed : PaymentMatchStatus.ManualOnly,
            CreatedAt = tx.FetchedAt, UpdatedAt = tx.FetchedAt,
        };
        CustomerIbanMemory Memory(DateTimeOffset createdAt, DateTimeOffset? lastConfirmedAt) => new()
        {
            Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(), IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
            IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = createdAt,
            LastConfirmedAt = lastConfirmedAt,
        };
        var expired = Tx(181); var unmatched = Tx(31); var linked = Tx(31); var young = Tx(10);
        var expiredMatch = Match(expired, Guid.NewGuid()); var unmatchedMatch = Match(unmatched, null);
        var linkedMatch = Match(linked, Guid.NewGuid());
        var staleConfirmed = Memory(now.AddMonths(-20), now.AddMonths(-12).AddDays(-1));
        var staleNeverConfirmed = Memory(now.AddMonths(-12).AddDays(-1), null);
        var fresh = Memory(now.AddMonths(-20), now.AddMonths(-1));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = new Customer { Id = Guid.NewGuid(), Email = $"br-{Guid.NewGuid():N}@example.test", Name = "Saklama",
                PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now };
            db.Customers.Add(customer);
            db.Licenses.Add(new License { Id = lic, CustomerId = customer.Id, LicenseKey = "br-" + Guid.NewGuid().ToString("N")[..12],
                SkuCode = "STD", ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddYears(1) });
            db.BankTransactions.AddRange(expired, unmatched, linked, young);
            db.PaymentMatches.AddRange(expiredMatch, unmatchedMatch, linkedMatch);
            db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(),
                CreatedAt = now.AddDays(-181) });
            db.CustomerIbanMemories.AddRange(staleConfirmed, staleNeverConfirmed, fresh);
            await db.SaveChangesAsync();
        }

        BankRetentionResult r;
        using (var scope = _factory.Services.CreateScope())
            r = await scope.ServiceProvider.GetRequiredService<BankDataRetentionJob>().RunAsync(CancellationToken.None);

        (r.TransactionsDeleted, r.MatchesDeleted, r.GapsDeleted, r.IbanMemoriesDeleted).Should().Be((1, 1, 1, 2));
        r.UnmatchedDescriptionsPurged.Should().Be(1, "öneri eşleşme değildir; bağlı olanın açıklaması kalır");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.BankTransactions.AnyAsync(t => t.Id == expired.Id)).Should().BeFalse();
            (await db.PaymentMatches.AnyAsync(m => m.Id == expiredMatch.Id)).Should().BeFalse();
            var u = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == unmatched.Id);
            (u.Description, u.CounterpartyName, u.RawJson).Should().Be(((string?)null, (string?)null, (string?)null));
            (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == unmatchedMatch.Id)).Evidence
                .Should().BeNull("gerekçe metni hareketin açıklamasıyla gider");
            var l = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == linked.Id);
            l.Description.Should().Be("aciklama");
            l.RawJson.Should().BeNull("ham kayıt 30 günde gider");
            (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == linkedMatch.Id)).Evidence.Should().Be("kanıt");
            (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == young.Id)).RawJson.Should().Be("{}");
            (await db.CustomerIbanMemories.Select(m => m.Id).ToListAsync()).Should().BeEquivalentTo(new[] { fresh.Id });
            var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
            records.Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.Retention);
            records.Select(x => x.Target).Should().Contain(new[] { "BankTransactions", "PaymentMatches", "PaymentMatchGaps",
                "BankTransactions.RawJson", "BankTransactions.Description+CounterpartyName", "PaymentMatches.Evidence",
                "CustomerIbanMemories" });
        }
    }
}
```

- [ ] **Adım 10: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Services.Bank|FullyQualifiedName~BankJobsDiTests"`
Expected: PASS (Docker gerekli: `BankDataRetentionRelationalTests`; zaman aşımında sınıfı tek başına yeniden koştur).

Run (Docker): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataCleanupRelationalTests|FullyQualifiedName~BankDataRetentionRelationalTests"`
Expected: PASS.

- [ ] **Adım 11: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/BankDataRetentionJob.cs OrderDeck.LicenseServer/Services/Bank/BankOptions.cs OrderDeck.LicenseServer/appsettings.json OrderDeck.LicenseServer/Domain/Bank/CustomerIbanMemory.cs OrderDeck.LicenseServer/Services/Bank/PaymentMatchReconciler.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer/Data/Migrations OrderDeck.LicenseServer.Tests/Services/Bank/BankDataRetentionJobTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankDataRetentionRelationalTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/PaymentMatchReconcilerTests.cs
git commit -m "fix(bank-kvkk): banka saklama sürelerini işlem tarihine bağla ve kısalt

Ham kayıt ve eşleşmeyen havalenin açıklaması 30, hareketin tamamı,
eşleşme ve gap satırı 180 gün; gerekçe metni açıklamayla gider; IBAN bağı
son doğrulanmış ödemeden 12 ay (LastConfirmedAt). Çekimden en az 7 gün
alt sınır (KVKK ön inceleme K-6, Ö-14, P1-11).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 11: Yalnız geçerli IBAN özetlenir; maskeli IBAN sayacı ve temizliği (P1-12)

**Neden (not Ö-7, Ö-6, K-7):** bankalar gönderen IBAN'ını maskeli verebilir (TCMB: rıza yoksa alıcının ekstresinde maskeli). `NormalizeIban` yalnız uzunluk ≥ 8 istiyor; maskeli değer de özetleniyor, aynı ilk/son karakterleri taşıyan farklı gönderenler aynı özete düşüyor ve IBAN hafızası farklı kişileri tek müşteriye bağlıyor. Artık yalnız tam ve geçerli IBAN özetlenir: harf/rakam, ülke + kontrol biçimi, uzunluk 15–34 (TR'de 26), ISO 7064 mod-97 = 1 (`HasValidIbanChecksum`, Görev 4). TR BBAN'ı harf içerebilir: "TR'den sonra yalnız rakam" koşulu konmaz (ama banka kodu ve rezerv hane rakam, aynı harfin 3+ dizisi maske sayılır: uygulama notu, karar 26). Banka hesabı başına tam/maskeli/boş sayacı tutulur (değer yazılmadan).

- **Maske de aynı kuralla (Ö-6):** `MaskIban` geçerli IBAN almadıysa (`Classify != Full`) `ShortValueMask` döner — Görev 6'daki "15'ten kısa" kuralının sıkılaşmışı: 15+ karakterlik IBAN olmayan değer de 7 karakteriyle görünmez.
- **Mevcut özetler:** maskesi maskeli kaynağı ele veren özetler `bank-data-cleanup` ile boşaltılır ve o özetle öğrenilmiş hafıza silinir. Maskeden anlaşılamayanlar için `--reset-iban-memory` **yalnız `CustomerIbanMemories`'i siler; hareketlerdeki özetler KALIR** (karar 5). Özet boşaltılsaydı IBAN talep aracı (Görev 17) o hareketleri — 180 güne kadar duran tutar, tarih, maske, açıklama ve ad — bulamaz, "veri yok" derdi; Shopper hesabı olmayanların tek yolu bu (K-7). Maskeli bir değerin özeti gerçek bir IBAN'ın özetine hiçbir zaman eşit olmadığı için kalan özetler talep aramasını da bozmaz.
- **Yeniden öğrenme kapısı:** sıfırlamadan sonra eski hareketlerdeki (maskeli kaynaklı olabilecek) özet, elle eşleme ya da dekont onayıyla hafızaya yeniden girmemeli. Yeni `BankTransaction.CounterpartyIbanHashValid` bayrağı: çekim işi bu görevden sonra yalnız geçerli IBAN'ı özetler ve bayrağı yazar; `LearnIbanAsync` yalnız bayraklı hareketten öğrenir. Göç bayrağı eski satırlarda `false` bırakır: kesme anından önce çekilmiş hareket bir daha IBAN öğretmez.

**Uygulama notu (Görev 11 incelemesi, 21.10):** kod aşağıdaki adımlardan şu noktalarda ayrılır. Metin işi, avukat paketi ve sonraki görevler koddakine göre yazılır.
- **Harfle maskelenmiş IBAN (inceleme).** Adım 3'ün `NormalizeIban`'ı `X` ile maskelenmiş değeri yalnız mod-97 ile eliyordu: değer harf/rakam, 26 karakter ve TR biçimi denetimlerinden geçer, kontrol hanesi ~1/97 olasılıkla tutar (sentetik denemede %1,03). Tutan değer tam sayılıp özetleniyor, `CounterpartyIbanHashValid = true` alıyor ve maskesi temiz göründüğü için (`TR12…456`) `LooksMasked`'tan kaçıyordu; `--reset-iban-memory` bağı silse de aynı hareket kapıdan geçtiği için bağ yeniden öğrenilirdi. Artık TR'de banka kodu ve rezerv hane rakam (SWIFT IBAN kaydı `TR2!n5!n1!n16!c`) ve BBAN'da aynı harften `BankHasher.MaskLetterRun` (3) ya da daha uzun dizi maske sayılır (küçük harf de, yabancı IBAN'da da). Hesap kısmındaki meşru harf kabul edilmeye devam eder (Adım 1'in `Harf_iceren_…` testi; iki aynı harf de geçerli). Daha sıkı seçenek ("TR'de hesap kısmı da yalnız rakam") karar 26'da. Rakamla maskeleyen banka iki kuralla da ayırt edilemez; hesabın maskeli sayacı o bankayı gösterir. Açıklama maskesi (`BankDescriptionMasker`) aynı `MaskIban`'ı kullanır: açıklamadaki böyle bir değer `…` olur (yine maskeli). Testler `Harfle_maskelenmis_…` (`BankHasherTests`, `ObifinPollJobTests`).
- **Şüphe bağın kendi maskesinden de okunur.** Adım 10 yalnız hareketin maskesine bakıyordu. Bağ 12 ay, öğreten havale 180 gün yaşar: öğreten havalesi silinmiş maskeli kaynaklı bağ yalnız kendi maskesinden tanınır. Şüpheli (lisans, özet) çiftleri hareketlerden ve bağlardan birlikte toplanır. Test `Kendi_maskesi_maskeli_kaynagi_ele_veren_bag_…`.
- **`LooksMasked("?")` = false.** Yer tutucu maskenin kaynağı bilinmez; plan kodu `true` dönerdi (Adım 9'un Theory'si "?"yi kapsamıyordu). Böyle bağ `--reset-iban-memory`'ye kalır. İnceleme sonrası `LooksMasked(m)` = `m == ShortValueMask || NeedsRemask(m)`: biçim denetimi tek yerde. Kuru koşu ile uygulamanın aynı sayıları vermesi bu eşdeğerliğe dayanır: uygulamada önceki adım (Ö-6) eski maskeyi `…`'ya çevirir, kuru koşuda eski maske kalır, ikisi de şüphelidir. Theory satırları eşdeğerliği de sınar.
- **Sıra ve geri alma.** Önce bağlar silinir, sonra hareketlerin özeti boşaltılır: bağ partisi düşerse yeniden koşu şüpheyi yine hareketlerden bulur (ters sırada özetler boşalmış, temiz maskeli bağ kalmış olurdu). Bağ silme partileri `TrySaveDeletionAsync`'ten geçer: satır okunduktan sonra eşzamanlı bir yol (eşleme kaldırma, saklama işi) onu sildiyse parti imha kaydıyla birlikte geri alınır, `FailedBatches` artar, komut yeniden çalıştırılır (çıkış 1). Özet boşaltma `CounterpartyIbanHashValid`'i de `false` yapar.
- **Kuru koşu = uygulama.** `_dryRunDeletedMemories` (hareketlerdeki `_dryRunDeleted`'ın karşılığı): kuru koşuda maskeli kaynak ve sıfırlama adımlarının sileceği bağlar işaretlenir, sonraki adımlar onları saymaz. Görev 10'un P1 öncesi başlangıç adımı (`BackfillIbanConfirmationAsync`) bu ikisinden sonra koşar ve silinen bağı saymaz (kuru koşuda da). Testler kuru koşunun raporunu uygulamanınkiyle birebir karşılaştırır.
- **Sıfırlama geçerli IBAN'lı bağı tutar (inceleme).** Adım 10'un kodu bütün bağları siliyordu: P1'den sonra geçerli IBAN'lı hareketten öğrenilmiş bağlar da gidiyor ve geri gelmiyordu (kararı verilmiş hareket yeniden öğretmez; o IBAN'dan yeni havale gelirse döner). Komut ne kadar geç koşulursa o kadar geçerli bağ kaybolurdu; karar 5'in "yeniden öğrenilir" cümlesi bunu karşılamıyordu. Artık özeti aynı lisansta `CounterpartyIbanHashValid` bir harekette görünen bağ kalır: maskeli değerin özeti geçerli IBAN'ınkine eşit olamaz, çünkü bayrak yalnız `NormalizeIban`'ı geçen değerde yazılır (harfle maskeleme düzeltmesi bu yüzden önce geldi). Kanıt yalnız aynı lisansta aranır (`--license` kapsamı; HMAC anahtarı P2'de lisansa özgü olabilir). Sınır: kanıt hareketle 180 gün yaşar; o IBAN'dan son 180 günde geçerli havale gelmemiş bağ silinir (fazlası silinir, KVKK yönünde güvenli). Silme partileri kimlik anlık görüntüsüyle gezilir (Adım 10'un `Take` döngüsü kalan bağlarda dönerdi). Çıktı: `sıfırlanan IBAN hafızası=N (geçerli IBAN'lı kalan=M)` (`Report.IbanMemoriesKeptValid`, sıfırlama istenmediyse 0). Testler `Reset_iban_memory_ayni_lisansta_gecerli_IBANli_…` ve `BankDataCleanupRelationalTests` (Testcontainers).
- **`LearnIbanAsync`'in kapısı tazelemeyi de keser.** Bayraksız hareket bağı ne öğretir ne tazeler; kararı `IbanConfirmedAt` ile işaretlenmez (Görev 10'un geri çekme kuralı onu doğrulama saymaz).
- **Testler adımlardakinden geniş:** giden hareket sayaca girmez; IBAN öğrenmesini ya da tazelemesini bekleyen test dosyalarının hareketleri bayraklı (`PaymentMatchRaceTests`, `IbanBondRewindRelationalTests`, `AdminBankaEslestirmePageTests`); admin sayfa testi sayaç hücresini (`data-cell="iban-quality"`, satır `data-license-accounts`) okur; `Tools/BankDataCleanupRelationalTests` maskeli kaynak ve sıfırlamayı gerçek SQL Server'da sınar.

**Sonraki görevler için kural (Görev 12, 13, 14):** çekim artık her sayfada `BankAccounts` satırına da yazar (IBAN niteliği sayacı; Görev 12'de `UnapprovedSkippedCount`). Hesap satırı tur boyunca izleyicide kalır, sayfanın kaydı onu günceller. Hesap satırını silen bir yol (kimlik değişimi `ResetShadowData`, Görev 13'ün yerel silmesi ve "Kaldır ve sil", Görev 14'ün hizmet sonu silmesi) çekimle yarışırsa o sayfanın kaydı `DbUpdateConcurrencyException` ile düşer (güncellenen satır 0). Sayfanın hareketleri ve sayaçları birlikte geri alınır, tur hata verir (`LastError`'a tür adı). Sonraki tur sorunu kendisi düzeltir: imleçten yeniden çeker, önceki sayfalarda yazılmış hareket `existing` denetiminde sayaçtan ÖNCE atlanır (sayaç iki kez sayılmaz), silinmiş hesap yer tutucuyla (Görev 12'den sonra onaysız) yeniden açılır. Kurallar: (1) Görev 12'nin onaysız hesap süzgeci sayaç satırından (`switch (BankHasher.Classify(…))`) ÖNCE gelir (Görev 12 Adım 4 öyle): onaysız hesabın hareketi IBAN niteliği sayacına girmez. (2) Hareketin yazılıp yazılmayacağına karar veren her yeni süzgeç de sayaçtan önce gelir: sayaç yalnız yazılan hareketi sayar. (3) Görev 13'ün "arada koşan çekim turu o hesabın yeni hareketini yazmaz" sözü hesap satırının sayfa başına taze okunmasına dayanır. Satır tur boyunca izlendiği için sonraki sayfanın sorgusu izlenen örneği döndürür (EF kimlik çözümü değerleri tazelemez): ret testi onayın tur ortasında, iki sayfa arasında kaldırılmasını da sınamalı.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankHasher.cs` (`NormalizeIban`, `MaskIban`, yeni `Classify`, `IbanQuality`)
- Modify: `OrderDeck.LicenseServer/Domain/Bank/BankAccount.cs`, `OrderDeck.LicenseServer/Domain/Bank/BankTransaction.cs` (`CounterpartyIbanHashValid`)
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs` (`UpsertAsync`), `PaymentMatchReconciler.cs` (`LearnIbanAsync` kapısı)
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Obifin/Index.cshtml(.cs)` (hesap satırı)
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankDataCleanupJob.cs`, `OrderDeck.LicenseServer/Tools/BankDataCleanup.cs`
- Create: göç `*_IbanQualityCountersAndHashValid`
- Test: `BankHasherTests.cs`, `ObifinPollJobTests.cs`, `PaymentMatchReconcilerTests.cs`, `BankDataCleanupJobTests.cs`, `Tools/BankDataCleanupTests.cs`
- İnceleme (uygulama notu): Test `Tools/BankDataCleanupRelationalTests.cs` (Testcontainers), `Pages/Admin/AdminObifinPageTests.cs`, `PaymentMatchRaceTests.cs`, `IbanBondRewindRelationalTests.cs`, `Pages/Admin/AdminBankaEslestirmePageTests.cs`

- [ ] **Adım 1: Doğrulama ve maske testlerini yaz (kırmızı)**

(`TestIban()` Görev 4'ten beri mod-97 geçerli; `WithCheckDigits`, `WrongCheckDigits` orada.)

`BankHasherTests.cs` — `Maske_bosluklu_kucuk_harfli_girdiyi_normalize_ederek_maskeler`'deki sabit IBAN'ı geçerli üret (maske artık yalnız geçerli IBAN'da 7 karakter gösterir):

```csharp
        var iban = WithCheckDigits("TR", string.Concat(Enumerable.Range(1, 22).Select(i => i % 10)));
        var messy = " " + string.Join(" ", iban.Chunk(4).Select(c => new string(c))).ToLowerInvariant() + " ";

        BankHasher.MaskIban(messy).Should().Be(iban[..4] + "…" + "012");
```

Sınıfın sonuna:

```csharp
    [Fact]
    public void Gecerli_TR_IBAN_tam_sayilir_ozetlenir_ve_ilk_dort_son_uc_karakterle_maskelenir()
    {
        var iban = TestIban();
        BankHasher.Classify(iban).Should().Be(IbanQuality.Full);
        NewHasher().HashIban(iban).Should().HaveLength(64);
        BankHasher.MaskIban(iban).Should().Be(iban[..4] + "…" + iban[^3..]);
    }

    [Fact]
    public void Maskeli_kisaltilmis_ya_da_kontrol_hanesi_yanlis_IBAN_ozetlenmez_maskesi_karakter_gostermez()
    {
        // KVKK Ö-7: maskeli değer özetlenseydi farklı gönderenler aynı özete düşerdi. Ö-6: IBAN olmayan değerin maskesi
        // hiçbir karakterini göstermez (15+ karakterlik hesap numarası dahil).
        var iban = TestIban();
        var h = NewHasher();
        var bad = new[]
        {
            iban[..10] + "******" + iban[16..],
            iban[..4] + "…" + iban[^3..],
            iban[..25],
            iban + "0",
            WrongCheckDigits(iban),
            TestPhone.Digits(16),
        };
        foreach (var value in bad)
        {
            h.HashIban(value).Should().BeNull(value.Length.ToString());
            BankHasher.Classify(value).Should().Be(IbanQuality.Masked);
            BankHasher.MaskIban(value).Should().Be(BankHasher.ShortValueMask, value.Length.ToString());
        }
        BankHasher.Classify(null).Should().Be(IbanQuality.Empty);
        BankHasher.Classify("   ").Should().Be(IbanQuality.Empty);
    }

    [Fact]
    public void Harf_iceren_TR_BBAN_kontrol_hanesi_dogruysa_gecerlidir()
    {
        // TR BBAN'ın son 16 karakteri standarda göre harf de içerebilir: "TR'den sonra yalnız rakam" koşulu yok.
        var bban = TestPhone.Digits(5) + "0" + "ABCD" + TestPhone.Digits(12);
        BankHasher.NormalizeIban(WithCheckDigits("TR", bban)).Should().NotBeNull();
    }

    [Fact]
    public void Yabanci_IBAN_kendi_uzunlugunda_kabul_edilir()
    {
        var de = WithCheckDigits("DE", TestPhone.Digits(18));
        BankHasher.NormalizeIban(de.ToLowerInvariant()).Should().Be(de);
    }
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankHasherTests"`
Expected: FAIL — `IbanQuality`, `Classify` yok.

- [ ] **Adım 3: Doğrulayıcıyı yaz**

`Services/Bank/BankHasher.cs` — namespace satırının altına, sınıftan önce:

```csharp
/// <summary>Gelen IBAN'ın niteliği; banka hesabı başına sayılır (KVKK Ö-7). Değer yazılmaz, yalnız sınıf.</summary>
public enum IbanQuality { Empty = 0, Masked = 1, Full = 2 }
```

`NormalizeIban` metodunu şununla değiştir ve altına `Classify` ekle (kontrol hanesi denetimi Görev 4'teki `HasValidIbanChecksum`):

```csharp
    /// <summary>
    /// Yalnız tam ve geçerli IBAN normalize edilir (KVKK Ö-7): boşluk atılır, büyük harfe çevrilir; yalnız ASCII harf ve rakam;
    /// 2 harf ülke + 2 rakam kontrol; uzunluk 15–34, TR'de tam 26; ISO 7064 MOD 97-10 kalanı 1. Aksi hâlde null. Maskeli değer
    /// (ör. <c>TR12****…</c>) özetlenseydi aynı ilk ve son karakterleri taşıyan farklı gönderenler aynı özete düşer, IBAN
    /// hafızası farklı kişileri tek müşteriye bağlardı. TR BBAN'ı standarda göre harf içerebilir: "TR'den sonra yalnız rakam"
    /// koşulu BİLEREK yok.
    /// </summary>
    public static string? NormalizeIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return null;
        var compact = Compact(iban);
        if (compact.Length < MinIbanLength || compact.Length > 34) return null;
        foreach (var c in compact)
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c)) return null;
        if (!char.IsAsciiLetterUpper(compact[0]) || !char.IsAsciiLetterUpper(compact[1])
            || !char.IsAsciiDigit(compact[2]) || !char.IsAsciiDigit(compact[3])) return null;
        if (compact.StartsWith("TR", StringComparison.Ordinal) && compact.Length != 26) return null;
        return HasValidIbanChecksum(compact) ? compact : null;
    }

    /// <summary>Boş → <see cref="IbanQuality.Empty"/>; geçerli → <see cref="IbanQuality.Full"/>; dolu ama geçersiz (maskeli,
    /// kısaltılmış, bozuk, hesap numarası) → <see cref="IbanQuality.Masked"/>.</summary>
    public static IbanQuality Classify(string? iban)
        => string.IsNullOrWhiteSpace(iban) ? IbanQuality.Empty
            : NormalizeIban(iban) is null ? IbanQuality.Masked : IbanQuality.Full;
```

`MaskIban`'ı (Görev 6) şununla değiştir:

```csharp
    /// <summary>`TR12…345` — yalnız görüntü, YALNIZ geçerli IBAN için (<see cref="Classify"/> = Full). Ham girdi ASLA dönmez:
    /// boş/boşluk → ""; maskeli, kısaltılmış, bozuk ya da hesap numarası → <see cref="ShortValueMask"/> (KVKK Ö-6: IBAN olmayan
    /// değerin hiçbir karakteri "maske" sütununda görünmez).</summary>
    public static string MaskIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return "";
        var normalized = NormalizeIban(iban);
        return normalized is null ? ShortValueMask : normalized[..4] + "…" + normalized[^3..];
    }
```

`HashIban`'ın özetini şununla değiştir: `/// <summary>Yalnız geçerli IBAN özetlenir (<see cref="NormalizeIban"/>); boşluk/küçük harf farkı özeti değiştirmez; boş, maskeli ya da geçersiz değer null döner.</summary>`

- [ ] **Adım 4: Koştur — hasher yeşil, bankanın geri kalanı da**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Services.Bank|FullyQualifiedName~Admin"`
Expected: PASS (testler `TestIban()` kullanıyor, Görev 4'ten beri geçerli). Kırmızı varsa: maske beklentisini sabit, geçersiz bir IBAN'dan kuran test — değeri `WithCheckDigits` ile üret.

- [ ] **Adım 5: Çekim sayacı testini yaz (kırmızı)**

`ObifinPollJobTests.cs` sınıfının sonuna:

```csharp
    [Fact]
    public async Task Maskeli_IBAN_ozetlenmez_hesapta_tam_maskeli_bos_sayilir()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var iban = BankHasherTests.TestIban();
        var masked = iban[..6] + "************" + iban[18..];
        client.Transactions.Add(Tx(31, new DateTime(2026, 9, 25, 9, 0, 0), 10m, iban: iban));
        client.Transactions.Add(Tx(32, new DateTime(2026, 9, 25, 9, 1, 0), 10m, iban: masked));
        client.Transactions.Add(Tx(33, new DateTime(2026, 9, 25, 9, 2, 0), 10m, iban: null));

        await job.RunAsync(CancellationToken.None);

        var full = await db.BankTransactions.SingleAsync(t => t.ObifinId == 31);
        full.CounterpartyIbanHash.Should().HaveLength(64);
        full.CounterpartyIbanHashValid.Should().BeTrue("bu özet geçerli IBAN'dan: IBAN öğretebilir");
        var m = await db.BankTransactions.SingleAsync(t => t.ObifinId == 32);
        m.CounterpartyIbanHash.Should().BeNull("maskeli IBAN özetlenmez (KVKK Ö-7)");
        m.CounterpartyIbanHashValid.Should().BeFalse();
        m.CounterpartyIbanMasked.Should().Be(BankHasher.ShortValueMask, "IBAN olmayan değerin maskesi karakter göstermez (Ö-6)");
        var account = await db.BankAccounts.SingleAsync(a => a.ObifinAccountId == 6);
        (account.IbanFullCount, account.IbanMaskedCount, account.IbanEmptyCount).Should().Be((1, 1, 1));
    }
```

- [ ] **Adım 6: Hesaba sayaçları ekle ve çekimde say**

`Domain/Bank/BankAccount.cs` — `public DateTimeOffset RefreshedAt { get; set; }` satırının ALTINA:

```csharp
    // KVKK Ö-7: bu hesaba gelen hareketlerde karşı IBAN'ın niteliği (değer yazılmaz). Maskeli sayısı sıfır değilse o bankanın
    // IBAN'ları özetlenemiyor demektir; Faz 2'deki "tek kişiye bağlı IBAN" koşulu o bankada kurulamaz.
    public int IbanFullCount { get; set; }
    public int IbanMaskedCount { get; set; }
    public int IbanEmptyCount { get; set; }
```

`ObifinPollJob.UpsertAsync` — hesabın çözüldüğü `if (!accounts.TryGetValue(dto.AccountId, out var account)) { … }` bloğunun ALTINA:

```csharp
            // KVKK Ö-7: banka hesabı başına IBAN niteliği sayacı (değer yazılmaz). Maskeli/geçersiz IBAN özetlenmez (HashIban null).
            switch (BankHasher.Classify(dto.CounterpartyIban))
            {
                case IbanQuality.Full: account.IbanFullCount++; break;
                case IbanQuality.Masked: account.IbanMaskedCount++; break;
                default: account.IbanEmptyCount++; break;
            }
```

`Domain/Bank/BankTransaction.cs` — `CounterpartyIbanMasked`'ın ALTINA:

```csharp
    /// <summary>Özet mod-97 geçerli bir IBAN'dan üretildi (Görev 11 sonrası çekim). Yalnız bu hareketler IBAN hafızası öğretir
    /// (<c>PaymentMatchReconciler.LearnIbanAsync</c>, KVKK Ö-7): öncesinde çekilen hareketin özeti maskeli IBAN'dan üretilmiş
    /// olabilir. Göç eski satırlarda false bırakır.</summary>
    public bool CounterpartyIbanHashValid { get; set; }
```

`ObifinPollJob.UpsertAsync` — `var masked = BankHasher.MaskIban(dto.CounterpartyIban);` satırının ALTINA `var ibanHash = _hasher.HashIban(dto.CounterpartyIban);` ekle; nesne başlatıcıdaki `CounterpartyIbanHash = _hasher.HashIban(dto.CounterpartyIban),` satırını şununla değiştir (özet artık yalnız geçerli IBAN'da dolu):

```csharp
                CounterpartyIbanHash = ibanHash, CounterpartyIbanHashValid = ibanHash is not null,
```

- [ ] **Adım 7: Yönetim sayfasında göster**

`Pages/Admin/Obifin/Index.cshtml.cs` — `AccountRow`'u şununla değiştir:

```csharp
    public sealed record AccountRow(string BankaKodu, string IbanMasked, string Currency, bool Active,
        DateTimeOffset? LastBankSyncAt, string? NotificationNote, int IbanFullCount, int IbanMaskedCount, int IbanEmptyCount);
```

`OnGetAsync`'teki `new AccountRow(a.BankaKodu, a.IbanMasked, a.Currency, a.Active, a.LastBankSyncAt, a.NotificationNote)` ifadesini `new AccountRow(a.BankaKodu, a.IbanMasked, a.Currency, a.Active, a.LastBankSyncAt, a.NotificationNote, a.IbanFullCount, a.IbanMaskedCount, a.IbanEmptyCount)` yap.

`Index.cshtml` — hesap `<span class="me-3">…</span>` satırının kapanış `</span>`'ından hemen önce ekle:

```cshtml
 · IBAN tam/maskeli/boş: <span data-cell="iban-quality">@a.IbanFullCount/@a.IbanMaskedCount/@a.IbanEmptyCount</span>
```

- [ ] **Adım 8: Göçü üret ve incele**

Run: `cd OrderDeck.LicenseServer && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add IbanQualityCountersAndHashValid --context LicenseDbContext --output-dir Data/Migrations`
Expected: `Up` yalnız üç `AddColumn<int>(… table: "BankAccounts", nullable: false, defaultValue: 0)` ve bir `AddColumn<bool>(name: "CounterpartyIbanHashValid", table: "BankTransactions", nullable: false, defaultValue: false)` (varsayılanlar: eski imajın INSERT'ü sütunları bilmese de geçer; eski hareketler IBAN öğretmez). Eski satırları `true` yapan `UPDATE` EKLEME.

- [ ] **Adım 8b: Eski hareketten IBAN öğrenilmesin (test önce, kırmızı)**

`PaymentMatchReconcilerTests.cs` — `Tx` yardımcısının nesne başlatıcısına `CounterpartyIbanHashValid = ibanHash is not null,` ekle (testlerin hareketleri bu görevden sonraki çekimi temsil eder). Başka bir test dosyası elle eşleme ya da dekont onayıyla IBAN öğrenilmesini bekliyorsa (`CustomerIbanMemories` iddiası; ör. `AdminBankaEslestirmePageTests`) o dosyanın hareketine de aynı bayrağı koy. Sınıfın sonuna:

```csharp
    [Fact]
    public async Task Kesme_anindan_once_cekilmis_hareketin_ozeti_IBAN_ogretmez()
    {
        // KVKK Ö-7: Görev 11'den önce çekilen hareketin özeti maskeli IBAN'dan üretilmiş olabilir; --reset-iban-memory'den sonra
        // ondan yeniden öğrenilen bağ farklı gönderenleri tek müşteriye bağlardı.
        using var db = NewDb(); var s = SeedShopper(db);
        var tx = Tx(db, s.LicenseId, 100m, DateTimeOffset.UtcNow.AddHours(-1), "EFT GELEN", Hasher.HashIban(BankHasherTests.TestIban()));
        tx.CounterpartyIbanHashValid = false;
        db.SaveChanges();

        await Recon(db).ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);

        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
    }
```

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentMatchReconcilerTests"`
Expected: FAIL — yalnız yeni test (hafıza öğrenildi).

`Services/Bank/PaymentMatchReconciler.cs` — `LearnIbanAsync`'in (Görev 10) ilk satırını:

```csharp
        if (tx.CounterpartyIbanHash is null) return;
```
şununla değiştir:
```csharp
        // KVKK Ö-7: yalnız geçerli IBAN'dan üretilmiş özet öğretir. Kesme anından (Görev 11) önce çekilen hareketin özeti maskeli
        // IBAN'dan olabilir; --reset-iban-memory hafızayı silse de o özet buradan geri gelirdi.
        if (tx.CounterpartyIbanHash is null || !tx.CounterpartyIbanHashValid) return;
```

Run: aynı komut. Expected: PASS.

- [ ] **Adım 9: Temizlik komutu testlerini yaz (kırmızı)**

`BankDataCleanupJobTests.cs` sınıfına yardımcı ve testler ekle:

```csharp
    private static CustomerIbanMemory Memory(Guid lic, string hash) => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(), IbanHash = hash, IbanMasked = "TR12…345",
        LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Maskeli_kaynakli_ozet_ve_ona_bagli_IBAN_hafizasi_temizlenir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var suspect = Hex64(); var clean = Hex64();
        BankTransaction Hashed(string hash, string mask) { var t = Tx(lic, 10m); t.CounterpartyIbanHash = hash; t.CounterpartyIbanMasked = mask; return t; }
        var maskedTx = Hashed(suspect, "TR12…**3");
        var sameHash = Hashed(suspect, "TR12…**3");
        var cleanTx = Hashed(clean, BankHasher.MaskIban(BankHasherTests.TestIban()));
        db.BankTransactions.AddRange(maskedTx, sameHash, cleanTx);
        var cleanMemory = Memory(lic, clean);
        db.CustomerIbanMemories.AddRange(Memory(lic, suspect), cleanMemory);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.MaskedSourceHashesCleared.Should().Be(2);
        r.MaskedSourceMemoriesDeleted.Should().Be(1);
        (await db.BankTransactions.Where(t => t.CounterpartyIbanHash != null).Select(t => t.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { cleanTx.Id });
        (await db.CustomerIbanMemories.Select(m => m.Id).ToListAsync()).Should().BeEquivalentTo(new[] { cleanMemory.Id });
    }

    [Fact]
    public async Task Reset_iban_memory_yalniz_hafizayi_siler_hareket_ozeti_kalir_kuru_kosuda_yalniz_sayar()
    {
        // KVKK K-7: hareketin özeti kalmalı — IBAN talep aracı (Görev 17) hareketleri yalnız özetle bulur; boşaltılsaydı 180 güne
        // kadar duran veri "yok" görünürdü. Yeniden öğrenmeyi LearnIbanAsync'in CounterpartyIbanHashValid kapısı engeller.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var hash = Hex64();
        var t = Tx(lic, 10m); t.CounterpartyIbanHash = hash; t.CounterpartyIbanMasked = BankHasher.MaskIban(BankHasherTests.TestIban());
        db.BankTransactions.Add(t);
        db.CustomerIbanMemories.Add(Memory(lic, hash));
        await db.SaveChangesAsync();

        var dry = await Job(db).RunAsync(null, apply: false, CancellationToken.None, resetIbanMemory: true);
        dry.IbanMemoriesReset.Should().Be(1);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(1);

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None, resetIbanMemory: true);

        r.IbanMemoriesReset.Should().Be(1);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
        (await db.BankTransactions.SingleAsync()).CounterpartyIbanHash.Should().Be(hash, "talep aracı hareketi özetle bulur");
        (await db.BankTransactions.SingleAsync()).CounterpartyIbanMasked.Should().NotBeNull("maske kalır");
    }

    [Theory]
    [InlineData("TR12…345", false)]
    [InlineData("TR12…**3", true)]
    [InlineData("TR**…345", true)]
    [InlineData("…", true)]
    [InlineData("1234…567", true)]
    [InlineData(null, false)]
    public void Maske_maskeli_kaynagi_ele_verir(string? mask, bool expected)
        => BankDataCleanupJob.LooksMasked(mask).Should().Be(expected);
```

`Tools/BankDataCleanupTests.cs` sınıfına:

```csharp
    [Fact]
    public void Reset_iban_memory_bayragi_kabul_edilir_yinelenirse_reddedilir()
    {
        BankDataCleanup.TryParseArgs(["bank-data-cleanup", "--reset-iban-memory", "--apply"], out var o).Should().BeTrue();
        o.Should().Be(new BankDataCleanup.Options(null, Apply: true, ResetIbanMemory: true));
        BankDataCleanup.TryParseArgs(["bank-data-cleanup", "--reset-iban-memory", "--reset-iban-memory"], out _).Should().BeFalse();
    }
```

- [ ] **Adım 10: Temizlik komutunu genişlet**

`Services/Bank/BankDataCleanupJob.cs`:

`Report` sınıfına:

```csharp
        public int MaskedSourceHashesCleared { get; set; }
        public int MaskedSourceMemoriesDeleted { get; set; }
        public int IbanMemoriesReset { get; set; }
```

`RunAsync` imzasını ve gövdesini şununla değiştir (log satırı aynı kalır, sonuna maskeli/sıfırlama sayıları eklenir):

```csharp
    /// <param name="resetIbanMemory">KVKK Ö-7, not karar 5: maskeden anlaşılamayan maskeli kaynaklı özetler için bütün IBAN
    /// hafızasını siler. Hareketlerdeki özetler KALIR (IBAN talep aracı onlarla arar, K-7); yeniden öğrenmeyi
    /// <c>CounterpartyIbanHashValid</c> kapısı engeller.</param>
    public async Task<Report> RunAsync(Guid? licenseId, bool apply, CancellationToken ct, bool resetIbanMemory = false)
    {
        var r = new Report();
        await DeleteOutgoingOrZeroAsync(licenseId, apply, r, ct);
        await ApplyInitialAnchorAsync(licenseId, apply, r, ct);
        await ClearTaxIdHashesAsync(licenseId, apply, r, ct);
        await RewriteRawJsonAsync(licenseId, apply, r, ct);
        await MaskDescriptionsAsync(licenseId, apply, r, ct);
        await RemaskShortValuesAsync(licenseId, apply, r, ct);
        await ClearMaskedSourceHashesAsync(licenseId, apply, r, ct);
        if (resetIbanMemory) await ResetIbanMemoryAsync(licenseId, apply, r, ct);
        _log.LogInformation("bank-data-cleanup ({Mode}): giden/sıfır={Outgoing} (atlanan {Skipped}), çapa={Anchors}, " +
            "çapa öncesi={PreAnchor} (bağlı {Linked}), eşleşme={Matches}, VKN kodu={TaxIds}, ham kayıt={Raw} (okunamayan {Unreadable}), " +
            "açıklama={Descriptions}, kısa maske={ShortMasks}, maskeli özet={MaskedHashes} (hafıza {MaskedMemories}), " +
            "sıfırlanan hafıza={ResetMemories}, düşen parti={Failed}", apply ? "uygula" : "kuru", r.OutgoingOrZeroDeleted,
            r.OutgoingOrZeroSkippedDecided, r.AnchorsSet, r.PreAnchorDeleted, r.PreAnchorSkippedLinked, r.MatchesDeleted,
            r.TaxIdHashesCleared, r.RawJsonRewritten, r.RawJsonUnreadableCleared, r.DescriptionsMasked, r.ShortMasksReplaced,
            r.MaskedSourceHashesCleared, r.MaskedSourceMemoriesDeleted, r.IbanMemoriesReset, r.FailedBatches);
        return r;
    }
```

Sınıfın sonuna:

```csharp
    /// <summary>Saklı maske (<c>XXXX…YYY</c>, <see cref="BankHasher.MaskIban"/>) maskeli ya da geçersiz bir IBAN'dan mı üretilmiş?
    /// Biçim tutmuyorsa, görünen yedi karakterden biri harf/rakam değilse ya da ülke + kontrol biçimi yoksa evet. Görünen
    /// karakterleri sağlam olan maskeli değer buradan anlaşılamaz: o yüzden <c>--reset-iban-memory</c> var.</summary>
    public static bool LooksMasked(string? masked)
    {
        if (string.IsNullOrEmpty(masked)) return false;
        var parts = masked.Split('…');
        if (parts.Length != 2 || parts[0].Length != 4 || parts[1].Length != 3) return true;
        var visible = parts[0] + parts[1];
        if (visible.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))) return true;
        return !(char.IsAsciiLetterUpper(visible[0]) && char.IsAsciiLetterUpper(visible[1])
            && char.IsAsciiDigit(visible[2]) && char.IsAsciiDigit(visible[3]));
    }

    /// <summary>KVKK Ö-7: maskesi maskeli kaynağı ele veren özet, aynı özeti taşıyan bütün hareketlerde boşaltılır ve o özetle
    /// öğrenilmiş IBAN hafızası silinir (lisans başına).</summary>
    private async Task ClearMaskedSourceHashesAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var hashed = await Transactions(licenseId).AsNoTracking().Where(t => t.CounterpartyIbanHash != null)
            .Select(t => new { t.LicenseId, Hash = t.CounterpartyIbanHash!, t.CounterpartyIbanMasked }).ToListAsync(ct);
        var suspects = hashed.Where(x => LooksMasked(x.CounterpartyIbanMasked))
            .GroupBy(x => x.LicenseId, x => x.Hash);
        foreach (var group in suspects)
        {
            var license = group.Key;
            var hashes = group.Distinct().ToList();
            var txs = await _db.BankTransactions
                .Where(t => t.LicenseId == license && t.CounterpartyIbanHash != null && hashes.Contains(t.CounterpartyIbanHash))
                .ToListAsync(ct);
            var memories = await _db.CustomerIbanMemories
                .Where(m => m.LicenseId == license && hashes.Contains(m.IbanHash)).ToListAsync(ct);
            r.MaskedSourceHashesCleared += txs.Count;
            r.MaskedSourceMemoriesDeleted += memories.Count;
            // Maskeli bir değerin özeti gerçek bir IBAN'ın özetine eşit olamaz: boşaltmak IBAN talep aramasından bir şey
            // kaybettirmez (talep gerçek IBAN'ın özetiyle arar).
            foreach (var t in txs) t.CounterpartyIbanHash = null;
            _db.CustomerIbanMemories.RemoveRange(memories);
            if (apply)
            {
                // RecordBatch satırın lisansı başına yazar (Görev 9 incelemesi).
                RecordBatch(txs.Select(t => t.LicenseId), "BankTransactions.CounterpartyIbanHash", Nulled,
                    "maskeli kaynaklı özet (Ö-7)");
                RecordBatch(memories.Select(m => m.LicenseId), "CustomerIbanMemories", Deleted, "maskeli kaynaklı özet (Ö-7)");
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>KVKK Ö-7, karar 5: bütün IBAN hafızası silinir. Hareketlerdeki özetler BİLEREK KALIR: IBAN talep aracı
    /// (BankSubjectRequestService) hareketleri yalnız özetle bulur, boşaltılsaydı 180 güne kadar duran veri erişim/silme
    /// talebinde "yok" görünürdü (K-7). Eski hareketin özeti hafızaya geri giremez: <c>LearnIbanAsync</c> yalnız
    /// <c>CounterpartyIbanHashValid</c> hareketten öğrenir.</summary>
    private async Task ResetIbanMemoryAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var memories = licenseId is { } l ? _db.CustomerIbanMemories.Where(m => m.LicenseId == l) : _db.CustomerIbanMemories;
        if (!apply)
        {
            r.IbanMemoriesReset = await memories.CountAsync(ct);
            return;
        }
        while (true)
        {
            var batch = await memories.OrderBy(m => m.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            _db.CustomerIbanMemories.RemoveRange(batch);
            RecordBatch(batch.Select(m => m.LicenseId), "CustomerIbanMemories", Deleted, "IBAN hafızası sıfırlama (Ö-7, karar 5)");
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            r.IbanMemoriesReset += batch.Count;
            if (batch.Count < BatchSize) break;
        }
    }
```

`Tools/BankDataCleanup.cs`:
- `Usage` → `"Kullanım: bank-data-cleanup [--license <guid>] [--apply] [--reset-iban-memory]"`
- `Options` → `public sealed record Options(Guid? License, bool Apply, bool ResetIbanMemory = false);`
- `RunAsync(LicenseDbContext …)` içinde iş çağrısı → `var r = await job.RunAsync(options.License, options.Apply, ct, options.ResetIbanMemory);`
- `TryParseArgs`'ta `var apply = false;` altına `var reset = false;`; `switch`'e `case "--reset-iban-memory" when !reset: reset = true; break;`; sondaki atama `options = new Options(license, apply, reset);`
- `Format` metodunu bütünüyle şununla değiştir:

```csharp
    public static string Format(BankDataCleanupJob.Report r)
        => $"giden/sıfır silinen={r.OutgoingOrZeroDeleted} (kararlı atlanan={r.OutgoingOrZeroSkippedDecided}) | " +
           $"çapa yazılan={r.AnchorsSet} çapa öncesi silinen={r.PreAnchorDeleted} (bağlı kalan={r.PreAnchorSkippedLinked}) | " +
           $"eşleşme silinen={r.MatchesDeleted} | VKN/TCKN kodu boşaltılan={r.TaxIdHashesCleared} | " +
           $"ham kayıt daraltılan={r.RawJsonRewritten} (okunamayan boşaltılan={r.RawJsonUnreadableCleared}) | " +
           $"açıklama maskelenen={r.DescriptionsMasked} | kısa maske çevrilen={r.ShortMasksReplaced} | " +
           $"maskeli kaynaklı özet boşaltılan={r.MaskedSourceHashesCleared} (hafıza silinen={r.MaskedSourceMemoriesDeleted}) | " +
           $"sıfırlanan IBAN hafızası={r.IbanMemoriesReset} | düşen parti={r.FailedBatches}";
```

- Sınıf özetine `--reset-iban-memory` paragrafı: `/// <para><c>--reset-iban-memory</c>: bütün IBAN hafızasını siler; hareket özetleri KALIR (IBAN talep aracı onlarla arar) ve eski hareketten yeniden öğrenilmez (CounterpartyIbanHashValid kapısı) — maskeli kaynaklı özetler saklı veriden tam anlaşılamadığı için Faz 2'den önce (KVKK Ö-7, karar Burak'ın).</para>`

- [ ] **Adım 11: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Services.Bank|FullyQualifiedName~BankDataCleanup|FullyQualifiedName~AdminObifinPageTests"`
Expected: PASS (Docker gerekli: `BankDataCleanupRelationalTests`).

- [ ] **Adım 12: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank OrderDeck.LicenseServer/Domain/Bank/BankAccount.cs OrderDeck.LicenseServer/Domain/Bank/BankTransaction.cs OrderDeck.LicenseServer/Pages/Admin/Obifin OrderDeck.LicenseServer/Tools/BankDataCleanup.cs OrderDeck.LicenseServer/Data/Migrations OrderDeck.LicenseServer.Tests/Services/Bank OrderDeck.LicenseServer.Tests/Tools/BankDataCleanupTests.cs OrderDeck.LicenseServer.Tests/Pages/Admin
git commit -m "fix(bank-kvkk): yalnız geçerli IBAN'ı özetle ve maskele, maskeli IBAN'ı say ve temizle

NormalizeIban harf/rakam, ülke+kontrol biçimi, TR'de 26 karakter ve
mod-97 ister; maskeli değer özetlenmez, IBAN olmayan değerin maskesi
karakter göstermez. Hesap başına tam/maskeli/boş sayacı. bank-data-cleanup
maskeli kaynaklı özetleri boşaltır; --reset-iban-memory yalnız hafızayı
siler, hareket özetleri talep aracı için kalır ve kesme anından önceki
hareket IBAN öğretmez (CounterpartyIbanHashValid) (KVKK ön inceleme Ö-7,
Ö-6, P1-12).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 12: Lisans başına izinli hesap listesi (P1-13)

**Neden (not Ö-10):** kod Obifin panelinden eklenen her bankayı otomatik olarak lisansa yazıyor (bilinmeyen hesap → yer tutucu → hareket yazılır). Faz 2 spec §13'teki fail-closed kural bugüne çekilir: yönetici onaylamadıkça hesabın hareketi hiçbir lisansa yazılmaz, yer tutucu açılır, atlanan sayısı sayılır ve yönetim sayfasında uyarı çıkar. Atlanan hareket imleci yine ilerletir: onaydan sonra geriye dönük gelmez (bilinçli — onay öncesi toplanmaz). Mevcut hesaplar göçle **onaylanmaz** (karar 4).

**Geçmiş veri ve onayın geri alınması (ikinci plan incelemesi, Ö-10):** kapı ileriye dönüktür. P1'den önce onaysız hesaplardan yazılmış hareketler 180 güne kadar kalır, eşleştirilir ve IBAN öğretirdi. Not §5.9 kontrolü tam olarak bu satırları soruyor (Obifin panelinden eklenmiş EMAR dışı ya da şahsi hesap). Yanlış verilmiş bir onayı geri almanın yolu da yoktu. İkisi Görev 13'teki "Reddet ve verisini sil" eylemiyle kapanır: `BankConnectionTerminator.RejectAccountAsync` onayı kaldırır ve o hesabın verisini partilerle silip imha kaydı yazar. Silme sınıfında olduğu için Görev 13'te; bu görev hesap satırına hesap numarasını (`ObifinAccountId`) ekler, ret formu onu yazdırır.

**Files:**
- Modify: `OrderDeck.LicenseServer/Domain/Bank/BankAccount.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (`BankAccount` modeli: `ApprovedBy` uzunluğu)
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs` (`UpsertAsync`)
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Obifin/Index.cshtml(.cs)`
- Modify: `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs`
- Create: göç `*_BankAccountApproval`
- Test: `ObifinPollJobTests.cs`, `Pages/Admin/AdminObifinPageTests.cs`

- [ ] **Adım 1: Çekim testlerini hazırla (kırmızı)**

`ObifinPollJobTests.cs` — `SeedVerifiedAsync`'te `await db.SaveChangesAsync(); return licenseId;` (en son iki satır) ÖNCESİNE onaylı hesap ekle:

```csharp
        // Tx yardımcısı hesap 6'yı kullanır: yönetici onaylı (KVKK Ö-10 — onaysız hesabın hareketi yazılmaz).
        db.BankAccounts.Add(new BankAccount { Id = Guid.NewGuid(), LicenseId = licenseId, ObifinAccountId = 6, BankaKodu = "qnb",
            IbanMasked = BankHasher.MaskIban(BankHasherTests.TestIban()), Currency = "TL", Active = true,
            RefreshedAt = DateTimeOffset.UtcNow, ApprovedAt = DateTimeOffset.UtcNow, ApprovedBy = "test" });
```

`Bilinmeyen_hesap_icin_yer_tutucu_acilir_bilinen_hesaba_baglanir` testini bütünüyle şununla değiştir:

```csharp
    [Fact]
    public async Task Onaysiz_ya_da_bilinmeyen_hesabin_hareketi_yazilmaz_yer_tutucu_acilir_sayac_artar()
    {
        // KVKK Ö-10: Obifin panelinden eklenen hesap kendiliğinden lisansa yazılmaz; yönetici onaylayana kadar hareketi atılır.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var approved = await db.BankAccounts.SingleAsync(a => a.LicenseId == lic && a.ObifinAccountId == 6);
        client.Transactions.Add(Tx(1, new DateTime(2026, 9, 25, 8, 1, 0), 10m));   // hesap 6 = onaylı
        client.Transactions.Add(new ObifinTransactionDto(2, 99, "qnb", new DateTime(2026, 9, 25, 8, 2, 0), 10m, "TL",
            null, null, null, null, null, null, "{}"));                       // hesap 99 bilinmiyor

        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.SingleAsync()).BankAccountId.Should().Be(approved.Id);
        sink.Received.Should().ContainSingle().Which.ObifinId.Should().Be(1);
        // Yer tutucu: saatlik hesap yenilemesi IBAN maskesini ve durumu tamamlar; onayı yalnız yönetici verir.
        var placeholder = await db.BankAccounts.SingleAsync(a => a.ObifinAccountId == 99);
        placeholder.LicenseId.Should().Be(lic);
        placeholder.Active.Should().BeFalse();
        placeholder.IbanMasked.Should().Be("?");
        placeholder.ApprovedAt.Should().BeNull();
        placeholder.UnapprovedSkippedCount.Should().Be(1);
        (await db.ObifinConnections.SingleAsync()).LastObifinTransactionId.Should().Be(2, "atlanan hareket de görüldü");
    }

    [Fact]
    public async Task Yalniz_onaysiz_hareket_gelen_turda_sayac_ve_imlec_turun_sonundaki_kayitla_kalici_olur()
    {
        // Sayfada yazılacak hareket yoksa UpsertAsync erken döner (fresh boş): yer tutucu ve sayaç izleyicide kalır, turun
        // sonundaki kimlik korumalı kayıt imleçle birlikte yazar. Bu yol başka hiçbir testte yok; kayıt atlanırsa uyarı sayısı
        // sessizce sıfır kalırdı.
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        client.Transactions.Add(new ObifinTransactionDto(2, 99, "qnb", new DateTime(2026, 9, 25, 8, 2, 0), 10m, "TL",
            null, null, null, null, null, null, "{}"));
        client.Transactions.Add(new ObifinTransactionDto(3, 99, "qnb", new DateTime(2026, 9, 25, 8, 3, 0), 12m, "TL",
            null, null, null, null, null, null, "{}"));

        await job.RunAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        (await db.BankTransactions.CountAsync()).Should().Be(0);
        sink.Received.Should().BeEmpty();
        (await db.BankAccounts.AsNoTracking().SingleAsync(a => a.ObifinAccountId == 99)).UnapprovedSkippedCount.Should().Be(2);
        (await db.ObifinConnections.AsNoTracking().SingleAsync()).LastObifinTransactionId.Should().Be(3);
    }
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ObifinPollJobTests"`
Expected: FAIL — `ApprovedAt`, `ApprovedBy`, `UnapprovedSkippedCount` yok.

- [ ] **Adım 3: Hesaba onay alanları**

`Domain/Bank/BankAccount.cs` — sayaçların ALTINA:

```csharp
    /// <summary>Yöneticinin bu hesabı lisansa bağladığı an (KVKK Ö-10). Null = onaysız: hareketi hiçbir lisansa yazılmaz, yalnız
    /// <see cref="UnapprovedSkippedCount"/> artar. Yalnız yayıncının müşteri tahsilatı hesabı onaylanır.</summary>
    public DateTimeOffset? ApprovedAt { get; set; }
    /// <summary>Onaylayan yöneticinin kimliği (<c>DataDestruction.ActorOf</c>: <c>sub</c>), kullanıcı adı DEĞİL (KVKK Ö-14): satır
    /// budanmaz, ad bir gerçek kişiyi gösterir; adı aynı işlemin 24 aylık denetim satırında.</summary>
    public string? ApprovedBy { get; set; }
    /// <summary>Onaysızken atlanan hareket sayısı (yönetim sayfasındaki uyarı).</summary>
    public int UnapprovedSkippedCount { get; set; }
```

`Data/LicenseDbContext.cs` — `mb.Entity<BankAccount>(b => { … })` içinde `b.Property(a => a.NotificationNote).HasMaxLength(500);` satırının ALTINA:

```csharp
            b.Property(a => a.ApprovedBy).HasMaxLength(256);
```

- [ ] **Adım 4: Çekimde onay kapısı**

`ObifinPollJob.UpsertAsync` — Görev 11'de eklenen `switch (BankHasher.Classify(…))` bloğunun ÜSTÜNE (Görev 11'in sonraki görev kuralı: süzgeç sayaçtan ÖNCE, onaysız hareket IBAN niteliği sayacına girmez; hesap satırını silen yolla yarışan sayfa `DbUpdateConcurrencyException` ile düşer, sonraki tur düzeltir):

```csharp
            // KVKK Ö-10: yönetici onaylamadıkça hesabın hareketi hiçbir lisansa yazılmaz (fail-closed). İmleç sıralı listeden
            // hesaplanır: atlanan hareket onaydan sonra geriye dönük gelmez — bilinçli, onay öncesi toplanmaz.
            if (account.ApprovedAt is null)
            {
                account.UnapprovedSkippedCount++;
                skippedUnapproved++;
                continue;
            }
```

Aynı metotta `var fresh = new List<BankTransaction>();` satırının ALTINA `var skippedUnapproved = 0;` ekle; döngüden sonra, `if (fresh.Count == 0) return;` satırının ÜSTÜNE:

```csharp
        if (skippedUnapproved > 0)
            _log.LogWarning("Obifin çekimi: onaysız hesap(lar)ın {Count} hareketi yazılmadı — bağlantı={ConnectionId}; " +
                "admin/obifin'de hesabı onaylayın", skippedUnapproved, conn.Id);
```

(Yer tutucu ve sayaç değişiklikleri izleyicide kalır; turun sonundaki kimlik korumalı kayıt onları yazar.)

- [ ] **Adım 5: Koştur — çekim yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ObifinPollJobTests"`
Expected: PASS.

- [ ] **Adım 6: Yönetim sayfası testini yaz (kırmızı)**

`AdminObifinPageTests.cs` sınıfının sonuna:

```csharp
    [Fact]
    public async Task Onaysiz_hesap_uyariyla_listelenir_onaylaninca_onay_ve_denetim_yazilir()
    {
        var lic = await SeedLicenseAsync();
        await SeedRawConnectionAsync(_factory, lic);
        var accountId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.BankAccounts.Add(new BankAccount { Id = accountId, LicenseId = lic, ObifinAccountId = Random.Shared.NextInt64(1, 1_000_000),
                BankaKodu = "qnb", IbanMasked = BankHasher.MaskIban(BankHasherTests.TestIban()), Currency = "TL",
                RefreshedAt = DateTimeOffset.UtcNow, UnapprovedSkippedCount = 3 });
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var doc = await ParseAsync(await client.GetStringAsync("/admin/obifin"));
        doc.QuerySelector("[data-banner='unapproved-accounts']").Should().NotBeNull();
        doc.QuerySelector($"[data-account='{accountId}'] [data-cell='approval']")!.TextContent.Should().Contain("3 hareket kaydedilmedi");

        var resp = await PostAsync(client, "ApproveAccount", new Dictionary<string, string> { ["AccountId"] = accountId.ToString() });

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var account = await vdb.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
        account.ApprovedAt.Should().NotBeNull();
        var adminId = (await vdb.AdminUsers.AsNoTracking().SingleAsync(a => a.Username == "admin")).Id;
        account.ApprovedBy.Should().Be(adminId.ToString(), "yöneticinin kimliği, kullanıcı adı değil (KVKK Ö-14)");
        (await vdb.AuditLogs.AnyAsync(a => a.EventType == AuditEvents.ObifinAccountApprove && a.TargetId == lic.ToString()))
            .Should().BeTrue();
    }
```

(Dosyaya `using OrderDeck.LicenseServer.Services.Audit;` ekle.)

- [ ] **Adım 7: Sayfayı genişlet**

`Services/Audit/AuditEvents.cs` — `ObifinPollNow` satırının ALTINA:

```csharp
    public const string ObifinAccountApprove = "obifin.account.approve";
```

`Pages/Admin/Obifin/Index.cshtml.cs`:

`AccountRow`'u şununla değiştir:

```csharp
    public sealed record AccountRow(Guid Id, long ObifinAccountId, string BankaKodu, string IbanMasked, string Currency, bool Active,
        DateTimeOffset? LastBankSyncAt, string? NotificationNote, int IbanFullCount, int IbanMaskedCount, int IbanEmptyCount,
        DateTimeOffset? ApprovedAt, int UnapprovedSkippedCount);
```

`OnGetAsync`'teki `new AccountRow(…)` ifadesini şununla değiştir:

```csharp
new AccountRow(a.Id, a.ObifinAccountId, a.BankaKodu, a.IbanMasked, a.Currency, a.Active, a.LastBankSyncAt, a.NotificationNote,
                a.IbanFullCount, a.IbanMaskedCount, a.IbanEmptyCount, a.ApprovedAt, a.UnapprovedSkippedCount)
```

Özelliklere:

```csharp
    /// <summary>Onaysız hesap sayısı (sayfanın üstündeki uyarı).</summary>
    public int UnapprovedAccounts => AccountsByLicense.Values.Sum(list => list.Count(a => a.ApprovedAt is null));
```

`OnPostPollNowAsync`'ten ÖNCE işleyiciyi ekle (BankHasher istemez; modül kapalıyken de çalışır):

```csharp
    /// <summary>KVKK Ö-10: hesabı lisansa bağlar; bundan sonraki hareketleri kaydedilir. Yalnız yayıncının müşteri tahsilatı
    /// hesabı onaylanmalı. Geri alma: "Reddet ve verisini sil" (Görev 13, <c>OnPostRejectAccountAsync</c>).</summary>
    public async Task<IActionResult> OnPostApproveAccountAsync(Guid accountId, CancellationToken ct)
    {
        var account = await _db.BankAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null)
        {
            TempData["Error"] = "Hesap bulunamadı.";
            return RedirectToPage();
        }
        if (account.ApprovedAt is null)
        {
            account.ApprovedAt = DateTimeOffset.UtcNow;
            // Yöneticinin kimliği, kullanıcı adı değil (KVKK Ö-14; Görev 9 kuralı): sütun budanmaz.
            account.ApprovedBy = DataDestruction.ActorOf(User);
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(AuditEvents.ObifinAccountApprove, AuditTargets.ObifinConnection, account.LicenseId.ToString(),
                new { account.ObifinAccountId, account.BankaKodu }, CancellationToken.None);
        }
        TempData["Success"] = "Hesap onaylandı; bundan sonraki hareketleri kaydedilecek.";
        return RedirectToPage();
    }
```

(`DataDestruction` için `using OrderDeck.LicenseServer.Services.Privacy;` Görev 9'dan beri sayfada var. Kullanıcı adı yazan bir yardımcı EKLENMEZ.)

`Index.cshtml` — `@if (Model.BankDisabled) { … }` bloğunun ALTINA:

```cshtml
@if (Model.UnapprovedAccounts > 0)
{
    <div class="alert alert-warning" data-banner="unapproved-accounts">
        @Model.UnapprovedAccounts hesap onaysız: hareketleri kaydedilmiyor. Yalnız yayıncının müşteri tahsilatı hesabını onaylayın (KVKK).
    </div>
}
```

Hesap satırları bloğunu (`@if (Model.AccountsByLicense.TryGetValue(…)) { <tr><td colspan="12" …> … </td></tr> }`) bütünüyle şununla değiştir:

```cshtml
        @if (Model.AccountsByLicense.TryGetValue(r.LicenseId, out var accounts) && accounts.Count > 0)
        {
            <tr><td colspan="12" class="small text-muted">
                @foreach (var a in accounts)
                {
                    <div data-account="@a.Id">
                        #@a.ObifinAccountId @a.BankaKodu @a.IbanMasked (@a.Currency) @(a.Active ? "aktif" : "pasif") · banka senk. @(a.LastBankSyncAt?.ToString("yyyy-MM-dd HH:mm") ?? "—") @(string.IsNullOrEmpty(a.NotificationNote) ? "" : " ⚠ " + a.NotificationNote)
                        · IBAN tam/maskeli/boş: <span data-cell="iban-quality">@a.IbanFullCount/@a.IbanMaskedCount/@a.IbanEmptyCount</span>
                        <span data-cell="approval">
                            @if (a.ApprovedAt is { } approvedAt)
                            {
                                <text>· onaylı (@approvedAt.ToString("yyyy-MM-dd"))</text>
                            }
                            else
                            {
                                <strong class="text-danger">· ONAYSIZ — @a.UnapprovedSkippedCount hareket kaydedilmedi</strong>
                                <form method="post" asp-page-handler="ApproveAccount" class="d-inline">
                                    <input type="hidden" name="AccountId" value="@a.Id" />
                                    <button class="btn btn-sm btn-outline-success">Onayla</button>
                                </form>
                            }
                        </span>
                    </div>
                }
            </td></tr>
        }
```

- [ ] **Adım 8: Göçü üret ve incele**

Run: `cd OrderDeck.LicenseServer && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add BankAccountApproval --context LicenseDbContext --output-dir Data/Migrations`
Expected: `Up` yalnız `AddColumn` × 3 (`ApprovedAt` nullable, `ApprovedBy` nvarchar(256) nullable, `UnapprovedSkippedCount` int default 0). **Mevcut hesapları onaylayan `UPDATE` ekleme** (karar 4).

- [ ] **Adım 9: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Services.Bank|FullyQualifiedName~AdminObifinPageTests|FullyQualifiedName~AdminBankaEslestirmePageTests"`
Expected: PASS.

Run (Docker): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataCleanupRelationalTests"`
Expected: PASS.

- [ ] **Adım 10: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/Bank/BankAccount.cs OrderDeck.LicenseServer/Data OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs OrderDeck.LicenseServer/Pages/Admin/Obifin OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs OrderDeck.LicenseServer.Tests/Pages/Admin/AdminObifinPageTests.cs
git commit -m "fix(bank-kvkk): yalnız yönetici onaylı banka hesabının hareketini kaydet

Obifin'den gelen hesap yönetici onaylamadıkça lisansa yazılmaz;
hareketi atılır, sayılır ve admin/obifin'de uyarı çıkar. Mevcut hesaplar
göçle onaylanmaz: deploy sonrası EMAR hesapları elle onaylanmalı
(KVKK ön inceleme Ö-10, P1-13).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 13: Bağlantıyı durdur, etkinleştir, kaldır ve sil (P1-10a)

**Neden (not K-6, Ö-9):** yönetim sayfasında yalnız Kaydet/Doğrula/Banka ekle/Şimdi çek var; durdurma ya da kaldırma yok. `IObifinClient.RemoveBankConnectionAsync` (`bankaapi/sil`) hiçbir yerden çağrılmıyor. Yayıncı silmesi (`AdminCustomersController.Purge`) banka tablolarını lisansla kaskadla götürüyor ama Obifin'e bir şey söylemiyor: Obifin bankadan çekmeyi ve saklamayı sürdürür. Yeni `BankConnectionTerminator`:
- **Durdur:** `Disabled` + `DisabledAt` (30 günlük sayaç; Görev 14) + `DisabledByAdmin`. Lisans bitişiyle zaten Disabled olan bağlantıda yalnız `DisabledByAdmin` yazılır (tarih değişmez): yönetici kararı, lisans yenilenince kendiliğinden açılmasın (Görev 14).
- **Etkinleştir:** yalnız lisans geçerliyse; `Unverified`'a döner (yönetici Doğrula'ya basar), hizmet sonu alanları (`DisabledAt`, `BankDataPurgedAt`, `DisabledByAdmin`) boşalır.
- **Kaldır ve sil (YALNIZ yöneticinin eylemi; `bankaapi/sil`'i çağıran tek yol):** Obifin tarafı **lisansa özgü**. `ObifinConnection`'da yalnız `LicenseId` tekil; aynı (BaseUrl, UserCode) birden çok lisansa girilebilir ve Faz 2 spec §13'te sahibin tercihi "(A) tek paylaşılan Obifin kullanıcısı". Bu yüzden: (1) aynı (BaseUrl, UserCode) başka bir lisansın bağlantısında da varsa Obifin'e **hiç gidilmez**, sonuç "paylaşılan kullanıcı — elle kaldır" olur; (2) yalnız bu lisansın yerel `BankConnections`'ındaki ve **onaylı** hesaplarının (Görev 12; Obifin hesap listesindeki `BankaApiId`) bağlantıları kaldırılır — pilotta QNB bağlantısı Obifin panelinden eklendi, yerelde satırı yok, onaylı hesap üzerinden bulunur; (3) Obifin listesindeki diğer bağlantılara dokunulmaz, "bu lisansa ait olduğu doğrulanamadı — elle kontrol" diye raporlanır (`ObifinConnectionService.ResetShadowData`'nın yabancı `BankaApiId` uyarısının aynısı). Sonra lisansın bütün banka verisi ve Obifin kimliği yerelde silinir, imha kaydı yazılır. Obifin'e gitmeden ve partilerden ÖNCE bağlantı tek SaveChanges'le durdurulur (`Disabled`, `DisabledByAdmin`, yeni `UpdatedAt`; aşağıdaki üçüncü inceleme maddesi). **Obifin API'sinde hareket silme ucu yok** (doküman: `bankaapi/liste|ekle|guncelle|sil|bankakodlari`, hesap listesi, hareket listesi): Obifin'deki kopyanın silinmesi yazılı istenir — sonuç mesajı bunu söyler. Obifin'e ulaşılamazsa ya da kaldırma düşerse yerel silme yine yapılır (KVKK), yönetici elle kaldırmaya yönlendirilir.
- **Yalnız yerel silme (Görev 14'teki otomatik iş için):** Obifin'e **gidilmez**. Lisansın banka verisi silinir. "Kaldır ve sil"in sahiplik kanıtı KALIR ki yönetici Obifin tarafını sonra kapatabilsin: Obifin kimliği, yerel `BankConnections` satırları (yalnız `BankaApiId`, kişisel veri değil) ve **onaylı** hesap satırları. Onaylı hesabın kişisel veri taşıyabilen alanları boşalır (`IbanMasked` = "", `IbanHash` ve `NotificationNote` = null; imha kaydı `nulled`), `ObifinAccountId`, `ApprovedAt` ve `ApprovedBy` kalır. Onaysız hesaplar silinir. Kalan satırları "Kaldır ve sil" siler. `BankDataPurgedAt` yazılır, sayfada uyarı şeridi çıkar.
- **Yayıncı silmesi:** Obifin bağlantısı olan lisans varken `Purge` 409 `bank-connection-active` ile reddeder ve önce "Kaldır ve sil"i ister. Purge içinde ağ çağrısı yapılmaz; Obifin tarafı tek yoldan (yukarıdaki korumalarla) kapanır.
- **`DisabledAt` yaşam döngüsü:** Disabled'dan çıkan her yol (Etkinleştir, lisans geçerliyken kimlik değişimi, başarılı doğrulama, Görev 14'teki lisans yenilenmesi) hizmet sonu alanlarını `ObifinConnectionService.ClearServiceEnd` ile boşaltır. Lisansı bitmiş bağlantıda Doğrula ve Banka ekle Obifin'e gitmez; kimlik değişimi Disabled bağlantıyı yalnız lisans geçerliyse açar (sayaç kimlik düzenlemesiyle sıfırlanmaz). Durum hücresi tarihi yalnız `Disabled` iken gösterir.
- `BankHasher` İSTEMEZ: banka modülü anahtarsız kapalıyken de durdurma ve silme çalışır. Obifin kimliği yeni statik `ObifinConnectionService.ResolveCredentials` ile çözülür (amaçlar aynı).

**İkinci plan incelemesi (20.10) — bu görevin ek parçaları:**
- **Durdurma işlemeyi de durdurur (K-6).** Durdurma ve lisans bitişi yalnız çekimi durduruyordu. Eşleştirme işleri bağlantının durumuna değil yalnız satırın varlığına bakıyordu (`PaymentMatchReconcileJob:44`, `BankMatchSweepJob:62`). 30 günlük beklemede dekont onayları saklı hareketlerle bağdaştırılıyor, IBAN öğreniliyor, `LastConfirmedAt` tazeleniyordu. Yerel silmeden sonra bağlantı satırı kaldığı için her onaylı dekont yeni bir gap yazıyordu; `BankDataPurgedAt` dolu olduğundan hizmet sonu işi bunları bir daha silmezdi. Not K-6: talimat bittikten sonra işlemeyi sürdüren taraf kendi amacını belirleyen konuma düşer. Yeni tek yüklem `ObifinConnectionService.MatchingActive` = `Status != Disabled && BankDataPurgedAt == null` (`LicenseActiveAt` gibi EF'e çevrilir, banka modülünde tek tanım). Dekont bağdaştırma işi ve telafi taraması yalnız bu yüklemi tutan bağlantının lisansında koşar. Bağdaştırıcının gap çözümü, onay bağdaştırması ve elle eşlemesi de yüklemi tutmayan bağlantıda durur. Elle eşleme incelemenin listesinde yoktu, ama o da IBAN öğretir ve `LastConfirmedAt`'i tazeler; kural delinmesin diye kapıya alındı. Bağlantısı hiç olmayan lisansta bağdaştırıcı bugünkü gibi davranır: iş ve tarama zaten bağlantı ister, bağlantı yoksa banka hareketi de yoktur. Kaldırma (`UnmatchAsync`) açık kalır, çünkü bağ siler.
- **Obifin tarafı izlenir (Ö-9 öneri 3, K-6 öneri 1; karar 21).** "Kaldır ve sil" `ObifinConnection` satırını sildiği için Obifin'den yazılı silme teyidinin beklendiği hiçbir yerde izlenmiyordu. Denetim satırındaki `writtenConfirmationRequired=true` 24 ayda maskeleniyor ve kapatılamıyordu. Artık "Kaldır ve sil" kişisel veri içermeyen bir `ObifinCopyDeletions` kaydı açar: lisans, istek anı ve yapan, teyit anı ve yapan. Lisansa ve bağlantıya FK yoktur, kayıt budanmaz. Yönetici teyit gelince admin/obifin'de "Teyit alındı"ya basar; imha kaydı (`Obifin.HareketKopyasi`, tetikleyici `processor-confirmation`) aynı `SaveChanges`'te yazılır. Hatırlatma ve 14 günlük gecikme Görev 14'te.
- **Onaysız hesabın geçmiş verisi; onayı geri alma (Ö-10).** Hesap satırına "Reddet ve verisini sil" eklenir (`RejectAccountAsync`). Onay varsa önce o kaldırılır; arada koşan çekim turu o hesabın yeni hareketini yazmaz. Sonra o hesabın bu lisanstaki hareketleri, eşleşmeleri ve bu hareketlerden öğrenilmiş IBAN bağları (`SourceBankTransactionId`) partilerle silinir. Dekont bağı silinen ödemenin gap'i açılır: gap varsa yeniden açılır, yoksa `UnlinkedByAdmin` gap'i yazılır (bağdaştırıcının kaldırma kuralı, dekont ölçümden düşmez). İmha kaydı tetikleyici `account-rejected` ile yazılır. Yönetici hesap numarasını yazarak onaylar. Hesap satırı yer tutucu olarak kalır. (Görev 11'in sonraki görev kuralı 3: çekim turu hesap satırını tur boyunca izler; "arada koşan tur yeni hareketi yazmaz" iki sayfa arasında kaldırılan onayla da sınanmalı. Hesap satırını silen yerel silme ve "Kaldır ve sil" çekimle yarışırsa o sayfa `DbUpdateConcurrencyException` ile düşer, sonraki tur düzeltir.)
- **"Kaldır ve sil" yazılı onay ister (Purge'ün `confirmEmail` kalıbı).** Formun kendisi `ConfirmRemove=true` gönderdiği için sunucu denetimi anlamsızdı; tek koruma JS `confirm()`'uydu. Paylaşılmayan kullanıcıda EMAR'ın QNB bağlantısını Obifin'den kaldırabilen geri dönüşsüz işlem için bu yetmez. Artık yönetici lisans anahtarının son 6 karakterini yazar, sunucu büyük/küçük harf ayırmadan karşılaştırır; gizli alan kalkar.
- **Yerel silme partilidir.** `DeleteLocalAsync` lisansın bütün satırlarını bir kerede yüklüyordu; saklama işi bunun tersini yapıyor. Artık `BatchSize` (500) partilerle siler, her parti imha kaydını aynı `SaveChanges`'te yazar ve partiler arasında izleyici boşalır. Bağlantının kendisi (ya da `BankDataPurgedAt` işareti) EN SON silinir: arada düşen silme bağlantıyı bırakır, iş ertesi gün ya da yönetici yeniden basınca kalanı siler. "Kaldır ve sil"de bağlantı partilerden önce durdurulur (üçüncü inceleme): partiler sürerken çekim ve eşleştirme ona yazamaz. Jeton çakışmasında bütün silme bir kez baştan yeniden denenir; sorgular her partide baştan koştuğu için yeniden deneme idempotenttir ve sayılar denemeler boyunca birikir. Gerçek SQL Server'da (FK sırası, `BankConnections` → `ObifinConnections`, jetonun `WHERE` koşulu) `BankConnectionTerminatorRelationalTests` sınar.

**Üçüncü plan incelemesi (20.10) — bu görevin ek parçaları:**
- **Yerel silme sahiplik kanıtını silmez (K-6).** Yerel silme Obifin kimliğini ve yerel `BankConnections`'ı "yönetici Obifin tarafını sonra Kaldır ve sil ile kapatsın" diye bırakıyordu, ama bütün `BankAccounts` satırlarını onaylılarla birlikte siliyordu. `RemoveFromObifinAsync` Obifin panelinden eklenmiş bağlantının bu lisansa ait olduğunu YALNIZ onaylı hesaptan bilir; pilottaki QNB bağlantısının yerelde satırı yok (testteki #600 / hesap 6). Senaryo: hizmet sonu silmesi hesap 6'yı siler, hatırlatma "Kaldır ve sil"i ister, yönetici basar. #600 "bu lisansa ait olduğu doğrulanamadı — elle kontrol edin, başka lisansınsa dokunmayın" diye raporlanır ve Obifin'den kaldırılmaz. Bağlantı satırı silindiği için "Kaldır ve sil bekliyor" satırı da listeden düşer. Obifin hizmet bittiği halde yayıncının bankasından çekmeyi sürdürür, sistem de bunu "sahibi belirsiz" diye gösterirdi. Artık yerel silme onaylı hesap satırını bırakır, yalnız kişisel veri taşıyabilen alanlarını boşaltır (`IbanMasked`, `IbanHash`, `NotificationNote`; imha kaydı hedefi `BankConnectionTerminator.ScrubbedAccountTarget`, eylem `nulled`). Onaysız hesap kanıt değildir, silinir. Kalan satırları "Kaldır ve sil" siler. Kalan alanlar (`ObifinAccountId`, onay anı, onaylayan yöneticinin takma kimliği) aynı süre kalan Obifin kimliğinden daha tanımlayıcı değildir. Lisans yenilenip bağlantı yeniden doğrulanırsa hesap yenilemesi alanları yeniden doldurur ve onay geçerli kalır. Yönetici o arada satırdaki "Onayı geri al"a basarsa kanıt da gider; o bağlantı "Kaldır ve sil"de elle kontrole düşer (Prod adımları 10). `BankRemovalResult.AccountsScrubbed` boşaltılan satırları sayar. Test: `Yerel_silmeden_sonra_Kaldir_ve_sil_panelden_eklenmis_baglantiyi_onayli_hesaptan_tanir`.
- **"Kaldır ve sil" önce durdurur.** Partili silme saniyeler sürer ve bağlantı en son adıma kadar `Verified` kalıyordu. Yönetici sayfası `obifin-poll` kilidini almaz; iki parti arasında bir çekim turu ya da "Şimdi çek" araya girebilir. Çekimin kimlik koruması `UpdatedAt`'i karşılaştırır ve partiler sürerken `UpdatedAt` değişmediği için turun kaydı geçerdi. Eşleştirme kapısı (`MatchingActive`) açık kaldığı için sink ve telafi taraması da yeni hareket ve eşleşme yazabilirdi. Hareket döngüsünden sonra ve bağlantı silinmeden önce yazılan satırlar bağlantısız lisansta kalır, imha kaydına girmez ve ancak 180 günlük kuralla silinirdi. Yönetici ise "banka verisi silindi" mesajını görürdü. Pencere eski tek SaveChanges'tekinden geniştir; Obifin tarafı paylaşılan kullanıcı ya da ulaşılamadı olduğunda yeni hareket gelmesi olasıdır. Artık `RemoveAndDeleteAsync` Obifin çağrısından ve partilerden ÖNCE bağlantıyı tek SaveChanges'le durdurur: `StopAsync` ile aynı `MarkStopped` (`Disabled`, Disabled değilse `DisabledAt` ve `LastError`, `DisabledByAdmin`, yeni `UpdatedAt`). Çekimin kimlik koruması yarıdaki turun kaydını düşürür, sonraki turlar ve "Şimdi çek" Disabled bağlantıyı atlar, `MatchingActive` kapısı eşleştirmeyi kapatır. Kapıyı durdurmadan önce geçmiş tek bir yazı kalabilir (denetim ile kayıt arası); pencere saniyelerden milisaniyelere iner. Silme yarıda düşerse bağlantı Disabled kalır: güvenli yön; yönetici yeniden basar, basmazsa hizmet sonu sayacı işler. Test: `Kaldir_ve_sil_once_baglantiyi_tek_kayitla_durdurur_sonra_Obifine_gider_ve_siler` (ilk SaveChanges yalnız durdurmadır; Obifin çağrısı anında depodaki bağlantı Disabled).

**Uygulama notu (Görev 13, 21.10):** kod aşağıdaki adımlardan şu noktalarda ayrılır; sonraki görevler koddakine göre yazılır.
- **Hesap reddi IBAN bağının süresini geri çeker (Görev 10'un sonraki görev kuralı).** Adım 4'ün `RejectAccountAsync`'i yalnız reddedilen hesabın öğrettiği bağları siliyordu; hesabın havalesi başka havalenin öğrettiği bir bağı tazelemişse (kararı `IbanConfirmedAt` işaretli) bağın süresi silinmiş bir doğrulamada kalırdı. Kural bağdaştırıcıda tek yerde: yeni `PaymentMatchReconciler.RewindIbanConfirmationsAsync(removed, removedSet, reason, trigger, actor, ct)`. Kaldırmanın özel `RewindIbanConfirmationAsync`/`RemainingConfirmationsAsync`'i artık silinen kümeyi ifade olarak alır: kaldırmada `t.Id == kaldırılan`, retde hesabın BÜTÜN hareketleri (sonraki partide silinecek havale kalan doğrulama sayılmaz; öğreten havalesi silinen kümede olan bağa dokunulmaz, o bağ öğreten havaleyle gider). Kalan yoksa bağ partiyle aynı SaveChanges'te silinir: hedef `CustomerIbanMemories`, ölçüt `LastConfirmedAt+12ay, hesap reddi, kalan doğrulama yok`, tetikleyici `account-rejected`, yapan yönetici. `BankAccountRejectionResult.Memories` bu silmeleri de sayar. Kaldırmanın davranışı değişmedi (`IbanBondRewindRelationalTests`, `PaymentMatchRaceTests`).
- **Sonlandırıcının kurucusu bağdaştırıcıyı da alır:** `BankConnectionTerminator(db, client, protection, PaymentMatchReconciler, log)`. Bağdaştırıcı da hasher istemez: "BankHasher İSTEMEZ" korunur, sayfa anahtarsız modülde açılır (sayfa testi). **Görev 14'ün test yardımcısı** (`BankServiceEndPurgeJobTests.Job`) sonlandırıcıyı beş argümanla kurar; araya `new PaymentMatchReconciler(db, new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance), Options.Create(new BankOptions()), NullLogger<PaymentMatchReconciler>.Instance)` girer (`BankConnectionTerminatorTests.Terminator` kalıbı; yardımcı `internal static`, başka test sınıfları da kullanır). `NoCallObifin` yine çağrılmaz: bağdaştırıcı Obifin'e gitmez.
- **Durum hücresindeki 30 gün** sayfanın sabiti `IndexModel.ServiceEndGraceDays` (Görev 14'ün iş sabiti henüz yoktu). Görev 14 `BankServiceEndPurgeJob.GracePeriod`'u eklerken sabiti ona bağlar (tek kaynak); "Durdur" bildirimi de aynı sabiti yazar.
- **`PurgeLocalDataAsync` idempotent:** `BankDataPurgedAt` ilk silmenin anında kalır (`??=`); ikinci koşu kalan satırı siler, boşaltılmış hesabı yeniden saymaz (test). Teyit kaydının kimliği yalnız başarılı son kayıttan sonra döner.
- **Ret testleri adımlardakinden geniş:** gap çözümüyle bağlanmış dekontun kapalı gap'i yeniden açılır; ret partilidir; IBAN bağı süre kuralı InMemory'de (dört durum: geri çekme, kalan yok → silme, son doğrulama değil, işaretsiz karar) ve SQL Server'da (iki parti, ifade çevirisi) sınanır; ret çekim turunun ortasında (iki sayfa arasında) gelince sonraki sayfa yazılmaz, önceki sayfanın hareketleri silinir, onay geri yazılmaz (`ObifinPollJobTests.Hesap_reddi_tur_ortasinda_…`; Görev 11 kuralı 3).
- **Sayfa:** ret onayı da sabit (`RejectConfirmMessage`, kesme işaretsiz); `OnPostRejectAccountAsync` `ObifinValidationException`'ı bildirime çevirir. Kaldırma onayının uzunluğu `RemoveConfirmLength` (6). Denetim ayrıntısına `Gaps`, `Accounts`, `BankConnections` sayıları da girer. Uçtan uca sayfa testi (`Kaldir_ve_sil_Obifinde_bu_lisansin_banka_baglantisini_kaldirir`) Kaydet + Banka ekle ile gerçek şifreli kimlik ve Obifin'de eklenmiş bağlantı kurar, `bankaapi/sil`'in o bağlantı için çağrıldığını sınar; imha ve teyit kayıtlarının yapanı yöneticinin kimliği (sayfa testleri).
- **Başarılı doğrulama** Disabled olmayan bağlantının bayat hizmet sonu alanlarını boşaltır (`Basarili_dogrulama_…`); Disabled'dan kimlik değişimiyle çıkan bağlantının yönetici durdurması da boşalır (Theory iki yönü sınar).
- **Purge reddi** `confirmEmail` denetiminden sonra gelir (yanlış e-posta yine 400).

**Görev 13 incelemesi (21.10) — düzeltmeler:**
- **Durdurulmuş bağlantıda Doğrula ve Banka ekle Obifin'e gitmez (K-6, karar 21(a)).** Lisans denetimi yalnız lisans bitişini kesiyordu. Yolu: Durdur → 30. gün yerel silme → yönetici satırdaki Doğrula'ya basar → hesap listesi IBAN'sız bırakılmış onaylı hesabın `IbanMasked`, `IbanHash`, `NotificationNote`'unu yeniden doldurur, yeni onaysız hesap satırları ekler; `MarkVerified` Disabled'ı koruduğu için bağlantı Disabled + `BankDataPurgedAt` kalır ve Görev 14 işi onu bir daha işlemez. Karar 21(a)'nın "IBAN'sız … Kaldır ve sil'e kadar durur" cümlesi delinirdi (Ö-16 kalıbı). Banka ekle de durdurulmuş bağlantı için Obifin'de yeni çekim başlatırdı. Artık `VerifyAsync` ve `AddBankConnectionAsync` lisans denetiminin hemen altında `ObifinConnectionService.EnsureNotStopped` ile `StoppedConnectionMessage` ("Bağlantı durdurulmuş; önce Etkinleştir.") fırlatır. Yol: Etkinleştir (Unverified, hizmet sonu alanları boşalır) → Doğrula. Sayfada Doğrula yalnız Disabled olmayan satırda. **Bilinçli sınır:** hesap yenilemesi (`RefreshAccountsAsync`) kapı almadı; Disabled bağlantıya yalnız yarışta gelir (saatlik iş Verified listesini okuduktan sonra yönetici durdurur). Yerel silme ise ancak 30 gün sonra koşar. O yüzden yarışta yeniden doldurulacak boşaltılmış hesap yoktur, `MarkVerified` Disabled'ı korur. Testler: `Durdurulmus_ve_yerel_verisi_silinmis_baglantida_Dogrula_ve_Banka_ekle_Obifine_gitmez_kanit_hesap_bos_kalir`; `ObifinConnectionServiceTests.Devre_disi_baglantida_Dogrula_Obifine_gitmez_baglanti_acilmaz` (eski "Disabled bağlantı doğrulanır, açılmaz, Ok" testinin yerine); sayfa testi `Durdur_ve_etkinlestir_…` düğmeyi sınar.
- **Yerel verisi silinmiş bağlantıda kimlik değişimi reddedilir (karar 21(a)); seçilen yol, Burak değiştirebilir.** `ClearServiceEnd` `BankDataPurgedAt`'i boşaltır, `ResetShadowData` "Kaldır ve sil"in sahiplik kanıtını (yerel `BankConnections`, IBAN'sız onaylı hesaplar) siler: şerit ve hatırlatma kaybolur, eski Obifin kullanıcısındaki banka bağlantısı izlenmeden çekmeyi sürdürür. Plan "lisans geçerliyken kimlik değişimi"ni Disabled'dan çıkış yolu saymıştı, ama yerel silme sonrasını düşünmemişti. Lisans bitmişken de kanıt silinirdi. Artık `UpsertWithResultAsync` `credentialChanged && BankDataPurgedAt != null` iken lisans durumundan bağımsız `PurgedCredentialChangeMessage` ("önce Kaldır ve sil") fırlatır, hiçbir şey değişmez. Aynı kimliğe yeni parola/anahtar kimlik değişimi değildir, kaydedilir; bağlantı Disabled ve işaretli kalır. Seçilmeyen alternatif: kimlik değişimi gölge veriyi sıfırlarken eski bağlantıda yerel `BankConnections` ya da onaylı hesap varsa bir `ObifinCopyDeletions` kaydı açıp eski Obifin kullanıcısındaki bağlantıların elle kaldırılması gerektiğini bildirmek. Daha karmaşıktır, Obifin tarafını elle kapanışa bırakır ve sahiplik kanıtını yine siler. Test: `Yerel_verisi_silinmis_baglantida_kimlik_degisimi_reddedilir_Kaldir_ve_sil_kaniti_ve_isareti_kalir` (Theory: lisans geçerli/bitmiş).
- **Kaldırmadan sonra yeniden hesap durdurulmuş bağlantıda koşmaz (K-6).** Kaldırma açık kalır (bağ siler), ama ardından eşleştirici saklı hareketin açıklamasını ve IBAN hafızasını okuyup `Proposed` yazıyordu. Artık `UnmatchAsync` kaldırma kaydedildikten sonra `MatchingStateAsync == false` ise yeniden hesabı atlar: satır `NoProposal` kalır, `false` döner. Denetim de yeniden hesabın `try`'ı içindedir: düşerse kaldırma yine başarılıdır. Sayfanın `UnmatchedNotRecomputedMessage`'ı iki nedeni de söyler: hesap düştü ya da bağlantı durdurulmuş. Testler: `Durdurulmus_baglantida_kaldirma_acik_kalir_oneri_yeniden_hesaplanmaz` ve karşı yüzü `Kaldirmadan_sonra_yeniden_hesap_etkin_baglantida_oneri_yazar`.
- **Sonraki görev kuralı (Görev 14):** hizmet sonu hatırlatması `BankDataPurgedAt`'e güvenebilir. İşaret yalnız şu yollarla boşalır: Etkinleştir, Disabled olmayan bağlantıda başarılı doğrulama (bayat alan) ve lisansın yenilenmesi (`ReviveRenewedLicensesAsync`, yalnız yöneticinin durdurmadığı bağlantıda). Kimlik değişimi işareti artık boşaltmaz. Lisansı yenilenip Unverified'a dönen bağlantıda Doğrula çalışır (kapı yalnız Disabled'ı keser).

**Görev 13 ikinci incelemesi (21.10) — düzeltmeler:**
- **Hesap reddinin imha kaydı reddedilen hesabı gösterir (Important; Ö-10, Görev 9'un geri yükleme tasarımı).** Kayıtta yalnız lisans, hedef, sayı, ölçüt (`onaysız ya da reddedilen hesap (Ö-10)`), tetikleyici ve yapan vardı; hesap numarası yalnız denetim satırında ve günlükte duruyordu. İkisi de geri yüklemede ya kaybolur ya da prosedürün kaynağı değildir. Geri yüklemeden sonra silmeler veritabanı dışındaki saatlik özetten yeniden uygulanır; yedek reddedilmiş hesabın onayını (`ApprovedAt`) da geri getirir. O zaman çekim o hesabın hareketini yeniden yazar, silinen veri geri gelirdi. Artık bütün ret kayıtlarının ölçütü `BankConnectionTerminator.RejectionCriterion(N)` = `reddedilen hesap #N (Ö-10)` (Obifin hesap numarası kişisel veri değil, 36 aylık denetim satırında da var). IBAN bağı süre kuralının kaydı da aynı metni kullanır: `LastConfirmedAt+12ay, reddedilen hesap #N (Ö-10), kalan doğrulama yok` (100 karaktere sığar). Görev 23'ün geri yükleme listesine `account-rejected` ve `processor-confirmation` satırları eklendi. Testler: `Hesap_reddedilince_…` (bütün kayıtların ölçütü), `Hesap_reddi_baska_havalenin_…` (IBAN bağı satırı).
- **İkinci jeton çakışması yöneticiye 500 göstermez.** "Kaldır ve sil" (ve yerel silme) ile hesap reddi ikinci `DbUpdateConcurrencyException`'ı ham haliyle yukarı veriyordu. Repo kalıbı (`ObifinConnectionService.SaveAsync`, bağdaştırıcı) şu: izleyici boşalır, `BankDeletionIncompleteException` (bir `ObifinValidationException`) fırlar. Mesajlar `DeleteIncompleteMessage` ("silme yarıda kaldı, bağlantı durduruldu, yeniden deneyin") ve `RejectIncompleteMessage` ("onay kaldırıldı"). "Kaldır ve sil" sayfası bu yolda durdurmayı denetime yazar (`ObifinConnectionStop`, `removeIncomplete=true`). Başka `DbUpdateException`'lar (FK, kısıt) programlama hatasıdır, eskisi gibi yukarı gider. Testler: `Kaldir_ve_sil_yeniden_denemede_de_cakisirsa_…`, `Hesap_reddi_yeniden_denemede_de_cakisirsa_…`, sayfa `Kaldir_ve_sil_yarida_kalirsa_500_degil_…` (test yardımcısı `BankConnectionTerminatorTests.TransactionDeleteConflict`).
- **Yerel silme yalnız durdurulmuş bağlantıda.** `PurgeLocalDataAsync` durumu hiç denetlemiyordu; `MatchingActive`'in "kod `BankDataPurgedAt`'i yalnız Disabled'da yazar" varsayımı yalnız çağıranın sözleşmesine dayanıyordu. Artık durum başta taze okunur; Disabled değilse hiçbir şey silinmeden `NotStoppedMessage` fırlar. Son adım (işaret ve onaylı hesabın boşaltılması) aynı denetimi yeniden yapar: partiler sürerken açılan bağlantı işaretlenmez. Kaydedilmiş partiler gitmiştir, imha kayıtları onları sayar. Testler: `Yerel_silme_durdurulmamis_baglantida_…`, `Yerel_silme_surerken_etkinlestirilen_…`; yerel silmeyi çağıran eski testler önce durdurur.
- **"Kaldır ve sil" hesap yenilemesiyle yarışta geç yazılan hesabı da siler.** Saatlik yenilemenin kaydı hesap partisinden sonra, bağlantı silinmeden önce düşerse eklediği `BankAccounts` satırları (IBAN maskesi, özeti, bildirim notu) bağlantısız lisansta kalırdı. Artık son `SaveChanges` lisansın kalan hesap ve banka bağlantısı satırlarını bağlantıyla birlikte siler ve imha kaydına yazar. Bu kayıttan sonra düşen yenileme bütünüyle geri alınır, çünkü bağlantının UPDATE'i 0 satır etkiler. `RefreshAccountsAsync`'e taze okuma eklenmedi: son adım yeterli. Test: `Kaldir_ve_sil_hesap_partisinden_sonra_yazilan_…`.
- **Teyit ve kaldırma aynı anda iki kez kayıt açmaz.** `ObifinCopyDeletion.ConfirmedAt` eşzamanlılık jetonudur (şema değişmez, snapshot güncel). İkinci "Teyit alındı" `AlreadyConfirmedMessage` alır, imha kaydı teyitle aynı işlemde geri alınır: gerçek SQL Server testi `Teyit_alindi_ayni_anda_…_gercek_SQL_Serverda_…`; InMemory'de işlem olmadığı için orada yalnız hata sınanır. "Kaldır ve sil"in son adımı bağlantı satırı yoksa teyit kaydı açmaz (`Ayni_anda_iki_Kaldir_ve_sil_…`).
- **Etkinleştir, yerel verisi silinmiş bağlantıda çapayı açılış gününe çeker** (karar 28, plan varsayılanı): `ObifinConnectionService.ReanchorAfterLocalPurge`. Test: `Yerel_verisi_silinmis_baglanti_etkinlestirilince_…`.
- **"Kaldır ve sil"in elle işi kalıcı:** `ObifinCopyDeletions.ManualAction` (boş geçilebilir, 1000 karakter; göç `*_ObifinCopyDeletionManualAction`, yalnız `AddColumn`). Metin `BankRemovalResult.ManualActionText` ile aynı yerden (`DescribeManualAction`) gelir; elle iş yoksa `null`. Bekleyenler tablosu `data-cell="manual-action"` hücresinde gösterir (yoksa "yalnız yazılı teyit"). Testler: `Kaldirilamayan_baglanti_…`, `Elle_is_yoksa_…`, sayfa `Kaldir_lisans_anahtari_…`.
- **Gap kuralı tek yerde:** `PaymentMatchReconciler.ReturnPaymentsToGapsAsync(licenseId, paymentIds, ct)`; kaldırma ve hesap reddi onu kullanır (sonlandırıcının kopyası `ReopenGapsAsync` kalktı). `Obifine_ulasilamazsa_…` testi artık `AdminRemove` ve yönetici kimliğiyle.
- **Durdurulmuş bağlantıda kimlik kaydı** "Şimdi doğrulayın" demez: kaydedilen bağlantı Disabled ise bildirim `StoppedConnectionMessage` ("Bağlantı durdurulmuş; önce Etkinleştir.") ile biter (sayfa testi `Durdurulmus_baglantida_kimlik_kaydi_…`).
- **Sonraki görev kuralları (Görev 14):**
  1. `PurgeLocalDataAsync` Disabled olmayan bağlantıda `ObifinValidationException(BankConnectionTerminator.NotStoppedMessage)` fırlatır (başta ya da son adımda). İş bunu **düşme saymaz**: `catch (ObifinValidationException ex) when (ex.Message == BankConnectionTerminator.NotStoppedMessage)` genel `catch`'ten önce gelir, izleyiciyi boşaltır, bilgi düzeyinde günlükler, nota "atlandı: bağlantı bu sırada etkinleştirildi" yazar. `failures`'a eklemez, `deleted`'ı artırmaz. Test: iş bağlantıyı seçtikten sonra etkinleştirilen bağlantı silinmez, e-posta "DÜŞTÜ" demez. Hizmet sonu testlerinin tohumu yerel silinecek bağlantıyı Disabled yazar (seçim zaten bunu ister).
  2. `ReviveRenewedLicensesAsync`, `ClearServiceEnd(c)`'den ÖNCE `ReanchorAfterLocalPurge(c, now)` çağırır (karar 28). Lisansı 30 günden sonra yenilenen, kendiliğinden durdurulmuş bağlantının yerel verisi silinmiş olabilir. Test: yerel verisi silinmiş, lisansı yenilenen bağlantının çapası bugün − 2; silinmemiş olanınki değişmez.
  3. Hatırlatma e-postası `ObifinCopyDeletions.ManualAction`'ı da okur ve doluysa satıra ekler (ör. `lisans X: Obifin'den yazılı silme teyidi bekleniyor — … ; Obifin'de: <ManualAction>`). Böylece Obifin'de hâlâ çekim yapan bağlantı her gün hatırlatılır. Planın Adım 4'teki `awaitingConfirmation` sorgusu `d.ManualAction`'ı da seçer. Test: elle işi olan teyit kaydının satırı metni taşır.
  4. "Kaldır ve sil"in ve hesap reddinin ikinci çakışması `BankDeletionIncompleteException`'dır (bir `ObifinValidationException`). Yerel silmede aynı istisna işin genel `catch`'ine düşer ve "DÜŞTÜ" sayılır; ertesi gün kalanı siler.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Bank/BankConnectionTerminator.cs`
- Create: `OrderDeck.LicenseServer/Domain/Bank/ObifinCopyDeletion.cs`
- Modify: `OrderDeck.LicenseServer/Domain/Bank/ObifinConnection.cs` (`BankDataPurgedAt`, `DisabledByAdmin`)
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (`ObifinCopyDeletions`)
- Modify: `OrderDeck.LicenseServer/Services/Privacy/DataDestruction.cs` (`Triggers.AccountRejected`, `Triggers.ProcessorConfirmation`)
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs` (statik `ResolveCredentials`, `IsLicenseActiveAsync`, `ClearServiceEnd`, `MatchingActive`, `MatchingStateAsync`; `MarkVerified`, `VerifyAsync`, `AddBankConnectionAsync`, `UpsertWithResultAsync`)
- Modify: `OrderDeck.LicenseServer/Services/Bank/PaymentMatchReconcileJob.cs`, `BankMatchSweepJob.cs`, `PaymentMatchReconciler.cs` (eşleştirme kapısı)
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Obifin/Index.cshtml(.cs)`
- Modify: `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Customers/AdminCustomersController.cs` (`Purge` reddi)
- Modify: `OrderDeck.LicenseServer/Program.cs` (DI)
- Create: göç `*_ObifinConnectionServiceEnd`; göç `*_ObifinCopyDeletionManualAction` (ikinci inceleme)
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankConnectionTerminatorTests.cs` (YENİ), `BankConnectionTerminatorRelationalTests.cs` (YENİ, Testcontainers), `PaymentMatchReconcileJobTests.cs`, `BankMatchSweepJobTests.cs`, `PaymentMatchReconcilerTests.cs`, `Pages/Admin/AdminObifinPageTests.cs`, `Controllers/Compliance/GdprFlowTests.cs`

- [ ] **Adım 1: Servis testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Bank/BankConnectionTerminatorTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK K-6/Ö-9: bağlantıyı durdurma, etkinleştirme, kaldırıp silme. Obifin tarafı lisansa özgü: paylaşılan Obifin
/// kullanıcısında Obifin'e hiç gidilmez, yalnız bu lisansa ait bağlantılar kaldırılır. Otomatik yerel silme Obifin'e gitmez ve
/// "Kaldır ve sil"in sahiplik kanıtını (yerel banka bağlantısı, IBAN'sız onaylı hesap) bırakır. Obifin'e ulaşılamasa da yerel
/// silme yapılır. Yerel silme partilidir; "Kaldır ve sil" Obifin'in yazılı silme teyidini bekleyen
/// kayıt açar; onaysız hesap reddedilip verisi silinir (Ö-10).</summary>
public sealed class BankConnectionTerminatorTests
{
    /// <summary>Her SaveChanges'in içeriği: silinen hareket sayısı (parti sınırı kanıtı), silinen satır sayısı ve o kayıtta
    /// değişen Obifin bağlantısının durumu ("Kaldır ve sil"in bağlantıyı silmeden önce durdurduğunun kanıtı).</summary>
    private sealed class SaveLogInterceptor : SaveChangesInterceptor
    {
        public List<int> DeletedTransactionsPerSave { get; } = new();
        public List<(int DeletedRows, ObifinConnectionStatus? ConnectionStatus)> Saves { get; } = new();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var tracker = eventData.Context!.ChangeTracker;
            DeletedTransactionsPerSave.Add(tracker.Entries<BankTransaction>().Count(e => e.State == EntityState.Deleted));
            Saves.Add((tracker.Entries().Count(e => e.State == EntityState.Deleted),
                tracker.Entries<ObifinConnection>().Where(e => e.State == EntityState.Modified)
                    .Select(e => (ObifinConnectionStatus?)e.Entity.Status).FirstOrDefault()));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TerminatorObifin : IObifinClient
    {
        public List<ObifinBankConnectionDto> Connections { get; } = new();
        public List<ObifinAccountDto> Accounts { get; } = new();
        public Func<Exception>? ListFails { get; set; }
        /// <summary>Bağlantı listesi istenmeden hemen önce: Obifin çağrısı anındaki yerel durumu okumak için.</summary>
        public Func<Task>? BeforeList { get; set; }
        public HashSet<long> RemoveFails { get; } = new();
        public List<long> Removed { get; } = new();
        /// <summary>Obifin'e giden her çağrı (bağlantı listesi, hesap listesi, kaldırma).</summary>
        public int Calls { get; private set; }

        public async Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c,
            CancellationToken ct = default)
        {
            Calls++;
            if (BeforeList is { } hook) await hook();
            if (ListFails is { } fail) throw fail();
            return Connections.ToList();
        }
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default)
        {
            Calls++;
            if (RemoveFails.Contains(id)) return Task.FromException(new ObifinApiException(new[] { "Silinemedi" }));
            Removed.Add(id);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ObifinAccountDto>>(Accounts.ToList());
        }
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default)
        {
            Calls++;
            throw new NotSupportedException();
        }
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static LicenseDbContext NewDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-term-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    private static BankConnectionTerminator Terminator(LicenseDbContext db, IObifinClient client)
        => new(db, client, Protection, NullLogger<BankConnectionTerminator>.Instance);

    private static ObifinConnectionService Service(LicenseDbContext db, IObifinClient client)
        => new(db, client, Protection,
            new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" })),
            Options.Create(new ObifinOptions()), NullLogger<ObifinConnectionService>.Instance);

    /// <summary>Obifin hesap listesindeki hesap: <paramref name="id"/> = ObifinAccountId, bağlı olduğu banka bağlantısı
    /// <paramref name="bankaApiId"/>.</summary>
    private static ObifinAccountDto Account(long id, long bankaApiId) => new(id, "qnb", bankaApiId, null, null, "TL", null, null, true, null);

    /// <summary>Gerçek şifreli kimlikle bağlantı (servis üzerinden) + her banka tablosundan birer satır. Hesap 6 ONAYLI (bu
    /// lisansın tahsilat hesabı), hesap 9 onaysız (Obifin panelinden eklenmiş, sahibi belli değil); yerel banka bağlantısı
    /// #501. <paramref name="userCode"/> verilirse o Obifin kullanıcısıyla (paylaşılan kullanıcı senaryosu).</summary>
    private static async Task<Guid> SeedAsync(LicenseDbContext db, IObifinClient client, bool licenseActive = true,
        string? userCode = null)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"t-{Guid.NewGuid():N}@x", Name = "T",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new License { Id = lic, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = licenseActive ? DateTimeOffset.UtcNow.AddDays(30) : DateTimeOffset.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        var conn = await Service(db, client).UpsertAsync(lic, "", userCode ?? $"api-{Guid.NewGuid():N}@x",
            $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}", CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var tx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 1, ObifinAccountId = 6, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now, FetchedAt = now,
            Description = "aciklama" };
        db.BankTransactions.Add(tx);
        db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id,
            Status = PaymentMatchStatus.NoProposal, CreatedAt = now, UpdatedAt = now });
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(),
            IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = now });
        db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(), CreatedAt = now });
        db.BankAccounts.AddRange(
            new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 6, BankaKodu = "qnb",
                IbanMasked = "TR12…345", IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", NotificationNote = "bildirim",
                Currency = "TL", RefreshedAt = now, ApprovedAt = now, ApprovedBy = "test" },
            new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 9, BankaKodu = "garanti",
                IbanMasked = "?", Currency = "TL", RefreshedAt = now });
        db.BankConnections.Add(new BankConnection { Id = Guid.NewGuid(), LicenseId = lic, ObifinConnectionId = conn.Id,
            BankaKodu = "qnb", BankaApiId = 501, Label = "QNB", Status = BankConnectionStatus.Active, CreatedAt = now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return lic;
    }

    [Fact]
    public async Task Durdur_Disabled_ve_DisabledAt_yazar_ikinci_durdurma_tarihi_degistirmez()
    {
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        var t = Terminator(db, client);

        await t.StopAsync(lic, CancellationToken.None);
        var first = (await db.ObifinConnections.AsNoTracking().SingleAsync()).DisabledAt;
        await t.StopAsync(lic, CancellationToken.None);

        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync();
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.DisabledAt.Should().NotBeNull().And.Be(first);
        conn.DisabledByAdmin.Should().BeTrue();
        conn.LastError.Should().Be(BankConnectionTerminator.StoppedByAdminMessage);
    }

    [Fact]
    public async Task Lisans_bitisiyle_durmus_baglantiyi_Durdur_yonetici_kararina_cevirir_tarih_degismez()
    {
        // Lisans yenilenince kendiliğinden açılan bağlantı (Görev 14) yöneticinin durdurduğu bağlantı olmamalı.
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client, licenseActive: false);
        var disabledAt = DateTimeOffset.UtcNow.AddDays(-3);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status = ObifinConnectionStatus.Disabled; conn.DisabledAt = disabledAt;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        await Terminator(db, client).StopAsync(lic, CancellationToken.None);

        var after = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        after.DisabledByAdmin.Should().BeTrue();
        after.DisabledAt.Should().Be(disabledAt, "30 günlük sayaç baştan başlamaz");
    }

    [Fact]
    public async Task Etkinlestir_lisans_gecerliyse_Unverified_yapar_bitmisse_reddeder()
    {
        using var db = NewDb(); var client = new TerminatorObifin();
        var active = await SeedAsync(db, client);
        var ended = await SeedAsync(db, client, licenseActive: false);
        var t = Terminator(db, client);
        await t.StopAsync(active, CancellationToken.None);
        await t.StopAsync(ended, CancellationToken.None);

        await t.ResumeAsync(active, CancellationToken.None);
        var act = () => t.ResumeAsync(ended, CancellationToken.None);

        var resumed = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == active);
        resumed.Status.Should().Be(ObifinConnectionStatus.Unverified);
        resumed.DisabledAt.Should().BeNull();
        resumed.BankDataPurgedAt.Should().BeNull();
        resumed.DisabledByAdmin.Should().BeFalse();
        await act.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankConnectionTerminator.LicenseStillEndedMessage);
    }

    [Fact]
    public async Task Kaldir_yalniz_bu_lisansa_ait_baglantilari_kaldirir_digerini_elle_kontrole_birakir_yerel_veriyi_siler()
    {
        // KVKK K-6/Ö-9 + Faz 2 spec §13: Obifin kullanıcısının altında başka lisansın (ya da sahibi belirsiz) bankası olabilir.
        // Yalnız yerel BankConnections (#501) ve ONAYLI hesabın bankası (#600) kaldırılır; onaysız hesabın bankası (#777)
        // Obifin'de kalır ve elle kontrole bırakılır.
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        client.Connections.Add(new ObifinBankConnectionDto(600, "qnb", "Panelden", true));
        client.Connections.Add(new ObifinBankConnectionDto(777, "garanti", "Bilinmeyen", true));
        client.Accounts.Add(Account(6, 600));
        client.Accounts.Add(Account(9, 777));
        var lic = await SeedAsync(db, client);
        var other = await SeedAsync(db, client);

        var r = await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "admin",
            CancellationToken.None);

        client.Removed.Should().BeEquivalentTo(new[] { 501L, 600L });
        r.ObifinRemoved.Should().BeEquivalentTo(new[] { 501L, 600L });
        r.ObifinUnowned.Should().BeEquivalentTo(new[] { 777L }, "bu lisansa ait olduğu doğrulanamayan bağlantıya dokunulmaz");
        r.NeedsManualObifinAction.Should().BeTrue();
        r.ManualActionText().Should().Contain("#777").And.Contain("elle kontrol");
        (r.Transactions, r.Matches, r.Memories, r.Gaps, r.Accounts, r.BankConnections).Should().Be((1, 1, 1, 1, 2, 1));
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == lic)).Should().BeFalse("Obifin kimliği de silinir");
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == lic)).Should().BeFalse();
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == other)).Should().BeTrue("başka lisansa dokunulmaz");
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == other)).Should().BeTrue();
        var records = await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic).ToListAsync();
        records.Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.AdminRemove && x.Actor == "admin");
        records.Select(x => x.Target).Should().Contain(new[] { "BankTransactions", "PaymentMatches", "CustomerIbanMemories",
            "PaymentMatchGaps", "BankAccounts", "BankConnections", "ObifinConnections" });
    }

    [Fact]
    public async Task Paylasilan_Obifin_kullanicisinda_Obifine_hic_gidilmez_baska_lisansa_dokunulmaz()
    {
        // Faz 2 spec §13 (A): tek paylaşılan Obifin kullanıcısı. Bir lisansın (test/demo ya da başka yayıncı) kaldırılması
        // EMAR'ın QNB bağlantısını Obifin'den silmemeli: QNB API'si için yeniden başvuru haftalar sürer.
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        client.Accounts.Add(Account(6, 501));
        var sharedUser = $"api-{Guid.NewGuid():N}@x";
        var lic = await SeedAsync(db, client, userCode: sharedUser);
        var emar = await SeedAsync(db, client, userCode: sharedUser);

        var r = await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "admin",
            CancellationToken.None);

        client.Calls.Should().Be(0, "paylaşılan Obifin kullanıcısında Obifin'e hiç gidilmez");
        client.Removed.Should().BeEmpty();
        r.SharedObifinUser.Should().BeTrue();
        r.NeedsManualObifinAction.Should().BeTrue();
        r.ManualActionText().Should().Contain("başka bir lisansta da kayıtlı");
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == lic)).Should().BeFalse("yerel silme yine yapılır");
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == lic)).Should().BeFalse();
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == emar)).Should().BeTrue();
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == emar)).Should().BeTrue();
        (await db.BankConnections.AnyAsync(b => b.LicenseId == emar && b.BankaApiId == 501)).Should().BeTrue();
    }

    [Fact]
    public async Task Obifine_ulasilamazsa_yerel_silme_yine_yapilir_elle_islem_istenir()
    {
        using var db = NewDb(); var client = new TerminatorObifin { ListFails = () => new HttpRequestException("ağ") };
        var lic = await SeedAsync(db, client);

        var r = await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.ServiceEnd,
            DataDestruction.SystemActor, CancellationToken.None);

        r.ObifinUnreachable.Should().BeTrue();
        r.NeedsManualObifinAction.Should().BeTrue();
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == lic)).Should().BeFalse();
    }

    [Fact]
    public async Task Kaldirilamayan_baglanti_sonucta_listelenir()
    {
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        client.Connections.Add(new ObifinBankConnectionDto(502, "qnb", "QNB-2", true));
        client.Accounts.Add(Account(6, 502));   // onaylı hesabın bankası: bu lisansın
        client.RemoveFails.Add(502);
        var lic = await SeedAsync(db, client);

        var r = await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "admin",
            CancellationToken.None);

        r.ObifinRemoved.Should().BeEquivalentTo(new[] { 501L });
        r.ObifinFailed.Should().BeEquivalentTo(new[] { 502L });
        r.NeedsManualObifinAction.Should().BeTrue();
        r.ManualActionText().Should().Contain("#502");
    }

    [Fact]
    public async Task Kaldir_ve_sil_once_baglantiyi_tek_kayitla_durdurur_sonra_Obifine_gider_ve_siler()
    {
        // Partili silme saniyeler sürer, yönetici sayfası obifin-poll kilidini almaz. Bağlantı en son adıma kadar Verified
        // kalsaydı arada koşan çekim turunun kaydı kimlik korumasından geçer (UpdatedAt değişmemiş), sink ve telafi taraması
        // yazar; döngüden sonra yazılan satır bağlantısız lisansta, imha kaydı dışında kalırdı. İlk kayıt yalnız durdurmadır,
        // Obifin çağrısı anında depodaki bağlantı durdurulmuştur.
        var log = new SaveLogInterceptor();
        using var db = NewDb(log); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        var lic = await SeedAsync(db, client);
        var seeded = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        seeded.Status = ObifinConnectionStatus.Verified;
        seeded.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        ObifinConnection? atObifinCall = null;
        client.BeforeList = async () =>
            atObifinCall = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        log.Saves.Clear();

        await Terminator(db, client).RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "admin",
            CancellationToken.None);

        log.Saves[0].Should().Be((0, (ObifinConnectionStatus?)ObifinConnectionStatus.Disabled),
            "ilk kayıt yalnız durdurma: hiçbir satır silinmeden");
        atObifinCall.Should().NotBeNull();
        (atObifinCall!.Status, atObifinCall.DisabledByAdmin).Should().Be((ObifinConnectionStatus.Disabled, true));
        atObifinCall.DisabledAt.Should().NotBeNull("silme yarıda düşerse hizmet sonu sayacı işler");
        atObifinCall.UpdatedAt.Should().BeAfter(before.UpdatedAt, "yarıdaki çekim turunun kimlik koruması kaydını düşürsün");
        log.Saves.Skip(1).Should().Contain(s => s.DeletedRows > 0, "silme durdurmadan sonra");
        client.Removed.Should().Equal(501L);
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == lic)).Should().BeFalse();
    }

    [Fact]
    public async Task Yerel_silme_Obifine_gitmez_kimligi_banka_baglantisini_ve_onayli_hesabi_IBANsiz_tutar_isaret_yazar()
    {
        // Otomatik hizmet sonu işi (Görev 14) bankaapi/sil çağırmaz: Obifin tarafını yönetici "Kaldır ve sil" ile kapatır. O da
        // bu lisansa ait bağlantıyı yerel BankConnections'tan ve ONAYLI hesaptan bilir: ikisi kalır. Onaylı hesabın kişisel veri
        // taşıyabilen alanları boşalır; onaysız hesap (9) kanıt değildir, silinir.
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        var lic = await SeedAsync(db, client);

        var r = await Terminator(db, client).PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd,
            DataDestruction.SystemActor, CancellationToken.None);

        client.Calls.Should().Be(0);
        r.ObifinNotAttempted.Should().BeTrue();
        r.NeedsManualObifinAction.Should().BeTrue();
        r.ManualActionText().Should().Contain("Kaldır ve sil");
        (r.Transactions, r.Matches, r.Memories, r.Gaps, r.Accounts, r.AccountsScrubbed, r.BankConnections)
            .Should().Be((1, 1, 1, 1, 1, 1, 0));
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.BankDataPurgedAt.Should().NotBeNull();
        (await db.BankConnections.AnyAsync(b => b.LicenseId == lic && b.BankaApiId == 501)).Should().BeTrue("yönetici sonra kaldırır");
        var kept = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic);
        (kept.ObifinAccountId, kept.ApprovedAt is not null, kept.IbanMasked, kept.IbanHash, kept.NotificationNote)
            .Should().Be((6L, true, "", (string?)null, (string?)null), "onaylı hesap Kaldır ve sil'in sahiplik kanıtı; IBAN'ı kalmaz");
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == lic)).Should().BeFalse();
        var records = await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic).ToListAsync();
        records.Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.ServiceEnd)
            .And.NotContain(x => x.Target == "ObifinConnections" || x.Target == "BankConnections");
        records.Should().ContainSingle(x => x.Target == "BankAccounts").Subject.RowCount.Should().Be(1, "yalnız onaysız hesap silindi");
        var scrubbed = records.Should().ContainSingle(x => x.Target == BankConnectionTerminator.ScrubbedAccountTarget).Subject;
        (scrubbed.Action, scrubbed.RowCount).Should().Be((DataDestruction.Actions.Nulled, 1));
    }

    [Fact]
    public async Task Yerel_silmeden_sonra_Kaldir_ve_sil_panelden_eklenmis_baglantiyi_onayli_hesaptan_tanir()
    {
        // KVKK K-6: Obifin panelinden eklenmiş bağlantının (#600; pilottaki QNB gibi) yerelde satırı yok, bu lisansa ait olduğu
        // yalnız onaylı hesaptan (6 → 600) bilinir. Hizmet sonu silmesi hesabı silseydi #600 "sahibi doğrulanamadı" diye
        // Obifin'de kalır, Obifin hizmet bittiği halde bankadan çekmeyi sürdürürdü.
        using var db = NewDb(); var client = new TerminatorObifin();
        client.Connections.Add(new ObifinBankConnectionDto(501, "qnb", "QNB", true));
        client.Connections.Add(new ObifinBankConnectionDto(600, "qnb", "Panelden", true));
        client.Accounts.Add(Account(6, 600));
        var lic = await SeedAsync(db, client);
        var t = Terminator(db, client);
        await t.PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor, CancellationToken.None);

        var r = await t.RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "admin", CancellationToken.None);

        client.Removed.Should().BeEquivalentTo(new[] { 501L, 600L });
        r.ObifinUnowned.Should().BeEmpty();
        r.NeedsManualObifinAction.Should().BeFalse();
        (r.Accounts, r.BankConnections).Should().Be((1, 1), "yerel silmenin bıraktığı onaylı hesap ve banka bağlantısı şimdi gider");
        (await db.BankAccounts.AnyAsync(a => a.LicenseId == lic)).Should().BeFalse();
        (await db.BankConnections.AnyAsync(b => b.LicenseId == lic)).Should().BeFalse();
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == lic)).Should().BeFalse();
    }

    [Fact]
    public async Task Lisansi_bitmis_baglantida_Dogrula_ve_Banka_ekle_Obifine_gitmez()
    {
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client, licenseActive: false);
        var svc = Service(db, client);

        var verify = () => svc.VerifyAsync(lic, CancellationToken.None);
        var add = () => svc.AddBankConnectionAsync(lic, "qnb", "QNB", new Dictionary<string, string>(), CancellationToken.None);

        await verify.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.LicenseEndedMessage);
        await add.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.LicenseEndedMessage);
        client.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Durdurulmus_baglantida_kimlik_degisince_lisans_gecerliyse_Unverified_sayac_bosalir_bitmisse_Disabled_kalir(
        bool licenseActive)
    {
        // KVKK K-6: hizmet sonu sayacı kimlik düzenlemesiyle sıfırlanmaz; Disabled'dan çıkan bağlantının tarihi kalmaz.
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client, licenseActive);
        var disabledAt = DateTimeOffset.UtcNow.AddDays(-5);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status = ObifinConnectionStatus.Disabled; conn.DisabledAt = disabledAt;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        await Service(db, client).UpsertAsync(lic, "", $"api-{Guid.NewGuid():N}@x", null, null, CancellationToken.None,
            allowShadowReset: true);

        var after = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        if (licenseActive)
        {
            after.Status.Should().Be(ObifinConnectionStatus.Unverified);
            after.DisabledAt.Should().BeNull();
        }
        else
        {
            after.Status.Should().Be(ObifinConnectionStatus.Disabled);
            after.DisabledAt.Should().Be(disabledAt);
        }
    }

    [Fact]
    public async Task Yerel_silme_parti_parti_yapilir_izleyici_birikmez_kayit_toplami_tutar()
    {
        // Saklama işinin kalıbı: lisansın bütün satırları bir kerede yüklenmez; her parti kaydını kendi SaveChanges'inde yazar.
        var counter = new SaveLogInterceptor();
        using var db = NewDb(counter); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);   // 1 hareket
        var now = DateTimeOffset.UtcNow;
        db.BankTransactions.AddRange(Enumerable.Range(0, BankConnectionTerminator.BatchSize * 2).Select(i => new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 100 + i, ObifinAccountId = 6, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now, FetchedAt = now,
        }));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        counter.DeletedTransactionsPerSave.Clear();
        var total = BankConnectionTerminator.BatchSize * 2 + 1;

        var r = await Terminator(db, client).PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd,
            DataDestruction.SystemActor, CancellationToken.None);

        r.Transactions.Should().Be(total);
        counter.DeletedTransactionsPerSave.Where(n => n > 0).Should().HaveCount(3)
            .And.OnlyContain(n => n <= BankConnectionTerminator.BatchSize);
        db.ChangeTracker.Entries().Should().BeEmpty("partiler arasında ve sonda izleyici boşaltılır");
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic && x.Target == "BankTransactions")
            .SumAsync(x => x.RowCount)).Should().Be(total);
    }

    [Fact]
    public async Task Kaldir_ve_sil_Obifin_kopyasi_icin_teyit_bekleyen_kayit_acar_yerel_silme_acmaz()
    {
        // KVKK Ö-9 öneri 3: Obifin'den yazılı silme teyidi, bağlantı satırı silindikten sonra da izlenebilmeli.
        using var db = NewDb(); var client = new TerminatorObifin();
        var removed = await SeedAsync(db, client);
        var purged = await SeedAsync(db, client);
        var t = Terminator(db, client);

        var r = await t.RemoveAndDeleteAsync(removed, DataDestruction.Triggers.AdminRemove, "yonetici-1", CancellationToken.None);
        await t.PurgeLocalDataAsync(purged, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor,
            CancellationToken.None);

        var pending = await db.ObifinCopyDeletions.AsNoTracking().SingleAsync();
        (pending.Id, pending.LicenseId, pending.RequestedBy, pending.ConfirmedAt)
            .Should().Be((r.ObifinCopyDeletionId!.Value, removed, "yonetici-1", (DateTimeOffset?)null));
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == removed)).Should().BeFalse("kayıt bağlantıdan bağımsız kalır");
    }

    [Fact]
    public async Task Teyit_alindi_kaydi_kapatir_imha_kaydi_yazar_ikinci_teyit_reddedilir()
    {
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        var t = Terminator(db, client);
        var r = await t.RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "yonetici-1", CancellationToken.None);

        (await t.ConfirmObifinCopyDeletedAsync(r.ObifinCopyDeletionId!.Value, "yonetici-2", CancellationToken.None))
            .Should().Be(lic);
        var again = () => t.ConfirmObifinCopyDeletedAsync(r.ObifinCopyDeletionId!.Value, "yonetici-2", CancellationToken.None);

        var row = await db.ObifinCopyDeletions.AsNoTracking().SingleAsync();
        row.ConfirmedAt.Should().NotBeNull();
        row.ConfirmedBy.Should().Be("yonetici-2");
        (await db.DataDestructionRecords.AsNoTracking()
                .SingleAsync(x => x.Trigger == DataDestruction.Triggers.ProcessorConfirmation))
            .Should().Match<OrderDeck.LicenseServer.Domain.DataDestructionRecord>(x => x.LicenseId == lic
                && x.Target == BankConnectionTerminator.ObifinCopyTarget && x.Actor == "yonetici-2" && x.RowCount == 1);
        await again.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankConnectionTerminator.AlreadyConfirmedMessage);
    }

    [Fact]
    public async Task Hesap_reddedilince_hareketleri_eslesmeleri_ogrettigi_IBAN_bagi_silinir_dekont_gapi_acilir_onay_geri_alinir()
    {
        // KVKK Ö-10: Görev 12'nin kapısı ileriye dönük; P1'den önce onaysız hesaptan yazılmış veri ancak bu eylemle gider.
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);   // hesap 6 onaylı (1 hareket), hesap 9 onaysız
        var now = DateTimeOffset.UtcNow;
        var foreign = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 900, ObifinAccountId = 9,
            BankaKodu = "garanti", Direction = BankTransactionDirection.Incoming, Amount = 20m, Currency = "TL", OccurredAt = now,
            FetchedAt = now, Description = "yabanci" };
        var paymentId = Guid.NewGuid();
        db.BankTransactions.Add(foreign);
        db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = foreign.Id,
            PaymentId = paymentId, Status = PaymentMatchStatus.ConfirmedByHuman, CreatedAt = now, UpdatedAt = now });
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(),
            IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.HumanApproval,
            SourceBankTransactionId = foreign.Id, CreatedAt = now });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var account9 = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic && a.ObifinAccountId == 9);
        var account6 = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic && a.ObifinAccountId == 6);
        var t = Terminator(db, client);

        var r = await t.RejectAccountAsync(account9.Id, "yonetici-1", CancellationToken.None);

        (r.Transactions, r.Matches, r.Memories, r.GapsReopened).Should().Be((1, 1, 1, 1));
        (await db.BankTransactions.Where(x => x.LicenseId == lic).Select(x => x.ObifinAccountId).ToListAsync())
            .Should().Equal(6L);
        (await db.CustomerIbanMemories.CountAsync(m => m.LicenseId == lic)).Should().Be(1, "başka hareketten gelen bağ kalır");
        var gap = await db.PaymentMatchGaps.AsNoTracking().SingleAsync(g => g.PaymentId == paymentId);
        (gap.Reason, gap.ResolvedAt).Should().Be((PaymentMatchGapReason.UnlinkedByAdmin, (DateTimeOffset?)null),
            "dekont ölçümden düşmez (bağdaştırıcının kaldırma kuralı)");
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic).ToListAsync())
            .Should().NotBeEmpty().And.OnlyContain(x => x.Trigger == DataDestruction.Triggers.AccountRejected
                && x.Actor == "yonetici-1");
        (await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == account9.Id)).ApprovedAt.Should().BeNull();

        await t.RejectAccountAsync(account6.Id, "yonetici-1", CancellationToken.None);   // onaylı hesabın onayı geri alınır

        var revoked = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == account6.Id);
        (revoked.ApprovedAt, revoked.ApprovedBy).Should().Be(((DateTimeOffset?)null, (string?)null));
        (await db.BankTransactions.AnyAsync(x => x.LicenseId == lic)).Should().BeFalse();
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankConnectionTerminatorTests"`
Expected: FAIL — `BankConnectionTerminator`, `BankDataPurgedAt`, `DisabledByAdmin`, `ObifinCopyDeletions` yok.

- [ ] **Adım 3: Varlık alanları, statik kimlik çözümü ve `DisabledAt` yaşam döngüsü**

Görev 1'in belgeleri P1 davranışını "Görev 13/14 ile" diye işaretledi; bu görev yöneticinin durdurmasını, etkinleştirmeyi ve `DisabledAt` boşaltmasını getirir. `Domain/Bank/ObifinConnection.cs` — `DisabledAt`'in özetini şununla değiştir ("bilinen boşluk" cümlesi gider: kimlik değişimi artık lisans bitmişse Disabled'ı korur):

```csharp
    /// <summary>Bağlantının <see cref="ObifinConnectionStatus.Disabled"/>'a çekildiği an (KVKK K-6): lisans bitti
    /// (<c>ObifinConnectionService.DisableEndedLicensesAsync</c>) ya da yönetici durdurdu (<c>BankConnectionTerminator</c>).
    /// Zaten Disabled olan bağlantıda yeniden yazılmaz: sayaç baştan başlamaz. Hizmet sonu silmesi (Görev 14) bu andan 30 gün
    /// sonra koşar; Disabled'dan çıkan her yol <c>ObifinConnectionService.ClearServiceEnd</c> ile boşaltır.</summary>
```

Aynı dosyada `DisabledAt`'in ALTINA:

```csharp
    /// <summary>Bağlantıyı yönetici durdurdu (KVKK K-6). Lisans bitişiyle kendiliğinden durdurulan bağlantı lisans yenilenince
    /// kendiliğinden Unverified'a döner (BankServiceEndPurgeJob); yöneticinin durdurduğu dönmez. Disabled'dan çıkınca boşalır.</summary>
    public bool DisabledByAdmin { get; set; }
    /// <summary>Hizmet sonu işinin bu bağlantının banka verisini YEREL olarak sildiği an (Obifin'e gidilmez). Obifin kimliği,
    /// yerel <c>BankConnections</c> ve onaylı hesap satırları (IBAN alanları boş) durur: yönetici Obifin tarafını "Kaldır ve sil"
    /// ile kapatır, o da bu lisansa ait bağlantıyı onlardan bilir. Doluyken iş bağlantıyı yeniden işlemez; Disabled'dan çıkınca
    /// boşalır.</summary>
    public DateTimeOffset? BankDataPurgedAt { get; set; }
```

`Services/Bank/ObifinConnectionService.cs` — `DisableEndedLicensesAsync`'in (Görev 1) ALTINA:

```csharp
    /// <summary>Lisans bugün geçerli mi (iptal edilmemiş, süresi dolmamış). Doğrulama, banka ekleme, kimlik değişimi,
    /// etkinleştirme ve hizmet sonu işi aynı kuralı kullanır: yüklem <see cref="LicenseActiveAt"/> (Görev 1), banka
    /// modülündeki tek tanım — burada yeniden yazılmaz.</summary>
    public static Task<bool> IsLicenseActiveAsync(LicenseDbContext db, Guid licenseId, DateTimeOffset now, CancellationToken ct)
        => db.Licenses.Where(LicenseActiveAt(now)).AnyAsync(l => l.Id == licenseId, ct);

    /// <summary>Bağlantı Disabled'dan çıkarken hizmet sonu alanları boşalır (KVKK K-6): 30 günlük sayaç, yerel silme işareti,
    /// yönetici durdurması. Disabled'dan çıkan her yol bunu çağırır: Etkinleştir, lisans geçerliyken kimlik değişimi, başarılı
    /// doğrulama, lisansın yenilenmesi (Görev 14). Kaydetmez.</summary>
    public static void ClearServiceEnd(ObifinConnection conn)
    {
        conn.DisabledAt = null;
        conn.BankDataPurgedAt = null;
        conn.DisabledByAdmin = false;
    }

    /// <summary>KVKK K-6: bağlantı eşleştirmeye açık mı — durdurulmamış (<see cref="ObifinConnectionStatus.Disabled"/> değil) ve
    /// yerel banka verisi hizmet sonunda silinmemiş. Durdurma ve lisans bitişi yalnız çekimi değil işlemeyi de durdurur: dekont
    /// bağdaştırma işi, telafi taraması, gap çözümü, onay bağdaştırması ve elle eşleme yalnız bunu tutan bağlantının lisansında
    /// koşar (talimat bittikten sonra işlemeyi sürdüren taraf kendi amacını belirleyen konuma düşer). Banka modülündeki tek tanım
    /// (<see cref="LicenseActiveAt"/> gibi); EF sorgusuna çevrilir. İki koşul ayrı ayrı sınanır: kod <c>BankDataPurgedAt</c>'i
    /// yalnız Disabled'da yazar, elle (SQL) düzeltilmiş bir satırda da kapı tutsun.</summary>
    public static Expression<Func<ObifinConnection, bool>> MatchingActive
        => c => c.Status != ObifinConnectionStatus.Disabled && c.BankDataPurgedAt == null;

    /// <summary>Lisansın eşleştirme durumu: bağlantısı yoksa null, varsa <see cref="MatchingActive"/>. İşler (dekont
    /// bağdaştırma, telafi taraması) yalnız true'da koşar; bağdaştırıcının kendisi yalnız false'ta durur — bağlantısız lisansta
    /// banka hareketi yoktur, bugünkü davranış korunur.</summary>
    public static async Task<bool?> MatchingStateAsync(LicenseDbContext db, Guid licenseId, CancellationToken ct)
    {
        var states = await db.ObifinConnections.AsNoTracking().Where(c => c.LicenseId == licenseId)
            .Select(MatchingActive).ToListAsync(ct);
        return states.Count == 0 ? null : states[0];
    }
```

`MarkVerified`'ı şununla değiştir. Özetin Görev 1'deki "Bugün Disabled'ı yalnız lisans bitişi yazar … Görev 13/14 ile gelir." cümlesi şuna döner: `Disabled'ı lisans bitişi (DisableEndedLicensesAsync) ve yönetici (BankConnectionTerminator) yazar; ondan Etkinleştir, lisans geçerliyken kimlik değişimi ve lisansın yenilenmesi (Görev 14) çıkarır.` Sonuna bir cümle: `Verified'a geçen bağlantının hizmet sonu alanları boşalır.`

```csharp
    private static void MarkVerified(ObifinConnection conn, DateTimeOffset now)
    {
        if (conn.Status != ObifinConnectionStatus.Disabled)
        {
            conn.Status = ObifinConnectionStatus.Verified;
            // DisabledAt/BankDataPurgedAt Disabled dışında anlamsız: durum hücresi ve hizmet sonu işi bayat tarih görmesin.
            ClearServiceEnd(conn);
        }
        conn.LastError = null; conn.LastVerifiedAt = now; conn.UpdatedAt = now;
    }
```

`VerifyAsync`'te `?? throw new ObifinValidationException("Obifin bağlantısı yok.");` satırının ALTINA ve `AddBankConnectionAsync`'te `?? throw new ObifinValidationException("Önce Obifin bağlantısı kaydedilmeli.");` satırının ALTINA:

```csharp
        // KVKK K-6: lisansı bitmiş yayıncının bankası için Obifin'e gidilmez (çekim ve hesap yenilemeyle aynı kural).
        if (!await IsLicenseActiveAsync(_db, licenseId, DateTimeOffset.UtcNow, ct))
            throw new ObifinValidationException(LicenseEndedMessage);
```

`UpsertWithResultAsync`'te durum satırını:

```csharp
        conn.Status = conn.Status == ObifinConnectionStatus.Disabled && !credentialChanged
            ? ObifinConnectionStatus.Disabled : ObifinConnectionStatus.Unverified;
```
şununla değiştir:
```csharp
        // Disabled bağlantı kimlik değişince açılır — YALNIZ lisans geçerliyse (KVKK K-6: lisansı bitmiş bağlantının hizmet
        // sonu sayacı kimlik düzenlemesiyle sıfırlanmasın; açılsaydı çekim onu hemen yeni tarihle yeniden durdururdu).
        var stayDisabled = conn.Status == ObifinConnectionStatus.Disabled
            && (!credentialChanged || !await IsLicenseActiveAsync(_db, licenseId, now, ct));
        conn.Status = stayDisabled ? ObifinConnectionStatus.Disabled : ObifinConnectionStatus.Unverified;
        if (!stayDisabled) ClearServiceEnd(conn);
```

Statik kimlik çözümü — örnek `TryResolveCredentials` metodunu şununla değiştir:

```csharp
    /// <summary>Şifre çözülemezse null (anahtar halkası kaybı) — çağıran Failed'a çeker.</summary>
    public ObifinCredentials? TryResolveCredentials(ObifinConnection conn) => ResolveCredentials(_protection, conn);

    /// <summary>Örnek gerektirmeyen çözüm: <see cref="BankHasher"/> istemeyen yollar (BankConnectionTerminator) için. Amaçlar
    /// örnekle aynı; çözülemezse null.</summary>
    public static ObifinCredentials? ResolveCredentials(IDataProtectionProvider protection, ObifinConnection conn)
    {
        try
        {
            return new ObifinCredentials(conn.BaseUrl, conn.UserCode,
                protection.CreateProtector(PasswordPurpose).Unprotect(conn.PasswordProtected),
                protection.CreateProtector(ApiKeyPurpose).Unprotect(conn.ApiKeyProtected));
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
```

Alanlara `private readonly IDataProtectionProvider _protection;` ekle ve kurucuda `_protection = protection;` ata (mevcut `_passwordProtector`/`_apiKeyProtector` kalır; şifreleme onları kullanıyor).

Obifin teyit kaydı — `OrderDeck.LicenseServer/Domain/Bank/ObifinCopyDeletion.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain.Bank;

/// <summary>
/// Obifin'deki hareket kopyasının silinmesi için yazılı teyit bekleyen istek (KVKK Ö-9 öneri 3, K-6; plan karar 21). "Kaldır ve
/// sil" açar (<c>BankConnectionTerminator</c>); yönetici Obifin'in yazılı teyidi gelince "Teyit alındı" ile kapatır. Kişisel veri
/// İÇERMEZ: lisans, anlar, yöneticinin takma kimliği. Lisansa ve bağlantıya FK YOK: ikisi de silinse kayıt yaşar; hiçbir iş
/// budamaz. Kapanana kadar <c>BankServiceEndPurgeJob</c> her gün hatırlatır.
/// </summary>
public sealed class ObifinCopyDeletion
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    /// <summary>"Kaldır ve sil" anı: Obifin'den yazılı silme bu andan beklenir.</summary>
    public DateTimeOffset RequestedAt { get; set; }
    /// <summary>Yöneticinin kimliği (<c>DataDestruction.ActorOf</c>), kullanıcı adı değil (KVKK Ö-14).</summary>
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? ConfirmedBy { get; set; }
}
```

`Data/LicenseDbContext.cs` — `DataDestructionRecords` DbSet'inin ALTINA `public DbSet<ObifinCopyDeletion> ObifinCopyDeletions => Set<ObifinCopyDeletion>();`; `mb.Entity<DataDestructionRecord>(…)` bloğunun ALTINA:

```csharp
        // Obifin kopyasının yazılı silme teyidi (KVKK Ö-9). Lisansa ve bağlantıya FK BİLEREK yok: kayıt ikisinden de uzun yaşar.
        mb.Entity<ObifinCopyDeletion>(b =>
        {
            b.HasKey(d => d.Id);
            b.Property(d => d.RequestedBy).HasMaxLength(256).IsRequired();
            b.Property(d => d.ConfirmedBy).HasMaxLength(256);
            b.HasIndex(d => d.LicenseId);
            // Hatırlatma sorgusu (ConfirmedAt IS NULL): bekleyen satır birkaç tanedir.
            b.HasIndex(d => d.RequestedAt, "IX_ObifinCopyDeletions_Pending").HasFilter("[ConfirmedAt] IS NULL");
        });
```

`Services/Privacy/DataDestruction.cs` — `Triggers` sınıfında `Cleanup`'ın ALTINA:

```csharp
        /// <summary>Yönetici onaysız hesabı reddetti ya da onayı geri aldı; hesabın verisi silindi (KVKK Ö-10).</summary>
        public const string AccountRejected = "account-rejected";
        /// <summary>Veri işleyenin (Obifin) kendi kopyasını sildiğine dair yazılı teyidi kaydedildi (KVKK Ö-9).</summary>
        public const string ProcessorConfirmation = "processor-confirmation";
```

- [ ] **Adım 4: Sonlandırıcıyı yaz**

`OrderDeck.LicenseServer/Services/Bank/BankConnectionTerminator.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Kaldırmanın sonucu. Yerel silme her durumda yapılmıştır. Obifin tarafı: <see cref="ObifinRemoved"/> kaldırıldı,
/// <see cref="ObifinFailed"/> kaldırılamadı, <see cref="ObifinUnowned"/> Obifin listesinde var ama bu lisansa ait olduğu
/// doğrulanamadı (dokunulmadı), <see cref="SharedObifinUser"/> Obifin kullanıcısı başka lisansta da kayıtlı (Obifin'e hiç
/// gidilmedi), <see cref="ObifinNotAttempted"/> yalnız yerel silme (otomatik hizmet sonu işi; Obifin'e gidilmedi).
/// <see cref="ObifinCopyDeletionId"/>: "Kaldır ve sil"in açtığı, Obifin'den yazılı silme teyidi bekleyen kayıt
/// (<c>ObifinCopyDeletions</c>); yalnız yerel silmede null. <see cref="Accounts"/> silinen hesap satırı;
/// <see cref="AccountsScrubbed"/> yalnız yerel silmede IBAN alanları boşaltılıp sahiplik kanıtı olarak bırakılan onaylı hesap.</summary>
public sealed record BankRemovalResult(IReadOnlyList<long> ObifinRemoved, IReadOnlyList<long> ObifinFailed,
    IReadOnlyList<long> ObifinUnowned, bool ObifinUnreachable, bool SharedObifinUser, bool ObifinNotAttempted,
    int Transactions, int Matches, int Memories, int Gaps, int Accounts, int AccountsScrubbed, int BankConnections,
    Guid? ObifinCopyDeletionId)
{
    public bool NeedsManualObifinAction => ObifinNotAttempted || ObifinUnreachable || SharedObifinUser
        || ObifinFailed.Count > 0 || ObifinUnowned.Count > 0;

    /// <summary>Yöneticinin Obifin'de elle yapacağı iş (admin bildirimi ve hizmet sonu e-postası); kimlik bilgisi içermez. Elle iş
    /// yoksa boş.</summary>
    public string ManualActionText()
    {
        if (ObifinNotAttempted)
            return "Obifin'e gidilmedi (yalnız yerel silme): admin/obifin'de bu lisans için 'Kaldır ve sil'e basın.";
        if (SharedObifinUser)
            return "Bu Obifin kullanıcısı başka bir lisansta da kayıtlı; Obifin'e hiç gidilmedi. Obifin panelinden YALNIZ bu " +
                "lisansın banka bağlantısını elle kaldırın, diğer lisansınkine dokunmayın.";
        if (ObifinUnreachable)
            return "Obifin'e ulaşılamadı: Obifin panelinden bu lisansın banka bağlantılarını elle kaldırın.";
        var parts = new List<string>();
        if (ObifinFailed.Count > 0)
            parts.Add($"kaldırılamayan banka bağlantıları #{string.Join(", #", ObifinFailed)}: Obifin panelinden elle kaldırın");
        if (ObifinUnowned.Count > 0)
            parts.Add($"bu lisansa ait olduğu doğrulanamayan #{string.Join(", #", ObifinUnowned)}: elle kontrol edin, başka " +
                "lisansınsa dokunmayın");
        return parts.Count == 0 ? "" : "Obifin'de " + string.Join("; ", parts) + ".";
    }
}

/// <summary>Hesap reddinin sonucu (KVKK Ö-10): silinen hareket, eşleşme ve IBAN bağı; açılan dekont gap'i.</summary>
public sealed record BankAccountRejectionResult(int Transactions, int Matches, int Memories, int GapsReopened);

/// <summary>
/// Banka bağlantısının durdurulması, yeniden etkinleştirilmesi, kaldırılıp verisinin silinmesi ve hizmet sonunda yalnız yerel
/// verisinin silinmesi (KVKK K-6, Ö-9); Obifin'in yazılı silme teyidinin kaydı; onaysız hesabın reddi (Ö-10).
/// <see cref="BankHasher"/> İSTEMEZ: banka modülü anahtarsız kapalıyken de durdurma ve silme çalışır (silme KVKK yükümlülüğü).
/// <para><b>Kaldır ve sil</b> (<see cref="RemoveAndDeleteAsync"/>, yalnız yöneticinin eylemi — <c>bankaapi/sil</c>'i çağıran
/// TEK yol): önce bağlantı tek SaveChanges'le durdurulur (partiler sürerken çekim ve eşleştirme yazmasın). Obifin tarafı lisansa
/// özgüdür, bkz. <see cref="RemoveFromObifinAsync"/>. Sonra lisansın bütün banka verisi ve Obifin kimliği silinir, imha kaydı
/// aynı işlemde yazılır. Obifin API'sinde hareket silme ucu yok: Obifin'deki kopyanın silinmesi
/// yazılı istenir. Obifin'e ulaşılamazsa ya da kaldırma düşerse yerel silme yine yapılır.</para>
/// <para><b>Yalnız yerel silme</b> (<see cref="PurgeLocalDataAsync"/>, otomatik hizmet sonu işi): Obifin'e gidilmez; banka
/// verisi silinir, "Kaldır ve sil"in sahiplik kanıtı kalır — Obifin kimliği, yerel <c>BankConnections</c> (yalnız
/// <c>BankaApiId</c>) ve ONAYLI hesap satırları (kişisel veri taşıyabilen alanları boşaltılmış; onaysız hesaplar silinir).
/// <c>BankDataPurgedAt</c> yazılır — yönetici Obifin tarafını sonra "Kaldır ve sil" ile kapatır.</para>
/// <para>Yerel silme <see cref="BatchSize"/>'lık partilerle yapılır (saklama işinin kalıbı): lisansın bütün satırları bir
/// kerede yüklenmez, her parti imha kaydını aynı SaveChanges'te yazar; bağlantının kendisi ya da <c>BankDataPurgedAt</c>
/// işareti EN SON. Eşzamanlı bir eşleştirme yazısıyla (jeton) çakışırsa bütün silme bir kez baştan yeniden denenir (her parti
/// sorgusu baştan koşar; sayılar birikir). Günlüğe kimlik bilgisi yazılmaz; yalnız lisans kimliği, BankaApiId, sayılar ve
/// istisna türü.</para>
/// <para><b>Obifin teyidi</b> (<see cref="ConfirmObifinCopyDeletedAsync"/>): "Kaldır ve sil"in açtığı <c>ObifinCopyDeletions</c>
/// kaydı yöneticinin "Teyit alındı"sıyla kapanır, imha kaydı yazılır. <b>Hesap reddi</b> (<see cref="RejectAccountAsync"/>):
/// onay kaldırılır, hesabın verisi partilerle silinir.</para>
/// </summary>
public sealed class BankConnectionTerminator
{
    /// <summary>Tek SaveChanges'te en fazla bu kadar satır (BankDataRetentionJob ile aynı); partiler arasında izleyici boşalır.</summary>
    public const int BatchSize = 500;
    /// <summary>Obifin tarafındaki bekleyen işin (Kaldır ve sil, yazılı silme teyidi) "GECİKTİ" sayıldığı süre (plan karar 21).</summary>
    public static readonly TimeSpan ObifinFollowUpOverdue = TimeSpan.FromDays(14);
    /// <summary>Obifin teyidinin imha kaydındaki hedef.</summary>
    public const string ObifinCopyTarget = "Obifin.HareketKopyasi";
    /// <summary>Yerel silmede sahiplik kanıtı olarak bırakılan onaylı hesabın boşaltılan alanları (imha kaydı hedefi, eylem
    /// <c>nulled</c>).</summary>
    public const string ScrubbedAccountTarget = "BankAccounts.IbanMasked+IbanHash+NotificationNote";

    public const string StoppedByAdminMessage = "Yönetici çekimi durdurdu.";
    public const string LicenseStillEndedMessage = "Lisans sona ermiş ya da iptal edilmiş; bağlantı etkinleştirilemez.";
    public const string NoConnectionMessage = "Bu lisansın Obifin bağlantısı yok.";
    public const string CopyDeletionNotFoundMessage = "Obifin silme teyidi kaydı bulunamadı.";
    public const string AlreadyConfirmedMessage = "Bu Obifin silme teyidi zaten kaydedilmiş.";
    public const string AccountNotFoundMessage = "Hesap bulunamadı.";

    private readonly LicenseDbContext _db;
    private readonly IObifinClient _client;
    private readonly IDataProtectionProvider _protection;
    private readonly ILogger<BankConnectionTerminator> _log;

    public BankConnectionTerminator(LicenseDbContext db, IObifinClient client, IDataProtectionProvider protection,
        ILogger<BankConnectionTerminator> log)
    { _db = db; _client = client; _protection = protection; _log = log; }

    /// <summary>Disabled + DisabledAt (hizmet sonu sayacı) + DisabledByAdmin. Lisans bitişiyle zaten Disabled ise yalnız
    /// DisabledByAdmin yazılır (tarih değişmez): yenilenen lisans yöneticinin durdurduğu bağlantıyı açmasın.</summary>
    public async Task StopAsync(Guid licenseId, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new ObifinValidationException(NoConnectionMessage);
        if (conn.Status == ObifinConnectionStatus.Disabled && conn.DisabledByAdmin) return;
        MarkStopped(conn, DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Yönetici durdurması, kaydetmez (<see cref="StopAsync"/>, <see cref="RemoveAndDeleteAsync"/>): Disabled + DisabledAt
    /// (hizmet sonu sayacı) + LastError; zaten Disabled ise (lisans bitişi) DisabledAt ve LastError değişmez, sayaç baştan
    /// başlamaz. Her durumda DisabledByAdmin ve yeni UpdatedAt: çekimin kimlik koruması yarıdaki turun kaydını düşürür.</summary>
    private static void MarkStopped(ObifinConnection conn, DateTimeOffset now)
    {
        if (conn.Status != ObifinConnectionStatus.Disabled)
        {
            conn.Status = ObifinConnectionStatus.Disabled;
            conn.DisabledAt = now;
            conn.LastError = StoppedByAdminMessage;
        }
        conn.DisabledByAdmin = true;
        conn.UpdatedAt = now;
    }

    /// <summary>Yalnız lisans geçerliyse: Unverified'a döner (yönetici Doğrula'ya basar), hizmet sonu alanları boşalır.</summary>
    public async Task ResumeAsync(Guid licenseId, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new ObifinValidationException(NoConnectionMessage);
        if (conn.Status != ObifinConnectionStatus.Disabled) return;
        var now = DateTimeOffset.UtcNow;
        if (!await ObifinConnectionService.IsLicenseActiveAsync(_db, licenseId, now, ct))
            throw new ObifinValidationException(LicenseStillEndedMessage);
        conn.Status = ObifinConnectionStatus.Unverified;
        ObifinConnectionService.ClearServiceEnd(conn);
        conn.LastError = null;
        conn.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Yöneticinin "Kaldır ve sil"i. ÖNCE bağlantıyı tek SaveChanges'le durdurur (<see cref="MarkStopped"/>): yönetici
    /// sayfası <c>obifin-poll</c> kilidini almaz, partili silme saniyeler sürer. Bağlantı en son adıma kadar Verified kalsaydı arada
    /// koşan çekim turunun kaydı kimlik korumasından geçer (UpdatedAt değişmemiş), sink ve telafi taraması (eşleştirme kapısı açık)
    /// yeni hareket ve eşleşme yazardı; döngüden sonra yazılan satır bağlantısız lisansta kalır, imha kaydına girmezdi. Durdurmayla
    /// yarıdaki turun kaydı düşer, sonraki turlar Disabled bağlantıyı atlar, <see cref="ObifinConnectionService.MatchingActive"/>
    /// eşleştirmeyi kapatır. Silme yarıda düşerse bağlantı Disabled kalır (güvenli yön). Sonra Obifin'de bu lisansa ait banka
    /// bağlantıları kaldırılır (lisansa özgü, <see cref="RemoveFromObifinAsync"/>) ve lisansın bütün banka verisi ile Obifin
    /// kimliği silinir.</summary>
    public async Task<BankRemovalResult> RemoveAndDeleteAsync(Guid licenseId, string trigger, string actor, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new ObifinValidationException(NoConnectionMessage);
        MarkStopped(conn, DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();   // conn bundan sonra yalnız okunur (kimlik, lisans)
        var obifin = await RemoveFromObifinAsync(conn, ct);
        return await DeleteLocalWithRetryAsync(licenseId, trigger, actor, keepConnection: false, obifin, ct);
    }

    /// <summary>Otomatik hizmet sonu silmesi (BankServiceEndPurgeJob): Obifin'e GİDİLMEZ. Banka verisi silinir; Obifin kimliği,
    /// yerel <c>BankConnections</c> ve onaylı hesap satırları (IBAN alanları boş) kalır, <c>BankDataPurgedAt</c> yazılır.</summary>
    public async Task<BankRemovalResult> PurgeLocalDataAsync(Guid licenseId, string trigger, string actor, CancellationToken ct)
    {
        if (!await _db.ObifinConnections.AnyAsync(c => c.LicenseId == licenseId, ct))
            throw new ObifinValidationException(NoConnectionMessage);
        return await DeleteLocalWithRetryAsync(licenseId, trigger, actor, keepConnection: true, ObifinOutcome.Skipped, ct);
    }

    private sealed record ObifinOutcome(List<long> Removed, List<long> Failed, List<long> Unowned, bool Unreachable, bool Shared,
        bool NotAttempted)
    {
        public static ObifinOutcome Skipped => new([], [], [], false, false, true);
        public static ObifinOutcome Unreached => new([], [], [], true, false, false);
    }

    /// <summary>
    /// Obifin tarafı, LİSANSA ÖZGÜ (Faz 2 spec §13: tek paylaşılan Obifin kullanıcısı olabilir; <c>ObifinConnection</c>'da yalnız
    /// <c>LicenseId</c> tekil):
    /// <list type="number">
    /// <item>Aynı (BaseUrl, UserCode) başka bir lisansın bağlantısında da varsa Obifin'e HİÇ gidilmez: o kullanıcının altındaki
    /// bağlantıların hangisinin kime ait olduğu buradan güvenle bilinemez.</item>
    /// <item>Yalnız bu lisansın yerel <c>BankConnections</c>'ındaki ve ONAYLI hesaplarının (Görev 12; Obifin hesap listesindeki
    /// <c>BankaApiId</c>) bağlantıları kaldırılır. Obifin panelinden eklenmiş bağlantı yalnız onaylı hesaptan bilinir; hizmet
    /// sonu yerel silmesi bu yüzden onaylı hesap satırını bırakır (<see cref="DeleteLocalAsync"/>), tanıma o silmeden sonra da
    /// çalışır.</item>
    /// <item>Obifin listesindeki diğer bağlantılara dokunulmaz; "elle kontrol" diye raporlanır.</item>
    /// </list>
    /// Kimlik çözülemez ya da iki listeden biri alınamazsa hiçbir şey kaldırılmaz (ulaşılamadı).
    /// </summary>
    private async Task<ObifinOutcome> RemoveFromObifinAsync(ObifinConnection conn, CancellationToken ct)
    {
        var licenseId = conn.LicenseId;
        var sameUserCode = await _db.ObifinConnections.AsNoTracking()
            .Where(c => c.LicenseId != licenseId && c.UserCode == conn.UserCode).Select(c => c.BaseUrl).ToListAsync(ct);
        if (sameUserCode.Any(b => string.Equals(b.TrimEnd('/'), conn.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            _log.LogWarning("Obifin kaldırma: Obifin kullanıcısı başka lisansta da kayıtlı, Obifin'e gidilmedi — lisans={LicenseId}",
                licenseId);
            return new ObifinOutcome([], [], [], Unreachable: false, Shared: true, NotAttempted: false);
        }
        var creds = ObifinConnectionService.ResolveCredentials(_protection, conn);
        if (creds is null)
        {
            _log.LogWarning("Obifin kaldırma: saklı kimlik çözülemedi — lisans={LicenseId}; Obifin'de elle kaldırılmalı", licenseId);
            return ObifinOutcome.Unreached;
        }

        var owned = (await _db.BankConnections.AsNoTracking().Where(b => b.LicenseId == licenseId)
            .Select(b => b.BankaApiId).ToListAsync(ct)).ToHashSet();
        var approvedAccounts = (await _db.BankAccounts.AsNoTracking().Where(a => a.LicenseId == licenseId && a.ApprovedAt != null)
            .Select(a => a.ObifinAccountId).ToListAsync(ct)).ToHashSet();
        IReadOnlyList<ObifinBankConnectionDto> list;
        try
        {
            list = await _client.ListBankConnectionsAsync(creds, ct);
            if (approvedAccounts.Count > 0)
                foreach (var a in await _client.ListAccountsAsync(creds, ct))
                    if (approvedAccounts.Contains(a.Id) && a.BankaApiId is { } apiId) owned.Add(apiId);
        }
        catch (Exception ex) when (ObifinConnectionService.DescribeClientFailure(ex, ct) is not null)
        {
            _log.LogWarning("Obifin kaldırma: banka bağlantı ya da hesap listesi alınamadı — lisans={LicenseId}: {Type}",
                licenseId, ex.GetType().Name);
            return ObifinOutcome.Unreached;
        }

        var removed = new List<long>();
        var failed = new List<long>();
        var unowned = new List<long>();
        foreach (var bc in list)
        {
            if (!owned.Contains(bc.BankaApiId))
            {
                unowned.Add(bc.BankaApiId);
                continue;
            }
            try
            {
                await _client.RemoveBankConnectionAsync(creds, bc.BankaApiId, ct);
                removed.Add(bc.BankaApiId);
            }
            catch (Exception ex) when (ObifinConnectionService.DescribeClientFailure(ex, ct) is not null)
            {
                failed.Add(bc.BankaApiId);
                _log.LogWarning("Obifin kaldırma: banka bağlantısı #{BankaApiId} kaldırılamadı — lisans={LicenseId}: {Type}",
                    bc.BankaApiId, licenseId, ex.GetType().Name);
            }
        }
        if (unowned.Count > 0)
            _log.LogWarning("Obifin kaldırma: {Count} banka bağlantısının bu lisansa ait olduğu doğrulanamadı, dokunulmadı — " +
                "lisans={LicenseId}", unowned.Count, licenseId);
        return new ObifinOutcome(removed, failed, unowned, Unreachable: false, Shared: false, NotAttempted: false);
    }

    /// <summary>Silinen satır sayıları: partiler ayrı kaydedildiği için yeniden denemede birikir.</summary>
    private sealed class Tally
    {
        public int Transactions, Matches, Memories, Gaps, Accounts, AccountsScrubbed, BankConnections;
        public Guid? CopyDeletionId;
    }

    private async Task<BankRemovalResult> DeleteLocalWithRetryAsync(Guid licenseId, string trigger, string actor,
        bool keepConnection, ObifinOutcome obifin, CancellationToken ct)
    {
        var tally = new Tally();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DeleteLocalAsync(licenseId, trigger, actor, keepConnection, tally, ct);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                // Arada bir eşleştirme yazısı jetonu ilerletti. Kaydedilmiş partiler gitti (sayıları tally'de); kalan satırlar taze
                // okunup bir kez yeniden silinir — her parti sorgusu baştan koşar, yeniden deneme idempotent.
                _db.ChangeTracker.Clear();
            }
        }
        _log.LogWarning("Banka verisi silindi — lisans={LicenseId}, tetikleyici={Trigger}, yalnız yerel={Local}: hareket={Transactions}, " +
            "eşleşme={Matches}, IBAN bağı={Memories}, silinen hesap={Accounts}, boşaltılan onaylı hesap={Scrubbed}; Obifin'de " +
            "kaldırılan={Removed}, kaldırılamayan={Failed}, sahibi doğrulanamayan={Unowned}, ulaşılamadı={Unreachable}, paylaşılan " +
            "kullanıcı={Shared}",
            licenseId, trigger, keepConnection, tally.Transactions, tally.Matches, tally.Memories, tally.Accounts,
            tally.AccountsScrubbed, obifin.Removed.Count, obifin.Failed.Count, obifin.Unowned.Count, obifin.Unreachable, obifin.Shared);
        return new BankRemovalResult(obifin.Removed, obifin.Failed, obifin.Unowned, obifin.Unreachable, obifin.Shared,
            obifin.NotAttempted, tally.Transactions, tally.Matches, tally.Memories, tally.Gaps, tally.Accounts,
            tally.AccountsScrubbed, tally.BankConnections, tally.CopyDeletionId);
    }

    /// <summary>Lisansın banka verisini <see cref="BatchSize"/>'lık partilerle siler: her parti imha kaydını AYNI SaveChanges'te
    /// yazar, partiler arasında izleyici boşalır. Bağlantının kendisi (kaldırma) ya da <c>BankDataPurgedAt</c> işareti ile onaylı
    /// hesapların boşaltılması (yerel silme) ve "Kaldır ve sil"in Obifin teyit kaydı EN SON, tek SaveChanges'te: önceki bir parti
    /// düşerse bağlantı ve işaret kalır, iş ertesi gün (ya da yönetici yeniden basınca) kalanı siler.
    /// <paramref name="keepConnection"/>: "Kaldır ve sil"in sahiplik kanıtı kalır — Obifin kimliği, yerel BankConnections ve onaylı
    /// hesap satırları (kişisel veri taşıyabilen alanları son adımda boşalır); onaysız hesaplar silinir, BankDataPurgedAt
    /// yazılır.</summary>
    private async Task DeleteLocalAsync(Guid licenseId, string trigger, string actor, bool keepConnection, Tally tally,
        CancellationToken ct)
    {
        var criterion = keepConnection ? "hizmet sonu + 30 gün (yalnız yerel)" : "bağlantı kaldırıldı";
        void Record(string target, int count) => DataDestruction.Record(_db, licenseId, target, DataDestruction.Actions.Deleted,
            count, criterion, trigger, actor);

        // Hareketler eşleşme satırlarıyla birlikte (eşleşme harekete bağlı).
        while (true)
        {
            var batch = await _db.BankTransactions.Where(t => t.LicenseId == licenseId).OrderBy(t => t.Id).Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            var ids = batch.Select(t => t.Id).ToList();
            var matches = await _db.PaymentMatches.Where(m => ids.Contains(m.BankTransactionId)).ToListAsync(ct);
            _db.PaymentMatches.RemoveRange(matches);
            _db.BankTransactions.RemoveRange(batch);
            Record("PaymentMatches", matches.Count);
            Record("BankTransactions", batch.Count);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            tally.Matches += matches.Count;
            tally.Transactions += batch.Count;
            if (batch.Count < BatchSize) break;
        }
        tally.Memories += await DeleteBatchesAsync(
            _db.CustomerIbanMemories.Where(m => m.LicenseId == licenseId).OrderBy(m => m.Id), "CustomerIbanMemories", Record, ct);
        tally.Gaps += await DeleteBatchesAsync(
            _db.PaymentMatchGaps.Where(g => g.LicenseId == licenseId).OrderBy(g => g.Id), "PaymentMatchGaps", Record, ct);
        // Yerel silmede ONAYLI hesap satırı kalır (son adımda boşalır): "Kaldır ve sil" Obifin panelinden eklenmiş, yerelde
        // BankConnections satırı olmayan bağlantının bu lisansa ait olduğunu yalnız onaylı hesaptan bilir (RemoveFromObifinAsync).
        // Silinseydi o bağlantı "sahibi doğrulanamadı" diye Obifin'de kalır, Obifin hizmet bittiği halde bankadan çekmeyi
        // sürdürürdü (K-6). Onaysız hesap kanıt değildir, silinir.
        var accounts = _db.BankAccounts.Where(a => a.LicenseId == licenseId);
        if (keepConnection) accounts = accounts.Where(a => a.ApprovedAt == null);
        tally.Accounts += await DeleteBatchesAsync(accounts.OrderBy(a => a.Id), "BankAccounts", Record, ct);
        if (!keepConnection)
            tally.BankConnections += await DeleteBatchesAsync(
                _db.BankConnections.Where(b => b.LicenseId == licenseId).OrderBy(b => b.Id), "BankConnections", Record, ct);

        // Son adım, tek SaveChanges: bağlantı (ya da işaret ve onaylı hesapların boşaltılması) ve Obifin teyit kaydı. Görev 17
        // açık talepleri de burada kapatır.
        var now = DateTimeOffset.UtcNow;
        var scrubbed = 0;
        var connections = await _db.ObifinConnections.Where(c => c.LicenseId == licenseId).ToListAsync(ct);
        if (keepConnection)
        {
            foreach (var c in connections) { c.BankDataPurgedAt = now; c.UpdatedAt = now; }
            // Onaylı hesap: kimlik (ObifinAccountId, ApprovedAt, ApprovedBy) kalır, kişisel veri taşıyabilen alanlar boşalır.
            // Lisansın onaylı hesabı birkaç tanedir, parti gerekmez. Kalan satırı "Kaldır ve sil" siler; lisans yenilenip
            // bağlantı yeniden doğrulanırsa hesap yenilemesi alanları yeniden doldurur, onay geçerli kalır.
            var approved = await _db.BankAccounts.Where(a => a.LicenseId == licenseId && a.ApprovedAt != null
                && (a.IbanMasked != "" || a.IbanHash != null || a.NotificationNote != null)).ToListAsync(ct);
            foreach (var a in approved) { a.IbanMasked = ""; a.IbanHash = null; a.NotificationNote = null; }
            DataDestruction.Record(_db, licenseId, ScrubbedAccountTarget, DataDestruction.Actions.Nulled, approved.Count,
                criterion, trigger, actor);
            scrubbed = approved.Count;
        }
        else
        {
            _db.ObifinConnections.RemoveRange(connections);
            Record("ObifinConnections", connections.Count);
            // KVKK Ö-9 öneri 3: Obifin'deki kopyanın yazılı silme teyidi bağlantı satırından bağımsız izlenir (FK yok, budanmaz).
            // Hatırlatma: BankServiceEndPurgeJob; kapatma: ConfirmObifinCopyDeletedAsync.
            var copy = new ObifinCopyDeletion { Id = Guid.NewGuid(), LicenseId = licenseId, RequestedAt = now, RequestedBy = actor };
            _db.ObifinCopyDeletions.Add(copy);
            tally.CopyDeletionId = copy.Id;
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        tally.AccountsScrubbed += scrubbed;   // kayıttan sonra: yeniden denemede iki kez sayılmasın
    }

    /// <summary>Sıralı sorgunun seçtiği satırları <see cref="BatchSize"/>'lık partilerle siler, her partinin imha kaydını aynı
    /// SaveChanges'te yazar. Silinen satır sorgudan düşer: her parti baştan sorgulanır.</summary>
    private async Task<int> DeleteBatchesAsync<T>(IQueryable<T> rows, string target, Action<string, int> record,
        CancellationToken ct) where T : class
    {
        var total = 0;
        while (true)
        {
            var batch = await rows.Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            _db.RemoveRange(batch);
            record(target, batch.Count);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize) return total;
        }
    }

    /// <summary>Obifin'in hareket kopyasını sildiğine dair yazılı teyit alındı (KVKK Ö-9 öneri 3; plan karar 21): kayıt kapanır,
    /// imha kaydı (<see cref="ObifinCopyTarget"/>, tetikleyici <c>processor-confirmation</c>) aynı SaveChanges'te yazılır. Teyit
    /// yazısının kendisi sistemde saklanmaz (repo dışı KVKK klasörü). Kişisel veri yok. Kaydın lisansını döner (denetim).</summary>
    public async Task<Guid> ConfirmObifinCopyDeletedAsync(Guid copyDeletionId, string actor, CancellationToken ct)
    {
        var row = await _db.ObifinCopyDeletions.FirstOrDefaultAsync(d => d.Id == copyDeletionId, ct)
            ?? throw new ObifinValidationException(CopyDeletionNotFoundMessage);
        if (row.ConfirmedAt is not null) throw new ObifinValidationException(AlreadyConfirmedMessage);
        row.ConfirmedAt = DateTimeOffset.UtcNow;
        row.ConfirmedBy = actor;
        DataDestruction.Record(_db, row.LicenseId, ObifinCopyTarget, DataDestruction.Actions.Deleted, 1,
            "Obifin yazılı silme teyidi", DataDestruction.Triggers.ProcessorConfirmation, actor);
        await _db.SaveChangesAsync(ct);
        return row.LicenseId;
    }

    /// <summary>
    /// KVKK Ö-10: hesabı reddeder ya da onayını geri alır ve o hesabın bu lisanstaki hareketlerini, eşleşmelerini ve bu
    /// hareketlerden öğrenilmiş IBAN bağlarını (<c>SourceBankTransactionId</c>) <see cref="BatchSize"/>'lık partilerle siler; imha
    /// kaydı tetikleyici <c>account-rejected</c>. Onay EN ÖNCE kaldırılır: arada koşan çekim turu o hesabın yeni hareketini
    /// yazmaz (yazarsa yeniden basmak yeter, idempotent). Dekont bağı silinen ödemenin gap'i açılır (<see cref="ReopenGapsAsync"/>).
    /// Hesap satırı kalır: yer tutucudur, Obifin'den geldikçe onaysız sayılır. Eşzamanlı eşleştirme yazısıyla çakışırsa bir kez
    /// yeniden denenir. <see cref="BankHasher"/> istemez.
    /// </summary>
    public async Task<BankAccountRejectionResult> RejectAccountAsync(Guid accountId, string actor, CancellationToken ct)
    {
        var account = await _db.BankAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new ObifinValidationException(AccountNotFoundMessage);
        var (licenseId, obifinAccountId) = (account.LicenseId, account.ObifinAccountId);
        if (account.ApprovedAt is not null)
        {
            account.ApprovedAt = null;
            account.ApprovedBy = null;
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();

        const string criterion = "onaysız ya da reddedilen hesap (Ö-10)";
        void Record(string target, int count) => DataDestruction.Record(_db, licenseId, target, DataDestruction.Actions.Deleted,
            count, criterion, DataDestruction.Triggers.AccountRejected, actor);
        int transactions = 0, matches = 0, memories = 0, gaps = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                while (true)
                {
                    var batch = await _db.BankTransactions
                        .Where(t => t.LicenseId == licenseId && t.ObifinAccountId == obifinAccountId)
                        .OrderBy(t => t.Id).Take(BatchSize).ToListAsync(ct);
                    if (batch.Count == 0) break;
                    var ids = batch.Select(t => t.Id).ToList();
                    var batchMatches = await _db.PaymentMatches.Where(m => ids.Contains(m.BankTransactionId)).ToListAsync(ct);
                    var batchMemories = await _db.CustomerIbanMemories
                        .Where(m => m.LicenseId == licenseId && m.SourceBankTransactionId != null
                            && ids.Contains(m.SourceBankTransactionId.Value))
                        .ToListAsync(ct);
                    var reopened = await ReopenGapsAsync(licenseId, batchMatches, ct);
                    _db.PaymentMatches.RemoveRange(batchMatches);
                    _db.CustomerIbanMemories.RemoveRange(batchMemories);
                    _db.BankTransactions.RemoveRange(batch);
                    Record("PaymentMatches", batchMatches.Count);
                    Record("CustomerIbanMemories", batchMemories.Count);
                    Record("BankTransactions", batch.Count);
                    await _db.SaveChangesAsync(ct);
                    _db.ChangeTracker.Clear();
                    transactions += batch.Count; matches += batchMatches.Count; memories += batchMemories.Count; gaps += reopened;
                    if (batch.Count < BatchSize) break;
                }
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                _db.ChangeTracker.Clear();
            }
        }
        _log.LogWarning("Banka hesabı reddedildi, verisi silindi — lisans={LicenseId}, hesap={ObifinAccountId}: hareket={Transactions}, " +
            "eşleşme={Matches}, IBAN bağı={Memories}, açılan gap={Gaps}", licenseId, obifinAccountId, transactions, matches, memories, gaps);
        return new BankAccountRejectionResult(transactions, matches, memories, gaps);
    }

    /// <summary>Dekont bağı silinecek eşleşmelerin ödemelerine açık gap (bağdaştırıcının kaldırma kuralı: dekont ölçümden
    /// düşmesin): gap'i varsa yeniden açılır, yoksa <see cref="PaymentMatchGapReason.UnlinkedByAdmin"/> yazılır. İzleyiciye ekler;
    /// partinin SaveChanges'i yazar. Açılan ya da yazılan gap sayısını döner.</summary>
    private async Task<int> ReopenGapsAsync(Guid licenseId, List<PaymentMatch> matches, CancellationToken ct)
    {
        var payments = matches.Where(m => m.PaymentId != null).Select(m => m.PaymentId!.Value).Distinct().ToList();
        if (payments.Count == 0) return 0;
        var existing = await _db.PaymentMatchGaps.Where(g => payments.Contains(g.PaymentId)).ToListAsync(ct);
        foreach (var g in existing) { g.ResolvedAt = null; g.ResolvedBankTransactionId = null; }
        var now = DateTimeOffset.UtcNow;
        foreach (var paymentId in payments.Except(existing.Select(g => g.PaymentId)))
            _db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = licenseId, PaymentId = paymentId,
                Reason = PaymentMatchGapReason.UnlinkedByAdmin, CreatedAt = now });
        return payments.Count;
    }
}
```

- [ ] **Adım 5: Koştur — servis yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankConnectionTerminatorTests|FullyQualifiedName~ObifinConnectionService"`
Expected: PASS.

- [ ] **Adım 5b: Durdurulmuş bağlantıda eşleştirme de durur (test önce, kırmızı)**

`PaymentMatchReconcileJobTests.cs` — `SeedAsync`'in imzasını `SeedAsync(PaymentStatus status, bool connected = true, ObifinConnectionStatus connectionStatus = ObifinConnectionStatus.Verified, DateTimeOffset? bankDataPurgedAt = null)` yap. Bağlantının başlatıcısında `Status = ObifinConnectionStatus.Verified,` → `Status = connectionStatus, BankDataPurgedAt = bankDataPurgedAt,`. Hareketin başlatıcısına `CounterpartyIbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", CounterpartyIbanHashValid = true,` ekle. Böylece onay bağdaştırması IBAN da öğretebilir ve aşağıdaki IBAN iddiası boş geçmez; diğer testler hafızaya bakmıyor. Sınıfın sonuna:

```csharp
    [Theory]
    [InlineData(ObifinConnectionStatus.Disabled, false)]
    [InlineData(ObifinConnectionStatus.Unverified, true)]
    public async Task Durdurulmus_ya_da_verisi_silinmis_baglantida_dekont_onayi_eslesme_gap_ve_IBAN_bagi_yazmaz(
        ObifinConnectionStatus status, bool purged)
    {
        // KVKK K-6: durdurma ve lisans bitişi yalnız çekimi değil işlemeyi de durdurur. 30 günlük beklemede saklı hareket
        // bağdaştırılmaz, IBAN öğrenilmez; yerel silmeden sonra her onay yeni bir gap yazmaz. İki koşul ayrı ayrı sınanır
        // (Unverified + işaret: elle düzeltilmiş satır).
        var s = await SeedAsync(PaymentStatus.Approved, connectionStatus: status,
            bankDataPurgedAt: purged ? DateTimeOffset.UtcNow.AddDays(-1) : null);
        if (purged)
        {
            // Yerel silme hareketleri ve eşleşmeleri götürdü; bağlantı satırı duruyor.
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.PaymentMatches.RemoveRange(db.PaymentMatches.Where(m => m.LicenseId == s.LicenseId));
            db.BankTransactions.RemoveRange(db.BankTransactions.Where(t => t.LicenseId == s.LicenseId));
            await db.SaveChangesAsync();
        }

        await RunAsync(s.PaymentId);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.PaymentMatchGaps.CountAsync(g => g.PaymentId == s.PaymentId)).Should().Be(0);
        (await vdb.PaymentMatches.AnyAsync(m => m.PaymentId == s.PaymentId)).Should().BeFalse();
        (await vdb.CustomerIbanMemories.CountAsync(m => m.LicenseId == s.LicenseId)).Should().Be(0);
    }
```

`BankMatchSweepJobTests.cs` sınıfının sonuna:

```csharp
    [Theory]
    [InlineData(ObifinConnectionStatus.Disabled, false)]
    [InlineData(ObifinConnectionStatus.Unverified, true)]
    public async Task Durdurulmus_ya_da_verisi_silinmis_baglantinin_hareketi_taranmaz(ObifinConnectionStatus status, bool purged)
    {
        // KVKK K-6: telafi taraması durdurulmuş bağlantının saklı hareketini eşleştirmez (öneri, gap çözümü).
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var conn = db.ObifinConnections.Local.Single(c => c.LicenseId == lic);
        conn.Status = status;
        if (purged) conn.BankDataPurgedAt = DateTimeOffset.UtcNow.AddDays(-1);
        db.WpfCustomerProjections.Add(new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube",
            Username = "ornek_musteri34", UpdatedAt = DateTimeOffset.UtcNow });
        db.BankTransactions.Add(Tx(lic, "HAVALE ornek_musteri34", TimeSpan.FromDays(2)));
        await db.SaveChangesAsync();

        var swept = await Job(db).RunAsync(CancellationToken.None);

        swept.Should().Be(0);
        (await db.PaymentMatches.CountAsync()).Should().Be(0);
    }
```

`PaymentMatchReconcilerTests.cs` sınıfının sonuna:

```csharp
    [Fact]
    public async Task Durdurulmus_baglantida_gap_cozulmez_onay_bagdastirilmaz_elle_esleme_reddedilir()
    {
        // KVKK K-6: durdurulmuş bağlantının saklı hareketi 30 günlük beklemede işlenmez: gap çözümü dekontu bağlamaz, yeni onay gap
        // yazmaz, elle eşleme IBAN öğretmez ve LastConfirmedAt'i tazelemez. Bağlantısı hiç olmayan lisans bugünkü gibi (ilk onay).
        using var db = NewDb(); var s = SeedShopper(db);
        var paidAt = DateTimeOffset.UtcNow.AddHours(-5);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 750m, paidAt);
        var recon = Recon(db);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);   // bağlantı yok: NoCandidate gap'i (bugünkü davranış)
        db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = s.LicenseId, BaseUrl = "https://obifin.invalid",
            UserCode = $"u-{Guid.NewGuid():N}", Status = ObifinConnectionStatus.Disabled, DisabledAt = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        var late = Tx(db, s.LicenseId, 750m, paidAt.AddHours(4), "HAVALE ornek_musteri34", Hasher.HashIban(BankHasherTests.TestIban()));
        late.CounterpartyIbanHashValid = true;
        db.SaveChanges();
        var second = Approved(db, s.LicenseId, s.ShopperId, 120m, DateTimeOffset.UtcNow.AddHours(-1));

        await recon.TryResolveGapAsync(late, CancellationToken.None);
        await recon.ReconcileApprovalAsync(second, CancellationToken.None);
        var manual = () => recon.ManualMatchAsync(s.LicenseId, late.Id, s.WpfCustomerId, CancellationToken.None);

        await manual.Should().ThrowAsync<ObifinValidationException>().WithMessage(PaymentMatchReconciler.MatchingStoppedMessage);
        (await db.PaymentMatchGaps.SingleAsync()).ResolvedAt.Should().BeNull("gap çözülmedi");
        (await db.PaymentMatchGaps.AnyAsync(g => g.PaymentId == second.Id)).Should().BeFalse("durdurulmuş bağlantıda yeni gap yok");
        (await db.PaymentMatches.AnyAsync(m => m.PaymentId != null)).Should().BeFalse();
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
    }
```

(`CounterpartyIbanHashValid` Görev 11'den; `ObifinConnection` için dosyanın üstünde `using OrderDeck.LicenseServer.Domain.Bank;` var.)

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PaymentMatchReconcileJobTests|FullyQualifiedName~BankMatchSweepJobTests|FullyQualifiedName~PaymentMatchReconcilerTests"`
Expected: FAIL — `MatchingStoppedMessage` yok (derleme). Satırı geçici olarak yorumlayıp koşarsan üç yeni test de kırmızıdır: eşleşme, gap ve IBAN bağı yazılıyor.

`Services/Bank/PaymentMatchReconcileJob.cs` — `if (!await _db.ObifinConnections.AnyAsync(c => c.LicenseId == payment.LicenseId, ct)) return;` satırını şununla değiştir:

```csharp
        // KVKK K-6: durdurulmuş ya da yerel verisi silinmiş bağlantının lisansında dekont bağdaştırılmaz
        // (ObifinConnectionService.MatchingActive). Bağlantısız lisansta da koşulmaz: banka hareketi yoktur, her onay anlamsız bir
        // NoCandidate gap'i yazıp ölçümü kirletirdi.
        if (await ObifinConnectionService.MatchingStateAsync(_db, payment.LicenseId, ct) != true) return;
```

Sınıf özetindeki "Yalnız hâlâ Approved olan ödeme ve Obifin bağlantısı olan lisans işlenir" → "Yalnız hâlâ Approved olan ödeme ve eşleştirmeye açık Obifin bağlantısı (<see cref="ObifinConnectionService.MatchingActive"/>) olan lisans işlenir"; paragrafın sonuna: "Durdurulmuş ya da yerel verisi silinmiş bağlantıda saklı hareket işlenmez (KVKK K-6); verisi silinmiş bağlantıda her onay yeni bir gap yazardı."

`Services/Bank/BankMatchSweepJob.cs` — sorgudan önce `var matchingLicenses = _db.ObifinConnections.Where(ObifinConnectionService.MatchingActive).Select(c => c.LicenseId);` (alt sorgu yerel değişkende: `DisableEndedLicensesAsync`'teki `activeLicenseIds` kalıbı — ifade özelliği lambda'nın içinde çağrılırsa EF onu çeviremeyebilir), sorgudaki `_db.ObifinConnections.Select(c => c.LicenseId).Contains(t.LicenseId)` → `matchingLicenses.Contains(t.LicenseId)`. Sınıf özetinde "Obifin bağlantısı olan lisansların" → "eşleştirmeye açık Obifin bağlantısı (<see cref="ObifinConnectionService.MatchingActive"/>; KVKK K-6) olan lisansların"; lisans süzgeci paragrafındaki "(bağlantısız lisansın hareketi de gelmez)" → "(bağlantısız, durdurulmuş ya da yerel verisi silinmiş lisansın hareketi de gelmez)".

`Services/Bank/PaymentMatchReconciler.cs`:
- `AlreadyDecidedMessage`'ın ALTINA:

```csharp
    /// <summary>Durdurulmuş ya da yerel verisi silinmiş bağlantıda elle eşleme (KVKK K-6).</summary>
    public const string MatchingStoppedMessage =
        "Bu lisansın banka bağlantısı durdurulmuş; durdurulmuş bağlantının verisi eşleştirilmez (KVKK). Önce Etkinleştir.";
```

- `ReconcileApprovalOnceAsync`'in ilk satırının ÜSTÜNE: `if (await ObifinConnectionService.MatchingStateAsync(_db, payment.LicenseId, ct) == false) return;`
- `TryResolveGapOnceAsync`'te `if (Matcher.IsExcluded(tx)) return;` satırının ALTINA: `if (await ObifinConnectionService.MatchingStateAsync(_db, tx.LicenseId, ct) == false) return;`
- `ManualMatchOnceAsync`'in ilk satırının ÜSTÜNE: `if (await ObifinConnectionService.MatchingStateAsync(_db, licenseId, ct) == false) throw new ObifinValidationException(MatchingStoppedMessage);` (`RetryOnceAsync` bu istisnayı yeniden denemeden yukarı verir; BankaEslestirme sayfası `ObifinValidationException`'ı hata şeridine basar.)
- Sınıf özetine paragraf: `/// <para><b>Durdurulmuş bağlantı (KVKK K-6).</b> Lisansın bağlantısı eşleştirmeye kapalıysa (<see cref="ObifinConnectionService.MatchingActive"/> yanlış: Disabled ya da yerel verisi silinmiş) onay bağdaştırması ve gap çözümü hiçbir şey yazmaz, elle eşleme <see cref="MatchingStoppedMessage"/> fırlatır: saklı hareket işlenmez, IBAN öğrenilmez, LastConfirmedAt tazelenmez, verisi silinmiş bağlantıda gap birikmez. Bağlantısı hiç olmayan lisans bugünkü gibi. Kaldırma açık kalır (bağ siler).</para>`

Run: aynı komut. Expected: PASS.

- [ ] **Adım 6: Yönetim sayfası testlerini yaz (kırmızı)**

`AdminObifinPageTests.cs` — `PageObifin.RemoveBankConnectionAsync`'i şununla değiştir (ve sınıf özetindeki "Kaldırma/hareket çağrısı beklenmez" → "Hareket çağrısı beklenmez; kaldırma kaydedilir"):

```csharp
        private readonly List<long> _removed = new();
        /// <summary>Kaldırılan banka bağlantıları (BankaApiId).</summary>
        public List<long> Removed { get { lock (_removed) return _removed.ToList(); } }
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default)
        {
            lock (_removed) _removed.Add(id);
            return Task.CompletedTask;
        }
```

Sınıfın sonuna:

```csharp
    [Fact]
    public async Task Durdur_ve_etkinlestir_modul_kapaliyken_de_calisir()
    {
        using var factory = new DisabledBankApiFactory();
        var lic = await SeedLicenseAsync(factory);
        await SeedRawConnectionAsync(factory, lic);
        var client = await factory.CreateLoggedInAdminClientAsync();
        var fields = () => new Dictionary<string, string> { ["LicenseId"] = lic.ToString() };

        (await PostAsync(client, "Stop", fields())).StatusCode.Should().Be(HttpStatusCode.Redirect);
        using (var scope = factory.Services.CreateScope())
        {
            var conn = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().ObifinConnections.AsNoTracking()
                .SingleAsync(c => c.LicenseId == lic);
            conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
            conn.DisabledAt.Should().NotBeNull();
        }
        async Task<string> StatusCellAsync()
            => (await ParseAsync(await client.GetStringAsync("/admin/obifin"))).QuerySelector("[data-cell='status']")!.TextContent;
        (await StatusCellAsync()).Should().Contain("silinecek");

        (await PostAsync(client, "Resume", fields())).StatusCode.Should().Be(HttpStatusCode.Redirect);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic)).Status.Should().Be(ObifinConnectionStatus.Unverified);
            (await db.AuditLogs.CountAsync(a => a.TargetId == lic.ToString()
                && (a.EventType == AuditEvents.ObifinConnectionStop || a.EventType == AuditEvents.ObifinConnectionResume))).Should().Be(2);
        }
        (await StatusCellAsync()).Should().NotContain("silinecek", "tarih yalnız Disabled iken gösterilir");
    }

    [Fact]
    public async Task Kaldir_lisans_anahtari_yazilmadan_bir_sey_yapmaz_yazilinca_siler_teyit_bekleyenlere_ekler()
    {
        // Purge'ün confirmEmail kalıbı: geri dönüşsüz işlem yalnız JS confirm()'una kalmasın. Obifin'in yazılı silme teyidi
        // bağlantı silindikten sonra da izlenir (KVKK Ö-9 öneri 3).
        var lic = await SeedLicenseAsync();
        await SeedRawConnectionAsync(_factory, lic);   // sahte korunan alanlar: kimlik çözülemez → Obifin'e gidilmez
        await SeedShadowDataAsync(lic);
        var client = await _factory.CreateLoggedInAdminClientAsync();
        string key;
        using (var scope = _factory.Services.CreateScope())
            key = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().Licenses.Where(l => l.Id == lic)
                .Select(l => l.LicenseKey).SingleAsync();

        await PostAsync(client, "Remove", new Dictionary<string, string> { ["LicenseId"] = lic.ToString() });
        await PostAsync(client, "Remove", new Dictionary<string, string> { ["LicenseId"] = lic.ToString(), ["ConfirmLicenseKey"] = "XXXXXX" });
        (await ShadowCountsAsync(lic)).Should().Be((1, 1, 1), "lisans anahtarı yazılmadan hiçbir şey silinmez");
        (await ParseAsync(await client.GetStringAsync("/admin/obifin"))).QuerySelector(".alert-danger.alert-dismissible")!
            .TextContent.Should().Contain(ObifinAdminPage.RemoveConfirmMismatchMessage);

        await PostAsync(client, "Remove", new Dictionary<string, string>
        {
            ["LicenseId"] = lic.ToString(), ["ConfirmLicenseKey"] = key[^6..].ToUpperInvariant(),
        });

        (await ShadowCountsAsync(lic)).Should().Be((0, 0, 0));
        var doc = await ParseAsync(await client.GetStringAsync("/admin/obifin"));
        doc.QuerySelector(".alert-danger.alert-dismissible")!.TextContent
            .Should().Contain("elle kaldırın").And.Contain(ObifinAdminPage.ObifinCopyWarning);
        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.ObifinConnections.AnyAsync(c => c.LicenseId == lic)).Should().BeFalse();
        (await db.AuditLogs.AnyAsync(a => a.EventType == AuditEvents.ObifinConnectionRemove && a.TargetId == lic.ToString())).Should().BeTrue();
        var deletion = await db.ObifinCopyDeletions.AsNoTracking().SingleAsync(d => d.LicenseId == lic);
        doc.QuerySelector($"[data-table='obifin-copy-pending'] tr[data-deletion='{deletion.Id}']").Should().NotBeNull();

        (await PostAsync(client, "ConfirmObifinCopyDeleted", new Dictionary<string, string> { ["DeletionId"] = deletion.Id.ToString() }))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await db.ObifinCopyDeletions.AsNoTracking().SingleAsync(d => d.Id == deletion.Id)).ConfirmedAt.Should().NotBeNull();
        (await db.AuditLogs.AnyAsync(a => a.EventType == AuditEvents.ObifinCopyDeletionConfirm && a.TargetId == lic.ToString()))
            .Should().BeTrue();
        (await ParseAsync(await client.GetStringAsync("/admin/obifin")))
            .QuerySelector($"tr[data-deletion='{deletion.Id}']").Should().BeNull("teyit edilen satır listeden çıkar");
    }

    [Fact]
    public async Task Hesap_reddi_hesap_numarasi_yazilinca_verisini_siler_onayi_kaldirir_denetime_yazar()
    {
        // KVKK Ö-10: P1'den önce onaysız hesaptan yazılmış veri ve yanlış verilmiş onay bu eylemle geri alınır.
        var lic = await SeedLicenseAsync();
        await SeedRawConnectionAsync(_factory, lic);
        var accountId = Guid.NewGuid(); var obifinAccountId = Random.Shared.NextInt64(1, 1_000_000);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.BankAccounts.Add(new BankAccount { Id = accountId, LicenseId = lic, ObifinAccountId = obifinAccountId, BankaKodu = "garanti",
                IbanMasked = "?", Currency = "TL", RefreshedAt = DateTimeOffset.UtcNow, ApprovedAt = DateTimeOffset.UtcNow,
                ApprovedBy = "test" });
            db.BankTransactions.Add(new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic,
                ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = obifinAccountId, BankaKodu = "garanti",
                Direction = BankTransactionDirection.Incoming, Amount = 5m, Currency = "TL", OccurredAt = DateTimeOffset.UtcNow,
                FetchedAt = DateTimeOffset.UtcNow, Description = "yabanci" });
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();
        Dictionary<string, string> Fields(string confirm) => new() { ["AccountId"] = accountId.ToString(), ["ConfirmAccount"] = confirm };

        await PostAsync(client, "RejectAccount", Fields("0"));
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().BankTransactions.CountAsync(t => t.LicenseId == lic))
                .Should().Be(1, "hesap numarası yazılmadan hiçbir şey silinmez");

        (await PostAsync(client, "RejectAccount", Fields(obifinAccountId.ToString()))).StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.BankTransactions.CountAsync(t => t.LicenseId == lic)).Should().Be(0);
        (await vdb.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).ApprovedAt.Should().BeNull();
        (await vdb.AuditLogs.AnyAsync(a => a.EventType == AuditEvents.ObifinAccountReject && a.TargetId == lic.ToString()))
            .Should().BeTrue();
        AuditEvents.DestructionEvents.Should().Contain(new[] { AuditEvents.ObifinAccountReject, AuditEvents.ObifinCopyDeletionConfirm });
    }
```

(`PageObifin` sahtesi kaldırmayı kaydeder ama bu iki test Obifin'e gitmez: sahte korunan alanlar çözülmez. Dosyanın üstünde `using OrderDeck.LicenseServer.Domain.Bank;` var; yoksa ekle.)

- [ ] **Adım 7: Sayfa işleyicileri, görünüm, denetim olayları, DI**

`Services/Audit/AuditEvents.cs` — `ObifinAccountApprove` satırının ALTINA:

```csharp
    public const string ObifinConnectionStop = "obifin.connection.stop";
    public const string ObifinConnectionResume = "obifin.connection.resume";
    /// <summary>Bağlantı kaldırıldı ve banka verisi silindi: imha olayı (<see cref="DestructionEvents"/>).</summary>
    public const string ObifinConnectionRemove = "obifin.connection.remove";
    /// <summary>Hesap reddedildi ya da onayı geri alındı, verisi silindi (KVKK Ö-10): imha olayı. Ayrıntı: hesap numarası ve
    /// sayılar.</summary>
    public const string ObifinAccountReject = "obifin.account.reject";
    /// <summary>Obifin'in hareket kopyasını sildiğine dair yazılı teyit kaydedildi (KVKK Ö-9): imha olayı.</summary>
    public const string ObifinCopyDeletionConfirm = "obifin.copy.confirm";
```

ve `DestructionEvents` kümesine `ObifinConnectionRemove, ObifinAccountReject, ObifinCopyDeletionConfirm,` ekle.

`Program.cs` — `builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.ObifinConnectionService>();` satırının ALTINA:

```csharp
        // BankHasher İSTEMEZ: anahtarsız modülde de durdurma ve silme çalışır (KVKK).
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.BankConnectionTerminator>();
```

`Pages/Admin/Obifin/Index.cshtml.cs`:
- `using System.Globalization;` ekle (`OrderDeck.LicenseServer.Services.Privacy` Görev 9'dan beri var).
- Alan `private readonly BankConnectionTerminator _terminator;`; kurucu imzasına `BankConnectionTerminator terminator` (`IAuditService audit`'ten sonra) ekle ve ata.
- Sabitler:

```csharp
    /// <summary>"Kaldır ve sil" onayı. Tek tırnaklı JS dizesine gömülür: kesme işareti içeremez.</summary>
    public const string RemoveConfirmMessage = "Bu lisansın Obifin bağlantısı kaldırılacak ve bütün banka verisi (hareketler, eşleşmeler, IBAN hafızası, hesaplar) silinecek. Geri alınamaz. Emin misiniz?";
    /// <summary>Kaldırmanın sonuç bildirimine eklenir (TempData; JS'e gömülmez).</summary>
    public const string ObifinCopyWarning = "Obifin'deki hareket kopyasının silinmesini Obifin'den YAZILI isteyin (API'de hareket silme ucu yok); teyit gelince 'Obifin silme teyidi bekleyenler'de 'Teyit alındı'ya basın.";
    /// <summary>Yazılı onay (Purge'ün confirmEmail kalıbı) tutmadı.</summary>
    public const string RemoveConfirmMismatchMessage = "Onay için lisans anahtarının son 6 karakterini yazın; eşleşmedi, hiçbir şey silinmedi.";
    public const string RejectConfirmMismatchMessage = "Onay için hesap numarasını yazın; eşleşmedi, hiçbir şey silinmedi.";
```

- `[BindProperty] public string? ConfirmLicenseKey { get; set; }` (gizli `ConfirmRemove` alanı YOK: formun kendisi `true` gönderdiği için sunucu denetimi anlamsızdı).
- `ConnectionRow`'a `DateTimeOffset? DisabledAt, DateTimeOffset? BankDataPurgedAt` ekle (`Status`'tan hemen sonra) ve `Select`'te `c.Status, c.DisabledAt, c.BankDataPurgedAt,` yap.
- Özellik: `/// <summary>Banka verisi hizmet sonunda yerelde silinmiş, Obifin tarafı "Kaldır ve sil" bekleyen bağlantı sayısı.</summary> public int PendingObifinRemoval => Rows.Count(r => r.BankDataPurgedAt is not null);`
- Obifin teyidi bekleyenler (karar 21):

```csharp
    public sealed record CopyDeletionRow(Guid Id, Guid LicenseId, string? CustomerEmail, DateTimeOffset RequestedAt);

    /// <summary>"Kaldır ve sil"den sonra Obifin'in yazılı silme teyidi bekleyen kayıtlar (eskiden yeniye).</summary>
    public List<CopyDeletionRow> PendingCopyDeletions { get; private set; } = new();
```

  `OnGetAsync`'in sonuna (lisans silinmişse e-posta yok, kimlik gösterilir):

```csharp
        PendingCopyDeletions = await _db.ObifinCopyDeletions.AsNoTracking().Where(d => d.ConfirmedAt == null)
            .OrderBy(d => d.RequestedAt)
            .Select(d => new CopyDeletionRow(d.Id, d.LicenseId,
                _db.Licenses.Where(l => l.Id == d.LicenseId).Select(l => l.Customer.Email).FirstOrDefault(), d.RequestedAt))
            .ToListAsync(ct);
```

- İşleyiciler (`OnPostPollNowAsync`'ten önce):

```csharp
    /// <summary>Çekimi durdurur (KVKK K-6). 30 gün içinde etkinleştirilmezse hizmet sonu işi veriyi siler. BankHasher istemez.</summary>
    public async Task<IActionResult> OnPostStopAsync(CancellationToken ct)
    {
        try
        {
            await _terminator.StopAsync(LicenseId, ct);
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("Stop", ex);
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        await _audit.LogAsync(AuditEvents.ObifinConnectionStop, AuditTargets.ObifinConnection, LicenseId.ToString(), null,
            CancellationToken.None);
        TempData["Success"] = "Çekim durduruldu. 30 gün içinde etkinleştirilmezse bu lisansın banka verisi silinir.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResumeAsync(CancellationToken ct)
    {
        try
        {
            await _terminator.ResumeAsync(LicenseId, ct);
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("Resume", ex);
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        await _audit.LogAsync(AuditEvents.ObifinConnectionResume, AuditTargets.ObifinConnection, LicenseId.ToString(), null,
            CancellationToken.None);
        TempData["Success"] = "Bağlantı etkinleştirildi; şimdi Doğrula'ya basın.";
        return RedirectToPage();
    }

    /// <summary>Obifin'de bu lisansa ait banka bağlantılarını kaldırır (paylaşılan Obifin kullanıcısında Obifin'e gitmez, sahibi
    /// doğrulanamayana dokunmaz) ve lisansın bütün banka verisini siler (KVKK K-6, Ö-9). <c>bankaapi/sil</c>'i çağıran TEK yol.
    /// İstek jetonu BİLEREK kullanılmaz: yarıda kesilen kaldırma Obifin'de bağlantı bırakıp yerelde iz kaybettirirdi. Yazılı onay:
    /// lisans anahtarının son 6 karakteri (Purge'ün <c>confirmEmail</c> kalıbı) — geri dönüşsüz işlem tek JS confirm()'una
    /// kalmasın.</summary>
    public async Task<IActionResult> OnPostRemoveAsync()
    {
        var licenseKey = await _db.Licenses.AsNoTracking().Where(l => l.Id == LicenseId).Select(l => l.LicenseKey)
            .FirstOrDefaultAsync(CancellationToken.None);
        var typed = (ConfirmLicenseKey ?? "").Trim();
        if (licenseKey is null || typed.Length != 6 || !licenseKey.EndsWith(typed, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = RemoveConfirmMismatchMessage;
            return RedirectToPage();
        }
        BankRemovalResult r;
        try
        {
            // Yapan: yöneticinin kimliği, kullanıcı adı değil — imha kaydı budanmaz (Görev 9 incelemesi).
            r = await _terminator.RemoveAndDeleteAsync(LicenseId, DataDestruction.Triggers.AdminRemove,
                DataDestruction.ActorOf(User), CancellationToken.None);
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("Remove", ex);
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        // Denetimde "elle iş" bilgisi de durur. Yazılı silme teyidinin takibi denetim satırında DEĞİL (24 ayda maskelenir,
        // kapatılamaz): ObifinCopyDeletions kaydında, hatırlatması BankServiceEndPurgeJob'da (karar 21).
        await _audit.LogAsync(AuditEvents.ObifinConnectionRemove, AuditTargets.ObifinConnection, LicenseId.ToString(),
            new { r.Transactions, r.Matches, r.Memories, obifinRemoved = r.ObifinRemoved.Count, obifinFailed = r.ObifinFailed.Count,
                obifinUnowned = r.ObifinUnowned.Count, r.ObifinUnreachable, r.SharedObifinUser,
                needsManualObifinAction = r.NeedsManualObifinAction, obifinCopyDeletionId = r.ObifinCopyDeletionId },
            CancellationToken.None);
        var summary = $"Banka verisi silindi: {r.Transactions} hareket, {r.Matches} eşleşme, {r.Memories} IBAN bağı, {r.Accounts} hesap. ";
        if (r.NeedsManualObifinAction)
            TempData["Error"] = summary + r.ManualActionText() + " " + ObifinCopyWarning;
        else
            TempData["Success"] = summary + $"Obifin'de {r.ObifinRemoved.Count} banka bağlantısı kaldırıldı. " + ObifinCopyWarning;
        return RedirectToPage();
    }

    /// <summary>Obifin'in hareket kopyasını sildiğine dair yazılı teyit alındı (KVKK Ö-9 öneri 3; karar 21). Teyit yazısı
    /// sistemde saklanmaz (KVKK klasörü).</summary>
    public async Task<IActionResult> OnPostConfirmObifinCopyDeletedAsync(Guid deletionId)
    {
        Guid licenseId;
        try
        {
            licenseId = await _terminator.ConfirmObifinCopyDeletedAsync(deletionId, DataDestruction.ActorOf(User),
                CancellationToken.None);
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("ConfirmObifinCopyDeleted", ex);
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        await _audit.LogAsync(AuditEvents.ObifinCopyDeletionConfirm, AuditTargets.ObifinConnection, licenseId.ToString(),
            new { deletionId }, CancellationToken.None);
        TempData["Success"] = "Obifin'in yazılı silme teyidi kaydedildi. Teyit yazısını KVKK klasöründe saklayın.";
        return RedirectToPage();
    }

    /// <summary>KVKK Ö-10: hesabı reddeder (onaylıysa onayını geri alır) ve o hesabın bu lisanstaki hareketlerini, eşleşmelerini,
    /// öğrettiği IBAN bağlarını siler. Yazılı onay: hesap numarası. İstek jetonu BİLEREK kullanılmaz (silmenin denetimi düşmesin).
    /// BankHasher istemez.</summary>
    public async Task<IActionResult> OnPostRejectAccountAsync(Guid accountId, string? confirmAccount)
    {
        var account = await _db.BankAccounts.AsNoTracking().Where(a => a.Id == accountId)
            .Select(a => new { a.LicenseId, a.ObifinAccountId }).FirstOrDefaultAsync(CancellationToken.None);
        if (account is null)
        {
            TempData["Error"] = BankConnectionTerminator.AccountNotFoundMessage;
            return RedirectToPage();
        }
        if ((confirmAccount ?? "").Trim() != account.ObifinAccountId.ToString(CultureInfo.InvariantCulture))
        {
            TempData["Error"] = RejectConfirmMismatchMessage;
            return RedirectToPage();
        }
        var r = await _terminator.RejectAccountAsync(accountId, DataDestruction.ActorOf(User), CancellationToken.None);
        await _audit.LogAsync(AuditEvents.ObifinAccountReject, AuditTargets.ObifinConnection, account.LicenseId.ToString(),
            new { account.ObifinAccountId, r.Transactions, r.Matches, r.Memories, r.GapsReopened }, CancellationToken.None);
        TempData["Success"] = $"Hesap reddedildi: {r.Transactions} hareket, {r.Matches} eşleşme, {r.Memories} IBAN bağı silindi; " +
            "bundan sonraki hareketleri kaydedilmez.";
        return RedirectToPage();
    }
```

(Yönetici kimliği her yerde `DataDestruction.ActorOf(User)`; sayfada kullanıcı adı yazan bir yardımcı yok — Görev 9 incelemesi, ikinci plan incelemesi.)

`Index.cshtml` — `@if (Model.UnapprovedAccounts > 0) { … }` bloğunun (Görev 12) ALTINA:

```cshtml
@if (Model.PendingObifinRemoval > 0)
{
    <div class="alert alert-warning" data-banner="pending-obifin-removal">
        @Model.PendingObifinRemoval bağlantının banka verisi hizmet sonunda silindi; Obifin tarafı bekliyor: satırdaki "Kaldır ve sil"e basın.
    </div>
}
@if (Model.PendingCopyDeletions.Count > 0)
{
    <div class="alert alert-warning" data-banner="obifin-copy-pending">
        <strong>Obifin silme teyidi bekleyenler</strong> — Obifin'den hareket kopyasının silindiğine dair yazılı teyit gelince "Teyit alındı"ya basın (KVKK).
        <table class="table table-sm mb-0" data-table="obifin-copy-pending">
            @foreach (var d in Model.PendingCopyDeletions)
            {
                var overdue = d.RequestedAt < Model.Now - BankConnectionTerminator.ObifinFollowUpOverdue;
                <tr data-deletion="@d.Id">
                    <td>@(d.CustomerEmail ?? d.LicenseId.ToString())</td>
                    <td class="@(overdue ? "text-danger fw-bold" : "")">istendi @d.RequestedAt.ToString("yyyy-MM-dd")@(overdue ? " (GECİKTİ)" : "")</td>
                    <td>
                        <form method="post" asp-page-handler="ConfirmObifinCopyDeleted" class="d-inline">
                            <input type="hidden" name="DeletionId" value="@d.Id" />
                            <button class="btn btn-sm btn-outline-success">Teyit alındı</button>
                        </form>
                    </td>
                </tr>
            }
        </table>
    </div>
}
```

Durum hücresini:
`<td data-cell="status">@r.Status</td>`
şununla değiştir (tarih yalnız `Disabled` iken; 30 = `BankServiceEndPurgeJob.GracePeriod`, Görev 14):
```cshtml
            <td data-cell="status">@r.Status@(r.Status != ObifinConnectionStatus.Disabled || r.DisabledAt is not { } disabledAt ? ""
                : r.BankDataPurgedAt is { } purgedAt
                    ? $" ({disabledAt:yyyy-MM-dd}; banka verisi {purgedAt:yyyy-MM-dd} silindi — Obifin için Kaldır ve sil)"
                    : $" ({disabledAt:yyyy-MM-dd}; silinecek {disabledAt.AddDays(30):yyyy-MM-dd})")</td>
```

Satırın eylem hücresinde `<a class="btn btn-sm btn-link" … >Düzenle</a>` satırının ÜSTÜNE:

```cshtml
                <form method="post" class="d-inline">
                    <input type="hidden" name="LicenseId" value="@r.LicenseId" />
                    @if (r.Status == ObifinConnectionStatus.Disabled)
                    {
                        <button class="btn btn-sm btn-outline-success" asp-page-handler="Resume">Etkinleştir</button>
                    }
                    else
                    {
                        <button class="btn btn-sm btn-outline-warning" asp-page-handler="Stop">Durdur</button>
                    }
                </form>
                <form method="post" class="d-inline" asp-page-handler="Remove" onsubmit="return confirm('@IndexModel.RemoveConfirmMessage')">
                    <input type="hidden" name="LicenseId" value="@r.LicenseId" />
                    <input name="ConfirmLicenseKey" class="form-control form-control-sm d-inline-block w-auto" maxlength="6" required
                           autocomplete="off" placeholder="lisans anahtarının son 6" aria-label="Onay: lisans anahtarının son 6 karakteri" />
                    <button class="btn btn-sm btn-outline-danger">Kaldır ve sil</button>
                </form>
```

Hesap satırında (Görev 12'nin `<span data-cell="approval">…</span>`'ının ALTINA, `</div>`'den önce) ret formu:

```cshtml
                        <form method="post" asp-page-handler="RejectAccount" class="d-inline" data-form="reject-account"
                              onsubmit="return confirm('Bu hesabın bu lisanstaki hareketleri, eşleşmeleri ve öğrettiği IBAN bağları silinecek; onayı kalkacak. Geri alınamaz.')">
                            <input type="hidden" name="AccountId" value="@a.Id" />
                            <input name="ConfirmAccount" class="form-control form-control-sm d-inline-block w-auto" required autocomplete="off"
                                   placeholder="hesap no" aria-label="Onay: hesap numarası (#@a.ObifinAccountId)" />
                            <button class="btn btn-sm btn-outline-danger">@(a.ApprovedAt is null ? "Reddet ve verisini sil" : "Onayı geri al ve verisini sil")</button>
                        </form>
```

- [ ] **Adım 8: Göçü üret ve incele**

Run: `cd OrderDeck.LicenseServer && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add ObifinConnectionServiceEnd --context LicenseDbContext --output-dir Data/Migrations`
Expected: `Up` yalnız `AddColumn<DateTimeOffset>(name: "BankDataPurgedAt", table: "ObifinConnections", nullable: true)`, `AddColumn<bool>(name: "DisabledByAdmin", table: "ObifinConnections", nullable: false, defaultValue: false)`, `CreateTable("ObifinCopyDeletions")` (FK'siz) ve iki `CreateIndex` (`LicenseId`; süzgeçli `IX_ObifinCopyDeletions_Pending`). P0'da lisans bitişiyle Disabled'a çekilmiş bağlantılar `DisabledByAdmin = false` ile gelir — doğru: P1'den önce yönetici durdurması yoktu.

- [ ] **Adım 9: Yayıncı silmesi Obifin bağlantısını atlamasın (test önce, kırmızı)**

`Controllers/Compliance/GdprFlowTests.cs` — üste `using System.Text.Json;` ekle; sınıfa yardımcı ve test:

```csharp
    /// <summary>Bearer-Admin istemcisi (üretilmiş yönetici; parola literal değil).</summary>
    private async Task<HttpClient> AdminApiClientAsync()
    {
        string adminToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
            var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
            var admin = new OrderDeck.LicenseServer.Domain.AdminUser
            {
                Id = Guid.NewGuid(), Username = $"a-{Guid.NewGuid():N}", PasswordHash = hasher.Hash($"p-{Guid.NewGuid():N}"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.AdminUsers.Add(admin);
            await db.SaveChangesAsync();
            adminToken = jwt.IssueAdminToken(admin.Id, admin.Username).Token;
        }
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        return client;
    }

    [Fact]
    public async Task Purge_Obifin_baglantisi_varken_reddedilir_once_Kaldir_ve_sil_istenir()
    {
        // KVKK K-6/Ö-9: yayıncı silmesi banka tablolarını kaskadla götürür ama Obifin'e bir şey söylemez; Obifin bankadan çekmeyi
        // ve saklamayı sürdürürdü. Bağlantı varken silme reddedilir: Obifin tarafı yalnız "Kaldır ve sil"de (lisansa özgü,
        // paylaşılan kullanıcı korumalı) kapanır.
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var licenseId = Guid.NewGuid();
        string email;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            email = (await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId)).Email;
            db.Licenses.Add(new OrderDeck.LicenseServer.Domain.License { Id = licenseId, LicenseKey = $"LDK-GD-{Guid.NewGuid():N}",
                CustomerId = customerId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
            // Korunan alanlar sahte: kimlik çözülemez → "Kaldır ve sil" Obifin'e gidemez (ulaşılamadı), yerel silme yine yapılır.
            db.ObifinConnections.Add(new OrderDeck.LicenseServer.Domain.Bank.ObifinConnection { Id = Guid.NewGuid(),
                LicenseId = licenseId, BaseUrl = "https://example.invalid", UserCode = $"u-{Guid.NewGuid():N}@x",
                PasswordProtected = $"p-{Guid.NewGuid():N}", ApiKeyProtected = $"a-{Guid.NewGuid():N}",
                Status = OrderDeck.LicenseServer.Domain.Bank.ObifinConnectionStatus.Verified,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var admin = await AdminApiClientAsync();

        var refused = await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("bank-connection-active");
        problem.GetProperty("detail").GetString().Should().Contain("Kaldır ve sil").And.Contain("yazılı");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId)).Email.Should().Be(email, "hiçbir şey silinmedi");
            var r = await scope.ServiceProvider.GetRequiredService<OrderDeck.LicenseServer.Services.Bank.BankConnectionTerminator>()
                .RemoveAndDeleteAsync(licenseId, OrderDeck.LicenseServer.Services.Privacy.DataDestruction.Triggers.AdminRemove,
                    "admin", CancellationToken.None);
            r.NeedsManualObifinAction.Should().BeTrue("kimlik çözülemedi: Obifin'de elle kaldırılacak");
        }

        (await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent, "Obifin tarafı kapandıktan sonra silme yapılır");
    }
```

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~GdprFlowTests"`
Expected: FAIL — purge 204 döner (bağlantı kaskadla gidiyor, Obifin'e bir şey söylenmiyor).

`Controllers/Customers/AdminCustomersController.cs` — `Purge`'te `var preEmail = customer.Email;` satırının ÜSTÜNE:

```csharp
        // KVKK K-6/Ö-9: banka tabloları lisansla kaskadla gider ama Obifin'e bir şey söylenmez — Obifin bankadan çekmeyi ve
        // saklamayı sürdürür. Obifin bağlantısı olan lisans varken silme reddedilir: Obifin tarafı (lisansa özgü kaldırma,
        // paylaşılan kullanıcı koruması, yazılı teyit uyarısı) yalnız admin/obifin'deki "Kaldır ve sil"de kapanır. Purge içinde
        // ağ çağrısı yapılmaz.
        var bankLicenses = await _db.ObifinConnections
            .CountAsync(c => _db.Licenses.Any(l => l.Id == c.LicenseId && l.CustomerId == id), ct);
        if (bankLicenses > 0)
            return Problem(title: "bank-connection-active", statusCode: 409,
                detail: $"Bu yayıncının {bankLicenses} lisansında Obifin banka bağlantısı var. Önce admin/obifin'de her biri için " +
                    "'Kaldır ve sil' çalıştırın ve Obifin'den hareket kopyasının silindiğine dair yazılı teyit isteyin; sonra " +
                    "silmeyi yineleyin.");
```

Metodun özetine bir paragraf: `/// Obifin banka bağlantısı olan lisans varken 409 bank-connection-active: önce admin/obifin → "Kaldır ve sil" (KVKK K-6, Ö-9).`

- [ ] **Adım 9b: Gerçek SQL Server testi**

Yerel silme her gün (hizmet sonu) ve yöneticinin tek tıklamasıyla geri dönüşsüz siler. Partili silme, `BankConnections` → `ObifinConnections` sırası (FK), eşleşme jetonunun `WHERE UpdatedAt = özgün` koşuluyla çakışmada yeniden deneme ve imha kaydının partiyle aynı işlemde yazılması InMemory'de sınanamaz. `OrderDeck.LicenseServer.Tests/Services/Bank/BankConnectionTerminatorRelationalTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>BankConnectionTerminator gerçek SQL Server'da (göçlerle kurulmuş şema, FK'ler, CHECK kısıtları): partili yerel silme,
/// bağlantının en son silinmesi, eşleşme jetonu çakışmasında bir kez yeniden deneme. Korunan alanlar sahte: kimlik çözülmez,
/// Obifin'e gidilmez ("ulaşılamadı").</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankConnectionTerminatorRelationalTests : IAsyncLifetime
{
    private sealed class HookedFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public SaveHookInterceptor Hook { get; } = new();
        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }

    private readonly SqlServerContainerFixture _sql;
    private HookedFactory _factory = null!;
    public BankConnectionTerminatorRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new HookedFactory(await _sql.CreateDatabaseAsync());
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Lisans + bağlantı + yerel banka bağlantısı (#501) + onaylı hesap + <paramref name="transactions"/> hareket (her
    /// birinin eşleşmesi) + IBAN bağı + gap. İlk eşleşmenin kimliğini de döner.</summary>
    private async Task<(Guid License, Guid FirstMatchId)> SeedAsync(int transactions)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"bt-{Guid.NewGuid():N}@example.test", Name = "Bt",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now };
        var lic = Guid.NewGuid();
        db.Customers.Add(customer);
        db.Licenses.Add(new License { Id = lic, CustomerId = customer.Id, LicenseKey = "bt-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddYears(1) });
        var connId = Guid.NewGuid();
        db.ObifinConnections.Add(new ObifinConnection { Id = connId, LicenseId = lic, BaseUrl = "https://example.invalid",
            UserCode = $"u-{Guid.NewGuid():N}@x", PasswordProtected = $"p-{Guid.NewGuid():N}", ApiKeyProtected = $"a-{Guid.NewGuid():N}",
            Status = ObifinConnectionStatus.Verified, CreatedAt = now, UpdatedAt = now });
        var bankConnId = Guid.NewGuid();
        db.BankConnections.Add(new BankConnection { Id = bankConnId, LicenseId = lic, ObifinConnectionId = connId, BankaKodu = "qnb",
            BankaApiId = 501, Label = "QNB", Status = BankConnectionStatus.Active, CreatedAt = now });
        db.BankAccounts.Add(new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, BankConnectionId = bankConnId, ObifinAccountId = 6,
            BankaKodu = "qnb", IbanMasked = "TR12…345", Currency = "TL", RefreshedAt = now, ApprovedAt = now, ApprovedBy = "test" });
        var firstMatch = Guid.Empty;
        for (var i = 0; i < transactions; i++)
        {
            var tx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = i + 1, ObifinAccountId = 6, BankaKodu = "qnb",
                Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now, FetchedAt = now,
                Description = "aciklama" };
            var match = new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id,
                Status = PaymentMatchStatus.NoProposal, CreatedAt = now, UpdatedAt = now };
            if (i == 0) firstMatch = match.Id;
            db.BankTransactions.Add(tx);
            db.PaymentMatches.Add(match);
        }
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = Guid.NewGuid(),
            IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.ManualMatch,
            CreatedAt = now });
        db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(), CreatedAt = now });
        await db.SaveChangesAsync();
        return (lic, firstMatch);
    }

    [Fact]
    public async Task Kaldir_ve_sil_gercek_SQL_Serverda_parti_parti_siler_baglanti_en_son_gider_teyit_kaydi_kalir()
    {
        var total = BankConnectionTerminator.BatchSize + 1;
        var (lic, _) = await SeedAsync(total);
        var (other, _) = await SeedAsync(1);

        BankRemovalResult r;
        using (var scope = _factory.Services.CreateScope())
            r = await scope.ServiceProvider.GetRequiredService<BankConnectionTerminator>()
                .RemoveAndDeleteAsync(lic, DataDestruction.Triggers.AdminRemove, "yonetici-1", CancellationToken.None);

        r.ObifinUnreachable.Should().BeTrue("korunan alanlar sahte: Obifin'e gidilmedi");
        (r.Transactions, r.Matches, r.Memories, r.Gaps, r.Accounts, r.BankConnections).Should().Be((total, total, 1, 1, 1, 1));
        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.BankTransactions.CountAsync(t => t.LicenseId == lic)).Should().Be(0);
        (await db.PaymentMatches.CountAsync(m => m.LicenseId == lic)).Should().Be(0);
        (await db.BankConnections.CountAsync(b => b.LicenseId == lic)).Should().Be(0);
        (await db.ObifinConnections.CountAsync(c => c.LicenseId == lic)).Should().Be(0);
        (await db.BankTransactions.CountAsync(t => t.LicenseId == other)).Should().Be(1, "başka lisansa dokunulmaz");
        (await db.ObifinCopyDeletions.AsNoTracking().SingleAsync(d => d.LicenseId == lic)).ConfirmedAt.Should().BeNull();
        var records = await db.DataDestructionRecords.AsNoTracking().Where(x => x.LicenseId == lic).ToListAsync();
        var txRecords = records.Where(x => x.Target == "BankTransactions").ToList();
        txRecords.Should().HaveCount(2, "iki parti, her biri kendi kaydıyla");
        txRecords.Sum(x => x.RowCount).Should().Be(total);
        records.Should().Contain(x => x.Target == "ObifinConnections" && x.RowCount == 1);
    }

    [Fact]
    public async Task Yerel_silme_gercek_SQL_Serverda_kimligi_banka_baglantisini_ve_onayli_hesabi_IBANsiz_tutar_isaret_yazar()
    {
        var (lic, _) = await SeedAsync(3);

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BankConnectionTerminator>()
                .PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor, CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.BankTransactions.CountAsync(t => t.LicenseId == lic)).Should().Be(0);
        var kept = await db.BankAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == lic);
        (kept.ApprovedAt is not null, kept.IbanMasked, kept.IbanHash)
            .Should().Be((true, "", (string?)null), "onaylı hesap Kaldır ve sil'in sahiplik kanıtı, IBAN'sız");
        (await db.BankConnections.CountAsync(b => b.LicenseId == lic)).Should().Be(1, "yönetici Obifin tarafını sonra kapatır");
        (await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic)).BankDataPurgedAt.Should().NotBeNull();
        (await db.ObifinCopyDeletions.AnyAsync(d => d.LicenseId == lic)).Should().BeFalse("teyit kaydını yalnız Kaldır ve sil açar");
    }

    [Fact]
    public async Task Eszamanli_eslestirme_yazisiyla_carpisan_silme_bir_kez_yeniden_denenir_sayilar_birikir()
    {
        var (lic, firstMatch) = await SeedAsync(2);
        _factory.Hook.BeforeSave = async () =>
        {
            _factory.Hook.BeforeSave = null;   // yalnız ilk parti çakışsın
            using var scope = _factory.Services.CreateScope();
            var other = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var m = await other.PaymentMatches.SingleAsync(x => x.Id == firstMatch);
            m.Evidence = "eşzamanlı yazı";
            m.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await other.SaveChangesAsync();
        };

        BankRemovalResult r;
        try
        {
            using var scope = _factory.Services.CreateScope();
            r = await scope.ServiceProvider.GetRequiredService<BankConnectionTerminator>()
                .PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor, CancellationToken.None);
        }
        finally
        {
            _factory.Hook.Reset();
        }

        (r.Transactions, r.Matches).Should().Be((2, 2));
        using var verify = _factory.Services.CreateScope();
        (await verify.ServiceProvider.GetRequiredService<LicenseDbContext>().PaymentMatches.CountAsync(m => m.LicenseId == lic))
            .Should().Be(0);
    }
}
```

- [ ] **Adım 10: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~AdminObifinPageTests|FullyQualifiedName~BankConnectionTerminatorTests|FullyQualifiedName~ObifinConnectionServiceTests|FullyQualifiedName~ObifinPollJobTests|FullyQualifiedName~BankJobsDiTests|FullyQualifiedName~GdprFlowTests|FullyQualifiedName~PaymentMatch|FullyQualifiedName~BankMatchSweepJobTests|FullyQualifiedName~AdminBankaEslestirmePageTests"`
Expected: PASS. (`ObifinConnectionServiceTests`'in lisansları geçerli; Disabled bağlantı testleri `MarkVerified`'ın Disabled'ı açmadığını hâlâ doğrular. `PaymentMatch` süzgecine giren eşzamanlılık sınıfları Testcontainers ister: Docker yoksa onlar düşer, ikinci komutta koşar.)

Run (Docker): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataCleanupRelationalTests|FullyQualifiedName~CustomerPurgeRelationalTests|FullyQualifiedName~BankConnectionTerminatorRelationalTests|FullyQualifiedName~PaymentMatchReconcilerConcurrencyTests"`
Expected: PASS (yeni sütunlar ve tablo SQL Server'da; purge'ün gerçek SQL yolu bağlantısız lisansta değişmedi; eşleştirme kapısı bağlantısız lisansta bugünkü davranışı korur).

- [ ] **Adım 11: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/Bank/ObifinConnection.cs OrderDeck.LicenseServer/Domain/Bank/ObifinCopyDeletion.cs OrderDeck.LicenseServer/Data OrderDeck.LicenseServer/Services/Privacy/DataDestruction.cs OrderDeck.LicenseServer/Services/Bank/BankConnectionTerminator.cs OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs OrderDeck.LicenseServer/Services/Bank/PaymentMatchReconcileJob.cs OrderDeck.LicenseServer/Services/Bank/BankMatchSweepJob.cs OrderDeck.LicenseServer/Services/Bank/PaymentMatchReconciler.cs OrderDeck.LicenseServer/Pages/Admin/Obifin OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs OrderDeck.LicenseServer/Controllers/Customers/AdminCustomersController.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Bank OrderDeck.LicenseServer.Tests/Pages/Admin/AdminObifinPageTests.cs OrderDeck.LicenseServer.Tests/Controllers/Compliance/GdprFlowTests.cs
git commit -m "feat(bank-kvkk): banka bağlantısını durdur, etkinleştir, kaldır ve sil

Yönetim sayfasına Durdur/Etkinleştir/Kaldır ve sil: kaldırma Obifin'de
yalnız bu lisansa ait bağlantıları (yerel kayıt ya da onaylı hesap)
bankaapi/sil ile kaldırır, paylaşılan Obifin kullanıcısında Obifin'e hiç
gitmez, sahibi doğrulanamayanı elle kontrole bırakır; lisansın banka
verisini partilerle siler, imha kaydı yazar ve Obifin'in yazılı silme
teyidini bekleyen kayıt açar (Teyit alındı). Kaldırma lisans anahtarıyla
yazılı onay ister ve silmeden önce bağlantıyı tek kayıtla durdurur
(partiler sürerken çekim ve eşleştirme yazmaz). Durdurulmuş ya da yerel
verisi silinmiş bağlantıda eşleştirme, dekont bağdaştırması ve IBAN
öğrenme de durur. Onaysız hesap reddedilip verisi silinir, onay geri
alınabilir. Otomatik iş için Obifin'e gitmeyen yerel silme: Kaldır ve
sil'in sahiplik kanıtı (yerel banka bağlantısı, IBAN'sız onaylı hesap)
kalır. Obifin bağlantısı varken yayıncı silmesi reddedilir (KVKK ön
inceleme K-6, Ö-9, Ö-10, P1-10).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 14: Hizmet sonu + 30 gün silme işi (P1-10b)

**Neden (not K-6 öneri 2):** bağlantı durdurulduktan ya da lisans bittikten 30 gün sonra lisansın bütün banka verisi silinmeli (İYS "ayrılış + 30 gün" kalıbı; Silme Yönetmeliği md. 11/3'teki 3 ay sınırının içinde). Günlük iş, sırayla:
0. **Tarihsiz `Disabled` damgalanır.** Kod `Disabled`'ı hep `DisabledAt`'le yazar (Görev 1, 13); tarihsiz satır ancak P0'dan önce elle (SQL ile) durdurulmuş olabilir. Görev 1 zaten Disabled olanı atladığı için ona hiç tarih yazılmaz; damgalanmasa hizmet sonu silmesine hiç girmezdi. Sayaç bugün başlar ve satır **yönetici durdurması** sayılır (`DisabledByAdmin`): bu durumu yazan bir insandır, 1. adım onu lisans geçerli diye kendiliğinden açmamalı. Prod adımlarındaki P0 öncesi kontrol (b) bu sayının 0 olmasını bekliyor; adım o varsayıma dayanmaz, ucuz olduğu için koşulsuz koşar.
1. **Lisansı yenilenen bağlantı silinmez.** Lisans uzatma (`AdminLicensesController.Extend`, `Licenses/Detail` → Uzat) ya da iptalin geri alınması bağlantıya dokunmuyor; iş olmasa bağlantı Disabled kalır ve 30. günde ödeme yapan yayıncının bütün banka verisi geri dönüşsüz silinirdi. Kendiliğinden (lisans bitişiyle) durdurulmuş ve lisansı bugün geçerli bağlantı `Unverified`'a döner, hizmet sonu alanları boşalır (`ObifinConnectionService.ReviveRenewedLicensesAsync`), yöneticiye "lisans yenilendi, Doğrula'ya basın" e-postası gider. Yöneticinin durdurduğu (`DisabledByAdmin`) bağlantı kendiliğinden açılmaz. Silme sorgusu aynı kuralı bir kez daha uygular (eşzamanlı uzatmaya karşı).
2. Lisansı biteni Disabled'a çeker (çekim anahtarsız zamanlanmıyor olabilir).
3. `DisabledAt`'i 30 günü geçen ve henüz işlenmemiş (`BankDataPurgedAt` boş) bağlantının banka verisini **yalnız yerelde** siler (`BankConnectionTerminator.PurgeLocalDataAsync`). "Kaldır ve sil"in sahiplik kanıtı kalır: Obifin kimliği, yerel banka bağlantıları ve onaylı hesap satırları (IBAN alanları boşaltılmış; Görev 13'ün üçüncü inceleme maddesi). **Obifin'e gidilmez:** `bankaapi/sil`'i yalnız yöneticinin "Kaldır ve sil"i çağırır (Görev 13: lisansa özgü, paylaşılan kullanıcı korumalı). İnsan onayı olmadan Obifin'deki bir bağlantıyı silmek — paylaşılan Obifin kullanıcısında EMAR'ın QNB bağlantısını da — haftalar süren yeniden başvuru demekti.
4. Yöneticiye özet e-posta: her lisans için "admin/obifin'de Kaldır ve sil" ve Obifin'den yazılı silme teyidi istenmesi; admin/obifin'de de uyarı şeridi kalır.
5. **Obifin tarafının takibi (ikinci plan incelemesi; K-6 öneri 1, Ö-9 öneri 3; karar 21).** Tek seferlik e-posta ve sayfadaki şerit Obifin'in çekmeyi ve saklamayı sürdürmesini durdurmazdı; hatırlatma, süre sınırı ve tırmandırma yoktu. Her koşu iki listeyi tek bir hatırlatma e-postasında toplar. Birinci liste: bu koşudan ÖNCE yerelde silinmiş ama "Kaldır ve sil" bekleyen bağlantılar (`BankDataPurgedAt` dolu, bağlantı satırı duruyor). İkinci liste: Obifin'den yazılı silme teyidi gelmemiş kopyalar (`ObifinCopyDeletions`, `ConfirmedAt` boş; Görev 13). Biri `BankConnectionTerminator.ObifinFollowUpOverdue`'yu (14 gün) geçtiyse konu "GECİKTİ" olur. Bekleyen yoksa e-posta gitmez. Bu koşuda yerelde silinenler bu listeye girmez, onlar için "yapıldı" e-postası gider. Hatırlatma uyarı yoludur (`AdminJobAlerts.SendAsync`): gönderim düşerse ertesi gün yeniden gelir.

**P0 → P1 geçişi:** Görev 1 (P0) `DisabledAt` yazmaya başladı. P1 geç deploy edilirse ilk 05:17 koşusu 30 günü geçmiş bütün bağlantıların verisini YEREL olarak siler (Obifin'e gidilmez; lisansı yenilenmiş olan 1. adımda kurtulur). Prod adımlarındaki "P1 deploy'undan önce" maddesine bakın.

Çekimle aynı kilit (`obifin-poll`): silme ile çekim yarışmaz. Anahtardan bağımsız HEP zamanlanır (`BankHasher` istemez).

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Bank/BankServiceEndPurgeJob.cs`
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs` (`ReviveRenewedLicensesAsync`, `LicenseRenewedMessage`)
- Modify: `OrderDeck.LicenseServer/Program.cs` (DI + `ScheduleBankJobs`)
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankServiceEndPurgeJobTests.cs` (YENİ), `BankJobsDiTests.cs`

- [ ] **Adım 1: İş testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Bank/BankServiceEndPurgeJobTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK K-6: hizmet sonu + 30 gün banka verisi YEREL olarak silinir (Obifin'e gidilmez); lisansı yenilenen silinmez;
/// lisansı biten önce durdurulur; yöneticiye özet gider.</summary>
public sealed class BankServiceEndPurgeJobTests
{
    /// <summary>Her çağrıda patlar: otomatik iş Obifin'e HİÇ gitmemeli (gitseydi iş lisansı "DÜŞTÜ" sayar, test kırılır).</summary>
    private sealed class NoCallObifin : IObifinClient
    {
        private static Exception Fail() => new InvalidOperationException("Otomatik hizmet sonu işi Obifin'e gitmemeli.");
        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default) => throw Fail();
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default) => throw Fail();
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default) => throw Fail();
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default) => throw Fail();
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default) => throw Fail();
    }

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-end-{Guid.NewGuid():N}").Options);

    private static BankServiceEndPurgeJob Job(LicenseDbContext db, TestEmailSender email)
        => new(db, new BankConnectionTerminator(db, new NoCallObifin(), new EphemeralDataProtectionProvider(),
                NullLogger<BankConnectionTerminator>.Instance),
            new AdminJobAlerts(email, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AdminJobAlerts.AlertEmailKey] = $"ops-{Guid.NewGuid():N}@example.test",
            }).Build(), NullLogger<AdminJobAlerts>.Instance),
            NullLogger<BankServiceEndPurgeJob>.Instance);

    /// <summary>Lisans + bağlantı (gerçek kimlik çözülse de iş Obifin'e gitmez) + bir hareket + yerel banka bağlantısı.</summary>
    private static async Task<Guid> SeedAsync(LicenseDbContext db, ObifinConnectionStatus status, DateTimeOffset? disabledAt,
        DateTimeOffset licenseExpiresAt, bool disabledByAdmin = false)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"e-{Guid.NewGuid():N}@x", Name = "E",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new License { Id = lic, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow.AddYears(-1), ExpiresAt = licenseExpiresAt });
        var now = DateTimeOffset.UtcNow;
        var connId = Guid.NewGuid();
        db.ObifinConnections.Add(new ObifinConnection { Id = connId, LicenseId = lic, BaseUrl = "https://example.invalid",
            UserCode = $"u-{Guid.NewGuid():N}@x", PasswordProtected = $"p-{Guid.NewGuid():N}", ApiKeyProtected = $"a-{Guid.NewGuid():N}",
            Status = status, DisabledAt = disabledAt, DisabledByAdmin = disabledByAdmin,
            LastError = disabledByAdmin ? BankConnectionTerminator.StoppedByAdminMessage
                : status == ObifinConnectionStatus.Disabled ? ObifinConnectionService.LicenseEndedMessage : null,
            CreatedAt = now, UpdatedAt = now });
        db.BankConnections.Add(new BankConnection { Id = Guid.NewGuid(), LicenseId = lic, ObifinConnectionId = connId,
            BankaKodu = "qnb", BankaApiId = 501, Label = "QNB", Status = BankConnectionStatus.Active, CreatedAt = now });
        db.BankTransactions.Add(new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 1, ObifinAccountId = 6,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now, FetchedAt = now });
        await db.SaveChangesAsync();
        return lic;
    }

    private static readonly DateTimeOffset Ended = DateTimeOffset.UtcNow.AddDays(-40);

    [Fact]
    public async Task Otuz_gunu_gecen_baglantinin_verisi_yerelde_silinir_Obifine_gidilmez_otuz_gun_icindeki_kalir()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var due = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), Ended);
        var waiting = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-29), Ended);

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(1, "NoCallObifin'e dokunulsaydı lisans DÜŞTÜ sayılırdı");
        (await db.BankTransactions.AnyAsync(t => t.LicenseId == due)).Should().BeFalse();
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == due);
        conn.BankDataPurgedAt.Should().NotBeNull("Obifin kimliği yönetici 'Kaldır ve sil' diyene kadar kalır");
        (await db.BankConnections.AnyAsync(b => b.LicenseId == due && b.BankaApiId == 501)).Should().BeTrue();
        (await db.BankTransactions.AnyAsync(t => t.LicenseId == waiting)).Should().BeTrue();
        (await db.DataDestructionRecords.AnyAsync(r => r.LicenseId == due && r.Trigger == DataDestruction.Triggers.ServiceEnd))
            .Should().BeTrue();
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(BankServiceEndPurgeJob.DoneSubject);
        mail.PlainBody.Should().Contain(due.ToString()).And.Contain("Kaldır ve sil").And.Contain("YAZILI teyit");
    }

    [Fact]
    public async Task Islenmis_baglanti_ertesi_gun_yeniden_islenmez_yalniz_Kaldir_ve_sil_hatirlatmasi_gelir()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), Ended);
        await Job(db, email).RunAsync(CancellationToken.None);

        var second = await Job(db, email).RunAsync(CancellationToken.None);

        second.Should().Be(0);
        email.Sent.Select(m => m.Subject).Should().Equal(BankServiceEndPurgeJob.DoneSubject, BankServiceEndPurgeJob.ObifinPendingSubject);
    }

    [Fact]
    public async Task Kaldir_ve_sil_bekleyen_ve_teyidi_gelmeyen_lisanslar_tek_hatirlatmada_14_gunu_gecen_GECIKTI()
    {
        // KVKK Ö-9 öneri 3: otomatik iş Obifin'e gitmiyor (karar 12); Obifin'in çekmeyi ve saklamayı bırakması izlenmeli.
        using var db = NewDb(); var email = new TestEmailSender();
        var awaitingRemoval = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-35), Ended);
        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == awaitingRemoval)).BankDataPurgedAt =
            DateTimeOffset.UtcNow.AddDays(-3);
        var awaitingConfirmation = Guid.NewGuid();   // bağlantısı ve lisansı silinmiş olabilir: kayıt FK'siz
        db.ObifinCopyDeletions.Add(new ObifinCopyDeletion { Id = Guid.NewGuid(), LicenseId = awaitingConfirmation,
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-15), RequestedBy = "yonetici-1" });
        await db.SaveChangesAsync();

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(0, "yerel verisi silinmiş bağlantı yeniden işlenmez");
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(BankServiceEndPurgeJob.ObifinOverdueSubject, "teyit isteği 14 günü geçti");
        mail.PlainBody.Should().Contain(awaitingRemoval.ToString()).And.Contain("Kaldır ve sil")
            .And.Contain(awaitingConfirmation.ToString()).And.Contain("yazılı silme teyidi").And.Contain("(GECİKTİ)");
    }

    [Fact]
    public async Task Teyit_bekleyen_14_gun_icindeyse_olagan_hatirlatma_gider_teyit_alininca_hatirlatma_durur()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var pending = new ObifinCopyDeletion { Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(),
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-2), RequestedBy = "yonetici-1" };
        db.ObifinCopyDeletions.Add(pending);
        await db.SaveChangesAsync();

        await Job(db, email).RunAsync(CancellationToken.None);
        (await db.ObifinCopyDeletions.SingleAsync()).ConfirmedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await Job(db, email).RunAsync(CancellationToken.None);

        var mail = email.Sent.Should().ContainSingle("teyit alındıktan sonra hatırlatma gitmez").Subject;
        mail.Subject.Should().Be(BankServiceEndPurgeJob.ObifinPendingSubject);
        mail.PlainBody.Should().Contain(pending.LicenseId.ToString()).And.NotContain("(GECİKTİ)");
    }

    [Fact]
    public async Task Lisans_yenilenmisse_veri_silinmez_baglanti_Unverified_olur_yoneticiye_uyari_gider()
    {
        // KVKK K-6 + veri kaybı: lisans 30 gün içinde uzatıldı (Extend bağlantıya dokunmuyor). Ödeme yapan yayıncının verisi
        // silinmemeli; bağlantı yönetici Doğrula'ya basana dek Unverified bekler.
        using var db = NewDb(); var email = new TestEmailSender();
        var lic = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31),
            licenseExpiresAt: DateTimeOffset.UtcNow.AddDays(30));

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(0);
        (await db.BankTransactions.AnyAsync(t => t.LicenseId == lic)).Should().BeTrue();
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
        (conn.DisabledAt, conn.BankDataPurgedAt).Should().Be(((DateTimeOffset?)null, (DateTimeOffset?)null));
        conn.LastError.Should().Be(ObifinConnectionService.LicenseRenewedMessage);
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(BankServiceEndPurgeJob.RenewedSubject);
        mail.PlainBody.Should().Contain(lic.ToString()).And.Contain("Doğrula");
    }

    [Fact]
    public async Task Yerel_silmeden_sonra_lisans_yenilenirse_baglanti_yine_acilir()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var lic = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), Ended);
        await Job(db, email).RunAsync(CancellationToken.None);
        (await db.Licenses.SingleAsync(l => l.Id == lic)).ExpiresAt = DateTimeOffset.UtcNow.AddDays(30);
        await db.SaveChangesAsync();

        await Job(db, email).RunAsync(CancellationToken.None);

        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
        conn.BankDataPurgedAt.Should().BeNull("yeniden toplanan veri sonraki hizmet sonunda yine silinebilsin");
    }

    [Fact]
    public async Task Yonetici_durdurduysa_lisans_gecerli_olsa_da_otuz_gunde_silinir_kendiliginden_acilmaz()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var lic = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31),
            licenseExpiresAt: DateTimeOffset.UtcNow.AddDays(30), disabledByAdmin: true);

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(1);
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.BankDataPurgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Lisansi_biten_ama_hala_Verified_baglanti_bugun_durdurulur_silinmez()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var lic = await SeedAsync(db, ObifinConnectionStatus.Verified, null, Ended);

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(0);
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.DisabledAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), "30 günlük sayaç bugün başlar");
        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Tarihsiz_Disabled_baglanti_bugun_damgalanir_yonetici_durdurmasi_sayilir_acilmaz_silinmez()
    {
        // P0 öncesi elle (SQL ile) Disabled yapılmış satırın DisabledAt'i yok: damgalanmasa hizmet sonu silmesine hiç girmezdi.
        // Kod bu durumu hiç yazmaz; yazan yöneticidir — lisans geçerli olsa da kendiliğinden açılmaz.
        using var db = NewDb(); var email = new TestEmailSender();
        var lic = await SeedAsync(db, ObifinConnectionStatus.Disabled, disabledAt: null,
            licenseExpiresAt: DateTimeOffset.UtcNow.AddDays(30));

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(0);
        (await db.BankTransactions.AnyAsync(t => t.LicenseId == lic)).Should().BeTrue();
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.DisabledByAdmin.Should().BeTrue();
        conn.DisabledAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), "30 günlük sayaç bugün başlar");
        email.Sent.Should().BeEmpty("yenilenme e-postası gitmez: bağlantı açılmadı");
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankServiceEndPurgeJobTests"`
Expected: FAIL — `BankServiceEndPurgeJob`, `LicenseRenewedMessage` yok.

- [ ] **Adım 3: Lisans yenilenmesini tanı**

`Services/Bank/ObifinConnectionService.cs` — `LicenseEndedMessage`'ın ALTINA:

```csharp
    /// <summary>Lisansı yenilenen (kendiliğinden durdurulmuş) bağlantının <c>LastError</c> metni.</summary>
    public const string LicenseRenewedMessage = "Lisans yenilendi; çekimi sürdürmek için Doğrula'ya basın.";
```

`ClearServiceEnd`'in (Görev 13) ALTINA:

```csharp
    /// <summary>KVKK K-6 + veri kaybı: kendiliğinden (lisans bitişiyle, <c>DisabledByAdmin</c> yanlış) Disabled'a çekilmiş
    /// bağlantının lisansı bugün yeniden geçerliyse — uzatma ya da iptalin geri alınması; ikisi de bağlantıya dokunmuyor —
    /// bağlantı Unverified'a döner, hizmet sonu alanları boşalır (<see cref="ClearServiceEnd"/>), <see cref="LicenseRenewedMessage"/>
    /// yazılır. Çekim yönetici Doğrula'ya basınca sürer. Yöneticinin durdurduğu bağlantı açılmaz. Lisans kimliklerini döner.</summary>
    public static async Task<IReadOnlyList<Guid>> ReviveRenewedLicensesAsync(LicenseDbContext db, DateTimeOffset now, ILogger log,
        CancellationToken ct)
    {
        // Lisans kuralı LicenseActiveAt'ten (Görev 1): banka modülünde tek tanım.
        var activeLicenseIds = db.Licenses.Where(LicenseActiveAt(now)).Select(l => l.Id);
        var renewed = await db.ObifinConnections
            .Where(c => c.Status == ObifinConnectionStatus.Disabled && !c.DisabledByAdmin
                && activeLicenseIds.Contains(c.LicenseId))
            .ToListAsync(ct);
        if (renewed.Count == 0) return [];
        foreach (var c in renewed)
        {
            c.Status = ObifinConnectionStatus.Unverified;
            ClearServiceEnd(c);
            c.LastError = LicenseRenewedMessage;
            c.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        log.LogWarning("Obifin: lisansı yenilenen {Count} bağlantı Unverified'a çekildi, hizmet sonu silmesi iptal", renewed.Count);
        return renewed.Select(c => c.LicenseId).ToList();
    }
```

Görev 1'in "Görev 14 ile" işaretlerini kaldır (bu görev onları getiriyor): `DisableEndedLicensesAsync` özetinde "(hizmet sonu işi Görev 14 ile)" → "ve hizmet sonu işi (<c>BankServiceEndPurgeJob</c>)"; "Lisans uzatılınca bağlantı kendiliğinden açılmaz … plan → Prod adımları." paragrafı şuna döner: `<para>Lisans uzatma (<c>AdminLicensesController.Extend</c>) bağlantıya dokunmaz; yenilenen lisansı <see cref="ReviveRenewedLicensesAsync"/> tanır.</para>`. `LicenseActiveAt` özetinde "Görev 13/14 ile gelecek lisans denetimleri (…) de bunu kullanır" → "lisans denetimleri (<see cref="IsLicenseActiveAsync"/>, <see cref="ReviveRenewedLicensesAsync"/>, hizmet sonu silmesi) de bunu kullanır".

- [ ] **Adım 4: İşi yaz**

`OrderDeck.LicenseServer/Services/Bank/BankServiceEndPurgeJob.cs`:

```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Hizmet sonu + <see cref="GracePeriod"/> banka verisi silmesi (KVKK K-6 öneri 2; İYS "ayrılış + 30 gün" kalıbı, Silme
/// Yönetmeliği md. 11/3'teki 3 ayın içinde). Günde bir:
/// <list type="number">
/// <item>Tarihsiz Disabled (kod Disabled'ı hep DisabledAt'le yazar; tarihsiz satırı ancak bir insan SQL ile yazmıştır) bugünle
/// damgalanır ve yönetici durdurması sayılır: sayaç başlar, sonraki adım onu kendiliğinden açmaz.</item>
/// <item>Lisansı yenilenen bağlantı silinmez: kendiliğinden durdurulmuşsa Unverified'a döner
/// (<see cref="ObifinConnectionService.ReviveRenewedLicensesAsync"/>), yöneticiye "Doğrula" e-postası gider.</item>
/// <item>Lisansı biten bağlantı Disabled'a çekilir (<see cref="ObifinConnectionService.DisableEndedLicensesAsync"/>): çekim
/// anahtarsız zamanlanmıyor olabilir, sayaç burada da başlasın.</item>
/// <item><c>DisabledAt</c>'i <see cref="GracePeriod"/>'u geçen, henüz işlenmemiş (<c>BankDataPurgedAt</c> boş) ve lisansı
/// yeniden geçerli olmayan (yönetici durdurduysa lisans önemsiz) bağlantının banka verisi
/// <see cref="BankConnectionTerminator.PurgeLocalDataAsync"/> ile YALNIZ YERELDE silinir ("Kaldır ve sil"in sahiplik kanıtı —
/// Obifin kimliği, yerel banka bağlantıları, IBAN'sız onaylı hesaplar — kalır). Obifin'e GİDİLMEZ:
/// <c>bankaapi/sil</c>'i yalnız yöneticinin "Kaldır ve sil"i çağırır (lisansa özgü, paylaşılan kullanıcı korumalı) — insan
/// onayı olmadan paylaşılan bir Obifin kullanıcısının altındaki bağlantı silinmesin. Bir lisansın hatası sıradakini bekletmez.</item>
/// <item>Bir şey silindiyse ya da düştüyse yöneticiye özet e-posta: her lisans için admin/obifin'de "Kaldır ve sil" ve Obifin'den
/// yazılı silme teyidi (Obifin API'sinde hareket silme ucu yok).</item>
/// <item>Obifin tarafının takibi (KVKK Ö-9 öneri 3; plan karar 21): bu koşudan önce yerelde silinmiş ama "Kaldır ve sil" bekleyen
/// bağlantılar ve yazılı silme teyidi gelmemiş Obifin kopyaları (<c>ObifinCopyDeletions</c>) her gün tek hatırlatma e-postasında;
/// biri <see cref="BankConnectionTerminator.ObifinFollowUpOverdue"/>'yu geçtiyse konu "GECİKTİ".</item>
/// </list>
/// Çekimle aynı kilit (<see cref="ObifinPollJob.LockResource"/>): silme ile çekim yarışmaz. <see cref="BankHasher"/> istemez;
/// anahtardan bağımsız HEP zamanlanır.
/// </summary>
[DisableConcurrentExecution(ObifinPollJob.LockResource, 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class BankServiceEndPurgeJob
{
    public const string JobId = "bank-service-end-purge";
    /// <summary>05:17 UTC: 5 dakikalık ızgaranın ve diğer günlük işlerin dışında.</summary>
    public const string Cron = "17 5 * * *";
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(30);
    public const string DoneSubject = "[OrderDeck] Hizmet sonu banka silmesi yapıldı";
    public const string FailedSubject = "[OrderDeck] Hizmet sonu banka silmesi düştü";
    public const string RenewedSubject = "[OrderDeck] Lisans yenilendi: banka bağlantısını doğrulayın";
    public const string ObifinPendingSubject = "[OrderDeck] Obifin tarafı bekliyor: Kaldır ve sil / yazılı silme teyidi";
    public const string ObifinOverdueSubject = "[OrderDeck] GECİKTİ: Obifin tarafı süresini geçti";

    private readonly LicenseDbContext _db;
    private readonly BankConnectionTerminator _terminator;
    private readonly AdminJobAlerts _alerts;
    private readonly ILogger<BankServiceEndPurgeJob> _log;

    public BankServiceEndPurgeJob(LicenseDbContext db, BankConnectionTerminator terminator, AdminJobAlerts alerts,
        ILogger<BankServiceEndPurgeJob> log)
    { _db = db; _terminator = terminator; _alerts = alerts; _log = log; }

    /// <summary>Verisi silinen lisans sayısı.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        // Tarihsiz Disabled: P0'dan önce elle (SQL ile) durdurulmuş satır. Görev 1 zaten Disabled olanı atlar, tarih yazmaz;
        // damgalanmasa hizmet sonu silmesine hiç girmezdi. Yazan bir insandır: yönetici durdurması sayılır, aşağıda açılmaz.
        var undated = await _db.ObifinConnections
            .Where(c => c.Status == ObifinConnectionStatus.Disabled && c.DisabledAt == null).ToListAsync(ct);
        if (undated.Count > 0)
        {
            foreach (var c in undated) { c.DisabledAt = now; c.DisabledByAdmin = true; c.UpdatedAt = now; }
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("Hizmet sonu: tarihsiz {Count} Disabled bağlantı bugünle damgalandı, yönetici durdurması sayıldı — " +
                "lisanslar={LicenseIds}", undated.Count, string.Join(",", undated.Select(c => c.LicenseId)));
        }
        var renewed = await ObifinConnectionService.ReviveRenewedLicensesAsync(_db, now, _log, ct);
        if (renewed.Count > 0)
            await _alerts.SendAsync(RenewedSubject, string.Join("\n", renewed.Select(id =>
                $"lisans {id}: lisans yeniden geçerli; hizmet sonu silmesi iptal, bağlantı Unverified. admin/obifin'de Doğrula'ya basın."))
                + "\n\nYöneticinin durdurduğu bağlantılar kendiliğinden açılmaz.", CancellationToken.None);
        await ObifinConnectionService.DisableEndedLicensesAsync(_db, now, _log, ct);
        // Obifin tarafının takibi (KVKK Ö-9 öneri 3, karar 21): bu koşudan ÖNCE yerelde silinmiş, "Kaldır ve sil" bekleyen
        // bağlantılar ve yazılı silme teyidi gelmemiş Obifin kopyaları. Silmeden önce okunur: bu koşuda silinenler "yapıldı"
        // e-postasında; yenilenen lisansın işareti yukarıda boşaldı.
        var awaitingRemoval = await _db.ObifinConnections.AsNoTracking().Where(c => c.BankDataPurgedAt != null)
            .Select(c => new { c.LicenseId, Since = c.BankDataPurgedAt!.Value }).ToListAsync(ct);
        var awaitingConfirmation = await _db.ObifinCopyDeletions.AsNoTracking().Where(d => d.ConfirmedAt == null)
            .Select(d => new { d.LicenseId, Since = d.RequestedAt }).ToListAsync(ct);
        var cutoff = now - GracePeriod;
        var activeLicenseIds = _db.Licenses.Where(ObifinConnectionService.LicenseActiveAt(now)).Select(l => l.Id);
        var due = await _db.ObifinConnections.AsNoTracking()
            .Where(c => c.Status == ObifinConnectionStatus.Disabled && c.DisabledAt != null && c.DisabledAt <= cutoff
                && c.BankDataPurgedAt == null
                // Savunma (yenilenme adımının kuralı, eşzamanlı bir uzatmaya karşı): lisansı bugün geçerli ve yönetici
                // durdurmamışsa silinmez.
                && (c.DisabledByAdmin || !activeLicenseIds.Contains(c.LicenseId)))
            .Select(c => c.LicenseId).ToListAsync(ct);
        var deleted = 0;
        var failures = new List<Exception>();
        var notes = new List<string>();
        foreach (var licenseId in due)
        {
            try
            {
                var r = await _terminator.PurgeLocalDataAsync(licenseId, DataDestruction.Triggers.ServiceEnd,
                    DataDestruction.SystemActor, ct);
                deleted++;
                notes.Add($"lisans {licenseId}: {r.Transactions} hareket, {r.Matches} eşleşme, {r.Memories} IBAN bağı, " +
                    $"{r.Accounts} onaysız hesap yerelde silindi; {r.AccountsScrubbed} onaylı hesabın IBAN'ı boşaltıldı (satır " +
                    $"'Kaldır ve sil'e kadar kalır). {r.ManualActionText()}");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Hizmet sonu banka silmesi düştü — lisans={LicenseId}", licenseId);
                failures.Add(ex);
                notes.Add($"lisans {licenseId}: DÜŞTÜ ({ex.GetType().Name})");
            }
        }
        if (notes.Count > 0)
            await _alerts.SendAsync(failures.Count > 0 ? FailedSubject : DoneSubject,
                string.Join("\n", notes) + "\n\nObifin'e gidilmedi. Her lisans için admin/obifin'de 'Kaldır ve sil'e basın (paylaşılan " +
                "Obifin kullanıcısı ya da sahibi doğrulanamayan bağlantı varsa sayfa söyler) ve Obifin'den hareket kopyasının " +
                "silindiğine dair YAZILI teyit isteyin (Obifin API'sinde hareket silme ucu yok).", CancellationToken.None);
        if (awaitingRemoval.Count + awaitingConfirmation.Count > 0)
        {
            var overdueAt = now - BankConnectionTerminator.ObifinFollowUpOverdue;
            string Line(Guid licenseId, DateTimeOffset since, string what)
                => $"lisans {licenseId}: {what} — {since.UtcDateTime:yyyy-MM-dd}'den beri{(since < overdueAt ? " (GECİKTİ)" : "")}";
            var lines = awaitingRemoval.Select(x => Line(x.LicenseId, x.Since,
                    "banka verisi yerelde silindi, admin/obifin'de 'Kaldır ve sil' bekliyor"))
                .Concat(awaitingConfirmation.Select(x => Line(x.LicenseId, x.Since,
                    "Obifin'den yazılı silme teyidi bekleniyor (admin/obifin → Teyit alındı)")));
            var overdue = awaitingRemoval.Any(x => x.Since < overdueAt) || awaitingConfirmation.Any(x => x.Since < overdueAt);
            await _alerts.SendAsync(overdue ? ObifinOverdueSubject : ObifinPendingSubject, string.Join("\n", lines) +
                "\n\nObifin kendi kopyasını silip yazılı teyit verene kadar bankadan çekmeyi ve saklamayı sürdürebilir. Bu " +
                $"hatırlatma her gün gelir; {BankConnectionTerminator.ObifinFollowUpOverdue.TotalDays:0} günü geçen satır gecikmiş " +
                "işaretlenir: Obifin'i yeniden yazılı uyarın.", CancellationToken.None);
        }
        if (failures.Count > 0)
            throw new AggregateException($"Hizmet sonu banka silmesi {failures.Count} lisansta düştü.", failures);
        // Başarılı koşunun kaydı, silinecek lisans olmasa da (Görev 9 incelemesi; iç imha prosedürü §4.2-3).
        DataDestruction.RecordRun(_db, JobId, "durdurma+30g");
        await _db.SaveChangesAsync(ct);
        return deleted;
    }
}
```

- [ ] **Adım 5: DI ve takvim**

`Program.cs` — `builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.BankMatchSweepJob>();` satırının ALTINA:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.BankServiceEndPurgeJob>();
```

`ScheduleBankJobs` içinde `bank-data-retention` kaydının ALTINA (`if (!enabled)`'dan ÖNCE):

```csharp
        // Hizmet sonu + 30 gün silme (KVKK K-6): BankHasher istemez, anahtarsız da kaydolur. 05:17 UTC, ızgara dışı.
        manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Bank.BankServiceEndPurgeJob>(
            OrderDeck.LicenseServer.Services.Bank.BankServiceEndPurgeJob.JobId,
            j => j.RunAsync(CancellationToken.None),
            OrderDeck.LicenseServer.Services.Bank.BankServiceEndPurgeJob.Cron);
```

`ScheduleBankJobs` özetine bir paragraf: `/// <para><c>bank-service-end-purge</c> 05:17'de, anahtardan bağımsız: hizmet sonu silmesi KVKK yükümlülüğü. Yalnız yerel siler; Obifin'e gitmez.</para>`

- [ ] **Adım 6: Takvim testlerini güncelle**

`BankJobsDiTests.cs`:
- `Banka_isleri_ve_servisleri_DI_kapsamindan_cozulur`'a ekle:
```csharp
        sp.GetRequiredService<BankServiceEndPurgeJob>().Should().NotBeNull();
        sp.GetRequiredService<BankConnectionTerminator>().Should().NotBeNull();
```
- `Anahtar_gecerliyse_dort_banka_isi_UTC_takvimiyle_kaydolur` → adı `Anahtar_gecerliyse_bes_banka_isi_UTC_takvimiyle_kaydolur`; beklenen diziye `("bank-service-end-purge", "17 5 * * *"),` ekle.
- `Anahtar_gecersizse_Obifin_isleri_kaydolmaz_eskileri_silinir_saklama_isi_yine_kaydolur` içindeki
```csharp
        var job = connection.GetRecurringJobs().Should().ContainSingle().Subject;
        job.Id.Should().Be("bank-data-retention");
        job.Cron.Should().Be("57 4 * * *");
        job.TimeZoneId.Should().Be("UTC");
```
şununla değiştir:
```csharp
        connection.GetRecurringJobs().Select(j => (j.Id, j.Cron, j.TimeZoneId)).Should().BeEquivalentTo(new[]
        {
            ("bank-data-retention", "57 4 * * *", "UTC"),
            // Hizmet sonu silmesi de KVKK yükümlülüğü; BankHasher istemez.
            ("bank-service-end-purge", "17 5 * * *", "UTC"),
        });
```

- [ ] **Adım 7: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankServiceEndPurgeJobTests|FullyQualifiedName~BankConnectionTerminatorTests|FullyQualifiedName~BankJobsDiTests"`
Expected: PASS.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/BankServiceEndPurgeJob.cs OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankServiceEndPurgeJobTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankJobsDiTests.cs
git commit -m "feat(bank-kvkk): hizmet sonu + 30 gün banka verisini silen günlük iş ekle

bank-service-end-purge (05:17 UTC, anahtardan bağımsız): elle durdurulmuş
tarihsiz bağlantıyı bugünle damgalar (yönetici durdurması sayılır); lisansı
yenilenen bağlantıyı silmez, Unverified'a çeker ve yöneticiyi uyarır; lisansı biteni
durdurur; 30 günü geçenin banka verisini yalnız yerelde siler, Obifin'e
gitmez (bankaapi/sil yalnız yöneticinin Kaldır ve sil'inde); yöneticiye
özet gönderir. Kaldır ve sil bekleyen bağlantıları ve Obifin'in yazılı
silme teyidi gelmemiş kopyalarını her gün hatırlatır, 14 günü geçeni
GECİKTİ diye işaretler (KVKK ön inceleme K-6, Ö-9, P1-10).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 15: KVKK silmesi banka verisini de temizler (P1-8)

**Neden (not K-7):** Shopper KVKK silmesi yalnız IBAN hafızasını siliyor ve kanıt metnini boşaltıyor; aynı kişiye bağlı hareketlerin açıklaması, gönderen adı ve ham kaydı 30/180. güne kadar kalıyor; eşleştirme satırında müşteri kimlikleri duruyor. Kurul 2021/670: "ilk periyodik imhada sileriz" cevabı yetersiz. Genişletilen 3b adımı:
- Kişiye bağlı eşleşme satırı (öneri ya da gerçek müşteri = silinen kişi): müşteri kimliği ve kanıt silinir; satır ölçüm kaydı olarak kalır. Kararsız öneri "öneri yok"a iner; öneriden farklı müşteriye elle verilmiş karar (ManualOnly + öneri) ölçümde çelişki sayıldığı için durum `Contradicted` olur (sonuç kaybolmasın — `PaymentMatchMetrics.Classify`).
- Bu satırların hareketleri ile **silinen IBAN hafızası özetinden gelen diğer hareketler**: açıklama, gönderen adı, ham kayıt silinir. Tutar, tarih, kısaltılmış IBAN ve tanıma kodu kalır (karar 6).
- Kanıtında silinen kişinin kullanıcı adı anahtarı geçen ama başka müşteriye bağlı ya da önerisiz satır (`named`): kanıt boşaltılır **ve hareketinin açıklaması, gönderen adı, ham kaydı da silinir** — kanıtın kaynağı o açıklamadır, silinen kişinin kullanıcı adını taşır; yalnız kanıt boşaltılsaydı açıklama, satır başka müşteriye bağlıysa 180 güne kadar kalırdı (K-7: 30 gün içinde silme). Satırdaki başka müşterinin kimliği ve kararı kalır. Anahtar kanıtta **tam** aranır, önekle değil (uygulama notundaki "Kanıtta tam anahtar"): silme geri dönüşsüz olduğu için başka müşterinin hareketine yalnız kanıtı silinen kişinin anahtarını taşıyorsa dokunulur.
- İmha kayıtları (tetikleyici `shopper-purge`, `SubjectId` = ShopperId) aynı işlemde. Purge'ün banka dışı adımları da (Shopper satırı, ödemeler, yayıncı kopyaları, bağlı satırlar, dekontlar) birer kayıt yazar: banka verisi olmayan kişinin silmesi de veritabanı dışındaki özete (Görev 9) girer ve geri yüklemeden sonra yeniden uygulanabilir.
- **Dokunulmayanlar (avukat kararı):** `WpfCustomerProjection.ScrubPersonal`'ın kullanıcı adını bırakması; `Payment.ShopperId` bağı.

Ortak kurallar yeni `BankSubjectScrub`'da; Görev 17'deki IBAN talep aracı da aynılarını kullanır. Purge'ün mevcut tek yeniden deneme kalıbı (eşleşme jetonu çakışırsa satırları tazele, kuralı yeniden uygula) korunur.

**Uygulama notu (Görev 15 ve incelemesi, 21.10):** kod aşağıdaki adımlardan şu noktalarda ayrılır; sonraki görevler (özellikle 17 ve 18) koddakine göre yazılır.
- **İmza:** `PurgeAsync(shopperId, ct)` (yapan `DataDestruction.AdminActor`, talep yok; testler ve eski çağıranlar) ve `PurgeAsync(shopperId, actor, requestId, ct)`. Silme talepleri sayfası `DataDestruction.ActorOf(User)` ve `request.Id` geçirir. Her imha kaydında `SubjectId` = ShopperId, `RequestId` = talep (Ö-13'ün alan listesi; süresiz kayıt başvuruya birleştirmesiz bağlanır), yapan = yöneticinin takma kimliği, ölçüt `ShopperPurgeService.PurgeCriterion` ("silme talebi").
- **Kayıtlar tablo tablo ve lisans başına** (Görev 9 kuralı; planın toplu `ShopperDependents` kaydı yok, sıfır satır yazılmaz): `Shoppers.FullName+Phone+Address+Email+TcProtected`, `ShopperPushDevices`, `ShopperRefreshTokens`, `ShopperPasswordResetCodes`, `ShopperSupportRequests` (lisanssız); `Payments.PersonalFields`, `ShopperPaymentMedia` (`ShopperPurgeService.MediaTarget`; ödemesi bilinmeyen kuyruk satırının nesnesi lisanssız), `PaymentSubmissionAudits.IpAddress+UserAgent` (yalnız parmak izi hâlâ dolu satırlar: 90 günlük işin boşalttığı sayılmaz), `WpfCustomerProjections.PersonalFields`, `Orders.DisplayName+MessageText` (yalnız `ScrubOrderTextOnPurge` açıkken), `ShopperBroadcasterLinks`, `CustomerIbanMemories`, `PaymentMatches.WpfCustomerId+Evidence` (kişiye bağlı satır), `PaymentMatches.Evidence` (named satır: yalnız kanıt), `BankTransactions.Description+CounterpartyName+RawJson`. IBAN hafızası `DependentRowsDeleted`'e de girer ama kayıtta yalnız kendi satırında sayılır.
- **Geciken dekont silmesi de kayıt yazar:** purge'de depodan silinemeyen nesneyi sonradan `OrphanedMediaCleanupJob` siler; gerçek silmeyle (`DeletedAt`) aynı SaveChanges'te `ShopperPaymentMedia` kaydı yazar: tetikleyici `shopper-purge`, ölçüt `PurgeCriterion`, konu kuyruk satırının ShopperId'si, lisans ödemeden (yoksa boş), yapan `system`. Talep kimliği kuyruk satırında yok; kayıt talebe `SubjectId` → `ShopperDeletionRequests.ShopperId` ile bağlanır.
- **`BankSubjectScrub` yardımcıları:** `HasText(tx)`: açıklama, ad ya da ham kayıt hâlâ dolu mu. Saklama kuralının çoktan boşalttığı hareket "silindi" diye sayılmaz: `BankTransactionsScrubbed` ve imha kaydı yalnız metni dolu hareketleri sayar (ilk silmenin `DescriptionPurgedAt`'i kalır). `References(match, id)`: satır o kimliğe bağlı mı (öneri ya da gerçek müşteri); kayıt kişiye bağlı satırı (`WpfCustomerId+Evidence`) yalnız kanıtı boşalanından (`Evidence`) bununla ayırır, sayım kuraldan ÖNCE yapılır (kural kimlikleri boşaltır). `MatchForCustomer` gerçek müşteriyi boşaltırken `IbanConfirmedAt`'i de boşaltır (Görev 10 kuralı: kararı boşaltan yol işareti de boşaltır; kişinin IBAN hafızası zaten silinir). Satır o kimliğe bağlı değilse `MatchEvidence` ile aynıdır, kişinin birden çok kimliği için sırayla çağrılabilir.
- **Kişinin bütün bağları:** 3b kişinin lisanstaki her `WpfCustomerId`'si için koşar (aynı lisansta Instagram + TikTok bağı; bir satır ikisini birden taşıyabilir). `ScrubMatch` satırın lisansındaki her silinen kimliğe `MatchForCustomer` uygular; lisansında silinen kimlik yoksa (yalnız named) `MatchEvidence`. Kişiye bağlı ve named satırlar tek sorguyla okunur.
- **Yeniden deneme hareketleri de tazeler:** hareketin jetonu yok, ama metni boşaltılan hareket okunduktan sonra başka yoldan silinebilir (saklama işi, hesap reddi, "Kaldır ve sil"; eşleşmesi FK kaskadıyla gider) ve UPDATE 0 satır etkiler. Ayrılan atlanır, kalanın metni yeniden boşaltılır. Sipariş metni (3a) de aynı kalıpta. İmha kaydının sayısı ilk okumadandır.
- **Kullanıcı adı anahtarları `MarkPurged`'dan ÖNCE alınır** (`usernames`, adım 3): "kullanıcı adı da boşaltılsın" avukat kararı `ScrubPersonal`'a tek satır olarak girerse named araması sessizce durmaz (Kapsam dışı tablosundaki "kod tek satır" notu bu sıraya dayanır).
- **Silme talepleri sayfasının "Sil" formu çalışmıyordu:** sayfa numarası Razor Pages'in ayrılmış `page` rota değeriyle taşınıyordu, form `action=""` ile çıkıyor, silme işleyiciye hiç ulaşmıyordu. Parametre `pageNumber` oldu (`asp-route-pageNumber`, `RedirectToPage(new { handled, pageNumber })`). `AdminDeletionRequestsPageTests` bu görevde açıldı: "Sil" formunun işleyiciye gitmesi ve imha kaydında yapan + talep kimliği.
- **Karar kilidi `DecidedAt`'tir (Görev 15 incelemesi, Important; m. 4/2-b).** `MatchForCustomer` karar verilmiş satırın müşterisini boşaltır, anını (`DecidedAt`) ve sonucunu (durum) bırakır. "Karar verilmiş, dekontsuz, müşterisiz" satır yalnız bu yoldan doğar (kaldırma `DecidedAt`'i de boşaltır). Bağdaştırıcı "başka müşteriye karar verilmiş" ölçütünü yalnız `ActualWpfCustomerId != null`'dan kuruyordu: silinen X'in sayfada kararlı 250 TL'lik havalesi, ±2 gün içinde onaylanan aynı tutarlı Y dekontunun (Y'nin havalesi henüz gelmemiş) tek adayı olur, ona bağlanırdı (dekont, Actual = Y, `ManualOnly`; ölçümün `ConfirmedByHuman` sonucu ezilir, Y'nin gerçek havalesi de bir daha bağlanamaz). Planın "satır ölçüm kaydı olarak kalır" sözü tutmazdı. Artık `PaymentMatchReconciler.CandidatesAsync`'in `taken` koşulu `m.PaymentId != null || (m.DecidedAt != null && m.ActualWpfCustomerId != wpfCustomerId)`, `LinkAsync`'in bayat seçim denetimi aynı ölçütte (EF `null != X`'i SQL'de doğru çevirir). Elle eşleme zaten `DecidedAt`'e bakıyordu: müşterisi boşalan karara önce kaldırma. Testler: `PaymentMatchReconcilerTests.KVKK_silmesiyle_musterisi_bosalan_karar_baska_musterinin_dekontuna_aday_sayilmaz` (gerçek purge, ardından onay bağdaştırması ve gap çözümü) ve `PaymentMatchRaceTests.Bagdastirici_secimden_sonra_KVKK_silmesiyle_musterisi_bosalan_karara_dekont_eklemez`.
- **Kanıtta tam anahtar (Görev 15 ikinci incelemesi, Important).** named araması `m.Evidence.Contains("username=" + key)` idi: "ali" işareti başka müşterinin `username=alican` kanıtına uyuyordu. Bu görevden önce yalnız o kanıt boşalıyordu (180 günde zaten gidecekti); artık named satırın hareketinin açıklaması, gönderen adı ve ham kaydı da geri dönüşsüz silindiği için başka müşterinin havale metni silinir, elle eşlemede açıklama görünmez, satırlar da silinen kişinin `SubjectId`'siyle imha kaydına sayılırdı. Kısa anahtarda ("a") toplu silme olurdu. Kanıt biçimleri (`PaymentMatcher`) anahtarı tam taşır: `username={key}`, `username={key};iban-memory`, `conflict:username={key},iban-memory`; anahtar yalnız `[a-z0-9]`. Arama artık `EndsWith(marker) || Contains(marker + ";") || Contains(marker + ",")` (SQL'e çevrilir; Testcontainers sınıfı sorguyu gerçek SQL Server'da koşturur). Kısa anahtar için ayrı bir kapı eklenmedi: tam eşleşme toplu silmeyi zaten önler (eşleştirici 3 karakterden kısa anahtar için kanıt yazmaz; öyle bir eski kanıt varsa yalnız tam eşleşeni gider). Eşleştiricideki biçimlerin yanında purge'e işaret eden not var. Test: `Kanitta_kullanici_adi_anahtari_tam_aranir_onu_onek_olarak_tasiyan_baska_anahtara_dokunulmaz` (üç biçimin üçü: önekli anahtar dokunulmaz, tam anahtar bulunur).
- **Yeniden deneme IBAN hafızasını da kapsar (Görev 15 ikinci incelemesi).** Hareketi silen yollar (`BankConnectionTerminator`, `BankDataRetentionJob`, `ObifinConnectionService`) kişinin hafızasını da siler; silme okuduktan sonra hafıza silinirse DELETE 0 satır etkiliyor, girdi süzgece girmediği için silme 500 ile düşüyordu. Süzgece `CustomerIbanMemory` eklendi; hafızalar tazelenir (ayrılan atlanır, kalan yeniden silinir). Ters yön: tazelenen eşleşmeyi eşzamanlı yazan silinen kişiye bağlarken `LearnIbanAsync` ile IBAN'ı öğretmişse kimlik boşalıyor, bağ kalıyordu. Yeniden deneme kişinin hafızasını yeniden sorgular (`ScrubLateIbanMemoriesAsync`), yeni bağı ve özetinden gelen hareketlerin metnini (yalnız `HasText`) siler; ikisi ilk okumanın kaydından ayrı bir kayıt satırıyla yazılır, sonuca (`DependentRowsDeleted`, `BankTransactionsScrubbed`) eklenir. Testler (Testcontainers): `Eszamanli_silinen_iban_hafizasiyla_carpisan_silme_tazeleyip_tamamlanir`, `Eszamanli_baglama_silinen_kisiye_iban_ogretirse_o_bag_ve_ozetinden_gelen_hareketin_metni_de_silinir`.
- **Tekrarlanan silme yapılmamış işi kaydetmez (Görev 15 ikinci incelemesi).** Shopper satırı ve ödemeler "yalnız gerçekten silineni say" kuralının dışındaydı: ikinci silmede ödemeler `Payment.ShopperId` bağıyla yeniden bulunur, aynı `RequestId`'le ikinci kayıt kümesi yazılırdı. Sayımlar boşaltmadan önce alınır: Shopper kaydı yalnız hedefteki alanlardan biri doluysa, ödeme yalnız kişisel alanı (ad, alıcı IBAN/adı, dekont anahtarı/türü) doluysa. Ölçüt `DeletedAt` **değil**: uygulamadan hesabını silen kişide talep anında dolar (`ShopperMeController.DeleteMe`), veri ise purge'de gider. `PaymentsScrubbed` (not ve denetim satırı) değişmedi. Sayfada talebi atomik sahiplenme eklenmedi: "Sil" formu `confirm()` ile çift tıklamayı keser; eşzamanlı iki POST'ta jeton olmadığı için iki silme de kaydedilebilir (bilinçli sınır; kapatmak `ShopperDeletionRequest.HandledAt` jetonu ve göç ister). Test: `Tekrarlanan_silme_yapilmamis_isi_imha_kaydina_yazmaz`.
- **Sonraki görev kuralları:**
  1. **Görev 17 (IBAN talep aracı) aynı `MatchForCustomer`'ı kullanır, aynı durumu doğurur.** Kilit bağdaştırıcıda kuruldu; Görev 17 ek bir şey yapmaz. Yeni bir yol "karar verilmiş satırı" tanırsa ölçüt `DecidedAt`'tir, müşteri değil. `IbanConfirmedAt`'i `MatchForCustomer` boşaltır, Görev 17 ayrıca yazmaz.
  2. **Görev 17'nin eşleşme kuralı satırın taşıdığı her silinen kimliğe uygulanır** (purge'ün `ScrubMatch` kalıbı): plan kodundaki `subject = Actual ∈ customers ? Actual : Proposed ∈ customers ? Proposed : null` yalnız birini boşaltır; aynı IBAN iki müşterinin hafızasındaysa ve satır ikisini taşıyorsa öneri kalırdı. `foreach (var c in customers) BankSubjectScrub.MatchForCustomer(m, c, now)` (bağlı değilse `MatchEvidence` ile aynı), satırda hiçbiri yoksa `MatchEvidence`.
  3. **Görev 17'nin sayımları purge'ünkiyle aynı kuralla:** hareket sayısı ve kaydı yalnız `BankSubjectScrub.HasText` olanlar; eşleşme kaydı `References` ile ikiye ayrılır (`PaymentMatches.WpfCustomerId+Evidence` / `PaymentMatches.Evidence`), sayım kuraldan önce. `Outcome` metnindeki sayılar da bunlar.
  4. **Görev 18:** `AdminDeletionRequestsPageTests.cs` VAR. Görev 18'in "(YENİ)" satırı ve dosyanın tamamını veren bloğu uygulanmaz: son tarih testi mevcut sınıfa EKLENİR (Görev 18'deki blok buna göre düzeltildi), mevcut iki test kalır. Sayfa numarası parametresi `pageNumber`'dır, `page` değil: yeni bağlantı ve yönlendirmeler de onu kullanır.
  5. **Görev 20: imha kaydı hedefleri çoğul DbSet adıyla** (repodaki kural: `CustomerIbanMemories`, `BankTransactions.…`, `AuditLogs.AdminUsername+IpAddress`). Aynı sütunun imhası tek hedef adıyla gruplanmalı: dış özet, geri yükleme prosedürü ve avukat paketi hedefe göre gruplar. Görev 20'nin hedefleri: `ShopperDeletionRequests.PhoneAtRequest`, `BankSubjectRequests.IbanMasked`, `AuditLogs.IpAddress`, `RefreshTokens.CreatedByIp`, `ShopperRefreshTokens.CreatedByIp`, `IntakeFormSubmissions.IpAddress+UserAgent`; ödeme denetiminin parmak izi purge'ünkiyle aynı sabitten: `ShopperPurgeService.SubmissionAuditFingerprintTarget`. Günlük (log) metinleri ayrı kalabilir. Görev 20'nin blokları buna göre düzeltildi.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Bank/BankSubjectScrub.cs`
- Modify: `OrderDeck.LicenseServer/Services/Shoppers/ShopperPurgeService.cs:13-39` (sonuç kaydı), `:243-279` (3b), `:326-333` (dönüş), `:352-390` (`SaveAsync`)
- Test: `OrderDeck.LicenseServer.Tests/Services/Shoppers/ShopperPurgeServiceTests.cs`

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`ShopperPurgeServiceTests.cs` — üste `using OrderDeck.LicenseServer.Services.Privacy;` ekle; sınıfın sonuna:

```csharp
    /// <summary>Silinen kişiye bağlı shopper bağı + projeksiyon; banka satırları için yardımcı.</summary>
    private static (Shopper Shopper, License License, Guid PurgedId) SeedLinkedShopper(LicenseDbContext db)
    {
        var shopper = SeedShopper(db);
        var license = SeedLicense(db);
        var purgedId = Guid.NewGuid();
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(), ShopperId = shopper.Id, LicenseId = license.Id, Platform = "instagram", Username = "ornek_m",
            WpfCustomerId = purgedId, JoinedAt = DateTimeOffset.UtcNow,
        });
        db.WpfCustomerProjections.Add(new WpfCustomerProjection { Id = purgedId, LicenseId = license.Id, Platform = "instagram",
            Username = "ornek_m", UpdatedAt = DateTimeOffset.UtcNow.AddDays(-3) });
        return (shopper, license, purgedId);
    }

    private static BankTransaction BankTx(License license, string? ibanHash = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = license.Id, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 6,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 250m, Currency = "TL",
        OccurredAt = DateTimeOffset.UtcNow.AddDays(-2), FetchedAt = DateTimeOffset.UtcNow.AddDays(-2),
        Description = "HAVALE ornek_m", CounterpartyName = "Ad Soyad", RawJson = """{"Aciklama":"HAVALE ornek_m"}""",
        CounterpartyIbanHash = ibanHash, CounterpartyIbanMasked = ibanHash is null ? null : "TR12…345",
    };

    /// <summary>Kanıt BİLEREK kullanıcı adı anahtarı taşımaz: "username=…" işaretiyle eşleşen satır kimlikten bağımsız
    /// boşaltılırdı ve başka müşterinin satırına dokunulmadığı sınanamazdı.</summary>
    private static PaymentMatch BankMatch(BankTransaction tx, PaymentMatchStatus status, Guid? proposed, Guid? actual) => new()
    {
        Id = Guid.NewGuid(), LicenseId = tx.LicenseId, BankTransactionId = tx.Id, ProposedWpfCustomerId = proposed,
        ActualWpfCustomerId = actual, Status = status, Layer = PaymentMatchLayer.IbanMemory, Confidence = 0.9m,
        Evidence = "iban-memory", DecidedAt = actual is null ? null : DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2),
    };

    [Fact]
    public async Task Kisiye_bagli_hareketlerin_metni_silinir_eslesme_kimlikleri_bosaltilir_olcum_sonucu_kalir()
    {
        // KVKK K-7: silme talebi banka verisinde de uygulanır; satırlar ölçüm kaydı olarak kalır.
        using var db = NewDb();
        var (shopper, license, purgedId) = SeedLinkedShopper(db);
        var otherId = Guid.NewGuid();
        var confirmedTx = BankTx(license); var proposedTx = BankTx(license); var manualTx = BankTx(license); var otherTx = BankTx(license);
        var confirmed = BankMatch(confirmedTx, PaymentMatchStatus.ConfirmedByHuman, purgedId, purgedId);
        var proposed = BankMatch(proposedTx, PaymentMatchStatus.Proposed, purgedId, null);
        var manualOther = BankMatch(manualTx, PaymentMatchStatus.ManualOnly, purgedId, otherId);
        var other = BankMatch(otherTx, PaymentMatchStatus.ConfirmedByHuman, otherId, otherId);
        db.BankTransactions.AddRange(confirmedTx, proposedTx, manualTx, otherTx);
        db.PaymentMatches.AddRange(confirmed, proposed, manualOther, other);
        await db.SaveChangesAsync();

        var result = await Build(db).Service.PurgeAsync(shopper.Id, default);

        result!.BankTransactionsScrubbed.Should().Be(3);
        foreach (var id in new[] { confirmedTx.Id, proposedTx.Id, manualTx.Id })
        {
            var t = await db.BankTransactions.AsNoTracking().SingleAsync(x => x.Id == id);
            t.Description.Should().BeNull(); t.CounterpartyName.Should().BeNull(); t.RawJson.Should().BeNull();
            t.DescriptionPurgedAt.Should().NotBeNull();
            t.Amount.Should().Be(250m, "tutar ve tarih kalır");
        }
        var matches = await db.PaymentMatches.AsNoTracking().ToDictionaryAsync(m => m.Id);
        matches[confirmed.Id].ProposedWpfCustomerId.Should().BeNull();
        matches[confirmed.Id].ActualWpfCustomerId.Should().BeNull();
        matches[confirmed.Id].Evidence.Should().BeNull();
        matches[confirmed.Id].Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman, "ölçüm sonucu durumdan okunur");
        matches[proposed.Id].Status.Should().Be(PaymentMatchStatus.NoProposal);
        matches[proposed.Id].ProposedWpfCustomerId.Should().BeNull();
        matches[manualOther.Id].Status.Should().Be(PaymentMatchStatus.Contradicted, "öneri silinse de çelişki sayılmaya devam eder");
        matches[manualOther.Id].ActualWpfCustomerId.Should().Be(otherId, "başka müşterinin kararı kalır");
        matches[other.Id].Evidence.Should().Be("iban-memory");
        (await db.BankTransactions.AsNoTracking().SingleAsync(x => x.Id == otherTx.Id)).Description.Should().Be("HAVALE ornek_m");
        (await db.DataDestructionRecords.AsNoTracking().Where(r => r.LicenseId == license.Id).ToListAsync())
            .Should().Contain(r => r.Target == "BankTransactions.Description+CounterpartyName+RawJson" && r.RowCount == 3
                && r.Trigger == DataDestruction.Triggers.ShopperPurge && r.SubjectId == shopper.Id);
    }

    [Fact]
    public async Task Kanitinda_kullanici_adi_gecen_baska_musterinin_hareketinin_metni_de_silinir()
    {
        // KVKK K-7: kanıtın kaynağı hareketin açıklamasıdır ve silinen kişinin kullanıcı adını taşır. Yalnız kanıt boşaltılsaydı
        // açıklama, satır başka müşteriye bağlı olduğu için 180 güne kadar kalırdı.
        using var db = NewDb();
        var (shopper, license, _) = SeedLinkedShopper(db);
        var otherId = Guid.NewGuid();
        var namedTx = BankTx(license);
        var named = BankMatch(namedTx, PaymentMatchStatus.ManualOnly, null, otherId);
        named.Evidence = $"conflict:username={BankTextNormalizer.UsernameKey("ornek_m")},iban-memory";
        db.BankTransactions.Add(namedTx);
        db.PaymentMatches.Add(named);
        await db.SaveChangesAsync();

        var result = await Build(db).Service.PurgeAsync(shopper.Id, default);

        result!.BankTransactionsScrubbed.Should().Be(1);
        var t = await db.BankTransactions.AsNoTracking().SingleAsync(x => x.Id == namedTx.Id);
        (t.Description, t.CounterpartyName, t.RawJson).Should().Be(((string?)null, (string?)null, (string?)null));
        t.Amount.Should().Be(250m, "tutar ve tarih kalır");
        var m = await db.PaymentMatches.AsNoTracking().SingleAsync(x => x.Id == named.Id);
        m.Evidence.Should().BeNull();
        m.ActualWpfCustomerId.Should().Be(otherId, "başka müşterinin kararı kalır");
    }

    [Fact]
    public async Task Banka_verisi_olmayan_silme_de_ShopperId_ile_imha_kaydi_yazar()
    {
        // KVKK Ö-12/Ö-13: veritabanı dışındaki saatlik özet yalnız imha kaydından beslenir; banka verisi olmayan kişinin silmesi
        // de geri yüklemeden sonra yeniden uygulanabilmeli.
        using var db = NewDb();
        var shopper = SeedShopper(db);
        await db.SaveChangesAsync();

        await Build(db).Service.PurgeAsync(shopper.Id, default);

        (await db.DataDestructionRecords.AsNoTracking().Where(r => r.SubjectId == shopper.Id).ToListAsync())
            .Should().Contain(r => r.Target == "Shoppers.FullName+Phone+Address+Email+TcProtected" && r.RowCount == 1
                && r.Trigger == DataDestruction.Triggers.ShopperPurge);
    }

    [Fact]
    public async Task Silinen_iban_hafizasinin_ozetinden_gelen_diger_hareketler_de_temizlenir()
    {
        using var db = NewDb();
        var (shopper, license, purgedId) = SeedLinkedShopper(db);
        var hash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = license.Id, WpfCustomerId = purgedId,
            IbanHash = hash, IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = DateTimeOffset.UtcNow });
        var fromIban = BankTx(license, hash);
        var unrelated = BankTx(license, $"{Guid.NewGuid():N}{Guid.NewGuid():N}");
        db.BankTransactions.AddRange(fromIban, unrelated);
        await db.SaveChangesAsync();

        await Build(db).Service.PurgeAsync(shopper.Id, default);

        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == fromIban.Id)).Description.Should().BeNull();
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == fromIban.Id)).CounterpartyIbanHash
            .Should().Be(hash, "tanıma kodu kalır — avukat kararı (plan karar 6)");
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == unrelated.Id)).Description.Should().Be("HAVALE ornek_m");
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
    }
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ShopperPurgeService"`
Expected: FAIL — `BankTransactionsScrubbed` yok.

- [ ] **Adım 3: Ortak kuralları yaz**

`OrderDeck.LicenseServer/Services/Bank/BankSubjectScrub.cs`:

```csharp
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// İlgili kişinin silme talebinde banka verisinin ortak kuralları (KVKK K-7). KVKK silmesi (ShopperPurgeService) ve IBAN ile
/// banka veri talebi aracı (BankSubjectRequestService) aynı kuralları kullanır. Yeniden uygulanabilir (idempotent); kaydetmez.
/// <para>Hareket: açıklama, gönderen adı ve ham kayıt silinir; tutar, tarih, kısaltılmış IBAN ve tanıma kodu kalır (özetin
/// silinmesi avukat kararı). Eşleşme: kişinin müşteri kimliği ve gerekçe metni silinir; satır ölçüm kaydı olarak kalır, ölçüm
/// sonucu durumdan okunur (<see cref="PaymentMatchMetrics"/>).</para>
/// </summary>
public static class BankSubjectScrub
{
    public static void Transaction(BankTransaction tx, DateTimeOffset now)
    {
        tx.Description = null;
        tx.CounterpartyName = null;
        tx.RawJson = null;
        tx.DescriptionPurgedAt ??= now;
    }

    public static void MatchForCustomer(PaymentMatch match, Guid wpfCustomerId, DateTimeOffset now)
    {
        match.Evidence = null;
        if (match.ProposedWpfCustomerId == wpfCustomerId)
        {
            match.ProposedWpfCustomerId = null;
            if (match.Status == PaymentMatchStatus.Proposed)
            {
                // Kararsız öneri kişiyle birlikte gider: satır tutarlı "öneri yok" durumuna iner.
                match.Status = PaymentMatchStatus.NoProposal;
                match.Layer = PaymentMatchLayer.None;
                match.Confidence = 0m;
            }
            else if (match.Status == PaymentMatchStatus.ManualOnly)
            {
                // Öneriden farklı müşteriye elle verilmiş karar ölçümde çelişki sayılıyordu (ManualOnly + öneri). Öneri silinince
                // sonuç kaybolmasın: Classify ikisini aynı sayar.
                match.Status = PaymentMatchStatus.Contradicted;
            }
        }
        if (match.ActualWpfCustomerId == wpfCustomerId) match.ActualWpfCustomerId = null;
        match.UpdatedAt = now;
    }

    public static void MatchEvidence(PaymentMatch match, DateTimeOffset now)
    {
        match.Evidence = null;
        match.UpdatedAt = now;
    }
}
```

- [ ] **Adım 4: Sonuç kaydını genişlet**

`ShopperPurgeService.cs` — `ShopperPurgeResult` kaydını şununla değiştir:

```csharp
public sealed record ShopperPurgeResult(
    int PaymentsScrubbed,
    int PdfsDeleted,
    int ProjectionsScrubbed,
    int DependentRowsDeleted,
    int PdfsPending,
    int BankTransactionsScrubbed = 0)
{
    /// <summary>
    /// Kovadaki her nesne gerçekten silindi mi. <c>false</c> ise silme
    /// KAPANMAMIŞ demektir; yönetim ekranı bunu toplam başarı gibi
    /// göstermemeli.
    /// </summary>
    public bool FullyCompleted => PdfsPending == 0;

    public string ToNote()
    {
        var note =
            $"{PaymentsScrubbed} ödeme temizlendi, {PdfsDeleted} dekont silindi, "
            + $"{ProjectionsScrubbed} yayıncı kopyası temizlendi, "
            + $"{DependentRowsDeleted} bağlı satır silindi.";
        if (BankTransactionsScrubbed > 0)
            note += $" {BankTransactionsScrubbed} banka hareketinin açıklaması, gönderen adı ve ham kaydı silindi.";
        if (PdfsPending > 0)
            note += $" {PdfsPending} dekont depodan silinemedi, "
                + "temizlik kuyruğunda yeniden denenecek.";
        return note;
    }
}
```

- [ ] **Adım 5: 3b adımını genişlet**

Üste `using OrderDeck.LicenseServer.Services.Privacy;` (yoksa) ekle. `// 3b. Gölge banka eşleştirmesinin izi (Obifin).` yorumuyla başlayıp `foreach (var m in scrubbedMatches) { m.Evidence = null; m.UpdatedAt = now; }` döngüsüyle biten bloğu bütünüyle şununla değiştir:

```csharp
        // 3b. Gölge banka eşleştirmesinin izi (Obifin) — KVKK K-7. IBAN hafızası "bu IBAN bu kişiyle ilişkili" bağıdır:
        //     silinir. Kişiye bağlı eşleşme satırında (öneri ya da gerçek müşteri) müşteri kimliği ve kanıt boşaltılır; satır
        //     ölçüm kaydı olarak kalır (BankSubjectScrub.MatchForCustomer). Bu satırların hareketleri, kullanıcı adı anahtarı
        //     yalnız kanıtta geçen satırların (named) hareketleri ve silinen hafıza özetinden gelen hareketlerin açıklaması,
        //     gönderen adı ve ham kaydı silinir; tutar, tarih, maske ve özet kalır (özet avukat kararı). named satırda yalnız
        //     kanıt boşaltılır, başka müşterinin kimliği kalır; hareket metni ise silinir — kanıtın kaynağı o açıklamadır ve
        //     silinen kişinin kullanıcı adını taşır. Anahtar kanıtta tam aranır (ardından son, ';' ya da ','): önek araması
        //     başka müşterinin hareketini silerdi. UpdatedAt eşleşmenin jetonudur; çakışma SaveAsync'te bir kez yeniden denenir.
        //     Kullanıcı adı (ScrubPersonal) ve Payment.ShopperId bağı avukat kararına kadar BİLEREK kalır.
        var usernames = projections.ToDictionary(c => c.Id, c => c.Username);
        var ibanMemories = new List<CustomerIbanMemory>();
        // Değer: kimliği silinecek müşteri; null = yalnız kanıt.
        var scrubbedMatches = new Dictionary<PaymentMatch, Guid?>();
        var scrubbedTransactions = new Dictionary<Guid, BankTransaction>();
        var bankCounts = new Dictionary<Guid, (int Memories, int Matches, int Transactions)>();
        foreach (var (licenseId, wpfCustomerId) in projectionKeys)
        {
            var memories = await _db.CustomerIbanMemories
                .Where(m => m.LicenseId == licenseId && m.WpfCustomerId == wpfCustomerId)
                .ToListAsync(ct);
            ibanMemories.AddRange(memories);

            Guid? id = wpfCustomerId;
            var key = usernames.TryGetValue(wpfCustomerId, out var username)
                ? BankTextNormalizer.UsernameKey(username)
                : "";
            var marker = key.Length > 0 ? "username=" + key : null;
            var linked = await _db.PaymentMatches
                .Where(m => m.LicenseId == licenseId && (m.ProposedWpfCustomerId == id || m.ActualWpfCustomerId == id))
                .ToListAsync(ct);
            var named = marker is null
                ? new List<PaymentMatch>()
                : await _db.PaymentMatches
                    .Where(m => m.LicenseId == licenseId && m.Evidence != null
                        && (m.Evidence.EndsWith(marker) || m.Evidence.Contains(marker + ";") || m.Evidence.Contains(marker + ","))
                        && m.ProposedWpfCustomerId != id && m.ActualWpfCustomerId != id)
                    .ToListAsync(ct);
            foreach (var m in linked) scrubbedMatches[m] = wpfCustomerId;
            foreach (var m in named) scrubbedMatches.TryAdd(m, null);

            var hashes = memories.Select(m => m.IbanHash).ToList();
            var matchedTxIds = linked.Concat(named).Select(m => m.BankTransactionId).ToList();
            var txs = await _db.BankTransactions
                .Where(t => t.LicenseId == licenseId && (matchedTxIds.Contains(t.Id)
                    || (t.CounterpartyIbanHash != null && hashes.Contains(t.CounterpartyIbanHash))))
                .ToListAsync(ct);
            foreach (var t in txs) scrubbedTransactions[t.Id] = t;
            var previous = bankCounts.GetValueOrDefault(licenseId);
            bankCounts[licenseId] = (previous.Memories + memories.Count, previous.Matches + linked.Count + named.Count,
                previous.Transactions + txs.Count);
        }
        _db.CustomerIbanMemories.RemoveRange(ibanMemories);
        foreach (var (m, customer) in scrubbedMatches) ScrubMatch(m, customer, now);
        foreach (var t in scrubbedTransactions.Values) BankSubjectScrub.Transaction(t, now);
        // SubjectId = ShopperId: geri yüklemeden sonra silme bununla yeniden uygulanır (Görev 9, deploy/README "Geri yükleme").
        void Record(Guid? license, string target, string action, int count) => DataDestruction.Record(_db, license, target, action,
            count, "silme talebi", DataDestruction.Triggers.ShopperPurge, DataDestruction.SystemActor, subjectId: shopperId);
        foreach (var (licenseId, counts) in bankCounts)
        {
            Record(licenseId, "CustomerIbanMemories", DataDestruction.Actions.Deleted, counts.Memories);
            Record(licenseId, "PaymentMatches.WpfCustomerId+Evidence", DataDestruction.Actions.Nulled, counts.Matches);
            Record(licenseId, "BankTransactions.Description+CounterpartyName+RawJson", DataDestruction.Actions.Nulled,
                counts.Transactions);
        }
```

`await SaveAsync(scrubbedMatches, scrubbedOrders, now, ct);` satırının ÜSTÜNE (purge'ün banka dışı adımları; aynı SaveChanges):

```csharp
        // KVKK Ö-12/Ö-13: banka verisi olmasa da silmenin kalıcı kaydı yazılır (kişisel veri yok, yalnız sayılar).
        Record(null, "Shoppers.FullName+Phone+Address+Email+TcProtected", DataDestruction.Actions.Nulled, 1);
        Record(null, "Payments.PersonalFields", DataDestruction.Actions.Nulled, payments.Count);
        Record(null, "ShopperPaymentMedia", DataDestruction.Actions.Deleted, pdfsDeleted);
        Record(null, "WpfCustomerProjections.PersonalFields", DataDestruction.Actions.Nulled, projections.Count);
        Record(null, "ShopperDependents", DataDestruction.Actions.Deleted, dependents);
```

Sınıfa (`ScrubOrderText`'in yanına) yardımcıyı ekle:

```csharp
    /// <summary>Eşleşme satırına 3b kuralını uygular: kişiye bağlıysa kimlik + kanıt, değilse yalnız kanıt.</summary>
    private static void ScrubMatch(PaymentMatch match, Guid? customer, DateTimeOffset now)
    {
        if (customer is { } c) BankSubjectScrub.MatchForCustomer(match, c, now);
        else BankSubjectScrub.MatchEvidence(match, now);
    }
```

Dönüşü şununla değiştir:

```csharp
        return new ShopperPurgeResult(
            PaymentsScrubbed: payments.Count,
            PdfsDeleted: pdfsDeleted,
            ProjectionsScrubbed: projections.Count,
            DependentRowsDeleted: dependents,
            PdfsPending: pendingRows.Count,
            BankTransactionsScrubbed: scrubbedTransactions.Count);
```

- [ ] **Adım 6: Yeniden denemeyi yeni kurala uydur**

`SaveAsync`'in imzasında `IReadOnlyCollection<PaymentMatch> scrubbedMatches` → `IReadOnlyDictionary<PaymentMatch, Guid?> scrubbedMatches` yap; `catch` koşulundaki `scrubbedMatches.Count > 0` aynı kalır; tazeleme döngüsünü:

```csharp
            foreach (var m in scrubbedMatches)
            {
                var entry = _db.Entry(m);
                await entry.ReloadAsync(ct);
                // Arada silinmiş (ayrıldı) ya da kanıtı zaten boşaltılmış.
                if (entry.State == EntityState.Detached || m.Evidence is null)
                    continue;
                m.Evidence = null;
                m.UpdatedAt = now;
            }
```
şununla değiştir:
```csharp
            foreach (var (m, customer) in scrubbedMatches)
            {
                var entry = _db.Entry(m);
                await entry.ReloadAsync(ct);
                // Arada silinmiş (ayrıldı). Tazelenen satıra kural yeniden uygulanır (idempotent).
                if (entry.State == EntityState.Detached)
                    continue;
                ScrubMatch(m, customer, now);
            }
```

`SaveAsync` özetindeki "kanıt yeniden boşaltılır" cümlesini "3b kuralı yeniden uygulanır (kişinin kimliği ve kanıt)" yap.

- [ ] **Adım 7: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ShopperPurge|FullyQualifiedName~DeletionRequest|FullyQualifiedName~PaymentMatchMetrics"`
Expected: PASS. (`ShopperPurgeServiceConcurrencyTests` Testcontainers ister; `Eszamanli_eslestirme_yazisiyla_carpisan…` testinde tazelenen satıra kural yeniden uygulanır: `PaymentId` ve `ConfirmedByHuman` korunur, kanıt boşalır — testin iddiaları geçerli kalır.)

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/BankSubjectScrub.cs OrderDeck.LicenseServer/Services/Shoppers/ShopperPurgeService.cs OrderDeck.LicenseServer.Tests/Services/Shoppers/ShopperPurgeServiceTests.cs
git commit -m "fix(kvkk): müşteri silmesinde banka hareketlerinin metnini ve eşleşme kimliklerini de sil

Silinen kişiye bağlı, kanıtında kullanıcı adı geçen ve IBAN hafızası
özetinden gelen hareketlerin açıklaması, gönderen adı ve ham kaydı;
eşleşme satırlarındaki müşteri kimlikleri ve kanıt silinir, ölçüm sonucu
korunur. Purge'ün bütün adımları ShopperId ile imha kaydı yazar
(KVKK ön inceleme K-7, Ö-13, P1-8).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 16: Banka sayfalarının görüntülenmesi denetime; denetimde kullanıcı adı yerine kimlik (P1-15, denetim kısmı)

**Neden (not Ö-4, Ö-14; Kurul 2018/10 3-c):** banka yönetim sayfalarında açıklama, kanıt ve maske görüntüleniyor ama görüntüleme loglanmıyor; elle eşleme denetim kaydına müşterinin kullanıcı adını yazıyor ve bu kayıt 24 ay duruyor, purge temizlemiyor. Artık: Banka Eşleştirme (lisans seçiliyken) ve Obifin sayfasının her görüntülenmesi `bank.page.view` olarak denetime yazılır (Görev 18'deki talep sayfası da); elle eşleme ayrıntısı kullanıcı adı yerine müşteri kayıt numarasını (`WpfCustomerId`) taşır. **Mevcut satırlar:** 28.09'dan bu yana yazılmış `bank.match.manual` satırları `{"username":…}` taşıyor ve 24 ay duruyor; `bank-data-cleanup` bunları kimliğe çevirir (eşleşme satırında gerçek müşteri varsa `{"wpfCustomerId":…}`, yoksa ayrıntı boşalır), varsayılan kuru koşu. **İki aşamalı doğrulama bu planın dışında** — takip planı ve sahibi kapsam tablosunda.

**Uygulama notu (Görev 16 ve incelemesi, 21–22.10):** kod aşağıdaki adımlardan şu noktalarda ayrılır. Metin işi, avukat paketi ve sonraki görevler (özellikle 17 ve 18) koddakine göre yazılır.
- **Görüntülemenin hedefi `AuditTargets.ObifinConnection` + lisans KİMLİĞİ, `AuditTargets.License` değil.** `license` hedefi lisans ANAHTARI taşır ve lisans sayfasının son 20 denetim kaydında listelenir: her sayfa açılışı orayı doldurur, lisansın gerçek olaylarını (uzatma, iptal) aşağı iterdi. Banka modülünün öbür denetim satırları da `obifin-connection` + lisans kimliğiyle yazılıyor. Ayrıntı yalnız sayfa adı ve sayfa numarası: `{"page":"banka-eslestirme","pageNo":N}`, `{"page":"obifin"}`. Banka Eşleştirme yalnız lisans seçili ve listede (Obifin bağlantılı) iken yazar; seçimsiz ya da listede olmayan lisansta veri gösterilmez, satır da yazılmaz (test bunu sayıyla sınar: görüntülemeler tam bir satır). Obifin sayfasında hedef **düzenleme formu açılan lisans**, yoksa boş (inceleme Minor): ham `?license=` değil, bağlantısız ya da hiç olmayan lisansın kimliği hedefe girmez. Onay isteyen kimlik kaydının yeniden basımı da (`OnPostSaveAsync` → `OnGetAsync(null, …)`) boş hedefle yazar. Testler: `AdminBankaEslestirmePageTests.Lisans_secili_sayfa_goruntulemesi_denetime_yazilir_secimsiz_sayfa_yazmaz`, `AdminObifinPageTests.Sayfa_goruntulemesi_denetime_yazilir`.
- **Eski satır kayıt numarasına kullanıcı ADINDAN çevrilir, eşleşmenin bugünkü müşterisinden değil (m. 4/2-b).** Eşleşme satırı bugünkü kararı, denetim satırı o günkü eşlemeyi gösterir. Kaldırılıp başka müşteriye verilmiş kararın müşterisi eski satıra yazılsaydı iz yanlış kişiyi gösterirdi. Ad, elle eşlemenin aramasıyla (`WpfCustomerProjection.IdentityKeyOf`) hareketin lisansındaki asıl kayıtlarda aranır; birleştirilmiş kopya kimliği asıl kayıtla taşır, kişi asıl kayıttır. Tek kayıt bulunursa o. Birden çoksa (aynı ad başka platformda) yalnız eşleşmenin bugünkü kararı denetim satırından ÖNCE verilmişse (`DecidedAt ≤ OccurredAt`) ve müşterisi onlardan biriyse o (inceleme Minor). Elle eşleme kararın anını denetimden önce yazar, kaldırma anı boşaltır, sonraki her karar daha geç bir an taşır: eşitlik bozma tahmin değil, aynı kararın kanıtıdır. Değilse hangisi olduğu tahmin edilmez.
- **Çevrilmeyen satır boşaltılır:** KVKK'yla silinmiş müşteri (silme bağı eşleşmede ve yeni biçimli denetim satırında kopardı, eski satır onu yeniden kurmamalı), bulunamayan ya da belirsiz ad, hareketi silinmiş satır (180 gün; lisansı bilinmez). İmha kaydında eylem ayrımı var: kimliğe çevirme `masked`, boşaltma `nulled`; hedef `AuditLogs.Details` (`BankSubjectScrub.ManualMatchAuditTarget`), tetikleyici `cleanup`, yapan `cli`, kayıt satırın lisansı başına (hareketi silinmiş satırınki boş).
- **`--license` koşusu yalnız o lisansın hareketini hedefleyen satırlara dokunur;** hareketi silinmiş satırların lisansı bilinmez, onları yalnız lisanssız koşu işler (Prod adımları 5). Uygulamanın önceki adımlarının sileceği hareket (çapa öncesi) kuru koşuda da silinmiş sayılır: kuru koşu uygulamayla aynı sayıyı verir. Çıktı alanı `denetimde kullanıcı adı çevrilen=`; çıktı kullanıcı adı içermez. Testler: `BankDataCleanupJobTests.Elle_eslemenin_eski_denetim_satirinda_kullanici_adi_kimlige_cevrilir_kuru_kosu_yazmaz`, `…Eski_denetim_satiri_kullanici_adindan_cevrilir_eslesmenin_bugunku_musterisinden_degil`, `…Eski_denetim_satiri_lisans_verilirse_yalniz_o_lisansta_cevrilir_kuru_kosu_uygulamayla_ayni_sayar`; gerçek SQL Server'da `BankDataCleanupRelationalTests.Elle_eslemenin_eski_denetim_satiri_kimlige_cevrilir_cikti_kullanici_adi_icermez`.
- **KVKK silmesi yeni biçimli satırı da boşaltır (inceleme Important; Ö-14, K-7).** Temizlik eski satırda silinmiş müşterinin kimliğine çevirmiyordu, ama Görev 16'dan sonra yazılan her `bank.match.manual` satırı `{"wpfCustomerId":X}` taşıyor ve purge bu satırlara dokunmuyordu. X silinince Görev 15'in eşleşmede kopardığı "X, T havalesini ödedi" bağı denetimde 24 ay kalıyordu: T'nin tutarı, tarihi ve maskeli IBAN'ı da kalır (karar 6), projeksiyon kullanıcı adını korur (avukat kararı), kimlik kişiye çözülür. K-7'nin 30 günlük silme vaadi bu satır için tutmazdı. Artık `ShopperPurgeService` adım 3c, kişinin lisanstaki her `WpfCustomerId`'si ve onlara birleştirilmiş kopyalar (`WithMergedCopiesAsync`, 3a'nın kopya izlemesiyle ortak) için bu satırların ayrıntısını boşaltır. Kopyalar dahil, çünkü elle eşleme o günkü asıl kaydı yazar; birleştirme eşleşmeyi taşır, denetim satırını taşımaz. Satır kalır: hangi yöneticinin hangi havaleyi elle eşlediği hesap verebilirlik için durur, yalnız kişinin kimliği gider. İmha kaydı silmeyle aynı SaveChanges'te: hedef `AuditLogs.Details`, eylem `nulled`, tetikleyici `shopper-purge`, `SubjectId` = ShopperId, `RequestId` = talep, lisans başına (lisans ayrıntının gösterdiği kimlikten), sayım boşaltmadan önce. Yeniden deneme kalıbı denetim satırını da kapsar: satır tazelenir, 24 aylık budamanın arada sildiği ayrılır, kalanın ayrıntısı yeniden boşaltılır. Tazelenen eşleşmeyi eşzamanlı bir elle eşleme silinen kişiye bağladıysa, sayfanın arada yazdığı denetim satırı da yeniden sorgulanıp boşaltılır (`ScrubLateManualMatchAuditsAsync`; ayrı kayıt satırı). Silmenin kaydından SONRA yazılan satır yakalanmaz. Elle eşleme silinmiş müşteriyi reddeder (`PurgedAt`), kalan pencere eşlemenin müşteri denetimiyle denetim satırının yazımı arasıdır. Kural ve ayrıntı biçimi tek yerde, `BankSubjectScrub`'da: `ManualMatchAuditDetails(id)` sayfanın AuditService'le yazdığı metnin aynısı (iki taraf testte metin olarak sabit), `ManualMatchAudits(audits, ids)` sorgusu (hedef hareket süzülmez; hareket 180 günde silinir, denetim satırı kalır; AuditLogs'ta ayrıntı indeksi yok, seyrek silmede tarama kabul) ve `ManualMatchAudit(a)`. Temizlik işi hedef adını ve biçimi buradan alır. Görev 16'dan önceki `{"username":…}` satırlarına silme dokunmaz; onları temizlik işi boşaltır, çünkü müşteri silinmiştir. Testler: `ShopperPurgeServiceTests.Elle_eslemenin_denetim_satirinda_silinen_kisinin_ve_kopyasinin_kimligi_bosaltilir`; Testcontainers'ta `ShopperPurgeServiceConcurrencyTests.Eszamanli_budanan_denetim_satiriyla_carpisan_silme_tazeleyip_tamamlanir` ve `…Eszamanli_elle_esleme_silinen_kisiye_denetim_yazarsa_yeniden_deneme_onu_da_bosaltir`.
- **Sonraki görev kuralları:**
  1. **Görev 17 (IBAN talebinin silmesi), elle eşleme denetimi.** `EraseAsync` bir eşleşmede kararın müşterisini boşaltıyorsa (`MatchForCustomer`; satırın `ActualWpfCustomerId`'si talebin müşterisi `s`), o hareketi hedefleyen ve `s`'yi gösteren elle eşleme denetim satırlarının ayrıntısını da boşaltır: `BankSubjectScrub.ManualMatchAudits(_db.AuditLogs, [s])` sorgusu `a.TargetId == tx.Id.ToString()` ile daraltılır, sonra `BankSubjectScrub.ManualMatchAudit(a)`. Yalnız o hareketin satırları, çünkü talep o IBAN'ın verisidir; kişinin başka havalelerinin satırı kalır. Sayım boşaltmadan önce yapılır, kaydı aynı SaveChanges'te yazılır: hedef `BankSubjectScrub.ManualMatchAuditTarget`, eylem `nulled`, tetikleyici `subject-request`, talep kimliğiyle. `Outcome` metnine ve `EraseResult`'a da girer. Yalnız önerisi (`Proposed`) boşalan satırın denetimi yoktur: elle eşleme kararı yazar, öneriyi yazmaz. Eşzamanlı yazıyla çakışmada Görev 17 baştan yeniden dener, kural ikinci denemede de koşar.
  2. **Görev 18 (talep sayfasının görüntülenmesi):** aynı olay (`bank.page.view`). Hedef `AuditTargets.BankSubjectRequest` + talep kimliği (Görev 18'in bloğu), `AuditTargets.License` değil. Hedef sayfanın gerçekten gösterdiği kayıttır, ham sorgu parametresi değil (Obifin incelemesi): `?id=` verilip talep bulunamazsa (`Selected` boş) hedef boş. Liste de maske gösterir; Obifin sayfası gibi her görüntüleme yazılır, talep seçili değilse hedef boş. Ayrıntıya kişisel veri girmez, yalnız sayfa adı ve talep numarası.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs`
- Modify: `OrderDeck.LicenseServer/Pages/Admin/BankaEslestirme/Index.cshtml.cs:85-130, 163-165`
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Obifin/Index.cshtml.cs` (`OnGetAsync`)
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankDataCleanupJob.cs`, `OrderDeck.LicenseServer/Tools/BankDataCleanup.cs` (eski denetim satırları)
- Test: `Pages/Admin/AdminBankaEslestirmePageTests.cs`, `Pages/Admin/AdminObifinPageTests.cs`, `Services/Bank/BankDataCleanupJobTests.cs`

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`AdminBankaEslestirmePageTests.cs` — `Liste_gosterir_elle_esler_ve_kaldirir` içindeki:

```csharp
        // Audit ayrıntısı yalnız kullanıcı adı: açıklama, tutar, IBAN girmez.
```
ve
```csharp
            .Which.Details.Should().Be("{\"username\":\"ornek_musteri34\"}");
```
satırlarını şununla değiştir:
```csharp
        // Audit ayrıntısı yalnız müşteri kayıt numarası (KVKK Ö-14): kullanıcı adı, açıklama, tutar, IBAN girmez.
```
```csharp
            .Which.Details.Should().Be($"{{\"wpfCustomerId\":\"{wpfId}\"}}");
```

Sınıfın sonuna:

```csharp
    [Fact]
    public async Task Lisans_secili_sayfa_goruntulemesi_denetime_yazilir()
    {
        var (lic, _, _) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();

        await client.GetStringAsync(PathFor(lic, pageNo: 2));

        using var scope = _factory.Services.CreateScope();
        var view = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EventType == AuditEvents.BankPageView && a.TargetId == lic.ToString());
        view.TargetType.Should().Be(AuditTargets.License);
        view.Details.Should().Be("{\"page\":\"banka-eslestirme\",\"pageNo\":2}");
    }
```

`AdminObifinPageTests.cs` sınıfının sonuna:

```csharp
    [Fact]
    public async Task Sayfa_goruntulemesi_denetime_yazilir()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var before = DateTimeOffset.UtcNow;

        await client.GetStringAsync("/admin/obifin");

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EventType == AuditEvents.BankPageView && a.OccurredAt >= before && a.Details!.Contains("\"obifin\"")))
            .Should().BeTrue();
    }
```

- [ ] **Adım 2: Koştur — kırmızı**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~AdminBankaEslestirmePageTests|FullyQualifiedName~AdminObifinPageTests"`
Expected: FAIL — `BankPageView` yok.

- [ ] **Adım 3: Uygula**

`AuditEvents.cs` — banka eşleştirme olaylarının yorumunu ve sabitlerini şununla değiştir:

```csharp
    // Gölge banka eşleştirmesi (admin/banka-eslestirme). Ayrıntıya yalnız müşteri kayıt numarası (WpfCustomerId) girer:
    // kullanıcı adı, açıklama, tutar, IBAN GİRMEZ (KVKK Ö-14).
    public const string BankMatchManual = "bank.match.manual";
    public const string BankMatchUnmatch = "bank.match.unmatch";
    // Banka yönetim sayfalarının görüntülenmesi (KVKK Ö-4; Kurul 2018/10 3-c). Ayrıntı: sayfa adı ve sayfa/talep numarası.
    public const string BankPageView = "bank.page.view";
```

`Pages/Admin/BankaEslestirme/Index.cshtml.cs` — `OnGetAsync`'te `Summary = await _metrics.ComputeAsync(LicenseId, WindowDays, ct);` satırının ALTINA:

```csharp
        // KVKK Ö-4: açıklama, kanıt ve maske gösteren sayfanın her görüntülenmesi denetime yazılır.
        await _audit.LogAsync(AuditEvents.BankPageView, AuditTargets.License, LicenseId.ToString(),
            new { page = "banka-eslestirme", pageNo = PageNo }, ct);
```

`OnPostManualMatchAsync` içindeki audit çağrısını şununla değiştir:

```csharp
        await _audit.LogAsync(AuditEvents.BankMatchManual, AuditTargets.BankTransaction, TransactionId.ToString(),
            new { wpfCustomerId = customerIds[0] }, CancellationToken.None);
```

Sınıf özetindeki "Audit ayrıntısı yalnız kullanıcı adıdır" ifadesini "Audit ayrıntısı yalnız müşteri kayıt numarasıdır (KVKK Ö-14); sayfanın görüntülenmesi de denetime yazılır" yap.

`Pages/Admin/Obifin/Index.cshtml.cs` — `OnGetAsync`'in sonuna (son `}`'dan önce):

```csharp
        // KVKK Ö-4: banka yönetim sayfasının görüntülenmesi denetime yazılır.
        await _audit.LogAsync(AuditEvents.BankPageView, AuditTargets.ObifinConnection, license?.ToString(),
            new { page = "obifin" }, ct);
```

- [ ] **Adım 4: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~AdminBankaEslestirmePageTests|FullyQualifiedName~AdminObifinPageTests|FullyQualifiedName~Audit"`
Expected: PASS.

- [ ] **Adım 5: Eski denetim satırlarındaki kullanıcı adı (test önce, kırmızı)**

`BankDataCleanupJobTests.cs` — üste `using OrderDeck.LicenseServer.Domain;` ve `using OrderDeck.LicenseServer.Services.Audit;` ekle; sınıfın sonuna:

```csharp
    [Fact]
    public async Task Elle_eslemenin_eski_denetim_satirinda_kullanici_adi_kimlige_cevrilir_kuru_kosu_yazmaz()
    {
        // KVKK Ö-14: 28.09'dan Görev 16'ya kadar bank.match.manual ayrıntısı {"username":…} taşıdı; 24 ay durur, purge
        // temizlemez. Eşleşme satırında gerçek müşteri varsa kimliğe çevrilir, yoksa ayrıntı boşalır.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var matchedTx = Tx(lic, 10m);
        db.BankTransactions.Add(matchedTx);
        db.PaymentMatches.Add(Match(matchedTx, actual: customer, decidedAt: DateTimeOffset.UtcNow));
        AuditLogEntry Audit(Guid txId, string details) => new()
        {
            Id = Guid.NewGuid(), EventType = AuditEvents.BankMatchManual, TargetType = AuditTargets.BankTransaction,
            TargetId = txId.ToString(), Details = details, OccurredAt = DateTimeOffset.UtcNow, AdminId = Guid.Empty,
            AdminUsername = "admin",
        };
        var withMatch = Audit(matchedTx.Id, "{\"username\":\"ornek_musteri34\"}");
        var orphan = Audit(Guid.NewGuid(), "{\"username\":\"ornek_musteri35\"}");   // hareket 180 günde silinmiş
        var already = Audit(matchedTx.Id, $"{{\"wpfCustomerId\":\"{customer}\"}}");
        db.AuditLogs.AddRange(withMatch, orphan, already);
        await db.SaveChangesAsync();

        (await Job(db).RunAsync(null, apply: false, CancellationToken.None)).AuditUsernamesRewritten.Should().Be(2);
        (await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == withMatch.Id)).Details.Should().Contain("ornek_musteri34",
            "kuru koşu yazmaz");

        var r = await Job(db).RunAsync(null, apply: true, CancellationToken.None);

        r.AuditUsernamesRewritten.Should().Be(2);
        (await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == withMatch.Id)).Details
            .Should().Be($"{{\"wpfCustomerId\":\"{customer}\"}}");
        (await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == orphan.Id)).Details.Should().BeNull();
        (await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == already.Id)).Details.Should().Be(already.Details);
    }
```

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataCleanupJobTests"`
Expected: FAIL — `AuditUsernamesRewritten` yok.

`Services/Bank/BankDataCleanupJob.cs` — üste `using System.Text.Json;` ve `using OrderDeck.LicenseServer.Services.Audit;` ekle. `Report`'a `public int AuditUsernamesRewritten { get; set; }`; `RunAsync`'te `ClearMaskedSourceHashesAsync` çağrısının ALTINA `await RewriteManualMatchAuditAsync(licenseId, apply, r, ct);` (log satırının sonuna `denetim kullanıcı adı={AuditUsernames}` ve `r.AuditUsernamesRewritten`). Sınıfın sonuna:

```csharp
    /// <summary>KVKK Ö-14: Görev 16'ya kadar elle eşleme denetim satırı ayrıntıya müşterinin kullanıcı adını yazdı
    /// (<c>{"username":…}</c>; 24 ay durur, purge temizlemez). Ayrıntı kimliğe çevrilir: hareketin eşleşme satırında gerçek müşteri
    /// varsa <c>{"wpfCustomerId":…}</c> (yeni kayıtların biçimi), yoksa (hareket 180 günde silinmiş) boşaltılır. Lisans verilirse
    /// yalnız o lisansın hareketini hedefleyen satırlar. Az satır: tek seferde.</summary>
    private async Task RewriteManualMatchAuditAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var rows = await _db.AuditLogs
            .Where(a => a.EventType == AuditEvents.BankMatchManual && a.Details != null && a.Details.Contains("\"username\""))
            .ToListAsync(ct);
        var txIds = rows.Select(a => Guid.TryParse(a.TargetId, out var id) ? id : Guid.Empty).Distinct().ToList();
        var owners = await _db.BankTransactions.AsNoTracking().Where(t => txIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.LicenseId, ct);
        var customers = await _db.PaymentMatches.AsNoTracking()
            .Where(m => txIds.Contains(m.BankTransactionId) && m.ActualWpfCustomerId != null)
            .ToDictionaryAsync(m => m.BankTransactionId, m => m.ActualWpfCustomerId!.Value, ct);
        // Değişen satırın lisansı (hareketi); hareketi silinmiş satırınki bilinmez (null). Kayıt lisans başına (Görev 9 incelemesi).
        var changed = new List<Guid?>();
        foreach (var a in rows)
        {
            var txId = Guid.TryParse(a.TargetId, out var id) ? id : Guid.Empty;
            Guid? rowOwner = owners.TryGetValue(txId, out var owner) ? owner : null;
            if (licenseId is { } l && rowOwner != l) continue;
            a.Details = customers.TryGetValue(txId, out var customer)
                ? JsonSerializer.Serialize(new { wpfCustomerId = customer })
                : null;
            changed.Add(rowOwner);
        }
        r.AuditUsernamesRewritten = changed.Count;
        if (apply && changed.Count > 0)
        {
            foreach (var license in changed.GroupBy(x => x))
                DataDestruction.Record(_db, license.Key, "AuditLogs.Details", Masked, license.Count(),
                    "elle eşleme denetiminde kullanıcı adı (Ö-14)", DataDestruction.Triggers.Cleanup, DataDestruction.CliActor);
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
    }
```

`Tools/BankDataCleanup.cs` — `Format`'ta `| düşen parti=` öncesine `| denetimde kullanıcı adı çevrilen={r.AuditUsernamesRewritten} ` ekle; sınıf özetine: `/// <para>Görev 16'dan önce yazılmış elle eşleme denetim satırlarındaki kullanıcı adını kimliğe çevirir (KVKK Ö-14).</para>`

Run: aynı komut. Expected: PASS. (`Ikinci_uygulama_hicbir_satiri_degistirmez` etkilenmez: denetim satırı yok.)

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs OrderDeck.LicenseServer/Pages/Admin/BankaEslestirme/Index.cshtml.cs OrderDeck.LicenseServer/Pages/Admin/Obifin/Index.cshtml.cs OrderDeck.LicenseServer/Services/Bank/BankDataCleanupJob.cs OrderDeck.LicenseServer/Tools/BankDataCleanup.cs OrderDeck.LicenseServer.Tests/Pages/Admin OrderDeck.LicenseServer.Tests/Services/Bank/BankDataCleanupJobTests.cs
git commit -m "fix(bank-kvkk): banka sayfası görüntülemesini denetle, denetime kullanıcı adı yazma

Banka Eşleştirme ve Obifin sayfalarının görüntülenmesi bank.page.view
olarak denetime yazılır; elle eşleme denetimi kullanıcı adı yerine
müşteri kayıt numarasını taşır; bank-data-cleanup eski satırlardaki
kullanıcı adını kimliğe çevirir (KVKK ön inceleme Ö-4, Ö-14, P1-15).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 17: IBAN ile banka verisi talebi — kayıt, arama, dışa aktarma, silme (P1-9a, servis)

**Neden (not K-7, Ö-11):** Shopper hesabı olmayanlar (yalnız formu dolduranlar, WPF'te kayıtlı müşteriler, müşteri olmayan karşı taraflar) için erişim ve silme aracı yok; "hakkımda hangi banka verisi var" ancak elle SQL ile cevaplanabiliyor; 30 günlük son tarih izlenmiyor. Yeni talep kaydı (`BankSubjectRequest`): lisans, tür (erişim/silme), IBAN'ın yalnız özeti ve maskesi, alış tarihi, **son tarih = alış + 30 gün**, kapanış ve kişisel veri içermeyen sonuç. IBAN sunucuda özetlenir (yalnız geçerli IBAN — Görev 11), düz saklanmaz; talep kapanınca özet de boşalır. Arama aynı lisanstaki aynı özetli hareketleri ve IBAN bağlarını bulur; dışa aktarım CSV'dir (müşteri kimliği yazılmaz); silme Görev 15'in kurallarıyla yapılır ve imha kaydı talep kimliğiyle yazılır.
- **Doğrulamanın kaydı (Ö-11, K-7 öneri 3):** başkasının havale bilgisini açıklamanın ve yanlış kişinin talebiyle veri silmenin ispat yükü veri sorumlusunda; uyarı şeridi yetmez. Dışa aktarma ve silmeden önce yönetici kimliğin ve IBAN sahipliğinin **nasıl** doğrulandığını kaydeder: kişisel veri içermeyen, zorunlu `Verification` (yöntem: ıslak imzalı dilekçe / KEP–güvenli e-imza–mobil imza / veri sorumlusunda kayıtlı iletişim — her birinde IBAN sahipliği banka belgesiyle), `VerifiedAt`, `VerifiedBy`. Kaydedilmeden dışa aktarma ve silme reddedilir.
- **Açık talep veri yokken kalmaz:** bağlantı kaldırma, hizmet sonu yerel silmesi (Görev 13–14) ve yayıncı silmesi lisansın açık taleplerini kapatır (özet boşalır, sonuç yazılır — yöneticinin başvurana yanıt vermesi gerekir). Kapanmış talebin maskesi 30 gün sonra boşalır (Görev 20).
- **Sonuç kategoridir, serbest metin değil (ikinci plan incelemesi; K-7, Ö-11; KVKK m. 13/3, Silme Yönetmeliği md. 12/1-c gerekçeli ret).** Talep yalnız iki yolla kapanıyordu: "Silindi" ya da sabit "Yanıtlandı (dışa aktarım iletildi)". Kimliği ya da IBAN sahipliği doğrulanamayan, ya da gerekçeyle (kısmen) reddedilen talebi kapatan yönetici kayda yanlış bir sonuç düşürüyordu; talep kaydı ispat değerini kaybederdi. Artık `BankSubjectResolution`: `Erased` (yalnız `EraseAsync`), `Answered` (yalnız doğrulaması kayıtlı ERİŞİM talebinde, çünkü dışa aktarım ancak orada mümkün), `RejectedNotVerified` (yalnız doğrulama kaydı OLMAYAN talepte), `RejectedLegalGround`, `Withdrawn`, `DataDeleted` (yalnız `CloseOpenForLicensesAsync`). Sonuç metni kategoriden üretilir, yönetici serbest metin yazmaz; sayfada "Reddet / kapat" seçenekleri olur ve kategori denetime yazılır (Görev 18).
- **Silme yalnız silme talebinde:** `EraseAsync` `Kind == Erasure` ister; erişim talebiyle silme yapılmaz.
- **Elle eşleme denetimi (Görev 16'nın sonraki görev kuralı 1):** silme bir eşleşmede kararın müşterisini boşaltıyorsa, o hareketin elle eşleme denetim satırında o müşterinin kimliğini de boşaltır (`BankSubjectScrub.ManualMatchAudits`), kaydını talep kimliğiyle yazar. Aşağıdaki `EraseOnceAsync` bloğu bunu henüz içermez; uygulama kurala göre yazılır.
- **CSV formül kaçışı** OWASP listesine uyar: `=`, `+`, `-`, `@`'e ek olarak sekme (`\t`) ve satır başı (`\r`) ile başlayan hücre de kesme işaretiyle başlar.
- **Yönetici sütunları kimlik taşır:** `CreatedBy`, `VerifiedBy`, `ClosedBy` yöneticinin `sub`'ı (`DataDestruction.ActorOf`), kullanıcı adı değil. Bu Görev 9'un kuralıdır (KVKK Ö-14): talep satırının saklama süresi henüz yok (karar 22), satır süresiz duruyor.

**Uygulama notu (Görev 17, 22.10):** kod aşağıdaki adımlardan şu noktalarda ayrılır; Görev 18, 20, metin işi ve avukat paketi koddakine göre yazılır.
- **Eşleşme kuralı ve sayımlar Görev 15'in kurallarıyla (kural 2 ve 3).** Talebin IBAN bağındaki müşterilerden birine bağlı satır (`References`) her müşteri için `MatchForCustomer` alır. Bağlı olmayan satırda yalnız kanıtı dolu olan `MatchEvidence` alır: boş kanıtlı satıra yazılmaz, eşleşme jetonu boşuna ilerlemez. Hareket yalnız `HasText` ise sayılır ve boşaltılır; saklama işinin ilk silme anı (`DescriptionPurgedAt`) kalır. Sayım kuraldan önce. İmha kaydı hedefleri `BankSubjectScrub`'daki ortak sabitlerden: `IbanMemoryTarget`, `TransactionTextTarget`, `LinkedMatchTarget`, `MatchEvidenceTarget`, `ManualMatchAuditTarget`. `ShopperPurgeService` de aynı sabitleri kullanır (değerler değişmedi). Ölçüt `BankSubjectRequestService.EraseCriterion` ("ilgili kişi talebi").
- **Elle eşleme denetimi (Görev 16 kuralı 1) birleştirilmiş kopyaları da kapsar.** Kararın müşterisi boşalan havalelerin `bank.match.manual` satırları boşalır. Elle eşleme o günkü asıl kaydı yazar; birleştirme eşleşmeyi ve IBAN bağını taşır, denetim satırını taşımaz. Bu yüzden müşterinin kopyalarının kimliği de aranır (purge 3c'nin gerekçesi). Kopya izleme `BankSubjectScrub.WithMergedCopiesAsync`'e taşındı, purge onu çağırır. `EraseResult`'ın dördüncü alanı `ManualMatchAudits`; `Outcome` metninde de var. Kişinin başka havalesinin satırı, başka müşterinin satırı ve IBAN bağı olmayan kararın satırı kalır.
- **`ClosedAt` eşzamanlılık jetonu** (şema değişmez; `ObifinCopyDeletion.ConfirmedAt` kalıbı). Aynı anda iki "Sil ve kapat" ya da kapatma (çift tık, iki yönetici) ikinci kez sonuç ve imha kaydı yazmıyor: ikinci silme yeniden denemede `ClosedMessage` alır. İkinci çakışma 500 değildir: izleyici boşalır, `EraseConflictMessage` döner (tek kayıt, hiçbir şey silinmedi, talep açık). `VerifyAsync` ve `CloseAsync` çakışmada `ClosedMessage` verir. Terminatörün son kaydı ve yayıncı silmesi de bu jetona takılabilir. Terminatör baştan bir kez yeniden dener; purge 409 `concurrent-credential-change` döner, operatör yeniden çağırır (idempotent).
- **`CreateAsync` ileri alış gününü reddeder** (`FutureReceivedMessage`): son tarih alıştan sayılır, ileri gün gecikmeyi gizlerdi. Lisans yoksa `LicenseNotFoundMessage`.
- **Testler adımlardakinden geniş:** karar 7 ayrımı ve kayıtların ikiye ayrılması, saklama işinin boşalttığı satır, kopya dahil denetim, iki eşzamanlılık testi, ikinci çakışma, ileri tarih (InMemory). Gerçek SQL Server'da `BankSubjectRequestRelationalTests`: CHECK kısıtı, sorgu çevirisi, ikinci eşzamanlı silmenin işlemle geri alınması (tek kayıt kümesi). Terminatör testi Theory (kaldırma / durdurulmuş bağlantıda yerel silme); yerel silmeden önce `StopAsync` (Görev 13 kuralı). `BankJobsDiTests` servisin anahtarsız da çözüldüğünü sınar.
- **Kapanışta boşalan IBAN özeti imha kaydına girer (inceleme 22.10, Important; Silme Yönetmeliği md. 7/1, Görev 9'un "her imha kayıt bırakır" kuralı).** Özet takma adlı kişisel veridir, talep kapanınca amacı biter: boşaltması süre/amaç gerekçeli bir imhadır. Karar 25'in muafiyeti (yanlış verinin düzeltmesi, m. 4/2-b) buraya uymaz. Üç kapanış yolu (silme, kapatma, `CloseOpenForLicensesAsync`) ortak `MarkClosed` adımından geçer. Kayıt: hedef `BankSubjectRequestService.IbanHashTarget` (`BankSubjectRequests.IbanHash`), eylem `nulled`, talep başına 1 satır, `RequestId` ile, kapanışla aynı SaveChanges'te (`ClosedAt` jetonu çakışırsa birlikte düşer). Ölçüt `ClosedCriterion(sonuç)` = `talep kapandı, amaç bitti (<Resolution>)`: kayıt tek başına yeniden uygulanabilir (Görev 13 kuralı), geri yüklenen açık talep aynı sonuçla yeniden kapatılır. Tetikleyici silmede ve kapatmada `subject-request`; `CloseOpenForLicensesAsync` çağıranınkini parametre olarak alır (`admin-remove`, `service-end`, `customer-purge`). Özeti zaten boş talep kayıt yazmaz. Kısaltılmış IBAN'ın 30 gün sonraki boşaltması Görev 20'de ayrıca kayda girer.
- **Sistemin kapattığı talebin sonuç metni Obifin'deki kopyayı anar (inceleme, Minor; Ö-16 kalıbı).** Yönetici başvurana bu metne göre yanıt verir; metin verinin silindiğini olduğundan kesin söylemesin. Kaldırma `BankConnectionTerminator.RemovedRequestOutcome` yazar: yalnız OrderDeck'teki veri silindi, Obifin'deki kopya için yazılı silme teyidi bekleniyor. Hizmet sonu `ServiceEndRequestOutcome` yazar: veri yalnız OrderDeck'te silindi, Obifin'deki kopya ve Obifin'in bankadan çekmesi "Kaldır ve sil" ve yazılı teyide kadar sürer. İkisi de 500 karakterin altında, "başvurana yanıt verin" ile biter. Yayıncı silmesinin metni değişmedi: banka verisinin silindiğini söylemiyor, Obifin bağlantısı varken purge zaten reddedilir.
- **Kapanan talep yöneticiye bildirilir (inceleme, Minor; m. 13/2).** Sistemin kapattığı (`DataDeleted`) talep açık listeden ve son tarih takibinden düşer, ama başvurana yanıt yükümlülüğü sürer. İki yol birlikte seçildi. (a) Kodda: `BankRemovalResult.SubjectRequestsClosed` ve `SubjectRequestsNotice()` ("N açık banka veri talebi kapatıldı — başvurana yanıt verin"). Sayı "Kaldır ve sil" bildiriminde, `obifin.connection.remove` denetim ayrıntısında (`subjectRequestsClosed`) ve hizmet sonu e-postasının lisans satırında görünür. Otomatik işte yönetici talebi yalnız bu e-postadan öğrenir. Yayıncı silmesinin `customer.purged` denetim satırı `closedBankSubjectRequests`'i taşır; yanıt 204 kalır. (b) Görev 18 için aşağıdaki kural 5: sayfa bu talepleri son tarihe kadar gösterir; purge yolunu da kapsar. Sayı, kişisel veri yok.
- **Dışa aktarım yalnız erişim talebinde (inceleme, Minor; m. 4/2-ç).** `ExportAsync` türü denetlemiyordu: silme talebinde de CSV alınabiliyordu. Silmeden önce alınan kopya sistem dışında imha kaydına girmeyen bir kopya bırakırdı, başvuran da yalnız silme istemişti. Artık silme talebinde `ExportWrongKindMessage` döner; denetim sırası kapalı → tür → doğrulama. Kopya isteyen başvurana ayrı bir erişim talebi açılır, yönetici veriyi aramayla (`FindAsync`) görür. `Answered`'ın "dışa aktarım ancak erişim talebinde" gerekçesi böylece kodla tutar.
- **Sonraki görev kuralları (Görev 18, 20, 23):**
  1. Sayfa servisin `ObifinValidationException`'larını bildirime çevirir. Eşzamanlılık da bu türle gelir (`ClosedMessage`, `EraseConflictMessage`), ayrıca `DbUpdateConcurrencyException` yakalanmaz. Silme bildirimi ve denetim ayrıntısı `r.ManualMatchAudits`'i de taşıyabilir (sayı, kişisel veri yok).
  2. `ReceivedOn` TR bugününden ileri olamaz; sayfanın varsayılanı bugün (TR) kalır.
  3. Görev 20'nin maske boşaltması (`ExecuteUpdate`) `ClosedAt`'e dokunmaz, jeton etkilenmez; InMemory yolunda da yalnız `IbanMasked` değişir.
  4. Görev 18: "CSV dışa aktar" düğmesi yalnız erişim talebinde (`Kind == Access`) görünür. Aşağıdaki sayfa testi dışa aktarımı silme talebinden yapıyor: test erişim talebiyle yazılır ve silme talebinde `ExportWrongKindMessage` bildirimini ayrıca sınar.
  5. Görev 18: sistemin kapattığı (`Resolution == DataDeleted`) ve son tarihi (`DueAt`) geçmemiş talep, varsayılan (açık) listede "yanıt bekleniyor" şeridiyle de görünür (sorgu: `ClosedAt == null || (Resolution == DataDeleted && DueAt > now)`). Satırda eylem düğmesi yok (talep kapalı), sonuç metni görünür. Kişisel veri yok.
  6. Görev 23: geri yükleme listesinin `subject-request` satırı kapatmayı da kapsar. Talebin kayıtlarında yalnız `BankSubjectRequests.IbanHash` hedefi varsa talep silinmemiş, kapatılmıştır: geri yüklenen açık talep ölçütteki sonuçla yeniden kapatılır. Hedefi `BankSubjectRequests.IbanHash` olan `admin-remove`, `service-end` ve `customer-purge` satırları kendi eylemlerinin yeniden uygulanmasıyla kapanır (aşağıdaki Görev 23 adımına yazıldı).

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/Bank/BankSubjectRequest.cs`
- Create: `OrderDeck.LicenseServer/Services/Bank/BankSubjectRequestService.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Modify: `OrderDeck.LicenseServer/Services/Bank/BankConnectionTerminator.cs`, `OrderDeck.LicenseServer/Controllers/Customers/AdminCustomersController.cs` (açık talepleri kapat)
- Modify: `OrderDeck.LicenseServer/Program.cs` (DI)
- Create: göç `*_BankSubjectRequests`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankSubjectRequestServiceTests.cs` (YENİ), `BankConnectionTerminatorTests.cs`, `Controllers/Compliance/GdprFlowTests.cs`
- Uygulama notu: Modify `Services/Bank/BankSubjectScrub.cs` (ortak imha hedefleri, `WithMergedCopiesAsync`), `Services/Shoppers/ShopperPurgeService.cs` (ortak sabitler ve kopya izleme); Test YENİ `Services/Bank/BankSubjectRequestRelationalTests.cs` (Testcontainers), `Services/Bank/BankJobsDiTests.cs`

- [ ] **Adım 1: Servis testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Bank/BankSubjectRequestServiceTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK K-7: IBAN ile banka verisi talebi. IBAN düz saklanmaz; son tarih alış + 30 gün; dışa aktarımda müşteri
/// kimliği yok; silme kişinin bağlarını ve hareket metinlerini siler, talebi kapatır.</summary>
public sealed class BankSubjectRequestServiceTests
{
    private static readonly BankHasher Hasher = new(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-subject-{Guid.NewGuid():N}").Options);

    private static Guid SeedLicense(LicenseDbContext db)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new OrderDeck.LicenseServer.Domain.Customer { Id = customerId, Email = $"s-{Guid.NewGuid():N}@x", Name = "S",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new OrderDeck.LicenseServer.Domain.License { Id = lic, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        db.SaveChanges();
        return lic;
    }

    private static BankTransaction Tx(Guid lic, string iban, string description = "HAVALE ornek") => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 6,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 125.5m, Currency = "TL",
        OccurredAt = DateTimeOffset.UtcNow.AddDays(-3), FetchedAt = DateTimeOffset.UtcNow.AddDays(-3),
        Description = description, CounterpartyName = "Ad Soyad", RawJson = """{"Aciklama":"HAVALE ornek"}""",
        CounterpartyIbanHash = Hasher.HashIban(iban), CounterpartyIbanMasked = BankHasher.MaskIban(iban),
    };

    private static BankSubjectRequestService Service(LicenseDbContext db) => new(db);

    [Fact]
    public async Task Maskeli_IBAN_ile_talep_acilmaz()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();

        var act = () => Service(db).CreateAsync(Hasher, lic, iban[..10] + "******" + iban[16..], BankSubjectRequestKind.Access,
            new DateOnly(2026, 10, 19), "admin", CancellationToken.None);

        await act.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.InvalidIbanMessage);
    }

    [Fact]
    public async Task Talep_IBANi_duz_saklamaz_son_tarih_alistan_30_gun_sonra()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();

        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Erasure, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);

        r.IbanHash.Should().Be(Hasher.HashIban(iban));
        r.IbanMasked.Should().Be(BankHasher.MaskIban(iban));
        r.ReceivedAt.Should().Be(new DateTimeOffset(2026, 10, 18, 21, 0, 0, TimeSpan.Zero), "TR gün başı");
        r.DueAt.Should().Be(r.ReceivedAt.AddDays(30));
        var json = System.Text.Json.JsonSerializer.Serialize(db.Entry(r).CurrentValues.ToObject());
        json.Should().NotContain(iban[4..20], "IBAN düz hiçbir alanda yok");
    }

    [Fact]
    public async Task Arama_ayni_lisanstaki_ayni_IBANi_bulur_baska_lisansinkini_bulmaz()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var other = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var mine = Tx(lic, iban);
        db.BankTransactions.AddRange(mine, Tx(other, iban), Tx(lic, BankHasherTests.TestIban()));
        await db.SaveChangesAsync();
        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Access, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);

        var lookup = await Service(db).FindAsync(r.Id, CancellationToken.None);

        lookup!.Transactions.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
    }

    [Fact]
    public async Task Disa_aktarim_tam_IBAN_ve_musteri_kimligi_icermez_formul_hucresi_kacislanir()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var customer = Guid.NewGuid();
        var tx = Tx(lic, iban, description: "=HYPERLINK(\"x\")");
        db.BankTransactions.Add(tx);
        db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id,
            ActualWpfCustomerId = customer, Status = PaymentMatchStatus.ManualOnly, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Access, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);
        await Service(db).VerifyAsync(r.Id, BankSubjectVerification.WetSignatureAndBankDocument, "admin", CancellationToken.None);

        var csv = Encoding.UTF8.GetString(await Service(db).ExportAsync(r.Id, CancellationToken.None));

        csv.Should().Contain(BankHasher.MaskIban(iban)).And.Contain("125.50").And.Contain("bir müşteriyle eşleşti");
        csv.Should().NotContain(iban[4..20]).And.NotContain(customer.ToString());
        csv.Should().Contain("\"'=HYPERLINK(\"\"x\"\")\"", "formül enjeksiyonu kesme işaretiyle etkisiz");
    }

    [Fact]
    public async Task Dogrulama_kaydedilmeden_disa_aktarma_ve_silme_yapilmaz_kayit_kisisel_veri_icermez()
    {
        // KVKK Ö-11/K-7: başkasının havale bilgisini açıklamamanın ve yanlış kişinin talebiyle silmemenin ispatı veri
        // sorumlusunda: yöntem kaydı zorunlu, serbest metin yok.
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        db.BankTransactions.Add(Tx(lic, iban));
        await db.SaveChangesAsync();
        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Erasure, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);

        var export = () => Service(db).ExportAsync(r.Id, CancellationToken.None);
        var erase = () => Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);
        var noMethod = () => Service(db).VerifyAsync(r.Id, BankSubjectVerification.None, "admin", CancellationToken.None);

        await export.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.NotVerifiedMessage);
        await erase.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.NotVerifiedMessage);
        await noMethod.Should().ThrowAsync<ObifinValidationException>();
        (await db.BankTransactions.AsNoTracking().SingleAsync()).Description.Should().NotBeNull("silme yapılmadı");

        await Service(db).VerifyAsync(r.Id, BankSubjectVerification.ElectronicSignatureAndBankDocument, "admin", CancellationToken.None);

        var verified = await db.BankSubjectRequests.AsNoTracking().SingleAsync();
        (verified.Verification, verified.VerifiedBy).Should().Be((BankSubjectVerification.ElectronicSignatureAndBankDocument, "admin"));
        verified.VerifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Silme_metni_ve_IBAN_bagini_siler_talebi_kapatir_ozeti_bosaltir_imha_kaydi_yazar()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        var customer = Guid.NewGuid();
        var tx = Tx(lic, iban);
        db.BankTransactions.Add(tx);
        var match = new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id, ActualWpfCustomerId = customer,
            ProposedWpfCustomerId = customer, Status = PaymentMatchStatus.ConfirmedByHuman, Evidence = "iban-memory",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.PaymentMatches.Add(match);
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customer,
            IbanHash = Hasher.HashIban(iban)!, IbanMasked = BankHasher.MaskIban(iban), LearnedFrom = IbanMemorySource.HumanApproval,
            CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Erasure, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);
        await Service(db).VerifyAsync(r.Id, BankSubjectVerification.WetSignatureAndBankDocument, "admin", CancellationToken.None);

        var result = await Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        (result.Transactions, result.IbanMemories, result.Matches).Should().Be((1, 1, 1));
        var t = await db.BankTransactions.AsNoTracking().SingleAsync();
        t.Description.Should().BeNull(); t.CounterpartyName.Should().BeNull(); t.RawJson.Should().BeNull();
        var m = await db.PaymentMatches.AsNoTracking().SingleAsync();
        m.ActualWpfCustomerId.Should().BeNull(); m.ProposedWpfCustomerId.Should().BeNull(); m.Evidence.Should().BeNull();
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
        var closed = await db.BankSubjectRequests.AsNoTracking().SingleAsync();
        closed.ClosedAt.Should().NotBeNull(); closed.IbanHash.Should().BeNull(); closed.ClosedBy.Should().Be("admin");
        closed.Resolution.Should().Be(BankSubjectResolution.Erased);
        (await db.DataDestructionRecords.AsNoTracking().Where(x => x.RequestId == r.Id).ToListAsync())
            .Should().OnlyContain(x => x.Trigger == DataDestruction.Triggers.SubjectRequest).And.HaveCount(3);

        var again = () => Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);
        await again.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.ClosedMessage);
    }

    [Fact]
    public async Task Lisansin_acik_talepleri_kapatilir_ozet_bosalir_kapali_talebe_dokunulmaz()
    {
        // Bağlantı kaldırma, hizmet sonu yerel silmesi ve yayıncı silmesi bu yardımcıyı çağırır: veri yokken açık talep ve
        // özeti kalmasın.
        using var db = NewDb(); var lic = SeedLicense(db); var other = SeedLicense(db);
        var open = await Service(db).CreateAsync(Hasher, lic, BankHasherTests.TestIban(), BankSubjectRequestKind.Access,
            new DateOnly(2026, 10, 19), "admin", CancellationToken.None);
        var otherOpen = await Service(db).CreateAsync(Hasher, other, BankHasherTests.TestIban(), BankSubjectRequestKind.Access,
            new DateOnly(2026, 10, 19), "admin", CancellationToken.None);

        var closed = await BankSubjectRequestService.CloseOpenForLicensesAsync(db, [lic], "Lisansın banka verisi silindi.",
            DataDestruction.SystemActor, DateTimeOffset.UtcNow, CancellationToken.None);
        await db.SaveChangesAsync();

        closed.Should().Be(1);
        var o = await db.BankSubjectRequests.AsNoTracking().SingleAsync(x => x.Id == open.Id);
        (o.ClosedAt is not null, o.IbanHash, o.Outcome).Should().Be((true, (string?)null, "Lisansın banka verisi silindi."));
        o.Resolution.Should().Be(BankSubjectResolution.DataDeleted);
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync(x => x.Id == otherOpen.Id)).ClosedAt.Should().BeNull();
    }

    [Fact]
    public async Task Kapatma_yalniz_kurala_uygun_kategoriyle_yapilir_sonuc_metni_kategoriden_gelir()
    {
        // KVKK m. 13/3, Silme Yönetmeliği md. 12/1-c: gerekçeli ret ayrı bir sonuç. Talep kaydına yanlış sonuç düşmesin:
        // doğrulanmamış erişim talebi "yanıtlandı", doğrulanmış talep "doğrulanamadı" kapanamaz; silme yalnız EraseAsync'te.
        using var db = NewDb(); var lic = SeedLicense(db);
        async Task<BankSubjectRequest> Open(BankSubjectRequestKind kind, bool verified)
        {
            var r = await Service(db).CreateAsync(Hasher, lic, BankHasherTests.TestIban(), kind, new DateOnly(2026, 10, 19), "admin",
                CancellationToken.None);
            if (verified)
                await Service(db).VerifyAsync(r.Id, BankSubjectVerification.WetSignatureAndBankDocument, "admin", CancellationToken.None);
            return r;
        }
        var unverifiedAccess = await Open(BankSubjectRequestKind.Access, verified: false);
        var verifiedAccess = await Open(BankSubjectRequestKind.Access, verified: true);
        var verifiedErasure = await Open(BankSubjectRequestKind.Erasure, verified: true);
        Func<Task> Close(BankSubjectRequest r, BankSubjectResolution resolution)
            => () => Service(db).CloseAsync(r.Id, resolution, "admin", CancellationToken.None);

        await Close(unverifiedAccess, BankSubjectResolution.Answered).Should().ThrowAsync<ObifinValidationException>()
            .WithMessage(BankSubjectRequestService.ResolutionNotAllowedMessage);
        await Close(verifiedErasure, BankSubjectResolution.Answered).Should().ThrowAsync<ObifinValidationException>();
        await Close(verifiedAccess, BankSubjectResolution.RejectedNotVerified).Should().ThrowAsync<ObifinValidationException>();
        await Close(verifiedErasure, BankSubjectResolution.Erased).Should().ThrowAsync<ObifinValidationException>();
        await Close(verifiedErasure, BankSubjectResolution.DataDeleted).Should().ThrowAsync<ObifinValidationException>();

        await Close(unverifiedAccess, BankSubjectResolution.RejectedNotVerified)();
        await Close(verifiedAccess, BankSubjectResolution.Answered)();
        await Close(verifiedErasure, BankSubjectResolution.RejectedLegalGround)();

        var rows = await db.BankSubjectRequests.AsNoTracking().ToDictionaryAsync(r => r.Id);
        rows[unverifiedAccess.Id].Should().Match<BankSubjectRequest>(r => r.Resolution == BankSubjectResolution.RejectedNotVerified
            && r.Outcome == BankSubjectRequestService.OutcomeText(BankSubjectResolution.RejectedNotVerified)
            && r.ClosedAt != null && r.IbanHash == null);
        rows[verifiedAccess.Id].Resolution.Should().Be(BankSubjectResolution.Answered);
        rows[verifiedErasure.Id].Resolution.Should().Be(BankSubjectResolution.RejectedLegalGround);
    }

    [Fact]
    public async Task Erisim_talebiyle_silme_yapilmaz()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var iban = BankHasherTests.TestIban();
        db.BankTransactions.Add(Tx(lic, iban));
        await db.SaveChangesAsync();
        var r = await Service(db).CreateAsync(Hasher, lic, iban, BankSubjectRequestKind.Access, new DateOnly(2026, 10, 19), "admin",
            CancellationToken.None);
        await Service(db).VerifyAsync(r.Id, BankSubjectVerification.WetSignatureAndBankDocument, "admin", CancellationToken.None);

        var erase = () => Service(db).EraseAsync(r.Id, "admin", CancellationToken.None);

        await erase.Should().ThrowAsync<ObifinValidationException>().WithMessage(BankSubjectRequestService.WrongKindMessage);
        (await db.BankTransactions.AsNoTracking().SingleAsync()).Description.Should().NotBeNull("silme yapılmadı");
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync()).ClosedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\t=1+1")]
    [InlineData("\r=1+1")]
    public void Formul_sayilabilecek_hucre_kesme_isaretiyle_baslar(string description)
    {
        // OWASP CSV injection: '=', '+', '-', '@', sekme ve satır başı ile başlayan hücre formül sayılabilir.
        var request = new BankSubjectRequest { Id = Guid.NewGuid(), IbanMasked = "TR12…345" };
        var row = new BankSubjectRequestService.TransactionRow(Guid.NewGuid(), DateTimeOffset.UtcNow, 1m, "TL", "qnb", "TR12…345",
            description, null, null, null, null, false);

        var csv = Encoding.UTF8.GetString(BankSubjectRequestService.ToCsv(new BankSubjectRequestService.Lookup(request, [row], 0)));

        csv.Should().Contain("\"'" + description + "\"");
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankSubjectRequestServiceTests"`
Expected: FAIL — tür yok.

- [ ] **Adım 3: Varlık ve model**

`OrderDeck.LicenseServer/Domain/Bank/BankSubjectRequest.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain.Bank;

public enum BankSubjectRequestKind { Access = 0, Erasure = 1 }

/// <summary>
/// Başvuranın kimliğinin ve IBAN'ın ona ait olduğunun nasıl doğrulandığı (KVKK Ö-11; Başvuru Tebliği m. 5'teki kanallar). Her
/// yöntemde IBAN sahipliği banka belgesiyle (dekont, hesap cüzdanı, banka ekstresi) görülür. Kişisel veri İÇERMEZ — yalnız
/// yöntem; belgenin kendisi kaydedilmez. Serbest metin BİLEREK yok.
/// </summary>
public enum BankSubjectVerification
{
    None = 0,
    /// <summary>Islak imzalı dilekçe + kimlik belgesi; IBAN banka belgesiyle.</summary>
    WetSignatureAndBankDocument = 1,
    /// <summary>KEP, güvenli elektronik imza ya da mobil imza; IBAN banka belgesiyle.</summary>
    ElectronicSignatureAndBankDocument = 2,
    /// <summary>Veri sorumlusunda önceden kayıtlı iletişim kanalı (ör. Shopper hesabının telefonu/e-postası); IBAN banka
    /// belgesiyle.</summary>
    RegisteredContactAndBankDocument = 3,
}

/// <summary>
/// Talebin nasıl kapandığı (KVKK m. 13/3: talep kabul edilir ya da gerekçesi açıklanarak reddedilir; Silme Yönetmeliği md.
/// 12/1-c gerekçeli ret). Kategoridir; sonuç metni koddan üretilir (<c>BankSubjectRequestService.OutcomeText</c>), serbest metin
/// YOK. Kişisel veri içermez.
/// </summary>
public enum BankSubjectResolution
{
    None = 0,
    /// <summary>Silme talebi uygulandı (yalnız <c>EraseAsync</c>).</summary>
    Erased = 1,
    /// <summary>Erişim talebi yanıtlandı, dışa aktarım başvurana iletildi (yalnız doğrulaması kayıtlı erişim talebi).</summary>
    Answered = 2,
    /// <summary>Kimlik ya da IBAN sahipliği doğrulanamadı; reddedildi (yalnız doğrulama kaydı OLMAYAN talep).</summary>
    RejectedNotVerified = 3,
    /// <summary>Kanuni gerekçeyle tamamen ya da kısmen reddedildi; gerekçe başvurana yazılı bildirildi.</summary>
    RejectedLegalGround = 4,
    /// <summary>Başvuran talebini geri çekti.</summary>
    Withdrawn = 5,
    /// <summary>Lisansın banka verisi silindiği için kapandı (bağlantı kaldırma, hizmet sonu, yayıncı silmesi; yalnız
    /// <c>CloseOpenForLicensesAsync</c>). Yönetici başvurana yine yanıt verir.</summary>
    DataDeleted = 6,
}

/// <summary>
/// Banka hareketi karşı tarafının (Shopper hesabı olsun olmasın) erişim ya da silme talebi (KVKK m. 11, m. 13/2; ön inceleme
/// K-7). Son tarih alış + 30 gün. IBAN düz saklanmaz: yalnız özet (talep kapanınca boşaltılır) ve kısaltılmış hâli (kapanıştan 30
/// gün sonra boşaltılır — SecurityDataRetentionJob). Lisansa FK BİLEREK yok (ShopperDeletionRequest kalıbı): talebin belgesi
/// lisans silinse de yaşar. Sonuç ve doğrulama yöntemi kişisel veri içermez.
/// </summary>
public sealed class BankSubjectRequest
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public BankSubjectRequestKind Kind { get; set; }
    public string? IbanHash { get; set; }
    /// <summary>Kısaltılmış IBAN; kapanıştan 30 gün sonra "" olur.</summary>
    public string IbanMasked { get; set; } = "";
    /// <summary>Dışa aktarma ve silmeden önce zorunlu (<see cref="BankSubjectVerification"/>).</summary>
    public BankSubjectVerification Verification { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    /// <summary>Yönetici sütunları (<see cref="VerifiedBy"/>, <see cref="CreatedBy"/>, <see cref="ClosedBy"/>): yöneticinin kimliği
    /// (<c>DataDestruction.ActorOf</c>: <c>sub</c>) ya da <c>system</c>; kullanıcı adı DEĞİL (KVKK Ö-14: satır süresiz, karar 22).</summary>
    public string? VerifiedBy { get; set; }
    /// <summary>Talebin OrderDeck'e ulaştığı gün (TR gün başı, UTC).</summary>
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
    /// <summary>Nasıl kapandığı (kategori); açıkken <see cref="BankSubjectResolution.None"/>.</summary>
    public BankSubjectResolution Resolution { get; set; }
    /// <summary>Sonucun metni: koddan üretilir (kategoriden ya da silmenin sayılarından), yönetici yazmaz. Kişisel veri yok.</summary>
    public string? Outcome { get; set; }
}
```

`Data/LicenseDbContext.cs` — `DataDestructionRecords` DbSet'inin ALTINA:

```csharp
    public DbSet<BankSubjectRequest> BankSubjectRequests => Set<BankSubjectRequest>();
```

`mb.Entity<DataDestructionRecord>(…)` bloğunun ALTINA:

```csharp
        // IBAN ile banka veri talebi (KVKK K-7). Lisansa FK BİLEREK yok. Özet yalnız 64 hex (diğer banka tablolarının kısıtı).
        mb.Entity<BankSubjectRequest>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(r => r.IbanHash).HasMaxLength(64);
            b.Property(r => r.IbanMasked).HasMaxLength(40).IsRequired();
            b.Property(r => r.Verification).HasConversion<string>().HasMaxLength(40).IsRequired();
            b.Property(r => r.VerifiedBy).HasMaxLength(256);
            b.Property(r => r.CreatedBy).HasMaxLength(256).IsRequired();
            b.Property(r => r.ClosedBy).HasMaxLength(256);
            b.Property(r => r.Resolution).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(r => r.Outcome).HasMaxLength(500);
            b.HasIndex(r => new { r.ClosedAt, r.DueAt });
            b.ToTable(tb => tb.HasCheckConstraint("CK_BankSubjectRequests_IbanHash_Hex64",
                "LEN([IbanHash]) = 64 AND [IbanHash] NOT LIKE '%[^0-9a-f]%'"));
        });
```

- [ ] **Adım 4: Servisi yaz**

`OrderDeck.LicenseServer/Services/Bank/BankSubjectRequestService.cs`:

```csharp
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// IBAN ile banka verisi talebi (KVKK K-7, Ö-11): kayıt, doğrulama kaydı, arama, dışa aktarma, silme, kapatma. Shopper hesabı
/// olmayanların tek yolu. Kimlik ve IBAN sahipliğinin doğrulanması yöneticinin işidir; NASIL doğrulandığı (kişisel veri içermeyen
/// yöntem) kaydedilmeden dışa aktarma ve silme yapılmaz — ispat yükü veri sorumlusunda. <see cref="BankHasher"/> yalnız talep
/// açarken gerekir (çağıran verir): modül anahtarsız kapalıyken açık talepler yine kapatılabilir ve silinebilir.
/// </summary>
public sealed class BankSubjectRequestService
{
    public const int ResponseDays = 30;
    public const string InvalidIbanMessage = "Geçerli bir IBAN girin (boşluklu yazılabilir). Kısaltılmış ya da maskeli IBAN aranamaz.";
    public const string NotFoundMessage = "Talep bulunamadı.";
    public const string ClosedMessage = "Talep kapatılmış.";
    public const string NotVerifiedMessage = "Önce başvuranın kimliğini ve IBAN'ın ona ait olduğunu doğrulayıp yöntemi kaydedin.";
    public const string NoMethodMessage = "Doğrulama yöntemini seçin.";
    public const string WrongKindMessage = "Erişim talebiyle silme yapılmaz; silme için ayrı bir silme talebi açın.";
    public const string ResolutionNotAllowedMessage = "Bu sonuç bu talebe uygulanamaz: 'Yanıtlandı' yalnız doğrulanmış erişim talebinde, " +
        "'Doğrulanamadı' yalnız doğrulama kaydı olmayan talepte seçilir; silme 'Sil ve kapat' ile yapılır.";
    /// <summary>Silmenin bildirimine eklenir: OrderDeck'in silemediği kopyalar.</summary>
    public const string OtherCopiesNotice = "Obifin'deki kopya için Obifin'e yazılı talep gönderin; bankanın kendi kayıtları bankanın sorumluluğundadır.";

    public sealed record TransactionRow(Guid Id, DateTimeOffset OccurredAt, decimal Amount, string Currency, string BankaKodu,
        string? IbanMasked, string? Description, string? CounterpartyName, string? TransactionCode, string? BankReference,
        PaymentMatchStatus? MatchStatus, bool LinkedToCustomer);

    public sealed record Lookup(BankSubjectRequest Request, IReadOnlyList<TransactionRow> Transactions, int IbanMemories);

    public sealed record EraseResult(int Transactions, int IbanMemories, int Matches);

    private readonly LicenseDbContext _db;

    public BankSubjectRequestService(LicenseDbContext db) => _db = db;

    public async Task<BankSubjectRequest> CreateAsync(BankHasher hasher, Guid licenseId, string iban, BankSubjectRequestKind kind,
        DateOnly receivedOnTr, string createdBy, CancellationToken ct)
    {
        var hash = hasher.HashIban(iban) ?? throw new ObifinValidationException(InvalidIbanMessage);
        if (!await _db.Licenses.AnyAsync(l => l.Id == licenseId, ct))
            throw new ObifinValidationException("Lisans bulunamadı.");
        var receivedAt = ObifinConnectionService.TrToUtc(receivedOnTr.ToDateTime(TimeOnly.MinValue));
        var request = new BankSubjectRequest
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Kind = kind, IbanHash = hash, IbanMasked = BankHasher.MaskIban(iban),
            ReceivedAt = receivedAt, DueAt = receivedAt.AddDays(ResponseDays), CreatedAt = DateTimeOffset.UtcNow, CreatedBy = createdBy,
        };
        _db.BankSubjectRequests.Add(request);
        await _db.SaveChangesAsync(ct);
        return request;
    }

    /// <summary>Talep + aynı lisansta aynı özetli hareketler (yeniden eskiye) + bu özetle kurulmuş IBAN bağı sayısı.</summary>
    public async Task<Lookup?> FindAsync(Guid requestId, CancellationToken ct)
    {
        var request = await _db.BankSubjectRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null) return null;
        if (request.IbanHash is null) return new Lookup(request, [], 0);
        var txs = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.LicenseId == request.LicenseId && t.CounterpartyIbanHash == request.IbanHash)
            .OrderByDescending(t => t.OccurredAt)
            .Select(t => new { t.Id, t.OccurredAt, t.Amount, t.Currency, t.BankaKodu, t.CounterpartyIbanMasked, t.Description,
                t.CounterpartyName, t.TransactionCode, t.BankReference })
            .ToListAsync(ct);
        var ids = txs.Select(t => t.Id).ToList();
        var matches = await _db.PaymentMatches.AsNoTracking().Where(m => ids.Contains(m.BankTransactionId))
            .Select(m => new { m.BankTransactionId, m.Status, Linked = m.ActualWpfCustomerId != null || m.PaymentId != null })
            .ToDictionaryAsync(m => m.BankTransactionId, ct);
        var memories = await _db.CustomerIbanMemories
            .CountAsync(m => m.LicenseId == request.LicenseId && m.IbanHash == request.IbanHash, ct);
        var rows = txs.Select(t =>
        {
            matches.TryGetValue(t.Id, out var m);
            return new TransactionRow(t.Id, t.OccurredAt, t.Amount, t.Currency, t.BankaKodu, t.CounterpartyIbanMasked, t.Description,
                t.CounterpartyName, t.TransactionCode, t.BankReference, m?.Status, m?.Linked ?? false);
        }).ToList();
        return new Lookup(request, rows, memories);
    }

    /// <summary>Kimlik ve IBAN sahipliğinin doğrulama YÖNTEMİNİ kaydeder (KVKK Ö-11). Açık talepte, dışa aktarma ve silmeden önce
    /// zorunlu; yeniden kaydedilebilir (son yöntem geçerli).</summary>
    public async Task VerifyAsync(Guid requestId, BankSubjectVerification method, string actor, CancellationToken ct)
    {
        if (method == BankSubjectVerification.None) throw new ObifinValidationException(NoMethodMessage);
        var request = await _db.BankSubjectRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new ObifinValidationException(NotFoundMessage);
        if (request.ClosedAt is not null) throw new ObifinValidationException(ClosedMessage);
        request.Verification = method;
        request.VerifiedAt = DateTimeOffset.UtcNow;
        request.VerifiedBy = actor;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Dışa aktarım (<see cref="ToCsv"/>): yalnız açık ve doğrulaması kaydedilmiş talepte.</summary>
    public async Task<byte[]> ExportAsync(Guid requestId, CancellationToken ct)
    {
        var lookup = await FindAsync(requestId, ct) ?? throw new ObifinValidationException(NotFoundMessage);
        if (lookup.Request.ClosedAt is not null) throw new ObifinValidationException(ClosedMessage);
        if (lookup.Request.Verification == BankSubjectVerification.None) throw new ObifinValidationException(NotVerifiedMessage);
        return ToCsv(lookup);
    }

    /// <summary>Lisansların açık taleplerini kapatır (bağlantı kaldırma, hizmet sonu yerel silmesi, yayıncı silmesi): özet boşalır,
    /// sonuç <see cref="BankSubjectResolution.DataDeleted"/>, <paramref name="outcome"/> (çağıranın sabit metni, yönetici girdisi
    /// değil) yazılır. Yönetici başvurana yine yanıt vermelidir. KAYDETMEZ: çağıranın silmesiyle aynı SaveChanges. Kapatılan
    /// sayısını döner.</summary>
    public static async Task<int> CloseOpenForLicensesAsync(LicenseDbContext db, IReadOnlyCollection<Guid> licenseIds, string outcome,
        string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (licenseIds.Count == 0) return 0;
        var open = await db.BankSubjectRequests.Where(r => licenseIds.Contains(r.LicenseId) && r.ClosedAt == null).ToListAsync(ct);
        foreach (var r in open)
        {
            r.ClosedAt = now;
            r.ClosedBy = actor;
            r.IbanHash = null;
            r.Resolution = BankSubjectResolution.DataDeleted;
            r.Outcome = outcome;
        }
        return open.Count;
    }

    /// <summary>Erişim talebinin dışa aktarımı (KVKK m. 11/1-b). Müşteri kimliği ve kullanıcı adı YAZILMAZ: havaleyi gönderen ile
    /// ödemesi sayılan müşteri farklı kişi olabilir. UTF-8 BOM'lu (Excel), noktalı virgül ayraçlı; '=', '+', '-', '@', sekme ve
    /// satır başı ile başlayan hücre kesme işaretiyle başlar (formül enjeksiyonu, OWASP CSV injection listesi). Doğrulama denetimi
    /// <see cref="ExportAsync"/>'te.</summary>
    public static byte[] ToCsv(Lookup lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine("islem_zamani_tr;tutar;para_birimi;banka;kisaltilmis_iban;aciklama;gonderen_adi;islem_kodu;banka_referansi;eslestirme");
        foreach (var t in lookup.Transactions)
        {
            var tr = TimeZoneInfo.ConvertTime(t.OccurredAt, ObifinConnectionService.TrZone);
            sb.AppendLine(string.Join(';',
                Cell(tr.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                Cell(t.Amount.ToString("0.00", CultureInfo.InvariantCulture)),
                Cell(t.Currency), Cell(t.BankaKodu), Cell(t.IbanMasked), Cell(t.Description), Cell(t.CounterpartyName),
                Cell(t.TransactionCode), Cell(t.BankReference),
                Cell(t.LinkedToCustomer ? "bir müşteriyle eşleşti" : "eşleşmedi")));
        }
        sb.AppendLine($"# Bu IBAN'ın bağlı olduğu müşteri kaydı sayısı: {lookup.IbanMemories} (müşteri kimliği yazılmaz)");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // OWASP CSV injection: sekme ve satır başı da formülün önüne geçebilir.
        if ("=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Silme talebi (KVKK m. 11/1-e): bu özetle kurulmuş IBAN bağları silinir; aynı özetli hareketlerin açıklaması, adı ve
    /// ham kaydı silinir; eşleşme satırında bu IBAN'ın bağlı olduğu müşterinin kimliği ve kanıt silinir (bağ yoksa yalnız kanıt —
    /// gönderen ile ödemesi sayılan müşteri farklı kişi olabilir). Talep kapanır, özet boşalır, imha kaydı talep kimliğiyle aynı
    /// işlemde yazılır. Eşzamanlı eşleştirme yazısıyla çakışırsa bir kez baştan yeniden denenir.</summary>
    public async Task<EraseResult> EraseAsync(Guid requestId, string actor, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await EraseOnceAsync(requestId, actor, ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<EraseResult> EraseOnceAsync(Guid requestId, string actor, CancellationToken ct)
    {
        var request = await _db.BankSubjectRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new ObifinValidationException(NotFoundMessage);
        if (request.ClosedAt is not null || request.IbanHash is null) throw new ObifinValidationException(ClosedMessage);
        // Erişim talebiyle silme yapılmaz: başvuranın istediği bu değil.
        if (request.Kind != BankSubjectRequestKind.Erasure) throw new ObifinValidationException(WrongKindMessage);
        // KVKK Ö-11: yanlış kişinin talebiyle silinmesin — doğrulama yöntemi kayıtlı olmalı.
        if (request.Verification == BankSubjectVerification.None) throw new ObifinValidationException(NotVerifiedMessage);
        var now = DateTimeOffset.UtcNow;
        var memories = await _db.CustomerIbanMemories
            .Where(m => m.LicenseId == request.LicenseId && m.IbanHash == request.IbanHash).ToListAsync(ct);
        var customers = memories.Select(m => m.WpfCustomerId).ToHashSet();
        var txs = await _db.BankTransactions
            .Where(t => t.LicenseId == request.LicenseId && t.CounterpartyIbanHash == request.IbanHash).ToListAsync(ct);
        var ids = txs.Select(t => t.Id).ToList();
        var matches = await _db.PaymentMatches.Where(m => ids.Contains(m.BankTransactionId)).ToListAsync(ct);

        _db.CustomerIbanMemories.RemoveRange(memories);
        foreach (var t in txs) BankSubjectScrub.Transaction(t, now);
        foreach (var m in matches)
        {
            Guid? subject = m.ActualWpfCustomerId is { } a && customers.Contains(a) ? a
                : m.ProposedWpfCustomerId is { } p && customers.Contains(p) ? p : null;
            if (subject is { } s) BankSubjectScrub.MatchForCustomer(m, s, now);
            else BankSubjectScrub.MatchEvidence(m, now);
        }

        request.ClosedAt = now;
        request.ClosedBy = actor;
        request.IbanHash = null;
        request.Resolution = BankSubjectResolution.Erased;
        request.Outcome = $"Silindi: {txs.Count} hareketin açıklaması/adı/ham kaydı, {memories.Count} IBAN bağı, " +
            $"{matches.Count} eşleşme satırının kimliği/kanıtı. Tutar, tarih, kısaltılmış IBAN ve tanıma kodu kaldı.";
        const string criterion = "ilgili kişi talebi";
        DataDestruction.Record(_db, request.LicenseId, "CustomerIbanMemories", DataDestruction.Actions.Deleted, memories.Count,
            criterion, DataDestruction.Triggers.SubjectRequest, actor, request.Id);
        DataDestruction.Record(_db, request.LicenseId, "BankTransactions.Description+CounterpartyName+RawJson",
            DataDestruction.Actions.Nulled, txs.Count, criterion, DataDestruction.Triggers.SubjectRequest, actor, request.Id);
        DataDestruction.Record(_db, request.LicenseId, "PaymentMatches.WpfCustomerId+Evidence", DataDestruction.Actions.Nulled,
            matches.Count, criterion, DataDestruction.Triggers.SubjectRequest, actor, request.Id);
        await _db.SaveChangesAsync(ct);
        return new EraseResult(txs.Count, memories.Count, matches.Count);
    }

    /// <summary>Talebi bir sonuç KATEGORİSİYLE kapatır (KVKK m. 13/3; Silme Yönetmeliği md. 12/1-c gerekçeli ret): sonuç metni
    /// kategoriden üretilir (<see cref="OutcomeText"/>), serbest metin yok; özet boşalır. Kurallar talep kaydının ispat değerini
    /// korur: <see cref="BankSubjectResolution.Answered"/> yalnız doğrulaması kayıtlı ERİŞİM talebinde (dışa aktarım ancak orada
    /// mümkün); <see cref="BankSubjectResolution.RejectedNotVerified"/> yalnız doğrulama kaydı OLMAYAN talepte;
    /// <see cref="BankSubjectResolution.RejectedLegalGround"/> ve <see cref="BankSubjectResolution.Withdrawn"/> her talepte.
    /// <see cref="BankSubjectResolution.Erased"/>'ı yalnız <see cref="EraseAsync"/>, <see cref="BankSubjectResolution.DataDeleted"/>'ı
    /// yalnız <see cref="CloseOpenForLicensesAsync"/> yazar.</summary>
    public async Task CloseAsync(Guid requestId, BankSubjectResolution resolution, string actor, CancellationToken ct)
    {
        var request = await _db.BankSubjectRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new ObifinValidationException(NotFoundMessage);
        if (request.ClosedAt is not null) throw new ObifinValidationException(ClosedMessage);
        var verified = request.Verification != BankSubjectVerification.None;
        var allowed = resolution switch
        {
            BankSubjectResolution.Answered => request.Kind == BankSubjectRequestKind.Access && verified,
            BankSubjectResolution.RejectedNotVerified => !verified,
            BankSubjectResolution.RejectedLegalGround or BankSubjectResolution.Withdrawn => true,
            _ => false,
        };
        if (!allowed) throw new ObifinValidationException(ResolutionNotAllowedMessage);
        request.ClosedAt = DateTimeOffset.UtcNow;
        request.ClosedBy = actor;
        request.IbanHash = null;
        request.Resolution = resolution;
        request.Outcome = OutcomeText(resolution);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Kapatma kategorisinin sonuç metni (talep listesi ve kayıt). Kişisel veri yok.</summary>
    public static string OutcomeText(BankSubjectResolution resolution) => resolution switch
    {
        BankSubjectResolution.Answered => "Yanıtlandı: dışa aktarım başvurana iletildi.",
        BankSubjectResolution.RejectedNotVerified => "Reddedildi: başvuranın kimliği ya da IBAN'ın ona ait olduğu doğrulanamadı.",
        BankSubjectResolution.RejectedLegalGround => "Reddedildi (tamamen ya da kısmen): kanuni gerekçe başvurana yazılı bildirildi.",
        BankSubjectResolution.Withdrawn => "Başvuran talebi geri çekti.",
        _ => "",
    };
}
```

- [ ] **Adım 5: DI**

`Program.cs` — `BankConnectionTerminator` kaydının ALTINA:

```csharp
        // BankHasher yalnız talep açarken (çağıran verir): anahtarsız modülde de açık talepler kapatılıp silinebilir.
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.BankSubjectRequestService>();
```

- [ ] **Adım 5b: Veri silinince açık talep kalmasın (test önce, kırmızı)**

`BankConnectionTerminatorTests.cs` sınıfının sonuna:

```csharp
    [Fact]
    public async Task Kaldirma_ve_yerel_silme_lisansin_acik_banka_veri_talebini_kapatir()
    {
        // KVKK K-7: veri silindikten sonra açık talep ve IBAN özeti kalmasın; yönetici başvurana yanıt verir.
        using var db = NewDb(); var client = new TerminatorObifin();
        var lic = await SeedAsync(db, client);
        db.BankSubjectRequests.Add(new BankSubjectRequest { Id = Guid.NewGuid(), LicenseId = lic, Kind = BankSubjectRequestKind.Erasure,
            IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", IbanMasked = "TR12…345", ReceivedAt = DateTimeOffset.UtcNow,
            DueAt = DateTimeOffset.UtcNow.AddDays(30), CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "admin" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        await Terminator(db, client).PurgeLocalDataAsync(lic, DataDestruction.Triggers.ServiceEnd, DataDestruction.SystemActor,
            CancellationToken.None);

        var closed = await db.BankSubjectRequests.AsNoTracking().SingleAsync();
        closed.ClosedAt.Should().NotBeNull();
        closed.IbanHash.Should().BeNull();
        closed.Outcome.Should().Contain("başvurana yanıt verin");
    }
```

`Controllers/Compliance/GdprFlowTests.cs` sınıfının sonuna (Görev 13'teki `AdminApiClientAsync` yardımcısıyla):

```csharp
    [Fact]
    public async Task Purge_yayincinin_acik_banka_veri_talebini_kapatir()
    {
        // KVKK K-7: talebin lisansa FK'sı yok (belge lisans silinse de yaşar); açık kalırsa IBAN özeti süresiz durur.
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var licenseId = Guid.NewGuid(); var requestId = Guid.NewGuid();
        string email;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            email = (await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId)).Email;
            db.Licenses.Add(new OrderDeck.LicenseServer.Domain.License { Id = licenseId, LicenseKey = $"LDK-GD-{Guid.NewGuid():N}",
                CustomerId = customerId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
            db.BankSubjectRequests.Add(new OrderDeck.LicenseServer.Domain.Bank.BankSubjectRequest { Id = requestId,
                LicenseId = licenseId, Kind = OrderDeck.LicenseServer.Domain.Bank.BankSubjectRequestKind.Access,
                IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", IbanMasked = "TR12…345", ReceivedAt = DateTimeOffset.UtcNow,
                DueAt = DateTimeOffset.UtcNow.AddDays(30), CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "admin" });
            await db.SaveChangesAsync();
        }
        using var admin = await AdminApiClientAsync();

        (await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var verify = _factory.Services.CreateScope();
        var request = await verify.ServiceProvider.GetRequiredService<LicenseDbContext>().BankSubjectRequests.AsNoTracking()
            .SingleAsync(r => r.Id == requestId);
        request.ClosedAt.Should().NotBeNull();
        request.IbanHash.Should().BeNull();
    }
```

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankConnectionTerminatorTests|FullyQualifiedName~GdprFlowTests"`
Expected: FAIL — iki yeni test (talep açık kalıyor).

`Services/Bank/BankConnectionTerminator.cs` — `DeleteLocalAsync`'in SON adımında (bağlantı ya da işaret ve onaylı hesapların boşaltılması, Obifin teyit kaydı: hepsinin yazıldığı tek SaveChanges; `if (keepConnection) { … } else { … }` bloğundan sonra), `await _db.SaveChangesAsync(ct);` satırının ÜSTÜNE (`now` o adımda tanımlı):

```csharp
        // KVKK K-7: veri yokken açık talep ve IBAN özeti kalmasın; yönetici başvurana yine yanıt verir (Görev 18 sayfası).
        await BankSubjectRequestService.CloseOpenForLicensesAsync(_db, [licenseId],
            (keepConnection ? "Hizmet sonu" : "Bağlantı kaldırıldı") + ": lisansın banka verisi silindi; talep kapatıldı — " +
            "başvurana yanıt verin.", actor, now, ct);
```

`Controllers/Customers/AdminCustomersController.cs` — `Purge`'te Görev 9'daki `Record("Customers.Email+Name+PasswordHash+Notes", …);` satırının ALTINA:

```csharp
        // KVKK K-7: yayıncının lisanslarındaki açık banka veri talepleri kapanır (FK yok: talep satırı belge olarak kalır, özet
        // boşalır). Yönetici başvurana yine yanıt verir.
        await OrderDeck.LicenseServer.Services.Bank.BankSubjectRequestService.CloseOpenForLicensesAsync(_db, licenseIds,
            "Yayıncı hesabı silindi; talep kapatıldı — başvurana yanıt verin.", actor, DateTimeOffset.UtcNow, ct);
```

Run: aynı komut. Expected: PASS.

- [ ] **Adım 6: Göçü üret ve incele**

Run: `cd OrderDeck.LicenseServer && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add BankSubjectRequests --context LicenseDbContext --output-dir Data/Migrations`
Expected: `Up` yalnız `CreateTable("BankSubjectRequests")` (CHECK kısıtıyla; `Resolution` nvarchar(32) not null) + `CreateIndex`.

- [ ] **Adım 7: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankSubjectRequestServiceTests|FullyQualifiedName~BankConnectionTerminatorTests|FullyQualifiedName~BankServiceEndPurgeJobTests|FullyQualifiedName~GdprFlowTests|FullyQualifiedName~BankJobsDiTests"`
Expected: PASS.

Run (Docker): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataCleanupRelationalTests|FullyQualifiedName~CustomerPurgeRelationalTests"`
Expected: PASS.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/Bank/BankSubjectRequest.cs OrderDeck.LicenseServer/Services/Bank/BankSubjectRequestService.cs OrderDeck.LicenseServer/Services/Bank/BankConnectionTerminator.cs OrderDeck.LicenseServer/Controllers/Customers/AdminCustomersController.cs OrderDeck.LicenseServer/Data OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankSubjectRequestServiceTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankConnectionTerminatorTests.cs OrderDeck.LicenseServer.Tests/Controllers/Compliance/GdprFlowTests.cs
git commit -m "feat(bank-kvkk): IBAN ile banka verisi talebi servisini ekle

BankSubjectRequest: IBAN yalnız özet ve maskeyle, son tarih alış + 30 gün;
kimlik ve IBAN sahipliği doğrulama yöntemi (kişisel veri içermez) kaydedilmeden
dışa aktarma ve silme yok; arama, müşteri kimliği yazmayan CSV dışa
aktarım (sekme ve satır başı dahil formül kaçışı), yalnız silme talebinde
silme (Görev 15 kurallarıyla, imha kaydı talep kimliğiyle) ve kişisel veri
içermeyen sonuç kategorisiyle kapatma (yanıtlandı, doğrulanamadı, kanuni
gerekçeyle ret, geri çekildi). Bağlantı kaldırma, hizmet sonu silmesi ve
yayıncı silmesi açık talepleri kapatır (KVKK ön inceleme K-7, Ö-11, P1-9).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 18: Banka veri talepleri sayfası ve silme talepleri listesinde son tarih (P1-9b)

**Neden:** Görev 17'nin servisine yönetici arayüzü; Shopper silme talepleri listesinde de son tarih (talep + 30 gün) ve gecikme işareti. Talep sayfası görüntülenmesi denetime yazılır (Görev 16'daki olay); oluşturma, doğrulama yönteminin kaydı, dışa aktarma, silme, kapatma da denetime yazılır (silme imha olayı → 36 ay). Dışa aktar ve Sil düğmeleri doğrulama yöntemi kaydedilmeden çalışmaz (servis reddeder, sayfa hata şeridi gösterir). İkinci plan incelemesinden sonra (Görev 17) "Sil ve kapat" yalnız silme talebinde görünür. "Yanıtlandı, kapat" yalnız doğrulanmış erişim talebinde görünür. "Reddet / kapat" seçenekleri kategoridir: doğrulanamadı (yalnız doğrulama kaydı yokken), kanuni gerekçeyle ret, geri çekildi. Seçilen kategori talebe ve denetim satırına yazılır. Oluşturan, doğrulayan ve kapatan sütunlarına yöneticinin kimliği (`DataDestruction.ActorOf`) yazılır, kullanıcı adı değil.

**Uygulama notu (Görev 18, 22.10):** kod aşağıdaki adımlardan şu noktalarda ayrılır; Görev 20, metin işi ve avukat paketi koddakine göre yazılır.
- **Dışa aktarım yalnız erişim talebinde (Görev 17 kuralı 4).** "CSV dışa aktar" (`data-form="export"`) yalnız `Kind == Access`'te basılır; silme talebine doğrudan gelen istek `ExportWrongKindMessage` bildirimi verir. Plandaki tek akış testi ikiye bölündü: erişim talebi (aç → doğrulamasız dışa aktarım reddi → doğrula → CSV → "Yanıtlandı") ve silme talebi (dışa aktarım reddi → doğrulamasız silme reddi → doğrula → "Sil ve kapat" → kapalı talepte yalnız "Yeni talep" formu kalır).
- **Sistemin kapattığı talep son tarihe kadar açık listede (Görev 17 kuralı 5).** Açık liste sorgusu `ClosedAt == null || (Resolution == DataDeleted && DueAt > now)`, son tarihe göre sıralı. Satırda `data-badge="awaiting-reply"` ("yanıt bekleniyor") ve sonuç metni; listede açıklayan bir şerit (`data-banner="awaiting-reply"`). Satır kapalı sekmede de görünür. Ayrıntıda kapanmış talebin sonucu (`data-cell="outcome"`) basılır, eylem formu yoktur.
- **Görüntüleme denetimi (Görev 16 kuralı 2).** Liste, kapalı sekme ve ayrıntı dahil her GET bir `bank.page.view` satırı yazar. Hedef `AuditTargets.BankSubjectRequest` + sayfanın gösterdiği talep (`Selected`); bulunamayan `?id=` boş hedef. Ayrıntı `{"page":"banka-veri-talepleri"}` ya da `{"page":"banka-veri-talepleri","requestId":"…"}`.
- **Tür seçimi zorunlu.** `Kind` boş gelirse talep açılmaz (`IndexModel.NoKindMessage`): tür sonradan değişmez ve hakkı belirler (erişim: dışa aktarım; silme: sil ve kapat), seçilmeyen tür sessizce "erişim"e düşmesin. Doğrulama seçicisinin kalıbı.
- **`ReceivedOn`** alanı bugün (TR) ile dolu gelir, `max` bugün; servis ileri günü yine reddeder (Görev 17 kuralı 2).
- **Yöntem ve tür Türkçe adla** gösterilir (`IndexModel.VerificationLabel`, `KindLabel`), enum adı değil; seçicinin değerleri enum adı.
- **Silme bildirimi ve denetim ayrıntısı** `ManualMatchAudits`'i de taşır (Görev 17 kuralı 1).
- **Ek testler:** doğrulanmış talepte "doğrulanamadı" seçeneği yok ve `VerifiedBy` kimlik; tür seçilmeden talep açılmaz; görüntüleme sayısı ve hedefi; gecikmiş açık talebin işareti; sistemin kapattığı talebin açık listede kalması ve son tarihi geçince düşmesi; banka modülü kapalıyken sayfa açılır, yeni talep açılmaz, açık talep kapatılır (`DisabledBankApiFactory`).
- **Silme talepleri listesinde son tarih** sayfanın mevcut "Talep Tarihi" sütunu gibi saklanan değerden (`RequestedAt + 30`, UTC tarihi) basılır; TR gününe göre en çok bir gün ÖNCE düşer (ihtiyatlı yön). Görev 20 telefon hücresini değiştirirken satırın `data-request`'ini ve `data-cell="due"` hücresini korur.

**Files:**
- Create: `OrderDeck.LicenseServer/Pages/Admin/BankaVeriTalepleri/Index.cshtml`, `Index.cshtml.cs`
- Modify: `OrderDeck.LicenseServer/Pages/Shared/_AdminLayout.cshtml:23`
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Shoppers/DeletionRequests.cshtml(.cs)`
- Modify: `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs`
- Test: `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBankaVeriTalepleriPageTests.cs` (YENİ)
- Modify (test): `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminDeletionRequestsPageTests.cs` — Görev 15'te açıldı ("Sil" formu, imha kaydı); mevcut dosyaya EKLE, yeniden yazma

- [ ] **Adım 1: Sayfa testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBankaVeriTalepleriPageTests.cs`:

```csharp
using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

/// <summary>IBAN ile banka veri talebi sayfası (KVKK K-7): talep açılır, hareketler listelenir, dışa aktarım tam IBAN içermez,
/// silme metni ve bağı siler, talep kapanır, hepsi denetime yazılır.</summary>
public sealed class AdminBankaVeriTalepleriPageTests : IClassFixture<ApiFactory>
{
    private const string PagePath = "/admin/banka-veri-talepleri";
    private readonly ApiFactory _factory;
    public AdminBankaVeriTalepleriPageTests(ApiFactory factory) => _factory = factory;

    private async Task<(Guid LicenseId, Guid TxId, string Iban)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<BankHasher>();
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"bv-{Guid.NewGuid():N}@x", Name = "Bv", PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new License { Id = lic, LicenseKey = $"LDK-BV-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = lic, BaseUrl = "https://example.invalid",
            UserCode = $"api-{Guid.NewGuid():N}@x", Status = ObifinConnectionStatus.Verified, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow });
        var iban = BankHasherTests.TestIban();
        var tx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000),
            ObifinAccountId = 6, BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 90m, Currency = "TL",
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-1), FetchedAt = DateTimeOffset.UtcNow.AddDays(-1), Description = "HAVALE talep",
            CounterpartyIbanHash = hasher.HashIban(iban), CounterpartyIbanMasked = BankHasher.MaskIban(iban) };
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        return (lic, tx.Id, iban);
    }

    private static async Task<FormUrlEncodedContent> FormAsync(HttpClient client, string getPath, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = AdminLoginHelper.ExtractAntiForgeryToken(await client.GetStringAsync(getPath));
        return new FormUrlEncodedContent(fields);
    }

    private static async Task<IDocument> ParseAsync(string html)
        => await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));

    [Fact]
    public async Task Girissiz_login_e_yonlenir()
    {
        var resp = await _factory.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(PagePath);
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("/admin/login");
    }

    [Fact]
    public async Task Talep_acilir_listelenir_disa_aktarim_tam_IBAN_icermez_silme_temizler_ve_kapatir()
    {
        var (lic, txId, iban) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var spaced = string.Join(" ", iban.Chunk(4).Select(c => new string(c)));

        var created = await client.PostAsync($"{PagePath}?handler=Create", await FormAsync(client, PagePath,
            new Dictionary<string, string> { ["LicenseId"] = lic.ToString(), ["Iban"] = spaced, ["Kind"] = "Erasure", ["ReceivedOn"] = "2026-10-19" }));

        created.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var detailPath = created.Headers.Location!.ToString();
        var id = Guid.Parse(detailPath[(detailPath.IndexOf("id=", StringComparison.Ordinal) + 3)..]);
        var html = await client.GetStringAsync(detailPath);
        html.Should().NotContain(iban[4..20], "IBAN sayfaya düz basılmaz");
        (await ParseAsync(html)).QuerySelector($"tr[data-tx='{txId}']").Should().NotBeNull();

        var unverified = await client.PostAsync($"{PagePath}?handler=Export&id={id}", await FormAsync(client, detailPath, new()));
        unverified.StatusCode.Should().Be(HttpStatusCode.Redirect, "doğrulama yöntemi kaydedilmeden dışa aktarım yok (KVKK Ö-11)");
        (await client.PostAsync($"{PagePath}?handler=Verify&id={id}", await FormAsync(client, detailPath,
            new Dictionary<string, string> { ["Verification"] = "WetSignatureAndBankDocument" }))).StatusCode
            .Should().Be(HttpStatusCode.Redirect);

        var export = await client.PostAsync($"{PagePath}?handler=Export&id={id}", await FormAsync(client, detailPath, new()));
        export.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var csv = await export.Content.ReadAsStringAsync();
        csv.Should().Contain(BankHasher.MaskIban(iban)).And.NotContain(iban[4..20]);

        var erased = await client.PostAsync($"{PagePath}?handler=Erase&id={id}", await FormAsync(client, detailPath, new()));
        erased.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == txId)).Description.Should().BeNull();
        var request = await db.BankSubjectRequests.AsNoTracking().SingleAsync(r => r.Id == id);
        request.ClosedAt.Should().NotBeNull(); request.IbanHash.Should().BeNull();
        var events = await db.AuditLogs.AsNoTracking().Where(a => a.TargetId == id.ToString()).Select(a => a.EventType).ToListAsync();
        events.Should().Contain(new[] { AuditEvents.BankSubjectCreate, AuditEvents.BankSubjectVerify, AuditEvents.BankSubjectExport,
            AuditEvents.BankSubjectErase });
        request.Verification.Should().Be(BankSubjectVerification.WetSignatureAndBankDocument);
        AuditEvents.DestructionEvents.Should().Contain(AuditEvents.BankSubjectErase, "silme 36 ay saklanır");
        (await db.AuditLogs.AnyAsync(a => a.EventType == AuditEvents.BankPageView && a.Details!.Contains(id.ToString()))).Should().BeTrue();
    }

    [Fact]
    public async Task Gecersiz_IBAN_hata_seridi_verir_talep_acmaz()
    {
        var (lic, _, iban) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();

        await client.PostAsync($"{PagePath}?handler=Create", await FormAsync(client, PagePath, new Dictionary<string, string>
        {
            ["LicenseId"] = lic.ToString(), ["Iban"] = iban[..10] + "******" + iban[16..], ["Kind"] = "Access",
        }));

        var toast = (await ParseAsync(await client.GetStringAsync(PagePath))).QuerySelector(".alert-danger.alert-dismissible")!.TextContent;
        toast.Should().Contain(BankSubjectRequestService.InvalidIbanMessage);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().BankSubjectRequests.AnyAsync(r => r.LicenseId == lic))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Dogrulanamayan_talep_kategoriyle_reddedilir_yanitlandi_secilemez_kayitta_yonetici_kimligi_durur()
    {
        // KVKK m. 13/3, Silme Yönetmeliği md. 12/1-c: gerekçeli ret ayrı sonuç; talep kaydına yanlış sonuç düşmez. Ö-14: satır
        // süresiz, yönetici sütunlarında kullanıcı adı yok.
        var (lic, _, iban) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var created = await client.PostAsync($"{PagePath}?handler=Create", await FormAsync(client, PagePath,
            new Dictionary<string, string> { ["LicenseId"] = lic.ToString(), ["Iban"] = iban, ["Kind"] = "Access" }));
        var detailPath = created.Headers.Location!.ToString();
        var id = Guid.Parse(detailPath[(detailPath.IndexOf("id=", StringComparison.Ordinal) + 3)..]);
        (await ParseAsync(await client.GetStringAsync(detailPath))).QuerySelector("form[data-form='erase']")
            .Should().BeNull("erişim talebinde silme düğmesi yok");

        await client.PostAsync($"{PagePath}?handler=Close&id={id}", await FormAsync(client, detailPath,
            new Dictionary<string, string> { ["Resolution"] = "Answered" }));
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().BankSubjectRequests.AsNoTracking()
                .SingleAsync(r => r.Id == id)).ClosedAt.Should().BeNull("doğrulanmamış erişim talebi 'yanıtlandı' kapanamaz");

        (await client.PostAsync($"{PagePath}?handler=Close&id={id}", await FormAsync(client, detailPath,
            new Dictionary<string, string> { ["Resolution"] = "RejectedNotVerified" }))).StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var request = await db.BankSubjectRequests.AsNoTracking().SingleAsync(r => r.Id == id);
        (request.Resolution, request.IbanHash).Should().Be((BankSubjectResolution.RejectedNotVerified, (string?)null));
        request.Outcome.Should().Be(BankSubjectRequestService.OutcomeText(BankSubjectResolution.RejectedNotVerified));
        var adminId = (await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Username == "admin")).Id.ToString();
        (request.CreatedBy, request.ClosedBy).Should().Be((adminId, adminId), "yöneticinin kimliği, kullanıcı adı değil (Ö-14)");
        (await db.AuditLogs.AsNoTracking().SingleAsync(a => a.EventType == AuditEvents.BankSubjectClose && a.TargetId == id.ToString()))
            .Details.Should().Contain("RejectedNotVerified");
    }
}
```

`OrderDeck.LicenseServer.Tests/Pages/Admin/AdminDeletionRequestsPageTests.cs` — dosya VAR (Görev 15: `PagePath` sabiti, kurucu, `SeedAsync`, `PurgeViaFormAsync`, iki test; using'lerde `AngleSharp`, `Microsoft.Extensions.DependencyInjection`, `OrderDeck.LicenseServer.Data`, `OrderDeck.LicenseServer.Domain`, `OrderDeck.LicenseServer.Tests.TestHelpers` zaten var). Dosyayı YENİDEN YAZMA; sınıf özetine "Listede son tarih (talep + 30 gün, KVKK m. 13/2) ve gecikme işareti." cümlesini ekle ve aşağıdaki testi sınıfa EKLE (mevcut testler kalır; sayfa numarası parametresi `pageNumber`):

```csharp
    [Fact]
    public async Task Bekleyen_talepte_son_tarih_gosterilir_gecikeni_isaretlenir()
    {
        var late = new ShopperDeletionRequest { Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), PhoneAtRequest = TestPhone.NewE164(),
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-31) };
        var fresh = new ShopperDeletionRequest { Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), PhoneAtRequest = TestPhone.NewE164(),
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.ShopperDeletionRequests.AddRange(late, fresh);
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var html = await client.GetStringAsync(PagePath);
        var doc = await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));

        doc.QuerySelector($"tr[data-request='{late.Id}'] [data-cell='due']")!.TextContent.Should()
            .Contain(late.RequestedAt.AddDays(30).ToString("dd.MM.yyyy")).And.Contain("gecikti");
        doc.QuerySelector($"tr[data-request='{fresh.Id}'] [data-cell='due']")!.TextContent.Should().NotContain("gecikti");
    }
```

- [ ] **Adım 2: Koştur — kırmızı**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~AdminBankaVeriTalepleriPageTests|FullyQualifiedName~AdminDeletionRequestsPageTests"`
Expected: FAIL — olaylar ve sayfa yok.

- [ ] **Adım 3: Denetim olayları**

`AuditEvents.cs` — `BankPageView` satırının ALTINA:

```csharp
    // IBAN ile banka veri talebi (admin/banka-veri-talepleri). Ayrıntıya IBAN, açıklama, ad GİRMEZ; yalnız tür ve sayılar.
    public const string BankSubjectCreate = "bank.subject.create";
    /// <summary>Kimlik + IBAN sahipliği doğrulama yöntemi kaydedildi (ayrıntı: yalnız yöntem adı).</summary>
    public const string BankSubjectVerify = "bank.subject.verify";
    public const string BankSubjectExport = "bank.subject.export";
    /// <summary>İmha olayı (<see cref="DestructionEvents"/>).</summary>
    public const string BankSubjectErase = "bank.subject.erase";
    public const string BankSubjectClose = "bank.subject.close";
```

`DestructionEvents` kümesine `BankSubjectErase,` ekle. `AuditTargets`'a: `public const string BankSubjectRequest = "bank-subject-request";`

- [ ] **Adım 4: Sayfa modeli**

`OrderDeck.LicenseServer/Pages/Admin/BankaVeriTalepleri/Index.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Pages.Admin.BankaVeriTalepleri;

/// <summary>
/// IBAN ile banka verisi talepleri (KVKK K-7, Ö-11): talep aç, hareketleri gör, CSV dışa aktar, sil, kapat. [Authorize] yok —
/// Program.cs <c>AuthorizeFolder("/Admin")</c>. Girilen IBAN yalnız istek belleğinde yaşar: sunucuda özetlenir, sayfaya, günlüğe,
/// denetime girmez. Talep açmak <see cref="BankHasher"/> ister (modül kapalıyken açılamaz); arama, dışa aktarma, silme ve
/// kapatma istemez. Kimlik ve IBAN sahipliğinin doğrulanması yöneticinin işidir (sayfa uyarır). Sayfanın görüntülenmesi ve her
/// işlem denetime yazılır.
/// </summary>
public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly BankSubjectRequestService _requests;
    private readonly IServiceProvider _services;
    private readonly IAuditService _audit;
    private readonly BankOptions _bank;

    public IndexModel(LicenseDbContext db, BankSubjectRequestService requests, IServiceProvider services, IAuditService audit,
        IOptions<BankOptions> bank)
    { _db = db; _requests = requests; _services = services; _audit = audit; _bank = bank.Value; }

    public sealed record LicenseOption(Guid Id, string Label);
    public sealed record RequestRow(Guid Id, string License, BankSubjectRequestKind Kind, string IbanMasked, DateTimeOffset ReceivedAt,
        DateTimeOffset DueAt, DateTimeOffset? ClosedAt, string? Outcome);

    [BindProperty] public Guid LicenseId { get; set; }
    [BindProperty] public string? Iban { get; set; }
    [BindProperty] public BankSubjectRequestKind Kind { get; set; }
    [BindProperty] public DateOnly? ReceivedOn { get; set; }
    [BindProperty] public BankSubjectVerification Verification { get; set; }
    /// <summary>Kapatmanın kategorisi (Görev 17: serbest metin yok).</summary>
    [BindProperty] public BankSubjectResolution Resolution { get; set; }

    public List<LicenseOption> Licenses { get; private set; } = new();
    public List<RequestRow> Requests { get; private set; } = new();
    public BankSubjectRequestService.Lookup? Selected { get; private set; }
    public bool ShowClosed { get; private set; }
    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
    public bool BankDisabled => !BankHasher.IsValidKey(_bank.HashKey);

    public async Task OnGetAsync(Guid? id, bool closed, CancellationToken ct)
    {
        ShowClosed = closed;
        Licenses = await _db.Licenses.AsNoTracking()
            .Where(l => _db.ObifinConnections.Any(c => c.LicenseId == l.Id))
            .OrderBy(l => l.Customer.Email)
            .Select(l => new LicenseOption(l.Id, l.Customer.Email + " · " + l.LicenseKey)).ToListAsync(ct);
        var query = _db.BankSubjectRequests.AsNoTracking().Where(r => closed ? r.ClosedAt != null : r.ClosedAt == null);
        var rows = await (closed ? query.OrderByDescending(r => r.ClosedAt) : query.OrderBy(r => r.DueAt)).Take(100).ToListAsync(ct);
        var licenseIds = rows.Select(r => r.LicenseId).Distinct().ToList();
        var labels = await _db.Licenses.AsNoTracking().Where(l => licenseIds.Contains(l.Id))
            .Select(l => new { l.Id, Label = l.Customer.Email + " · " + l.LicenseKey }).ToDictionaryAsync(x => x.Id, x => x.Label, ct);
        Requests = rows.Select(r => new RequestRow(r.Id, labels.GetValueOrDefault(r.LicenseId, "(silinmiş lisans)"), r.Kind,
            r.IbanMasked, r.ReceivedAt, r.DueAt, r.ClosedAt, r.Outcome)).ToList();
        if (id is { } requestId)
        {
            Selected = await _requests.FindAsync(requestId, ct);
            // KVKK Ö-4: banka verisi gösteren sayfanın görüntülenmesi denetime yazılır.
            await _audit.LogAsync(AuditEvents.BankPageView, AuditTargets.BankSubjectRequest, requestId.ToString(),
                new { page = "banka-veri-talepleri", requestId }, ct);
        }
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (BankDisabled)
        {
            TempData["Error"] = BankHasher.DisabledMessage;
            return RedirectToPage();
        }
        BankSubjectRequest request;
        try
        {
            var hasher = _services.GetRequiredService<BankHasher>();
            var receivedOn = ReceivedOn ?? DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ObifinConnectionService.TrZone).DateTime);
            // Yönetici sütunlarına kimlik, kullanıcı adı değil (KVKK Ö-14; satır süresiz — karar 22).
            request = await _requests.CreateAsync(hasher, LicenseId, Iban ?? "", Kind, receivedOn, DataDestruction.ActorOf(User), ct);
        }
        catch (ObifinValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        await _audit.LogAsync(AuditEvents.BankSubjectCreate, AuditTargets.BankSubjectRequest, request.Id.ToString(),
            new { kind = request.Kind.ToString() }, CancellationToken.None);
        TempData["Success"] = $"Talep açıldı; son tarih {TimeZoneInfo.ConvertTime(request.DueAt, ObifinConnectionService.TrZone):dd.MM.yyyy}.";
        return RedirectToPage(new { id = request.Id });
    }

    /// <summary>KVKK Ö-11: kimlik ve IBAN sahipliğinin doğrulama YÖNTEMİ (kişisel veri değil). Dışa aktarma ve silmeden önce
    /// zorunlu.</summary>
    public async Task<IActionResult> OnPostVerifyAsync(Guid id)
    {
        try
        {
            await _requests.VerifyAsync(id, Verification, DataDestruction.ActorOf(User), CancellationToken.None);
        }
        catch (ObifinValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage(new { id });
        }
        await _audit.LogAsync(AuditEvents.BankSubjectVerify, AuditTargets.BankSubjectRequest, id.ToString(),
            new { method = Verification.ToString() }, CancellationToken.None);
        TempData["Success"] = "Doğrulama yöntemi kaydedildi.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostExportAsync(Guid id, CancellationToken ct)
    {
        byte[] csv;
        try
        {
            csv = await _requests.ExportAsync(id, ct);
        }
        catch (ObifinValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage(new { id });
        }
        await _audit.LogAsync(AuditEvents.BankSubjectExport, AuditTargets.BankSubjectRequest, id.ToString(), null,
            CancellationToken.None);
        return File(csv, "text/csv; charset=utf-8", $"banka-verisi-{id:N}.csv");
    }

    /// <summary>İstek jetonu BİLEREK kullanılmaz: kaydedilmiş silmenin denetimi istek iptaliyle düşmesin.</summary>
    public async Task<IActionResult> OnPostEraseAsync(Guid id)
    {
        BankSubjectRequestService.EraseResult r;
        try
        {
            // Yapan: yöneticinin kimliği — imha kaydı budanmaz, kullanıcı adı girmez (Görev 9 incelemesi).
            r = await _requests.EraseAsync(id, DataDestruction.ActorOf(User), CancellationToken.None);
        }
        catch (ObifinValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage(new { id });
        }
        await _audit.LogAsync(AuditEvents.BankSubjectErase, AuditTargets.BankSubjectRequest, id.ToString(),
            new { r.Transactions, r.IbanMemories, r.Matches }, CancellationToken.None);
        TempData["Success"] = $"Silindi: {r.Transactions} hareketin açıklaması/adı/ham kaydı, {r.IbanMemories} IBAN bağı. Talep kapatıldı. "
            + BankSubjectRequestService.OtherCopiesNotice;
        return RedirectToPage(new { id });
    }

    /// <summary>Talebi bir sonuç kategorisiyle kapatır (KVKK m. 13/3; Silme Yönetmeliği md. 12/1-c gerekçeli ret): yanıtlandı
    /// (yalnız doğrulanmış erişim talebi), doğrulanamadı, kanuni gerekçeyle ret, geri çekildi. Kural serviste
    /// (<see cref="BankSubjectRequestService.CloseAsync"/>); kategori denetime yazılır, serbest metin yok.</summary>
    public async Task<IActionResult> OnPostCloseAsync(Guid id)
    {
        try
        {
            await _requests.CloseAsync(id, Resolution, DataDestruction.ActorOf(User), CancellationToken.None);
        }
        catch (ObifinValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage(new { id });
        }
        await _audit.LogAsync(AuditEvents.BankSubjectClose, AuditTargets.BankSubjectRequest, id.ToString(),
            new { resolution = Resolution.ToString() }, CancellationToken.None);
        TempData["Success"] = "Talep kapatıldı: " + BankSubjectRequestService.OutcomeText(Resolution);
        return RedirectToPage(new { id });
    }
}
```

- [ ] **Adım 5: Görünüm**

`OrderDeck.LicenseServer/Pages/Admin/BankaVeriTalepleri/Index.cshtml`:

```cshtml
@page "/admin/banka-veri-talepleri"
@model IndexModel
@using OrderDeck.LicenseServer.Domain.Bank
@using OrderDeck.LicenseServer.Services.Bank
@{
    ViewData["Title"] = "Banka Veri Talepleri";
    var tr = ObifinConnectionService.TrZone;
}
@* TempData şeritleri _AdminLayout → _ToastPartial'da basılır. Girilen IBAN hiçbir yerde geri basılmaz. *@
<h1 class="h3 mb-3">Banka Veri Talepleri (IBAN ile)</h1>

<div class="alert alert-warning small">
    Dışa aktarmadan ya da silmeden önce başvuranın <strong>kimliğini ve IBAN'ın ona ait olduğunu</strong> doğrulayın; başka birinin
    havale bilgisi açıklanmamalı. Son tarih talebin ulaştığı günden 30 gündür (KVKK m. 13/2).
</div>
@if (Model.BankDisabled)
{
    <div class="alert alert-danger" data-banner="bank-disabled">@BankHasher.DisabledMessage — yeni talep açılamaz; açık talepler işlenebilir.</div>
}

<h2 class="h5">Yeni talep</h2>
<form method="post" asp-page-handler="Create" class="row g-2 mb-4" autocomplete="off">
    <div class="col-md-4"><label class="form-label">Lisans</label>
        <select name="LicenseId" class="form-select" required>
            <option value="">— lisans seçin —</option>
            @foreach (var l in Model.Licenses)
            {
                <option value="@l.Id">@l.Label</option>
            }
        </select></div>
    <div class="col-md-4"><label class="form-label">IBAN (boşluklu yazılabilir)</label>
        <input name="Iban" class="form-control" required autocomplete="off" /></div>
    <div class="col-md-2"><label class="form-label">Tür</label>
        <select name="Kind" class="form-select">
            <option value="Access">Erişim</option>
            <option value="Erasure">Silme</option>
        </select></div>
    <div class="col-md-2"><label class="form-label">Talebin ulaştığı gün</label>
        <input name="ReceivedOn" type="date" class="form-control" /></div>
    <div class="col-12"><button class="btn btn-primary">Talep aç</button></div>
</form>

<ul class="nav nav-tabs mb-3">
    <li class="nav-item"><a class="nav-link @(Model.ShowClosed ? "" : "active")" asp-route-closed="false">Açık</a></li>
    <li class="nav-item"><a class="nav-link @(Model.ShowClosed ? "active" : "")" asp-route-closed="true">Kapalı</a></li>
</ul>
<table class="table table-sm" data-table="subject-requests">
    <thead><tr><th>Lisans</th><th>Tür</th><th>IBAN</th><th>Ulaştı</th><th>Son tarih</th><th>Durum</th><th></th></tr></thead>
    <tbody>
    @foreach (var r in Model.Requests)
    {
        var overdue = r.ClosedAt is null && r.DueAt < Model.Now;
        <tr data-request="@r.Id">
            <td>@r.License</td>
            <td>@(r.Kind == BankSubjectRequestKind.Erasure ? "Silme" : "Erişim")</td>
            <td><code>@(string.IsNullOrEmpty(r.IbanMasked) ? "(silindi)" : r.IbanMasked)</code></td>
            <td>@TimeZoneInfo.ConvertTime(r.ReceivedAt, tr).ToString("dd.MM.yyyy")</td>
            <td data-cell="due" class="@(overdue ? "text-danger fw-bold" : "")">@TimeZoneInfo.ConvertTime(r.DueAt, tr).ToString("dd.MM.yyyy")@(overdue ? " (gecikti)" : "")</td>
            <td><small>@(r.ClosedAt is { } c ? $"kapandı {TimeZoneInfo.ConvertTime(c, tr):dd.MM.yyyy}: {r.Outcome}" : "açık")</small></td>
            <td><a asp-route-id="@r.Id" asp-route-closed="@Model.ShowClosed">Aç</a></td>
        </tr>
    }
    </tbody>
</table>

@if (Model.Selected is { } s)
{
    <h2 class="h5 mt-4">Talep @s.Request.IbanMasked — @(s.Request.Kind == BankSubjectRequestKind.Erasure ? "silme" : "erişim")</h2>
    <p class="small text-muted">Bu IBAN'ın bağlı olduğu müşteri kaydı: @s.IbanMemories. Hareket: @s.Transactions.Count.</p>
    <table class="table table-sm">
        <thead><tr><th>İşlem (TR)</th><th>Tutar</th><th>IBAN</th><th>Açıklama</th><th>Gönderen</th><th>Eşleştirme</th></tr></thead>
        <tbody>
        @foreach (var t in s.Transactions)
        {
            <tr data-tx="@t.Id">
                <td>@TimeZoneInfo.ConvertTime(t.OccurredAt, tr).ToString("dd.MM.yyyy HH:mm")</td>
                <td>@t.Amount.ToString("N2") @t.Currency</td>
                <td><code>@t.IbanMasked</code></td>
                <td>@t.Description</td>
                <td>@t.CounterpartyName</td>
                <td>@(t.LinkedToCustomer ? "eşleşti" : "eşleşmedi")</td>
            </tr>
        }
        </tbody>
    </table>
    @if (s.Request.ClosedAt is null)
    {
        <form method="post" class="row g-2 mb-2" asp-page-handler="Verify" asp-route-id="@s.Request.Id" data-form="verify">
            <div class="col-md-8"><label class="form-label">Kimlik ve IBAN sahipliği nasıl doğrulandı? (zorunlu; belge saklanmaz)</label>
                <select name="Verification" class="form-select" required>
                    <option value="">— yöntem seçin —</option>
                    <option value="WetSignatureAndBankDocument">Islak imzalı dilekçe + kimlik; IBAN banka belgesiyle</option>
                    <option value="ElectronicSignatureAndBankDocument">KEP / güvenli e-imza / mobil imza; IBAN banka belgesiyle</option>
                    <option value="RegisteredContactAndBankDocument">Kayıtlı iletişim kanalı; IBAN banka belgesiyle</option>
                </select></div>
            <div class="col-md-4 align-self-end"><button class="btn btn-outline-secondary">Doğrulamayı kaydet</button></div>
        </form>
        @* Doğrulayanın kimliği (sub) basılmaz: yöneticiye bir şey söylemez, yalnız tarih ve yöntem. *@
        <p class="small" data-cell="verification">@(s.Request.Verification == BankSubjectVerification.None
            ? "Doğrulama kaydı yok: dışa aktarma ve silme yapılamaz."
            : $"Doğrulandı: {s.Request.Verification} ({TimeZoneInfo.ConvertTime(s.Request.VerifiedAt!.Value, tr):dd.MM.yyyy HH:mm})")</p>
        <form method="post" class="d-inline" asp-page-handler="Export" asp-route-id="@s.Request.Id">
            <button class="btn btn-outline-primary">CSV dışa aktar</button>
        </form>
        @if (s.Request.Kind == BankSubjectRequestKind.Erasure)
        {
            <form method="post" class="d-inline" asp-page-handler="Erase" asp-route-id="@s.Request.Id" data-form="erase"
                  onsubmit="return confirm('Bu IBAN ile ilgili havale metinleri ve IBAN bağı silinecek. Geri alınamaz. Emin misiniz?')">
                <button class="btn btn-outline-danger">Sil ve kapat</button>
            </form>
        }
        @if (s.Request.Kind == BankSubjectRequestKind.Access && s.Request.Verification != BankSubjectVerification.None)
        {
            <form method="post" class="d-inline" asp-page-handler="Close" asp-route-id="@s.Request.Id">
                <input type="hidden" name="Resolution" value="Answered" />
                <button class="btn btn-outline-secondary">Yanıtlandı (dışa aktarım iletildi), kapat</button>
            </form>
        }
        @* KVKK m. 13/3, Silme Yönetmeliği md. 12/1-c: gerekçeli ret kategoridir; gerekçenin kendisi başvurana yazılı gider, sisteme
           serbest metin girmez. *@
        <form method="post" class="row g-2 mt-2" asp-page-handler="Close" asp-route-id="@s.Request.Id" data-form="reject">
            <div class="col-md-8"><label class="form-label">Reddet / kapat (gerekçe başvurana yazılı bildirilir)</label>
                <select name="Resolution" class="form-select" required>
                    <option value="">— sonuç seçin —</option>
                    @if (s.Request.Verification == BankSubjectVerification.None)
                    {
                        <option value="RejectedNotVerified">Kimlik ya da IBAN sahipliği doğrulanamadı</option>
                    }
                    <option value="RejectedLegalGround">Kanuni gerekçeyle (tamamen ya da kısmen) reddedildi</option>
                    <option value="Withdrawn">Başvuran talebi geri çekti</option>
                </select></div>
            <div class="col-md-4 align-self-end"><button class="btn btn-outline-danger">Reddet / kapat</button></div>
        </form>
    }
}
```

`Pages/Shared/_AdminLayout.cshtml` — Banka Eşleştirme satırının ALTINA:

```cshtml
                    <li class="nav-item"><a class="nav-link" asp-page="/Admin/BankaVeriTalepleri/Index">Banka Veri Talepleri</a></li>
```

- [ ] **Adım 6: Silme talepleri listesinde son tarih**

`Pages/Admin/Shoppers/DeletionRequests.cshtml.cs`:
- Sınıfa `public const int ResponseDays = 30;` ve `public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;`
- `Row` kaydına `RequestedAt`'ten sonra `DateTimeOffset DueAt,` ekle.
- `LoadAsync`'teki `new Row(r.Id, r.ShopperId, r.PhoneAtRequest, r.RequestedAt,` satırını `new Row(r.Id, r.ShopperId, r.PhoneAtRequest, r.RequestedAt, r.RequestedAt.AddDays(ResponseDays),` yap.

`DeletionRequests.cshtml`:
- Başlıkta `<th>Talep Tarihi</th>` satırının ALTINA `<th>Son tarih</th>`.
- Satırda `<tr>` → `<tr data-request="@r.Id">`; `<td>@r.RequestedAt.ToString("dd.MM.yyyy HH:mm")</td>` satırının ALTINA:

```cshtml
                    @{ var overdue = r.HandledAt is null && r.DueAt < Model.Now; }
                    <td data-cell="due" class="@(overdue ? "text-danger fw-bold" : "")">@r.DueAt.ToString("dd.MM.yyyy")@(overdue ? " (gecikti)" : "")</td>
```

- [ ] **Adım 7: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~Pages.Admin|FullyQualifiedName~ShopperMeDelete"`
Expected: PASS.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Pages/Admin/BankaVeriTalepleri OrderDeck.LicenseServer/Pages/Shared/_AdminLayout.cshtml OrderDeck.LicenseServer/Pages/Admin/Shoppers OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBankaVeriTalepleriPageTests.cs OrderDeck.LicenseServer.Tests/Pages/Admin/AdminDeletionRequestsPageTests.cs
git commit -m "feat(bank-kvkk): banka veri talepleri sayfası ve silme taleplerinde son tarih ekle

admin/banka-veri-talepleri: IBAN ile talep aç, doğrulama yöntemini kaydet,
hareketleri gör, CSV dışa aktar, silme talebinde sil ve kapat, sonuç
kategorisiyle kapat ya da reddet; her işlem ve görüntüleme denetime
yazılır, yönetici sütunlarına kullanıcı adı değil kimlik girer. Shopper
silme talepleri listesi son tarihi (talep + 30 gün) ve gecikmeyi gösterir
(KVKK ön inceleme K-7, Ö-11, P1-9).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 19: Dekont bildiriminde tutar ve ret gerekçesi yok (P1-19)

**Neden (not K-5 öneri 3):** dekont onay/ret bildirimi yayıncı adını, tutarı ve serbest metin ret gerekçesini Google FCM'ye gönderiyor. Gövdeden tutar ve gerekçe çıkar ("olgu" — onay/ret — kalır; ayrıntı uygulamada). Bu, aktarımı kaldırmaz (cihaz jetonu, yayıncı adı yine gider), veriyi küçültür.

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelPaymentsController.cs:140, 178-179, 228-279`
- Test: `OrderDeck.LicenseServer.Tests/Services/Push/ShopperOrderPaymentPushTests.cs:226-262`

- [ ] **Adım 1: Testleri çevir (kırmızı)**

`ShopperOrderPaymentPushTests.cs`:
- `PaymentApprove_pushes_shopper` içindeki `n.Body.Should().Contain("480").And.Contain("onayland");` satırını şununla değiştir:
```csharp
        n.Body.Should().Be(OrderDeck.LicenseServer.Controllers.Panel.PanelPaymentsController.PaymentApprovedPushBody);
        n.Body.Should().NotContain("480", "tutar FCM'ye gitmez (KVKK K-5)");
```
- `PaymentReject_pushes_shopper_with_reason` testinin adını `PaymentReject_pushes_shopper_without_amount_or_reason` yap ve `n.Body.Should().Contain("reddedildi").And.Contain("Tutar uyu");` satırını şununla değiştir:
```csharp
        n.Body.Should().Be(OrderDeck.LicenseServer.Controllers.Panel.PanelPaymentsController.PaymentRejectedPushBody);
        n.Body.Should().NotContain("Tutar uyu", "serbest metin ret gerekçesi FCM'ye gitmez (KVKK K-5)").And.NotContain("100");
```

- [ ] **Adım 2: Koştur — kırmızı**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ShopperOrderPaymentPushTests"`
Expected: FAIL — sabitler yok.

- [ ] **Adım 3: Uygula**

`PanelPaymentsController.cs` — sınıfın başına (alanlardan önce):

```csharp
    /// <summary>KVKK K-5: bildirim gövdesi Google FCM'ye gider; tutar ve serbest metin ret gerekçesi YAZILMAZ, ayrıntı uygulamada.</summary>
    public const string PaymentApprovedPushBody = "Dekontun onaylandı.";
    public const string PaymentRejectedPushBody = "Dekontun reddedildi. Ayrıntıyı uygulamada görebilirsin.";
```

Çağrıları güncelle: `await NotifyShopperPaymentDecisionAsync(payment, approved: true, reason: null, ct);` → `await NotifyShopperPaymentDecisionAsync(payment, approved: true, ct);`; ret çağrısındaki iki satırı (`await NotifyShopperPaymentDecisionAsync(payment, approved: false,` / `reason: payment.RejectReason, ct);`) → `await NotifyShopperPaymentDecisionAsync(payment, approved: false, ct);`.

Metodun imzasını `private async Task NotifyShopperPaymentDecisionAsync(Payment payment, bool approved, CancellationToken ct)` yap ve gövdedeki:

```csharp
            var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            var amount = payment.Amount.ToString("N2", tr);
            var (title, body) = approved
                ? (broadcasterName, $"{amount} ₺ dekontun onaylandı")
                : (broadcasterName,
                    string.IsNullOrWhiteSpace(reason)
                        ? $"{amount} ₺ dekontun reddedildi"
                        : $"{amount} ₺ dekontun reddedildi: {reason}");
```
bloğunu şununla değiştir:
```csharp
            // KVKK K-5: tutar ve ret gerekçesi FCM'ye gitmez; yayıncı adı (başlık) ve olgu kalır.
            var title = broadcasterName;
            var body = approved ? PaymentApprovedPushBody : PaymentRejectedPushBody;
```

Metodun özetine şu cümleyi ekle: `Gövdede tutar ve ret gerekçesi yok (KVKK K-5): FCM üçüncü taraf, ayrıntı uygulamada.`

- [ ] **Adım 4: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ShopperOrderPaymentPushTests|FullyQualifiedName~PanelPayments"`
Expected: PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelPaymentsController.cs OrderDeck.LicenseServer.Tests/Services/Push/ShopperOrderPaymentPushTests.cs
git commit -m "fix(kvkk): dekont bildiriminde tutarı ve ret gerekçesini FCM'ye gönderme

Onay/ret bildiriminin gövdesi sabit metin; tutar ve serbest metin ret
gerekçesi uygulamada kalır (KVKK ön inceleme K-5, P1-19).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 20: Silme talebindeki telefon 30 gün sonra boşaltılır (P2-25)

**Neden (not D-5, Ö-11):** `ShopperDeletionRequests.PhoneAtRequest` süresiz; talebin kaydı kanıt olarak kalmalı ama telefona işlendikten sonra ihtiyaç yok. İşlenmiş talebin telefonu işlenmeden 30 gün sonra boşaltılır (`SecurityDataRetentionJob`, kendi süresiyle — `RetentionDays` kapalıyken de koşar). Bekleyen talebe dokunulmaz (yönetici kimin talebine baktığını görmeli). Aynı kural banka veri talebinin (Görev 17) kısaltılmış IBAN'ına da uygulanır: özet kapanışta boşalıyor, maske (`BankSubjectRequests.IbanMasked`) kapanıştan 30 gün sonra boşaltılır; açık talebe dokunulmaz. Süreler politikaya yazılmalı (metin işi).

**İmha kaydı (ikinci plan incelemesi; Ö-13, Görev 9'un "her imha kayıt bırakır" kuralı):** telefon ve maske boşaltması `DataDestructionRecords`'a yazmıyor, koşu kaydı bırakmıyordu; iş düşse ya da hiç koşmasa haftalık "son başarılı çalışma" kontrolü bunu göremezdi. İşin ortak boşaltma yardımcısı (`RunAsync`) artık her partinin kaydını (hedef, `nulled`, satır sayısı, ölçüt, tetikleyici `retention`, yapan `system`) partiyle AYNI işlemde yazar. Relational'da `ExecuteUpdate` ve kayıt açık bir işlemde (`audit-retention`'ın kalıbı); InMemory'de tek SaveChanges. Bu yardımcıdan geçen bütün hedefler kayıt bırakır: telefon, maske ve mevcut IP/UA boşaltmaları. Başarılı koşu sonda `DataDestruction.RecordRun(JobId)` yazar. Relational yol `SecurityDataRetentionRelationalTests` ile gerçek SQL Server'da sınanır.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Audit/SecurityDataRetentionJob.cs` (bütünüyle: süre, imha kaydı, koşu kaydı)
- Modify: `OrderDeck.LicenseServer/Program.cs` (`security-data-retention` → `SecurityDataRetentionJob.JobId`)
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Shoppers/DeletionRequests.cshtml` (boş telefon)
- Test: `OrderDeck.LicenseServer.Tests/Services/Audit/SecurityDataRetentionJobTests.cs`, YENİ `SecurityDataRetentionRelationalTests.cs` (Testcontainers)

- [ ] **Adım 1: Testi yaz (kırmızı)**

`SecurityDataRetentionJobTests.cs` sınıfının sonuna:

```csharp
    [Fact]
    public async Task Islenmis_silme_talebindeki_telefon_30_gun_sonra_bosaltilir_talep_kalir()
    {
        // KVKK D-5: talebin kaydı kanıt olarak kalır; işlendikten sonra telefona ihtiyaç yok.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        ShopperDeletionRequest Request(DateTimeOffset? handledAt) => new()
        {
            Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), PhoneAtRequest = TestPhone.NewE164(),
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-60), HandledAt = handledAt, HandledBy = handledAt is null ? null : "admin",
        };
        var old = Request(DateTimeOffset.UtcNow.AddDays(-31));
        var recent = Request(DateTimeOffset.UtcNow.AddDays(-29));
        var pending = Request(null);
        db.ShopperDeletionRequests.AddRange(old, recent, pending);
        await db.SaveChangesAsync();

        await new SecurityDataRetentionJob(db, Options.Create(new SecurityDataRetentionOptions()),
            NullLogger<SecurityDataRetentionJob>.Instance).AnonymiseAsync(CancellationToken.None);

        var rows = await db.ShopperDeletionRequests.AsNoTracking()
            .Where(r => r.Id == old.Id || r.Id == recent.Id || r.Id == pending.Id).ToDictionaryAsync(r => r.Id);
        rows[old.Id].PhoneAtRequest.Should().BeEmpty();
        rows[old.Id].HandledAt.Should().NotBeNull("talebin kaydı kanıt olarak kalır");
        rows[recent.Id].PhoneAtRequest.Should().Be(recent.PhoneAtRequest);
        rows[pending.Id].PhoneAtRequest.Should().Be(pending.PhoneAtRequest, "bekleyen talepte yönetici kimin talebine baktığını görür");
    }

    [Fact]
    public async Task Kapanmis_banka_veri_talebinin_kisaltilmis_IBANi_30_gun_sonra_bosaltilir_acik_talebe_dokunulmaz()
    {
        // KVKK Ö-11: özet kapanışta boşaldı; kısaltılmış IBAN da talebin belgesi için gereğinden uzun kalmasın.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        OrderDeck.LicenseServer.Domain.Bank.BankSubjectRequest Request(DateTimeOffset? closedAt) => new()
        {
            Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), Kind = OrderDeck.LicenseServer.Domain.Bank.BankSubjectRequestKind.Access,
            IbanHash = closedAt is null ? $"{Guid.NewGuid():N}{Guid.NewGuid():N}" : null, IbanMasked = "TR12…345",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-60), DueAt = DateTimeOffset.UtcNow.AddDays(-30),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-60), CreatedBy = "admin", ClosedAt = closedAt,
            ClosedBy = closedAt is null ? null : "admin",
        };
        var old = Request(DateTimeOffset.UtcNow.AddDays(-31));
        var recent = Request(DateTimeOffset.UtcNow.AddDays(-29));
        var open = Request(null);
        db.BankSubjectRequests.AddRange(old, recent, open);
        await db.SaveChangesAsync();

        await new SecurityDataRetentionJob(db, Options.Create(new SecurityDataRetentionOptions()),
            NullLogger<SecurityDataRetentionJob>.Instance).AnonymiseAsync(CancellationToken.None);

        var rows = await db.BankSubjectRequests.AsNoTracking()
            .Where(r => r.Id == old.Id || r.Id == recent.Id || r.Id == open.Id).ToDictionaryAsync(r => r.Id);
        rows[old.Id].IbanMasked.Should().BeEmpty();
        rows[old.Id].ClosedAt.Should().NotBeNull("talebin kaydı kalır");
        rows[recent.Id].IbanMasked.Should().Be("TR12…345");
        rows[open.Id].IbanMasked.Should().Be("TR12…345");
    }

    [Fact]
    public async Task Her_bosaltma_kisisel_veri_icermeyen_imha_kaydi_yazar_basarili_kosu_kosu_kaydi_birakir()
    {
        // KVKK Ö-13 (Görev 9 kuralı): her imha kayıt bırakır; haftalık "son başarılı çalışma" kontrolü koşu kaydından okunur.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var start = DateTimeOffset.UtcNow;
        db.ShopperDeletionRequests.Add(new ShopperDeletionRequest
        {
            Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), PhoneAtRequest = TestPhone.NewE164(),
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-60), HandledAt = DateTimeOffset.UtcNow.AddDays(-31), HandledBy = "admin",
        });
        await db.SaveChangesAsync();

        await new SecurityDataRetentionJob(db, Options.Create(new SecurityDataRetentionOptions()),
            NullLogger<SecurityDataRetentionJob>.Instance).AnonymiseAsync(CancellationToken.None);

        // Paylaşılan InMemory veritabanı: yalnız bu koşunun kayıtları (başka testlerin satırları da boşaltılmış olabilir).
        var records = await db.DataDestructionRecords.AsNoTracking().Where(r => r.OccurredAt >= start).ToListAsync();
        records.Should().Contain(r => r.Target == "ShopperDeletionRequests.PhoneAtRequest"
            && r.Action == OrderDeck.LicenseServer.Services.Privacy.DataDestruction.Actions.Nulled && r.RowCount >= 1
            && r.Trigger == OrderDeck.LicenseServer.Services.Privacy.DataDestruction.Triggers.Retention
            && r.Actor == OrderDeck.LicenseServer.Services.Privacy.DataDestruction.SystemActor && r.LicenseId == null);
        records.Should().ContainSingle(r => r.Action == OrderDeck.LicenseServer.Services.Privacy.DataDestruction.Actions.RunCompleted
            && r.Target == SecurityDataRetentionJob.JobId);
    }
```

`OrderDeck.LicenseServer.Tests/Services/Audit/SecurityDataRetentionRelationalTests.cs` (relational yol: `ExecuteUpdate` ile imha kaydı aynı açık işlemde):

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Audit;

/// <summary>SecurityDataRetentionJob gerçek SQL Server'da: ExecuteUpdate ile boşaltma ve imha kaydı aynı işlemde, koşu kaydı
/// sonda.</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SecurityDataRetentionRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    public SecurityDataRetentionRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Telefon_bosaltmasi_ve_imha_kaydi_gercek_SQL_Serverda_ayni_islemde_yazilir_kosu_kaydi_kalir()
    {
        var handled = new ShopperDeletionRequest { Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), PhoneAtRequest = TestPhone.NewE164(),
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-60), HandledAt = DateTimeOffset.UtcNow.AddDays(-31), HandledBy = "admin" };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.ShopperDeletionRequests.Add(handled);
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SecurityDataRetentionJob>().AnonymiseAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.ShopperDeletionRequests.AsNoTracking().SingleAsync(r => r.Id == handled.Id)).PhoneAtRequest.Should().BeEmpty();
        var records = await vdb.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Should().ContainSingle(r => r.Target == "ShopperDeletionRequests.PhoneAtRequest")
            .Which.Should().Match<DataDestructionRecord>(r => r.Action == DataDestruction.Actions.Nulled && r.RowCount == 1
                && r.Trigger == DataDestruction.Triggers.Retention);
        records.Should().ContainSingle(r => r.Action == DataDestruction.Actions.RunCompleted && r.Target == SecurityDataRetentionJob.JobId);
    }
}
```

- [ ] **Adım 2: Koştur — kırmızı**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SecurityDataRetention"`
Expected: FAIL — `JobId` yok (derleme); sonra telefon boşaltılmadı, imha kaydı yok (Docker gerekli: `SecurityDataRetentionRelationalTests`).

- [ ] **Adım 3: Uygula**

`SecurityDataRetentionJob.cs` — `SecurityDataRetentionOptions`'a `BatchSize`'ın ALTINA:

```csharp
    /// <summary>İşlenmiş silme talebindeki telefonun (<c>ShopperDeletionRequest.PhoneAtRequest</c>) ve kapanmış banka veri
    /// talebindeki kısaltılmış IBAN'ın (<c>BankSubjectRequest.IbanMasked</c>) boşaltılacağı gün, işlenme/kapanış tarihinden.
    /// 0 = kapalı. <see cref="RetentionDays"/>'ten bağımsız. KVKK ön inceleme D-5, Ö-11; gizlilik politikasına yazılmalı.</summary>
    public int DeletionRequestPhoneDays { get; set; } = 30;
```

Dosyanın üstüne `using OrderDeck.LicenseServer.Services.Privacy;` ve `using OrderDeck.LicenseServer.Services.Shoppers;` ekle (ödeme denetimi hedefi `ShopperPurgeService.SubmissionAuditFingerprintTarget`; Görev 15 incelemesinin kuralı 5: imha kaydı hedefleri çoğul DbSet adıyla). Sınıfa `public const string JobId = "security-data-retention";` ekle; `Program.cs`'teki `manager.AddOrUpdate<…SecurityDataRetentionJob>("security-data-retention", …)` dizesini `OrderDeck.LicenseServer.Services.Audit.SecurityDataRetentionJob.JobId` yap (koşu kaydının hedefi ile Hangfire kimliği tek yerde).

`AnonymiseAsync`'i ve `RunAsync`'i bütünüyle şununla değiştir (IP/UA hedefleri ve sorguları aynen kalır; yalnız ölçüt ve kayıt eklenir):

```csharp
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task AnonymiseAsync(CancellationToken ct)
    {
        if (_opt.DeletionRequestPhoneDays <= 0 && _opt.RetentionDays <= 0)
        {
            _log.LogInformation("[SecurityDataRetention] devre dışı (RetentionDays=0, DeletionRequestPhoneDays=0)");
            return;
        }
        var relational = _db.Database.IsRelational();

        // KVKK D-5: işlenmiş silme talebinin telefonu (kendi süresi; RetentionDays kapalıyken de). Bekleyen talebe dokunulmaz.
        if (_opt.DeletionRequestPhoneDays > 0)
        {
            var phoneCutoff = DateTimeOffset.UtcNow.AddDays(-_opt.DeletionRequestPhoneDays);
            var phones = await RunAsync(
                "ShopperDeletionRequests.PhoneAtRequest", $"HandledAt+{_opt.DeletionRequestPhoneDays}g, işlenmiş talep",
                () => _db.ShopperDeletionRequests.Where(r => r.HandledAt != null && r.HandledAt < phoneCutoff && r.PhoneAtRequest != ""),
                q => q.ExecuteUpdateAsync(s => s.SetProperty(r => r.PhoneAtRequest, ""), ct),
                rows => { foreach (var r in rows) r.PhoneAtRequest = ""; },
                relational, ct);
            if (phones > 0)
                _log.LogInformation("[SecurityDataRetention] {Count} işlenmiş silme talebinin telefonu boşaltıldı", phones);

            // KVKK Ö-11: kapanmış banka veri talebinin kısaltılmış IBAN'ı (özet kapanışta boşaldı). Açık talebe dokunulmaz.
            var masks = await RunAsync(
                "BankSubjectRequests.IbanMasked", $"ClosedAt+{_opt.DeletionRequestPhoneDays}g, kapanmış talep",
                () => _db.BankSubjectRequests.Where(r => r.ClosedAt != null && r.ClosedAt < phoneCutoff && r.IbanMasked != ""),
                q => q.ExecuteUpdateAsync(s => s.SetProperty(r => r.IbanMasked, ""), ct),
                rows => { foreach (var r in rows) r.IbanMasked = ""; },
                relational, ct);
            if (masks > 0)
                _log.LogInformation("[SecurityDataRetention] {Count} kapanmış banka veri talebinin kısaltılmış IBAN'ı boşaltıldı", masks);
        }

        if (_opt.RetentionDays > 0)
            await AnonymiseSecurityFieldsAsync(relational, ct);
        else
            _log.LogInformation("[SecurityDataRetention] IP/User-Agent boşaltması devre dışı (RetentionDays=0)");

        // Kesilen koşu (Hangfire kapanışı) partilerin döngüsünden sessizce çıkar: başarılı koşu kaydı yazılmasın
        // (audit-retention kalıbı). Ölçüt metninde ';' yok: dış kopyanın (CSV) ayracı.
        ct.ThrowIfCancellationRequested();
        DataDestruction.RecordRun(_db, JobId, $"IP/UA +{_opt.RetentionDays}g / silme talebi telefonu ve banka talebi maskesi " +
            $"+{_opt.DeletionRequestPhoneDays}g");
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    /// <summary>90 günden eski güvenlik kayıtlarındaki IP ve User-Agent alanları (politikadaki "Güvenlik kayıtları: 90 gün").
    /// Hedefler ve sorgular önceki sürümle aynı; her parti imha kaydı bırakır.</summary>
    private async Task AnonymiseSecurityFieldsAsync(bool relational, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_opt.RetentionDays);
        var days = _opt.RetentionDays;

        var audit = await RunAsync(
            "AuditLogs.IpAddress", $"OccurredAt+{days}g",
            () => _db.AuditLogs.Where(a => a.OccurredAt < cutoff && a.IpAddress != null),
            q => q.ExecuteUpdateAsync(s => s.SetProperty(a => a.IpAddress, (string?)null), ct),
            rows => { foreach (var r in rows) r.IpAddress = null; },
            relational, ct);

        var refreshTokens = await RunAsync(
            "RefreshTokens.CreatedByIp", $"CreatedAt+{days}g",
            () => _db.RefreshTokens.Where(t => t.CreatedAt < cutoff && t.CreatedByIp != null),
            q => q.ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedByIp, (string?)null), ct),
            rows => { foreach (var r in rows) r.CreatedByIp = null; },
            relational, ct);

        var shopperTokens = await RunAsync(
            "ShopperRefreshTokens.CreatedByIp", $"CreatedAt+{days}g",
            () => _db.ShopperRefreshTokens.Where(t => t.CreatedAt < cutoff && t.CreatedByIp != null),
            q => q.ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedByIp, (string?)null), ct),
            rows => { foreach (var r in rows) r.CreatedByIp = null; },
            relational, ct);

        var intake = await RunAsync(
            "IntakeFormSubmissions.IpAddress+UserAgent", $"SubmittedAt+{days}g",
            () => _db.IntakeFormSubmissions.Where(
                s => s.SubmittedAt < cutoff && (s.IpAddress != null || s.UserAgent != null)),
            q => q.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IpAddress, (string?)null)
                .SetProperty(x => x.UserAgent, (string?)null), ct),
            rows => { foreach (var r in rows) { r.IpAddress = null; r.UserAgent = null; } },
            relational, ct);

        // Mali kayıt: satır ve tutar duruyor, yalnız istemci parmak izi gidiyor.
        // Sütunlar non-nullable olduğu için null yerine boş string yazılıyor.
        var paymentAudit = await RunAsync(
            ShopperPurgeService.SubmissionAuditFingerprintTarget, $"CreatedAt+{days}g",
            () => _db.PaymentSubmissionAudits.Where(
                a => a.CreatedAt < cutoff && (a.IpAddress != "" || a.UserAgent != "")),
            q => q.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IpAddress, "")
                .SetProperty(x => x.UserAgent, ""), ct),
            rows => { foreach (var r in rows) { r.IpAddress = ""; r.UserAgent = ""; } },
            relational, ct);

        var total = audit + refreshTokens + shopperTokens + intake + paymentAudit;
        if (total > 0)
            _log.LogInformation(
                "[SecurityDataRetention] {Cutoff} öncesi {Total} satır anonimleştirildi "
                + "(audit={Audit}, refresh={Refresh}, shopperRefresh={ShopperRefresh}, "
                + "intake={Intake}, paymentAudit={PaymentAudit})",
                cutoff, total, audit, refreshTokens, shopperTokens, intake, paymentAudit);
        else
            _log.LogDebug("[SecurityDataRetention] {Cutoff} öncesi anonimleştirilecek satır yok", cutoff);
    }

    /// <summary>
    /// Tek tablo için batch'li boşaltma. Her parti kişisel veri içermeyen imha kaydını (hedef = <paramref name="label"/>,
    /// <c>nulled</c>, ölçüt, tetikleyici <c>retention</c>, yapan <c>system</c>) boşaltmayla AYNI işlemde yazar (KVKK Ö-13; Görev 9
    /// kuralı). Relational sağlayıcıda ExecuteUpdate + kayıt açık bir işlemde (AuditRetentionJobs'taki kalıp), InMemory'de
    /// (testler) load+set ve kayıt tek SaveChanges'te.
    /// </summary>
    private async Task<int> RunAsync<TEntity>(
        string label,
        string criterion,
        Func<IQueryable<TEntity>> query,
        Func<IQueryable<TEntity>, Task<int>> bulkUpdate,
        Action<List<TEntity>> inMemoryUpdate,
        bool relational,
        CancellationToken ct) where TEntity : class
    {
        void Record(int count) => DataDestruction.Record(_db, null, label, DataDestruction.Actions.Nulled, count, criterion,
            DataDestruction.Triggers.Retention, DataDestruction.SystemActor);

        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            int affected;
            if (relational)
            {
                // ExecuteUpdate WHERE'i zaten boşaltılmışları dışlıyor, yani her tur kalan işi küçültüyor ve döngü kesin
                // sonlanıyor. Kayıt boşaltmayla aynı işlemde: biri olup öbürü olmasın.
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                affected = await bulkUpdate(query().Take(_opt.BatchSize));
                Record(affected);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            else
            {
                var rows = await query().Take(_opt.BatchSize).ToListAsync(ct);
                inMemoryUpdate(rows);
                affected = rows.Count;
                Record(affected);
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();
            total += affected;
            if (affected < _opt.BatchSize) break;
        }

        if (total > 0)
            _log.LogDebug("[SecurityDataRetention] {Label}: {Count} satır", label, total);

        return total;
    }
```

Sınıf özetine ekle: `/// Ayrıca işlenmiş silme talebinin telefonu işlenmeden ve kapanmış banka veri talebinin kısaltılmış IBAN'ı kapanıştan <see cref="SecurityDataRetentionOptions.DeletionRequestPhoneDays"/> gün sonra boşaltılır (KVKK D-5, Ö-11). Her boşaltma partisi kişisel veri içermeyen imha kaydını aynı işlemde yazar; başarılı koşu sonda koşu kaydı (<see cref="JobId"/>) bırakır (KVKK Ö-13).`

`Pages/Admin/BankaVeriTalepleri/Index.cshtml` (Görev 18) boş maskeyi zaten "(silindi)" gösterir.

`DeletionRequests.cshtml` — `<td><code>@r.PhoneAtRequest</code></td>` → `<td><code>@(string.IsNullOrEmpty(r.PhoneAtRequest) ? "(silindi)" : r.PhoneAtRequest)</code></td>`

- [ ] **Adım 4: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SecurityDataRetention|FullyQualifiedName~DeletionRequests|FullyQualifiedName~DataDestructionDigestJobTests"`
Expected: PASS (Docker gerekli: `SecurityDataRetentionRelationalTests`; zaman aşımında sınıfı tek başına yeniden koştur).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Audit/SecurityDataRetentionJob.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer/Pages/Admin/Shoppers/DeletionRequests.cshtml OrderDeck.LicenseServer.Tests/Services/Audit/SecurityDataRetentionJobTests.cs OrderDeck.LicenseServer.Tests/Services/Audit/SecurityDataRetentionRelationalTests.cs
git commit -m "fix(kvkk): işlenmiş silme talebindeki telefonu ve banka talebindeki maskeyi 30 gün sonra boşalt

Talebin kaydı kanıt olarak kalır; telefon işlenmeden, banka veri
talebinin kısaltılmış IBAN'ı kapanıştan 30 gün sonra
SecurityDataRetentionJob ile boşaltılır, bekleyen/açık talebe dokunulmaz.
İşin her boşaltma partisi imha kaydını aynı işlemde yazar, başarılı koşu
koşu kaydı bırakır (KVKK ön inceleme D-5, Ö-11, Ö-13, P2-25).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 21: Yayıncının veri sorumlusu bilgileri — veri modeli, yönetim ve panel ucu (P1-17a)

**Neden (not K-3):** politika "veri sorumlusu, banka hesabı kendisine ait olan yayıncıdır" diyor ama yayıncının adı, unvanı, adresi ya da başvuru yolu hiçbir yerde yok; veri modelinde alan bile yok (m. 10/1-a). Yayıncı hesabı (`Customer`) başına yeni `DataControllerProfiles` tablosu: ad soyad / unvan, posta adresi, KVKK başvuru e-postası, KEP (isteğe bağlı). Müşteri düzeyinde, çünkü kayıt formu (`IntakeFormConfig.CustomerId`) ve panel kiracısı müşteri; `License`'a sütun eklenmez (her UPDATE lisansın rowversion'ını ilerletir). Yeni tablo = genişletme. Yazanlar: yönetim (müşteri detay sayfası) ve panel ucu (yalnız hesap sahibi). Yayıncının KVKK silmesinde (`AdminCustomersController.Purge`) silinir. Gösterim Görev 22'de.

**İkinci plan incelemesi (20.10) — bu görevin ek parçaları:**
- **Bilgi olmadan banka modülü açılmaz (K-3 öneri 3: "banka modülü açılmadan önce"; karar 23).** Plan alanı ve gösterimi ekliyordu ama profili olmayan yayıncı için Obifin bağlantısı kaydedilebiliyor, doğrulanabiliyor ve banka eklenebiliyordu. m. 10/1-a ihlali (yayıncının kimliği hiçbir kanalda yok) yeni bağlantılarda yeniden doğardı. Artık `ObifinConnectionService`'in ilk bağlantı kaydı (`UpsertWithResultAsync`, bağlantı yokken), `VerifyAsync` ve `AddBankConnectionAsync`, lisansın müşterisinde `DataControllerProfile` yoksa `DataControllerMissingMessage` ile reddeder ve Obifin'e gitmez. `Obifin:RequireDataControllerProfile` varsayılan `true`'dur (fail-closed); yalnız pilot geçişi için kapatılır (Prod adımları). Mevcut Verified bağlantının çekimi kapıya takılmaz.
- **Gösterilen kimliğin ispatı (m. 10/1-a ispat yükü; karar 24).** Form bloğu sürümlenen aydınlatma metninin dışındaydı, profilin de geçmişi yoktu (yalnız `UpdatedAt`). Bir kayıt anında ilgili kişiye hangi kimliğin gösterildiği sonradan ispatlanamıyordu. Artık profil her içerik değişikliğinde `Version`'ı artırır ve içeriği `DataControllerProfileVersions`'a ekler (yalnız eklenir). Formun gösterdiği sürüm kayda yazılır (`IntakeFormSubmission.DataControllerVersion`, Görev 22). Sürüm satırları yayıncı silmesinde profille birlikte silinir.
- **Yapan = kimlik:** yönetimden yazılan profilin ve sürümün `UpdatedBy`/`CreatedBy`'ı `admin:` + `DataDestruction.ActorOf(User)`, kullanıcı adı değil (Görev 9 kuralı, KVKK Ö-14).

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/DataControllerProfile.cs`, `OrderDeck.LicenseServer/Domain/DataControllerProfileVersion.cs`
- Create: `OrderDeck.LicenseServer/Services/Privacy/DataControllerProfiles.cs`
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelDataControllerController.cs`
- Modify: `OrderDeck.LicenseServer/Domain/IntakeFormSubmission.cs` (`DataControllerVersion`; Görev 22 yazar)
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Modify: `OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs`, `ObifinOptions.cs`; `OrderDeck.LicenseServer/appsettings.json` (`Obifin:RequireDataControllerProfile`)
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Customers/Detail.cshtml(.cs)`
- Modify: `OrderDeck.LicenseServer/Controllers/Customers/AdminCustomersController.cs` (purge)
- Modify: `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs`
- Create: göç `*_DataControllerProfiles`
- Create: `OrderDeck.LicenseServer.Tests/TestHelpers/TestDataController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Privacy/DataControllerProfilesTests.cs`, `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelDataControllerControllerTests.cs`, `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminCustomerDataControllerTests.cs`, `OrderDeck.LicenseServer.Tests/Services/Bank/ObifinDataControllerGateTests.cs` (YENİ); `Controllers/Compliance/GdprFlowTests.cs`; banka bağlantısı kuran testlerin tohum yardımcıları (Adım 8b)

- [ ] **Adım 1: Doğrulama testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Privacy/DataControllerProfilesTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Privacy;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Privacy;

/// <summary>KVKK K-3: yayıncının veri sorumlusu bilgileri — zorunlu alanlar, e-posta biçimi, kırpma; içerik değişince yeni
/// sürüm (m. 10/1-a ispatı).</summary>
public sealed class DataControllerProfilesTests
{
    [Fact]
    public async Task Icerik_degisince_yeni_surum_yazilir_ayni_icerik_surum_artirmaz()
    {
        // KVKK m. 10/1-a ispat yükü: formda gösterilen kimlik sonradan ispatlanabilsin (IntakeFormSubmission.DataControllerVersion).
        using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"dc-{Guid.NewGuid():N}").Options);
        var customerId = Guid.NewGuid();
        DataControllerProfiles.Validate(Valid(), out var first);
        DataControllerProfiles.Validate(Valid() with { PostalAddress = "Yeni Mah. 3. Sok. No:4 İstanbul" }, out var moved);

        (await DataControllerProfiles.UpsertAsync(db, customerId, first, "panel", CancellationToken.None)).Version.Should().Be(1);
        (await DataControllerProfiles.UpsertAsync(db, customerId, first, "panel", CancellationToken.None)).Version.Should().Be(1);
        (await DataControllerProfiles.UpsertAsync(db, customerId, moved, "admin:yonetici-1", CancellationToken.None))
            .Version.Should().Be(2);

        var versions = await db.DataControllerProfileVersions.AsNoTracking().OrderBy(v => v.Version).ToListAsync();
        versions.Select(v => (v.Version, v.PostalAddress, v.CreatedBy)).Should().Equal(
            (1, first.PostalAddress!, "panel"), (2, "Yeni Mah. 3. Sok. No:4 İstanbul", "admin:yonetici-1"));
    }

    private static DataControllerInput Valid() => new("Örnek Ticaret Ltd. Şti.", "Örnek Mah. 1. Sok. No:2 Kadıköy/İstanbul",
        "kvkk@example.test", null);

    [Fact]
    public void Gecerli_girdi_kirpilarak_kabul_edilir_bos_KEP_null_olur()
    {
        var error = DataControllerProfiles.Validate(
            new DataControllerInput("  Örnek Ticaret  ", " Adres 1 ", " kvkk@example.test ", "   "), out var valid);

        error.Should().BeNull();
        valid.Should().Be(new DataControllerInput("Örnek Ticaret", "Adres 1", "kvkk@example.test", null));
    }

    [Fact]
    public void Eksik_ya_da_bozuk_alan_reddedilir()
    {
        DataControllerProfiles.Validate(Valid() with { LegalName = " " }, out _).Should().Contain("unvan");
        DataControllerProfiles.Validate(Valid() with { LegalName = new string('a', DataControllerProfiles.LegalNameMax + 1) }, out _)
            .Should().Contain("unvan");
        DataControllerProfiles.Validate(Valid() with { PostalAddress = "" }, out _).Should().Contain("adres");
        DataControllerProfiles.Validate(Valid() with { KvkkEmail = "kvkk" }, out _).Should().Contain("e-posta");
        DataControllerProfiles.Validate(Valid() with { KvkkEmail = "Ad <kvkk@example.test>" }, out _).Should().Contain("e-posta");
        DataControllerProfiles.Validate(Valid() with { KepAddress = "kep" }, out _).Should().Contain("KEP");
        DataControllerProfiles.Validate(Valid(), out _).Should().BeNull();
    }
}
```

- [ ] **Adım 2: Panel ucu testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelDataControllerControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>KVKK K-3: yayıncı veri sorumlusu bilgilerini panelden girer; yalnız hesap sahibi değiştirir.</summary>
public sealed class PanelDataControllerControllerTests : IClassFixture<ApiFactory>
{
    private const string Path = "/api/panel/data-controller";
    private readonly ApiFactory _factory;
    public PanelDataControllerControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record ProfileDto(string LegalName, string PostalAddress, string KvkkEmail, string? KepAddress);
    private sealed record Resp(ProfileDto? Profile);

    private async Task<(HttpClient Client, Guid CustomerId)> OwnerAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License { Id = Guid.NewGuid(), LicenseKey = $"LDK-DC-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        await db.SaveChangesAsync();
        return (client, customerId);
    }

    private static object Body(string email = "kvkk@example.test") => new
    {
        legalName = "Örnek Ticaret Ltd. Şti.", postalAddress = "Örnek Mah. 1. Sok. No:2 İstanbul", kvkkEmail = email,
        kepAddress = "ornek@hs01.kep.tr",
    };

    [Fact]
    public async Task Sahip_kaydeder_ve_okur()
    {
        var (client, _) = await OwnerAsync();
        (await (await client.GetAsync(Path)).Content.ReadFromJsonAsync<Resp>())!.Profile.Should().BeNull();

        var put = await client.PutAsJsonAsync(Path, Body());

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var read = (await (await client.GetAsync(Path)).Content.ReadFromJsonAsync<Resp>())!.Profile!;
        read.Should().Be(new ProfileDto("Örnek Ticaret Ltd. Şti.", "Örnek Mah. 1. Sok. No:2 İstanbul", "kvkk@example.test",
            "ornek@hs01.kep.tr"));
    }

    [Fact]
    public async Task Gecersiz_alan_400_doner()
    {
        var (client, _) = await OwnerAsync();
        var put = await client.PutAsJsonAsync(Path, Body(email: "gecersiz"));
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await put.Content.ReadAsStringAsync()).Should().Contain("invalid-field");
    }

    [Fact]
    public async Task Personel_degistiremez()
    {
        var (owner, _) = await OwnerAsync();
        var staff = await PanelOperatorHelper.StaffClientAsync(_factory, owner);
        var put = await staff.PutAsJsonAsync(Path, Body());
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await put.Content.ReadAsStringAsync()).Should().Contain("owner-only");
    }
}
```

`OrderDeck.LicenseServer.Tests/Pages/Admin/AdminCustomerDataControllerTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

public sealed class AdminCustomerDataControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminCustomerDataControllerTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Yonetici_veri_sorumlusu_bilgilerini_kaydeder_ve_denetime_yazar()
    {
        var customerId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Customers.Add(new Customer { Id = customerId, Email = $"dc-{Guid.NewGuid():N}@example.test", Name = "Dc",
                PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var path = $"/admin/customers/{customerId}";
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await client.GetStringAsync(path));

        var resp = await client.PostAsync($"{path}?handler=DataController", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["DcLegalName"] = "Örnek Ticaret Ltd. Şti.",
            ["DcPostalAddress"] = "Örnek Mah. 1. Sok. No:2 İstanbul", ["DcKvkkEmail"] = "kvkk@example.test",
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var profile = await vdb.DataControllerProfiles.AsNoTracking().SingleAsync(p => p.CustomerId == customerId);
        profile.LegalName.Should().Be("Örnek Ticaret Ltd. Şti.");
        profile.KepAddress.Should().BeNull();
        var adminId = (await vdb.AdminUsers.AsNoTracking().SingleAsync(a => a.Username == "admin")).Id;
        profile.UpdatedBy.Should().Be("admin:" + adminId, "yöneticinin kimliği, kullanıcı adı değil (KVKK Ö-14)");
        (await vdb.DataControllerProfileVersions.AsNoTracking().SingleAsync(v => v.CustomerId == customerId))
            .Should().Match<DataControllerProfileVersion>(v => v.Version == 1 && v.LegalName == "Örnek Ticaret Ltd. Şti."
                && v.CreatedBy == "admin:" + adminId);
        (await vdb.AuditLogs.AnyAsync(a => a.EventType == AuditEvents.CustomerDataControllerUpdate && a.TargetId == customerId.ToString()))
            .Should().BeTrue();
        (await client.GetStringAsync(path)).Should().Contain("kvkk@example.test");
    }
}
```

`Controllers/Compliance/GdprFlowTests.cs` — `Purge_via_admin_anonymises_customer_and_deletes_data` içinde `// Mint an admin token.` satırının ÜSTÜNE:

```csharp
        // Veri sorumlusu bilgileri de yayıncının verisi: purge siler (KVKK K-3).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.DataControllerProfiles.Add(new OrderDeck.LicenseServer.Domain.DataControllerProfile
            {
                CustomerId = customerId, LegalName = "Örnek Ticaret", PostalAddress = "Adres", KvkkEmail = "kvkk@example.test",
                Version = 1, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "panel",
            });
            db.DataControllerProfileVersions.Add(new OrderDeck.LicenseServer.Domain.DataControllerProfileVersion
            {
                Id = Guid.NewGuid(), CustomerId = customerId, Version = 1, LegalName = "Örnek Ticaret", PostalAddress = "Adres",
                KvkkEmail = "kvkk@example.test", CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "panel",
            });
            await db.SaveChangesAsync();
        }
```

ve `remainingBackups.Should().Be(0);` satırının ALTINA:

```csharp
        (await verifyDb.DataControllerProfiles.CountAsync(p => p.CustomerId == customerId)).Should().Be(0);
        (await verifyDb.DataControllerProfileVersions.CountAsync(v => v.CustomerId == customerId)).Should().Be(0,
            "sürümler şahıs yayıncıda kişisel veri: profille birlikte silinir (karar 24)");
```

Banka modülü kapısı — `OrderDeck.LicenseServer.Tests/TestHelpers/TestDataController.cs`:

```csharp
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>Yayıncının veri sorumlusu bilgisi (Görev 21): banka bağlantısı kuran testlerin ön koşulu (KVKK K-3 kapısı,
/// <c>Obifin:RequireDataControllerProfile</c>). Değerler sahte, e-posta üretilir; kaydetmez.</summary>
public static class TestDataController
{
    public static void Add(LicenseDbContext db, Guid customerId)
        => db.DataControllerProfiles.Add(new DataControllerProfile
        {
            CustomerId = customerId, LegalName = "Test Yayıncı", PostalAddress = "Test Mah. 1. Sok. No:1",
            KvkkEmail = $"kvkk-{Guid.NewGuid():N}@example.test", Version = 1, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test",
        });
}
```

`OrderDeck.LicenseServer.Tests/Services/Bank/ObifinDataControllerGateTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK K-3 öneri 3 (karar 23): banka modülü yayıncının veri sorumlusu bilgileri toplanmadan açılmaz — ilk bağlantı
/// kaydı, Doğrula ve Banka ekle reddedilir, Obifin'e gidilmez. Bayrak yalnız pilot geçişi için kapatır; varsayılan açık.</summary>
public sealed class ObifinDataControllerGateTests
{
    /// <summary>Her Obifin çağrısını sayar.</summary>
    private sealed class CountingObifin : IObifinClient
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
        { Calls++; return Task.FromResult<IReadOnlyList<ObifinAccountDto>>([]); }
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default)
        { Calls++; return Task.FromResult<IReadOnlyList<ObifinBankConnectionDto>>([]); }
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default)
        { Calls++; return Task.CompletedTask; }
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default)
        { Calls++; return Task.CompletedTask; }
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"dc-gate-{Guid.NewGuid():N}").Options);

    private static ObifinConnectionService Service(LicenseDbContext db, IObifinClient client, bool required = true)
        => new(db, client, Protection,
            new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" })),
            Options.Create(new ObifinOptions { RequireDataControllerProfile = required }), NullLogger<ObifinConnectionService>.Instance);

    private static Guid SeedLicense(LicenseDbContext db, bool withProfile)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"g-{Guid.NewGuid():N}@x", Name = "G",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new License { Id = lic, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        if (withProfile) TestDataController.Add(db, customerId);
        db.SaveChanges();
        return lic;
    }

    private static Task<ObifinConnection> SaveAsync(ObifinConnectionService svc, Guid lic)
        => svc.UpsertAsync(lic, "", $"api-{Guid.NewGuid():N}@x", $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}",
            CancellationToken.None);

    [Fact]
    public async Task Veri_sorumlusu_bilgisi_yoksa_ilk_kayit_reddedilir_Obifine_gidilmez()
    {
        using var db = NewDb(); var client = new CountingObifin();
        var lic = SeedLicense(db, withProfile: false);

        var save = () => SaveAsync(Service(db, client), lic);

        await save.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.DataControllerMissingMessage);
        (await db.ObifinConnections.CountAsync()).Should().Be(0);
        client.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Bilgisi_olmayan_yayincida_Dogrula_ve_Banka_ekle_reddedilir_mevcut_baglanti_durur()
    {
        // Bağlantı P1'den önce kurulmuş (ya da bilgi sonradan silinmiş): satır ve çekim kalır, ama yeniden doğrulama ve yeni banka
        // yok.
        using var db = NewDb(); var client = new CountingObifin();
        var lic = SeedLicense(db, withProfile: true);
        var svc = Service(db, client);
        await SaveAsync(svc, lic);
        db.DataControllerProfiles.RemoveRange(db.DataControllerProfiles);
        await db.SaveChangesAsync();

        var verify = () => svc.VerifyAsync(lic, CancellationToken.None);
        var add = () => svc.AddBankConnectionAsync(lic, "qnb", "QNB", new Dictionary<string, string>(), CancellationToken.None);

        await verify.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.DataControllerMissingMessage);
        await add.Should().ThrowAsync<ObifinValidationException>().WithMessage(ObifinConnectionService.DataControllerMissingMessage);
        client.Calls.Should().Be(0);
        (await db.ObifinConnections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Bayrak_kapatilirsa_bilgi_olmadan_kayit_yapilir_varsayilan_acik()
    {
        new ObifinOptions().RequireDataControllerProfile.Should().BeTrue("fail-closed");
        using var db = NewDb(); var client = new CountingObifin();
        var lic = SeedLicense(db, withProfile: false);

        await SaveAsync(Service(db, client, required: false), lic);

        (await db.ObifinConnections.CountAsync()).Should().Be(1);
    }
}
```

- [ ] **Adım 3: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~DataController|FullyQualifiedName~GdprFlowTests"`
Expected: FAIL — türler yok (`DataControllerProfileVersion`, `RequireDataControllerProfile`, `DataControllerMissingMessage`).

- [ ] **Adım 4: Varlık, model, yardımcı**

`OrderDeck.LicenseServer/Domain/DataControllerProfile.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayıncının veri sorumlusu bilgileri (KVKK m. 10/1-a; ön inceleme K-3): yayıncının veri sorumlusu olduğu yerlerde (kayıt
/// formu, Shopper) ilgili kişiye gösterilir. Yayıncı hesabı (<see cref="Customer"/>) başına bir satır. Şahıs yayıncıda kişisel
/// veridir (ad, adres): yayıncının KVKK silmesinde silinir.
/// </summary>
public sealed class DataControllerProfile
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    /// <summary>Ad soyad ya da ticaret unvanı.</summary>
    public string LegalName { get; set; } = "";
    public string PostalAddress { get; set; } = "";
    /// <summary>KVKK başvurularının gönderileceği e-posta.</summary>
    public string KvkkEmail { get; set; } = "";
    /// <summary>Kayıtlı elektronik posta adresi, varsa.</summary>
    public string? KepAddress { get; set; }
    /// <summary>Gösterilen içeriğin sürümü: her içerik değişikliğinde artar, içeriği <see cref="DataControllerProfileVersion"/>'da
    /// (KVKK m. 10/1-a ispatı; <see cref="IntakeFormSubmission.DataControllerVersion"/> buna işaret eder).</summary>
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary><c>panel</c> ya da <c>admin:&lt;yönetici kimliği&gt;</c> (<c>DataDestruction.ActorOf</c>; kullanıcı adı değil).</summary>
    public string UpdatedBy { get; set; } = "";
}
```

`OrderDeck.LicenseServer/Domain/DataControllerProfileVersion.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Veri sorumlusu bilgisinin bir sürümü (KVKK m. 10/1-a; ispat yükü veri sorumlusunda — ön inceleme K-3, plan karar 24): kayıt
/// formunda gösterilen bloğun içeriği sonradan ispatlanabilsin (<see cref="IntakeFormSubmission.DataControllerVersion"/>). Yalnız
/// eklenir. Şahıs yayıncıda kişisel veridir: yayıncının KVKK silmesinde profille birlikte silinir.
/// </summary>
public sealed class DataControllerProfileVersion
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public int Version { get; set; }
    public string LegalName { get; set; } = "";
    public string PostalAddress { get; set; } = "";
    public string KvkkEmail { get; set; } = "";
    public string? KepAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary><c>panel</c> ya da <c>admin:&lt;yönetici kimliği&gt;</c> (kullanıcı adı değil).</summary>
    public string CreatedBy { get; set; } = "";
}
```

`Domain/IntakeFormSubmission.cs` — `KvkkTextVersion`'ın ALTINA (Görev 22 yazar):

```csharp
    /// <summary>Kayıt anında formda gösterilen veri sorumlusu bloğunun sürümü (<see cref="DataControllerProfileVersion"/>; KVKK
    /// m. 10/1-a ispatı, plan karar 24). Null: blok gösterilmedi (yayıncı bilgi girmemişti) ya da sayfanın bildirdiği sürüm o
    /// yayıncıda yok.</summary>
    public int? DataControllerVersion { get; set; }
```

`OrderDeck.LicenseServer/Services/Privacy/DataControllerProfiles.cs`:

```csharp
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>Veri sorumlusu bilgisinin dışa görünümü (panel, kayıt formu, Shopper). <see cref="Version"/>: gösterilen içeriğin
/// sürümü (kayıt formu gönderimle birlikte geri yollar, karar 24).</summary>
public sealed record DataControllerView(string LegalName, string PostalAddress, string KvkkEmail, string? KepAddress, int Version,
    DateTimeOffset UpdatedAt);

/// <summary>Panel/yönetim girdisi.</summary>
public sealed record DataControllerInput(string? LegalName, string? PostalAddress, string? KvkkEmail, string? KepAddress);

/// <summary>Yayıncının veri sorumlusu bilgilerinin (<see cref="DataControllerProfile"/>) tek doğrulama, okuma ve yazma yolu
/// (KVKK K-3). Panel ucu ve yönetim sayfası aynı kuralları kullanır.</summary>
public static class DataControllerProfiles
{
    public const int LegalNameMax = 200;
    public const int PostalAddressMax = 500;
    public const int EmailMax = 254;

    /// <summary>Kırpar ve doğrular: ad/unvan ve adres zorunlu, e-posta geçerli (görünen ad olmadan), KEP varsa e-posta biçiminde;
    /// boşluk KEP null olur. Hata varsa Türkçe ileti, yoksa null.</summary>
    public static string? Validate(DataControllerInput input, out DataControllerInput normalized)
    {
        var name = (input.LegalName ?? "").Trim();
        var address = (input.PostalAddress ?? "").Trim();
        var email = (input.KvkkEmail ?? "").Trim();
        var kep = string.IsNullOrWhiteSpace(input.KepAddress) ? null : input.KepAddress.Trim();
        normalized = new DataControllerInput(name, address, email, kep);
        if (name.Length is 0 or > LegalNameMax) return $"Ad soyad / unvan zorunlu, en fazla {LegalNameMax} karakter.";
        if (address.Length is 0 or > PostalAddressMax) return $"Posta adresi zorunlu, en fazla {PostalAddressMax} karakter.";
        if (!IsEmail(email)) return "KVKK başvuru e-postası geçerli bir e-posta adresi olmalı.";
        if (kep is not null && !IsEmail(kep)) return "KEP adresi geçerli bir e-posta biçiminde olmalı.";
        return null;
    }

    private static bool IsEmail(string value)
        => value.Length is > 0 and <= EmailMax && MailAddress.TryCreate(value, out var parsed) && parsed.Address == value;

    public static Task<DataControllerView?> ReadAsync(LicenseDbContext db, Guid customerId, CancellationToken ct)
        => db.DataControllerProfiles.AsNoTracking().Where(p => p.CustomerId == customerId)
            .Select(p => new DataControllerView(p.LegalName, p.PostalAddress, p.KvkkEmail, p.KepAddress, p.Version, p.UpdatedAt))
            .FirstOrDefaultAsync(ct);

    /// <summary>Bu yayıncıda bu sürüm var mı (kayıt formunun gizli alanı yalnız o zaman kayda yazılır, Görev 22).</summary>
    public static Task<bool> VersionExistsAsync(LicenseDbContext db, Guid customerId, int version, CancellationToken ct)
        => db.DataControllerProfileVersions.AnyAsync(v => v.CustomerId == customerId && v.Version == version, ct);

    /// <summary><see cref="Validate"/>'ten geçmiş girdiyi yazar (yoksa ekler); kendi SaveChanges'ini çağırır. İçerik değiştiyse
    /// (ya da ilk kayıtsa) <see cref="DataControllerProfile.Version"/> artar ve içerik <see cref="DataControllerProfileVersion"/>'a
    /// eklenir, aynı SaveChanges'te (KVKK m. 10/1-a ispatı, karar 24). İçerik aynıysa hiçbir şey yazılmaz.</summary>
    public static async Task<DataControllerView> UpsertAsync(LicenseDbContext db, Guid customerId, DataControllerInput valid,
        string updatedBy, CancellationToken ct)
    {
        var row = await db.DataControllerProfiles.FirstOrDefaultAsync(p => p.CustomerId == customerId, ct);
        var unchanged = row is not null && row.LegalName == valid.LegalName && row.PostalAddress == valid.PostalAddress
            && row.KvkkEmail == valid.KvkkEmail && row.KepAddress == valid.KepAddress;
        if (!unchanged)
        {
            if (row is null)
            {
                row = new DataControllerProfile { CustomerId = customerId };
                db.DataControllerProfiles.Add(row);
            }
            var now = DateTimeOffset.UtcNow;
            row.LegalName = valid.LegalName!;
            row.PostalAddress = valid.PostalAddress!;
            row.KvkkEmail = valid.KvkkEmail!;
            row.KepAddress = valid.KepAddress;
            row.Version++;
            row.UpdatedAt = now;
            row.UpdatedBy = updatedBy;
            db.DataControllerProfileVersions.Add(new DataControllerProfileVersion
            {
                Id = Guid.NewGuid(), CustomerId = customerId, Version = row.Version, LegalName = row.LegalName,
                PostalAddress = row.PostalAddress, KvkkEmail = row.KvkkEmail, KepAddress = row.KepAddress, CreatedAt = now,
                CreatedBy = updatedBy,
            });
            await db.SaveChangesAsync(ct);
        }
        return new DataControllerView(row!.LegalName, row.PostalAddress, row.KvkkEmail, row.KepAddress, row.Version, row.UpdatedAt);
    }
}
```

`Data/LicenseDbContext.cs` — DbSet'lerin arasına (`LicenseShippingSettings`'in altına):

```csharp
    public DbSet<DataControllerProfile> DataControllerProfiles => Set<DataControllerProfile>();
    public DbSet<DataControllerProfileVersion> DataControllerProfileVersions => Set<DataControllerProfileVersion>();
```

`mb.Entity<LicenseShippingSettings>(…)` bloğunun ALTINA:

```csharp
        // Yayıncının veri sorumlusu bilgileri (KVKK K-3): müşteri başına bir satır, müşteri silinirse gider.
        mb.Entity<DataControllerProfile>(b =>
        {
            b.HasKey(p => p.CustomerId);
            b.HasOne(p => p.Customer).WithOne().HasForeignKey<DataControllerProfile>(p => p.CustomerId).OnDelete(DeleteBehavior.Cascade);
            b.Property(p => p.LegalName).HasMaxLength(DataControllerProfiles.LegalNameMax).IsRequired();
            b.Property(p => p.PostalAddress).HasMaxLength(DataControllerProfiles.PostalAddressMax).IsRequired();
            b.Property(p => p.KvkkEmail).HasMaxLength(DataControllerProfiles.EmailMax).IsRequired();
            b.Property(p => p.KepAddress).HasMaxLength(DataControllerProfiles.EmailMax);
            b.Property(p => p.UpdatedBy).HasMaxLength(300).IsRequired();
        });

        // Veri sorumlusu bilgisinin sürümleri (KVKK m. 10/1-a ispatı, karar 24): yalnız eklenir; müşteri silinirse gider (purge
        // ayrıca siler, satır anonimleşir ama kalır).
        mb.Entity<DataControllerProfileVersion>(b =>
        {
            b.HasKey(v => v.Id);
            b.HasOne<Customer>().WithMany().HasForeignKey(v => v.CustomerId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(v => new { v.CustomerId, v.Version }).IsUnique();
            b.Property(v => v.LegalName).HasMaxLength(DataControllerProfiles.LegalNameMax).IsRequired();
            b.Property(v => v.PostalAddress).HasMaxLength(DataControllerProfiles.PostalAddressMax).IsRequired();
            b.Property(v => v.KvkkEmail).HasMaxLength(DataControllerProfiles.EmailMax).IsRequired();
            b.Property(v => v.KepAddress).HasMaxLength(DataControllerProfiles.EmailMax);
            b.Property(v => v.CreatedBy).HasMaxLength(300).IsRequired();
        });
```

(Dosyanın üstüne `using OrderDeck.LicenseServer.Services.Privacy;` ekle — yoksa.)

- [ ] **Adım 5: Panel ucu**

`OrderDeck.LicenseServer/Controllers/Panel/PanelDataControllerController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının veri sorumlusu bilgileri (KVKK K-3): kayıt formunda ve Shopper'da ilgili kişiye gösterilir. Okuma her panel
/// kullanıcısına açık; değiştirme yalnız hesap sahibi (personel değiştiremez). Kiracı müşteri düzeyinde (lisans gerekmez).
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
[Route("api/panel/data-controller")]
public sealed class PanelDataControllerController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelDataControllerController(LicenseDbContext db) => _db = db;

    /// <param name="Profile">Hiç girilmediyse null.</param>
    public sealed record DataControllerResponse(DataControllerView? Profile);

    [HttpGet]
    public async Task<IActionResult> GetAsync(CancellationToken ct)
        => Ok(new DataControllerResponse(await DataControllerProfiles.ReadAsync(_db, User.GetTenantCustomerId(), ct)));

    [HttpPut]
    public async Task<IActionResult> PutAsync([FromBody] DataControllerInput input, CancellationToken ct)
    {
        if (User.IsOperator())
            return Problem(title: "owner-only", detail: "Veri sorumlusu bilgilerini yalnız hesap sahibi değiştirebilir.", statusCode: 403);
        if (DataControllerProfiles.Validate(input, out var valid) is { } error)
            return Problem(title: "invalid-field", detail: error, statusCode: 400);
        var view = await DataControllerProfiles.UpsertAsync(_db, User.GetTenantCustomerId(), valid, "panel", ct);
        return Ok(new DataControllerResponse(view));
    }
}
```

- [ ] **Adım 6: Yönetim sayfası ve purge**

`Services/Audit/AuditEvents.cs` — `CustomerPurged` satırının ALTINA:

```csharp
    // Yayıncının veri sorumlusu bilgileri (KVKK K-3) yönetimden değiştirildi. Ayrıntı yok.
    public const string CustomerDataControllerUpdate = "customer.data-controller.update";
```

`Pages/Admin/Customers/Detail.cshtml.cs` — `using OrderDeck.LicenseServer.Services.Privacy;` ekle; özellikler:

```csharp
    public DataControllerView? DataController { get; private set; }
    [BindProperty] public string? DcLegalName { get; set; }
    [BindProperty] public string? DcPostalAddress { get; set; }
    [BindProperty] public string? DcKvkkEmail { get; set; }
    [BindProperty] public string? DcKepAddress { get; set; }
```

`OnGetAsync`'te `return Page();`'den önce: `DataController = await DataControllerProfiles.ReadAsync(_db, id, ct);`

Yeni işleyici:

```csharp
    /// <summary>KVKK K-3: yayıncının veri sorumlusu bilgileri (formda ve Shopper'da gösterilir).</summary>
    public async Task<IActionResult> OnPostDataControllerAsync(Guid id, CancellationToken ct)
    {
        if (!await _db.Customers.AnyAsync(c => c.Id == id, ct)) return NotFound();
        if (DataControllerProfiles.Validate(new DataControllerInput(DcLegalName, DcPostalAddress, DcKvkkEmail, DcKepAddress),
                out var valid) is { } error)
        {
            TempData["Error"] = error;
            return RedirectToPage("./Detail", new { id });
        }
        // Yapan: yöneticinin kimliği, kullanıcı adı değil (KVKK Ö-14; Görev 9 kuralı) — sürüm satırı yayıncı silinene kadar durur.
        await DataControllerProfiles.UpsertAsync(_db, id, valid, "admin:" + DataDestruction.ActorOf(User), ct);
        await _audit.LogAsync(AuditEvents.CustomerDataControllerUpdate, AuditTargets.Customer, id.ToString(), null, ct);
        TempData["Success"] = "Veri sorumlusu bilgileri kaydedildi.";
        return RedirectToPage("./Detail", new { id });
    }
```

`Detail.cshtml` — "Profil" tablosunun bulunduğu `<div class="col-md-6">` bloğunun kapanışından SONRA (aynı `row` içinde):

```cshtml
    <div class="col-md-6">
        <h5>Veri sorumlusu bilgileri (KVKK)</h5>
        <p class="small text-muted">Kayıt formunda ve müşteri uygulamasında ilgili kişiye gösterilir.</p>
        <form method="post" asp-page-handler="DataController" asp-route-id="@Model.Customer.Id" class="row g-2">
            <div class="col-12"><label class="form-label">Ad soyad / unvan</label>
                <input name="DcLegalName" class="form-control" value="@Model.DataController?.LegalName" required /></div>
            <div class="col-12"><label class="form-label">Posta adresi</label>
                <input name="DcPostalAddress" class="form-control" value="@Model.DataController?.PostalAddress" required /></div>
            <div class="col-md-6"><label class="form-label">KVKK başvuru e-postası</label>
                <input name="DcKvkkEmail" type="email" class="form-control" value="@Model.DataController?.KvkkEmail" required /></div>
            <div class="col-md-6"><label class="form-label">KEP (varsa)</label>
                <input name="DcKepAddress" class="form-control" value="@Model.DataController?.KepAddress" /></div>
            <div class="col-12"><button class="btn btn-outline-primary btn-sm">Kaydet</button></div>
        </form>
    </div>
```

`Controllers/Customers/AdminCustomersController.cs` — `Purge` içinde `_db.EmailConfirmationTokens.RemoveRange(…);` satırının ALTINA:

```csharp
        // KVKK K-3: veri sorumlusu bilgileri ve sürümleri (şahıs yayıncıda ad ve adres) yayıncının verisidir (karar 24).
        _db.DataControllerProfiles.RemoveRange(_db.DataControllerProfiles.Where(p => p.CustomerId == id));
        _db.DataControllerProfileVersions.RemoveRange(_db.DataControllerProfileVersions.Where(v => v.CustomerId == id));
```

ve Görev 9'daki `Record("Customers.Email+Name+PasswordHash+Notes", …);` satırının ALTINA (kalıcı imha kaydı; sayım izleyiciden, Görev 9 ikinci inceleme 8. madde):

```csharp
        Record("DataControllerProfiles", DataDestruction.Actions.Deleted, Deleted<DataControllerProfile>());
        Record("DataControllerProfileVersions", DataDestruction.Actions.Deleted, Deleted<DataControllerProfileVersion>());
```

`Detail.cshtml.cs`'te `DataDestruction` için `using OrderDeck.LicenseServer.Services.Privacy;` yukarıda eklendi.

- [ ] **Adım 6b: Banka modülü kapısı**

`Services/Bank/ObifinOptions.cs` — `PageSize`'ın ALTINA:

```csharp
    /// <summary>KVKK K-3 öneri 3 (plan karar 23): yayıncının veri sorumlusu bilgileri (<c>DataControllerProfiles</c>) olmadan ilk
    /// bağlantı kaydı, Doğrula ve Banka ekle reddedilir. Varsayılan AÇIK (fail-closed); yalnız pilot geçişinde, metin işi
    /// yayımlanıp bilgi girilene kadar <c>Obifin__RequireDataControllerProfile=false</c> ile kapatılır.</summary>
    public bool RequireDataControllerProfile { get; set; } = true;
```

`appsettings.json` — `"Obifin"` bölümüne `"RequireDataControllerProfile": true` ekle (`"PageSize": 1000`'den sonra).

`Services/Bank/ObifinConnectionService.cs` — `LicenseEndedMessage`'ın ALTINA:

```csharp
    /// <summary>KVKK K-3 öneri 3: banka modülü yayıncının veri sorumlusu bilgileri toplanmadan açılmaz.</summary>
    public const string DataControllerMissingMessage =
        "Önce yayıncının veri sorumlusu bilgilerini girin (Müşteriler → müşteri → Veri sorumlusu bilgileri; KVKK m. 10/1-a).";
```

`IsLicenseActiveAsync`'in (Görev 13) ALTINA:

```csharp
    /// <summary>KVKK K-3 öneri 3 (karar 23): lisansın müşterisinde veri sorumlusu bilgisi yoksa <see cref="DataControllerMissingMessage"/>
    /// fırlatır (<see cref="ObifinOptions.RequireDataControllerProfile"/> kapalıysa denetlemez). İlk bağlantı kaydı, Doğrula ve
    /// Banka ekle çağırır, Obifin'e gitmeden önce; mevcut Verified bağlantının çekimi çağırmaz (veri kaybı olmasın).</summary>
    private async Task EnsureDataControllerAsync(Guid licenseId, CancellationToken ct)
    {
        if (!_opt.RequireDataControllerProfile) return;
        var present = await _db.Licenses.Where(l => l.Id == licenseId)
            .AnyAsync(l => _db.DataControllerProfiles.Any(p => p.CustomerId == l.CustomerId), ct);
        if (!present) throw new ObifinValidationException(DataControllerMissingMessage);
    }
```

Çağrılar:
- `UpsertWithResultAsync` — `if (conn is null) { … }` bloğunda `İlk kayıtta şifre ve API anahtarı zorunlu.` denetiminin ALTINA, yeni bağlantı oluşturulmadan önce: `await EnsureDataControllerAsync(licenseId, ct);`
- `VerifyAsync` ve `AddBankConnectionAsync` — Görev 13'ün lisans denetiminin (`if (!await IsLicenseActiveAsync(…)) throw …;`) ALTINA: `await EnsureDataControllerAsync(licenseId, ct);`

`UpsertWithResultAsync` ve `VerifyAsync` özetlerine bir cümle: `İlk kayıt ve doğrulama yayıncının veri sorumlusu bilgisini ister (KVKK K-3, ObifinOptions.RequireDataControllerProfile).`

- [ ] **Adım 6c: Banka bağlantısı kuran testlerin tohumu**

Kapı varsayılan açık olduğu için servisle bağlantı kuran her test tohumuna veri sorumlusu bilgisi eklenir. Her tohum yardımcısında müşteri ve lisans eklendikten sonra, `SaveChanges`'ten önce `TestDataController.Add(db, customerId);` (dosyaya `using OrderDeck.LicenseServer.Tests.TestHelpers;`, yoksa):
- `Services/Bank/ObifinConnectionServiceTests.cs` → `SeedLicense`
- `Services/Bank/ObifinConnectionServiceConcurrencyTests.cs` → `SeedAsync`
- `Services/Bank/ObifinPollJobTests.cs` → `SeedVerifiedAsync`
- `Services/Bank/ObifinAccountRefreshJobTests.cs` → `SeedVerifiedAsync`
- `Pages/Admin/AdminObifinPageTests.cs` → `SeedLicenseAsync(ApiFactory)` (Kaydet işleyicisi ilk kaydı da yapar)
- `Services/Bank/BankConnectionTerminatorTests.cs` → `SeedAsync` (Görev 13)

Lisanssız bir kimlikle `UpsertAsync` çağıran test varsa (kapı lisansın müşterisine bakar) o test `SeedLicense`'e geçirilir. Bu yardımcılar dışında bağlantıyı doğrudan (`db.ObifinConnections.Add`) kuran testler kapıya takılmaz.

- [ ] **Adım 7: Göçü üret ve incele**

Run: `cd OrderDeck.LicenseServer && DOTNET_CLI_UI_LANGUAGE=en dotnet ef migrations add DataControllerProfiles --context LicenseDbContext --output-dir Data/Migrations`
Expected: `Up` yalnız `CreateTable("DataControllerProfiles")` (PK `CustomerId`, `Customers`'a kaskad FK, `Version` int), `CreateTable("DataControllerProfileVersions")` (`Customers`'a kaskad FK) + tekil `CreateIndex` (`CustomerId`, `Version`) ve `AddColumn<int>(name: "DataControllerVersion", table: "IntakeFormSubmissions", nullable: true)`.

- [ ] **Adım 8: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~DataController|FullyQualifiedName~GdprFlowTests|FullyQualifiedName~Pages.Admin|FullyQualifiedName~Services.Bank"`
Expected: PASS (kapı açık: banka testlerinin tohumunda veri sorumlusu bilgisi var — Adım 6c; kırmızı kalan bir banka testi tohumsuz bağlantı kuruyordur).

Run (Docker): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BankDataCleanupRelationalTests|FullyQualifiedName~CustomerPurgeRelationalTests"`
Expected: PASS.

- [ ] **Adım 9: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/DataControllerProfile.cs OrderDeck.LicenseServer/Domain/DataControllerProfileVersion.cs OrderDeck.LicenseServer/Domain/IntakeFormSubmission.cs OrderDeck.LicenseServer/Services/Privacy/DataControllerProfiles.cs OrderDeck.LicenseServer/Controllers/Panel/PanelDataControllerController.cs OrderDeck.LicenseServer/Data OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs OrderDeck.LicenseServer/Services/Bank/ObifinOptions.cs OrderDeck.LicenseServer/appsettings.json OrderDeck.LicenseServer/Pages/Admin/Customers OrderDeck.LicenseServer/Controllers/Customers/AdminCustomersController.cs OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs OrderDeck.LicenseServer.Tests/TestHelpers/TestDataController.cs OrderDeck.LicenseServer.Tests/Services/Privacy/DataControllerProfilesTests.cs OrderDeck.LicenseServer.Tests/Services/Bank OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelDataControllerControllerTests.cs OrderDeck.LicenseServer.Tests/Pages/Admin OrderDeck.LicenseServer.Tests/Controllers/Compliance/GdprFlowTests.cs
git commit -m "feat(kvkk): yayıncının veri sorumlusu bilgilerini topla, bilgi olmadan banka modülünü açma

DataControllerProfiles (müşteri başına: unvan, posta adresi, KVKK
e-postası, KEP): panel ucu (yalnız hesap sahibi yazar) ve yönetim
sayfasından düzenlenir, her içerik değişikliği sürümlenir
(DataControllerProfileVersions), yayıncı silmesinde silinir. Bilgi yoksa
ilk Obifin bağlantı kaydı, Doğrula ve Banka ekle reddedilir
(Obifin:RequireDataControllerProfile, varsayılan açık). Yönetimden
yazanın kimliği, kullanıcı adı değil (KVKK ön inceleme K-3, Ö-14, P1-17).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 22: Veri sorumlusu bilgileri kayıt formunda ve Shopper ucunda (P1-17b)

**Neden (not K-3, K-8):** EMAR'ın hesabına havale yapan biri veri sorumlusunun kim olduğunu ve ona nasıl başvuracağını hiçbir metinden öğrenemiyor. Kayıt formunda (yayıncı bilgiyi girdiyse) aydınlatma paragrafının üstünde ayrı bir blok; Shopper için iki sunucu alanı: anonim kod sorgusu yanıtına (kayıt ekranı) `dataController` alanı ve bağlı müşteriye `GET …/{licenseId}/data-controller` (dekont ekranı). Shopper uygulamasının bunları göstermesi kardeş repo işi (kapsam dışı, #18).

**`KvkkNotice.TextVersion` bu görevde ARTMAZ:** blok `<p class="kvkk">` özetinin dışında (`IntakeFormKvkkTests` yalnız o paragrafı ve politika kutusunu özetler) ve hiçbir yayıncı bilgiyi girmeden çizilmez — deploy anında formda görünen metin değişmez. Metin işinin (v2) form paragrafı zaten veri sorumlusunu anacak; sürümü o iş artırır. Bilgiler girilmeden önce metin işinin yayında olması gerekir (Prod adımları).

**Gösterilen sürüm kayda gider (ikinci plan incelemesi; m. 10/1-a ispatı, karar 24):** blok `KvkkNotice.TextVersion`'ın dışında olduğu için kayıt anında hangi kimliğin gösterildiği sonradan ispatlanamıyordu. Blok, profilin sürümünü gizli alana (`Input.DataControllerVersion`) basar. Gönderim, o sürüm bu yayıncıda varsa onu `IntakeFormSubmission.DataControllerVersion`'a yazar; içerik `DataControllerProfileVersions`'tan okunur (Görev 21). Bayat sekme eski sürümü bildirir, doğru olan da odur. Uydurma ya da başka yayıncının sürümü null olur.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs` (`DataControllerVersionExistsAsync`, `SaveSubmissionAsync`)
- Modify: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs`, `IntakeForm.cshtml` (stil + blok + gizli sürüm alanı)
- Modify: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperBroadcastersController.cs:37, 48-61` + yeni uç
- Test: `OrderDeck.LicenseServer.Tests/Pages/Public/IntakeFormDataControllerTests.cs` (YENİ), `Controllers/Shopper/ShopperBroadcastersCodeLookupTests.cs`, `Controllers/Shopper/ShopperDataControllerTests.cs` (YENİ)

- [ ] **Adım 1: Form testini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Pages/Public/IntakeFormDataControllerTests.cs`:

```csharp
using AngleSharp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

/// <summary>KVKK K-3: kayıt formu yayıncının veri sorumlusu bilgilerini (girildiyse) aydınlatma paragrafının dışında gösterir.</summary>
public sealed class IntakeFormDataControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IntakeFormDataControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<string> SeedAsync(bool withProfile)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"dcf-{Guid.NewGuid():N}@example.test", Name = "Dcf",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow };
        db.Customers.Add(customer);
        db.Licenses.Add(new License { Id = Guid.NewGuid(), LicenseKey = $"LDK-DCF-{Guid.NewGuid():N}", CustomerId = customer.Id,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        var slug = $"d-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig { Id = Guid.NewGuid(), CustomerId = customer.Id, Slug = slug,
            WhatsAppPhone = TestPhone.NewE164(), IsActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        if (withProfile)
        {
            db.DataControllerProfiles.Add(new DataControllerProfile { CustomerId = customer.Id, LegalName = "Örnek Ticaret Ltd. Şti.",
                PostalAddress = "Örnek Mah. 1. Sok. No:2 İstanbul", KvkkEmail = "kvkk@example.test", KepAddress = "ornek@hs01.kep.tr",
                Version = 1, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "panel" });
            db.DataControllerProfileVersions.Add(new DataControllerProfileVersion { Id = Guid.NewGuid(), CustomerId = customer.Id,
                Version = 1, LegalName = "Örnek Ticaret Ltd. Şti.", PostalAddress = "Örnek Mah. 1. Sok. No:2 İstanbul",
                KvkkEmail = "kvkk@example.test", KepAddress = "ornek@hs01.kep.tr", CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "panel" });
        }
        await db.SaveChangesAsync();
        return slug;
    }

    /// <summary>Kayıt formunun gönderimi (IntakeFormKvkkTests'in alanları; kimlik bilgisi yok, değerler üretilir).</summary>
    private static FormUrlEncodedContent Submission(string token, string slug, string? dataControllerVersion)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token), new("Slug", slug),
            new("Input.InstagramUsername", $"dc_{Guid.NewGuid():N}"[..20]), new("Input.FullName", "Örnek Müşteri"),
            new("Input.Email", $"musteri-{Guid.NewGuid():N}@example.com"), new("Input.City", "İstanbul"),
            new("Input.District", "Kadıköy"), new("Input.Address", "Atatürk Cad. No:12"),
            new("Input.Phone", TestPhone.NewNational()), new("Input.Tckn", TestTckn.NewValid()),
            new("Input.WhatsAppConsent", "true"), new("Input.WhatsAppConsent", "false"),
            new("Input.KvkkAcknowledged", "true"), new("Input.KvkkAcknowledged", "false"),
            new("Input.KvkkTextVersion", KvkkNotice.TextVersion),
        };
        if (dataControllerVersion is not null) fields.Add(new("Input.DataControllerVersion", dataControllerVersion));
        return new FormUrlEncodedContent(fields);
    }

    [Fact]
    public async Task Bilgi_girildiyse_blok_cizilir_aydinlatma_paragrafinin_disinda()
    {
        var slug = await SeedAsync(withProfile: true);
        var html = await _factory.CreateClient().GetStringAsync($"/musteri-kayit/{slug}");
        var doc = await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));

        var block = doc.QuerySelector("[data-block='data-controller']")!;
        block.TextContent.Should().Contain("Örnek Ticaret Ltd. Şti.").And.Contain("Örnek Mah. 1. Sok. No:2 İstanbul")
            .And.Contain("kvkk@example.test").And.Contain("ornek@hs01.kep.tr");
        block.ClassName.Should().NotBe("kvkk", "aydınlatma paragrafının özeti (KvkkNotice.TextVersion) bu bloğu içermez");
    }

    [Fact]
    public async Task Bilgi_girilmediyse_blok_yok()
    {
        var slug = await SeedAsync(withProfile: false);
        var html = await _factory.CreateClient().GetStringAsync($"/musteri-kayit/{slug}");
        html.Should().NotContain("data-block=\"data-controller\"");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gonderim_sayfanin_gosterdigi_surumu_yazar_var_olmayan_surum_yazilmaz(bool genuine)
    {
        // KVKK m. 10/1-a, ispat yükü veri sorumlusunda (karar 24): kayıt anında hangi kimliğin gösterildiği sonradan ispatlanabilmeli.
        var slug = await SeedAsync(withProfile: true);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var html = await client.GetStringAsync($"/r/{slug}");
        var shown = Regex.Match(html, "name=\"Input\\.DataControllerVersion\" value=\"(\\d+)\"").Groups[1].Value;
        shown.Should().Be("1", "blok gösterildi, sürümü gizli alanda");

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Submission(AdminLoginHelper.ExtractAntiForgeryToken(html), slug, genuine ? shown : "99"));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var scope = _factory.Services.CreateScope();
        var sub = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().IntakeFormSubmissions.AsNoTracking()
            .SingleAsync(s => s.Config.Slug == slug);
        sub.DataControllerVersion.Should().Be(genuine ? 1 : null, "yalnız o yayıncının var olan bir sürümü yazılır");
    }
}
```

(Dosyanın üstüne `using System.Net;`, `using System.Text.RegularExpressions;`, `using Microsoft.AspNetCore.Mvc.Testing;`, `using Microsoft.EntityFrameworkCore;`, `using OrderDeck.LicenseServer.Services.IntakeForm;` ekle.)

- [ ] **Adım 2: Shopper testlerini yaz (kırmızı)**

`ShopperBroadcastersCodeLookupTests.cs` — sınıfa:

```csharp
    private sealed record ControllerDto(string LegalName, string PostalAddress, string KvkkEmail, string? KepAddress);
    private sealed record LookupWithController(Guid LicenseId, string DisplayName, ControllerDto? DataController);

    [Fact]
    public async Task Lookup_yayincinin_veri_sorumlusu_bilgisini_doner_girilmediyse_null()
    {
        var withCode = "dc-" + Guid.NewGuid().ToString("N")[..8];
        var withoutCode = "nodc-" + Guid.NewGuid().ToString("N")[..8];
        var (licenseId, _) = await SeedLicenseAsync(withCode);
        await SeedLicenseAsync(withoutCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customerId = await db.Licenses.Where(l => l.Id == licenseId).Select(l => l.CustomerId).SingleAsync();
            db.DataControllerProfiles.Add(new DataControllerProfile { CustomerId = customerId, LegalName = "Örnek Ticaret",
                PostalAddress = "Adres", KvkkEmail = "kvkk@example.test", UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "panel" });
            await db.SaveChangesAsync();
        }
        var client = _factory.CreateClient();

        var with = await client.GetFromJsonAsync<LookupWithController>($"/api/v1/shopper/broadcasters/code-lookup?code={withCode}");
        var without = await client.GetFromJsonAsync<LookupWithController>($"/api/v1/shopper/broadcasters/code-lookup?code={withoutCode}");

        with!.DataController.Should().Be(new ControllerDto("Örnek Ticaret", "Adres", "kvkk@example.test", null));
        without!.DataController.Should().BeNull();
    }
```

`OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperDataControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>KVKK K-3: bağlı müşteri, ödeme yaptığı yayıncının veri sorumlusu bilgisini görür; bağlı olmadığı yayıncınınkini değil.</summary>
public sealed class ShopperDataControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperDataControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record ControllerDto(string LegalName, string PostalAddress, string KvkkEmail, string? KepAddress);
    private sealed record Resp(ControllerDto? Profile);
    private sealed record AuthResponse(string AccessToken);

    private async Task<(Guid LicenseId, string Code)> SeedLicenseAsync(bool withProfile)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"sdc-{Guid.NewGuid():N}@x.test", Name = "Sdc",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow };
        db.Customers.Add(customer);
        var code = "sdc-" + Guid.NewGuid().ToString("N")[..8];
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License { Id = licenseId, CustomerId = customer.Id, SkuCode = "STD", IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1), LicenseKey = "key-" + Guid.NewGuid().ToString("N"), ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow });
        if (withProfile)
            db.DataControllerProfiles.Add(new DataControllerProfile { CustomerId = customer.Id, LegalName = "Örnek Ticaret",
                PostalAddress = "Adres", KvkkEmail = "kvkk@example.test", UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "panel" });
        await db.SaveChangesAsync();
        return (licenseId, code);
    }

    [Fact]
    public async Task Bagli_musteri_gorur_bagli_olmadigi_yayincida_403()
    {
        var (linked, code) = await SeedLicenseAsync(withProfile: true);
        var (unlinked, _) = await SeedLicenseAsync(withProfile: true);
        var client = _factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", new
        {
            broadcasterCode = code, fullName = "Dc Kullanici", phone = TestPhone.NewE164(), password = $"dc-{Guid.NewGuid():N}",
            address = "Ankara", platform = "youtube", username = "dckullanici",
        });
        register.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await register.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var mine = await client.GetFromJsonAsync<Resp>($"/api/v1/shopper/broadcasters/{linked}/data-controller");
        var other = await client.GetAsync($"/api/v1/shopper/broadcasters/{unlinked}/data-controller");

        mine!.Profile.Should().Be(new ControllerDto("Örnek Ticaret", "Adres", "kvkk@example.test", null));
        other.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
```

- [ ] **Adım 3: Koştur — kırmızı**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~IntakeFormDataControllerTests|FullyQualifiedName~ShopperBroadcastersCodeLookupTests|FullyQualifiedName~ShopperDataControllerTests"`
Expected: FAIL.

- [ ] **Adım 4: Form**

`Services/IntakeForm/IntakeFormService.cs` — sınıfa:

```csharp
    /// <summary>Formu oluşturan yayıncının veri sorumlusu bilgileri (KVKK K-3); girilmediyse null.</summary>
    public Task<Privacy.DataControllerView?> GetDataControllerAsync(Guid customerId, CancellationToken ct = default)
        => Privacy.DataControllerProfiles.ReadAsync(_db, customerId, ct);

    /// <summary>Sayfanın bildirdiği veri sorumlusu sürümü bu yayıncıda var mı (karar 24: yalnız varsa kayda yazılır).</summary>
    public Task<bool> DataControllerVersionExistsAsync(Guid customerId, int version, CancellationToken ct = default)
        => Privacy.DataControllerProfiles.VersionExistsAsync(_db, customerId, version, ct);
```

`SaveSubmissionAsync`'e son isteğe bağlı parametre `int? dataControllerVersion = null` ekle (`kvkkTextVersion`'dan sonra), özetine `/// <paramref name="dataControllerVersion"/>: formda gösterilen veri sorumlusu bloğunun sürümü (KVKK m. 10/1-a ispatı, karar 24); çağıran doğrular.` ve nesne başlatıcısına `KvkkAcknowledgedAt = …` satırının ALTINA `DataControllerVersion = dataControllerVersion,`.

`Pages/Public/IntakeForm.cshtml.cs` — `using Microsoft.AspNetCore.Mvc.Filters;` ve `using OrderDeck.LicenseServer.Services.Privacy;` ekle; `public IntakeFormConfig? Config { get; private set; }` satırının ALTINA:

```csharp
    /// <summary>Formu oluşturan yayıncının veri sorumlusu bilgileri (KVKK K-3); girilmediyse null ve blok çizilmez.</summary>
    public DataControllerView? DataController { get; private set; }

    /// <summary>Sayfa çizilecekse (PageResult) veri sorumlusu bilgisi yüklenir: her <c>Page()</c> dönüşüne ayrı yükleme yazılmasın.
    /// Yönlendirme ve 410 dönüşlerinde yüklenmez.</summary>
    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var executed = await next();
        if (executed.Result is PageResult && Config is not null)
            DataController = await _service.GetDataControllerAsync(Config.CustomerId, context.HttpContext.RequestAborted);
    }
```

`IntakeFormInput`'a (`KvkkTextVersion`'ın ALTINA):

```csharp
        /// <summary>Sayfanın gösterdiği veri sorumlusu bloğunun sürümü (gizli alan; KVKK m. 10/1-a ispatı, karar 24). Yalnız o
        /// yayıncının var olan bir sürümüyse kayda yazılır; bayat sekme eski sürümü bildirir, doğru olan da odur.</summary>
        public int? DataControllerVersion { get; set; }
```

Gönderim işleyicisinde `await _service.SaveSubmissionAsync(` çağrısının ÜSTÜNE:

```csharp
        // KVKK m. 10/1-a ispatı (karar 24): kayıt, sayfanın gösterdiği veri sorumlusu bloğunun sürümünü taşır. Yalnız bu yayıncının
        // var olan bir sürümü yazılır; uydurma ya da başka yayıncının sürümü null olur.
        var dataControllerVersion = Input.DataControllerVersion is { } shownVersion
            && await _service.DataControllerVersionExistsAsync(Config.CustomerId, shownVersion, ct) ? shownVersion : (int?)null;
```

ve çağrının son argümanından (`kvkkTextVersion: Input.KvkkTextVersion`) sonra `, dataControllerVersion: dataControllerVersion` ekle.

`Pages/Public/IntakeForm.cshtml` — stilde `.kvkk { … }` satırının ALTINA:

```css
        .kvkk-controller { font-size:11.5px; color:var(--od-ink); margin:14px 0 4px; }
```

`<p class="kvkk">`'nın hemen ÜSTÜNDEKİ Razor yorumunun (`@* Bu paragrafın, … *@`) ÜSTÜNE:

```cshtml
            @* Veri sorumlusunun kimliği (KVKK m. 10/1-a, ön inceleme K-3): yayıncının panelde/yönetimde girdiği bilgiler. Girilmediyse
               çizilmez. Bilerek p.kvkk DEĞİL: aydınlatma paragrafının özeti (KvkkNotice.TextVersion, IntakeFormKvkkTests) yayıncıya
               göre değişmesin. Paragrafın metni (ve sürümü) metin işinde. *@
            @if (Model.DataController is { } dc)
            {
                <p class="kvkk-controller" data-block="data-controller">
                    Veri sorumlusu: <strong>@dc.LegalName</strong>, @dc.PostalAddress · KVKK başvuru: @dc.KvkkEmail
                    @if (dc.KepAddress is { } kep)
                    {
                        <text> · KEP: @kep</text>
                    }
                </p>
                @* Gösterilen sürüm kayda gider (KVKK m. 10/1-a ispatı, karar 24). Düz input: öznitelik sırası testte sabit. *@
                <input type="hidden" name="Input.DataControllerVersion" value="@dc.Version" />
            }
```

(Blok formun İÇİNDE olmalı: `<p class="kvkk">` ve `Input.KvkkTextVersion` gizli alanı gibi. Değilse gizli alanı `Input.KvkkTextVersion` alanının yanına taşı ve aynı `@if` ile koru.)

- [ ] **Adım 5: Shopper uçları**

`Controllers/Shopper/ShopperBroadcastersController.cs` — `using OrderDeck.LicenseServer.Services.Privacy;` ekle.

`CodeLookupResponse`'u şununla değiştir:

```csharp
    /// <param name="DataController">Yayıncının veri sorumlusu bilgileri (KVKK K-3, kayıt ekranı); girilmediyse null.</param>
    public sealed record CodeLookupResponse(Guid LicenseId, string DisplayName, DataControllerView? DataController);
```

`CodeLookup` içinde:

```csharp
        var license = await _db.Licenses
            .Where(l => l.ShopperCode == normalized)
            .Select(l => new { l.Id, CustomerName = l.Customer.Name })
            .FirstOrDefaultAsync(ct);
        if (license is null) return NotFound();

        return Ok(new CodeLookupResponse(license.Id, license.CustomerName));
```
bloğunu şununla değiştir:
```csharp
        var license = await _db.Licenses
            .Where(l => l.ShopperCode == normalized)
            .Select(l => new { l.Id, l.CustomerId, CustomerName = l.Customer.Name })
            .FirstOrDefaultAsync(ct);
        if (license is null) return NotFound();

        var controller = await DataControllerProfiles.ReadAsync(_db, license.CustomerId, ct);
        return Ok(new CodeLookupResponse(license.Id, license.CustomerName, controller));
```

`Leave` metodundan ÖNCE yeni uç:

```csharp
    // ── GET /api/v1/shopper/broadcasters/{licenseId}/data-controller ─────────

    public sealed record DataControllerResponse(DataControllerView? Profile);

    /// <summary>Bağlı yayıncının veri sorumlusu bilgileri (KVKK K-3, dekont ekranı). Bağlı değilse 403 not-linked.</summary>
    [HttpGet("{licenseId:guid}/data-controller")]
    public async Task<IActionResult> GetDataController(Guid licenseId, CancellationToken ct)
    {
        var shopperId = User.GetShopperId();
        if (shopperId is null) return Unauthorized();
        if (!await _db.Shoppers.AnyAsync(s => s.Id == shopperId && s.DeletedAt == null, ct)) return Unauthorized();
        if (!await _db.ShopperBroadcasterLinks.AnyAsync(l => l.ShopperId == shopperId && l.LicenseId == licenseId && l.LeftAt == null, ct))
            return Problem(title: "not-linked", statusCode: 403);
        var customerId = await _db.Licenses.Where(l => l.Id == licenseId).Select(l => l.CustomerId).FirstAsync(ct);
        return Ok(new DataControllerResponse(await DataControllerProfiles.ReadAsync(_db, customerId, ct)));
    }
```

- [ ] **Adım 6: Koştur — yeşil**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~IntakeForm|FullyQualifiedName~Controllers.Shopper"`
Expected: PASS (`IntakeFormKvkkTests.Metin_degisirse_surum_de_degismeli` dahil — profil seed edilmiyor, blok çizilmiyor, özet aynı).

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs OrderDeck.LicenseServer/Controllers/Shopper/ShopperBroadcastersController.cs OrderDeck.LicenseServer.Tests/Pages/Public/IntakeFormDataControllerTests.cs OrderDeck.LicenseServer.Tests/Controllers/Shopper
git commit -m "feat(kvkk): yayıncının veri sorumlusu bilgisini formda ve Shopper ucunda göster

Kayıt formu bilgi girildiyse aydınlatma paragrafının dışında veri
sorumlusu bloğunu çizer ve gönderim gösterilen sürümü kayda yazar;
Shopper kod sorgusu dataController alanını, bağlı müşteri GET
{licenseId}/data-controller ucunu alır (KVKK ön inceleme K-3, P1-17).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 23: Yerel SQL yedeği şifreli ve 1 gün; HashKey .env yedeğine girmez (P1-16)

**Neden (not Ö-12, Ö-6):** VPS'teki yerel SQL yedeği bilerek şifresiz ve ~4 gün kalıyor; politika "şifreli yedekler" diyor. IBAN tanıma kodlarının anahtarı (`OrderDeck__Bank__HashKey`) `.env` gece yedeğinin içinde, SQL yedeğiyle aynı kovada ve aynı parolayla: tek parola + kova erişimi hem özetleri hem anahtarı verir.
- **SQL:** yerelde yalnız **bugünkü** şifreli kopya (`.bak.gz.gpg`) kalır — ada göre silinir, `-mtime` eşiğine değil (cron saati kayarsa dünkü kopya kalmasın). Düz `.bak`/`.bak.gz` yalnız şifrelenene kadar yaşar; betik nerede düşerse düşsün `EXIT` tuzağı siler. Şifre-çöz turunun düz ara dosyası (`encrypt_verified`'ın `.roundtrip`'i) SQL konteynerine bağlı klasörde değil, `mktemp -d` ile açılan 700 izinli çalışma klasöründe üretilir; betik `umask 077` ile başlar. Eski sürümden kalmış düz kopyalar da silinir. Uzaktaki zincir (adlar, önek, parola, budama) aynen.
- **HashKey (notun önerisi "yalnız parola yöneticisinde"):** `.env` yedeği HashKey satırı süzülerek alınır; asıl kopyası Bitwarden'da. Geri yüklemede `.env`'e elle eklenir. **Ön koşul (Burak):** Bitwarden'daki HashKey'in VPS'tekiyle aynı olduğunu doğrula — aksi hâlde host kaybında özetler eşleşmez.
- Bu betikleri CI kopyalamaz (CI yalnız `rollback-license-server.sh`'ı kopyalıyor); Burak elle kopyalar ve bir kez çalıştırıp günlüğe bakar. Cron satırları değişmez.

**Files:**
- Modify: `deploy/scripts/backup-sql-to-r2.sh` (bütünüyle)
- Modify: `deploy/scripts/backup-env-to-r2.sh:37-46`
- Modify: `deploy/README.md` (Off-host replication bölümü)

- [ ] **Adım 1: SQL betiğini yaz**

`deploy/scripts/backup-sql-to-r2.sh` içeriğini bütünüyle şununla değiştir:

```bash
#!/usr/bin/env bash
# Nightly SQL .bak → R2, GPG şifreli. See HA-PLAYBOOK.md gap G2.
#
# Şifreleme neden şart: `.bak` ham müşteri verisidir (Customers, Payments,
# GiveawayParticipants — KVKK kapsamında). Düz gzip olarak R2'de dururken
# `.env`'i şifrelemek tiyatroydu: R2 token'ını ele geçiren birinin `.env`'e
# ihtiyacı yok, veri zaten `.bak`'ta. 2026-08-22 restore tatbikatında tek bir
# *salt-okunur* token'la tüm prod veritabanı temiz bir makinede restore edildi.
#
# Yerel kopya (KVKK ön inceleme Ö-12, 2026-10): sunucunun kendi diskinde de
# YALNIZ şifreli kopya kalır, en çok 1 gün. Önceden şifresiz .bak.gz ~4 gün
# duruyordu; politika "şifreli yedekler" diyordu. Düz .bak / .bak.gz yalnız
# şifrelenene kadar yaşar: betik nerede düşerse düşsün EXIT tuzağı siler.
# Yerelden geri yükleme:
#   gpg --decrypt /opt/orderdeck/sql-data/backup/orderdeck-<tarih>.bak.gz.gpg | gunzip > x.bak
#
# Cron: 0 3 * * * /opt/orderdeck/scripts/backup-sql-to-r2.sh >>/var/log/orderdeck-sql-backup.log 2>&1
set -euo pipefail
# Betiğin yazdığı her dosya (şifreli kopya, şifre-çöz turunun düz ara dosyası) yalnız sahibine açık.
umask 077

LOG_PREFIX="[$(date -u +%Y-%m-%dT%H:%M:%SZ)]"
echo "$LOG_PREFIX backup-sql start"

# shellcheck source=lib-backup-crypt.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/lib-backup-crypt.sh"

# Parola kontrolü BACKUP DATABASE'den önce: parola yoksa yükleyemeyeceğimiz bir
# yedek için SQL'i yarım saat meşgul etmenin anlamı yok.
require_passphrase

SQL_PASSWORD=$(grep ^SQL_PASSWORD /opt/orderdeck/.env | cut -d= -f2)
STAMP=$(date -u +%Y-%m-%d)
KEEP_DAYS=30
BUCKET="s3://orderdeck-prod-backups/sql-bak"
LOCAL_DIR="/opt/orderdeck/sql-data/backup"
BAK="/var/opt/mssql/backup/orderdeck-${STAMP}.bak"
HOST_BAK="${LOCAL_DIR}/orderdeck-${STAMP}.bak"
GZ="${HOST_BAK}.gz"
CIPHER="${GZ}.gpg"
# Şifreleme ve şifre-çöz turu 700 izinli ayrı bir klasörde (aynı dosya sisteminde: büyük dosya /tmp'ye taşınmaz). LOCAL_DIR
# SQL konteynerine bağlı; düz ara dosya (.roundtrip) orada, başka izinlerle bir an bile durmasın.
WORK=$(mktemp -d "${LOCAL_DIR}/.enc.XXXXXX")
chmod 700 "$WORK"

# Düz kopyalar ve şifre-çöz turunun ara dosyası betik nasıl biterse bitsin silinir.
trap 'rm -f "$HOST_BAK" "$GZ"; rm -rf "$WORK"' EXIT

# 1) BACKUP DATABASE inside container.
#    INIT overwrites if today's file already exists (rerun-safe).
docker exec orderdeck-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$SQL_PASSWORD" -C -No \
  -Q "BACKUP DATABASE OrderDeckLicense TO DISK = N'$BAK' WITH FORMAT, INIT" \
  > /tmp/sql-backup.log 2>&1 || { echo "$LOG_PREFIX BACKUP DATABASE failed:"; cat /tmp/sql-backup.log; exit 1; }

if [[ ! -f "$HOST_BAK" ]]; then echo "$LOG_PREFIX bak file missing on host: $HOST_BAK"; exit 1; fi

# 2) Compress (Express has no native COMPRESSION; gzip externally).
gzip -9 -f "$HOST_BAK"

# 3) Encrypt in the private work dir (roundtrip file lives there too), then
#    drop the plaintext at once and move the cipher next to the backups.
#    Sıra önemli: önce sıkıştır sonra şifrele — tersi sıkışmaz.
encrypt_verified "$GZ" "$WORK/cipher.gpg"
rm -f "$GZ"
mv "$WORK/cipher.gpg" "$CIPHER"
SIZE=$(stat -c%s "$CIPHER")

# 4) Upload to R2 with date prefix.
aws s3 cp "$CIPHER" "${BUCKET}/orderdeck-${STAMP}.bak.gz.gpg" \
  --profile r2 --only-show-errors

# 5) Local retention: YALNIZ bugünkü şifreli kopya kalır (zamana değil ada
#    bakar: cron saati kayarsa dünkü kopya -mtime eşiğine takılıp kalmasın).
#    Eski sürümden kalmış düz kopyalar ve yarım kalmış çalışma klasörleri de gider.
find "$LOCAL_DIR" -maxdepth 1 -name "orderdeck-*.bak.gz.gpg" ! -name "$(basename "$CIPHER")" -delete
find "$LOCAL_DIR" -maxdepth 1 \( -name "orderdeck-*.bak.gz" -o -name "orderdeck-*.bak" \) -delete
find "$LOCAL_DIR" -maxdepth 1 -type d -name ".enc.*" ! -path "$WORK" -exec rm -rf {} +

# 6) Remote retention.
prune_dated_objects "$BUCKET" "$KEEP_DAYS"

echo "$LOG_PREFIX backup-sql done size=${SIZE}B remote=${BUCKET}/orderdeck-${STAMP}.bak.gz.gpg local=${CIPHER}"
```

- [ ] **Adım 2: `.env` yedeğinden HashKey'i süz**

`deploy/scripts/backup-env-to-r2.sh` — `CIPHER="$TMPDIR/env-${STAMP}.gpg"` ile `encrypt_verified "$ENV_FILE" "$CIPHER"` satırlarını şununla değiştir:

```bash
CIPHER="$TMPDIR/env-${STAMP}.gpg"

# IBAN tanıma kodlarının anahtarı (OrderDeck__Bank__HashKey) BİLEREK yedeğe
# girmez (KVKK ön inceleme Ö-6, 2026-10): özetlerle aynı kovada ve aynı
# parolayla durmasın. Asıl kopyası parola yöneticisinde (Bitwarden); geri
# yüklemede .env'e elle eklenir.
FILTERED="$TMPDIR/env.filtered"
{ grep -Ev '^[[:space:]]*(export[[:space:]]+)?OrderDeck__Bank__HashKey=' "$ENV_FILE" || true; } > "$FILTERED"
if [[ ! -s "$FILTERED" ]]; then
  echo "$LOG_PREFIX HATA: süzülmüş .env boş, yükleme yapılmadı"
  exit 1
fi

encrypt_verified "$FILTERED" "$CIPHER"
```

Başlık yorumuna bir satır: `# OrderDeck__Bank__HashKey satırı süzülür (Bitwarden'da): bkz. aşağıdaki FILTERED.`

- [ ] **Adım 3: README'yi güncelle**

`deploy/README.md` — "Off-host replication" bölümünde dört düzenleme.

(a) Şu cümleyi:

```markdown
Yereldeki `.bak` kopyaları bilerek şifresiz; host'a erişen zaten DB'ye erişiyor,
şifrelemek yalnızca restore'u zorlaştırırdı.
```

şununla değiştir:

```markdown
Yereldeki SQL kopyası da şifreli (`orderdeck-<tarih>.bak.gz.gpg`) ve yalnız 1 gün
tutulur (KVKK ön inceleme Ö-12, 2026-10); düz `.bak`/`.bak.gz` yalnız şifrelenene
kadar yaşar, betik düşerse de silinir.
```

(b) "Dördü birlikte anlamlı: …" paragrafının ALTINA:

```markdown
**HashKey yedeklenmez.** `.env` yedeği `OrderDeck__Bank__HashKey` satırı süzülerek
alınır: IBAN tanıma kodlarının anahtarı özetlerle aynı kovada ve aynı parolayla
durmasın (KVKK Ö-6). Asıl kopyası parola yöneticisinde; geri yüklemede `.env`'e elle
eklenir. Anahtar kaybolursa eski özetler eşleşmez, IBAN hafızası yeniden öğrenilir.
```

(c) "Geri yükleme" bölümündeki kod bloğunun ALTINA:

```markdown
`.env`'i çözdükten sonra `OrderDeck__Bank__HashKey=<parola yöneticisindeki değer>`
satırını ekleyin (yedekte yok).
```

(d) (c)'nin ALTINA yeni alt bölüm (KVKK ön inceleme Ö-12; Görev 9'daki dış kopya):

```markdown
#### Silmeleri yeniden uygula (KVKK)

Yedek, alındığı andan sonra yapılan silmeleri geri getirir. `DataDestructionRecords`
tablosu da aynı veritabanında olduğu için yedek anına sarılır: yeniden uygulamanın
dayanağı tablo değil, saatlik **"[OrderDeck] İmha kaydı özeti"** e-postalarıdır
(`DataDestructionDigestJob`; `Admin__AlertEmail` kutusu). Uygulamayı geri yüklenen
veritabanıyla açtıktan sonra, kullanıcılara açmadan önce:

1. Yedeğin anını not edin (`orderdeck-<tarih>.bak.gz.gpg` → o günün 03:00 UTC'si).
2. Hangfire panosundan `bank-data-retention`, `security-data-retention` ve
   `audit-retention`'ı elle tetikleyin: süreye bağlı silmeler kendiliğinden yeniden
   uygulanır (`bank-service-end-purge` ertesi 05:17'de kendisi koşar).
3. Yedek anından sonraki özet e-postalarını açın; her satırı **tetikleyicisine** göre
   yeniden uygulayın (`konu` = ShopperId/CustomerId, `talep` = talep kimliği):
   - `shopper-purge`: admin/shoppers/deletion-requests'te o ShopperId'nin talebi geri
     yüklenmişse yeniden "Sil"; talep yedekten sonra geldiyse satır yoktur — aynı
     ShopperId için `ShopperDeletionRequests`'e talep satırı ekleyip sayfadan işleyin.
   - `customer-purge`: `POST /api/v1/admin/customers/{konu}/purge` (onay e-postası geri
     yüklenen satırdaki adres). Obifin bağlantısı varsa önce admin/obifin → "Kaldır ve sil".
   - `subject-request`: talep satırı geri yüklenmiş ve açıksa admin/banka-veri-talepleri'nde
     yeniden "Sil ve kapat"; yoksa başvurunun aslındaki (yazılı başvuru / e-posta) IBAN'la
     yeni talep açıp silin — talep kapanınca IBAN özeti boşaldığı için kayıt IBAN taşımaz.
     Talebin kayıtlarında yalnız `BankSubjectRequests.IbanHash` hedefi varsa talep silinmemiş,
     kapatılmıştır: geri yüklenen açık talebi ölçütteki sonuçla (`talep kapandı, amaç bitti
     (<sonuç>)`) yeniden kapatın.
   - Hedefi `BankSubjectRequests.IbanHash` olan `admin-remove`, `service-end` ve
     `customer-purge` satırları: lisansın açık banka veri talebini o silme kapatmıştı; aynı
     eylemin yeniden uygulanması onu yeniden kapatır.
   - `admin-remove`: admin/obifin'de o lisans için yeniden "Kaldır ve sil" (Obifin tarafı
     zaten kaldırıldıysa liste boş döner).
   - `account-rejected`: ölçütteki `#N` reddedilen hesabın Obifin numarasıdır
     (`reddedilen hesap #N (Ö-10)`). admin/obifin'de o lisansın `#N` hesabında yeniden
     "Reddet ve verisini sil" (geri yükleme onayı da geri getirdiyse düğme "Onayı geri al ve
     verisini sil" der; yazılı onay yine hesap numarası). Bunu kullanıcılara açmadan ve ilk
     çekim turundan önce yapın: onay geri geldiyse çekim o hesabın hareketini yeniden yazar.
   - `processor-confirmation`: admin/obifin → "Obifin silme teyidi bekleyenler"de o lisansın
     kaydında yeniden "Teyit alındı" (teyit yazısı KVKK klasöründe). Kayıt yedekten sonra
     açıldıysa önce aynı lisansın `admin-remove` satırı onu yeniden açar.
   - `credential-change`: admin/obifin'de o lisansın Obifin kimliğini yeniden kaydedin.
   - `cleanup`: `bank-data-cleanup --apply` (ve yedekten sonra koşulduysa
     `--reset-iban-memory --apply`) yeniden.
   - `retention`, `service-end`: 2. adım yeterli. Eylemi `run-completed` olan satırlar
     yalnız periyodik işin koştuğunu gösterir, uygulanacak bir şey yoktur.
   Aynı satır birden çok e-postada görünebilir (gönderilip işaretlenemeyen kayıt, geri
   yüklemeden sonra yedekte gönderilmemiş kayıt yeniden gider): yeniden uygulama
   idempotenttir, ikincisi bir şey değiştirmez.
4. Son başarılı özetten sonraki kayıtlar e-postaya girmemiştir: olağan durumda saat
   başı +13'te önceki saatler gider, yani en çok ~1 saat 13 dakika; Hangfire'da
   `data-destruction-digest` Failed görünüyorduysa (SMTP, boş adres) daha fazlası. Eski
   veritabanına hâlâ erişiliyorsa
   `SELECT * FROM DataDestructionRecords WHERE DigestedAt IS NULL OR OccurredAt > '<yedek anı>'`
   çıktısını alıp aynı şekilde uygulayın; erişilemiyorsa admin/shoppers/deletion-requests,
   admin/banka-veri-talepleri ve günlükleri o aralık için elle gözden geçirin.
5. Yeniden uygulanan her silme yeni bir imha kaydı yazar; bitince bir tutanak (tarih,
   yedek anı, uygulanan satır sayısı) KVKK klasörüne eklenir.
```

- [ ] **Adım 4: Sözdizimi ve süzgeç denetimi**

Run (Bash):
```bash
bash -n deploy/scripts/backup-sql-to-r2.sh && bash -n deploy/scripts/backup-env-to-r2.sh && echo SYNTAX_OK
command -v shellcheck >/dev/null && shellcheck -x deploy/scripts/backup-sql-to-r2.sh deploy/scripts/backup-env-to-r2.sh || echo "shellcheck yok — atlandı"
S="C:/Users/burak/AppData/Local/Temp/claude/C--Users-burak-source-repos-LiveDeck/63d54caa-5d33-4ad5-8e7f-ec1fa286947b/scratchpad"
printf 'A=1\nOrderDeck__Bank__HashKey=x\n  export OrderDeck__Bank__HashKey=y\nB=OrderDeck__Bank__HashKey=z\n' > "$S/env-filter-test"
{ grep -Ev '^[[:space:]]*(export[[:space:]]+)?OrderDeck__Bank__HashKey=' "$S/env-filter-test" || true; }
rm -f "$S/env-filter-test"
# Yerel saklama: yalnız bugünkü şifreli kopya kalır (betikteki 5. adımın aynısı, sahte dosyalarla).
D="$S/retention-test"; rm -rf "$D"; mkdir -p "$D/.enc.old" "$D/.enc.now"
touch "$D/orderdeck-2026-10-18.bak.gz.gpg" "$D/orderdeck-2026-10-19.bak.gz.gpg" "$D/orderdeck-2026-10-17.bak.gz" "$D/x.txt"
LOCAL_DIR="$D"; CIPHER="$D/orderdeck-2026-10-19.bak.gz.gpg"; WORK="$D/.enc.now"
find "$LOCAL_DIR" -maxdepth 1 -name "orderdeck-*.bak.gz.gpg" ! -name "$(basename "$CIPHER")" -delete
find "$LOCAL_DIR" -maxdepth 1 \( -name "orderdeck-*.bak.gz" -o -name "orderdeck-*.bak" \) -delete
find "$LOCAL_DIR" -maxdepth 1 -type d -name ".enc.*" ! -path "$WORK" -exec rm -rf {} +
ls -A "$D"; rm -rf "$D"
```
Expected: `SYNTAX_OK`; süzgeç çıktısı yalnız `A=1` ve `B=OrderDeck__Bank__HashKey=z` (satır başı olmayan eşleşme süzülmez); saklama denemesinde kalanlar yalnız `.enc.now`, `orderdeck-2026-10-19.bak.gz.gpg` ve `x.txt`.

- [ ] **Adım 5: Commit**

```bash
git add deploy/scripts/backup-sql-to-r2.sh deploy/scripts/backup-env-to-r2.sh deploy/README.md
git commit -m "fix(backup-kvkk): yerel SQL yedeğini şifreli ve 1 gün tut, HashKey'i .env yedeğinden çıkar

Sunucu diskinde yalnız şifreli .bak.gz.gpg kalır (1 gün); düz kopyalar
şifrelenince ve hata durumunda silinir. .env gece yedeği
OrderDeck__Bank__HashKey satırını içermez (Bitwarden'da). README'ye geri
yüklemeden sonra silmeleri imha özeti e-postalarından yeniden uygulama
prosedürü eklendi. Betikler CI ile kopyalanmaz: Burak VPS'e elle kopyalar
(KVKK ön inceleme Ö-12, Ö-6, P1-16).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 24: TR/EN yürürlük tarihi eşitlik testi (P1-22)

**Neden (not Ö-17):** Türkçe politika 2026-10-19 tarihli yayında; İngilizce sayfa 2026-09-04 tarihli ve banka bölümünü içermiyor, "solely … order, shipping, and invoicing" fiilen yanlış; hiçbir test İngilizceyi karşılaştırmıyor. Test kuralı: İngilizce sayfanın yürürlük tarihi Türkçeyle **aynıdır** ya da İngilizce sayfa Türkçe metnin geçerli olduğunu, Türkçenin **doğru** yürürlük tarihiyle söyler (`TURKISH_EFFECTIVE_DATE`). Türkçe tarih değişip İngilizce unutulursa kırmızı. Test bugün yeşil kalsın diye notun önerdiği "Türkçe metin geçerlidir" uyarısı (not Ö-17, birebir öneri) İngilizce sayfaya eklenir; çevirinin kendisi avukat onayından sonra metin işinde. Repoda web test altyapısı (vitest vb.) yok: test sunucu test projesinde, `IntakeFormKvkkTests` gibi web kaynağını okur (CI'da ubuntu işinde koşar). **Metin işi İngilizce sayfayı da değiştiriyorsa** bu uyarıyı birleştirmede tek kopya tut.

**Files:**
- Modify: `web/app/(en)/en/privacy-policy/page.tsx:10, 13-20`
- Create: `OrderDeck.LicenseServer.Tests/Pages/Public/PolicyEffectiveDateParityTests.cs`

- [ ] **Adım 1: Testi yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Pages/Public/PolicyEffectiveDateParityTests.cs`:

```csharp
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

/// <summary>
/// KVKK Ö-17: İngilizce gizlilik politikası Türkçeyle aynı yürürlük tarihini taşır ya da Türkçe metnin geçerli olduğunu, Türkçenin
/// GÜNCEL yürürlük tarihiyle söyler. Türkçe metin değişip İngilizce unutulursa kırmızı. Web kaynağı okunur (web test altyapısı yok).
/// </summary>
public sealed class PolicyEffectiveDateParityTests
{
    private const string TrPath = "web/app/(tr)/gizlilik-politikasi/page.tsx";
    private const string EnPath = "web/app/(en)/en/privacy-policy/page.tsx";

    [Fact]
    public void Ingilizce_sayfa_Turkceyle_ayni_tarihte_ya_da_Turkce_metnin_gecerli_oldugunu_soyler()
    {
        var tr = Date(File.ReadAllText(FindRepoFile(TrPath)), "EFFECTIVE_DATE");
        var enSource = File.ReadAllText(FindRepoFile(EnPath));
        var en = Date(enSource, "EFFECTIVE_DATE");
        if (en == tr) return;

        Date(enSource, "TURKISH_EFFECTIVE_DATE").Should().Be(tr,
            $"İngilizce sayfa ({en}) Türkçeden ({tr}) geride: 'Türkçe metin geçerlidir' notu Türkçenin GÜNCEL tarihini göstermeli " +
            $"— {EnPath} içindeki TURKISH_EFFECTIVE_DATE'i güncelle ya da çeviriyi aynı tarihle yayımla");
        enSource.Should().Contain("prevails").And.Contain("{TURKISH_EFFECTIVE_DATE}", "not tarihi sabitten basmalı");
    }

    private static string Date(string source, string name)
    {
        var m = Regex.Match(source, $@"const {name}(?::\s*string)?\s*=\s*'(\d{{4}}-\d{{2}}-\d{{2}})';");
        m.Success.Should().BeTrue($"{name} sabiti bulunamadı");
        return m.Groups[1].Value;
    }

    private static string FindRepoFile(string relativePath)
    {
        var parts = relativePath.Split('/');
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{relativePath} bulunamadı ({AppContext.BaseDirectory} klasöründen yukarı arandı).");
    }
}
```

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PolicyEffectiveDateParityTests"`
Expected: FAIL — `TURKISH_EFFECTIVE_DATE sabiti bulunamadı`.

- [ ] **Adım 2: İngilizce sayfaya uyarıyı ekle**

`web/app/(en)/en/privacy-policy/page.tsx` — `const EFFECTIVE_DATE = '2026-09-04';` satırının ALTINA:

```tsx
/**
 * Effective date of the Turkish policy (web/app/(tr)/gizlilik-politikasi/page.tsx). While this translation lags behind, the note
 * below says the Turkish text prevails; PolicyEffectiveDateParityTests (server tests) keeps this equal to the Turkish date.
 * When the full translation is published with the Turkish effective date, delete this constant and the note.
 */
const TURKISH_EFFECTIVE_DATE = '2026-10-19';
```

`<LegalLayout …>` açılışından sonraki ilk `<p>`'nin ÜSTÜNE (koşulsuz: iki sabit literal tipinde, karşılaştırma TS2367 verirdi; çeviri yayımlanınca not elle silinir):

```tsx
      <p>
        <strong>Note:</strong> This English text is a convenience translation. The Turkish version at{' '}
        <a href="/gizlilik-politikasi">orderdeckapp.com/gizlilik-politikasi</a>, effective {TURKISH_EFFECTIVE_DATE},
        prevails. It currently contains sections not yet reflected here, including Section 1.4 on the processing of
        broadcasters&apos; bank account transactions, and the related retention periods and rights.
      </p>
```

- [ ] **Adım 3: Koştur — yeşil; web kalite kapısı**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PolicyEffectiveDateParityTests|FullyQualifiedName~IntakeFormKvkkTests"`
Expected: PASS (Türkçe sayfaya dokunulmadı; form özetleri aynı).

Run (Bash): `cd web && npm ci && npm run lint && npm run typecheck`
Expected: lint 0 uyarı, typecheck temiz. (Ana checkout'un `web/tsconfig.json`'ına DOKUNMA — bu worktree'nin kendi kopyası.)

- [ ] **Adım 4: Commit**

```bash
git add "web/app/(en)/en/privacy-policy/page.tsx" OrderDeck.LicenseServer.Tests/Pages/Public/PolicyEffectiveDateParityTests.cs
git commit -m "test(kvkk): İngilizce politikanın Türkçe yürürlük tarihini izlediğini denetle

İngilizce gizlilik politikası Türkçeyle aynı tarihte değilse Türkçe
metnin geçerli olduğunu Türkçenin güncel tarihiyle söyler; test ikisini
eşit tutar (KVKK ön inceleme Ö-17, P1-22).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Görev 25: Tam sunucu paketi (ayrık koşu)

**Files:** yok (doğrulama).

- [ ] **Adım 1: Docker'ı denetle**

Run (Bash): `docker version --format '{{.Server.Version}}'`
Expected: sürüm basılır. "NamedPipeClientStream … ConnectInternal" görürsen aşağıdaki komutta `DOCKER_HOST` satırı ZORUNLU; görmezsen o satırı çıkar.

- [ ] **Adım 2: Paketi ayrık başlat (PowerShell)**

```powershell
$s = 'C:/Users/burak/AppData/Local/Temp/claude/C--Users-burak-source-repos-LiveDeck/63d54caa-5d33-4ad5-8e7f-ec1fa286947b/scratchpad'
Remove-Item "$s/kvkk-server-suite.done", "$s/kvkk-server-suite.log" -ErrorAction SilentlyContinue
$cmd = "`$env:DOTNET_CLI_UI_LANGUAGE='en'; `$env:DOCKER_HOST='npipe://./pipe/dockerDesktopLinuxEngine'; " +
       "Set-Location 'C:/Users/burak/source/repos/LiveDeck/.claude/worktrees/kvkk-banka-kod'; " +
       "dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q 2>&1 | Out-File -Encoding utf8 '$s/kvkk-server-suite.log'; " +
       "New-Item -ItemType File '$s/kvkk-server-suite.done' | Out-Null"
Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', $cmd
```

- [ ] **Adım 3: Bitmesini bekle (Bash; 10 dakikayı aşarsa aynı komutu yeniden ver)**

```bash
S="C:/Users/burak/AppData/Local/Temp/claude/C--Users-burak-source-repos-LiveDeck/63d54caa-5d33-4ad5-8e7f-ec1fa286947b/scratchpad"
until [ -f "$S/kvkk-server-suite.done" ]; do sleep 20; done; tail -40 "$S/kvkk-server-suite.log"
```
Expected: `Passed!  - Failed: 0`. Kırmızı varsa: `StartContainerAsync`/`TaskCanceledException` zaman aşımıysa (başka oturum Docker kullanıyor) o sınıfı filtreyle tek başına yeniden koştur; kod hatasıysa ilgili görevin adımına dön, düzelt, ayrı commit at.

- [ ] **Adım 4: Hijyen ve kapsam taraması**

Run (Bash):
```bash
git log --oneline origin/master..HEAD
git diff --stat origin/master..HEAD
git diff origin/master..HEAD -- OrderDeck.LicenseServer.Tests | grep -nE "TR[0-9]{24}|[0-9]{11}|Password *= *\"[^\"]+\"" || echo "LITERAL_YOK"
git diff origin/master..HEAD -- 'OrderDeck.LicenseServer/Data/Migrations/*.cs' | grep -nE "DropColumn|DropTable|RenameColumn|AlterColumn" || echo "DARALTMA_YOK"
```
Expected: plan commit'leri + her görevin commit adımı başına bir commit (Görev 9'da iki; inceleme düzeltme commit'leri bunlara ek; P0 ayrı PR'a alındıysa bu dalda yalnız P1'inkiler) — sayıyı görev listesindeki "Commit" adımlarıyla karşılaştır; `LITERAL_YOK` (eşleşme çıkarsa satıra bak: üretilmiş değer mi, literal mi); `DARALTMA_YOK`. Ayrıca `git grep -n "bankaapi/sil\|RemoveBankConnectionAsync" OrderDeck.LicenseServer` yalnız `BankConnectionTerminator.RemoveFromObifinAsync`'i ve istemciyi göstermeli (Obifin'de kaldırmanın tek yolu).

- [ ] **Adım 5: Sonuç**

Push ve PR yok (Burak isterse). Burak'a özet: commit listesi, paket sonucu, "Prod adımları" bölümü, karar listesi.

---

## Not maddesi → görev eşlemesi

| Not §5.1 | Görev |
|---|---|
| P0-1 lisans bitince çekimi durdur, Disabled + DisabledAt | 1 |
| P0-2 giden ve sıfır tutarlı hareketi yazma (yön yalnız işaretli tutardan); mevcut satırları sil | 2, 8 |
| P0-3 VKN/TCKN kodu yazımını kaldır, eski değerleri boşalt | 3, 8 |
| P0-4 ham kayıt beyaz listesi, 30 gün | 5, 8 |
| P0-5 açıklamada IBAN/TCKN maskeleme (sınırsız kalıp + mod-97) | 4, 8 |
| P0-6 MaskIban kısa değer; mevcut kısa/eski maskeler | 6, 8, 11 |
| P0-7 ilk çekim bağlantı anına; bağlanmamış ilk çekim satırlarını sil | 7, 8 |
| P1-8 purge'ün banka adımı (kanıtında kullanıcı adı geçen hareketler dahil) | 15 |
| P1-9 IBAN ile arama/dışa aktarma/silme, talep kaydı, doğrulama yöntemi, son tarih; veri silinince açık talebin kapanması; sonuç kategorileri (yanıtlandı / doğrulanamadı / kanuni gerekçeyle ret / geri çekildi), silmenin yalnız silme talebinde yapılması, CSV kaçışında sekme ve satır başı; yönetici sütunlarında kimlik; silme taleplerinde son tarih | 17, 18, 20 |
| P1-10 durdur / kaldır ve sil (Obifin tarafı lisansa özgü, paylaşılan kullanıcıda Obifin'e gidilmez; lisans anahtarıyla yazılı onay); durdurulmuş ya da yerel verisi silinmiş bağlantıda eşleştirme, dekont bağdaştırması ve IBAN öğrenmenin de durması (`MatchingActive`); hizmet sonu + 30 gün işi (yalnız yerel, partili, lisans yenilenince silmez; gerçek SQL testi); Obifin bağlantılı yayıncı silmesinin reddi; Obifin `bankaapi/sil` (hareket silme ucu yok → yazılı teyit kaydı `ObifinCopyDeletions`, günlük hatırlatma, 14 günde GECİKTİ) | 13, 14 |
| P1-11 saklama işlem tarihinden; eşleşmeyen 30 gün (yalnız dekont bağı olan da "eşleşen"); eşleştirme satırı 180 gün; IBAN bağı LastConfirmedAt + 12 ay; gerçek SQL Server testi; ilk koşunun beklenen sayımı (Prod adımları) | 10 |
| P1-12 NormalizeIban (harf/rakam, TR 26, mod-97), sayaç, maskeli kaynaklı özet temizliği; sıfırlamada özetlerin kalması ve yeniden öğrenme kapısı | 11 |
| P1-13 lisans başına izinli hesap listesi, uyarı; onaysız hesabın reddi ve P1 öncesi verisinin silinmesi, onayın geri alınması | 12, 13 |
| P1-14 kalıcı imha kaydı ≥ 36 ay (yayıncı silmesi, kimlik değişimi, temizlik komutu dahil), kişisel veri içermeyen olaylarla sınırlı denetim muafiyeti, iş düşerse e-posta, veritabanı dışındaki saatlik kopya | 9 |
| P1-15 (denetim) sayfa görüntüleme denetimi; kullanıcı adı yerine kimlik; eski satırların çevrilmesi; KVKK silmesinin (ve IBAN talebinin) denetimdeki kimliği boşaltması | 16 (IBAN talebi: 17) |
| P1-16 yerel SQL yedeği şifreli/yalnız bugünkü; HashKey ayrı; geri yüklemeden sonra silmeleri yeniden uygulama prosedürü | 23 (+ 9) |
| P1-17 veri sorumlusu bilgileri: model, admin, panel, form, Shopper ucu; bilgi olmadan banka modülünün açılmaması (fail-closed, pilot bayrağı); gösterilen sürümün kaydı; ödeme mesajına basılması (WPF) | 21, 22; ödeme mesajı kapsam dışı tablosunda (WPF satırı) |
| P1-19 FCM gövdesinden tutar ve ret gerekçesi | 19 |
| P1-22 TR/EN yürürlük tarihi eşitlik testi | 24 |
| P2-25 silme talebindeki telefon (ve kapanmış banka talebinin maskesi); işin imha ve koşu kaydı | 20 |
| P1-15 (iki aşamalı doğrulama), P1-18 / K-8 (Shopper, panel, WPF ödeme şablonu), P1-21 (banka kimliği girişini kaldırma ya da işletenin lisansıyla sınırlama), `CounterpartyTaxIdHash` daraltması, HashKey'li eski `.env` yedekleri | Kapsam dışı tablosu: takip planı ve sahibi orada |




