namespace OrderDeck.App.Startup;

/// <summary>Geri yükleme gate'inin sonucu.</summary>
public enum RestoreOutcome
{
    /// <summary>Operatör atladı; boş veritabanıyla devam.</summary>
    Skipped = 0,
    /// <summary>Yedek indirildi ve yazıldı; uygulama yeniden başlamalı.</summary>
    Restored
}
