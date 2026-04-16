using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Neo.ExcelMcp.Bridge;

/// <summary>
/// MCP tool surface exposed to Claude. Each method forwards to the add-in
/// running inside Excel via the PipeClient. The tool result is the raw
/// JSON body returned by the add-in — Claude parses it on the other side.
/// </summary>
[McpServerToolType]
public sealed class ExcelTools
{
    [McpServerTool(Name = "excel_context")]
    [Description(
        "Returns the current state of the active Excel workbook as JSON: " +
        "workbook file name, active sheet name, current selection address " +
        "(e.g. 'B2:D10'), row/column counts of the selection, and the list " +
        "of all sheets in the workbook. " +
        "Requires Excel to be running with the 'Neo for Excel' add-in loaded. " +
        "If Excel is busy (a cell is being edited, a modal dialog is open, or " +
        "a long calculation is running), the call may time out — retry after a moment.")]
    public static async Task<string> GetContext(PipeClient pipe)
    {
        try
        {
            return await pipe.CallAsync("excel_context", new { });
        }
        catch (Exception ex)
        {
            return
                "ERROR: Could not reach Excel.\n\n" +
                $"{ex.Message}\n\n" +
                "Make sure Excel is running and the 'Neo for Excel' add-in is enabled " +
                "(File → Options → Add-ins → Manage: Excel Add-ins → Go).";
        }
    }

    [McpServerTool(Name = "excel_read")]
    [Description(
        "Reads cell values from the active Excel workbook and returns them as a 2D array. " +
        "If 'range' is omitted, reads the current user selection. " +
        "If 'sheet' is omitted, uses the active sheet. " +
        "Examples: excel_read(sheet='Sheet1', range='A1:D10'), excel_read() for current selection.")]
    public static async Task<string> ReadRange(
        PipeClient pipe,
        [Description("Sheet name, e.g. 'Sheet1'. Omit to use the active sheet.")] string? sheet = null,
        [Description("Cell range in A1 notation, e.g. 'A1:D10' or 'B2'. Omit to read the current selection.")] string? range = null)
    {
        try
        {
            return await pipe.CallAsync("excel_read", new { sheet, range });
        }
        catch (Exception ex)
        {
            return $"ERROR: Could not reach Excel.\n\n{ex.Message}";
        }
    }

    [McpServerTool(Name = "excel_write")]
    [Description(
        "Writes values to a range in the active Excel workbook. " +
        "Values are a 2D array (rows × columns). The range is resized to fit the data. " +
        "If 'sheet' is omitted, writes to the active sheet. " +
        "Maximum 50,000 cells per call. " +
        "Example: excel_write(range='A1', values=[['Name','Age'],['Alice',30],['Bob',25]])")]
    public static async Task<string> WriteRange(
        PipeClient pipe,
        [Description("Target range start, e.g. 'A1' or 'B2:D10'. The range is auto-resized to match the values array.")] string range,
        [Description("2D array of values to write. Each inner array is one row. " +
            "Example: [['Header1','Header2'],[1,2],[3,4]]")] object[][] values,
        [Description("Sheet name, e.g. 'Sheet1'. Omit to use the active sheet.")] string? sheet = null)
    {
        try
        {
            return await pipe.CallAsync("excel_write", new { sheet, range, values });
        }
        catch (Exception ex)
        {
            return $"ERROR: Could not reach Excel.\n\n{ex.Message}";
        }
    }

    [McpServerTool(Name = "excel_tables")]
    [Description(
        "Lists all Excel Tables (ListObjects) across all sheets in the active workbook. " +
        "Returns each table's name, sheet, range, row count, and column headers. " +
        "Use this to discover structured data before reading with excel_read.")]
    public static async Task<string> ListTables(PipeClient pipe)
    {
        try
        {
            return await pipe.CallAsync("excel_tables", new { });
        }
        catch (Exception ex)
        {
            return $"ERROR: Could not reach Excel.\n\n{ex.Message}";
        }
    }

