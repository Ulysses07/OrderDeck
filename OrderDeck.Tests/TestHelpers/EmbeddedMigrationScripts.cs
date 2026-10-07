using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OrderDeck.Core.Storage;

namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Gömülü göç betiklerini verilen sürüme KADAR yükler: eski bir şema dünyası kurulup veri
/// ekildikten sonra tam <see cref="MigrationRunner"/> koşusu kalan göçleri gerçek satırlar
/// üstünde sınar.
/// </summary>
public static class EmbeddedMigrationScripts
{
    public static IReadOnlyList<(int Version, string Sql)> UpTo(int maxVersion)
    {
        var asm = typeof(MigrationRunner).Assembly;
        const string prefix = "OrderDeck.Core.Storage.Migrations.";
        var list = new List<(int Version, string Sql)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(".sql", StringComparison.Ordinal))
                continue;
            var file = name.Substring(prefix.Length);
            var version = int.Parse(file.Substring(0, file.IndexOf('_')), CultureInfo.InvariantCulture);
            if (version > maxVersion) continue;
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add((version, reader.ReadToEnd()));
        }
        return list.OrderBy(t => t.Version).ToList();
    }
}
