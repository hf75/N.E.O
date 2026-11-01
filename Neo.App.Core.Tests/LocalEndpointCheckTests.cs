using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// The hosts start these checks before their splash screen, so the start does not wait for an AI
/// server that is not running. An answer counts once: whoever asks later gets a fresh one.
/// </summary>
public class LocalEndpointCheckTests
{
    [Fact]
    public void A_started_check_finds_the_server_and_the_next_question_asks_again()
    {
        var port = FreePort();
        var endpoint = $"http://localhost:{port}/v1";

        using (var server = new HttpListener())
        {
            server.Prefixes.Add($"http://localhost:{port}/");
            server.Start();
            _ = server.GetContextAsync().ContinueWith(answer => answer.Result.Response.Close(), TaskContinuationOptions.OnlyOnRanToCompletion);

            AppController.StartLocalEndpointChecks(new SettingsModel { OllamaEndpoint = endpoint, LmStudioEndpoint = "" });
            AppController.IsLocalEndpointReachable(endpoint).Should().BeTrue();
        }

        AppController.IsLocalEndpointReachable(endpoint).Should().BeFalse("the server is gone, and the old answer must not be used again");
    }

    [Fact]
    public void An_endpoint_nobody_checked_is_asked_directly()
        => AppController.IsLocalEndpointReachable($"http://localhost:{FreePort()}/v1").Should().BeFalse();

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
