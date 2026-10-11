namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Ödeme işi SQL'inin birden çok depoda paylaşılan parçaları. Aynı kural iki yerde ayrı yazılsaydı
/// biri değişip öbürü unutulurdu — kopya taşıması ile kişi birleştirmesi aynı şeyi yapıyor: aynı kişi
/// ve kapsamda ikinci bir iş.
/// </summary>
internal static class PaymentJobSql
{
    /// <summary>İş para taşıyor mu (WHERE/CASE koşulu, satırın kendi kolonları üzerinde). C# karşılığı
    /// <see cref="PaymentJob.CarriesMoney"/> — ikisi aynı kalmalı.
    ///
    /// <para>Belirsiz (<c>apply_uncertain</c>), geri alma bekleyen (<c>reverse_pending</c>) ve tutarı
    /// sıfırdan büyük uygulanmış iş. Tutarı boş 'applied' satırı (sağlıklı akışta oluşmaz) temkinle
    /// para taşıyor sayılır: uzlaştırma onu yeniden oynatıp gerçeği öğrenir.</para></summary>
    public const string CarriesMoney =
        "(State IN ('apply_uncertain', 'reverse_pending') " +
        "OR (State = 'applied' AND (AppliedAmount IS NULL OR CAST(AppliedAmount AS REAL) > 0)))";

    /// <summary>
    /// Bir işi miras kapsamına indirir — çağıran <c>WHERE</c> ekler; parametre <c>@now</c>.
    /// <list type="bullet">
    /// <item><c>ScopeKey = 'legacy:{ApplyKey ya da Id}:{Id}'</c>: iş Id'si adı kendiliğinden benzersiz
    ///   kılar (UX_PaymentJob_Scope çakışmaz). Adı hiçbir yol ayrıştırmaz (miras taraması
    ///   <c>LIKE 'legacy:%'</c>, yeniden oynatma <c>ApplyKey</c> kolonunu okur).</item>
    /// <item><c>PriorScopeKey</c> (göç 047): indirilen işin ASIL kapsamı — banka beklentisi (W2)
    ///   kaybeden işin beklentisini bununla iptal eder; miras uzlaştırması onu yalnız bu kapsamın
    ///   tıklamasında ele alır. İlk indirme korunur.</item>
    /// <item>Para taşıyan iş AÇIK bırakılır (<c>ClosedAt = NULL</c>, kapalı olsa bile): miras
    ///   uzlaştırması (<c>PaymentRequestService</c>) onu asıl kapsamının sıradaki onaylı tıklamasında
    ///   kendi müşteri Id'siyle kesinleştirir ve fazla düşümü geri alır. Eskiden taşıma anahtarlı ama
    ///   KAPALI işi kapalı bırakıyordu; miras taraması yalnız açık işlere baktığı için o düşüm hiç
    ///   geri alınmıyordu.</item>
    /// <item>Gerisi (parasız) kapanır: geri alınacak bir şey yok.</item>
    /// <item>Teslim kaydı (<c>DeliveredAt</c>/<c>DeliveredNet</c>/<c>Deliveries</c>/<c>SendAttempts</c>)
    ///   SİLİNMEZ: indirilen işin isteği müşteriye gitmişse kapsamın sıradaki tıklaması bunu görüp sorar.
    ///   Önceki sürümün (geri dönüş) yalnız ClosedAt'la bıraktığı teslim (<see cref="ClosedByLaterDelivery"/>)
    ///   netiyle birlikte teslim kaydına geçirilir (<see cref="FreezeDelivery"/>): miras kapsamında ClosedAt
    ///   artık teslim sayılmaz, para taşıyan iş için de az sonra silinir.</item>
    /// </list>
    /// SQLite UPDATE'in bütün atamaları satırın ESKİ değerlerini okur — koşuldaki kapsam indirme öncesidir.
    /// </summary>
    public const string DemoteToLegacy =
        "UPDATE PaymentJob SET " + FreezeDelivery +
        "PriorScopeKey = COALESCE(PriorScopeKey, ScopeKey), " +
        "ScopeKey = 'legacy:' || COALESCE(ApplyKey, Id) || ':' || Id, " +
        "ClosedAt = CASE WHEN " + CarriesMoney + " THEN NULL ELSE COALESCE(ClosedAt, @now) END, " +
        "UpdatedAt = @now ";

