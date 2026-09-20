using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.Iys;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>Doğrulama sonucu. <b>Üç</b> değer, iki değil — gerekçe için
/// <see cref="NetgsmAccountVerifier"/>.</summary>
public enum NetgsmVerifyOutcome
{
    /// <summary>Abone no + şifre + marka kodu üçlüsü İYS tarafından kabul edildi.</summary>
    Ok,

    /// <summary>İYS kimliği/markayı KESİN olarak reddetti (code 30/60).</summary>
    Rejected,

    /// <summary>Şu an cevap alınamadı. Hesap hakkında hiçbir şey öğrenilmedi.</summary>
    Unavailable,
}

/// <param name="Message">Panelde yayıncıya gösterilecek insan okunur açıklama.
/// <b>Ham İYS gövdesi buraya yazılmaz.</b> Gerekçe iki ayaklı: API şifresi
/// <i>istek</i> gövdesinin <c>header</c>'ında gidiyor (bkz.
/// <c>NetgsmIysClient.PostAsync</c>) — <i>yanıt</i> gövdesinde şifre
/// beklemiyoruz; ama yanıt gövdesi de ham sağlayıcı verisidir: yayıncıya
/// gösterilecek bir metin değil ve <c>LastError</c> sütununu taşırır.</param>
public sealed record NetgsmVerifyResult(NetgsmVerifyOutcome Outcome, string? Message);

/// <summary>
/// Bir Netgsm/İYS kurulumunu tek <c>/iys/search</c> çağrısıyla doğrular.
/// Abone no, API şifresi ve marka kodu gövdede birlikte gittiği için tek çağrı
/// üçünü birden sınar (spec §2.2). <b>Test SMS'i atılmaz.</b>
///
/// <para><b>Neden üç sonuç.</b> "Reddedildi" ile "ulaşamadım" aynı kovaya
/// düşerse, İYS'nin yarım saatlik bir arızası günlük işin o turunda çalışan
/// BÜTÜN yayıncıları <c>Failed</c>'a çeker: kampanyalar durur, onay toplama
/// durur ve hiçbiri kendiliğinden geri gelmez (yayıncının panele girip
/// kaydetmesi gerekir). Yalnız <see cref="IysConfigurationException"/> kesin
/// karardır; başka her şey <see cref="NetgsmVerifyOutcome.Unavailable"/>'dır
/// ve hesabın durumuna DOKUNMAZ.</para>
///
/// <para><b>Başlık doğrulanmaz.</b> <c>Header</c> bu çağrının payload'ında yok;
/// Netgsm onaysız başlığı yalnız gönderim anında reddediyor. <c>Verified</c>
/// "başlık onaylı" anlamına GELMEZ.</para>
/// </summary>
public sealed class NetgsmAccountVerifier
{
    /// <summary>
    /// Doğrulama sorgusunun alıcısı. <c>/iys/search</c> en az bir alıcı istiyor
    /// ama salt-okunur: hiçbir onay oluşturmaz, değiştirmez.
    ///
    /// <para>Sabit ve <b>tahsis edilmemiş</b> bir numara seçildi (TR'de 500
    /// operatör öneki kullanımda değil). Gerçek bir müşterinin numarasını
    /// kullanmak, o kişiyi ilgisiz bir doğrulama turunun günlüklerine
    /// düşürürdü — KVKK'da veri minimizasyonunun tam tersi.</para>
    /// </summary>
    public const string ProbeRecipient = "+905000000000";

    private readonly IIysClient _iys;
    private readonly ILogger<NetgsmAccountVerifier> _log;

    public NetgsmAccountVerifier(IIysClient iys, ILogger<NetgsmAccountVerifier> log)
    {
        _iys = iys;
        _log = log;
    }

