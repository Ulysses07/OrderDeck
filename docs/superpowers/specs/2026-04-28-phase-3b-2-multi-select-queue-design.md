# Faz 3b-2 — Kuyrukta Multi-Select + Akıllı Yazdırma + Enter Chat→Queue (Tasarım)

**Hedef:** Yayıncının yazdırma kuyruğunda birden çok etiketi seçip silebilmesi veya seçili alt-kümeyi yazdırabilmesi; chat'ten Enter ile etiket eklemesi.

**Kapsam:** `MainShellViewModel` + `MainShellView` davranış genişletmesi. Yeni dosya yok, mimari değişiklik yok.

**Pre-Faz-3b-2 state:** Faz 3b-1 HEAD `17af78d`. 114/114 test geçer.

---

## 1. Bağlam

**Sorun:** Mevcut yazdırma kuyruğu `ListBox` `SelectionMode="Single"` (default). Yayıncı kuyruğa düşmüş bir etiketin yanlış olduğunu fark etse, sadece tek tek silebilir. 10 etiketten 3'ünü silmek için 3 kez "Seçileni Sil" tıklamak zorunda. Çift-tık + yanlış mesaj kombosu yaygın bir hata kaynağı; toplu temizlik kullanışlı olur.

Phase 3b-1 brainstorm'unda "Enter chat→queue" ve "Ctrl+A queue all-select" kısayolları context routing engeline çarpıp ertelenmişti. Phase 3b-2'de sabit kodlu (event handler) yaklaşımla çözüyoruz.

**Kazanım:** Multi-select + smart Print birleşince yayıncı şöyle de kullanabilir:
1. Kuyruk doldu, içinden 5 etiket seç
2. "Yazdır (5)" sadece seçilenleri basar; kalanlar kuyrukta kalır (örneğin daha sonraki bir batch için)

---

## 2. Mimari Etkisi

Mimari değişiklik yok. Mevcut MVVM + ListBox pattern'i aynı.

**Etkilenen dosyalar:**

| Katman | Dosya | Eylem |
|---|---|---|
| Labeling — interface | `LiveDeck.Labeling/ILabelPrinter.cs` | Yeni — printer abstraction (test edilebilirlik) |
| Labeling — impl | `LiveDeck.Labeling/LabelPrinter.cs` | Modify — `: ILabelPrinter` |
| VM | `LiveDeck.App/ViewModels/MainShellViewModel.cs` | Modify — `LabelPrinter` → `ILabelPrinter`, `SelectedQueueItems`, derived label property'leri, akıllı `Print`, multi-delete |
| AppHost | `LiveDeck.App/AppHost.cs` | Modify — DI'da `ILabelPrinter` register |
| View | `LiveDeck.App/Views/MainShellView.xaml` | Modify — QueueList `SelectionMode="Extended"` + handler, button binding'leri |
| View code-behind | `LiveDeck.App/Views/MainShellView.xaml.cs` | Modify — `QueueList_OnSelectionChanged` + `ChatList_OnPreviewKeyDown` |
| Tests | `LiveDeck.Tests/App/MainShellPrintTests.cs` | Yeni — akıllı Print + multi-delete davranışı (fake `ILabelPrinter`) |

**Yeni dosya:** 2 (interface + test).

---

## 3. Detaylar

### 3.1 MainShellViewModel — yeni state

```csharp
/// <summary>Multi-select kuyrukta seçili olan etiketler. Code-behind QueueList.SelectionChanged
/// event'inden senkronize eder. Boş = hiç seçim yok.</summary>
public ObservableCollection<LabelViewModel> SelectedQueueItems { get; } = new();

/// <summary>Yazdır butonu için dinamik label.</summary>
public string PrintButtonLabel => SelectedQueueItems.Count > 0
    ? $"Yazdır ({SelectedQueueItems.Count})"
    : "Yazdır";

/// <summary>Sil butonu için dinamik label.</summary>
public string DeleteButtonLabel => SelectedQueueItems.Count switch
{
    0 => "Seçileni Sil",
    1 => "Seçileni Sil",
    _ => $"Seçilenleri Sil ({SelectedQueueItems.Count})"
};
```

