// WinForms chart: actual vs fitted vs holdout forecast.
// Uses the chart control that ships with .NET Framework (no NuGet needed):
// add a reference to System.Windows.Forms.DataVisualization to the project.
//
// Usage (e.g. from a button click):
//   var report = ReplenishmentPlanner.Backtest(sales, Granularity.Weekly, DateTime.Today, 8);
//   new ForecastChartForm(report).Show();

using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace ReplenishmentForecast
{
    public class ForecastChartForm : Form
    {
        public ForecastChartForm(FitReport r)
        {
            Text = "Actual vs Forecast - " + r.Model;
            Width = 1100;
            Height = 600;

            var chart = new Chart { Dock = DockStyle.Fill };

            var area = new ChartArea("main");
            area.AxisX.LabelStyle.Format = "yyyy-MM-dd";
            area.AxisX.MajorGrid.LineColor = Color.Gainsboro;
            area.AxisY.MajorGrid.LineColor = Color.Gainsboro;
            area.AxisY.Title = "Sales qty";
            area.CursorX.IsUserSelectionEnabled = true;     // drag to zoom, right-click axis to reset
            area.AxisX.ScaleView.Zoomable = true;
            chart.ChartAreas.Add(area);

            chart.Legends.Add(new Legend("legend") { Docking = Docking.Top });
            chart.Titles.Add(string.Format("{0}   |   holdout WAPE: {1:P1}", r.Model, r.Wape));

            var actual = NewSeries("Actual", Color.SteelBlue, ChartDashStyle.Solid, 2);
            for (int i = 0; i < r.Dates.Length; i++)
                actual.Points.AddXY(r.Dates[i], r.Actual[i]);

            var fitted = NewSeries("Fitted (in-sample, 1 step ahead)", Color.DarkOrange, ChartDashStyle.Dash, 1);
            for (int i = 0; i < r.Dates.Length; i++)
                if (!double.IsNaN(r.Fitted[i])) fitted.Points.AddXY(r.Dates[i], r.Fitted[i]);

            var forecast = NewSeries("Forecast (holdout)", Color.Crimson, ChartDashStyle.Solid, 3);
            for (int i = 0; i < r.HoldoutDates.Length; i++)
                forecast.Points.AddXY(r.HoldoutDates[i], r.HoldoutForecast[i]);

            chart.Series.Add(actual);
            chart.Series.Add(fitted);
            chart.Series.Add(forecast);

            Controls.Add(chart);
        }

        static Series NewSeries(string name, Color color, ChartDashStyle dash, int width)
        {
            return new Series(name)
            {
                ChartType = SeriesChartType.Line,
                XValueType = ChartValueType.DateTime,
                Color = color,
                BorderDashStyle = dash,
                BorderWidth = width,
                ChartArea = "main",
                Legend = "legend"
            };
        }
    }
}
