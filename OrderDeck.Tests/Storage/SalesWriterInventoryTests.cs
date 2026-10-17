using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Storage;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Şema koruma testi (spec 2026-10-13 §3.2, §10.3): Label, Shipment ve StreamSession'a yazan HER SQL
/// burada listelidir; her satırın notu yazımın 049 tetikleyicileri karşısındaki anlamını söyler
/// (kuyruğa alma, ABORT, kilit). Liste dışı bir yazım test düşürür — desen
/// <c>CustomerSyncRepositoryTests.Customera_baslanan_her_tablo_tasimada_ele_alinir</c>.
///
/// <para><b>Neden:</b> kuyruğa alma tetikleyicisi unutulan <c>SyncedAt = NULL</c>'u telafi eder, ABORT
/// tetikleyicileri unutulmuş bir yolu görünür hataya çevirir; ama yeni bir yazımın mod On'da hangi
/// tetikleyiciye takılacağını ve canlı yolu kırıp kırmayacağını yazan düşünmeli. W1'in sonraki her
/// görevi (v2 yanıtı, geri doldurma, Y5'in yerel yolu …) bu listeyi günceller.</para>
///
/// <para>Tarama OrderDeck.Core ve OrderDeck.App'teki .cs dosyalarında BÜYÜK harfli SQL fiillerini arar
/// (depodaki SQL büyük harfle yazılır; küçük harfli düzyazı yorumlar eşleşmez). Yalnız yorum
/// satırları atlanır. Dinamik tablo adı (<c>UPDATE {table}</c>) hangi tabloya yazdığı bilinmediği
/// için her zaman listelenir.</para>
/// </summary>
public sealed class SalesWriterInventoryTests
{
    private sealed record Writer(string File, string Member, string Statement, int Count, string[] Triggers, string Note);

    private const string LabelRepo = "OrderDeck.Core/Storage/Repositories/LabelRepository.cs";
    private const string SessionRepo = "OrderDeck.Core/Storage/Repositories/SessionRepository.cs";
    private const string ShipmentRepo = "OrderDeck.Core/Storage/Repositories/ShipmentRepository.cs";
    private const string CustomerSyncRepo = "OrderDeck.Core/Storage/Repositories/CustomerSyncRepository.cs";
    private const string AdjusterFile = "OrderDeck.Core/Sales/ShipmentAmountAdjuster.cs";
    private const string DerivationFile = "OrderDeck.Core/Sales/ShipmentAmountDerivation.cs";

    private static readonly string[] LabelUnit = ["TR_Label_Enqueue", "TR_Label_ServerAuthority_U"];

