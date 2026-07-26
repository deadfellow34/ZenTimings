using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZenTimings.Localization;
using ZenTimings.ViewModels;

namespace ZenTimings
{
    /// <summary>
    /// Attaches explanatory tooltips to the timing labels of whichever timings panel is loaded.
    /// Done at runtime by walking the panel's visual tree so all six panel variants are covered
    /// without duplicating a ToolTip attribute ~45 times per panel.
    ///
    /// Each tooltip is built lazily when it opens, so it can show live data:
    ///   description  +  value in ns at the current frequency  +  the JEDEC-rated minimum from SPD.
    /// </summary>
    public static class TimingTooltips
    {
        private class TimingInfo
        {
            public string Property;   // property on BaseDramTimings, null when not a plain nCK value
            public string RatedName;  // key into SpdRatedTimings, null when SPD has no rating
            public string En;
            public string Tr;
        }

        private static readonly Dictionary<string, TimingInfo> Map =
            new Dictionary<string, TimingInfo>(StringComparer.OrdinalIgnoreCase)
        {
            { "tCL", new TimingInfo { Property = "CL", RatedName = "tCL",
                En = "CAS Latency - clocks between a READ command and the first data word.",
                Tr = "CAS gecikmesi - READ komutu ile ilk verinin gelmesi arasındaki çevrim sayısı." } },
            { "tRCDRD", new TimingInfo { Property = "RCDRD", RatedName = "tRCD",
                En = "RAS-to-CAS delay for reads - row activate to READ.",
                Tr = "Okuma için RAS-CAS gecikmesi - satır aktivasyonundan READ'e." } },
            { "tRCDWR", new TimingInfo { Property = "RCDWR", RatedName = "tRCD",
                En = "RAS-to-CAS delay for writes - row activate to WRITE.",
                Tr = "Yazma için RAS-CAS gecikmesi - satır aktivasyonundan WRITE'a." } },
            { "tRP", new TimingInfo { Property = "RP", RatedName = "tRP",
                En = "Row precharge time - closing a row before another can be opened.",
                Tr = "Satır ön-şarj süresi - yeni satır açılmadan önce mevcut satırın kapanma süresi." } },
            { "tRAS", new TimingInfo { Property = "RAS", RatedName = "tRAS",
                En = "Minimum time a row must stay open before it may be precharged.",
                Tr = "Bir satırın kapatılabilmesi için açık kalması gereken en kısa süre." } },
            { "tRC", new TimingInfo { Property = "RC", RatedName = "tRC",
                En = "Row cycle time - activate to activate on the same bank. Should be >= tRAS + tRP.",
                Tr = "Satır çevrim süresi - aynı bank'ta aktivasyondan aktivasyona. tRAS + tRP'den küçük olmamalı." } },
            { "tRRDS", new TimingInfo { Property = "RRDS", RatedName = null,
                En = "Row-to-row activate delay, different bank group (Short).",
                Tr = "Farklı bank grubunda satırdan satıra aktivasyon gecikmesi (Short)." } },
            { "tRRDL", new TimingInfo { Property = "RRDL", RatedName = "tRRDL",
                En = "Row-to-row activate delay, same bank group (Long).",
                Tr = "Aynı bank grubunda satırdan satıra aktivasyon gecikmesi (Long)." } },
            { "tFAW", new TimingInfo { Property = "FAW", RatedName = "tFAW",
                En = "Four activate window - at most four row activates may occur inside this window.",
                Tr = "Dört aktivasyon penceresi - bu pencerede en fazla dört satır aktivasyonu yapılabilir." } },
            { "tWTRS", new TimingInfo { Property = "WTRS", RatedName = "tWTRS",
                En = "Write-to-read turnaround, different bank group (Short).",
                Tr = "Farklı bank grubunda yazma-okuma dönüş süresi (Short)." } },
            { "tWTRL", new TimingInfo { Property = "WTRL", RatedName = "tWTRL",
                En = "Write-to-read turnaround, same bank group (Long).",
                Tr = "Aynı bank grubunda yazma-okuma dönüş süresi (Long)." } },
            { "tWR", new TimingInfo { Property = "WR", RatedName = "tWR",
                En = "Write recovery - last write burst to precharge on the same bank.",
                Tr = "Yazma toparlanma süresi - son yazmadan aynı bank'ın ön-şarjına." } },
            { "tRFC", new TimingInfo { Property = "RFC", RatedName = "tRFC",
                En = "Refresh cycle time (all banks). The single biggest latency knob on DDR5.",
                Tr = "Yenileme çevrim süresi (tüm bank'lar). DDR5'te gecikmeyi en çok etkileyen ayar." } },
            { "tRFC2", new TimingInfo { Property = "RFC2", RatedName = "tRFC2",
                En = "Refresh cycle time in fine-granularity (2x) refresh mode.",
                Tr = "İnce taneli (2x) yenileme modunda yenileme çevrim süresi." } },
            { "tRFCsb", new TimingInfo { Property = "RFCsb", RatedName = "tRFCsb",
                En = "Same-bank refresh cycle time (DDR5 per-bank refresh).",
                Tr = "Aynı-bank yenileme çevrim süresi (DDR5 bank başına yenileme)." } },
            { "tRFC (ns)", new TimingInfo { Property = null, RatedName = "tRFC",
                En = "Refresh cycle time expressed in nanoseconds - comparable across frequencies.",
                Tr = "Yenileme çevrim süresinin nanosaniye karşılığı - frekanslar arası karşılaştırılabilir." } },
            { "tMOD", new TimingInfo { Property = "MOD", RatedName = null,
                En = "Mode register set command update delay.",
                Tr = "Mode register yazma komutu sonrası bekleme süresi." } },
            { "tMODPDA", new TimingInfo { Property = "MODPDA", RatedName = null,
                En = "Mode register update delay in per-DRAM addressability mode.",
                Tr = "Per-DRAM adresleme modunda mode register güncelleme gecikmesi." } },
            { "tMRD", new TimingInfo { Property = "MRD", RatedName = null,
                En = "Mode register set command cycle time.",
                Tr = "Mode register yazma komutu çevrim süresi." } },
            { "tMRDPDA", new TimingInfo { Property = "MRDPDA", RatedName = null,
                En = "Mode register set cycle time in per-DRAM addressability mode.",
                Tr = "Per-DRAM adresleme modunda mode register yazma çevrim süresi." } },
            { "tPHYWRD", new TimingInfo { Property = "PHYWRD", RatedName = null,
                En = "PHY write data delay (controller side, not a DRAM parameter).",
                Tr = "PHY yazma verisi gecikmesi (denetleyici tarafı, DRAM parametresi değil)." } },
            { "tPHYWRL", new TimingInfo { Property = "PHYWRL", RatedName = null,
                En = "PHY write latency (controller side).",
                Tr = "PHY yazma gecikmesi (denetleyici tarafı)." } },
            { "tPHYRDL", new TimingInfo { Property = "PHYRDL", RatedName = null,
                En = "PHY read latency (controller side).",
                Tr = "PHY okuma gecikmesi (denetleyici tarafı)." } },
            { "tRDPRE", new TimingInfo { Property = "RDPRE", RatedName = "tRTP",
                En = "Read to precharge (tRTP) - last read burst to precharge on the same bank.",
                Tr = "Okumadan ön-şarja (tRTP) - son okumadan aynı bank'ın ön-şarjına." } },
            { "tWRPRE", new TimingInfo { Property = "WRPRE", RatedName = null,
                En = "Write to precharge - derived from tCWL + burst + tWR.",
                Tr = "Yazmadan ön-şarja - tCWL + burst + tWR'den türetilir." } },
            { "tRTP", new TimingInfo { Property = "RTP", RatedName = "tRTP",
                En = "Read to precharge - last read burst to precharge on the same bank.",
                Tr = "Okumadan ön-şarja - son okumadan aynı bank'ın ön-şarjına." } },
            { "tCWL", new TimingInfo { Property = "CWL", RatedName = null,
                En = "CAS write latency - clocks between a WRITE command and the first data word.",
                Tr = "CAS yazma gecikmesi - WRITE komutu ile ilk verinin gönderilmesi arası." } },
            { "tRDWR", new TimingInfo { Property = "RDWR", RatedName = null,
                En = "Read-to-write bus turnaround.",
                Tr = "Okumadan yazmaya veri yolu dönüş süresi." } },
            { "tWRRD", new TimingInfo { Property = "WRRD", RatedName = null,
                En = "Write-to-read bus turnaround.",
                Tr = "Yazmadan okumaya veri yolu dönüş süresi." } },
            { "tRDRDSCL", new TimingInfo { Property = "RDRDSCL", RatedName = "tCCD_L",
                En = "Read-to-read, same bank group (CAS-to-CAS Long) - the applied read-side tCCD_L.",
                Tr = "Aynı bank grubunda okumadan okumaya (CAS-to-CAS Long) - uygulanan okuma tarafı tCCD_L." } },
            { "tWRWRSCL", new TimingInfo { Property = "WRWRSCL", RatedName = "tCCD_L_WR",
                En = "Write-to-write, same bank group (CAS-to-CAS Long) - the applied write-side tCCD_L.",
                Tr = "Aynı bank grubunda yazmadan yazmaya (CAS-to-CAS Long) - uygulanan yazma tarafı tCCD_L." } },
            { "tRDRDSC", new TimingInfo { Property = "RDRDSC", RatedName = null,
                En = "Read-to-read, same chip select (rank), different bank group.",
                Tr = "Aynı chip select (rank), farklı bank grubunda okumadan okumaya." } },
            { "tRDRDSD", new TimingInfo { Property = "RDRDSD", RatedName = null,
                En = "Read-to-read, same DIMM, different rank.",
                Tr = "Aynı DIMM, farklı rank'ta okumadan okumaya." } },
            { "tRDRDDD", new TimingInfo { Property = "RDRDDD", RatedName = null,
                En = "Read-to-read, different DIMM.",
                Tr = "Farklı DIMM'ler arasında okumadan okumaya." } },
            { "tWRWRSC", new TimingInfo { Property = "WRWRSC", RatedName = null,
                En = "Write-to-write, same chip select (rank), different bank group.",
                Tr = "Aynı chip select (rank), farklı bank grubunda yazmadan yazmaya." } },
            { "tWRWRSD", new TimingInfo { Property = "WRWRSD", RatedName = null,
                En = "Write-to-write, same DIMM, different rank.",
                Tr = "Aynı DIMM, farklı rank'ta yazmadan yazmaya." } },
            { "tWRWRDD", new TimingInfo { Property = "WRWRDD", RatedName = null,
                En = "Write-to-write, different DIMM.",
                Tr = "Farklı DIMM'ler arasında yazmadan yazmaya." } },
            { "tCKE", new TimingInfo { Property = "CKE", RatedName = null,
                En = "Clock enable pulse width - minimum time CKE must stay in a state.",
                Tr = "CKE darbe genişliği - CKE'nin bir durumda kalması gereken en kısa süre." } },
            { "tREFI", new TimingInfo { Property = "REFI", RatedName = null,
                En = "Average refresh interval. Higher = fewer refreshes = more bandwidth, less margin.",
                Tr = "Ortalama yenileme aralığı. Yüksek = daha az yenileme = daha çok bant genişliği, daha az pay." } },
            { "tSTAG", new TimingInfo { Property = "STAG", RatedName = null,
                En = "Refresh stagger - spreads per-rank refreshes to flatten current spikes.",
                Tr = "Yenileme kaydırma - rank başına yenilemeleri yayarak akım sıçramalarını azaltır." } },
            { "tXP", new TimingInfo { Property = "XP", RatedName = null,
                En = "Exit power-down to a valid command.",
                Tr = "Güç tasarrufu modundan çıkıp geçerli komut verebilme süresi." } },
            { "tCCD_L", new TimingInfo { Property = null, RatedName = "tCCD_L",
                En = "CAS-to-CAS delay, same bank group (read side).",
                Tr = "Aynı bank grubunda CAS-CAS gecikmesi (okuma tarafı)." } },
            { "tCCD_L_WR", new TimingInfo { Property = null, RatedName = "tCCD_L_WR",
                En = "CAS-to-CAS delay, same bank group (write side).",
                Tr = "Aynı bank grubunda CAS-CAS gecikmesi (yazma tarafı)." } },
            { "tCCD_L_WR2", new TimingInfo { Property = null, RatedName = "tCCD_L_WR2",
                En = "Second write-side CAS-to-CAS delay, same bank group.",
                Tr = "Aynı bank grubunda ikinci yazma tarafı CAS-CAS gecikmesi." } },
            { "Cmd2T", new TimingInfo { Property = "Cmd2T", RatedName = null,
                En = "Command rate - 1T issues a command every clock, 2T every other clock.",
                Tr = "Komut hızı - 1T her çevrimde, 2T iki çevrimde bir komut gönderir." } },
            { "GDM", new TimingInfo { Property = "GDM", RatedName = null,
                En = "Gear Down Mode - halves the command rate for signal integrity.",
                Tr = "Gear Down Mode - sinyal bütünlüğü için komut hızını yarıya indirir." } },
            { "BGS", new TimingInfo { Property = "BGS", RatedName = null,
                En = "Bank group swap - interleaves addresses across bank groups.",
                Tr = "Bank grubu takası - adresleri bank grupları arasında serpiştirir." } },
            { "BGS Alt", new TimingInfo { Property = "BGSAlt", RatedName = null,
                En = "Alternative bank group swap mapping.",
                Tr = "Alternatif bank grubu takas haritalaması." } },
            { "Refresh", new TimingInfo { Property = "RefreshMode", RatedName = null,
                En = "Refresh mode in use: normal, fine-granularity or mixed (same-bank).",
                Tr = "Kullanılan yenileme modu: normal, ince taneli veya karışık (aynı-bank)." } },
            { "PowerDown", new TimingInfo { Property = "PowerDown", RatedName = null,
                En = "DRAM power-down mode. Off gives a small latency win at a small power cost.",
                Tr = "DRAM güç tasarrufu modu. Kapalı olması az da olsa gecikme kazandırır." } },
        };