    public async Task<NetgsmVerifyResult> VerifyAsync(
        IysAccountContext account, CancellationToken ct = default)
    {
        try
        {
            var result = await _iys.SearchAsync(account, new[] { ProbeRecipient }, ct);
            if (result.Code == "0")
                return new NetgsmVerifyResult(NetgsmVerifyOutcome.Ok, null);

            // `Rejected` dalıyla simetrik günlük. Bu olmadan EN ZOR teşhis
            // edilen vaka hiç iz bırakmıyor: geçerli JSON ama `code` alanı yok
            // → `Sanitize` panele "tanınmayan yanıt" yazar, ham kod hiçbir yere
            // düşmez ("bütün yayıncılar Unavailable'a düştü, sebep ne?"
            // sorusu sunucu günlüğünden cevaplanamaz).
            // Yeni bir sır riski AÇMAZ: sunucu günlüğü yayıncıya gösterilmiyor
            // ve API şifresi İSTEK gövdesinde, yanıtta değil. Yine de kırpıyoruz
            // — `Code` sınırsız uzunlukta olabilir, gerekçe için `Sanitize` doc'u.
            var code = result.Code ?? "";
            _log.LogWarning(
                "Netgsm doğrulaması beklenmeyen yanıt: lisans={LicenseId} kod={Code} uzunluk={Length}",
                account.LicenseId, code.Length > 200 ? code[..200] : code, code.Length);

            return new NetgsmVerifyResult(NetgsmVerifyOutcome.Unavailable,
                $"İYS beklenmeyen yanıt kodu döndürdü ({Sanitize(result.Code)}). "
                + "Sorun sürerse Netgsm'e danışın.");
        }
        catch (IysConfigurationException ex)
        {
            // ex.Message'ı DEĞİL sabit metni döndürüyoruz: istisna mesajı ileride
            // ham gövdeyi taşımaya başlarsa ham SAĞLAYICI YANITI LastError'a,
            // oradan da panele düşerdi — gerekçenin tamamı için
            // `NetgsmVerifyResult.Message` doc'u.
            _log.LogWarning("Netgsm doğrulaması reddedildi: lisans={LicenseId} kod={Code}",
                account.LicenseId, ex.Code);
            // `_` dalı bugün ERİŞİLEMEZ (`NetgsmIysClient.PostAsync` yalnız
            // 30/60'ı fırlatıyor) ama "kod 30" diye sabitlemiyoruz: oraya üçüncü bir
            // kod eklendiği gün bu metin yayıncıya YANLIŞ işi yaptırırdı —
            // olmayan bir şifre sorununu kovalar.
            return new NetgsmVerifyResult(NetgsmVerifyOutcome.Rejected, ex.Code switch
            {
                "60" => "İYS marka kodu bu Netgsm hesabına ait değil (kod 60). "
                        + "Marka kodunu İYS panelinden kontrol edin.",
                "30" => "Netgsm abone numarası veya API şifresi reddedildi (kod 30).",
                _ => $"İYS kurulumu reddedildi (kod {Sanitize(ex.Code)}). "
                     + "Abone numarası, API şifresi ve marka kodunu kontrol edin.",
            });
        }
        // İptal GERÇEKTEN istendiyse yutma: kapanış turu her hesaba
        // "ulaşılamadı" yazmamalı. Muhafız gövdede değil `when`'de: istisna
        // filtresi BİRİNCİ GEÇİŞTE, yığın çözülmeden çalışır — `false` dönerse
        // bu çerçeveye hiç girilmez, `true` dönerse asıl fırlatma noktası
        // korunur. Gövdeye yazılan `throw;` ise async'te "End of stack trace
        // from previous location" sınırı ekleyip izi bulandırırdı. Depoda aynı
        // desen 6 yerde böyle yazılı; en yakını kardeş işler
        // `IysConsentVerifyJob` ve `IysConsentPushJob`.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // `TaskCanceledException` DEĞİL atası. `IIysClient` bir ARAYÜZ:
        // `ct.ThrowIfCancellationRequested()` çağıran bir uygulama düz
        // `OperationCanceledException` fırlatır, dar tip onu kaçırırdı. Buradaki
        // kazanç yalnız ÇAĞIRANIN `ct`'si İPTAL EDİLMEMİŞKEN gelen iptal
        // istisnasıdır — gerçek iptali yukarıdaki muhafız zaten alıp yeniden
        // fırlatıyor. Örnek: istemci kendi içinde istek başına zaman aşımı
        // jetonu bağlarsa, o jeton yandığında çağıranın `ct`'si sağlamdır ve
        // bu geçici arıza `Unavailable` olmalı, dışarı kaçmamalı. Bugünkü tek
        // istemcide erişilemez (`HttpClient` zaman aşımı `TaskCanceledException`
        // atıyor) ama ata tipi yazmanın maliyeti sıfır.
        // `JsonException` dalı SAVUNMA amaçlı: bugünkü `NetgsmIysClient` onu iki
        // yerde kendi içinde yutuyor (`SearchAsync` ayrıştırması + `ReadCode`),
        // geriye yalnız `JsonSerializer.Serialize` kalıyor ve o pratikte
        // fırlatmaz. Ama arayüzün başka bir uygulaması ayrıştırma hatasını
        // dışarı verebilir; o gün yayıncının kurulumu kapanmamalı.
        catch (Exception ex) when (ex is HttpRequestException
                                     or OperationCanceledException
                                     or System.Text.Json.JsonException)
        {
            _log.LogWarning(ex, "Netgsm doğrulaması ulaşılamadı: lisans={LicenseId}",
                account.LicenseId);
            // Yalnız OLGU: "ne oldu". "Bundan sonra ne olacak" cümlesi
            // BİLEREK yok — cevabı çağırana göre değişiyor. Günlük işte satır
            // `Verified` kalır ve yarın yeniden taranır; panel yolunda
            // `UpsertAsync` doğrulamadan ÖNCE `Failed` yazmıştır ve o satır bir
            // daha taranmaz. Sözleşmeyi buraya yazsaydık ikisinden birinde
            // yayıncıya yalan söylerdik; doğrulayıcı kendisini kimin
            // çağırdığını bilmemeli, o bilgi çağırandadır. Cümleyi ekleyen
            // yerler: `NetgsmAccountVerifyJob` ve `PanelNetgsmAccountController`.
            return new NetgsmVerifyResult(NetgsmVerifyOutcome.Unavailable,
                "İYS'ye şu an ulaşılamadı, kurulumunuz doğrulanamadı.");
        }
    }

