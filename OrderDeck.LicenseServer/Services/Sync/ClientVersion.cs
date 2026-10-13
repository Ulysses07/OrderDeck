using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Masaüstü istemcinin (WPF) sürümü — <c>User-Agent</c>'tan okunur ("OrderDeck-WPF/0.9.10"). Tek ayrıştırıcı:
/// etkinleştirmenin <c>AppVersion</c>'ı (saha sürüm dağılımı, R8 §23-8), satış senkronunun
/// <c>SalesSync:MinClientVersion</c> kapısı ve ortak bakiyenin <c>PersonBalance:MinClientVersion</c> kapısı aynı
/// kodu kullanır (spec 2026-10-13 §4.0, ortak bakiye §7.2).
///
/// <para><b>Karşılaştırma <see cref="System.Version"/> ile.</b> Metin karşılaştırması "0.9.10"u "0.9.9"dan
/// küçük bulur; sürüm kapısı yanlış istemciyi geçirirdi. Eksik bileşen 0 sayılır ("0.9" = "0.9.0" =
/// "0.9.0.0").</para>
///
/// <para><b>Ekler.</b> "+" sonrası derleme bilgisi sıralamaya girmez (SemVer). "-" sonrası ön sürüm eki
/// (<see cref="PreRelease"/>) aynı numaranın yayımlanmış sürümünden ÖNCE gelir: "0.9.11-rc1", "0.9.11"
/// alt sınırını geçmez ama "0.9.10"u geçer. Ön sürüm ekleri kendi aralarında sıralanmaz: aynı numarada alt
/// sınırı ancak aynı ek (ya da eksiz sürüm) geçer. Bu kasıtlı olarak kapalı-hata yönündedir: belirsiz durumda
/// kapı kapanır.</para>
/// </summary>
/// <param name="Number">Sürüm numarası, dört bileşene tamamlanmış.</param>
/// <param name="PreRelease">Ön sürüm eki ("rc1"); yoksa null.</param>
public sealed record ClientVersion(Version Number, string? PreRelease)
{
    // Desen LicensesController'dan taşındı (R8 §23-8); değiştirmek Activations.AppVersion'ın sahadaki
    // değerlerini değiştirir. Uzunluk sınırı WpfReplicas.ClientVersion (nvarchar(32)) ile aynı.
    private static readonly Regex UserAgentPattern =
        new(@"\bOrderDeck-WPF/([0-9A-Za-z.\-]{1,32})", RegexOptions.Compiled);

    /// <summary>User-Agent'taki ham sürüm metni ("OrderDeck-WPF/0.9.5" → "0.9.5"). Desen tutmazsa null: eski
    /// istemciler UA göndermiyor ve alan null kalmalı, tarayıcı UA'sı gibi başka bir şey yazılmamalı.</summary>
    public static string? RawFromUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;
        var m = UserAgentPattern.Match(userAgent);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Ham sürüm metnini ayrıştırır; ayrıştırılamıyorsa null (kapı için "eski" demektir).</summary>
    public static ClientVersion? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();

        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        string? preRelease = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = text[(dash + 1)..];
            text = text[..dash];
            if (preRelease.Length == 0) return null;
        }

        if (!Version.TryParse(text, out var v)) return null;
        return new ClientVersion(
            new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)), preRelease);
    }

    /// <summary><see cref="RawFromUserAgent"/> + <see cref="Parse"/>.</summary>
    public static ClientVersion? FromUserAgent(string? userAgent) => Parse(RawFromUserAgent(userAgent));

    /// <summary>
    /// İstemci alt sınırı karşılıyor mu. Kapalı-hata: istemci sürümü bilinmiyor ya da ayrıştırılamıyorsa
    /// (<paramref name="client"/> null) ya da alt sınır tanımsızsa (<paramref name="min"/> null) false.
    /// </summary>
    public static bool IsAtLeast(ClientVersion? client, ClientVersion? min)
    {
        if (client is null || min is null) return false;
        var byNumber = client.Number.CompareTo(min.Number);
        if (byNumber != 0) return byNumber > 0;
        // Aynı numara: eksiz sürüm her ekli hâlinden sonra gelir; iki farklı ek sıralanmaz (kapalı-hata).
        return client.PreRelease is null
            || string.Equals(client.PreRelease, min.PreRelease, StringComparison.OrdinalIgnoreCase);
    }
}