Mevcut `[ObservableProperty] private LabelViewModel? _selectedQueueItem;` (Phase 3b-1) **kalır** — primary selection için (ContextMenu, native single-select fallback). Multi-select onun üstüne katmanlanır.

Constructor'a:
```csharp
SelectedQueueItems.CollectionChanged += (_, _) =>
{
    OnPropertyChanged(nameof(PrintButtonLabel));
    OnPropertyChanged(nameof(DeleteButtonLabel));
};
```

### 3.2 MainShellViewModel — akıllı Print

```csharp
[RelayCommand]
private void Print()
{
    var snapshot = SelectedQueueItems.Count > 0
        ? SelectedQueueItems.ToList()
        : PrintQueue.ToList();
    if (snapshot.Count == 0) return;

    var labels = snapshot.Select(vm => vm.Label).ToList();
    _printer.Print(labels);
    _labels.MarkPrintedAndRecord(labels.Select(l => l.Id).ToList());

    // Sadece yazdırılanları kuyruktan kaldır
    foreach (var vm in snapshot) PrintQueue.Remove(vm);
    SelectedQueueItems.Clear();
}
```

**Davranış matrisi:**

| Durum | Yazdırılan | Kuyrukta kalan |
|---|---|---|
| Seçim yok, kuyruk boş | (nothing) | Boş |
| Seçim yok, kuyruk N | N hepsi | Boş |
| 3 seçili / 10 kuyrukta | 3 seçili | 7 kalan |
| 10/10 seçili | 10 hepsi | Boş |

`EndStream` flow'undaki `Print()` çağrısı (mevcut, fire-and-forget) aynı davranır — stream sonunda kullanıcı multi-select etmemişse hepsi yazdırılır.

### 3.3 MainShellViewModel — multi-delete

```csharp
[RelayCommand]
private void RemoveSelectedFromQueue()
{
    if (SelectedQueueItems.Count == 0) return;
    foreach (var vm in SelectedQueueItems.ToList())
    {
        _labels.Delete(vm.Id);
        PrintQueue.Remove(vm);
    }
    SelectedQueueItems.Clear();
}

// Phase 3b-1 shortcut komutu da aynı yola döner.
[RelayCommand]
private void DeleteSelectedFromQueueViaShortcut() => RemoveSelectedFromQueue();
```

**Önemli:** `RemoveSelectedFromQueueCommand` artık `CommandParameter` almıyor. XAML'deki bağlamayı kaldırırız (bkz. §3.5).

`SelectedQueueItems.ToList()` snapshot al — silme sırasında collection mutate olur.

### 3.4 MainShellView.xaml — QueueList değişikliği

Mevcut:
```xml
<ListBox Grid.Row="1"
         x:Name="QueueList"
         ItemsSource="{Binding PrintQueue}"
         SelectedItem="{Binding SelectedQueueItem, Mode=TwoWay}"
         Background="..." Foreground="..." BorderBrush="..." BorderThickness="1">
```

Yeni:
```xml
<ListBox Grid.Row="1"
         x:Name="QueueList"
         ItemsSource="{Binding PrintQueue}"
         SelectedItem="{Binding SelectedQueueItem, Mode=TwoWay}"
         SelectionMode="Extended"
         SelectionChanged="QueueList_OnSelectionChanged"
         Background="..." Foreground="..." BorderBrush="..." BorderThickness="1">
```

`SelectionMode="Extended"` Ctrl+click, Shift+click, Ctrl+A native davranışını otomatik etkinleştirir.

### 3.5 MainShellView.xaml — buton bindingleri

Mevcut:
```xml
<Button Content="Yazdır" Command="{Binding PrintCommand}" .../>
<Button Content="Seçileni Sil"
        Command="{Binding RemoveSelectedFromQueueCommand}"
        CommandParameter="{Binding ElementName=QueueList, Path=SelectedItem}"
        Padding="14,8" Margin="8,0,0,0"/>
```

