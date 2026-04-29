using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LiveDeck.LicenseServer.Pages.Admin.Licenses;

public class DetailModel : PageModel
{
    public string? Key { get; private set; }

    public IActionResult OnGet(string key)
    {
        Key = key;
        return Page();
    }
}
