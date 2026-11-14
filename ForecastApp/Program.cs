// Replenishment suggestion with a plain-C# seasonal ARIMA (no external libraries).
// Target: .NET Framework 4.7.2+, C# 7.3 (Windows Forms App). Contains the forecasting engine and the entry point.
//
// Design points
//  1. Granularity (daily / weekly / monthly / yearly) is chosen by the user. Raw sales rows are
//     bucketed into that granularity, and the seasonal period is derived from it.
//  2. No future promo data goes into the model. The model forecasts BASELINE demand from history.
//     The user can enter a promo factor (e.g. 1.20 = +20%) per forecast period to adjust the result.
//  3. Unit of measure is irrelevant: the series is divided by its mean before fitting (unit-free),
//     and results are scaled back, so they come out in the same unit the user typed in.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace ReplenishmentForecast
{
    // ------------------------------------------------------------------ inputs / outputs

    public enum Granularity { Daily, Weekly, Monthly, Yearly }

    public class SalesRecord
    {
        public DateTime Date;
        public double Qty;          // any unit, as long as it is the same for the whole series
    }

    public class ForecastRequest
    {
        public Granularity Granularity = Granularity.Daily;
        public int HorizonPeriods = 1;          // how many periods the next order must cover
        public double ServiceLevel = 0.95;      // 0.90 - 0.99
        public double OnHandQty;                // current stock, same unit as sales
        public double[] PromoFactors;           // optional, one per horizon period; null = 1.0 everywhere
        public DateTime AsOfDate = DateTime.Today;
        public double OrderMultiple = 1;        // optional rounding (pack size), same unit as sales
    }

    public class ForecastPeriod
    {
        public DateTime Start;
        public double Baseline;     // model output
        public double Adjusted;     // baseline * promo factor
    }

    public class ReplenishmentResult
    {
        public string Model;
        public string Note;         // why a simpler model was used, if it was
        public List<ForecastPeriod> Periods = new List<ForecastPeriod>();
        public double ForecastDemand;
        public double SafetyStock;
        public double TargetStock;
        public double SuggestedQty;
    }

    // ------------------------------------------------------------------ time bucketing

    public static class TimeBuckets
    {
        public static DateTime PeriodStart(DateTime d, Granularity g)
        {
            d = d.Date;
            switch (g)
            {
                case Granularity.Daily: return d;
                case Granularity.Weekly: return d.AddDays(-(((int)d.DayOfWeek + 6) % 7)); // Monday start
                case Granularity.Monthly: return new DateTime(d.Year, d.Month, 1);
                default: return new DateTime(d.Year, 1, 1);
            }
        }

        public static DateTime NextStart(DateTime s, Granularity g)
        {
            switch (g)
            {
                case Granularity.Daily: return s.AddDays(1);
                case Granularity.Weekly: return s.AddDays(7);
                case Granularity.Monthly: return s.AddMonths(1);
                default: return s.AddYears(1);
            }
        }

        public static DateTime AddPeriods(DateTime s, Granularity g, int k)
        {
            switch (g)
            {
                case Granularity.Daily: return s.AddDays(k);
                case Granularity.Weekly: return s.AddDays(7 * k);
                case Granularity.Monthly: return s.AddMonths(k);
                default: return s.AddYears(k);
            }
        }

        // Sums qty per period, fills empty periods with 0, keeps COMPLETE periods only.
        // nextStart = start of the first period to forecast.
        public static double[] Aggregate(IEnumerable<SalesRecord> rows, Granularity g, DateTime asOf,
                                         out DateTime nextStart)
        {
            var sums = new Dictionary<DateTime, double>();
            foreach (var r in rows)
            {
                if (r.Date.Date > asOf.Date) continue;
                DateTime key = PeriodStart(r.Date, g);
                double v;
                sums.TryGetValue(key, out v);
                sums[key] = v + r.Qty;
            }
            if (sums.Count == 0) throw new InvalidOperationException("No sales history.");

            DateTime first = sums.Keys.Min();
            // a history that starts mid-period would understate its first bucket
            DateTime minDate = rows.Where(r => r.Date.Date <= asOf.Date).Min(r => r.Date.Date);
            if (g != Granularity.Daily && minDate > first) first = NextStart(first, g);

            var list = new List<double>();
            DateTime cur = first;
            while (NextStart(cur, g) <= asOf.Date.AddDays(1))   // period fully elapsed
            {
                double v;
                list.Add(sums.TryGetValue(cur, out v) ? v : 0.0);
                cur = NextStart(cur, g);
            }
            nextStart = cur;
            return list.ToArray();
        }
    }

    // ------------------------------------------------------------------ SARIMA (CSS + Nelder-Mead)

    public class SarimaModel
    {
        int p, d, q, P, D, Q, s;
        double[] ar, ma;                 // expanded (seasonal x non-seasonal) coefficients, lag 1..n
        List<int> diffLags = new List<int>();
        List<double[]> levels = new List<double[]>();
        double[] w, e;                   // differenced (demeaned) series and its residuals
        double wMean;

        public double Sigma;             // residual std dev (one-step error, normalized scale)
        public double Aic;

        public override string ToString()
        {
            if (s < 2) return string.Format("ARIMA({0},{1},{2})", p, d, q);
            return string.Format("SARIMA({0},{1},{2})({3},{4},{5})[{6}]", p, d, q, P, D, Q, s);
        }

        // ---- model selection: pick differencing by variance, orders by AIC on a small grid
        public static SarimaModel FitBest(double[] y, int s)
        {
            int D = 0, d = 0;
            double[] cur = y;
            if (s > 1)
            {
                var t = Diff(cur, s);
                if (Variance(t) < Variance(cur)) { D = 1; cur = t; }
            }
            if (Variance(Diff(cur, 1)) < Variance(cur)) d = 1;

            int maxSeas = s > 1 ? 1 : 0;
            int maxOrder = y.Length < 20 ? 1 : 2;      // small samples: keep the model small
            double limit = 10 * y.Select(v => Math.Abs(v)).Max() + 1;
            SarimaModel best = null;

            for (int p = 0; p <= maxOrder; p++)
            for (int q = 0; q <= maxOrder; q++)
            for (int P = 0; P <= maxSeas; P++)
            for (int Q = 0; Q <= maxSeas; Q++)
            {
                var m = Fit(y, p, d, q, P, D, Q, s);
                if (m == null) continue;
                var probe = m.Forecast(Math.Max(s, 12));          // reject exploding models
                if (probe.Any(v => double.IsNaN(v) || Math.Abs(v) > limit)) continue;
                if (best == null || m.Aic < best.Aic) best = m;
            }
            return best;
        }

        static SarimaModel Fit(double[] y, int p, int d, int q, int P, int D, int Q, int s)
        {
            var m = new SarimaModel { p = p, d = d, q = q, P = P, D = D, Q = Q, s = s };
            for (int i = 0; i < D; i++) m.diffLags.Add(s);
            for (int i = 0; i < d; i++) m.diffLags.Add(1);

            m.levels.Add(y);
            foreach (int lag in m.diffLags) m.levels.Add(Diff(m.levels.Last(), lag));

            double[] raw = m.levels.Last();
            if (raw.Length < 5) return null;
            m.wMean = (d + D == 0) ? raw.Average() : 0.0;
            m.w = raw.Select(v => v - m.wMean).ToArray();
            m.e = new double[m.w.Length];

            int k = p + q + P + Q;
            double[] theta = new double[k];
            double[] scratch = new double[m.w.Length];

            if (k > 0)
            {
                Func<double[], double> objective = th =>
                {
                    if (th.Any(v => Math.Abs(v) > 0.98)) return 1e12;       // keep coefficients tame
                    double[] a, b;
                    Expand(th, p, q, P, Q, s, out a, out b);
                    if (m.w.Length - a.Length < 4) return 1e12;
                    double sse = Css(m.w, a, b, scratch);
                    return (double.IsNaN(sse) || double.IsInfinity(sse)) ? 1e12 : sse;
                };
                theta = NelderMead(objective, new double[k], 0.1, 400);
            }

            Expand(theta, p, q, P, Q, s, out m.ar, out m.ma);
            double sseFinal = Css(m.w, m.ar, m.ma, m.e);
            int nEff = m.w.Length - m.ar.Length;
            int K = k + 1;                                   // coefficients + variance
            if (nEff < 4 || nEff - K - 1 <= 0 || double.IsNaN(sseFinal) || sseFinal >= 1e11) return null;

            m.Sigma = Math.Sqrt(sseFinal / nEff);
            // AICc: AIC with a small-sample correction, so short histories do not pick over-sized models
            m.Aic = nEff * Math.Log(sseFinal / nEff + 1e-12) + 2 * K
                    + 2.0 * K * (K + 1) / (nEff - K - 1);
            return m;
        }

        public double[] Forecast(int h)
        {
            var wExt = new List<double>(w);
            var eExt = new List<double>(e);
            var wf = new double[h];
            for (int i = 0; i < h; i++)
            {
                int t = wExt.Count;
                double pred = 0;
                for (int k = 1; k <= ar.Length; k++) if (t - k >= 0) pred += ar[k - 1] * wExt[t - k];
                for (int k = 1; k <= ma.Length; k++) if (t - k >= 0) pred += ma[k - 1] * eExt[t - k];
                wExt.Add(pred);
                eExt.Add(0);                       // future shocks have expectation 0
                wf[i] = pred;
            }

            double[] cur = wf.Select(v => v + wMean).ToArray();

            // undo differencing, last difference first
            for (int i = diffLags.Count - 1; i >= 0; i--)
            {
                int lag = diffLags[i];
                var ext = new List<double>(levels[i]);
                var outp = new double[h];
                for (int j = 0; j < h; j++)
                {
                    double val = cur[j] + ext[ext.Count - lag];
                    ext.Add(val);
                    outp[j] = val;
                }
                cur = outp;
            }
            return cur;
        }

        // One-step-ahead fitted values on the (normalized) level scale: actual - residual.
        // NaN where the model has no fitted value (the first few observations).
        public double[] FittedValues()
        {
            double[] y = levels[0];
            int offset = diffLags.Sum();
            var fit = new double[y.Length];
            for (int i = 0; i < fit.Length; i++) fit[i] = double.NaN;
            for (int i = ar.Length; i < e.Length; i++) fit[i + offset] = y[i + offset] - e[i];
            return fit;
        }

        // ---- helpers
        static double[] Diff(double[] x, int lag)
        {
            if (x.Length <= lag) return new double[0];
            var r = new double[x.Length - lag];
            for (int i = 0; i < r.Length; i++) r[i] = x[i + lag] - x[i];
            return r;
        }

        static double Variance(double[] x)
        {
            if (x.Length < 2) return double.MaxValue;
            double mean = x.Average();
            return x.Sum(v => (v - mean) * (v - mean)) / (x.Length - 1);
        }

        static double[] Multiply(double[] a, double[] b)
        {
            var r = new double[a.Length + b.Length - 1];
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++) r[i + j] += a[i] * b[j];
            return r;
        }

        // theta layout: [phi(p), PHI(P), theta(q), THETA(Q)]
        static void Expand(double[] th, int p, int q, int P, int Q, int s, out double[] ar, out double[] ma)
        {
            double[] a = new double[p + 1], A = new double[P * s + 1];
            double[] b = new double[q + 1], B = new double[Q * s + 1];
            a[0] = A[0] = b[0] = B[0] = 1;
            for (int i = 0; i < p; i++) a[i + 1] = -th[i];
            for (int j = 0; j < P; j++) A[(j + 1) * s] = -th[p + j];
            for (int i = 0; i < q; i++) b[i + 1] = th[p + P + i];
            for (int j = 0; j < Q; j++) B[(j + 1) * s] = th[p + P + q + j];

            double[] pa = Multiply(a, A), pb = Multiply(b, B);
            ar = new double[pa.Length - 1];
            ma = new double[pb.Length - 1];
            for (int k = 1; k < pa.Length; k++) ar[k - 1] = -pa[k];
            for (int k = 1; k < pb.Length; k++) ma[k - 1] = pb[k];
        }

        // conditional sum of squares: first ar.Length residuals are treated as 0
        static double Css(double[] w, double[] ar, double[] ma, double[] e)
        {
            int n = w.Length, start = ar.Length;
            double sse = 0;
            for (int t = 0; t < n; t++)
            {
                if (t < start) { e[t] = 0; continue; }
                double pred = 0;
                for (int k = 1; k <= ar.Length; k++) pred += ar[k - 1] * w[t - k];
                for (int k = 1; k <= ma.Length; k++) if (t - k >= 0) pred += ma[k - 1] * e[t - k];
                e[t] = w[t] - pred;
                sse += e[t] * e[t];
            }
            return sse;
        }

        static double[] NelderMead(Func<double[], double> f, double[] x0, double step, int maxIter)
        {
            int n = x0.Length;
            var sx = new double[n + 1][];
            var fv = new double[n + 1];
            sx[0] = (double[])x0.Clone();
            for (int i = 0; i < n; i++) { var x = (double[])x0.Clone(); x[i] += step; sx[i + 1] = x; }
            for (int i = 0; i <= n; i++) fv[i] = f(sx[i]);

            for (int iter = 0; iter < maxIter; iter++)
            {
                Array.Sort(fv, sx);
                if (Math.Abs(fv[n] - fv[0]) < 1e-10) break;

                var c = new double[n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) c[j] += sx[i][j] / n;

                Func<double, double[]> point = coef =>
                {
                    var r = new double[n];
                    for (int j = 0; j < n; j++) r[j] = c[j] + coef * (sx[n][j] - c[j]);
                    return r;
                };

                var xr = point(-1.0); double fr = f(xr);
                if (fr < fv[0])
                {
                    var xe = point(-2.0); double fe = f(xe);
                    if (fe < fr) { sx[n] = xe; fv[n] = fe; } else { sx[n] = xr; fv[n] = fr; }
                }
                else if (fr < fv[n - 1]) { sx[n] = xr; fv[n] = fr; }
                else
                {
                    var xc = fr < fv[n] ? point(-0.5) : point(0.5);
                    double fc = f(xc);
                    if (fc < Math.Min(fv[n], fr)) { sx[n] = xc; fv[n] = fc; }
                    else
                    {
                        for (int i = 1; i <= n; i++)
                        {
                            for (int j = 0; j < n; j++) sx[i][j] = sx[0][j] + 0.5 * (sx[i][j] - sx[0][j]);
                            fv[i] = f(sx[i]);
                        }
                    }
                }
            }
            Array.Sort(fv, sx);
            return sx[0];
        }
    }

    // ------------------------------------------------------------------ backtest report (for charts)

    public class FitReport
    {
        public string Model;
        public DateTime[] Dates;            // every historical period
        public double[] Actual;             // actual sales, original unit
        public double[] Fitted;             // in-sample one-step fit (NaN where undefined)
        public DateTime[] HoldoutDates;     // last N periods, hidden from the model
        public double[] HoldoutActual;
        public double[] HoldoutForecast;    // what the model predicted for them
        public double Wape;                 // sum|actual - forecast| / sum(actual) on the holdout
    }

    // ------------------------------------------------------------------ planner

    public static class ReplenishmentPlanner
    {
        const int MinObsForArima = 6;      // below this even a small ARIMA cannot be estimated

        public static ReplenishmentResult Plan(IEnumerable<SalesRecord> sales, ForecastRequest req)
        {
            DateTime nextStart;
            double[] y = TimeBuckets.Aggregate(sales, req.Granularity, req.AsOfDate, out nextStart);

            // unit-free: divide by mean so the optimizer never sees "pieces vs cartons vs kg"
            double scale = y.Average();
            if (scale <= 0) scale = 1;
            double[] yn = y.Select(v => v / scale).ToArray();

            int h = Math.Max(1, req.HorizonPeriods);
            int s = SeasonalPeriod(req.Granularity, yn.Length);

            double[] fc = null;
            double sigma = 0;
            string modelName = null;

            string note = null;
            if (yn.Length >= MinObsForArima)
            {
                SarimaModel m = null;
                if (s > 0) m = SarimaModel.FitBest(yn, s);          // 1) seasonal model
                if (m == null)                                       // 2) plain ARIMA (no seasonal terms)
                {
                    m = SarimaModel.FitBest(yn, 0);
                    if (m != null)
                    {
                        int needed = 3 * FullSeasonLength(req.Granularity);
                        note = FullSeasonLength(req.Granularity) < 2
                            ? "No seasonal cycle for this granularity; plain ARIMA used."
                            : string.Format("Seasonal terms off: {0} periods needed, {1} available. Plain ARIMA used.",
                                            needed, yn.Length);
                    }
                }
                if (m != null)
                {
                    fc = m.Forecast(h);
                    sigma = m.Sigma;
                    modelName = m.ToString();
                }
            }
            if (fc == null)   // 3) last resort: fewer than 6 periods, or nothing could be fitted
            {
                note = string.Format("Only {0} periods available (need {1}+ for ARIMA); average used.",
                                     yn.Length, MinObsForArima);
                var recent = yn.Skip(Math.Max(0, yn.Length - 4)).ToArray();
                fc = Enumerable.Repeat(recent.Average(), h).ToArray();
                sigma = StdDev(yn.Skip(Math.Max(0, yn.Length - 8)).ToArray());
                modelName = "Moving average (fallback)";
            }

            var res = new ReplenishmentResult { Model = modelName, Note = note };
            DateTime start = nextStart;
            double factorSum = 0;
            for (int i = 0; i < h; i++)
            {
                double pf = (req.PromoFactors != null && i < req.PromoFactors.Length) ? req.PromoFactors[i] : 1.0;
                double baseline = Math.Max(0, fc[i]) * scale;
                res.Periods.Add(new ForecastPeriod { Start = start, Baseline = baseline, Adjusted = baseline * pf });
                factorSum += pf;
                start = TimeBuckets.NextStart(start, req.Granularity);
            }

            double z = InverseNormal(Math.Min(0.999, Math.Max(0.5, req.ServiceLevel)));
            double avgFactor = factorSum / h;

            res.ForecastDemand = res.Periods.Sum(x => x.Adjusted);
            res.SafetyStock = z * sigma * scale * Math.Sqrt(h) * avgFactor;   // simple sqrt-time approximation
            res.TargetStock = res.ForecastDemand + res.SafetyStock;

            double need = Math.Max(0, res.TargetStock - req.OnHandQty);
            if (req.OrderMultiple > 0) need = Math.Ceiling(need / req.OrderMultiple - 1e-9) * req.OrderMultiple;
            res.SuggestedQty = need;
            return res;
        }

        // Hides the last `holdout` periods, fits on the rest, forecasts the hidden part and compares.
        public static FitReport Backtest(IEnumerable<SalesRecord> sales, Granularity g, DateTime asOf, int holdout)
        {
            DateTime nextStart;
            double[] y = TimeBuckets.Aggregate(sales, g, asOf, out nextStart);
            int n = y.Length;
            if (holdout < 1 || n - holdout < MinObsForArima)
                throw new InvalidOperationException("Not enough history for this holdout size.");

            double[] train = y.Take(n - holdout).ToArray();
            double scale = train.Average();
            if (scale <= 0) scale = 1;
            double[] tn = train.Select(v => v / scale).ToArray();

            int s = SeasonalPeriod(g, tn.Length);
            SarimaModel m = null;
            if (s > 0) m = SarimaModel.FitBest(tn, s);
            if (m == null) m = SarimaModel.FitBest(tn, 0);
            if (m == null) throw new InvalidOperationException("No model could be fitted.");

            var rep = new FitReport { Model = m.ToString(), Actual = y };
            rep.Dates = new DateTime[n];
            for (int i = 0; i < n; i++) rep.Dates[i] = TimeBuckets.AddPeriods(nextStart, g, i - n);

            double[] fitN = m.FittedValues();
            rep.Fitted = new double[n];
            for (int i = 0; i < n; i++)
                rep.Fitted[i] = (i < fitN.Length && !double.IsNaN(fitN[i])) ? fitN[i] * scale : double.NaN;

            double[] fc = m.Forecast(holdout);
            rep.HoldoutDates = rep.Dates.Skip(n - holdout).ToArray();
            rep.HoldoutActual = y.Skip(n - holdout).ToArray();
            rep.HoldoutForecast = fc.Select(v => Math.Max(0, v) * scale).ToArray();

            double num = 0, den = rep.HoldoutActual.Sum();
            for (int i = 0; i < holdout; i++) num += Math.Abs(rep.HoldoutActual[i] - rep.HoldoutForecast[i]);
            rep.Wape = den > 0 ? num / den : double.NaN;
            return rep;
        }

        static int FullSeasonLength(Granularity g)
        {
            switch (g)
            {
                case Granularity.Daily: return 7;
                case Granularity.Weekly: return 52;
                case Granularity.Monthly: return 12;
                default: return 0;
            }
        }

        // seasonal cycle length in PERIODS, only if there is enough history to estimate it
        static int SeasonalPeriod(Granularity g, int nObs)
        {
            int s;
            switch (g)
            {
                case Granularity.Daily: s = 7; break;
                case Granularity.Weekly: s = 52; break;
                case Granularity.Monthly: s = 12; break;
                default: s = 0; break;
            }
            return (s > 1 && nObs >= 3 * s) ? s : 0;
        }

        static double StdDev(double[] x)
        {
            if (x.Length < 2) return 0;
            double m = x.Average();
            return Math.Sqrt(x.Sum(v => (v - m) * (v - m)) / (x.Length - 1));
        }

        // Abramowitz & Stegun 26.2.23, good for service levels 0.5 .. 0.999
        static double InverseNormal(double p)
        {
            double t = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
            return t - (2.515517 + 0.802853 * t + 0.010328 * t * t)
                       / (1 + 1.432788 * t + 0.189269 * t * t + 0.001308 * t * t * t);
        }
    }

    // ------------------------------------------------------------------ demo

    class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        // The original console example with synthetic data (call it from a test if you still want it).
        public static void ConsoleDemo()
        {
            // synthetic history: 4 years of daily sales with weekly + yearly pattern, slight trend, noise
            var rnd = new Random(42);
            var sales = new List<SalesRecord>();
            DateTime asOf = new DateTime(2026, 11, 14);
            double[] weekday = { 0.9, 0.95, 1.0, 1.0, 1.1, 1.3, 0.75 };   // Mon..Sun
            for (DateTime d = asOf.AddDays(-1460); d <= asOf; d = d.AddDays(1))
            {
                int idx = ((int)d.DayOfWeek + 6) % 7;
                double yearly = 1 + 0.15 * Math.Sin(2 * Math.PI * d.DayOfYear / 365.0);
                double trend = 1 + 0.0002 * (d - asOf.AddDays(-1460)).TotalDays;
                double noise = 1 + 0.12 * (rnd.NextDouble() - 0.5) * 2;
                sales.Add(new SalesRecord { Date = d, Qty = Math.Round(100 * weekday[idx] * yearly * trend * noise) });
            }

            // Case 1: small distributor, daily granularity, order covers the next 3 days, no promo
            Run("Small distributor (daily, 3-day cover)", sales, new ForecastRequest
            {
                Granularity = Granularity.Daily,
                HorizonPeriods = 3,
                ServiceLevel = 0.95,
                OnHandQty = 150,
                AsOfDate = asOf
            });

            // Case 2: big distributor, monthly granularity, 1-month cover, +20% promo planned
            Run("Big distributor (monthly, 1-month cover, promo x1.20)", sales, new ForecastRequest
            {
                Granularity = Granularity.Monthly,
                HorizonPeriods = 1,
                ServiceLevel = 0.97,
                OnHandQty = 2000,
                PromoFactors = new[] { 1.20 },
                OrderMultiple = 50,
                AsOfDate = asOf
            });

            // Case 3: new customer with only ~14 months of history -> seasonal terms off, plain ARIMA used
            var shortHistory = sales.Where(x => x.Date > asOf.AddMonths(-14)).ToList();
            Run("Short history (monthly, 14 months)", shortHistory, new ForecastRequest
            {
                Granularity = Granularity.Monthly,
                HorizonPeriods = 1,
                ServiceLevel = 0.95,
                OnHandQty = 2000,
                AsOfDate = asOf
            });
        }

        static void Run(string title, List<SalesRecord> sales, ForecastRequest req)
        {
            var r = ReplenishmentPlanner.Plan(sales, req);
            Console.WriteLine("== " + title);
            Console.WriteLine("Model          : " + r.Model);
            if (r.Note != null) Console.WriteLine("Note           : " + r.Note);
            foreach (var p in r.Periods)
                Console.WriteLine("  {0:yyyy-MM-dd}  baseline {1,10:N1}   adjusted {2,10:N1}", p.Start, p.Baseline, p.Adjusted);
            Console.WriteLine("Forecast demand: {0:N1}", r.ForecastDemand);
            Console.WriteLine("Safety stock   : {0:N1}", r.SafetyStock);
            Console.WriteLine("Target stock   : {0:N1}", r.TargetStock);
            Console.WriteLine("On hand        : {0:N1}", req.OnHandQty);
            Console.WriteLine("SUGGESTED ORDER: {0:N0}", r.SuggestedQty);
            Console.WriteLine();
        }
    }
}
