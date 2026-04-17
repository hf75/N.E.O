using System;
using System.Linq;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Neo.App.WebApp.Services;
using Neo.App.WebApp.Services.Ai;
using Neo.App.WebApp.Services.Compilation;
using Neo.App.WebApp.Services.Sessions;
using Neo.App.WebApp.Views;

namespace Neo.App.WebApp;

public partial class App : Application
{
    public static Uri BackendBaseUri { get; set; } = new("http://localhost/", UriKind.Absolute);
    public static ISessionStore SessionStore { get; set; } = new InMemorySessionStore();

    /// <summary>Fires once when the MainView is constructed so the host can add platform hooks.</summary>
    public static event Action<MainView>? MainViewConfigured;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        DisableAvaloniaDataAnnotationValidation();

        var view = BuildMainView();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { Content = view };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = view;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static MainView BuildMainView()
    {
        var http = new HttpClient { BaseAddress = BackendBaseUri };
        var ai = new AiClient(http);
        var compiler = new WasmCompiler(http);
        var nuget = new NuGetResolver(http);
        var pluginHost = new InProcessPluginHost();
        var orchestrator = new AppOrchestrator(ai, compiler, pluginHost, nuget);

        var view = new MainView();
        view.Bind(orchestrator, SessionStore);
        MainViewConfigured?.Invoke(view);
        return view;
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var toRemove = BindingPlugins.DataValidators
            .OfType<DataAnnotationsValidationPlugin>().ToArray();
        foreach (var p in toRemove) BindingPlugins.DataValidators.Remove(p);
    }
}
