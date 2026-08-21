using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.Core.Chat;
using Xunit;

namespace OrderDeck.Tests.Chat;

public class SpamFilterTests
{
    private static SpamFilter Make(SpamFilterSettings s) => new(() => s);

    // Most rules don't depend on the username — use a fixed value for those
    // tests. The duplicate-rule tests below pass distinct usernames where the
    // scoping behaviour matters.
    private const string U = "user1";

    [Fact]
    public void Disabled_filter_passes_everything()
    {
        var s = new SpamFilterSettings
        {
            Enabled = false,
            DropLinks = true,
            DropProfanity = true,
            DropDuplicates = true,
            BlockedWords = new() { "kötü" }
        };
        var f = Make(s);

        f.ShouldDrop("https://spam.com", U, 100).Should().BeNull();
        f.ShouldDrop("kötü adam",        U, 100).Should().BeNull();
        f.ShouldDrop("aynı mesaj",       U, 100).Should().BeNull();
        f.ShouldDrop("aynı mesaj",       U, 101).Should().BeNull();
    }

    [Fact]
    public void DropShortMessages_blocks_below_threshold_only()
    {
        var s = new SpamFilterSettings { DropShortMessages = true, MinMessageLength = 3 };
        var f = Make(s);

        f.ShouldDrop("ok",   U, 100).Should().Be("short");
        f.ShouldDrop("aa",   U, 100).Should().Be("short");
        f.ShouldDrop("evet", U, 100).Should().BeNull();
    }

    [Fact]
    public void DropLinks_catches_url_shapes()
    {
        var f = Make(new SpamFilterSettings { DropLinks = true });

        f.ShouldDrop("https://example.com check this", U, 100).Should().Be("link");
        f.ShouldDrop("www.x.com",                      U, 100).Should().Be("link");
        f.ShouldDrop("bence shopify.com çok güzel",    U, 100).Should().Be("link");
        f.ShouldDrop("siteleri çok beğendim",          U, 100).Should().BeNull();
    }

    [Fact]
    public void DropAllCaps_requires_both_length_and_uppercase_ratio()
    {
        var f = Make(new SpamFilterSettings { DropAllCaps = true });

        f.ShouldDrop("ÇOK GÜZEL ÜRÜN BU", U, 100).Should().Be("allcaps");
        // Single uppercase word OK
        f.ShouldDrop("XL beden var mı",   U, 100).Should().BeNull();
        // Short text doesn't trigger (might be a brand name)
        f.ShouldDrop("WOW",               U, 100).Should().BeNull();
    }

    [Fact]
    public void DropProfanity_uses_whole_word_match()
    {
        var s = new SpamFilterSettings
        {
            DropProfanity = true,
            BlockedWords = new() { "kötü" }
        };
        var f = Make(s);

        f.ShouldDrop("bu çok kötü bir ürün", U, 100).Should().Be("profanity");
        f.ShouldDrop("KÖTÜ ürün",            U, 100).Should().Be("profanity");
        // Substring shouldn't match — "köyüm" contains the same letters but
        // not the word.
        f.ShouldDrop("köyüm güzel",          U, 100).Should().BeNull();
        f.ShouldDrop("hiç de fena değil",    U, 100).Should().BeNull();
    }

    [Fact]
    public void DropDuplicates_same_user_same_text_within_2s()
    {
        // Same user pasting the same message twice within 2s → drop.
        // After 2s the window has expired so the next post passes.
        var f = Make(new SpamFilterSettings { DropDuplicates = true });

        f.ShouldDrop("aynı yazı", U, 100).Should().BeNull();        // first time → ok
        f.ShouldDrop("aynı yazı", U, 101).Should().Be("duplicate"); // 1s later → drop (within 2s)
        f.ShouldDrop("aynı yazı", U, 103).Should().BeNull();        // 3s later → window expired, ok
    }

    [Fact]
    public void DropDuplicates_does_not_scope_across_users()
    {
        // Critical: a giveaway keyword typed by 50 different viewers within
        // 2s must NOT be dropped after the first. Per-user scoping is the
        // whole point of the rewrite.
        var f = Make(new SpamFilterSettings { DropDuplicates = true });

        f.ShouldDrop("çekilis", "user-a", 100).Should().BeNull();
        f.ShouldDrop("çekilis", "user-b", 100).Should().BeNull();
        f.ShouldDrop("çekilis", "user-c", 101).Should().BeNull();
        // Same user, same text, within window → still drops.
        f.ShouldDrop("çekilis", "user-a", 101).Should().Be("duplicate");
    }

    [Fact]
    public void DropDuplicates_is_case_insensitive_and_trim_aware()
    {
        var f = Make(new SpamFilterSettings { DropDuplicates = true });

        f.ShouldDrop("MERHABA",     U, 100).Should().BeNull();
        f.ShouldDrop("merhaba",     U, 100).Should().Be("duplicate");
        f.ShouldDrop("  merhaba  ", U, 101).Should().Be("duplicate");
    }

    [Fact]
    public void Empty_or_whitespace_message_passes_through()
    {
        var f = Make(new SpamFilterSettings { DropShortMessages = true, MinMessageLength = 5 });
        f.ShouldDrop("",    U, 100).Should().BeNull();
        f.ShouldDrop("   ", U, 100).Should().BeNull();
    }

    [Fact]
    public void Multiple_rules_first_match_wins()
    {
        var s = new SpamFilterSettings
        {
            DropShortMessages = true, MinMessageLength = 5,
            DropLinks = true,
            DropDuplicates = true
        };
        var f = Make(s);

        // "go" hits short before anything else.
        f.ShouldDrop("go", U, 100).Should().Be("short");
    }

    // ── Eşzamanlılık (Y-05) ────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SpamFilter"/> tekil (singleton) kaydedilir ve dört ayrı
    /// döngüden aynı anda çağrılır: köprünün soket başına alma döngüsü,
    /// YouTube resmi API yoklayıcısı, Instagram yoklayıcısı ve Facebook akışı.
    /// Yinelenen penceresi bu çağrılar arasında paylaşıldığı için okuma+yazma
    /// bölümünün eşzamanlı erişime dayanması şart.
    ///
    /// <para>Farklı kullanıcı + farklı metin gönderiliyor, yani <i>hiçbir</i>
    /// çağrının "duplicate" dönmemesi gerekiyor. Zaman aşımı bilerek var:
    /// bağlı listede döngü oluşursa <c>foreach</c> hiç bitmez, testin asılı
    /// kalması yerine düşmesini istiyoruz.</para>
    /// </summary>
    [Fact]
    public async Task Duplicate_window_survives_concurrent_ingest()
    {
        var f = Make(new SpamFilterSettings { DropDuplicates = true });

        const int sources = 4;
        const int perSource = 20_000;

        var start = new System.Threading.Barrier(sources);
        var tasks = Enumerable.Range(0, sources).Select(source => Task.Run(() =>
        {
            var drops = new List<string>();
            start.SignalAndWait();
            for (var i = 0; i < perSource; i++)
            {
                // Sabit "şimdi": pencere hiç dolmuyor, dolayısıyla her çağrı
                // hem listeyi tarıyor hem de ekleyip (200'ü aşınca) baştan
                // siliyor — yarışın en sıkı hâli.
                var reason = f.ShouldDrop($"mesaj-{source}-{i}", $"kullanici-{source}", 100);
                if (reason is not null) drops.Add(reason);
            }
            return drops;
        })).ToArray();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        results.SelectMany(d => d).Should().BeEmpty(
            "her mesaj benzersiz; 'duplicate' dönmesi pencerenin bozulduğu anlamına gelir");
    }
}