Yeni:
```xml
<Button Content="{Binding PrintButtonLabel}" Command="{Binding PrintCommand}" .../>
<Button Content="{Binding DeleteButtonLabel}"
        Command="{Binding RemoveSelectedFromQueueCommand}"
        Padding="14,8" Margin="8,0,0,0"/>
```

`CommandParameter` kalkar — VM artık kendi state'inden multi-select listesini okur.

`Hepsini Temizle` butonu aynen kalır.

### 3.6 MainShellView.xaml.cs — sync handler + Enter

```csharp
private void QueueList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (DataContext is not MainShellViewModel vm) return;

    foreach (var added in e.AddedItems.OfType<LabelViewModel>())
        if (!vm.SelectedQueueItems.Contains(added))
            vm.SelectedQueueItems.Add(added);

    foreach (var removed in e.RemovedItems.OfType<LabelViewModel>())
        vm.SelectedQueueItems.Remove(removed);
}

private void ChatList_OnPreviewKeyDown(object sender, KeyEventArgs e)
{
    if (e.Key != Key.Enter) return;
    if (DataContext is not MainShellViewModel vm) return;
    if (ChatList.SelectedItem is not ChatMessageViewModel msgVm) return;

    vm.AddChatToQueue(msgVm);
    e.Handled = true;
}
```

`ChatList`'e XAML'de `PreviewKeyDown="ChatList_OnPreviewKeyDown"` eklenir. Mevcut `MouseDoubleClick` aynen kalır.

`Add` ve `Remove` `Contains` check'i: WPF bazı senaryolarda aynı item'ı iki kez `AddedItems`'a koyabilir (collection re-binding); duplicate guard.

`using` direktifleri: `System.Windows.Input` (`KeyEventArgs`, `Key`), `System.Linq` (`OfType`).

---

## 4. Hata Yönetimi

| Senaryo | Davranış |
|---|---|
| Multi-select varken Print | Sadece seçili etiketler yazdırılır; kalanlar korunur |
| Hiç seçim + boş kuyruk + Print | Erken return (mevcut davranış) |
| Multi-delete sırasında DB exception | Mevcut tek-silmedeki davranış: tek-tek `_labels.Delete` çağrılarından biri throw ederse kalan silimler atlanır. PrintQueue half-state'te kalır. YAGNI: try/catch eklemiyoruz |
| Multi-select + ContextMenu "Müşteri Detayı" / "Kara Listeye Al" | Mevcut single `SelectedQueueItem` kullanılıyor (primary), multi-select etkilemez |
| Enter chat'te seçim yokken | Erken return, no-op |
| Enter `ActiveCode`/`ActivePriceText` TextBox focus iken | `ChatList_OnPreviewKeyDown` sadece ChatList içindeki Enter'da fire eder; TextBox'lar etkilenmez |
| WPF SelectionChanged duplicate AddedItems | `Contains` guard ile no-op |
| Yazdırılan etiket sayısı 0 (boş seçim olmuş) | `snapshot.Count == 0` → erken return |

---

## 5. Test Stratejisi

### 5.1 Yeni unit testler (~3)

`LiveDeck.Tests/App/MainShellPrintTests.cs`:

| Test | Doğruladığı |
|---|---|
| `Print_with_no_selection_prints_all_and_empties_queue` | Mevcut davranış korunmuş |
| `Print_with_partial_selection_prints_selected_only_and_keeps_remainder` | Akıllı print kalanları korur |
| `RemoveSelectedFromQueue_with_multi_selection_deletes_all_selected` | Multi-delete |

