using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ExcelDna.Integration;

namespace Neo.ExcelMcp.AddIn;

/// <summary>
/// The ONLY place that touches Excel's COM object model.
///
/// Rules of this class (do not break them):
///  1. Every COM access is wrapped in RunOnMainAsync — Excel COM is STA,
///     calling it from a thread-pool thread throws COMException RPC_E_WRONG_THREAD.
///  2. Every COM reference (dynamic x = obj.Foo) is released in a finally block
///     via Marshal.ReleaseComObject — otherwise Excel.exe stays zombied after close.
///  3. Never release ExcelDnaUtil.Application — Excel-DNA manages its lifetime.
///  4. Every public async method has a timeout — if Excel is busy (cell edit,
///     modal dialog, big recalc), queued macros wait until Excel is idle again.
/// </summary>
internal static class ExcelGateway
{
    /// <summary>
    /// Marshals <paramref name="work"/> onto Excel's main (STA) thread
    /// via ExcelAsyncUtil.QueueAsMacro and awaits the result.
    /// </summary>
    public static Task<T> RunOnMainAsync<T>(Func<T> work, int timeoutSeconds = 30)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() => tcs.TrySetException(new TimeoutException(
            $"Excel did not respond within {timeoutSeconds}s. " +
            "Excel may be busy (cell being edited, modal dialog open, or long calculation).")));

        ExcelAsyncUtil.QueueAsMacro(() =>
        {
            try
            {
                var result = work();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                try { cts.Dispose(); } catch { /* ignore */ }
            }
        });

        return tcs.Task;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Tool: excel_context
    // ────────────────────────────────────────────────────────────────────────

    public static Task<object> GetContextAsync() =>
        RunOnMainAsync<object>(() =>
        {
            object? appObj = ExcelDnaUtil.Application;
            if (appObj == null)
                return MakeEmptyContext("Excel application unavailable");

            dynamic app = appObj;
            dynamic? wb = null, ws = null, sel = null;
            dynamic? worksheets = null, selRows = null, selCols = null;

            try
            {
                wb = Try(() => app.ActiveWorkbook);
                ws = Try(() => app.ActiveSheet);
                sel = Try(() => app.Selection);

                if (wb == null)
                    return MakeEmptyContext("No active workbook");

                string workbookName = SafeString(() => (string)wb.Name);
                string sheetName = ws != null ? SafeString(() => (string)ws.Name) : "";
                string selectionAddress = sel != null ? SafeString(() => (string)sel.Address) : "";

                int rowsCount = 0;
                int colsCount = 0;
                if (sel != null)
                {
                    selRows = Try(() => sel.Rows);
                    selCols = Try(() => sel.Columns);
                    rowsCount = selRows != null ? SafeInt(() => (int)selRows.Count) : 0;
                    colsCount = selCols != null ? SafeInt(() => (int)selCols.Count) : 0;
                }

                var sheetNames = new List<string>();
                worksheets = Try(() => wb.Worksheets);
                if (worksheets != null)
                {
                    int count = SafeInt(() => (int)worksheets.Count);
                    for (int i = 1; i <= count; i++)
                    {
                        dynamic? item = null;
                        try
                        {
                            item = worksheets[i];
                            if (item != null)
                                sheetNames.Add(SafeString(() => (string)item.Name));
                        }
                        catch { /* skip one bad sheet, continue */ }
                        finally
                        {
                            Release(item);
                        }
                    }
                }

                return new
                {
                    workbook = workbookName,
                    activeSheet = sheetName,
                    selection = selectionAddress,
                    rows = rowsCount,
                    cols = colsCount,
                    sheets = sheetNames
                };
            }
            finally
            {
                Release(selRows);
                Release(selCols);
                Release(worksheets);
                Release(sel);
                Release(ws);
                Release(wb);
                // app is NOT released — Excel-DNA owns it
            }
        });

    // ────────────────────────────────────────────────────────────────────────
    //  Tool: excel_read
    // ────────────────────────────────────────────────────────────────────────

    public static Task<object> ReadRangeAsync(string? sheet, string? range) =>
        RunOnMainAsync<object>(() =>
        {
            object? appObj = ExcelDnaUtil.Application;
            if (appObj == null)
                return new { error = "Excel application unavailable" };

            dynamic app = appObj;
            dynamic? wb = null, ws = null, rng = null;

            try
            {
                wb = Try(() => app.ActiveWorkbook);
                if (wb == null)
                    return new { error = "No active workbook" };

                // Get the target sheet
                if (!string.IsNullOrWhiteSpace(sheet))
                {
                    dynamic? worksheets = null;
                    try
                    {
                        worksheets = wb.Worksheets;
                        ws = Try(() => worksheets[sheet]);
                    }
                    finally
                    {
                        Release(worksheets);
                    }
                    if (ws == null)
                        return new { error = $"Sheet '{sheet}' not found." };
                }
                else
                {
                    ws = Try(() => app.ActiveSheet);
                    if (ws == null)
                        return new { error = "No active sheet" };
                }

                // Get the range — if none specified, use current selection
                if (!string.IsNullOrWhiteSpace(range))
                {
                    rng = Try(() => ws.Range[range]);
                    if (rng == null)
                        return new { error = $"Range '{range}' is invalid." };
                }
                else
                {
                    rng = Try(() => app.Selection);
                    if (rng == null)
                        return new { error = "Nothing selected" };
                }

                // Read values — Value2 avoids Currency/Date COM marshalling issues
                dynamic? rows = null, cols = null;
                try
                {
                    rows = rng.Rows;
                    cols = rng.Columns;
                    int rowCount = (int)rows.Count;
                    int colCount = (int)cols.Count;

                    // Single cell → Value2 returns a scalar, not an array
                    if (rowCount == 1 && colCount == 1)
                    {
                        object? val = rng.Value2;
                        return new
                        {
                            sheet = SafeString(() => (string)ws.Name),
                            range = SafeString(() => (string)rng.Address),
                            rows = rowCount,
                            cols = colCount,
                            values = new List<List<object?>> { new() { val ?? "" } }
                        };
                    }

                    // Multi-cell → Value2 returns a 1-based object[,]
                    object[,] raw = rng.Value2;
                    var data = new List<List<object?>>();
                    for (int r = 1; r <= rowCount; r++)
                    {
                        var row = new List<object?>();
                        for (int c = 1; c <= colCount; c++)
                        {
                            row.Add(raw[r, c] ?? "");
                        }
                        data.Add(row);
                    }

                    return new
                    {
                        sheet = SafeString(() => (string)ws.Name),
                        range = SafeString(() => (string)rng.Address),
                        rows = rowCount,
                        cols = colCount,
                        values = data
                    };
                }
                finally
                {
                    Release(rows);
                    Release(cols);
                }
            }
            finally
            {
                Release(rng);
                Release(ws);
                Release(wb);
            }
        });

    // ────────────────────────────────────────────────────────────────────────
    //  Tool: excel_write
    // ────────────────────────────────────────────────────────────────────────

    public static Task<object> WriteRangeAsync(string? sheet, string range, List<List<object?>> values) =>
        RunOnMainAsync<object>(() =>
        {
            if (string.IsNullOrWhiteSpace(range))
                return new { error = "Range is required for write operations." };
            if (values == null || values.Count == 0)
                return new { error = "Values array is empty." };

            object? appObj = ExcelDnaUtil.Application;
            if (appObj == null)
                return new { error = "Excel application unavailable" };

            dynamic app = appObj;
            dynamic? wb = null, ws = null, rng = null;

            try
            {
                wb = Try(() => app.ActiveWorkbook);
                if (wb == null)
                    return new { error = "No active workbook" };

                if (!string.IsNullOrWhiteSpace(sheet))
                {
                    dynamic? worksheets = null;
                    try
                    {
                        worksheets = wb.Worksheets;
                        ws = Try(() => worksheets[sheet]);
                    }
                    finally { Release(worksheets); }
                    if (ws == null)
                        return new { error = $"Sheet '{sheet}' not found." };
                }
                else
                {
                    ws = Try(() => app.ActiveSheet);
                    if (ws == null)
                        return new { error = "No active sheet" };
                }

                rng = Try(() => ws.Range[range]);
                if (rng == null)
                    return new { error = $"Range '{range}' is invalid." };

                int rowCount = values.Count;
                int colCount = values.Max(r => r.Count);

                // Cap at 50k cells for safety
                if ((long)rowCount * colCount > 50_000)
                    return new { error = $"Write too large ({rowCount}x{colCount} = {(long)rowCount * colCount} cells). Maximum 50,000 cells per call." };

                // Build 1-based object[,] for COM
                var arr = new object[rowCount, colCount];
                for (int r = 0; r < rowCount; r++)
                {
                    for (int c = 0; c < colCount; c++)
                    {
                        arr[r, c] = (c < values[r].Count ? values[r][c] : null) ?? "";
                    }
                }

                // Resize range to match data dimensions
                dynamic? topLeft = null, resized = null;
                try
                {
                    topLeft = rng.Cells[1, 1];
                    resized = topLeft.Resize[rowCount, colCount];
                    resized.Value2 = arr;

                    return new
                    {
                        success = true,
                        sheet = SafeString(() => (string)ws.Name),
                        range = SafeString(() => (string)resized.Address),
                        rows = rowCount,
                        cols = colCount,
                        cellsWritten = rowCount * colCount
                    };
                }
                finally
                {
                    Release(resized);
                    Release(topLeft);
                }
            }
            finally
            {
                Release(rng);
                Release(ws);
                Release(wb);
            }
        });

    // ────────────────────────────────────────────────────────────────────────
    //  Tool: excel_tables
    // ────────────────────────────────────────────────────────────────────────

    public static Task<object> ListTablesAsync() =>
        RunOnMainAsync<object>(() =>
        {
            object? appObj = ExcelDnaUtil.Application;
            if (appObj == null)
                return new { error = "Excel application unavailable" };

            dynamic app = appObj;
            dynamic? wb = null, worksheets = null;

            try
            {
                wb = Try(() => app.ActiveWorkbook);
                if (wb == null)
                    return new { error = "No active workbook" };

                worksheets = wb.Worksheets;
                int sheetCount = SafeInt(() => (int)worksheets.Count);

                var tables = new List<object>();

                for (int si = 1; si <= sheetCount; si++)
                {
                    dynamic? ws = null, listObjects = null;
                    try
                    {
                        ws = worksheets[si];
                        string sheetName = SafeString(() => (string)ws.Name);
                        listObjects = Try(() => ws.ListObjects);
                        if (listObjects == null) continue;

                        int loCount = SafeInt(() => (int)listObjects.Count);
                        for (int li = 1; li <= loCount; li++)
                        {
                            dynamic? lo = null, rng = null, headerRow = null;
                            try
                            {
                                lo = listObjects[li];
                                rng = Try(() => lo.Range);
                                headerRow = Try(() => lo.HeaderRowRange);

                                string tableName = SafeString(() => (string)lo.Name);
                                string tableRange = rng != null ? SafeString(() => (string)rng.Address) : "";
                                int rowCount = SafeInt(() => (int)lo.ListRows.Count);

                                // Read header names
                                var headers = new List<string>();
                                if (headerRow != null)
                                {
                                    dynamic? hCols = null;
                                    try
                                    {
                                        hCols = headerRow.Columns;
                                        int hCount = SafeInt(() => (int)hCols.Count);
                                        for (int hi = 1; hi <= hCount; hi++)
                                        {
                                            dynamic? hCell = null;
                                            try
                                            {
                                                hCell = hCols[hi];
                                                headers.Add(SafeString(() => Convert.ToString(hCell.Value2) ?? ""));
                                            }
                                            finally { Release(hCell); }
                                        }
                                    }
                                    finally { Release(hCols); }
                                }

                                tables.Add(new
                                {
                                    name = tableName,
                                    sheet = sheetName,
                                    range = tableRange,
                                    rows = rowCount,
                                    headers
                                });
                            }
                            finally
                            {
                                Release(headerRow);
                                Release(rng);
                                Release(lo);
                            }
                        }
                    }
                    finally
                    {
                        Release(listObjects);
                        Release(ws);
                    }
                }

                return new { tables, count = tables.Count };
            }
            finally
            {
                Release(worksheets);
                Release(wb);
            }
        });

    // ────────────────────────────────────────────────────────────────────────
    //  Tool: excel_write_formula
    // ────────────────────────────────────────────────────────────────────────

    public static Task<object> WriteFormulaAsync(string? sheet, string range, List<List<object?>> formulas) =>
        RunOnMainAsync<object>(() =>
        {
            if (string.IsNullOrWhiteSpace(range))
                return new { error = "Range is required." };
            if (formulas == null || formulas.Count == 0)
                return new { error = "Formulas array is empty." };

            dynamic app = (dynamic)ExcelDnaUtil.Application!;
            dynamic? wb = null, ws = null, rng = null;

            try
            {
                wb = Try(() => app.ActiveWorkbook);
                if (wb == null) return new { error = "No active workbook" };

                ws = ResolveSheet(app, wb, sheet, out string? err);
                if (ws == null) return new { error = err };

                rng = Try(() => ws.Range[range]);
                if (rng == null) return new { error = $"Range '{range}' is invalid." };

                int rowCount = formulas.Count;
                int colCount = formulas.Max(r => r.Count);

                var arr = new object[rowCount, colCount];
                for (int r = 0; r < rowCount; r++)
                    for (int c = 0; c < colCount; c++)
                        arr[r, c] = (c < formulas[r].Count ? formulas[r][c] : null) ?? "";

                dynamic? topLeft = null, resized = null;
                try
                {
                    topLeft = rng.Cells[1, 1];
                    resized = topLeft.Resize[rowCount, colCount];
                    resized.Formula = arr; // Formula instead of Value2

                    return new
                    {
                        success = true,
                        sheet = SafeString(() => (string)ws.Name),
                        range = SafeString(() => (string)resized.Address),
                        rows = rowCount,
                        cols = colCount
                    };
                }
                finally { Release(resized); Release(topLeft); }
            }
            finally { Release(rng); Release(ws); Release(wb); }
        });

    // ────────────────────────────────────────────────────────────────────────
    //  Tool: excel_read_table
    // ────────────────────────────────────────────────────────────────────────

    public static Task<object> ReadTableAsync(string tableName) =>
        RunOnMainAsync<object>(() =>
        {
            if (string.IsNullOrWhiteSpace(tableName))
                return new { error = "Table name is required." };

            dynamic app = (dynamic)ExcelDnaUtil.Application!;
            dynamic? wb = null, worksheets = null;

            try
            {
                wb = Try(() => app.ActiveWorkbook);
                if (wb == null) return new { error = "No active workbook" };

                worksheets = wb.Worksheets;
                int sheetCount = SafeInt(() => (int)worksheets.Count);

                for (int si = 1; si <= sheetCount; si++)
                {
                    dynamic? ws = null, listObjects = null;
                    try
                    {
                        ws = worksheets[si];
                        listObjects = Try(() => ws.ListObjects);
                        if (listObjects == null) continue;

                        int loCount = SafeInt(() => (int)listObjects.Count);
                        for (int li = 1; li <= loCount; li++)
                        {
                            dynamic? lo = null, headerRange = null, dataRange = null;
                            try
                            {
                                lo = listObjects[li];
                                string name = SafeString(() => (string)lo.Name);
                                if (!name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                // Read headers
                                headerRange = Try(() => lo.HeaderRowRange);
                                var headers = new List<string>();
                                if (headerRange != null)
                                {
                                    dynamic? hCols = null;
                                    try
                                    {
                                        hCols = headerRange.Columns;
                                        int hCount = SafeInt(() => (int)hCols.Count);
                                        for (int hi = 1; hi <= hCount; hi++)
                                        {
                                            dynamic? hCell = null;
                                            try { hCell = hCols[hi]; headers.Add(SafeString(() => Convert.ToString(hCell.Value2) ?? "")); }
                                            finally { Release(hCell); }
                                        }
                                    }
                                    finally { Release(hCols); }
                                }

                                // Read data body
                                dataRange = Try(() => lo.DataBodyRange);
                                var records = new List<Dictionary<string, object?>>();

                                if (dataRange != null)
                                {
                                    dynamic? dRows = null, dCols = null;
                                    try
                                    {
                                        dRows = dataRange.Rows;
                                        dCols = dataRange.Columns;
                                        int rowCount = SafeInt(() => (int)dRows.Count);
                                        int colCount = SafeInt(() => (int)dCols.Count);

                                        object[,]? raw = rowCount == 1 && colCount == 1 ? null : Try(() => (object[,])dataRange.Value2);

                                        for (int r = 1; r <= rowCount; r++)
                                        {
                                            var record = new Dictionary<string, object?>();
                                            for (int c = 1; c <= colCount && c <= headers.Count; c++)
                                            {
                                                object? val;
                                                if (raw != null) { val = raw[r, c]; }
                                                else
                                                {
                                                    dynamic? cell = null;
                                                    try { cell = dataRange.Cells[r, c]; val = cell?.Value2; }
                                                    finally { Release(cell); }
                                                }
                                                record[headers[c - 1]] = val ?? "";
                                            }
                                            records.Add(record);
                                        }
                                    }
                                    finally { Release(dRows); Release(dCols); }
                                }

                                return new
                                {
                                    table = name,
                                    sheet = SafeString(() => (string)ws.Name),
                                    headers,
                                    rows = records.Count,
                                    records
                                };
                            }
                            finally { Release(dataRange); Release(headerRange); Release(lo); }
                        }
                    }
                    finally { Release(listObjects); Release(ws); }
                }

                return new { error = $"Table '{tableName}' not found." };
            }
            finally { Release(worksheets); Release(wb); }
        });

    // ────────────────────────────────────────────────────────────────────────
    //  Tool: excel_create_sheet
    // ────────────────────────────────────────────────────────────────────────

    public static Task<object> CreateSheetAsync(string name) =>
        RunOnMainAsync<object>(() =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return new { error = "Sheet name is required." };

            dynamic app = (dynamic)ExcelDnaUtil.Application!;
            dynamic? wb = null, worksheets = null, newSheet = null;

            try
            {
                wb = Try(() => app.ActiveWorkbook);
                if (wb == null) return new { error = "No active workbook" };

                worksheets = wb.Worksheets;
                // Check if name already exists
                int count = SafeInt(() => (int)worksheets.Count);
                for (int i = 1; i <= count; i++)
                {
                    dynamic? existing = null;
                    try
                    {
                        existing = worksheets[i];
                        if (SafeString(() => (string)existing.Name).Equals(name, StringComparison.OrdinalIgnoreCase))
                            return new { error = $"Sheet '{name}' already exists." };
                    }
                    finally { Release(existing); }
                }

                newSheet = worksheets.Add(After: worksheets[count]);
                newSheet.Name = name;

                return new
                {
                    success = true,
                    sheet = name,
                    totalSheets = count + 1
                };
            }
            finally { Release(newSheet); Release(worksheets); Release(wb); }
        });

    // ────────────────────────────────────────────────────────────────────────
    //  Tool: excel_format
    // ────────────────────────────────────────────────────────────────────────

    public static Task<object> FormatRangeAsync(
        string? sheet, string range, bool? bold, bool? italic,
        double? fontSize, string? fontColor, string? bgColor,
        string? numberFormat, string? horizontalAlignment, bool? borders) =>
        RunOnMainAsync<object>(() =>
        {
            if (string.IsNullOrWhiteSpace(range))
                return new { error = "Range is required." };

            dynamic app = (dynamic)ExcelDnaUtil.Application!;
            dynamic? wb = null, ws = null, rng = null;

            try
            {
                wb = Try(() => app.ActiveWorkbook);
                if (wb == null) return new { error = "No active workbook" };

                ws = ResolveSheet(app, wb, sheet, out string? err);
                if (ws == null) return new { error = err };

                rng = Try(() => ws.Range[range]);
                if (rng == null) return new { error = $"Range '{range}' is invalid." };

                var applied = new List<string>();

                dynamic? font = null, interior = null;
                try
                {
                    font = rng.Font;
                    interior = rng.Interior;

                    if (bold.HasValue) { font.Bold = bold.Value; applied.Add($"bold={bold.Value}"); }
                    if (italic.HasValue) { font.Italic = italic.Value; applied.Add($"italic={italic.Value}"); }
                    if (fontSize.HasValue) { font.Size = fontSize.Value; applied.Add($"fontSize={fontSize.Value}"); }
                    if (fontColor != null) { font.Color = ParseColor(fontColor); applied.Add($"fontColor={fontColor}"); }
                    if (bgColor != null) { interior.Color = ParseColor(bgColor); applied.Add($"bgColor={bgColor}"); }
                    if (numberFormat != null) { rng.NumberFormat = numberFormat; applied.Add($"numberFormat={numberFormat}"); }

                    if (horizontalAlignment != null)
                    {
                        int align = horizontalAlignment.ToLowerInvariant() switch
                        {
                            "left" => -4131,    // xlLeft
                            "center" => -4108,  // xlCenter
                            "right" => -4152,   // xlRight
                            _ => -4108
                        };
                        rng.HorizontalAlignment = align;
                        applied.Add($"align={horizontalAlignment}");
                    }

                    if (borders == true)
                    {
                        // Thin borders on all edges (xlThin = 2, xlContinuous = 1)
                        for (int edge = 7; edge <= 10; edge++) // xlEdgeLeft=7..xlEdgeRight=10
                        {
                            dynamic? border = null;
                            try { border = rng.Borders[edge]; border.LineStyle = 1; border.Weight = 2; }
                            finally { Release(border); }
                        }
                        // Inner borders
                        dynamic? bH = null, bV = null;
                        try
                        {
                            bH = Try(() => rng.Borders[12]); // xlInsideHorizontal
                            if (bH != null) { bH.LineStyle = 1; bH.Weight = 2; }
                            bV = Try(() => rng.Borders[11]); // xlInsideVertical
                            if (bV != null) { bV.LineStyle = 1; bV.Weight = 2; }
                        }
                        finally { Release(bH); Release(bV); }
                        applied.Add("borders=true");
                    }
                }
                finally { Release(interior); Release(font); }

                return new
                {
                    success = true,
                    sheet = SafeString(() => (string)ws.Name),
                    range = SafeString(() => (string)rng.Address),
                    applied
                };
            }
            finally { Release(rng); Release(ws); Release(wb); }
        });

    // ────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Resolves a worksheet by name, or returns the active sheet if name is null.</summary>
    private static dynamic? ResolveSheet(dynamic app, dynamic wb, string? sheetName, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(sheetName))
        {
            dynamic? worksheets = null;
            try
            {
                worksheets = wb.Worksheets;
                var ws = Try(() => worksheets[sheetName]);
                if (ws == null) error = $"Sheet '{sheetName}' not found.";
                return ws;
            }
            finally { Release(worksheets); }
        }
        else
        {
            var ws = Try(() => app.ActiveSheet);
            if (ws == null) error = "No active sheet";
            return ws;
        }
    }

    /// <summary>Parses a color from HTML hex (#RRGGBB) or named color to Excel OLE RGB int.</summary>
    private static int ParseColor(string color)
    {
        if (string.IsNullOrWhiteSpace(color)) return 0;

        // Named colors
        var oleColor = color.Trim().ToLowerInvariant() switch
        {
            "red" => (255, 0, 0),
            "green" => (0, 128, 0),
            "blue" => (0, 0, 255),
            "yellow" => (255, 255, 0),
            "orange" => (255, 165, 0),
            "white" => (255, 255, 255),
            "black" => (0, 0, 0),
            "gray" or "grey" => (128, 128, 128),
            "purple" => (128, 0, 128),
            _ => (-1, -1, -1)
        };
        if (oleColor.Item1 >= 0)
            return oleColor.Item1 | (oleColor.Item2 << 8) | (oleColor.Item3 << 16);

        // HTML hex: #RRGGBB
        var hex = color.TrimStart('#');
        if (hex.Length == 6)
        {
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return r | (g << 8) | (b << 16);
        }

        return 0;
    }

    private static object MakeEmptyContext(string error) => new
    {
        workbook = "",
        activeSheet = "",
        selection = "",
        rows = 0,
        cols = 0,
        sheets = Array.Empty<string>(),
        error
    };

    private static dynamic? Try(Func<dynamic?> f)
    {
        try { return f(); }
        catch { return null; }
    }

    private static string SafeString(Func<string> f)
    {
        try { return f() ?? ""; }
        catch { return ""; }
    }

    private static int SafeInt(Func<int> f)
    {
        try { return f(); }
        catch { return 0; }
    }

    private static void Release(object? comObj)
    {
        if (comObj == null) return;
        try { Marshal.ReleaseComObject(comObj); }
        catch { /* ignore — best effort */ }
    }
}