    /// <summary>
    /// <c>result.Code</c> HER ZAMAN kısa bir kod değildir.
    /// <c>NetgsmIysClient.ReadCode</c>, gövdede <c>code</c> alanı bulamazsa
    /// <b>bütün gövdeyi</b> kod diye döndürüyor — üstelik
    /// <c>NetgsmIysClient.PostAsync</c> bunu <b>kırpılmamış</b> gövde üzerinde
    /// çağırıyor; 2000 karakterlik sınır yalnız döndürülen <c>RawBody</c>'ye
    /// uygulanıyor, yani <c>Code</c> sınırsız uzunlukta olabilir. Ağ geçidi bir
    /// HTML hata sayfası dönerse o HTML aynen <c>LastError</c>'a yazılır: hem
    /// 500 karakterlik sütunu taşırır (<c>DbUpdateException</c>), hem ham
    /// sağlayıcı yanıtını yayıncının paneline taşır. Yalnız kısa, rakamsal
    /// kodları göster: uzunluk eşiği rastgele değil — kısa rakamsal kod
    /// yayıncının Netgsm'e danışırken söyleyeceği tek somut bilgidir, o
    /// aralığın dışındaki her şey ham gövdedir.
    /// </summary>
    private static string Sanitize(string? code)
        => !string.IsNullOrWhiteSpace(code) && code.Length <= 8 && code.All(char.IsAsciiDigit)
            ? code
            : "tanınmayan yanıt";
}
