namespace OrderDeck.LicenseServer.Domain.Bank;

/// <summary>"Müşteri ödemesi değil" gerekçesi (banka havalesi spec'i 2026-10-10 §6). String saklanır, ad değişmez.</summary>
public enum BankDismissReason
{
    /// <summary>Yayıncının kendi havalesi.</summary>
    OwnTransfer = 0,

    /// <summary>Pazaryeri ödemesi.</summary>
    MarketplacePayout = 1,

    /// <summary>İade.</summary>
    Refund = 2,

    /// <summary>Başka; not yalnız bunda alınır.</summary>
    Other = 3,
}

/// <summary>Onayı geri alma gerekçesi (banka havalesi spec'i 2026-10-10 §6, §11.2). String saklanır, ad değişmez.</summary>
public enum BankRevertReason
{
    /// <summary>Yanlış müşteriye onaylandı.</summary>
    WrongCustomer = 0,

    /// <summary>Müşteri ödemesi değil.</summary>
    NotCustomerPayment = 1,

    /// <summary>Banka havaleyi geri aldı.</summary>
    BankReturned = 2,

    /// <summary>İlgili kişi itirazı (KVKK).</summary>
    DataSubjectObjection = 3,

    /// <summary>Başka.</summary>
    Other = 4,
}

/// <summary>Kararı kimin verdiği (banka havalesi spec'i 2026-10-10 §2.6). String saklanır, ad değişmez.</summary>
public enum BankDecisionActorKind
{
    /// <summary>Yayıncı; WPF kararı da budur (tenant jetonuyla çağırır).</summary>
    Owner = 0,

    /// <summary>Panel personeli.</summary>
    Staff = 1,

    /// <summary>Sunucunun kendisi (otomatik onay, arka plan işi).</summary>
    System = 2,
}

/// <summary>Kararın verildiği yer (banka havalesi spec'i 2026-10-10 §2.6). String saklanır, ad değişmez.</summary>
public enum BankDecisionSurface
{
    /// <summary>Web paneli.</summary>
    Panel = 0,

    /// <summary>Masaüstü.</summary>
    Wpf = 1,

    /// <summary>Hareketi ilk gören çekim (sink).</summary>
    Sink = 2,

    /// <summary>Telafi taraması.</summary>
    Sweep = 3,

    /// <summary>Kargo adımı koşucusu.</summary>
    Runner = 4,

    /// <summary>Dekont onayı.</summary>
    ReceiptApproval = 5,

    /// <summary>Yönetim sayfası.</summary>
    Admin = 6,
}
