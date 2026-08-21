using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OrderDeck.Overlay;

/// <summary>
/// Bağlı tek bir overlay soketine yazan sınırlı kuyruk + tek yazıcı pompa.
///
/// <para><b>Asıl kusur: sınırsız birikme.</b> Eski yayın döngüsü her istemci
/// için <c>_ = SendBytes(bytes)</c> ile ateşle-unut çalışıyordu. .NET 10'da
/// eşzamanlı <c>SendAsync</c> çağrıları soketin iç kilidinde sıraya giriyor
/// (ölçüldü: soket ne <c>InvalidOperationException</c> fırlatıyor ne de
/// <c>Abort()</c> ediliyor) — yani veri bozulmuyor, ama <i>bekleyen</i> her
/// çağrı bir <see cref="Task"/> + bir <c>byte[]</c>'i canlı tutuyor. OBS
/// penceresi küçültülmüş ya da makine takılmışsa TCP penceresi dolar,
/// gönderim askıda kalır ve sohbet hızlandıkça bu yığın <b>tavansız</b>
/// büyür. Üst sınır da yok, düşürme politikası da: sadece bellek.</para>
///
/// <para><b>İkinci kusur: görünmezlik.</b> <c>SendBytes</c>'ın gövdesi boş bir
/// <c>catch</c> ile bitiyordu. Bir istemci koptuğunda ya da gönderim
/// başarısız olduğunda hiçbir yere iz kalmıyordu; overlay'in neden karardığı
/// logdan okunamıyordu.</para>
///
/// <para><b>Neden kuyruk, yalnız kilit değil.</b> Soket başına bir
/// <c>SemaphoreSlim</c> zaten var olan iç sıralamayı tekrar ederdi ama
/// birikmeyi durdurmazdı — sorun eşzamanlılık değil, tavansızlık. Sınırlı
/// kanal hem üst sınırı hem de tek yazıcı sıralamasını açıkça garanti
/// ediyor.</para>
///
/// <para><b>Dolduğunda en eskisi atılır.</b> Kapasite kadar geride kalmış bir
/// overlay'in eski mesajları zaten ekranda görünmeyecek (chat.js son 15
/// satırı gösteriyor); canlı yayında değerli olan en yenisi. Atılan mesaj
/// sayılıyor ve kapanışta loglanıyor — sessiz kayıp yok.</para>
/// </summary>
internal sealed class OverlayClient : IAsyncDisposable
{
    /// <summary>Soket başına tampon. ~10 msg/sn'lik yoğun bir sohbette
    /// istemciye yarım dakikadan fazla toparlanma payı bırakır.</summary>
    internal const int Capacity = 256;

    private readonly WebSocket _ws;
    private readonly string _channelName;
    private readonly ILogger _log;
    private readonly Channel<byte[]> _queue;
    private readonly Task _pump;
    private int _dropped;

    public OverlayClient(WebSocket ws, string channelName, ILogger log, CancellationToken ct)
    {
        _ws = ws;
        _channelName = channelName;
        _log = log;
        _queue = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            itemDropped: _ => Interlocked.Increment(ref _dropped));
        _pump = PumpAsync(ct);
    }

    /// <summary>Yayın ipliğinden çağrılır; asla bloklamaz ve asla fırlatmaz.</summary>
    public void Enqueue(byte[] bytes)
    {
        // DropOldest kipinde TryWrite yalnız kanal kapandığında false döner.
        if (_queue.Writer.TryWrite(bytes)) return;
        _log.LogDebug("Overlay {Channel} client already closed; frame discarded", _channelName);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var bytes in _queue.Reader.ReadAllAsync(ct))
            {
                if (_ws.State != WebSocketState.Open) break;
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            // Tek bir istemcinin kopması yayını durdurmamalı — ama sessizce de
            // geçilmemeli; overlay'in neden karardığı loga düşsün.
            _log.LogDebug(ex, "Overlay {Channel} client send failed; dropping client", _channelName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Overlay {Channel} client pump failed", _channelName);
        }
    }

    /// <summary>Kuyruğu kapatır ve pompanın bitmesini bekler.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _pump; } catch { /* PumpAsync zaten yutuyor */ }

        var dropped = Volatile.Read(ref _dropped);
        if (dropped > 0)
        {
            _log.LogWarning(
                "Overlay {Channel} client dropped {Count} frame(s) — istemci yayına yetişemedi",
                _channelName, dropped);
        }
    }
}
