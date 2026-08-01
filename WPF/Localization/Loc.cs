using System;
using System.Collections.Generic;
using System.Windows.Markup;

namespace ZenTimings.Localization
{
    public enum AppLanguage
    {
        English = 0,
        Turkish = 1,
    }

    /// <summary>
    /// Minimal, dependency-free string table (no satellite assemblies, no resx churn).
    /// The language is resolved once at startup from <see cref="AppSettings.Language"/>;
    /// changing it needs a restart, like the other "applied on next launch" options.
    /// Technical timing names (tCL, tRP, ...) are deliberately not translated.
    /// </summary>
    public static class Loc
    {
        public static AppLanguage Language { get; set; } = AppLanguage.English;

        // key -> { English, Turkish }
        private static readonly Dictionary<string, string[]> Table =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // --- Main menu ---
            { "Menu.File",              new[] { "_File",                "_Dosya" } },
            { "Menu.ExportHtml",        new[] { "Export as HTML",       "HTML olarak dışa aktar" } },
            { "Menu.ExportJson",        new[] { "Export as JSON",       "JSON olarak dışa aktar" } },
            { "Menu.Exit",              new[] { "E_xit",                "Çı_kış" } },
            { "Menu.Tools",             new[] { "_Tools",               "_Araçlar" } },
            { "Menu.DimmTelemetry",     new[] { "_DIMM Telemetry",      "_DIMM Telemetrisi" } },
            { "Menu.SpdInfo",           new[] { "_SPD Info",            "_SPD Bilgisi" } },
            { "Menu.SystemInfo",        new[] { "System _Info",         "Sistem _Bilgisi" } },
            { "Menu.OcTools",           new[] { "_OC Tools",            "_OC Araçları" } },
            { "Menu.Options",           new[] { "_Options",             "_Seçenekler" } },
            { "Menu.Debug",             new[] { "_Debug",               "_Hata Ayıklama" } },
            { "Menu.StartLogging",      new[] { "Start _CSV Logging",   "CSV Kaydını _Başlat" } },
            { "Menu.StopLogging",       new[] { "Stop _CSV Logging",    "CSV Kaydını _Durdur" } },
            { "Menu.Help",              new[] { "_Help",                "_Yardım" } },
            { "Menu.OcProfiles",        new[] { "_Reference Profiles",  "_Referans Profilleri" } },
            { "Menu.MemoryLatency",     new[] { "Memory _Benchmark",    "Bellek _Testi" } },
            { "Menu.About",             new[] { "_About",               "_Hakkında" } },
            { "Menu.Changelog",         new[] { "Changelog",            "Değişiklik Günlüğü" } },

            // --- Main window ---
            // Short labels: the readouts row drives the window width, and the units in the values
            // already say what each one is.
            { "Main.CpuDie",            new[] { "CPU",                  "CPU" } },
            { "Main.IodHotspot",        new[] { "IOD",                  "IOD" } },
            { "Main.Memory",            new[] { "DIMM",                 "DIMM" } },
            { "Main.DimmPowerTip",      new[] { "Sum of the on-module PMIC power readings",
                                                "Modül üzerindeki PMIC güç okumalarının toplamı" } },

            // --- OC Tools window ---
            { "Oc.Title",               new[] { "OC Tools",             "OC Araçları" } },
            { "Oc.TabRegisters",        new[] { "Register Diff",        "Register Farkı" } },
            { "Oc.TabProfiles",         new[] { "My Profiles",          "Profillerim" } },
            { "Oc.TabSpd",              new[] { "SPD vs Live",          "SPD / Canlı" } },
            { "Oc.CaptureA",            new[] { "Capture A",            "A'yı Yakala" } },
            { "Oc.CaptureB",            new[] { "Capture B",            "B'yi Yakala" } },
            { "Oc.LoadA",               new[] { "Load A…",              "A'yı Yükle…" } },
            { "Oc.LoadB",               new[] { "Load B…",              "B'yi Yükle…" } },
            { "Oc.SaveA",               new[] { "Save A…",              "A'yı Kaydet…" } },
            { "Oc.SaveB",               new[] { "Save B…",              "B'yi Kaydet…" } },
            { "Oc.Compare",             new[] { "Compare",              "Karşılaştır" } },
            { "Oc.ShowAll",             new[] { "Show unchanged",       "Değişmeyenleri göster" } },
            { "Oc.SaveProfile",         new[] { "Save current…",        "Mevcut durumu kaydet…" } },
            { "Oc.DeleteProfile",       new[] { "Delete",               "Sil" } },
            { "Oc.CompareToLive",       new[] { "Compare to live",      "Canlı ile karşılaştır" } },
            { "Oc.CompareTwo",          new[] { "Compare two",          "İkisini karşılaştır" } },
            { "Oc.Refresh",             new[] { "Refresh",              "Yenile" } },
            { "Oc.Ready",               new[] { "Ready.",               "Hazır." } },

