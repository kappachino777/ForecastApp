// Sales data importer: .csv / .txt / .tsv / .xlsx, no external libraries.
// Target: .NET Framework 4.7.2, C# 7.3.
// Project references needed: System.IO.Compression, System.Xml.Linq.
//
// Expected columns (header row required, order does not matter, extra columns are ignored):
//   Date          required   2026-11-14   (ISO format recommended)
//   Qty           required   120          (any unit, same unit for the whole file)
//   ProductCode   optional   SKU-001      (omit the column for a single-product file)
//
// Usage:
//   var imp = SalesImporter.Import(@"C:\data\sales.xlsx");
//   MessageBox.Show(imp.Summary());
//   var rows = imp.BySku["SKU-001"];     // List<SalesRecord>, feed to ReplenishmentPlanner.Plan(...)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ReplenishmentForecast
{
    public class ImportOptions
    {
        public string DateFormat;                                   // e.g. "dd/MM/yyyy"; null = auto-detect
        public CultureInfo DateCulture = CultureInfo.CurrentCulture; // used only for non-ISO text dates
        public string DefaultSku = "(all)";                          // key used when there is no ProductCode column
        public int MaxProblemsKept = 50;
    }

    public class ImportResult
    {
        public Dictionary<string, List<SalesRecord>> BySku =
            new Dictionary<string, List<SalesRecord>>(StringComparer.OrdinalIgnoreCase);
        public List<string> Problems = new List<string>();
        public int ProblemsNotListed;
        public int RowsRead, RowsImported, RowsSkipped;
        public DateTime? MinDate, MaxDate;

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("Imported {0} of {1} rows, {2} product(s)", RowsImported, RowsRead, BySku.Count);
            if (MinDate.HasValue) sb.AppendFormat(", {0:yyyy-MM-dd} to {1:yyyy-MM-dd}", MinDate, MaxDate);
            if (RowsSkipped > 0) sb.AppendFormat(", {0} row(s) skipped", RowsSkipped);
            foreach (var p in Problems.Take(10)) sb.AppendLine().Append("  - " + p);
            int more = Problems.Count - 10 + ProblemsNotListed;
            if (more > 0) sb.AppendLine().AppendFormat("  ... and {0} more", more);
            return sb.ToString();
        }
    }

    public static class SalesImporter
    {
        static readonly string[] DateAliases = { "date", "transactiondate", "salesdate", "trxdate", "invoicedate" };
        static readonly string[] QtyAliases = { "qty", "quantity", "salesqty", "sales", "units" };
        static readonly string[] SkuAliases = { "productcode", "product", "sku", "item", "itemcode", "productid" };

        // ------------------------------------------------------------ main entry

        public static ImportResult Import(string path, ImportOptions opt = null)
        {
            opt = opt ?? new ImportOptions();
            List<string[]> table = ReadTable(path);

            int h = table.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
            if (h < 0) throw new InvalidDataException("The file is empty.");

            string[] head = table[h];
            int cDate = FindColumn(head, DateAliases);
            int cQty = FindColumn(head, QtyAliases);
            int cSku = FindColumn(head, SkuAliases);
            if (cDate < 0 || cQty < 0)
                throw new InvalidDataException("Required columns not found. Need 'Date' and 'Qty'. Found: "
                                               + string.Join(", ", head));

            var res = new ImportResult();
            for (int i = h + 1; i < table.Count; i++)
            {
                string[] r = table[i];
                if (r.All(c => string.IsNullOrWhiteSpace(c))) continue;      // blank line
                res.RowsRead++;
                int rowNo = i + 1;

                DateTime date;
                string dText = Cell(r, cDate);
                if (!TryParseDate(dText, opt, out date))
                {
                    Skip(res, opt, string.Format("Row {0}: invalid date '{1}'", rowNo, dText));
                    continue;
                }

                double qty;
                string qText = Cell(r, cQty);
                if (!TryParseQty(qText, out qty))
                {
                    Skip(res, opt, string.Format("Row {0}: invalid qty '{1}'", rowNo, qText));
                    continue;
                }

                string sku = opt.DefaultSku;
                if (cSku >= 0)
                {
                    sku = Cell(r, cSku).Trim();
                    if (sku.Length == 0)
                    {
                        Skip(res, opt, string.Format("Row {0}: empty product code", rowNo));
                        continue;
                    }
                }

                if (qty < 0) Note(res, opt, string.Format("Row {0}: negative qty {1} kept (treated as a return)", rowNo, qty));

                List<SalesRecord> list;
                if (!res.BySku.TryGetValue(sku, out list)) { list = new List<SalesRecord>(); res.BySku[sku] = list; }
                list.Add(new SalesRecord { Date = date.Date, Qty = qty });

                res.RowsImported++;
                if (!res.MinDate.HasValue || date < res.MinDate) res.MinDate = date.Date;
                if (!res.MaxDate.HasValue || date > res.MaxDate) res.MaxDate = date.Date;
            }
            return res;
        }

        static void Skip(ImportResult res, ImportOptions opt, string msg)
        {
            res.RowsSkipped++;
            Note(res, opt, msg);
        }

        static void Note(ImportResult res, ImportOptions opt, string msg)
        {
            if (res.Problems.Count < opt.MaxProblemsKept) res.Problems.Add(msg);
            else res.ProblemsNotListed++;
        }

        // ------------------------------------------------------------ parsing helpers

        static string Norm(string s)
        {
            return (s ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "").Replace(".", "");
        }

        static int FindColumn(string[] head, string[] aliases)
        {
            for (int i = 0; i < head.Length; i++)
                if (aliases.Contains(Norm(head[i]))) return i;
            return -1;
        }

        static string Cell(string[] r, int idx)
        {
            return idx < r.Length ? (r[idx] ?? "") : "";
        }

        static bool TryParseDate(string text, ImportOptions opt, out DateTime d)
        {
            string s = (text ?? "").Trim();
            d = default(DateTime);
            if (s.Length == 0) return false;

            if (opt.DateFormat != null)
                return DateTime.TryParseExact(s, opt.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

            string[] iso = { "yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd", "yyyy-MM-dd HH:mm:ss",
                             "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss" };
            if (DateTime.TryParseExact(s, iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return true;

            // Excel stores dates as serial numbers (about 1954 to 2119 in this range)
            double serial;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out serial)
                && serial > 20000 && serial < 80000)
            {
                d = DateTime.FromOADate(serial);
                return true;
            }

            // anything else is culture dependent (dd/MM vs MM/dd), so set ImportOptions.DateFormat if unsure
            return DateTime.TryParse(s, opt.DateCulture, DateTimeStyles.None, out d);
        }

        // Handles 1234.5  1,234.5  1.234,5  12,5  (comma-only values with 3 digits after it count as thousands)
        static bool TryParseQty(string text, out double q)
        {
            q = 0;
            string s = (text ?? "").Trim().Replace(" ", "");
            if (s.Length == 0) return false;

            int c = s.LastIndexOf(','), p = s.LastIndexOf('.');
            if (c >= 0 && p >= 0)
                s = c > p ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
            else if (c >= 0)
                s = Regex.IsMatch(s, @"^-?\d{1,3}(,\d{3})+$") ? s.Replace(",", "") : s.Replace(',', '.');

            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out q);
        }

        // ------------------------------------------------------------ file readers (return raw cell text)

        static List<string[]> ReadTable(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".csv":
                case ".txt":
                case ".tsv":
                    return ReadDelimited(path);
                case ".xlsx":
                case ".xlsm":
                    return ReadXlsx(path);
                case ".xls":
                    throw new NotSupportedException("Old .xls files are not supported. Save the file as .xlsx or .csv.");
                default:
                    throw new NotSupportedException("Unsupported file type: " + ext);
            }
        }

        // ---- CSV / TXT: auto-detects , ; tab | and handles "quoted, fields" and "" escapes
        static List<string[]> ReadDelimited(string path)
        {
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                text = sr.ReadToEnd();

            char delim = DetectDelimiter(text);
            var rows = new List<string[]>();
            var row = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(ch);
                }
                else if (ch == '"') inQuotes = true;
                else if (ch == delim) { row.Add(sb.ToString()); sb.Clear(); }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(sb.ToString()); sb.Clear();
                    rows.Add(row.ToArray()); row.Clear();
                }
                else sb.Append(ch);
            }
            if (sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString()); rows.Add(row.ToArray()); }
            return rows;
        }

        static char DetectDelimiter(string text)
        {
            string line = "";
            using (var sr = new StringReader(text))
            {
                string l;
                while ((l = sr.ReadLine()) != null && l.Trim().Length == 0) { }
                if (l != null) line = l;
            }

            char[] candidates = { ',', ';', '\t', '|' };
            char best = ',';
            int bestCount = 0;
            foreach (char cand in candidates)
            {
                int count = 0;
                bool q = false;
                foreach (char ch in line)
                {
                    if (ch == '"') q = !q;
                    else if (ch == cand && !q) count++;
                }
                if (count > bestCount) { bestCount = count; best = cand; }
            }
            return best;
        }

        // ---- XLSX (first worksheet only), read straight from the zip/XML so no Excel or library is needed
        static List<string[]> ReadXlsx(string path)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                // shared strings table
                var shared = new List<string>();
                var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
                if (ssEntry != null)
                    using (var st = ssEntry.Open())
                        foreach (var si in XDocument.Load(st).Root.Elements(ns + "si"))
                            shared.Add(string.Concat(si.Descendants(ns + "t").Select(x => x.Value)));

                // locate the first sheet's xml file
                string target;
                using (var st = zip.GetEntry("xl/workbook.xml").Open())
                {
                    var first = XDocument.Load(st).Root.Element(ns + "sheets").Elements(ns + "sheet").First();
                    string rid = (string)first.Attribute(rel + "id");
                    using (var rs = zip.GetEntry("xl/_rels/workbook.xml.rels").Open())
                    {
                        var r = XDocument.Load(rs).Root.Elements(pkg + "Relationship")
                                         .First(x => (string)x.Attribute("Id") == rid);
                        target = (string)r.Attribute("Target");
                    }
                }
                target = target.TrimStart('/');
                string sheetPath = target.StartsWith("xl/") ? target : "xl/" + target;

                // stream the sheet row by row (keeps memory low on big files)
                var rows = new List<string[]>();
                using (var st = zip.GetEntry(sheetPath).Open())
                using (var xr = XmlReader.Create(st))
                {
                    xr.MoveToContent();
                    while (!xr.EOF)
                    {
                        if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "row")
                        {
                            var row = (XElement)XNode.ReadFrom(xr);
                            int rowNo = (int?)row.Attribute("r") ?? rows.Count + 1;
                            while (rows.Count < rowNo - 1) rows.Add(new string[0]);    // keep Excel row numbers

                            var cells = new List<string>();
                            foreach (var c in row.Elements(ns + "c"))
                            {
                                string cref = (string)c.Attribute("r");
                                int col = cref != null ? ColumnIndex(cref) : cells.Count;
                                while (cells.Count < col) cells.Add("");
                                cells.Add(CellText(c, ns, shared));
                            }
                            rows.Add(cells.ToArray());
                        }
                        else xr.Read();
                    }
                }
                return rows;
            }
        }

        static string CellText(XElement c, XNamespace ns, List<string> shared)
        {
            string t = (string)c.Attribute("t");
            if (t == "inlineStr") return string.Concat(c.Descendants(ns + "t").Select(x => x.Value));
            string v = (string)c.Element(ns + "v");
            if (v == null) return "";
            if (t == "s")
            {
                int idx;
                return int.TryParse(v, out idx) && idx < shared.Count ? shared[idx] : "";
            }
            return v;     // numbers (dates arrive as serial numbers), formula strings, booleans
        }

        static int ColumnIndex(string cellRef)
        {
            int idx = 0;
            foreach (char ch in cellRef)
            {
                if (!char.IsLetter(ch)) break;
                idx = idx * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }
            return idx - 1;
        }
    }
}
