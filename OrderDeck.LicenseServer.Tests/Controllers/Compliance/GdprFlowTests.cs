using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Compliance;

public class GdprFlowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public GdprFlowTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Export_returns_zip_with_expected_entries()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        var resp = await client.GetAsync("/api/v1/me/export");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/zip");

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entryNames = zip.Entries.Select(e => e.Name).ToList();

        entryNames.Should().Contain(new[]
        {
            "customer.json", "licenses.json", "activations.json",
            "email-logs.json", "backups.json", "audit-log.json", "_export-info.json"
        });

        // customer.json must contain the customer's email but NOT the password hash.
        var customerJson = await ReadEntryAsync(zip, "customer.json");
        customerJson.Should().Contain(customerId.ToString());
        customerJson.Should().NotContain("PasswordHash", because: "password hashes must never leave the server");
    }

    [Fact]
    public async Task Export_without_auth_returns_401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/me/export");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Purge_via_admin_anonymises_customer_and_deletes_data()
    {
        var (custClient, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        // Upload a backup so purge has something to clean up.
        var payload = new byte[1024];
        RandomNumberGenerator.Fill(payload);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        custClient.DefaultRequestHeaders.Add("X-Backup-Sha256", sha);
        var upload = await custClient.PostAsync("/api/v1/me/backups", content);
        upload.EnsureSuccessStatusCode();

        // Snapshot the original email — needed for the confirmation field.
        string originalEmail;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            originalEmail = (await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId)).Email;
            // Panel uygulamasının push kaydı (cihaz kimliği + FCM jetonu): müşteri satırı anonimleştirilip kaldığı için FK
            // cascade'i tetiklenmez, purge açıkça silmeli.
            db.PushDevices.Add(new OrderDeck.LicenseServer.Domain.PushDevice
            {
                Id = Guid.NewGuid(), CustomerId = customerId, DeviceId = $"dev-{Guid.NewGuid():N}", Platform = "android",
                PushToken = $"push-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Veri sorumlusu bilgileri de yayıncının verisi: purge siler (KVKK K-3).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.DataControllerProfiles.Add(new OrderDeck.LicenseServer.Domain.DataControllerProfile
            {
                CustomerId = customerId, LegalName = "Örnek Ticaret", PostalAddress = "Adres", KvkkEmail = "kvkk@example.test",
                Version = 1, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "panel",
            });
            db.DataControllerProfileVersions.Add(new OrderDeck.LicenseServer.Domain.DataControllerProfileVersion
            {
                Id = Guid.NewGuid(), CustomerId = customerId, Version = 1, LegalName = "Örnek Ticaret", PostalAddress = "Adres",
                KvkkEmail = "kvkk@example.test", CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "panel",
            });
            await db.SaveChangesAsync();
        }

        // Mint an admin token.
        string adminToken;
        OrderDeck.LicenseServer.Domain.AdminUser admin;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
            var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
            var existing = await db.AdminUsers.FirstOrDefaultAsync();
            if (existing is null)
            {
                existing = new OrderDeck.LicenseServer.Domain.AdminUser
                {
                    Id = Guid.NewGuid(),
                    Username = "purgetest-admin",
                    PasswordHash = hasher.Hash("x"),
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.AdminUsers.Add(existing);
                await db.SaveChangesAsync();
            }
            admin = existing;
            adminToken = jwt.IssueAdminToken(admin.Id, admin.Username).Token;
        }

        using var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var purge = await adminClient.PostAsJsonAsync(
            $"/api/v1/admin/customers/{customerId}/purge",
            new { confirmEmail = originalEmail });
        purge.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Customer row exists but anonymised; backups/licenses/activations gone.
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var anon = await verifyDb.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId);
        anon.Email.Should().StartWith("purged-");
        anon.Email.Should().EndWith("@deleted.invalid");
        anon.Name.Should().Be("[Deleted]");
        anon.PasswordHash.Should().Be("PURGED");

        var remainingBackups = await verifyDb.CustomerBackups.CountAsync(b => b.CustomerId == customerId);
        remainingBackups.Should().Be(0);
        (await verifyDb.DataControllerProfiles.CountAsync(p => p.CustomerId == customerId)).Should().Be(0);
        (await verifyDb.DataControllerProfileVersions.CountAsync(v => v.CustomerId == customerId)).Should().Be(0,
            "sürümler şahıs yayıncıda kişisel veri: profille birlikte silinir (karar 24)");

        // AuditLog must record the purge — the row stays even though customer data is gone.
        var purgeAudit = await verifyDb.AuditLogs.CountAsync(
            a => a.EventType == "customer.purged" && a.TargetId == customerId.ToString());
        purgeAudit.Should().BeGreaterThan(0);

        // KVKK Ö-13: kalıcı imha kaydı yayıncı kimliğiyle (anonimleştirilmiş satır kalır) ve kişisel veri İÇERMEZ — silinen
        // e-posta yalnız 24 ayda budanan customer.purged denetim satırında.
        var records = await verifyDb.DataDestructionRecords.AsNoTracking().Where(r => r.SubjectId == customerId).ToListAsync();
        records.Should().Contain(r => r.Target == "CustomerBackups" && r.RowCount == 1
            && r.Trigger == OrderDeck.LicenseServer.Services.Privacy.DataDestruction.Triggers.CustomerPurge);
        records.Should().Contain(r => r.Target == "Customers.Email+Name+PasswordHash+Notes" && r.RowCount == 1);
        // Girişin bıraktığı yenileme jetonu (oluşturan IP'yi taşır) da silinir ve kayda geçer.
        records.Should().Contain(r => r.Target == "RefreshTokens" && r.RowCount >= 1);
        System.Text.Json.JsonSerializer.Serialize(records).Should().NotContain(originalEmail);
        // Push cihazı (cihaz kimliği + jeton) silinir ve kayda geçer.
        (await verifyDb.PushDevices.CountAsync(d => d.CustomerId == customerId)).Should().Be(0);
        records.Should().Contain(r => r.Target == "PushDevices" && r.RowCount == 1);
        // Veri sorumlusu bilgisi ve sürümü silinir ve kayda geçer (KVKK K-3, karar 24; sayım izleyiciden).
        records.Should().Contain(r => r.Target == "DataControllerProfiles" && r.RowCount == 1
            && r.Action == OrderDeck.LicenseServer.Services.Privacy.DataDestruction.Actions.Deleted);
        records.Should().Contain(r => r.Target == "DataControllerProfileVersions" && r.RowCount == 1
            && r.Action == OrderDeck.LicenseServer.Services.Privacy.DataDestruction.Actions.Deleted);
        // Yapan: yöneticinin kimliği (sub), kullanıcı adı değil — tablo budanmaz, ad bir gerçek kişiyi gösterir (KVKK Ö-14).
        records.Should().OnlyContain(r => r.Actor == admin.Id.ToString());
    }

    [Fact]
    public async Task Purge_with_wrong_confirmation_email_returns_400()
    {
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        string adminToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
            var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
            var admin = new OrderDeck.LicenseServer.Domain.AdminUser
            {
                Id = Guid.NewGuid(),
                Username = $"a-{Guid.NewGuid():N}",
                PasswordHash = hasher.Hash("x"),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.AdminUsers.Add(admin);
            await db.SaveChangesAsync();
            adminToken = jwt.IssueAdminToken(admin.Id, admin.Username).Token;
        }

        using var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var purge = await adminClient.PostAsJsonAsync(
            $"/api/v1/admin/customers/{customerId}/purge",
            new { confirmEmail = "wrong@example.com" });
        purge.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Bearer-Admin istemcisi (üretilmiş yönetici; parola literal değil).</summary>
    private async Task<HttpClient> AdminApiClientAsync()
    {
        string adminToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
            var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
            var admin = new OrderDeck.LicenseServer.Domain.AdminUser
            {
                Id = Guid.NewGuid(), Username = $"a-{Guid.NewGuid():N}", PasswordHash = hasher.Hash($"p-{Guid.NewGuid():N}"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.AdminUsers.Add(admin);
            await db.SaveChangesAsync();
            adminToken = jwt.IssueAdminToken(admin.Id, admin.Username).Token;
        }
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        return client;
    }

    [Fact]
    public async Task Purge_Obifin_baglantisi_varken_reddedilir_once_Kaldir_ve_sil_istenir()
    {
        // KVKK K-6/Ö-9: yayıncı silmesi banka tablolarını kaskadla götürür ama Obifin'e bir şey söylemez; Obifin bankadan çekmeyi
        // ve saklamayı sürdürürdü. Bağlantı varken silme reddedilir: Obifin tarafı yalnız "Kaldır ve sil"de (lisansa özgü,
        // paylaşılan kullanıcı korumalı) kapanır.
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var licenseId = Guid.NewGuid();
        string email;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            email = (await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId)).Email;
            db.Licenses.Add(new OrderDeck.LicenseServer.Domain.License { Id = licenseId, LicenseKey = $"LDK-GD-{Guid.NewGuid():N}",
                CustomerId = customerId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
            // Korunan alanlar sahte: kimlik çözülemez → "Kaldır ve sil" Obifin'e gidemez (ulaşılamadı), yerel silme yine yapılır.
            db.ObifinConnections.Add(new OrderDeck.LicenseServer.Domain.Bank.ObifinConnection { Id = Guid.NewGuid(),
                LicenseId = licenseId, BaseUrl = "https://example.invalid", UserCode = $"u-{Guid.NewGuid():N}@x",
                PasswordProtected = $"p-{Guid.NewGuid():N}", ApiKeyProtected = $"a-{Guid.NewGuid():N}",
                Status = OrderDeck.LicenseServer.Domain.Bank.ObifinConnectionStatus.Verified,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var admin = await AdminApiClientAsync();

        var refused = await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("bank-connection-active");
        problem.GetProperty("detail").GetString().Should().Contain("Kaldır ve sil").And.Contain("yazılı");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId)).Email.Should().Be(email, "hiçbir şey silinmedi");
            (await db.Licenses.AnyAsync(l => l.Id == licenseId)).Should().BeTrue();
            var r = await scope.ServiceProvider.GetRequiredService<OrderDeck.LicenseServer.Services.Bank.BankConnectionTerminator>()
                .RemoveAndDeleteAsync(licenseId, OrderDeck.LicenseServer.Services.Privacy.DataDestruction.Triggers.AdminRemove,
                    "admin", CancellationToken.None);
            r.NeedsManualObifinAction.Should().BeTrue("kimlik çözülemedi: Obifin'de elle kaldırılacak");
        }

        (await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent, "Obifin tarafı kapandıktan sonra silme yapılır");
    }

    [Fact]
    public async Task Purge_yayincinin_acik_banka_veri_talebini_kapatir()
    {
        // KVKK K-7: talebin lisansa FK'sı yok (belge lisans silinse de yaşar); açık kalırsa IBAN özeti süresiz durur. Yönetici
        // başvurana yine yanıt verir.
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var licenseId = Guid.NewGuid(); var requestId = Guid.NewGuid();
        string email;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            email = (await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId)).Email;
            db.Licenses.Add(new OrderDeck.LicenseServer.Domain.License { Id = licenseId, LicenseKey = $"LDK-GD-{Guid.NewGuid():N}",
                CustomerId = customerId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
            db.BankSubjectRequests.Add(new OrderDeck.LicenseServer.Domain.Bank.BankSubjectRequest { Id = requestId,
                LicenseId = licenseId, Kind = OrderDeck.LicenseServer.Domain.Bank.BankSubjectRequestKind.Access,
                IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", IbanMasked = "TR12…345", ReceivedAt = DateTimeOffset.UtcNow,
                DueAt = DateTimeOffset.UtcNow.AddDays(30), CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "admin" });
            await db.SaveChangesAsync();
        }
        using var admin = await AdminApiClientAsync();

        (await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var verify = _factory.Services.CreateScope();
        var request = await verify.ServiceProvider.GetRequiredService<LicenseDbContext>().BankSubjectRequests.AsNoTracking()
            .SingleAsync(r => r.Id == requestId);
        request.ClosedAt.Should().NotBeNull();
        request.IbanHash.Should().BeNull();
        request.Resolution.Should().Be(OrderDeck.LicenseServer.Domain.Bank.BankSubjectResolution.DataDeleted);
        request.Outcome.Should().Contain("Yayıncı hesabı silindi").And.Contain("başvurana yanıt verin");
        Guid.TryParse(request.ClosedBy, out _).Should().BeTrue("kapatan yöneticinin kimliği (sub), kullanıcı adı değil");
        // m. 13/2: yanıt yükümlülüğü sürer — kapanan talep sayısı (kişisel veri değil) purge'ün denetim satırında.
        (await verify.ServiceProvider.GetRequiredService<LicenseDbContext>().AuditLogs.AsNoTracking()
                .SingleAsync(a => a.EventType == OrderDeck.LicenseServer.Services.Audit.AuditEvents.CustomerPurged
                    && a.TargetId == customerId.ToString()))
            .Details.Should().Contain("\"closedBankSubjectRequests\":1");
        // Silme Yönetmeliği md. 7/1: boşalan özet imha kaydına girer, purge'ün kayıtlarıyla aynı işlemde.
        (await verify.ServiceProvider.GetRequiredService<LicenseDbContext>().DataDestructionRecords.AsNoTracking()
                .Where(x => x.RequestId == requestId).ToListAsync())
            .Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                Target = OrderDeck.LicenseServer.Services.Bank.BankSubjectRequestService.IbanHashTarget,
                Action = OrderDeck.LicenseServer.Services.Privacy.DataDestruction.Actions.Nulled, RowCount = 1,
                Trigger = OrderDeck.LicenseServer.Services.Privacy.DataDestruction.Triggers.CustomerPurge,
                Actor = request.ClosedBy, LicenseId = (Guid?)licenseId,
            });
    }

    private static async Task<string> ReadEntryAsync(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"entry {name} missing");
        await using var s = entry.Open();
        using var sr = new StreamReader(s);
        return await sr.ReadToEndAsync();
    }
}
