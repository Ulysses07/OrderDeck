using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Auth;

/// <summary>
/// R12-S01: <b>her başarılı kimlik geçersizleştirmesi kendi neslini üretir.</b>
///
/// <c>Customer.AuthVersion</c> refresh token'ların damgası; yenilemede güncel
/// nesille karşılaştırılıyor (R11-S01). Ama nesil düz bir okuma-artırma-yazma
/// ile ilerliyordu ve satırda bunu koşullu yazıya bağlayan bir eşzamanlılık
/// jetonu yoktu: iki parola değişimi aynı nesli okuyup ikisi de aynı sonraki
/// değeri yazabiliyordu. Sonuç iki "başarılı" değişim ama TEK nesil artışı —
/// yani ikinci değişim, birinci parolayla açılmış oturumu iptal etmiş
/// sayılıyor, gerçekte iptal etmiyor.
///
/// <para>Neden gerçek SQL Server: InMemory sağlayıcısı eşzamanlılık jetonunu
/// hiç uygulamaz. Bu kusur orada ne görünür ne de düzeltmesi kanıtlanabilir —
/// test bozuk kodda da yeşil yanar.</para>
///
/// <para>Neden refresh satırı yok: <c>RefreshToken.RevokedAt</c> jetonu bazı
/// sıralamaları dolaylı olarak zaten durduruyor. Kusur tam olarak o dolaylı
/// korumanın devrede OLMADIĞI durumda açığa çıkıyor (ör. operatör önce çıkış
/// yapmış, elindeki access hâlâ ömrü içinde). Erişim jetonu bu yüzden
/// doğrudan üretiliyor: aranan yarışın kendisini kurar.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CustomerAuthVersionConcurrencyTests : IAsyncLifetime
{
    private const int ParallelAttempts = 8;

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public CustomerAuthVersionConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static string NewPassword() => $"pw-{Guid.NewGuid():N}";

    [Fact]
    public async Task Esazamanli_parola_degisimleri_ayni_nesli_paylasmaz()
    {
        var current = NewPassword();
        var (customerId, email) = await SeedCustomerAsync(current);
        var access = await IssueAccessTokenAsync(customerId, email);

        var candidates = Enumerable.Range(0, ParallelAttempts).Select(_ => NewPassword()).ToArray();
        var responses = await ChangePasswordInParallelAsync(access, current, candidates);

        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        var authVersion = await ReadAuthVersionAsync(customerId);

        authVersion.Should().Be(accepted,
            "kabul edilen her kimlik geçersizleştirmesi kendi neslini üretmeli; " +
            "nesil sayısı kabul sayısından azsa bir değişim, önceki parolayla " +
            "açılmış oturumu iptal ettiğini sanarken iptal etmemiş olur");
    }

    [Fact]
    public async Task Bayat_parola_yazisi_sessizce_kabul_edilmez()
    {
        var current = NewPassword();
        var (customerId, email) = await SeedCustomerAsync(current);
        var access = await IssueAccessTokenAsync(customerId, email);

        var candidates = Enumerable.Range(0, ParallelAttempts).Select(_ => NewPassword()).ToArray();
        var responses = await ChangePasswordInParallelAsync(access, current, candidates);

        responses.Count(r => r.StatusCode == HttpStatusCode.NoContent).Should().Be(1,
            "aynı başlangıç durumunu gören yazılardan yalnız biri geçerli olabilir");
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict)
            .Should().Be(ParallelAttempts - 1,
                "kaybeden istek anlaşılır bir çatışma cevabı almalı — 500 ya da " +
                "sessiz 204 istemciye 'parolan değişti' der ve değişmemiştir");

        // Kabul edilen aday gerçekten yürürlükte, reddedilenler değil.
        var winners = new List<string>();
        foreach (var (resp, candidate) in responses.Zip(candidates))
        {
            var ok = await LoginSucceedsAsync(email, candidate);
            if (resp.StatusCode == HttpStatusCode.NoContent)
            {
                ok.Should().BeTrue("kabul edilen parola giriş yapabilmeli");
                winners.Add(candidate);
            }
            else
            {
                ok.Should().BeFalse("reddedilen isteğin parolası yazılmış olamaz");
            }
        }
        winners.Should().HaveCount(1);
    }

    [Fact]
    public async Task Seri_iki_degisim_iki_nesil_uretir()
    {
        // Zorunlu karşıt kontrol: jeton, ÇAKIŞMAYAN meşru yolu bozmamalı.
        var first = NewPassword();
        var (customerId, email) = await SeedCustomerAsync(first);
        var access = await IssueAccessTokenAsync(customerId, email);

        var second = NewPassword();
        (await ChangePasswordAsync(access, first, second)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        var third = NewPassword();
        (await ChangePasswordAsync(access, second, third)).StatusCode
            .Should().Be(HttpStatusCode.NoContent, "seri değişim çatışma değildir");

        (await ReadAuthVersionAsync(customerId)).Should().Be(2,
            "iki ayrı geçersizleştirme iki nesil demektir");
        (await LoginSucceedsAsync(email, second)).Should().BeFalse("ara parola kapanmalı");
        (await LoginSucceedsAsync(email, third)).Should().BeTrue("son parola geçerli olmalı");
    }

    [Fact]
    public async Task Yanlis_mevcut_parola_nesli_ilerletmez()
    {
        // Kapı yalnız BAŞARILI geçersizleştirmeyi sayar: reddedilen deneme
        // nesli ilerletirse meşru oturumlar bedava düşerdi.
        var current = NewPassword();
        var (customerId, email) = await SeedCustomerAsync(current);
        var access = await IssueAccessTokenAsync(customerId, email);

        var resp = await ChangePasswordAsync(access, NewPassword(), NewPassword());
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ReadAuthVersionAsync(customerId)).Should().Be(0);
        (await LoginSucceedsAsync(email, current)).Should().BeTrue("mevcut parola ayakta kalmalı");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary>
    /// N isteği aynı anda yollar. İstemciler ve ısınma turu bilerek döngüden
    /// önce: <c>CreateClient</c> ilk çağrıda sunucuyu ayağa kaldırıyor, JIT
    /// bedeli istekleri birbirinden ayırıp yarış penceresini kapatıyor
    /// (RefreshTokenConcurrencyTests'te ölçüldü).
    /// </summary>
    private async Task<HttpResponseMessage[]> ChangePasswordInParallelAsync(
        string accessToken, string currentPassword, string[] candidates)
    {
        var clients = candidates.Select(_ => CreateAuthedClient(accessToken)).ToArray();

        await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            "/api/v1/me/password", new { currentPassword = "x", newPassword = "x" })));

        return await Task.WhenAll(clients.Zip(candidates).Select(pair =>
            pair.First.PostAsJsonAsync("/api/v1/me/password", new
            {
                currentPassword,
                newPassword = pair.Second,
            })));
    }

    private Task<HttpResponseMessage> ChangePasswordAsync(
        string accessToken, string currentPassword, string newPassword)
        => CreateAuthedClient(accessToken).PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword,
            newPassword,
        });

    private HttpClient CreateAuthedClient(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<bool> LoginSucceedsAsync(string email, string password)
    {
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        return resp.StatusCode == HttpStatusCode.OK;
    }

    private async Task<int> ReadAuthVersionAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Customers.AsNoTracking()
            .Where(c => c.Id == customerId).Select(c => c.AuthVersion).SingleAsync();
    }

    private async Task<string> IssueAccessTokenAsync(Guid customerId, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
        return await Task.FromResult(jwt.IssueCustomerToken(customerId, email).Token);
    }

    private async Task<(Guid Id, string Email)> SeedCustomerAsync(string password)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"authver-{Guid.NewGuid():N}@x",
            Name = "Yayıncı",
            PasswordHash = hasher.Hash(password),
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return (customer.Id, customer.Email);
    }
}