        /// <summary>
        /// Hooks a timings panel. Safe to call more than once; safe when <paramref name="vm"/> is null.
        /// </summary>
        public static void Attach(FrameworkElement panel, MainViewModel vm)
        {
            if (panel == null)
                return;

            if (panel.IsLoaded)
                Apply(panel, vm);
            else
                panel.Loaded += (s, e) => Apply(panel, vm);
        }

        private static void Apply(DependencyObject root, MainViewModel vm)
        {
            foreach (TextBlock label in EnumerateTextBlocks(root))
            {
                string text = label.Text;
                if (string.IsNullOrEmpty(text))
                    continue;

                TimingInfo info;
                if (!Map.TryGetValue(text.Trim(), out info))
                    continue;

                // Placeholder so ToolTipService starts tracking the element; the real content is
                // produced in ToolTipOpening, which is the only moment live values are meaningful.
                label.ToolTip = text;
                var captured = info;
                label.ToolTipOpening -= OnToolTipOpening;
                label.Tag = new object[] { captured, vm };
                label.ToolTipOpening += OnToolTipOpening;
            }
        }

        private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
        {
            var element = sender as TextBlock;
            var state = element?.Tag as object[];
            if (element == null || state == null || state.Length != 2)
                return;

            var info = state[0] as TimingInfo;
            var vm = state[1] as MainViewModel;
            if (info == null)
                return;

            element.ToolTip = BuildText(element.Text, info, vm);
        }

