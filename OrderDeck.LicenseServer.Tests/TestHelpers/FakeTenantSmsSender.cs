using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Kampanya job testleri için programlanabilir kiracı göndericisi — gerçek
/// Netgsm çağrısı yapmaz. Telefon başına istisna kuyruklanabilir
/// (<see cref="FailWith"/>), gönderilenler kimlikleriyle birlikte kaydedilir.
/// Kilit + kanca kalıbı <see cref="RecordingSmsSender"/>'ın birebir kardeşi.
/// </summary>
public sealed class FakeTenantSmsSender : ITenantSmsSender
{
    public sealed record Message(TenantSmsCredentials Creds, string Phone, string Text);

    private readonly List<Message> _sent = new();
    private readonly object _lock = new();

    /// <summary>Telefon → fırlatılacak istisna. O telefona gönderim KAYDEDİLMEZ
    /// (temiz ret / ağ hatası simülasyonu — hangisi olduğuna istisna tipi karar verir).</summary>
    public Dictionary<string, Exception> FailWith { get; } = new();

    /// <summary>Dolu ise HER gönderim bu istisnayla düşer
    /// (eski <c>RecordingSmsSender.ThrowOnSend</c>'in karşılığı).</summary>
    public Exception? FailAllWith { get; set; }

    /// <summary>Başarılı gönderimlerin döneceği Netgsm <c>jobid</c>'i (null = yanıtta yok).</summary>
    public string? NextJobId { get; set; }

    /// <summary>Kayıt yapıldıktan sonra çağrılır — kilidin DIŞINDA, gerekçe
    /// <see cref="RecordingSmsSender.OnSent"/>'te.</summary>
    public Action<Message>? OnSent { get; set; }

    /// <summary>Programlanmış istisna fırlatılmadan HEMEN önce çağrılır —
    /// gönderim ile sonuç yazımı arasına rakip bir yazım sıkıştırmak için
    /// (yarış testleri).</summary>
    public Action<string>? OnFailing { get; set; }

    public IReadOnlyList<Message> Sent
    {
        get { lock (_lock) return _sent.ToList(); }
    }

    /// <summary>Paylaşılan fixture'da test başına sıfırlama: kayıtlar,
    /// programlanmış hatalar, jobid ve kanca birlikte temizlenir.</summary>
    public void Clear()
    {
        lock (_lock) _sent.Clear();
        FailWith.Clear();
        FailAllWith = null;
        NextJobId = null;
        OnSent = null;
        OnFailing = null;
    }

    public Task<string?> SendAsync(
        TenantSmsCredentials credentials, string toPhone, string message,
        CancellationToken ct = default)
    {
        if (FailAllWith is not null) throw FailAllWith;
        if (FailWith.TryGetValue(toPhone, out var ex))
        {
            OnFailing?.Invoke(toPhone);
            throw ex;
        }

        var msg = new Message(credentials, toPhone, message);
        lock (_lock) _sent.Add(msg);
        OnSent?.Invoke(msg);
        return Task.FromResult(NextJobId);
    }
}
