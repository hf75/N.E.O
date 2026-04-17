using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Neo.App.WebApp.Services.Sessions;

namespace Neo.App.WebApp.Views;

public partial class LoadSessionDialog : Window
{
    private readonly ISessionStore _store;

    public string? SelectedName => SessionList.SelectedItem as string;

    public LoadSessionDialog() { InitializeComponent(); _store = null!; }

    public LoadSessionDialog(ISessionStore store)
    {
        InitializeComponent();
        _store = store;
        LoadBtn.Click += (_, _) => { if (SelectedName is not null) Close(SelectedName); };
        CancelBtn.Click += (_, _) => Close(null);
        DeleteBtn.Click += async (_, _) =>
        {
            if (SelectedName is null) return;
            await _store.DeleteAsync(SelectedName);
            await RefreshAsync();
        };
        Opened += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var names = await _store.ListAsync();
        SessionList.ItemsSource = new List<string>(names);
    }
}