`MainShellViewModel`'in tam DI graph'ı (chat bus, label svc, vs) gerektiriyor; mevcut Phase 1+ test fixture'ları zaten bu pattern'i kullanıyor (`InMemorySqlite` + gerçek service'ler + fake printer). Test: `LabelPrinter`'ın test-double'ı (interface yok, sealed class — minimal şekilde subclass'lanabilir mi? Hayır, sealed. Workaround: test'te kuyrukta yeterli etiket olduğunda `Print()` çağır, sonra `PrintQueue.Count` ve `_labels.GetUnprintedBySession(...)` ile state'i doğrula. Yazdırma fiziksel olarak gerçekleşir — testte bir mock printer'a gönderilmeli).

**Test architecture decision:** `LabelPrinter` sealed; test edilebilirlik için ya interface ekleyeceğiz ya da Print metodunu test edemeyiz. **Pragmatik:** test'te `LabelPrinter` instance'ı oluştur ama hiç gerçek yazdırma yapma — geçerli printer adı vermezsek `LabelPrinter.Print` exception fırlatır, bu test ortamı için kabul edilemez.

**Çözüm:** `MainShellViewModel`'e ufak bir refactor — `LabelPrinter` yerine `Action<IReadOnlyList<Label>>` printer delegate al. DI'da `sp.GetRequiredService<LabelPrinter>().Print` lambda ile bağla. Test'te capturing lambda ver.

**Alternatif (önerilen):** `ILabelPrinter` interface ekle, `LabelPrinter` implement etsin. Daha temiz, P5 polish'inde de işe yarar (printer mock'ları). Faz scope'una girer, küçük bir refactor.

**Kapsam kararı:** Bu spec için `ILabelPrinter` interface eklemek dahildir — tek dosya değişikliği, test edilebilirliği açar.

### 5.2 Manuel kabul testi

| # | Senaryo |
|---|---|
| 1 | 3 etiket ekle → Ctrl+click ile 2'sini seç → "Seçilenleri Sil (2)" butonu → kuyrukta 1 kalır |
| 2 | 5 etiket ekle → Shift+click ile 3'ünü seç → Del → kuyrukta 2 kalır |
| 3 | 5 etiket ekle → seçim yapmadan "Yazdır" → kuyruk boşalır, hepsi yazdırılır |
| 4 | 5 etiket ekle → 2'sini seç → "Yazdır (2)" → seçilenler yazdırılır, 3'ü kuyrukta kalır |
| 5 | 5 etiket → Ctrl+A → 5 seçili → Yazdır (5) → hepsi yazdırılır |
| 6 | Chat'te bir mesaja tıkla → Enter → kuyruğa eklenir |
| 7 | Aktif kod TextBox'ında Enter bas → kuyruğa ekleme **olmaz** (sadece ChatList focus iken) |
| 8 | Multi-select varken sağ tık "Müşteri Detayı" → primary item'ın detayı açılır |
| 9 | Hepsini Temizle butonu → onay → kuyruk boşalır + SelectedQueueItems boş |

---

## 6. YAGNI (yapılmayanlar)

- Multi-select drag-drop yeniden sıralama (Phase 5 polish)
- "Tümünü seç" butonu (Ctrl+A native + Hepsini Temizle yeterli)
- Multi-delete için onay dialog'u (Hepsini Temizle zaten onay sorar; tekil silme sorgusuz; tutarlılık)
- "Tümünü Yazdır" + "Seçilenleri Yazdır" iki ayrı buton (akıllı mod yeterli)
- Multi-print sırasında progress bar (yazdırma sync, az sayıda etiket)
- Selectionkararsız uyarısı (snapshot.Count == 0 → sessiz no-op zaten doğal)
- ListBox virtualization tweak'leri (default davranış yeterli)

---

## 7. Phase 3b-2 Sonrası Açık Bırakılanlar

- **Phase 3c:** Stream Deck SDK (Phase 3b-1 komut envanteri yeniden kullanılır)
- **Phase 4:** Lisans sistemi
- **Phase 5:** drag-drop reorder, polish
- Multi-profile shortcuts (gelecek)

---

## 8. Kabul Kriterleri

- ✅ Build temiz, 0 warning, 0 error
- ✅ ~117/117 test pass (114 baseline + 3 new)
- ✅ Multi-select Ctrl+click/Shift+click ile çalışıyor
- ✅ Del + "Seçileni Sil" butonu çoklu siliyor
- ✅ Smart Print: seçim yoksa hepsi, seçim varsa sadece seçilenler
- ✅ Enter chat'te seçili mesajı kuyruğa ekliyor (TextBox'larda etkili değil)
- ✅ Ctrl+A ListBox focus iken WPF native ile hepsi seçiyor
- ✅ Phase 3b-1 davranışları (kısayol customization, F1) regrese olmamış
