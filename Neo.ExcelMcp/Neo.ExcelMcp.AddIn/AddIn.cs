using System;
using ExcelDna.Integration;

namespace Neo.ExcelMcp.AddIn;

/// <summary>
/// Excel-DNA entry point. Called by Excel when the add-in is loaded / unloaded.
/// AutoOpen runs on Excel's main (STA) thread — we start a background task for
/// the pipe server so we do not block Excel's startup.
/// </summary>
public sealed class AddIn : IExcelAddIn
{
    private PipeServer? _server;

    public void AutoOpen()
    {
        try
        {
            Log.Info($"AutoOpen: log at {Log.LogPath}");
            _server = new PipeServer("neo-excel-test");
            _server.Start();
            Log.Info(@"PipeServer started on \\.\pipe\neo-excel-test");
        }
        catch (Exception ex)
        {
            Log.Error($"AutoOpen failed: {ex}");
        }
    }

    public void AutoClose()
    {
        try
        {
            Log.Info("AutoClose: stopping PipeServer");
            _server?.Stop();
            _server = null;
            Log.Info("AutoClose done");
        }
        catch (Exception ex)
        {
            Log.Error($"AutoClose failed: {ex}");
        }
    }
}
