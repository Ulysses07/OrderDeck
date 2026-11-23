namespace OrderDeck.Core.Customers;

/// <summary>
/// Canlı sohbet satırının, gönderene bakarak taşıdığı iki işaret
/// (<c>CustomerRepository.GetChatSenderMarks</c>).
/// </summary>
/// <param name="IsBlacklisted">Kişi, tanıtıcısının herhangi bir yazımında kara listede.</param>
/// <param name="IsRegistered">Kişinin telefonu kayıtlı: yazan hesabın satırında, öbür "@" yazımındaki
/// ikizinde ya da aynı kişinin bağlı (aynı <c>GroupId</c>) bir hesabında.</param>
public readonly record struct ChatSenderMarks(bool IsBlacklisted, bool IsRegistered);
