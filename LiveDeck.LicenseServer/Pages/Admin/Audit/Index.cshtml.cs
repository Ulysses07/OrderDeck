using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Audit;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin.Audit;

public class IndexModel : PageModel
{
    private const int PageSize = 50;
    private readonly LicenseDbContext _db;

    public IndexModel(LicenseDbContext db) => _db = db;

    public List<AuditLogEntry> Entries { get; private set; } = new();
    public string? EventType { get; private set; }
    public string? AdminUsername { get; private set; }
    public DateTimeOffset From { get; private set; }
    public DateTimeOffset To { get; private set; }
    public int CurrentPage { get; private set; }
    public int TotalPages { get; private set; }

    public List<string> AvailableEventTypes { get; } = new()
    {
        AuditEvents.AdminLogin,
        AuditEvents.AdminLogout,
        AuditEvents.CustomerConfirmEmail,
        AuditEvents.LicenseIssue,
        AuditEvents.LicenseRevoke,
        AuditEvents.LicenseExtend,
        AuditEvents.ActivationForceDeactivate
    };

    public async Task OnGetAsync(string? eventType, string? adminUsername, DateTimeOffset? from, DateTimeOffset? to, int page, CancellationToken ct)
    {
        EventType = eventType;
        AdminUsername = adminUsername;
        From = from ?? DateTimeOffset.UtcNow.AddDays(-7);
        To = to ?? DateTimeOffset.UtcNow;
        CurrentPage = page < 1 ? 1 : page;

        var query = _db.AuditLogs
            .Where(a => a.OccurredAt >= From && a.OccurredAt <= To);
        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(a => a.EventType == eventType);
        if (!string.IsNullOrWhiteSpace(adminUsername))
            query = query.Where(a => a.AdminUsername.Contains(adminUsername));

        var total = await query.CountAsync(ct);
        TotalPages = (int)Math.Ceiling(total / (double)PageSize);

        Entries = await query
            .OrderByDescending(a => a.OccurredAt)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);
    }
}
