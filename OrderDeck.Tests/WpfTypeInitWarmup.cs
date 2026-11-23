using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;

namespace OrderDeck.Tests;

/// <summary>
/// WPF'in statik kurucularını (tip başlatıcıları) testlerden ÖNCE ve TEK iş parçacığında bitirir.
///
/// <para><b>Kapattığı kilitlenme.</b> 19.11.2026'da paket süresiz asıldı; olaya karışan iki test
/// sınıfı tek başına koşulunca yaklaşık iki koşuda bir yeniden üretildi. WPF'in paylaşılan BAML
/// şema bağlamındaki tek kilit iki ters sırayla alınıyor:</para>
/// <list type="bullet">
/// <item>BAML okuyan iş parçacığı kilidi ALIR, sonra tip başlatıcısı çalıştırır:
/// <c>WpfSharedBamlSchemaContext.GetKnownBamlMember</c> kilit altında "bilinen üyenin"
/// <c>DependencyProperty</c> alanını okur (<c>&lt;ScrollViewer VerticalScrollBarVisibility=…&gt;</c> →
/// <c>ScrollViewer..cctor</c> → <c>ItemsControl</c> başlatıcısı).</item>
/// <item>Tip başlatıcısının İÇİNDEKİ iş parçacığı aynı kilidi ister: varsayılan şablon kuran her
/// statik kurucu <c>FrameworkElementFactory.Type</c> → <c>GetKnownXamlType</c> yolundan o kilide
/// girer (<c>ItemsControl..cctor</c> → <c>ContentPresenter..cctor</c>).</item>
/// </list>
/// <para>xUnit koleksiyonları paralel koştuğu için STA'sız bir test (<c>ListCollectionView</c>
/// üzerinden <c>ItemsControl</c>'e ilk dokunan görünüm modeli testi) ile XAML yükleyen bir STA testi
/// bu iki ucu aynı anda tutabiliyordu: biri başlatıcının içinde kilidi, öteki kilidin içinde
/// başlatıcıyı bekler. Uçlardan biri Monitor olduğu için CLR döngüyü göremez; STA testi
/// <c>ThemeTestHost</c> kilidini de bırakmadığından bütün WPF testleri arkasında asılı kalır.</para>
///
/// <para><b>Neden burada biter.</b> O kilit altında WPF'in başka tipten okuduğu tek statik alan,
/// bilinen üyelerin <c>DependencyProperty</c> alanlarıdır; hepsi <see cref="DependencyPropertyOwners"/>
/// kümesindeki tiplerde tanımlı. Bu tiplerin başlatıcıları önceden bittiği için kilidi tutan iş
/// parçacığı, o kilide ihtiyaç duyan hiçbir tip başlatıcısını artık bekleyemez — döngünün bir ucu
/// yok. Hangi test hangi WPF tipine hangi iş parçacığından ilk dokunursa dokunsun sonuç değişmez;
/// yeni yazılan bir test bu kilit üzerinden aynı kilitlenmeyi geri getiremez. Ölçüm (19.11.2026,
/// .NET 10.0.11): WPF 10 kaynağındaki 227 kilit-altı okumanın hepsi bu kümede; ısınmadan sonra
/// bilinen üyelerin tamamı (269) oluşturulurken tek bir WPF tip başlatıcısı çalışmıyor.</para>
///
/// <para><b>Neden modül başlatıcısı.</b> Modül başlatıcısı bitmeden bu derlemedeki hiçbir kod,
/// dolayısıyla hiçbir test çalışamaz: ısınma kendiliğinden tek iş parçacıklıdır. Aynı nedenle işi
/// ayrı bir iş parçacığına VERME — o da bu modülün kodunu çalıştıracağı için başlatıcının bitmesini
/// bekler, başlatıcı da onu.</para>
///
/// <para>Bedeli test süreci başına bir kez, boşta ~0,3 sn (352 tip); çoğu WPF'i yüklemenin sabit
/// bedeli. WPF testi içeren koşuda büyük kısmı zaten ödenecekti; WPF'e hiç dokunmayan süzgeçli koşuya
/// ve test keşfine eklenir.</para>
/// </summary>
internal static class WpfTypeInitWarmup
{
    /// <summary>Isınma bitti mi. Yalnız modül başlatıcısı çalıştıysa true (koruma testi bunu okur).</summary>
    internal static bool Completed { get; private set; }

    /// <summary>Isınmada statik kurucusu düşen tipler. Koruma testi boş olduğunu doğrular.</summary>
    internal static IReadOnlyList<(Type Type, Exception Error)> Failures { get; private set; } = [];

    [ModuleInitializer]
    internal static void RunDependencyPropertyOwnerInitializers()
    {
        Failures = RunInitializers(DependencyPropertyOwners());
        Completed = true;
    }

    /// <summary>Verilen tiplerin statik kurucularını sırayla çalıştırır; düşenleri döndürür.</summary>
    internal static List<(Type Type, Exception Error)> RunInitializers(IEnumerable<Type> types)
    {
        var failures = new List<(Type Type, Exception Error)>();

        foreach (var type in types)
        {
            // Düşen kurucu ısınmayı durdurmaz: burada fırlatmak, hiçbir testin kullanmadığı tek bir
            // tip yüzünden derlemedeki BÜTÜN testleri düşürürdü. Koruma da eksilmez: kurucusu düşmüş
            // tip "bitmiş" sayılır, sonraki erişim beklemeden aynı hatayı verir. Hata yutulmuyor:
            // koruma testi listeyi boş bekler, tipi kullanan testler de kendi hatasıyla düşer.
            try { RuntimeHelpers.RunClassConstructor(type.TypeHandle); }
            catch (Exception ex) { failures.Add((type, ex)); }
        }

        return failures;
    }

    /// <summary>
    /// Çekirdek üç WPF derlemesinde (WindowsBase, PresentationCore, PresentationFramework) public
    /// static <see cref="DependencyProperty"/> alanı TANIMLAYAN public tipler. Kilit altında oluşturulan
    /// bilinen BAML üyeleri yalnız bu derlemelerdeki bu tür alanları okur. Küme gerekenden geniş:
    /// liste elle tutulmuyor ki eksik kalmasın.
    /// </summary>
    internal static IEnumerable<Type> DependencyPropertyOwners()
    {
        Assembly[] core =
        [
            typeof(DependencyObject).Assembly,
            typeof(UIElement).Assembly,
            typeof(FrameworkElement).Assembly,
        ];

        return core
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => !type.ContainsGenericParameters)
            .Where(type => type
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(field => field.FieldType == typeof(DependencyProperty)));
    }
}
