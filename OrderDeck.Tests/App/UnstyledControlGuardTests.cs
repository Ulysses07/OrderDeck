using System.Text.RegularExpressions;

namespace OrderDeck.Tests.App;

/// <summary>
/// KALICI BEKÇİ — Faz 4b ile geldi.
///
/// NEDEN VAR: Faz 4b <c>DarkControls.xaml</c>'in örtük stillerini sildi. Örtük
/// stil yokken <c>&lt;Button/&gt;</c> yazmak derlemeyi kırmaz, çalışma anında
/// da patlamaz — Windows'un açık gri varsayılan görünümüyle koyu zeminin
/// üstünde durur. Gözle gezmeden fark edilmez; tek kanıt tarama.
///
/// KAPSAM DIŞI TİPLER ve gerekçeleri:
/// - <c>ListBoxItem</c> / <c>ComboBoxItem</c> / <c>TabItem</c> /
///   <c>MenuItem</c>: stilleri ebeveynin <c>ItemContainerStyle</c>'ından
///   geliyor. Bu ilişkiyi metin taramasıyla çözmek kırılgan olurdu ve
///   kullanım yerinde stil olmaması burada DOĞRU.
/// - <c>TextBlock</c>: <c>Foreground</c> miras alınan bir bağımlılık
///   özelliği, MainWindow.xaml onu veriyor (Faz 4b'nin ince çekirdek kararı
///   buna dayanıyor).
/// - <c>Themes/</c> ve <c>App.xaml</c>: stil tanımlarının kendisi.
/// </summary>
public class UnstyledControlGuardTests
{
    private static readonly string[] Guarded =
    [
        "Button", "TextBox", "PasswordBox", "CheckBox", "RadioButton",
        "ComboBox", "ListBox", "TabControl", "DataGrid", "Label", "GroupBox",
        "DatePicker", "Slider", "ProgressBar", "ToggleButton",
        "controls:ShortcutCaptureButton",
    ];

    [Fact]
    public void No_view_uses_a_guarded_control_without_a_style()
    {
        var offenders = new List<string>();
        var root = RepoRoot();

        foreach (var file in ViewFiles(root))
        {
            var xaml = File.ReadAllText(file);
            var localImplicit = LocalImplicitTargets(xaml);
            var relative = Path.GetRelativePath(root, file);

            foreach (var name in Guarded)
            {
                if (localImplicit.Contains(name) ||
                    localImplicit.Contains(name.Split(':')[^1]))
                    continue;

                foreach (Match m in ElementTag(name).Matches(xaml))
                {
                    if (Regex.IsMatch(m.Groups[1].Value, "\\bStyle\\s*=\\s*\""))
                        continue;

                    var selfClosing = m.Groups[2].Value == "/";
                    if (!selfClosing &&
                        SkipTrivia(xaml[(m.Index + m.Length)..])
                            .StartsWith("<" + name + ".Style", StringComparison.Ordinal))
                        continue;

                    var line = xaml[..m.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{relative}:{line} — <{name}> stilsiz");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Stilsiz kontrol(ler):" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Baştaki boşlukları ve XML yorumlarını atlar. Gerekçe: kontrolün ilk
    /// çocuğu olan <c>&lt;X.Style&gt;</c>'ın önünde çoğu zaman o stili
    /// açıklayan bir yorum duruyor.
    /// </summary>
    private static string SkipTrivia(string s)
    {
        while (true)
        {
            s = s.TrimStart();
            if (!s.StartsWith("<!--", StringComparison.Ordinal)) return s;

            var end = s.IndexOf("-->", StringComparison.Ordinal);
            if (end < 0) return s;
            s = s[(end + 3)..];
        }
    }

    /// <summary>Nitelik değerinin içindeki &gt; karakterini yutmasın diye tırnak farkındalıklı.</summary>
    private static Regex ElementTag(string name)
        => new("<" + Regex.Escape(name) + "(?![\\w.:])((?:\"[^\"]*\"|[^<>])*?)(/?)>",
               RegexOptions.Singleline);

    private static readonly Regex StyleTag =
        new("<Style(?![\\w.:])((?:\"[^\"]*\"|[^<>])*?)/?>", RegexOptions.Singleline);

    private static readonly Regex TargetTypeAttr =
        new("TargetType\\s*=\\s*\"(?:\\{x:Type\\s+)?([\\w:]+)\\}?\"");

    /// <summary>
    /// Bir <c>*.Resources</c> bloğunun gövdesi. YALNIZ burası taranır: tek bir
    /// kontrolün içine yazılan <c>&lt;Button.Style&gt;&lt;Style …&gt;</c> da
    /// x:Key'siz bir Style'dır, ama o kontrolden başkasını kapsamaz — Resources
    /// ayrımı yapılmazsa bekçi o tipi TÜM dosya için muaf sayar.
    /// </summary>
    private static readonly Regex ResourcesBlock =
        new("<([\\w:]+)\\.Resources\\s*>(.*?)</\\1\\.Resources\\s*>", RegexOptions.Singleline);

    /// <summary>Dosyanın kendi Resources'ında tanımlı, x:Key'siz (= örtük) stillerin hedef tipleri.</summary>
    private static HashSet<string> LocalImplicitTargets(string xaml)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match block in ResourcesBlock.Matches(xaml))
        foreach (Match m in StyleTag.Matches(block.Groups[2].Value))
        {
            var attrs = m.Groups[1].Value;
            if (attrs.Contains("x:Key", StringComparison.Ordinal)) continue;

            var target = TargetTypeAttr.Match(attrs);
            if (target.Success) set.Add(target.Groups[1].Value);
        }

        return set;
    }

    private static IEnumerable<string> ViewFiles(string root)
        => Directory
            .EnumerateFiles(Path.Combine(root, "OrderDeck.App"), "*.xaml",
                            SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)!) != "Themes")
            .Where(f => Path.GetFileName(f) != "App.xaml")
            .OrderBy(f => f, StringComparer.Ordinal);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrderDeck.sln")))
            dir = dir.Parent;

        Assert.True(dir is not null, "OrderDeck.sln bulunamadı — depo kökü tespit edilemedi.");
        return dir!.FullName;
    }
}