    private static readonly Writer[] Inventory =
    [
        new(LabelRepo, "Insert", "INSERT INTO Label", 1, ["TR_Label_ServerAuthority_I"],
            "Oluşum. SyncedAt NULL doğar (kuyrukta). Authority='server' oturuma yerel ekleme düşer; o oturuma ekleme komut ya da akışla, kilit altında gelir."),
        new(LabelRepo, "ConfirmTentativeBackups", "UPDATE Label", 1, LabelUnit,
            "Yedek birimi. Revision+1, SyncedAt ve StockSyncedAt NULL kendisi yazar: kuyruğa alma tetikleyicisi etkisiz, çift artış yok. Sunucu otoritesinde düşer."),
        new(LabelRepo, "MarkCancelled", "UPDATE Label", 1, LabelUnit,
            "İptal birimi. Revision+1 ve StockSyncedAt NULL (stok-ilgili) kendisi yazar. Sunucu otoritesinde düşer."),
        new(LabelRepo, "Uncancel", "UPDATE Label", 1, LabelUnit,
            "İptal birimi (geri alma). Revision+1 ve StockSyncedAt NULL kendisi yazar. Sunucu otoritesinde düşer."),
        new(LabelRepo, "MarkPrinted", "UPDATE Label", 1, LabelUnit,
            "Baskı birimi. Revision+1 kendisi yazar, StockSyncedAt'e dokunmaz. Ölü kod (S4 listesi)."),
        new(LabelRepo, "MarkPrintedBatch", "UPDATE Label", 1, LabelUnit,
            "Baskı birimi (048). Revision+1 kendisi yazar, StockSyncedAt'e dokunmaz. Sunucu otoritesinde düşer."),
        new(LabelRepo, "UpdatePriceForCorrection", "UPDATE Label", 1, LabelUnit,
            "Fiyat birimi, koşullu yazım. Revision+1 kendisi yazar. Sunucu otoritesinde düşer."),
        new(LabelRepo, "UpdatePrice", "UPDATE Label", 1, LabelUnit,
            "Fiyat birimi (yedek onayı). Revision+1 kendisi yazar. Sunucu otoritesinde düşer."),
        new(LabelRepo, "MarkSynced", "UPDATE Label", 1, [],
            "Gönderim onayı, F05 karşılaştır-ve-yaz. SyncedAt ve StockSyncedAt hiçbir tetikleyici listesinde değil: her durumda geçer, kuyruğa almaz."),
        new(LabelRepo, "MarkSyncedBatch", "UPDATE Label", 1, [],
            "v2 gönderim onayı (W1-06), tek kısa işlemde, F05 karşılaştır-ve-yaz. SyncedAt, StockSyncedAt ve ServerVersion hiçbir tetikleyici listesinde değil: her kipte ve her oturumda geçer, kuyruğa almaz, Revision değişmez (DM8)."),
        new(SessionRepo, "Insert", "INSERT INTO StreamSession", 1, [],
            "Origin ve Authority varsayılanla 'local' doğar. Ekleme tetikleyicisi yok."),
        new(SessionRepo, "End", "UPDATE StreamSession", 1, ["TR_StreamSession_Enqueue", "TR_StreamSession_ServerAuthority"],
            "EndedAt. Revision+1 kendisi yazar: kuyruğa alma tetikleyicisi etkisiz. Authority='server' oturumda düşer."),
        new(SessionRepo, "MarkSynced", "UPDATE StreamSession", 1, [],
            "Gönderim onayı, F05 karşılaştır-ve-yaz. Tetikleyici dışı."),
        new(SessionRepo, "ApplyRelease", "UPDATE StreamSession", 1, [],
            "Devir yanıtı (W1-07, §5.2): ReleasedAt ve Authority, SyncApplyScope işleminde. İkisi de hiçbir tetikleyici listesinde değil: kuyruğa almaz, Authority='server' satırda da geçer. Authority='server' YALNIZ aynı işlemde okunan Mode = 1 ∧ EndedAt dolu iken; mod Off'ta Authority hiç değişmez, aktif oturumda yalnız ReleasedAt saklanır."),
        new(SessionRepo, "AckIfUnchanged", "UPDATE StreamSession", 1, [],
            "Devir yanıtının onayı (W1-07): MarkSynced'in F05 karşılaştır-ve-yaz koşulu, çağıranın işleminde (devir anıyla aynı SyncApplyScope ya da iz kaydıyla aynı DbWrite). Tetikleyici dışı."),
        new(ShipmentRepo, "Insert", "INSERT INTO Shipment", 1, ["TR_Shipment_ServerOwned_I"],
            "Dosya açılışı. Mod 1'de düşer. OriginReplicaId = _meta.ReplicaId aynı ifadede (W1-04): mod Off'ta dosyanın tek yazıcısı bu kopya. Eski ikilinin NULL'u da bu veritabanının dosyası sayılır (049 başlığı)."),
        new(AdjusterFile, "Apply", "UPDATE Shipment", 1, ["TR_Shipment_Enqueue", "TR_Shipment_ServerOwned_U"],
            "Y5 dosya bloğu (§7.4): CumulativeAmount fark kadar; Revision+1 ve SyncedAt NULL kendisi yazar — push yanıtının current'ı kilit altında uygulanır, kilit TR_Shipment_Enqueue'yu susturur, değişiklik yine gönderilmeli. Mod 1'de yazmaz (kipi aynı işlemde okuyup bloğu atlar): ABORT'a varmaz. Kargoya verilmiş ve yerelde olmayan dosyaya yazmaz."),
        new(DerivationFile, "DeriveOpenFiles", "UPDATE Shipment", 1, ["TR_Shipment_Enqueue", "TR_Shipment_ServerOwned_U"],
            "Y5'in fark tabanı, açılışta bir kez (§7.4, §8.2-8): kargoya verilmemiş dosyanın tutarı bağlardan. Kilit altında DEĞİL ve Revision'ı ARTIRMAZ: TR_Shipment_Enqueue değişen dosyayı kuyruğa alır (ClearStale'den sonra koşar). Yalnız mod 0'da çağrılır: ABORT'a varmaz."),
        new(ShipmentRepo, "Update", "UPDATE Shipment", 1, ["TR_Shipment_Enqueue", "TR_Shipment_ServerOwned_U"],
            "Karar ve tutar. Revision+1 kendisi yazar: kuyruğa alma tetikleyicisi etkisiz. Mod 1'de düşer."),
        new(ShipmentRepo, "MarkSynced", "UPDATE Shipment", 1, [],
            "Gönderim onayı, F05 karşılaştır-ve-yaz. Tetikleyici dışı."),
        new(ShipmentRepo, "AttachLabel", "UPDATE Label", 1, ["TR_Label_Enqueue", "TR_Label_ShipmentLink", "TR_Label_ServerAuthority_U"],
            "Kargo bağı. Revision'ı ARTIRMAZ: TR_Label_Enqueue kuyruğa alır (049'un davranış değişikliği, fazladan bir gönderim). Mod 1'de ve sunucu otoritesinde düşer."),
        new(CustomerSyncRepo, "MoveReferences", "UPDATE {table}", 1, [],
            "Yeniden anahtarlama: CustomerId'yi Label ve Shipment dahil taşır. CustomerId hiçbir tetikleyici listesinde değil: her kipte ve her oturumda yazar, kuyruğa almaz (sunucu kopyayı kendisi taşır, S18)."),
    ];

