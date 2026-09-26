using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Çekim işi her yeni GELEN hareketi (kaydedildikten sonra) buraya verir. PR-2 eşleştiriciyi takar.
/// <para>Verilen varlık çağrı sırasında izlenir (kayıtlı, Unchanged); sink döndükten sonra çekim işi onu
/// izleyiciden ayırır (Detached). Sink varlığı sonradan değiştirmek isterse kendi sorgusuyla yeniden okumalı.</para></summary>
public interface IBankTransactionSink
{
    Task OnNewIncomingAsync(BankTransaction tx, CancellationToken ct);
}

public sealed class NoopBankTransactionSink : IBankTransactionSink
{
    public Task OnNewIncomingAsync(BankTransaction tx, CancellationToken ct) => Task.CompletedTask;
}