    [McpServerTool(Name = "excel_write_formula")]
    [Description(
        "Writes formulas to cells in the active Excel workbook. " +
        "Use English formula names (SUM, AVERAGE, IF, VLOOKUP) — Excel translates them automatically. " +
        "Example: excel_write_formula(range='D2', formulas=[['=SUM(A2:C2)'],['=SUM(A3:C3)']])")]
    public static async Task<string> WriteFormula(
        PipeClient pipe,
        [Description("Target range start, e.g. 'D2'.")] string range,
        [Description("2D array of formula strings. Each formula starts with '='. " +
            "Example: [['=SUM(A1:C1)'],['=AVERAGE(A2:C2)']]")] object[][] formulas,
        [Description("Sheet name. Omit to use the active sheet.")] string? sheet = null)
    {
        try
        {
            return await pipe.CallAsync("excel_write_formula", new { sheet, range, formulas });
        }
        catch (Exception ex)
        {
            return $"ERROR: Could not reach Excel.\n\n{ex.Message}";
        }
    }

    [McpServerTool(Name = "excel_read_table")]
    [Description(
        "Reads an Excel Table (ListObject) by name and returns it as an array of records " +
        "(each record is a dictionary of column header → cell value). " +
        "Use excel_tables first to discover available table names.")]
    public static async Task<string> ReadTable(
        PipeClient pipe,
        [Description("The name of the Excel Table (ListObject) to read.")] string name)
    {
        try
        {
            return await pipe.CallAsync("excel_read_table", new { name });
        }
        catch (Exception ex)
        {
            return $"ERROR: Could not reach Excel.\n\n{ex.Message}";
        }
    }

    [McpServerTool(Name = "excel_create_sheet")]
    [Description(
        "Creates a new worksheet in the active workbook. " +
        "The new sheet is added after the last existing sheet.")]
    public static async Task<string> CreateSheet(
        PipeClient pipe,
        [Description("Name for the new sheet.")] string name)
    {
        try
        {
            return await pipe.CallAsync("excel_create_sheet", new { name });
        }
        catch (Exception ex)
        {
            return $"ERROR: Could not reach Excel.\n\n{ex.Message}";
        }
    }

    [McpServerTool(Name = "excel_format")]
    [Description(
        "Applies formatting to a cell range without changing values. " +
        "Supports bold, italic, font size, font color, background color, number format, " +
        "alignment, and borders. Colors can be HTML hex (#FF0000) or names (red, blue, green). " +
        "Example: excel_format(range='A1:C1', bold=true, bgColor='#4472C4', fontColor='white')")]
    public static async Task<string> FormatRange(
        PipeClient pipe,
        [Description("Cell range to format, e.g. 'A1:C5'.")] string range,
        [Description("Make text bold.")] bool? bold = null,
        [Description("Make text italic.")] bool? italic = null,
        [Description("Font size in points, e.g. 14.")] double? fontSize = null,
        [Description("Font color: HTML hex (#FF0000) or name (red, blue, green, white, black, orange, purple, gray).")] string? fontColor = null,
        [Description("Background fill color: HTML hex or name.")] string? bgColor = null,
        [Description("Number format string, e.g. '#,##0.00' for thousands, '0%' for percent, 'dd/mm/yyyy' for date.")] string? numberFormat = null,
        [Description("Horizontal alignment: 'left', 'center', or 'right'.")] string? horizontalAlignment = null,
        [Description("Add thin borders around all cells in the range.")] bool? borders = null,
        [Description("Sheet name. Omit to use the active sheet.")] string? sheet = null)
    {
        try
        {
            return await pipe.CallAsync("excel_format", new
            {
                sheet, range, bold, italic, fontSize,
                fontColor, bgColor, numberFormat, horizontalAlignment, borders
            });
        }
        catch (Exception ex)
        {
            return $"ERROR: Could not reach Excel.\n\n{ex.Message}";
        }
    }
}