            // --- Options ---
            { "Opt.Language",           new[] { "Language",             "Dil" } },
            { "Opt.ScreenshotHotkey",   new[] { "Screenshot hotkey (Ctrl+Alt+S)", "Ekran görüntüsü kısayolu (Ctrl+Alt+S)" } },
            { "Opt.TrayLiveIcon",       new[] { "Live value on tray icon",        "Tepsi ikonunda canlı değer" } },
            { "Opt.TrayIconColor",      new[] { "Tray icon colour",     "Tepsi ikonu rengi" } },
            { "Tray.CopiedToClipboard", new[] { "Copied to clipboard",  "Panoya kopyalandı" } },
            { "Main.Whea",              new[] { "WHEA",                 "WHEA" } },

            // --- All DIMMs window ---
            { "AllDimms.Title",         new[] { "All DIMMs",            "Tüm DIMM'ler" } },
            { "AllDimms.Button",        new[] { "All",                  "Tümü" } },
            { "AllDimms.Tip",           new[] { "All channels side by side",
                                                "Tüm kanallar yan yana" } },
            { "AllDimms.Busy",          new[] { "Not while a benchmark is running",
                                                "Benchmark çalışırken kullanılamaz" } },
            { "Opt.StartWithWindows",   new[] { "Start with Windows",   "Windows ile başlat" } },

            // --- Options dialog ---
            { "Opt.Title",              new[] { "Options",              "Seçenekler" } },
            { "Opt.SectionGeneral",     new[] { "General",              "Genel" } },
            { "Opt.SectionReadouts",    new[] { "Readouts",             "Göstergeler" } },
            { "Opt.ShowCpuTemp",        new[] { "CPU Die temperature",  "CPU Die sıcaklığı" } },
            { "Opt.ShowIodTemp",        new[] { "IOD temperature",      "IOD sıcaklığı" } },
            { "Opt.ShowMemTemp",        new[] { "Memory temperature",   "Bellek sıcaklığı" } },
            { "Opt.ShowDimmPower",      new[] { "DIMM power",           "DIMM gücü" } },
            { "Opt.ShowWhea",           new[] { "WHEA error count",     "WHEA hata sayısı" } },
            { "Opt.SectionWindow",      new[] { "Window",               "Pencere" } },
            { "Opt.SectionScreenshot",  new[] { "Screenshot",           "Ekran Görüntüsü" } },
            { "Opt.AdvancedMode",       new[] { "Advanced mode",        "Gelişmiş mod" } },
            { "Opt.AutoRefresh",        new[] { "Auto refresh",         "Otomatik yenileme" } },
            { "Opt.SingleInstance",     new[] { "Single instance",      "Tek örnek" } },
            { "Opt.MinimizeToTray",     new[] { "Minimize to tray",     "Sistem tepsisine küçült" } },
            { "Opt.Theme",              new[] { "Theme",                "Tema" } },
            { "Opt.ImpedanceSource",    new[] { "Impedance table source", "Empedans tablosu kaynağı" } },
            { "Opt.SavePosition",       new[] { "Save position on app close", "Kapanışta konumu kaydet" } },
            { "Opt.CornerRadius",       new[] { "Corner radius (Win 11)", "Köşe yuvarlaklığı (Win 11)" } },
            { "Opt.ScreenshotMode",     new[] { "Mode",                 "Mod" } },
            { "Opt.ScreenshotLocation", new[] { "Location",             "Konum" } },
            { "Opt.SettingsSaved",      new[] { "Settings Saved.",      "Ayarlar kaydedildi." } },
            { "Opt.RestartNeeded",      new[] { "Some settings will be applied on next launch.",
                                                "Bazı ayarlar bir sonraki açılışta uygulanacak." } },
            { "Common.Browse",          new[] { "Browse...",            "Gözat..." } },
            { "Common.Apply",           new[] { "Apply",                "Uygula" } },
            { "Common.Close",           new[] { "Close",                "Kapat" } },

