using System;
using System.Globalization;
using System.Windows.Data;

namespace ZenTimings.Converters
{
    /// <summary>
    /// Appends this clock's multiplier to its label: how many times it has to be multiplied before
    /// all three of MCLK / FCLK / UCLK land on the same number.
    /// </summary>
    /// <remarks>
    /// That common number is the least common multiple, and dividing it by each clock gives the
    /// multipliers. 2400 / 1800 / 2400 meet at 7200, so they read 3, 4, 3 - the 1800 needs four
    /// steps where the 2400s need three. When one clock is a plain division of another the answer
    /// collapses to the familiar form: 2400 / 1200 / 2400 reads 1, 2, 1, and 4000 / 2000 / 2000
    /// reads 1, 2, 2.
    ///
    /// Working from the LCM rather than from the fastest clock is what lets a ratio like 4:3 be
    /// stated at all - dividing the fastest by each clock only produces whole numbers when the
    /// others happen to divide into it exactly.
    ///
    /// Rides on the label because the value column is fixed width and the panel must not get wider.
    /// The parameter names which of the three the label belongs to.
    /// </remarks>
    public class ClockRatioConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string label = parameter as string ?? string.Empty;

            if (values == null || values.Length < 3)
                return label;

            long mclk = Round(values[0]);
            long fclk = Round(values[1]);
            long uclk = Round(values[2]);

            if (mclk <= 0 || fclk <= 0 || uclk <= 0)
                return label;

            long mine;
            switch (label)
            {
                case "MCLK": mine = mclk; break;
                case "FCLK": mine = fclk; break;
                case "UCLK": mine = uclk; break;
                default: return label;
            }

            // All three first. This is the case worth showing, because it states the whole setup.
            long meetingPoint = Lcm(Lcm(mclk, fclk), uclk);
            if (meetingPoint > 0 && Worst(meetingPoint, mclk, fclk, uclk) <= MaxSteps)
                return label + "  " + (meetingPoint / mine).ToString(CultureInfo.InvariantCulture);

            // FCLK often sits on a value - 2233, 2167 - that shares nothing with the memory clock,
            // and including it would push the common multiple into the millions. Letting that hide
            // MCLK and UCLK as well would be throwing away the one ratio that always matters, so
            // fall back to those two on their own and leave FCLK blank.
            if (label == "FCLK")
                return label;

            long pair = Lcm(mclk, uclk);
            if (pair > 0 && Worst(pair, mclk, uclk) <= MaxSteps)
                return label + "  " + (pair / mine).ToString(CultureInfo.InvariantCulture);

            return label;
        }

        /// <summary>
        /// A ratio only says something while the numbers stay small. 3000 against 2200 reduces to
        /// 15 : 11, which is arithmetically true and tells nobody anything.
        /// </summary>
        private const long MaxSteps = 8;

        private static long Worst(long meetingPoint, params long[] clocks)
        {
            long worst = 0;
            foreach (long clock in clocks)
            {
                long steps = meetingPoint / clock;
                if (steps > worst)
                    worst = steps;
            }

            return worst;
        }

        private static long Round(object value)
        {
            if (value is float) return (long)Math.Round((float)value);
            if (value is double) return (long)Math.Round((double)value);
            if (value is int) return (int)value;
            if (value is uint) return (uint)value;

            return 0;
        }

        private static long Gcd(long a, long b)
        {
            while (b != 0)
            {
                long t = b;
                b = a % b;
                a = t;
            }

            return a;
        }

        /// <summary>Least common multiple, or 0 when it would not fit.</summary>
        private static long Lcm(long a, long b)
        {
            if (a <= 0 || b <= 0)
                return 0;

            long divisor = Gcd(a, b);
            if (divisor <= 0)
                return 0;

            // Coprime clocks multiply out fast; bail rather than overflow.
            long result = a / divisor;
            if (result > long.MaxValue / b)
                return 0;

            return result * b;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
