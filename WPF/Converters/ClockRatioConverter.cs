using System;
using System.Globalization;
using System.Windows.Data;

namespace ZenTimings.Converters
{
    /// <summary>
    /// Appends the memory controller ratio to the UCLK label: 1:1 while it runs at the memory
    /// clock, 1:2 once it drops to half.
    /// </summary>
    /// <remarks>
    /// Only UCLK against MCLK is stated, because that is the ratio the BIOS actually offers - the
    /// UCLK DIV1 setting - and the only one that changes memory controller throughput. FCLK is left
    /// alone: on AM5 it is decoupled and sits around 2000-2200 while MCLK goes past 3000, so the AM4
    /// habit of chasing 1:1:1 across all three no longer applies and a number next to it would only
    /// invite the comparison.
    ///
    /// Rides on the label because the value column is fixed width and the panel must not get wider.
    /// The parameter names which of the three the label belongs to.
    /// </remarks>
    public class ClockRatioConverter : IMultiValueConverter
    {
        /// <summary>Values are MCLK then UCLK; only the UCLK label is ever decorated.</summary>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string label = parameter as string ?? string.Empty;

            if (values == null || values.Length < 2)
                return label;

            long mclk = Round(values[0]);
            long uclk = Round(values[1]);

            if (mclk <= 0 || uclk <= 0)
                return label;

            // Only the two ratios the memory controller actually offers, matched within half a
            // percent rather than exactly: the 66 MHz straps (5867, 6133...) round to numbers
            // like 1467 : 2933 that share no factor, and an exact reduction would drop the label
            // exactly where it is most needed. Anything else gets no label at all - an arbitrary
            // small fraction would look like a mode that does not exist.
            double measured = (double)uclk / mclk;

            if (Math.Abs(measured - 1.0) < 0.005)
                return label + "  1:1";

            if (Math.Abs(measured - 0.5) < 0.005)
                return label + "  1:2";

            return label;
        }

        private static long Round(object value)
        {
            if (value is float) return (long)Math.Round((float)value);
            if (value is double) return (long)Math.Round((double)value);
            if (value is int) return (int)value;
            if (value is uint) return (uint)value;

            return 0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