        private static string BuildText(string label, TimingInfo info, MainViewModel vm)
        {
            var sb = new StringBuilder();
            sb.Append(Loc.Language == AppLanguage.Turkish ? info.Tr : info.En);

            float frequency = vm != null ? vm.MemoryFrequency : 0f;
            double tckPs = frequency > 0 ? 2000000.0 / frequency : 0;

            // Live value in ns - the frequency-independent view of the same number.
            double? live = GetLiveValue(info, vm);
            if (live.HasValue && frequency > 0)
            {
                double ns = live.Value * 2000.0 / frequency;
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    Loc.Language == AppLanguage.Turkish
                        ? "Şu an: {0:0} clk  =  {1:0.###} ns  @ {2:0} MT/s"
                        : "Now: {0:0} clk  =  {1:0.###} ns  @ {2:0} MT/s",
                    live.Value, ns, frequency);
            }

            // JEDEC rating straight out of the module's SPD.
            if (info.RatedName != null)
            {
                RatedTiming rated;
                var table = SpdRatedTimings.Cached;
                if (table != null && table.TryGetValue(info.RatedName, out rated))
                {
                    if (live.HasValue)
                        sb.AppendLine();
                    else
                        sb.AppendLine().AppendLine();

                    int ratedNck = rated.NckAt(tckPs);
                    if (ratedNck > 0)
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            Loc.Language == AppLanguage.Turkish
                                ? "JEDEC (SPD): {0:0.###} ns  =  en az {1:0} clk"
                                : "JEDEC (SPD): {0:0.###} ns  =  min {1:0} clk",
                            rated.Nanoseconds, ratedNck);
                    }
                    else
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "JEDEC (SPD): {0:0.###} ns", rated.Nanoseconds);
                    }
                }
            }

            return sb.ToString();
        }

        private static double? GetLiveValue(TimingInfo info, MainViewModel vm)
        {
            if (info.Property == null || vm == null || vm.Timings == null)
                return null;

            try
            {
                var prop = vm.Timings.GetType().GetProperty(info.Property);
                object value = prop?.GetValue(vm.Timings, null);
                if (value == null)
                    return null;

                if (value is uint) return (uint)value;
                if (value is int) return (int)value;
                if (value is ushort) return (ushort)value;
                if (value is byte) return (byte)value;
                if (value is float) return (float)value;
                if (value is double) return (double)value;
            }
            catch
            {
                // Property shape differs between Core versions - fall through to "no live value".
            }

            return null;
        }

        private static IEnumerable<TextBlock> EnumerateTextBlocks(DependencyObject root)
        {
            var result = new List<TextBlock>();
            Collect(root, result);
            return result;
        }

        private static void Collect(DependencyObject node, List<TextBlock> sink)
        {
            if (node == null)
                return;

            var textBlock = node as TextBlock;
            if (textBlock != null)
                sink.Add(textBlock);

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                Collect(VisualTreeHelper.GetChild(node, i), sink);
        }
    }
}
