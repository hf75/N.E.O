using CommunityToolkit.Mvvm.ComponentModel;

namespace Neo.App.WebApp.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _greeting = "Welcome to Avalonia!";
}
