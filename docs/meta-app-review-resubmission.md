# Meta App Review — `pages_manage_engagement` yeniden başvuru

2026-07-26 sonucunda `pages_manage_engagement` **"Screencast Not Aligned with
Use Case Details"** ile, `pages_manage_metadata` ise **"Disallowed Use Case
Details"** ile reddedildi. Bu doküman ikinci turun malzemesini toplar.

## Ne değişti

| Konu | Önceki başvuru | Bu tur |
|---|---|---|
| FB moderasyon UI | **Yoktu** — `FacebookModerationService` hiçbir komuta bağlı değildi, dolayısıyla kayıtta gösterilecek bir akış yoktu | Chat sağ-tık menüsünde "FB'de yorumu sil" + "FB'de kullanıcıyı banla" |
| `pages_manage_metadata` | İsteniyordu ("canlı yayın durumunu okumak için") | **İstenmiyor** — o işi `pages_read_engagement` yapıyor, biz webhook değil SSE kullanıyoruz |
| "Is Facebook Login integrated on this platform?" | `No` (reviewer notlarıyla çelişiyordu) | **`Yes`** |

## Screencast çekim senaryosu

Ekran kaydını 1080p, tek monitör, imleç görünür al. Arayüz Türkçe; Meta'nın
Screen Recording Guide'ı bunu yasaklamıyor ama her adımı **İngilizce
altyazı/başlıkla** anlatmak şart — reviewer ekranda ne olduğunu okuyabilmeli.

Meta'nın istediği 5 madde ve karşılığı:

1. **Complete Meta login flow** — Settings → Facebook → "Connect to Facebook" →
   tarayıcıda Facebook Login for Business diyaloğu açılır. Login ekranını
   baştan sona göster (kullanıcı adı/parola alanını bulanıklaştırma, sadece
   yazarken klavyeyi gösterme).
2. **User granting app access** — izin onay ekranında `pages_manage_engagement`
   satırının göründüğü kareyi **birkaç saniye beklet**. Reviewer bunu arıyor.
3. **End-to-end use case** —
   a. Page'de canlı yayın başlat.
   b. OrderDeck'te "Start Stream".
   c. İzleyici yorumlarının chat paneline gerçek zamanlı düştüğünü göster
      (yorum yazan kişinin adı görünür — `Business Asset User Profile Access`
      onaylandı).
   d. Uygunsuz bir yoruma **sağ tıkla → "FB'de yorumu sil"** →
      onay diyaloğu → Evet.
   e. **Facebook tarafına geç** ve yorumun gerçekten silindiğini göster.
      Bu adım geçen sefer eksikti; en kritik kare bu.
   f. Aynı kullanıcıya **sağ tıkla → "FB'de kullanıcıyı banla"** → onay → Evet →
      Page ayarlarında engellenenler listesinde göründüğünü göster.
   g. **Sağ tıkla → "FB'de banı kaldır"** → banın kalktığını göster.
      Bu adım zorunlu değil ama işlemin geri alınabilir olduğunu kanıtlar;
      reviewer'ın "kullanıcıya zarar veriyor mu" endişesini doğrudan karşılar.
4. **Screen Recording Guide** — her tıklamadan önce ne yapacağını anlatan
   İngilizce altyazı/başlık ekle. Menü kalemleri Türkçe olduğu için hangi
   butonun ne işe yaradığını yazıyla açıkla.
5. **Server-to-server notu** — gerekmiyor; bu bir masaüstü uygulaması ve
   frontend Meta login akışı görünür durumda. Yine de submission notuna
   "Desktop app; Facebook Login for Business flow is visible in the
   screencast" cümlesini ekle.

## Başvuru formunda düzeltilecekler

- `pages_manage_metadata` isteğini **kaldır**.
- "Is Facebook Login integrated on this platform?" → **Yes**.
- Reviewer talimatlarındaki installer sürümünü güncelle (0.3.9 → yeni sürüm).
- Test hesabı kimlik bilgilerinin hâlâ geçerli olduğunu doğrula.

## `pages_manage_engagement` gerekçe metni (güncellenmiş)

> OrderDeck is a Windows desktop application for live-stream sellers. During a
> Facebook Live broadcast on the seller's own Page, viewers post comments to
> place product orders and to interact with the seller. We use
> `pages_manage_engagement` to let the seller moderate that live comment stream
> from inside OrderDeck.
>
> In the app's live chat panel the seller right-clicks a comment and chooses
> "Delete comment on Facebook" or "Ban user on Facebook". Deleting calls
> `DELETE /{comment-id}`; banning calls `POST /{page-id}/blocked` with the
> commenter's page-scoped id. Both actions are confirmed by a dialog first and
> only ever run in response to the seller's explicit click.
>
> This matters because order comments and spam arrive in the same stream at the
> same rate. If the seller cannot remove abusive or spam comments, genuine
> orders get buried and are lost — moderation is what keeps the sales chat
> usable. The permission is used only on the seller's own Page. Comment data is
> not used for advertising, is not shared with third parties, and is not
> combined with data from other sources.

## Notlar

- Reddedilen izin Live Mode'da normal kullanıcılara verilmez, ama **app'te rolü
  olan kullanıcılar (Administrator/Developer/Tester) onaysız izinleri de
  verebilir** → kaydı kendi hesabınla çekebilirsin, moderasyon gerçekten çalışır.
