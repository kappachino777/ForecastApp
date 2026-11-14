// Chart helper for WinForms (System.Windows.Forms.DataVisualization, ships with .NET Framework;
// add a reference to System.Windows.Forms.DataVisualization).
// Replaces ForecastChartForm.cs: the chart now lives inside the main window.

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms.DataVisualization.Charting;

namespace ReplenishmentForecast
{
    public static class ForecastChart
    {
        public static Chart CreateChart()
        {
            var chart = new Chart { Dock = System.Windows.Forms.DockStyle.Fill };

            var area = new ChartArea("main");
            area.AxisX.LabelStyle.Format = "yyyy-MM-dd";
            area.AxisX.MajorGrid.LineColor = Color.Gainsboro;
            area.AxisY.MajorGrid.LineColor = Color.Gainsboro;
            area.AxisY.Title = "Sales qty per period";
            area.CursorX.IsUserSelectionEnabled = true;      // drag to zoom
            area.AxisX.ScaleView.Zoomable = true;
            chart.ChartAreas.Add(area);

            chart.Legends.Add(new Legend("legend") { Docking = Docking.Top });
            return chart;
        }

        public static void Populate(Chart chart, RunOutput o)
        {
            chart.Series.Clear();
            chart.Titles.Clear();

            string title = o.Plan.Model;
            if (o.Backtest != null) title += string.Format("   |   backtest WAPE: {0:P1}", o.Backtest.Wape);
            chart.Titles.Add(title);

            // 1) actual history
            var actual = NewSeries("Actual", Color.SteelBlue, ChartDashStyle.Solid, 2, false);
            for (int i = 0; i < o.HistoryDates.Length; i++)
                actual.Points.AddXY(o.HistoryDates[i], o.HistoryActual[i]);
            chart.Series.Add(actual);

            // 2) in-sample fit and hidden-period forecast (only when a backtest was possible)
            if (o.Backtest != null)
            {
                var fitted = NewSeries("Fitted (in-sample, 1 step ahead)", Color.DarkOrange, ChartDashStyle.Dash, 1, false);
                for (int i = 0; i < o.Backtest.Dates.Length; i++)
                    if (!double.IsNaN(o.Backtest.Fitted[i])) fitted.Points.AddXY(o.Backtest.Dates[i], o.Backtest.Fitted[i]);
                chart.Series.Add(fitted);

                var hold = NewSeries("Backtest forecast (last periods hidden from the model)", Color.Crimson,
                                     ChartDashStyle.Solid, 3, true);
                for (int i = 0; i < o.Backtest.HoldoutDates.Length; i++)
                    hold.Points.AddXY(o.Backtest.HoldoutDates[i], o.Backtest.HoldoutForecast[i]);
                chart.Series.Add(hold);
            }

            // 3) the real forecast for the coming periods
            var next = NewSeries("Forecast (next periods)", Color.SeaGreen, ChartDashStyle.Solid, 3, true);
            foreach (var p in o.Plan.Periods) next.Points.AddXY(p.Start, p.Baseline);
            chart.Series.Add(next);

            if (o.Plan.Periods.Any(p => Math.Abs(p.Adjusted - p.Baseline) > 1e-9))
            {
                var promo = NewSeries("Forecast with promo factor", Color.MediumPurple, ChartDashStyle.Dash, 3, true);
                foreach (var p in o.Plan.Periods) promo.Points.AddXY(p.Start, p.Adjusted);
                chart.Series.Add(promo);
            }
        }

        static Series NewSeries(string name, Color color, ChartDashStyle dash, int width, bool markers)
        {
            return new Series(name)
            {
                ChartType = SeriesChartType.Line,
                XValueType = ChartValueType.DateTime,
                Color = color,
                BorderDashStyle = dash,
                BorderWidth = width,
                MarkerStyle = markers ? MarkerStyle.Circle : MarkerStyle.None,
                MarkerSize = 7,
                ChartArea = "main",
                Legend = "legend"
            };
        }
    }
}
