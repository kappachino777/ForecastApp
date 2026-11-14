// Glue between the importer, the SARIMA planner and the chart. Everything stays in memory.
//
//   ImportResult imp = SalesImporter.Import(path);
//   RunOutput run    = ForecastRunner.Run(imp.BySku["SKU-001"], request, holdout: 8);
//   ForecastChart.Populate(chart, run);

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReplenishmentForecast
{
    public class RunOutput
    {
        public ForecastRequest Request;
        public DateTime[] HistoryDates;      // start date of every complete historical period
        public double[] HistoryActual;       // actual sales per period (original unit)
        public ReplenishmentResult Plan;     // next-period forecast and suggested order
        public FitReport Backtest;           // null when the history is too short for a backtest
        public string BacktestMessage;       // reason, when Backtest is null
    }

    public static class ForecastRunner
    {
        public static RunOutput Run(IEnumerable<SalesRecord> sales, ForecastRequest req, int holdout)
        {
            List<SalesRecord> list = sales as List<SalesRecord> ?? sales.ToList();

            DateTime nextStart;
            double[] y = TimeBuckets.Aggregate(list, req.Granularity, req.AsOfDate, out nextStart);
            if (y.Length == 0)
                throw new InvalidOperationException(
                    "No complete period of data before the 'as of' date. Choose a later date or a finer granularity.");

            var o = new RunOutput { Request = req, HistoryActual = y };
            o.HistoryDates = new DateTime[y.Length];
            for (int i = 0; i < y.Length; i++)
                o.HistoryDates[i] = TimeBuckets.AddPeriods(nextStart, req.Granularity, i - y.Length);

            o.Plan = ReplenishmentPlanner.Plan(list, req);

            try
            {
                o.Backtest = ReplenishmentPlanner.Backtest(list, req.Granularity, req.AsOfDate, holdout);
            }
            catch (InvalidOperationException ex)
            {
                o.BacktestMessage = ex.Message;   // chart still shows history and the next-period forecast
            }
            return o;
        }
    }
}
