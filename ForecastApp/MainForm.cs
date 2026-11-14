// Main window (built in code, no designer needed): import -> SARIMA -> graph. No database, all in memory.
// Add references: System.Windows.Forms.DataVisualization, System.IO.Compression, System.Xml.Linq.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReplenishmentForecast
{
    public class MainForm : Form
    {
        readonly Button btnImport = new Button { Text = "Import file...", Width = 110, Height = 28 };
        readonly Button btnRun = new Button { Text = "Run forecast", Width = 110, Height = 28, Enabled = false };
        readonly Label lblFile = new Label
        {
            Text = "No file imported yet. Expected columns: Date, Qty and optionally ProductCode.",
            Dock = DockStyle.Top, Height = 24, Padding = new Padding(8, 4, 0, 0)
        };

        readonly ComboBox cboSku = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        readonly ComboBox cboGran = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        readonly NumericUpDown nudHorizon = new NumericUpDown { Minimum = 1, Maximum = 366, Value = 1, Width = 70 };
        readonly NumericUpDown nudHoldout = new NumericUpDown { Minimum = 1, Maximum = 365, Value = 8, Width = 70 };
        readonly NumericUpDown nudService = new NumericUpDown { Minimum = 50, Maximum = 99.9m, DecimalPlaces = 1, Value = 95, Width = 70 };
        readonly NumericUpDown nudOnHand = new NumericUpDown { Minimum = 0, Maximum = 1000000000000m, DecimalPlaces = 2, Width = 110 };
        readonly NumericUpDown nudMultiple = new NumericUpDown { Minimum = 0, Maximum = 1000000000m, DecimalPlaces = 2, Value = 1, Width = 80 };
        readonly TextBox txtPromo = new TextBox { Text = "1", Width = 110 };
        readonly DateTimePicker dtpAsOf = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110 };

        readonly System.Windows.Forms.DataVisualization.Charting.Chart chart = ForecastChart.CreateChart();
        readonly TextBox txtResult = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Bottom, Height = 180,
            ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9.5f)
        };

        ImportResult _import;       // the imported data, kept in memory only
        bool _loading;              // true while the SKU list is being filled

        public MainForm()
        {
            Text = "Stock replenishment forecast (prototype)";
            Width = 1200;
            Height = 800;
            StartPosition = FormStartPosition.CenterScreen;

            cboGran.DataSource = Enum.GetValues(typeof(Granularity));
            cboGran.SelectedItem = Granularity.Weekly;

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, Padding = new Padding(8)
            };
            top.Controls.Add(Field("", btnImport));
            top.Controls.Add(Field("Product", cboSku));
            top.Controls.Add(Field("Granularity", cboGran));
            top.Controls.Add(Field("Data as of", dtpAsOf));
            top.Controls.Add(Field("Cover (periods)", nudHorizon));
            top.Controls.Add(Field("On hand qty", nudOnHand));
            top.Controls.Add(Field("Service level %", nudService));
            top.Controls.Add(Field("Promo factor (1.2;1;1)", txtPromo));
            top.Controls.Add(Field("Order multiple", nudMultiple));
            top.Controls.Add(Field("Backtest periods", nudHoldout));
            top.Controls.Add(Field("", btnRun));

            // docking: fill control first, edge-docked controls after it
            Controls.Add(chart);
            Controls.Add(txtResult);
            Controls.Add(lblFile);
            Controls.Add(top);

            btnImport.Click += async (s, e) => await ImportAsync();
            btnRun.Click += async (s, e) => await RunForecastAsync();
            cboSku.SelectedIndexChanged += (s, e) =>
            {
                UpdateAsOfFromSelectedSku();
                if (!_loading && btnRun.Enabled) { var ignored = RunForecastAsync(); }
            };
        }

        // ------------------------------------------------------------ import

        async Task ImportAsync()
        {
            string path;
            using (var dlg = new OpenFileDialog { Filter = "Sales data|*.csv;*.txt;*.tsv;*.xlsx|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }

            SetBusy(true);
            ImportResult imp;
            try
            {
                imp = await Task.Run(() => SalesImporter.Import(path));
            }
            catch (Exception ex)
            {
                SetBusy(false);
                MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (imp.BySku.Count == 0)
            {
                SetBusy(false);
                MessageBox.Show(this, "No valid rows found.\n\n" + imp.Summary(), "Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _import = imp;
            lblFile.Text = Path.GetFileName(path) + "  -  " + imp.Summary().Split('\n')[0].Trim();

            _loading = true;
            cboSku.DataSource = imp.BySku.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            cboSku.SelectedIndex = 0;
            UpdateAsOfFromSelectedSku();
            _loading = false;

            if (imp.RowsSkipped > 0)
                MessageBox.Show(this, imp.Summary(), "Import finished with problems", MessageBoxButtons.OK, MessageBoxIcon.Information);

            SetBusy(false);
            btnRun.Enabled = true;
            await RunForecastAsync();          // straight into the model and the graph
        }

        void UpdateAsOfFromSelectedSku()
        {
            if (_import == null || cboSku.SelectedItem == null) return;
            var rows = _import.BySku[(string)cboSku.SelectedItem];
            // default: the day of the last record, so the forecast starts right after the data ends
            dtpAsOf.Value = rows.Max(r => r.Date);
        }

        // ------------------------------------------------------------ model + graph

        async Task RunForecastAsync()
        {
            if (_import == null || cboSku.SelectedItem == null) return;

            ForecastRequest req;
            int holdout = (int)nudHoldout.Value;
            List<SalesRecord> sales = _import.BySku[(string)cboSku.SelectedItem];
            try
            {
                int horizon = (int)nudHorizon.Value;
                req = new ForecastRequest
                {
                    Granularity = (Granularity)cboGran.SelectedItem,
                    HorizonPeriods = horizon,
                    ServiceLevel = (double)nudService.Value / 100.0,
                    OnHandQty = (double)nudOnHand.Value,
                    OrderMultiple = (double)nudMultiple.Value,
                    PromoFactors = ParsePromo(horizon),
                    AsOfDate = dtpAsOf.Value.Date
                };
            }
            catch (FormatException ex)
            {
                MessageBox.Show(this, ex.Message, "Check the inputs", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true);
            try
            {
                RunOutput output = await Task.Run(() => ForecastRunner.Run(sales, req, holdout));
                ForecastChart.Populate(chart, output);
                txtResult.Text = Describe(output);
            }
            catch (Exception ex)
            {
                txtResult.Text = "Could not run the forecast: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        // "1.2" = same factor for every cover period, "1.2;1;1" = one factor per period
        double[] ParsePromo(int horizon)
        {
            string[] tokens = txtPromo.Text.Split(new[] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return null;

            var vals = new List<double>();
            foreach (string t in tokens)
            {
                double v;
                if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0)
                    throw new FormatException("Promo factor '" + t + "' is not a valid number. Use for example 1.2 or 1.2;1;1");
                vals.Add(v);
            }
            if (vals.Count == 1) return Enumerable.Repeat(vals[0], horizon).ToArray();
            return vals.ToArray();             // missing periods count as 1.0
        }

        static string Describe(RunOutput o)
        {
            ReplenishmentResult p = o.Plan;
            var sb = new StringBuilder();
            sb.AppendLine("Model            : " + p.Model);
            if (p.Note != null) sb.AppendLine("Note             : " + p.Note);
            sb.AppendLine(string.Format("History          : {0} periods, {1:yyyy-MM-dd} to {2:yyyy-MM-dd}",
                o.HistoryActual.Length, o.HistoryDates[0], o.HistoryDates[o.HistoryDates.Length - 1]));
            sb.AppendLine();
            foreach (var per in p.Periods)
                sb.AppendLine(string.Format("  {0:yyyy-MM-dd}   baseline {1,12:N1}   with promo {2,12:N1}", per.Start, per.Baseline, per.Adjusted));
            sb.AppendLine();
            sb.AppendLine(string.Format("Forecast demand  : {0:N1}", p.ForecastDemand));
            sb.AppendLine(string.Format("Safety stock     : {0:N1}", p.SafetyStock));
            sb.AppendLine(string.Format("Target stock     : {0:N1}", p.TargetStock));
            sb.AppendLine(string.Format("On hand          : {0:N1}", o.Request.OnHandQty));
            sb.AppendLine(string.Format("SUGGESTED ORDER  : {0:N0}", p.SuggestedQty));
            sb.AppendLine();
            if (o.Backtest != null)
                sb.AppendLine(string.Format("Backtest         : last {0} periods hidden, WAPE {1:P1} (model {2})",
                    o.Backtest.HoldoutDates.Length, o.Backtest.Wape, o.Backtest.Model));
            else
                sb.AppendLine("Backtest         : not shown. " + o.BacktestMessage);
            return sb.ToString();
        }

        // ------------------------------------------------------------ small UI helpers

        void SetBusy(bool busy)
        {
            UseWaitCursor = busy;
            btnImport.Enabled = !busy;
            btnRun.Enabled = !busy && _import != null;
        }

        static Control Field(string caption, Control c)
        {
            int w = Math.Max(c.Width, TextRenderer.MeasureText(caption, SystemFonts.DefaultFont).Width) + 14;
            var p = new Panel { Width = w, Height = 54 };
            p.Controls.Add(new Label { Text = caption, AutoSize = true, Left = 0, Top = 0 });
            c.Left = 0;
            c.Top = 20;
            p.Controls.Add(c);
            return p;
        }
    }
}
