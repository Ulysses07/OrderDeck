using System.Text;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// JWT yapılandırmasını <b>açılışta</b> doğrular ve geçersizse sunucuyu hiç
/// başlatmaz (<c>ValidateOnStart</c>).
///
/// Neden açılışta: imzalama anahtarı yanlışsa bunun tek belirtisi üretilen
/// token'ların sahte olabilmesidir — çalışan sunucuda hiçbir alarm çalmaz.
/// <c>/healthz</c> anonim olduğu için yanlış yapılandırılmış deploy sağlık
/// kontrolünden geçer, kırmızı yanan hiçbir şey olmaz ve hata ancak
/// sömürüldüğünde fark edilir. Açılışta patlamak bu sessizliği bozar:
/// konteyner ayağa kalkmaz, deploy görünür şekilde başarısız olur.
///
/// Somut senaryolar:
/// <list type="bullet">
/// <item><description>
/// <c>docker-compose.yml</c>'deki <c>Jwt__SecretKey: "${JWT_SECRET}"</c>, .env
/// dosyasında değişken yoksa <b>boş string</b>'e çözülür — ve boş değer
/// appsettings'teki değeri de ezer. Anahtar sessizce yok olur.
/// </description></item>
/// <item><description>
/// Ortam değişkeni hiç verilmezse <c>appsettings.json</c>'daki placeholder
/// kullanılır. O değer bu depoda, depo da <b>public</b>: yani anahtar
/// herkesin elinde. Customer, admin ve shopper token'ları aynı anahtarla
/// imzalandığı için tek bir sahte token her üç yüzeyi birden açar.
/// </description></item>
/// </list>
/// </summary>
public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    /// <summary>
    /// <c>appsettings.json</c> ile gelen yer tutucu. Üretimde bu değerin
    /// görülmesi "ortam değişkeni hiç ulaşmamış" demektir.
    /// </summary>
    public const string Placeholder =
        "REPLACE-WITH-256-BIT-RANDOM-SECRET-IN-PRODUCTION-AT-LEAST-32-CHARS";

    /// <summary>
    /// HS256'nın anahtar boyu 256 bit. Daha kısa anahtar algoritmanın vaat
    /// ettiği güvenlik seviyesini vermez — imza "HMAC-SHA256" yazar ama
    /// gerçekte taşıdığı direnç anahtar kadardır.
    /// </summary>
    public const int MinSecretKeyBytes = 32;

    /// <summary>
    /// Eşik bilerek çok düşük: 32 karakterlik rastgele bir anahtarda ayrık
    /// karakter sayısı tipik olarak 20'nin üstündedir, yani bu kural gerçek
    /// bir anahtarı asla reddetmez. Yakaladığı şey "aaaa…" ya da "12341234…"
    /// gibi uzunluk şartını doldurmak için yazılmış doldurma değerler.
    /// </summary>
    public const int MinDistinctChars = 8;

    private readonly bool _isProduction;

    public JwtOptionsValidator(IHostEnvironment env) => _isProduction = env.IsProduction();

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
        => Validate(options, _isProduction);

    /// <summary>
    /// Saf kural kümesi — ortam bilgisi parametre olarak geliyor ki host
    /// ayağa kaldırmadan test edilebilsin.
    /// </summary>
    /// <param name="isProduction">
    /// Yalnız üretimde uygulanan kurallar var: geliştirici makinesinde ve
    /// testlerde placeholder anahtar meşru, orada üretilen token'ın kimseye
    /// zararı yok. Bu ayrımı kaldırmak yerel kurulumu bozar ve kural
    /// "geliştiricinin devre dışı bırakmak isteyeceği bir engel" hâline
    /// gelirdi.
    /// </param>
    public static ValidateOptionsResult Validate(JwtOptions options, bool isProduction)
    {
        var errors = new List<string>();

        // Uzunluk/boşluk kuralları her ortamda geçerli: bunlar yapılandırmanın
        // yanlış olduğunu değil, ÇALIŞMAYACAĞINI söyler.
        if (string.IsNullOrWhiteSpace(options.SecretKey))
        {
            errors.Add(
                "Jwt:SecretKey boş. Ortam değişkeni (Jwt__SecretKey / JWT_SECRET) " +
                "eksik ya da boş bir değere çözülmüş olabilir.");
        }
        else
        {
            var byteCount = Encoding.UTF8.GetByteCount(options.SecretKey);
            if (byteCount < MinSecretKeyBytes)
            {
                // Anahtarın kendisi ASLA mesaja girmiyor: doğrulama hatası
                // startup log'una düşer, log da sır saklamayan bir yer.
                errors.Add(
                    $"Jwt:SecretKey çok kısa ({byteCount} bayt); HS256 için en az " +
                    $"{MinSecretKeyBytes} bayt gerekli.");
            }

            if (isProduction)
            {
                if (string.Equals(options.SecretKey, Placeholder, StringComparison.Ordinal)
                    || options.SecretKey.Contains("REPLACE-WITH", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        "Jwt:SecretKey hâlâ appsettings.json'daki yer tutucu. Bu değer " +
                        "public depoda duruyor; onunla imzalanan customer/admin/shopper " +
                        "token'ları herkes tarafından üretilebilir.");
                }
                else if (options.SecretKey.Distinct().Count() < MinDistinctChars)
                {
                    errors.Add(
                        "Jwt:SecretKey uzunluk şartını dolduruyor ama içeriği tekrarlı; " +
                        "rastgele üretilmiş bir anahtar kullanın.");
                }
            }
        }

        // Issuer boşsa hiçbir token doğrulanamaz (ValidateIssuer açık ve
        // ValidIssuer bu değer) — yani sunucu ayakta ama kimse giriş yapamaz.
        if (string.IsNullOrWhiteSpace(options.Issuer))
            errors.Add("Jwt:Issuer boş; token doğrulaması ValidIssuer olarak bu değeri kullanıyor.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
