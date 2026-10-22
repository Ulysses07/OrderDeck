using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Ortak bakiye §4.11: <c>GroupId</c> kancası yalnız izlenen nesnelerdeki değişimi görür. Sunucu kodunda
/// <c>GroupId</c>'ye <c>ExecuteUpdate</c> ya da ham SQL ile yazım olursa kanca onu kaçırır: ayırma, apply'ın
/// okuduğu bakiye satırının jetonunu ilerletmez ve para ayrılmış satırdan çekilebilir. Kaynak taranır (göçler hariç).
/// </summary>
public sealed class GroupIdWriteConventionTests
{
    private static readonly Regex ExecuteUpdateOnGroupId =
        new(@"SetProperty\s*\(\s*\w+\s*=>\s*\w+\.GroupId\s*,", RegexOptions.Compiled);

    // SQL anahtar sözcükleri bu kod tabanında büyük harfle yazılıyor; GroupIdChangedAt eşleşmez (\b).
    private static readonly Regex RawSqlOnGroupId =
        new(@"\bUPDATE\b[^;]{0,400}?\bSET\b[^;]{0,400}?\[?\bGroupId\b\]?\s*=(?!=)",
            RegexOptions.Compiled | RegexOptions.Singleline);

    [Theory]
    [InlineData("db.WpfCustomerProjections.ExecuteUpdateAsync(s => s.SetProperty(p => p.GroupId, (string?)null));")]
    [InlineData("\"UPDATE WpfCustomerProjections SET GroupId = @g WHERE Id = @id\"")]
    [InlineData("\"UPDATE [WpfCustomerProjections] SET [UpdatedAt] = @u, [GroupId] = NULL WHERE [Id] = @id\"")]
    public void Kaliplar_yasak_yazimi_yakalar(string sample) =>
        (ExecuteUpdateOnGroupId.IsMatch(sample) || RawSqlOnGroupId.IsMatch(sample)).Should().BeTrue();

    [Theory]
    [InlineData("row.GroupId = target;")]
    [InlineData("\"UPDATE Orders SET UpdatedAt = @u\"; var x = p.GroupIdChangedAt;")]
    [InlineData("s.SetProperty(p => p.GroupIdChangedAt, now)")]
    public void Kaliplar_izinli_yazimi_yakalamaz(string sample) =>
        (ExecuteUpdateOnGroupId.IsMatch(sample) || RawSqlOnGroupId.IsMatch(sample)).Should().BeFalse();

    [Fact]
    public void Sunucu_kodunda_GroupIdye_ExecuteUpdate_ya_da_ham_SQL_ile_yazim_yok()
    {
        var root = ServerSourceRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Excluded(root, file))
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return ExecuteUpdateOnGroupId.IsMatch(text) || RawSqlOnGroupId.IsMatch(text);
            })
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();

        offenders.Should().BeEmpty(
            "GroupId yalnız izlenen nesneler üzerinden yazılır — PersonGroupSaveHook başka yazımı göremez (§4.11)");
    }

    private static bool Excluded(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        return relative.StartsWith("Data/Migrations/", StringComparison.Ordinal)
            || relative.StartsWith("bin/", StringComparison.Ordinal)
            || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Sunucu projesinin kaynak kökü, test çıktısından yukarı aranarak. Bulunamazsa sessizce geçmek yerine patlar
    /// (<c>GraphApiVersionSingleSourceTests</c> deseni): hiçbir dosyayı taramayan yeşil bir test, testin hiç
    /// olmamasından beterdir.
    /// </summary>
    private static string ServerSourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "OrderDeck.LicenseServer");
            if (File.Exists(Path.Combine(candidate, "OrderDeck.LicenseServer.csproj"))) return candidate;
        }
        throw new InvalidOperationException("OrderDeck.LicenseServer kaynak dizini bulunamadı (test kaynak ağacından koşmalı).");
    }
}