    /// <summary>
    /// Teslim kaydını DONDURUR — işi yeniden açan (ClosedAt'ı silen) ya da tutar alanlarını
    /// (ProductTotal/AppliedAmount) değiştiren HER yazımın SET listesine eklenir (sonunda virgül var).
    /// C# karşılığı <see cref="PaymentJob.FreezeDelivery"/> (taklit depo) — ikisi aynı kalmalı.
    /// <list type="bullet">
    /// <item>Önceki sürümün teslimi (<see cref="ClosedByLaterDelivery"/>): kapanış teslim kaydına geçer
    ///   (DeliveredAt = ClosedAt). Yeniden açma ClosedAt'ı siler; geçmeseydi iz tümden kaybolur, sıradaki
    ///   tıklama sorusuz yeniden gönderirdi.</item>
    /// <item>Net: önceki sürümün teslimiyse ya da teslim kaydı var ama neti yoksa, işin O ANKİ
    ///   alanlarından (<c>od_payment_net</c>, kayıpsız). Kapalı işte alanlar gönderilen mesajın
    ///   alanlarıdır; yazım onları değiştirdikten sonra okuyucu teslim netini türetemez — soru müşteriden
    ///   hiç istenmemiş bir tutarı "istendi" diye gösterirdi.</item>
    /// </list>
    /// SQLite atamaları satırın ESKİ değerlerini okur: net, yazımın değiştirdiği alanlardan ÖNCEKİdir.
    /// </summary>
    public const string FreezeDelivery =
        "DeliveredAt = CASE WHEN " + ClosedByLaterDelivery + " THEN ClosedAt ELSE DeliveredAt END, " +
        "DeliveredNet = CASE WHEN " + ClosedByLaterDelivery + " OR (DeliveredAt IS NOT NULL AND DeliveredNet IS NULL) " +
        "THEN od_payment_net(ProductTotal, AppliedAmount) ELSE DeliveredNet END, ";

    /// <summary><c>od_payment_net</c> (<see cref="SqliteSearchFunctions"/>): ProductTotal − AppliedAmount,
    /// ondalık metin olarak (<c>PaymentJob.RequestedNet</c> ve depo yazımlarıyla aynı biçim — invariant
    /// <see cref="decimal"/>). Okunamayan tutar NULL döner: göçü ya da yazımı düşürmek yerine okuyucu işin
    /// alanlarına düşer.</summary>
    internal static string? NetText(string? productTotal, string? appliedAmount)
    {
        if (!TryDec(productTotal, out var total)) return null;
        var applied = 0m;
        if (appliedAmount is not null && !TryDec(appliedAmount, out applied)) return null;
        return (total - applied).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryDec(string? text, out decimal value)
        => decimal.TryParse(text, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    /// <summary>Önceki sürümün teslimi (koşul, satırın kendi kolonları üzerinde). C# karşılığı
    /// <c>PaymentJob.LastDeliveredAt</c>'in kuralı — ikisi aynı kalmalı.
    ///
    /// <para>Önceki sürüm (Velopack geri dönüşü; göç 047 kolonları durur) teslimde teslim kaydı yazmaz,
    /// yalnız ClosedAt'ı doldurur. Legacy olmayan kapsamda ClosedAt'ı iki sürümde de yalnız teslim yazar;
    /// bu sürümün teslimi ClosedAt'ı teslim anından sonraya hiç taşımaz. Yani ClosedAt teslim kaydından
    /// yeniyse (ya da teslim kaydı yoksa) o kapanış önceki sürümün teslimidir ve en sonuncusudur — net
    /// tutar işin alanlarındadır (kapalı işte alanlar gönderilen mesajın alanlarıdır).</para></summary>
    public const string ClosedByLaterDelivery =
        "(ClosedAt IS NOT NULL AND (DeliveredAt IS NULL OR ClosedAt > DeliveredAt) " +
        "AND ScopeKey <> 'legacy' AND ScopeKey NOT LIKE 'legacy:%')";
}