            // --- Memory benchmark ---
            // The exported HTML stays English on purpose: it is meant to be shared and read by
            // people who do not run this copy of the app.
            { "Bench.Title",            new[] { "Memory Benchmark",     "Bellek Testi" } },
            { "Bench.Result",           new[] { "Result",               "Sonuç" } },
            { "Bench.NotMeasured",      new[] { "Not measured yet",     "Henüz ölçülmedi" } },
            { "Bench.Read",             new[] { "Read",                 "Okuma" } },
            { "Bench.Write",            new[] { "Write",                "Yazma" } },
            { "Bench.Copy",             new[] { "Copy",                 "Kopyalama" } },
            { "Bench.Random",           new[] { "Random",               "Rastgele" } },
            { "Bench.RandomTip",        new[] { "Independent random line fetches, many in flight - governed by the bank-cycle timings (tRAS, tRC, tRRD, tFAW), not by one access's latency.",
                                                "Aynı anda çok sayıda bağımsız rastgele satır okuması - tek bir erişimin gecikmesi değil, bank döngüsü zamanlamaları (tRAS, tRC, tRRD, tFAW) belirler." } },
            { "Bench.Ceiling",          new[] { "Theoretical ceiling",  "Teorik tavan" } },
            { "Bench.Context",          new[] { "Configuration at the time of the run",
                                                "Ölçüm anındaki yapılandırma" } },
            { "Bench.Test",             new[] { "Test",                 "Test" } },
            { "Bench.Buffer",           new[] { "Buffer",               "Tampon" } },
            { "Bench.Run",              new[] { "Run",                  "Çalıştır" } },
            { "Bench.History",          new[] { "History",              "Geçmiş" } },
            { "Bench.ExportHtml",       new[] { "Export HTML",          "HTML aktar" } },
            { "Bench.Pin",              new[] { "Pin as baseline",      "Referans olarak sabitle" } },
            { "Bench.Hint",             new[] { "App polling pauses during a run. Large pages are used automatically when the account holds the 'Lock pages in memory' right; the result line says which mode measured. Compare runs on this machine, not against other tools.",
                                                "Ölçüm sırasında uygulamanın yoklaması durur. Hesapta 'Sayfaları bellekte kilitle' yetkisi varsa büyük sayfalar kendiliğinden kullanılır; hangi modda ölçüldüğü sonuç satırında yazar. Sonuçları başka araçlarla değil, bu makinedeki diğer ölçümlerle karşılaştırın." } },

            { "Bench.Busy",             new[] { "A benchmark is still finishing - try again in a moment.",
                                                "Önceki ölçüm hâlâ tamamlanıyor - birazdan tekrar deneyin." } },
            { "Bench.MeasuringLatency", new[] { "Measuring latency",    "Gecikme ölçülüyor" } },
            { "Bench.MeasuringBandwidth", new[] { "Measuring bandwidth", "Bant genişliği ölçülüyor" } },
            { "Bench.Failed",           new[] { "Failed",               "Başarısız" } },
            { "Bench.FailedWith",       new[] { "Failed: ",             "Başarısız: " } },
            { "Bench.FailedToStart",    new[] { "Failed to start.",     "Başlatılamadı." } },
            { "Bench.FailedToStartWith", new[] { "Failed to start: ",   "Başlatılamadı: " } },

            // "min of {0} slices, spread {1} ns, {2} MB, {3} pages"
            { "Bench.Detail",           new[] { "min of {0} slices, spread {1} ns, {2} MB, {3} pages",
                                                "{0} dilimin en düşüğü, yayılım {1} ns, {2} MB, {3} sayfa" } },
            { "Bench.PagesLarge",       new[] { "large",                "büyük" } },
            { "Bench.Pages4K",          new[] { "4K",                   "4K" } },
            { "Bench.CacheBound",       new[] { "cache-bound",          "önbellek sınırlı" } },
            { "Bench.NoisyLong",        new[] { "noisy run, close other load and repeat",
                                                "gürültülü ölçüm, diğer yükleri kapatıp tekrarlayın" } },
            { "Bench.Noisy",            new[] { "noisy",                "gürültülü" } },
            { "Bench.NotComparable",    new[] { "not comparable",       "karşılaştırılamaz" } },

            { "Bench.DramBus",          new[] { "DRAM bus",             "DRAM veri yolu" } },
            { "Bench.FabricRead",       new[] { "Fabric read",          "Fabric okuma" } },
            { "Bench.FabricWrite",      new[] { "Fabric write",         "Fabric yazma" } },
            { "Bench.Unavailable",      new[] { "Unavailable",          "Kullanılamıyor" } },
            { "Bench.NoClocks",         new[] { "clocks not reported",  "saatler bildirilmiyor" } },

            { "Bench.ErrRange",         new[] { "Buffer size out of range.",
                                                "Tampon boyutu aralık dışında." } },
            { "Bench.ErrMemory",        new[] { "Not enough free memory for that buffer size.",
                                                "Bu tampon boyutu için yeterli boş bellek yok." } },
            { "Bench.ErrCancelled",     new[] { "Cancelled.",           "İptal edildi." } },

            { "Bench.ExportOk",         new[] { "HTML file exported successfully!",
                                                "HTML dosyası başarıyla dışa aktarıldı!" } },
            { "Bench.ExportFailed",     new[] { "Could not write the file: ",
                                                "Dosya yazılamadı: " } },
        };

        public static string T(string key)
        {
            string[] entry;
            if (key != null && Table.TryGetValue(key, out entry))
            {
                int index = (int)Language;
                if (index >= 0 && index < entry.Length && !string.IsNullOrEmpty(entry[index]))
                    return entry[index];
                return entry[0];
            }

            // Unknown keys fall through as-is so a missing entry is visible but harmless.
            return key ?? string.Empty;
        }
    }

    /// <summary>XAML helper: <c>Header="{loc:Loc Menu.File}"</c>.</summary>
    [MarkupExtensionReturnType(typeof(string))]
    public class LocExtension : MarkupExtension
    {
        public LocExtension() { }

        public LocExtension(string key)
        {
            Key = key;
        }

        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return Loc.T(Key);
        }
    }
}
