using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Çekim işinden gelen her yeni GELEN hareketi eşleştiriciye verir. Eşleştirme hatası çekimi
/// düşürmez (hareket zaten kaydedildi; sink hareketi bir daha görmez, öneriyi saatlik telafi taraması
/// <see cref="BankMatchSweepJob"/> üretir).
/// <para><b>Kendi kapsamı.</b> Eşleştirici kaydederken bağlamının bekleyen TÜM değişikliklerini yazar. Çekim işinin bağlamında
/// koşsaydı işin izlediği bağlantının kaydedilmemiş alanlarını (imleç, hata) işin kimlik denetimini atlayarak yazar, öneri
/// satırları da işin izleyicisinde birikirdi (90 günlük ilk çekimde her DetectChanges büyürdü). Bu yüzden sink örneği —
/// çekim işinin kapsamında çözülür, yani bir çekim koşusu — ilk çağrıda TEK alt kapsam açar, eşleştiriciyi ve onun
/// bağlamını oradan çözer ve koşu boyunca yeniden kullanır (eşleştiricinin lisans başına aday önbelleği sıcak kalır). Her
/// çağrıdan sonra alt bağlamın izleyicisi boşaltılır; alt kapsam sink ile birlikte, DI kapsamı kapanınca kapanır. Hareket alt
/// bağlama eklenmez: eşleştirici yalnız Id'sini ve skaler alanlarını okur.</para>
/// <para>Çekim işi sırayla, tek akıştan çağırır; eşzamanlı çağrı için tasarlanmadı.</para>
/// <para>Yalnız işin kendi iptali yukarı çıkar. Başka her hata (başka bir iptal, ör. komut zaman aşımı, dahil) hareket Id'si
/// ve istisnayla loglanıp yutulur; açıklama, ad, IBAN ya da tutar loglanmaz.</para></summary>
public sealed class MatchingBankTransactionSink : IBankTransactionSink, IAsyncDisposable, IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MatchingBankTransactionSink> _log;
    private AsyncServiceScope? _scope;
    private LicenseDbContext? _db;
    private PaymentMatcher? _matcher;
    private bool _disposed;

    public MatchingBankTransactionSink(IServiceScopeFactory scopes, ILogger<MatchingBankTransactionSink> log)
    {
        _scopes = scopes; _log = log;
    }

    public async Task OnNewIncomingAsync(BankTransaction tx, CancellationToken ct)
    {
        try
        {
            try
            {
                await Matcher().MatchAsync(tx, ct);
            }
            finally
            {
                // Öneri satırları koşu boyunca birikmesin; düşen bir çağrının izi de sıradaki harekete taşınmasın.
                _db?.ChangeTracker.Clear();
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.LogError(ex, "Gölge eşleştirme düştü (hareket {TransactionId}); telafi taraması (bank-match-sweep) yeniden dener",
                tx.Id);
        }
    }

    /// <summary>Alt kapsamı ilk çağrıda açar. Çözümleme düşerse kapsam açık kalır ve sonraki çağrı AYNI kapsamda yeniden
    /// dener: kapsam başına tek açılış, sızıntı yok.</summary>
    private PaymentMatcher Matcher()
    {
        if (_matcher is not null) return _matcher;
        ObjectDisposedException.ThrowIf(_disposed, this);
        _scope ??= _scopes.CreateAsyncScope();
        var sp = _scope.Value.ServiceProvider;
        _db = sp.GetRequiredService<LicenseDbContext>();
        _matcher = sp.GetRequiredService<PaymentMatcher>();
        return _matcher;
    }

    public ValueTask DisposeAsync() => Release() is { } scope ? scope.DisposeAsync() : ValueTask.CompletedTask;

    public void Dispose() => Release()?.Dispose();

    private AsyncServiceScope? Release()
    {
        _disposed = true;
        var scope = _scope;
        _scope = null; _db = null; _matcher = null;
        return scope;
    }
}
