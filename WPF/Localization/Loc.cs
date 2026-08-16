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
            // Mnemonics inside the Tools submenu have to be distinct or Alt+key cycles the
            // highlight instead of invoking: O clashed with OC Tools, D with DIMM Telemetry,
            // and in Turkish B with Sistem Bilgisi.
            { "Menu.Options",           new[] { "O_ptions",             "Seçe_nekler" } },
            { "Menu.Debug",             new[] { "D_ebug",               "_Hata Ayıklama" } },
            { "Menu.StartLogging",      new[] { "Start _CSV Logging",   "CSV _Kaydını Başlat" } },
            { "Menu.StopLogging",       new[] { "Stop _CSV Logging",    "CSV _Kaydını Durdur" } },
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
            { "Bench.RandomTip",        new[] { "Independent random line fetches, many in flight - governed by the bank-cycle timings (tRAS, tRC, tRRD, tFAW), not by one access's latency.",
                                                "Aynı anda çok sayıda bağımsız rastgele satır okuması - tek bir erişimin gecikmesi değil, bank döngüsü zamanlamaları (tRAS, tRC, tRRD, tFAW) belirler." } },
            { "Bench.Ceiling",          new[] { "Theoretical ceiling",  "Teorik tavan" } },
            { "Bench.Context",          new[] { "Configuration at the time of the run",
                                                "Ölçüm anındaki yapılandırma" } },
            { "Bench.Test",             new[] { "Test",                 "Test" } },
            // Not "Test": the box sits on the Test tab already, a header repeating the tab name
            // says nothing. Not "Run" either - the button inside carries that word.
            { "Bench.RunBox",           new[] { "Measurement",          "Ölçüm" } },
            { "Bench.Buffer",           new[] { "Buffer",               "Tampon" } },
            { "Bench.Run",              new[] { "Run",                  "Çalıştır" } },
            { "Bench.Running",          new[] { "Running benchmark…",   "Benchmark çalışıyor…" } },
            { "Bench.Rerun",            new[] { "Re-run",               "Tekrar çalıştır" } },
            { "Bench.History",          new[] { "History",              "Geçmiş" } },
            { "Bench.ExportHtml",       new[] { "Export HTML",          "HTML aktar" } },
            { "Bench.Pin",              new[] { "Pin as baseline",      "Referans olarak sabitle" } },
            { "Bench.Hint",             new[] { "Large pages are used automatically when the account holds the 'Lock pages in memory' right; the result line says which mode measured. Compare runs on this machine, not against other tools.",
                                                "Hesapta 'Sayfaları bellekte kilitle' yetkisi varsa büyük sayfalar kendiliğinden kullanılır; hangi modda ölçüldüğü sonuç satırında yazar. Sonuçları başka araçlarla değil, bu makinedeki diğer ölçümlerle karşılaştırın." } },

            { "Bench.Busy",             new[] { "A benchmark is still finishing - try again in a moment.",
                                                "Önceki ölçüm hâlâ tamamlanıyor - birazdan tekrar deneyin." } },
            { "Bench.MeasuringLatency", new[] { "Measuring latency",    "Gecikme ölçülüyor" } },
            { "Bench.MeasuringBandwidth", new[] { "Measuring bandwidth", "Bant genişliği ölçülüyor" } },
            { "Bench.MeasuringCache",   new[] { "Measuring cache levels", "Önbellek seviyeleri ölçülüyor" } },
            { "Bench.Failed",           new[] { "Failed",               "Başarısız" } },
            { "Bench.FailedWith",       new[] { "Failed: ",             "Başarısız: " } },
            { "Bench.FailedToStart",    new[] { "Failed to start.",     "Başlatılamadı." } },
            { "Bench.FailedToStartWith", new[] { "Failed to start: ",   "Başlatılamadı: " } },

            // No buffer size here: the grid's DRAM row carries it in the size column now, and the
            // same figure twice on one tab reads as two different things.
            { "Bench.Detail",           new[] { "{0} pages",
                                                "{0} sayfa" } },
            { "Bench.PagesLarge",       new[] { "large",                "büyük" } },
            { "Bench.Pages4K",          new[] { "4K",                   "4K" } },
            { "Bench.LpNoRight",        new[] { "no 'Lock pages in memory' right",
                                                "'Sayfaları bellekte kilitle' yetkisi yok" } },
            { "Bench.LpFragmented",     new[] { "no contiguous block free",
                                                "bitişik blok kalmamış" } },
            { "Bench.LpUnsupported",    new[] { "large pages unsupported",
                                                "büyük sayfa desteklenmiyor" } },
            { "Bench.LpOther",          new[] { "error {0}",            "hata {0}" } },
            // The mode either way, not the fallback alone: the line is printed wherever the
            // bandwidth pass's page mode is not the walk's, the walk having produced none included.
            { "Bench.LpBandwidth",      new[] { "bandwidth on {0} pages",
                                                "bant genişliği {0} sayfada" } },
            { "Bench.ErrMemFault",      new[] { "memory error caught - the RAM returned corrupted data, the overclock is not stable",
                                                "bellek hatası yakalandı - RAM bozuk veri döndürdü, hız aşırtma stabil değil" } },
            { "Bench.SlowStores",       new[] { "write/copy on ordinary stores",
                                                "write/copy normal store ile" } },
            { "Bench.AboveBus",         new[] { "a figure came out above what the DRAM bus can carry and was withheld",
                                                "bir değer DRAM veri yolunun taşıyabileceğinin üzerinde çıktı ve gösterilmedi" } },
            { "Bench.LpHelpButton",     new[] { "What to do?",           "Ne yapmalı?" } },
            { "Bench.LpHelpTitle",      new[] { "Large pages",           "Büyük sayfalar" } },
            { "Bench.LpHelpNoRight",    new[] { "Large pages (2 MB) take the TLB cost out of the measurement and pin the physical layout - that is what makes runs repeatable. They need the 'Lock pages in memory' right, which is off by default even for administrators.\n\n1. Win+R, type secpol.msc, Enter\n2. Local Policies > User Rights Assignment\n3. Open 'Lock pages in memory' and add your Windows user\n4. Sign out and back in (a reboot also works)\n\nWindows Home has no secpol.msc; results on 4K pages remain valid, just a little less repeatable.",
                                                "Büyük sayfalar (2 MB) TLB maliyetini ölçümden çıkarır ve fiziksel yerleşimi sabitler - koşudan koşuya tekrarlanabilirliğin kaynağı budur. Bunun için yöneticilerde bile varsayılan olarak kapalı olan 'Sayfaları bellekte kilitle' yetkisi gerekir.\n\n1. Win+R, secpol.msc yazıp Enter\n2. Yerel İlkeler > Kullanıcı Hakları Ataması\n3. 'Sayfaları bellekte kilitle'yi açıp Windows kullanıcınızı ekleyin\n4. Oturumu kapatıp yeniden açın (yeniden başlatma da olur)\n\nWindows Home'da secpol.msc yoktur; 4K sayfadaki sonuçlar yine geçerlidir, yalnızca biraz daha az tekrarlanabilir." } },
            { "Bench.LpHelpFragmented", new[] { "The right is granted, but no contiguous 2 MB physical block was free - large pages cannot be assembled from scattered 4K frames, and a machine that has been up for a while fragments naturally.\n\nReboot and run the benchmark early, before large applications carve the memory up. Nothing is misconfigured.",
                                                "Yetki verilmiş, ancak bitişik 2 MB'lik fiziksel blok kalmamış - büyük sayfalar dağınık 4K çerçevelerden birleştirilemez ve uzun süredir açık bir makine doğal olarak parçalanır.\n\nYeniden başlatıp benchmark'ı erken, büyük uygulamalar belleği bölmeden çalıştırın. Yanlış ayarlanmış bir şey yok." } },
            // No "remove it in secpol.msc": this box is shown to Windows Home users too, and the
            // half above it has just told them their edition does not have secpol.msc.
            { "Bench.LpGrantAsk",       new[] { "Grant it now?\n\nThe right goes to your Windows account, so every program you run may use it. This is the same change secpol.msc makes, and the only way to make it on Windows Home, where secpol.msc does not exist.",
                                                "Şimdi verilsin mi?\n\nYetki Windows hesabınıza verilir, yani çalıştırdığınız her program kullanabilir. secpol.msc'nin yaptığı değişikliğin aynısı; secpol.msc'nin bulunmadığı Windows Home'da bunu yapmanın tek yolu da budur." } },
            { "Bench.LpGrantOk",        new[] { "Granted. Sign out and back in (or reboot) for it to take effect - the access token is built at sign-in.",
                                                "Verildi. Etkili olması için oturumu kapatıp açın (ya da yeniden başlatın) - erişim jetonu oturum açılışında kurulur." } },
            { "Bench.LpGrantFailed",    new[] { "Could not grant the right: ",
                                                "Yetki verilemedi: " } },
            { "Bench.CacheBound",       new[] { "cache-bound",          "önbellek sınırlı" } },
            { "Bench.RamHungry",        new[] { "needs free RAM",       "boş RAM ister" } },
            { "Bench.LargeBuffer",      new[] { "At 2048 MB the bandwidth pass asks for two buffers of that size, so about 4.5 GB has to be free or the run refuses to start, and 4 GB of large pages falls back to 4K far more often on a machine that has been up a while. Worth it only where 1024 MB is not far past the L3 - a 96 MB part - and comparable only with other 2048 MB runs.",
                                                "2048 MB'ta bant genişliği turu bu boyutta iki tampon ister, yani ~4,5 GB boş bellek olmalı, yoksa koşu hiç başlamaz; ayrıca 4 GB'lık büyük sayfa, bir süredir açık makinelerde çok daha sık 4K'ya düşer. Yalnız 1024 MB'ın L3'ü fazla aşmadığı parçalarda (96 MB) anlamlı ve sadece diğer 2048 MB koşularıyla karşılaştırılır." } },
            { "Bench.SmallBuffer",      new[] { "At 256 MB the cache supplies part of the stream and it is counted as bandwidth, so read and copy can come out above the ceilings above. For a comparable figure use 1024 MB.",
                                                "256 MB'ta akışın bir kısmını önbellek besler ve bu bant genişliği olarak sayılır; read ve copy yukarıdaki tavanların üstünde çıkabilir. Karşılaştırılabilir sonuç için 1024 MB kullanın." } },
            { "Bench.NoisyLong",        new[] { "noisy run, close other load and repeat",
                                                "gürültülü ölçüm, diğer yükleri kapatıp tekrarlayın" } },
            { "Bench.Noisy",            new[] { "noisy",                "gürültülü" } },
            { "Bench.NotComparable",    new[] { "not comparable",       "karşılaştırılamaz" } },

            { "Bench.Level",            new[] { "",                     "" } },
            { "Bench.ColRead",          new[] { "read",                 "okuma" } },
            { "Bench.ColWrite",         new[] { "write",                "yazma" } },
            { "Bench.ColCopy",          new[] { "copy",                 "kopyalama" } },
            { "Bench.ColSize",          new[] { "size",                 "boyut" } },
            { "Bench.ResultTitle",      new[] { "Cache & Memory Benchmark", "Önbellek ve Bellek Testi" } },
            { "Bench.Close",            new[] { "Close",                "Kapat" } },
            { "Bench.System",           new[] { "System",               "Sistem" } },
            { "Bench.InfoCpu",          new[] { "CPU",                  "İşlemci" } },
            { "Bench.InfoBoard",        new[] { "Motherboard",          "Anakart" } },
            { "Bench.InfoBios",         new[] { "BIOS",                 "BIOS" } },
            { "Bench.InfoMemory",       new[] { "Memory",               "Bellek" } },
            { "Bench.SecGrid",          new[] { "Cache & Memory",       "Önbellek & Bellek" } },
            { "Bench.SecAll",           new[] { "All cores",            "Tüm çekirdekler" } },
            { "Bench.SecOne",           new[] { "One core",             "Tek çekirdek" } },
            { "Bench.FootRandom",       new[] { "random {0} GB/s",      "rastgele {0} GB/s" } },
            { "Bench.DeltaLegend",      new[] { "deltas against the baseline: {0}",
                                                "farklar » referansa göre: {0}" } },
            { "Bench.NoBandwidthDelta", new[] { "no DRAM bandwidth delta - the baseline's bandwidth pass ran on another page mode, or recorded none",
                                                "DRAM bant genişliği farkı yok - referansın bant genişliği ölçümü başka sayfa modunda yapılmış ya da modu kaydedilmemiş" } },
            { "Bench.NoAllCoreDelta",   new[] { "no all-core deltas on the cache rows - the baseline's all-core figures came from a different set of cores",
                                                "önbellek satırlarında tüm-çekirdek farkı yok - referansın tüm-çekirdek değerleri farklı bir çekirdek kümesinden alınmış" } },
            { "Bench.NoRungDelta",      new[] { "some cache rows have no delta at all - the baseline holds no cache of that level and size",
                                                "bazı önbellek satırlarında hiç fark yok - referansta o seviye ve boyutta bir önbellek kayıtlı değil" } },
            { "Bench.ColLat",           new[] { "latency",              "gecikme" } },
            { "Bench.StarMeaning",      new[] { "* 4K pages or fewer cores - hover the row",
                                                "* 4K sayfa ya da daha az çekirdek - satırın üzerine gelin" } },
            { "Bench.AllReduced",       new[] { "all-core figures from {0} of {1} cores - split over every core, each core's slice of this cache would fit inside its own L2 and the row would measure L2",
                                                "tüm-çekirdek değerleri {1} çekirdeğin {0} tanesinden - tüm çekirdeklere bölününce her çekirdeğin dilimi kendi L2'sine sığar ve satır L2 ölçerdi" } },
            { "Bench.AllL2Bound",       new[] { "all-core figures withheld - even split two ways, each core's slice of this cache sits inside its own L2; the one-core columns carry the honest figure",
                                                "tüm-çekirdek değerleri gösterilmedi - iki çekirdeğe bölününce bile her dilim kendi L2'sine sığıyor; dürüst değer tek çekirdek kolonlarında" } },
            { "Bench.AllOneCache",      new[] { "this cache is shared by {0} of the package's {1} cores and the row is measured inside it - a core outside would read this cache across the fabric and the row would measure the trip",
                                                "bu önbelleği paketteki {1} çekirdeğin {0} tanesi paylaşır ve satır onun içinden ölçülür - dışarıdaki bir çekirdek bu önbelleğe fabric üzerinden erişirdi ve satır o geçişi ölçerdi" } },
            { "Bench.AllCacheRows",     new[] { "one row per different L3 in the package - identical caches share a row, so this row stands for every one its size",
                                                "paketteki her farklı L3 için bir satır - aynı olan önbellekler tek satırı paylaşır, yani bu satır kendi boyutundaki tüm önbellekleri temsil eder" } },
            { "Bench.CrewOf",           new[] { "{0} of {1}",            "{0} / {1}" } },
            { "Bench.LevelAvg",         new[] { "L3 avg",               "L3 ort" } },
            { "Bench.AvgTip",           new[] { "mean of the die rows above - what a thread on a random die can expect, not a physical cache",
                                                "üstteki die satırlarının ortalaması - rastgele bir die'a düşen iş parçacığının beklediği değer, fiziksel bir önbellek değil" } },
            { "Bench.Ladder4K",         new[] { "4K pages - the L3 latency reads a little high",
                                                "4K sayfa - L3 gecikmesi biraz yüksek okunur" } },
            // The line above is a TLB cost the walk pays over 512 pages of fallback, so it belongs
            // to the L3 row and to a row that walked on 4K. The other two name the mode only.
            { "Bench.Ladder4KRow",      new[] { "figures on 4K pages",
                                                "değerler 4K sayfada" } },
            { "Bench.Ladder4KAll",      new[] { "an all-core buffer did not get large pages - the latency and the one-core columns did",
                                                "tüm-çekirdek tamponlarından biri büyük sayfa alamadı - gecikme ve tek çekirdek kolonları aldı" } },
            { "Bench.DramBus",          new[] { "DRAM bus",             "DRAM veri yolu" } },
            // "fabric", not "CCD link": the write ceiling is non-zero on monolithic parts too,
            // and those are exactly the ones with no CCD to name.
            { "Bench.FabricRead",       new[] { "Fabric read",          "Fabric okuma" } },
            { "Bench.FabricWrite",      new[] { "Fabric write",         "Fabric yazma" } },
            { "Bench.Unavailable",      new[] { "Unavailable",          "Kullanılamıyor" } },
            // Not "clocks not reported": the rows are gated on the ceilings now, so an empty
            // panel no longer implies the clocks were missing.
            { "Bench.NoClocks",         new[] { "not enough information", "yeterli bilgi yok" } },

            { "Bench.ErrRange",         new[] { "Buffer size out of range.",
                                                "Tampon boyutu aralık dışında." } },
            { "Bench.ErrMemory",        new[] { "Not enough free memory - the run needs about 2.5 GB free.",
                                                "Yeterli boş bellek yok - test için yaklaşık 2,5 GB boş bellek gerekiyor." } },
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
