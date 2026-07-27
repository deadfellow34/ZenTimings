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
            { "Main.CpuDie",            new[] { "CPU Die:",             "CPU Die:" } },
            { "Main.Memory",            new[] { "Memory:",              "Bellek:" } },
            { "Main.DimmPower",         new[] { "DIMM Power:",          "DIMM Gücü:" } },

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
            { "Main.Whea",              new[] { "WHEA:",                "WHEA:" } },
            { "Opt.StartWithWindows",   new[] { "Start with Windows",   "Windows ile başlat" } },

            // --- Options dialog ---
            { "Opt.Title",              new[] { "Options",              "Seçenekler" } },
            { "Opt.SectionGeneral",     new[] { "General",              "Genel" } },
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
            { "Common.Browse",          new[] { "Browse...",            "Gözat..." } },
            { "Common.Apply",           new[] { "Apply",                "Uygula" } },
            { "Common.Close",           new[] { "Close",                "Kapat" } },
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
