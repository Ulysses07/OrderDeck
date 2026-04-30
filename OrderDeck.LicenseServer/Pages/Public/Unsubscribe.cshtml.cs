using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Pages.Public;

public class UnsubscribeModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly UnsubscribeTokenSigner _signer;

    public UnsubscribeModel(LicenseDbContext db, UnsubscribeTokenSigner signer)
    {
        _db = db;
        _signer = signer;
    }

    public string? CustomerEmail { get; private set; }
    public bool AlreadyUnsubscribed { get; private set; }
    public bool JustUnsubscribed { get; private set; }
    public bool TokenInvalid { get; private set; }

    [BindProperty]
    public string Token { get; set; } = "";

    public async Task OnGetAsync(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_signer.TryVerify(token, out var customerId, out _))
        {
            TokenInvalid = true;
            return;
        }

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer is null)
        {
            TokenInvalid = true;
            return;
        }

        Token = token;
        CustomerEmail = customer.Email;
        AlreadyUnsubscribed = customer.Unsubscribed;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Token) || !_signer.TryVerify(Token, out var customerId, out _))
        {
            TokenInvalid = true;
            return Page();
        }

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null)
        {
            TokenInvalid = true;
            return Page();
        }

        if (!customer.Unsubscribed)
        {
            customer.Unsubscribed = true;
            await _db.SaveChangesAsync(ct);
        }

        CustomerEmail = customer.Email;
        JustUnsubscribed = true;
        return Page();
    }
}