    /// <summary>Fiil + tablo; tablo adı tırnaklı olabilir (<c>"Label"</c>, <c>\"Label\"</c>, <c>[Label]</c>).
    /// Tablo adından sonra harf/rakam gelmez (LabelBackup, Labels eşleşmez).</summary>
    private static readonly Regex WriteSql = new(
        @"\b(?<verb>UPDATE|INSERT\s+(?:OR\s+[A-Z]+\s+)?INTO|REPLACE\s+INTO|DELETE\s+FROM)\s+[\\""\[`]*(?<table>Label|Shipment|StreamSession|\{\w+\})[\\""\]`]*(?!\w)",
        RegexOptions.Compiled);

    private static readonly Regex MemberDeclaration = new(
        @"^\s*(?:public|private|internal|protected)\b", RegexOptions.Compiled);

    private static readonly Regex MemberName = new(
        @"(?<name>\w+)\s*(?:<[^<>]*>)?\s*\(|(?<name>\w+)\s*=", RegexOptions.Compiled);

    [Fact]
    public void Label_Shipment_ve_StreamSessiona_yazan_her_SQL_envanterde_ve_tetikleyici_anlami_semayla_tutarli()
    {
        // 1) Tarayıcının kendisi: bütün yazım biçimlerini görür, benzer adlı tabloları ve yorumları görmez.
        Scan("x.cs", @"
            public void A() { Execute(""UPDATE Label SET Price = 1""); }
            public void B() { Execute(""INSERT OR IGNORE INTO Shipment (Id) VALUES (1)""); }
            public void C() { Execute(""REPLACE INTO StreamSession (Id) VALUES (1)""); }
            public void D() { Execute(""DELETE FROM Label WHERE Id = 1""); }
            public void E() { Execute($""UPDATE {table} SET CustomerId = 1""); }
            public void F() { Execute(""INSERT INTO \""Label\"" (Id) VALUES (1)""); }
            public void G() { Execute(""UPDATE LabelBackup SET X = 1; INSERT INTO Labels (Id) VALUES (1)""); }
            public void H() { Execute(""INSERT INTO Customer (Id) VALUES (1) ON CONFLICT(Id) DO UPDATE SET X = 1""); }
            // UPDATE Label SET yorumdaki SQL sayılmaz
            ")
            .Select(w => $"{w.Member}:{w.Statement}")
            .Should().Equal("A:UPDATE Label", "B:INSERT INTO Shipment", "C:REPLACE INTO StreamSession",
                "D:DELETE FROM Label", "E:UPDATE {table}", "F:INSERT INTO Label");

        // 2) Envanterin andığı her tetikleyici şemada var; 049'un bu üç tablodaki her tetikleyicisi en az
        //    bir yazımı yönetir (yoksa ya tetikleyici gereksiz ya envanter eksik).
        using (var db = new InMemorySqlite())
        {
            new MigrationRunner(db).Run();
            using var c = db.Open();
            var schema = c.Query<string>(@"SELECT name FROM sqlite_master WHERE type = 'trigger'
                                            AND tbl_name IN ('Label', 'Shipment', 'StreamSession')").ToHashSet(StringComparer.Ordinal);
            var named = Inventory.SelectMany(w => w.Triggers).ToHashSet(StringComparer.Ordinal);
            named.Should().BeSubsetOf(schema, "envanterin notları göç 049'un tetikleyicilerine dayanır");
            schema.Should().BeSubsetOf(named, "her tetikleyici en az bir yazımın notunda");
        }

        // 3) Depodaki yazımlar = envanter.
        var root = RepoRoot();
        var found = new[] { "OrderDeck.Core", "OrderDeck.App" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsBuildOutput(root, f))
            .SelectMany(f => Scan(Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)))
            .GroupBy(w => Key(w.File, w.Member, w.Statement))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        found.Should().NotBeEmpty("ön koşul: tarama depoyu buldu");

        var listed = Inventory.ToDictionary(w => Key(w.File, w.Member, w.Statement), w => w.Count, StringComparer.Ordinal);
        found.Where(kv => !listed.TryGetValue(kv.Key, out var n) || n != kv.Value)
            .Select(kv => $"{kv.Key} ×{kv.Value}")
            .Should().BeEmpty(
                "Label/Shipment/StreamSession'a yeni ya da değişmiş yazım: 049 tetikleyicileri karşısındaki anlamını " +
                "(Revision'ı kendisi mi artırıyor, mod 1'de ya da Authority='server'da düşer mi, kilit altında mı) " +
                "düşünüp SalesWriterInventoryTests.Inventory'ye ekle");
        listed.Keys.Except(found.Keys)
            .Should().BeEmpty("envanterde olup depoda olmayan yazım: satırı kaldır ya da yeni yerine taşı");
    }

    private static string Key(string file, string member, string statement) => $"{file} :: {member} :: {statement}";

    private static IEnumerable<(string File, string Member, string Statement)> Scan(string file, string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) continue;
            foreach (Match m in WriteSql.Matches(lines[i]))
            {
                var verb = Regex.Replace(m.Groups["verb"].Value, @"\s+", " ");
                verb = Regex.Replace(verb, @"^INSERT OR [A-Z]+ INTO$", "INSERT INTO");
                yield return (file, EnclosingMember(lines, i), $"{verb} {m.Groups["table"].Value}");
            }
        }
    }

    /// <summary>Yazımı içeren üye: geriye doğru ilk erişim belirleyicili bildirimin adı (metot, alan ya da
    /// sabit). Yerel fonksiyon ve lambda içindeki yazım çevreleyen metoda yazılır.</summary>
    private static string EnclosingMember(string[] lines, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            if (!MemberDeclaration.IsMatch(lines[i])) continue;
            var m = MemberName.Match(lines[i]);
            if (m.Success) return m.Groups["name"].Value;
        }
        return "?";
    }

    private static bool IsBuildOutput(string root, string path)
    {
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrderDeck.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "OrderDeck.sln bulunamadı — depo kökü tespit edilemedi.");
        return dir!.FullName;
    }
}
