using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

/// <summary>Banka Faz 2 kod kümeleri (spec 2026-10-10 §2, §5.2, §6). Enum'lar string saklanır: ad veritabanının
/// sözleşmesidir.</summary>
public sealed class BankDecisionCodesTests
{
    private static void Names<T>(params string[] expected) where T : struct, Enum => Enum.GetNames<T>().Should().Equal(expected);

    [Fact] // adlar veritabanında durur: değişirse eski satır okunamaz
    public void Enum_adlari_sabit()
    {
        Names<PaymentSource>("Receipt", "Bank");
        Names<PaymentApprovalSource>("ReceiptHuman", "BankHuman", "BankAuto");
        Names<PaymentMatchDecision>("None", "Excluded", "Awaiting", "Unmatched", "Approved", "AutoApproved", "ClosedByReceipt", "Dismissed");
        Names<IbanMemorySource>("ManualMatch", "HumanApproval", "BankConfirm");
        Names<BankDismissReason>("OwnTransfer", "MarketplacePayout", "Refund", "Other");
        Names<BankRevertReason>("WrongCustomer", "NotCustomerPayment", "BankReturned", "DataSubjectObjection", "Other");
        Names<BankDecisionActorKind>("Owner", "Staff", "System");
        Names<BankDecisionSurface>("Panel", "Wpf", "Sink", "Sweep", "Runner", "ReceiptApproval", "Admin");
        Names<BankAskReason>("UsernameOnly", "NameOnly", "IbanNotConfirmed", "IbanShared", "IbanOtherCustomer", "IbanDisputed", "IbanHashUnverified",
            "CustomerNotCanonical", "NoExpectation", "AmountMismatch", "AmbiguousExpectations", "ExpectationUnreliable", "RequestInProgress",
            "SalesSyncNotReady", "PendingReceipt", "UnlinkedApprovedPayment", "ReceiptOtherCustomer", "TooOld"); // Parse bilinmeyeni atlar: adı değişen neden sessizce kaybolur
        Names<PaymentAllocationKind>("Payment", "Waived", "Cancelled", "OffBank");
        Names<PaymentShipmentStepState>("Waiting", "NeedsDecision", "Bound", "BoundNeedsDecision", "Reverting", "Closed");
        Names<PaymentShipmentStepReason>("SessionLive", "ModeOff", "Shortage", "RecipientPays", "OrderSetUnknown", "OrdersChanged", "NothingToBind", "Threshold", "RetryExhausted");
        Names<PaymentShipmentStepDecision>("Normal", "Hold", "ShipNow", "RecipientPays");
        ((int)PaymentSource.Receipt, (int)PaymentMatchDecision.None).Should().Be((0, 0), "DB varsayılanı CLR varsayılanıyla aynı");
    }

    [Fact]
    public void Sorma_nedenleri_enum_sirasiyla_yazilir_bilinmeyen_kod_atlanir()
    {
        BankAskReasons.Format([BankAskReason.TooOld, BankAskReason.UsernameOnly, BankAskReason.TooOld]).Should().Be("UsernameOnly,TooOld");
        BankAskReasons.Format([]).Should().BeNull();
        BankAskReasons.Parse(null).Should().BeEmpty();
        BankAskReasons.Parse("UsernameOnly,IleridekiKod,3,tooold,TooOld").Should().Equal(BankAskReason.UsernameOnly, BankAskReason.TooOld);
        BankAskReasons.Format(Enum.GetValues<BankAskReason>())!.Length.Should().BeLessThanOrEqualTo(BankAskReasons.MaxLength);
    }
}
