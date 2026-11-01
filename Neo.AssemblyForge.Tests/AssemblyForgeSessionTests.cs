using FluentAssertions;
using Neo.AssemblyForge.Completion;
using Neo.AssemblyForge.Services;
using Newtonsoft.Json;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class AssemblyForgeSessionTests
{
    private static AssemblyForgeWorkspace CreateTestWorkspace() => new()
    {
        NuGetPackageDirectory = Path.Combine(Path.GetTempPath(), "neo-session-test-nuget"),
        OutputDllPath = Path.Combine(Path.GetTempPath(), "neo-session-test-out.dll"),
        ReferenceAssemblyDirectories = new[] { Path.GetTempPath() },
    };

    private static AssemblyForgeSession CreateSession(
        Func<AssemblyForgeCompletionRequest, CancellationToken, Task<string>> handler,
        AssemblyForgeSessionOptions? options = null,
        AssemblyForgePipelineOptions? pipelineOptions = null,
        ICompilationService? compiler = null,
        INuGetPackageService? nuget = null)
    {
        var client = new AssemblyForgeClient(
            new DelegateCompletionProvider(handler),
            CreateTestWorkspace(),
            pipelineOptions: pipelineOptions,
            nuGetPackageService: nuget ?? new StubNuGetPackageService(),
            compilationService: compiler ?? new StubCompilationService());

        return client.CreateSession(options);
    }

    private static string MakeJsonResponse(StructuredResponse response)
        => JsonConvert.SerializeObject(response);

    #region Chat-only response

    [Fact]
    public async Task RunAsync_ChatOnlyResponse_ReturnsChatOnlyStatus()
    {
        var session = CreateSession((_, _) =>
            Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                Chat = "Hello! How can I help?",
            })));

        var result = await session.RunAsync("hi");

        result.Status.Should().Be(AssemblyForgeStatus.ChatOnly);
        result.StructuredResponse.Should().NotBeNull();
        result.StructuredResponse!.Chat.Should().Contain("Hello");
        result.AttemptsUsed.Should().Be(1);
    }

    #endregion

    #region PowerShell response

    [Fact]
    public async Task RunAsync_PowerShellResponse_ReturnsPowerShellReadyStatus()
    {
        var session = CreateSession((_, _) =>
            Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                PowerShellScript = "Get-Process",
                Explanation = "Lists running processes",
            })));

        var result = await session.RunAsync("list processes");

        result.Status.Should().Be(AssemblyForgeStatus.PowerShellReady);
        result.StructuredResponse.Should().NotBeNull();
        result.StructuredResponse!.PowerShellScript.Should().Be("Get-Process");
    }

    #endregion

    #region ConsoleApp response

    [Fact]
    public async Task RunAsync_ConsoleAppResponse_ReturnsConsoleAppReadyStatus()
    {
        var session = CreateSession((_, _) =>
            Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                ConsoleAppCode = "class Program { static void Main() { } }",
                Explanation = "A console app",
            })));

        var result = await session.RunAsync("make a console app");

        result.Status.Should().Be(AssemblyForgeStatus.ConsoleAppReady);
        result.StructuredResponse.Should().NotBeNull();
        result.StructuredResponse!.ConsoleAppCode.Should().Contain("Program");
    }

    #endregion

    #region Code validation and retries

    [Fact]
    public async Task RunAsync_CodeWithoutUserControl_RetriesAndFails()
    {
        var session = CreateSession(
            (_, _) => Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                Code = "class NotAUserControl { }",
            })),
            pipelineOptions: new AssemblyForgePipelineOptions { MaxAttempts = 2 });

        var result = await session.RunAsync("make something");

        result.Status.Should().Be(AssemblyForgeStatus.Failed);
        result.AttemptsUsed.Should().Be(2);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RunAsync_EveryTry_SaysWhichAttemptItIs()
    {
        var attempts = new List<int>();
        var session = CreateSession(
            (request, _) =>
            {
                attempts.Add(request.Attempt);
                return Task.FromResult(MakeJsonResponse(new StructuredResponse { Code = "class NotAUserControl { }" }));
            },
            pipelineOptions: new AssemblyForgePipelineOptions { MaxAttempts = 3 });

        await session.RunAsync("make something");

        attempts.Should().Equal(1, 2, 3);
    }

    #endregion

    #region Successful code with compilation

    [Fact]
    public async Task RunAsync_ValidCode_ReturnsSuccess()
    {
        var validCode = @"
using System;
using System.Windows.Controls;

namespace Neo.Dynamic
{
    public sealed class DynamicUserControl : UserControl
    {
        public DynamicUserControl() { }
    }
}";
        var session = CreateSession((_, _) =>
            Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                Code = validCode,
                NuGetPackages = new List<string>(),
                Explanation = "Simple control",
            })));

        var result = await session.RunAsync("make a simple control");

        result.Status.Should().Be(AssemblyForgeStatus.Success);
        result.AttemptsUsed.Should().Be(1);
        result.StructuredResponse.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_ValidCode_UpdatesSessionState()
    {
        var validCode = @"
using System;
using System.Windows.Controls;

namespace Neo.Dynamic
{
    public sealed class DynamicUserControl : UserControl
    {
        public DynamicUserControl() { }
    }
}";
        var session = CreateSession((_, _) =>
            Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                Code = validCode,
                Explanation = "Simple control",
            })));

        await session.RunAsync("make a control");

        session.State.CurrentCode.Should().Be(validCode);
        session.State.History.Should().Contain("make a control");
        session.State.LastErrorMessage.Should().BeEmpty();
    }

    #endregion

    #region Patch response

    [Fact]
    public async Task RunAsync_ValidPatch_AppliesPatchAndReturnsSuccess()
    {
        var options = new AssemblyForgeSessionOptions
        {
            InitialCode = @"using System;
using System.Windows.Controls;

namespace Neo.Dynamic
{
    public sealed class DynamicUserControl : UserControl
    {
        public DynamicUserControl()
        {
        }
    }
}",
        };

        var patchText = @"--- a/./currentcode.cs
+++ b/./currentcode.cs
@@ -8,7 +8,7 @@
     public sealed class DynamicUserControl : UserControl
     {
         public DynamicUserControl()
-        {
+        {   Content = new System.Windows.Controls.Grid();
         }
     }
 }";

        var session = CreateSession(
            (_, _) => Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                Patch = patchText,
                Explanation = "Added grid content",
            })),
            options: options);

        var result = await session.RunAsync("add a grid");

        result.Status.Should().Be(AssemblyForgeStatus.Success);
        result.StructuredResponse.Should().NotBeNull();
        session.State.CurrentCode.Should().Contain("Grid");
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task RunAsync_Cancelled_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var session = CreateSession((_, _) =>
            Task.FromResult(MakeJsonResponse(new StructuredResponse { Chat = "hi" })));

        var act = () => session.RunAsync("test", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_CancelledDuringCompletion_ThrowsOperationCanceledException()
    {
        var session = CreateSession((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("{}");
        });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => session.RunAsync("test", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Max attempts exceeded

    [Fact]
    public async Task RunAsync_AllAttemptsFail_ReturnsFailedStatus()
    {
        var session = CreateSession(
            (_, _) => Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                Code = "// not a user control",
            })),
            pipelineOptions: new AssemblyForgePipelineOptions { MaxAttempts = 3 });

        var result = await session.RunAsync("build something");

        result.Status.Should().Be(AssemblyForgeStatus.Failed);
        result.AttemptsUsed.Should().Be(3);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RunAsync_MaxAttempts1_OnlyTriesOnce()
    {
        int callCount = 0;
        var session = CreateSession(
            (_, _) =>
            {
                callCount++;
                return Task.FromResult(MakeJsonResponse(new StructuredResponse
                {
                    Code = "invalid code without usercontrol",
                }));
            },
            pipelineOptions: new AssemblyForgePipelineOptions { MaxAttempts = 1 });

        var result = await session.RunAsync("make something");

        result.Status.Should().Be(AssemblyForgeStatus.Failed);
        callCount.Should().Be(1);
    }

    #endregion

    #region Chat response updates history

    [Fact]
    public async Task RunAsync_ChatResponse_UpdatesHistory()
    {
        var session = CreateSession((_, _) =>
            Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                Chat = "I can help with that!",
            })));

        await session.RunAsync("what can you do?");

        session.State.History.Should().Contain("what can you do?");
        session.State.History.Should().Contain("I can help with that!");
    }

    #endregion

    #region Executable artifact kind

    [Fact]
    public async Task RunAsync_ExecutableArtifact_ValidatesEntrypoint()
    {
        var validExeCode = @"
using System;
namespace Neo.Dynamic
{
    public static class DynamicProgram
    {
        public static void Main() { }
    }
}";
        var session = CreateSession(
            (_, _) => Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                Code = validExeCode,
            })),
            options: new AssemblyForgeSessionOptions
            {
                ArtifactKind = AssemblyForgeArtifactKind.Executable,
            });

        var result = await session.RunAsync("make an app");

        result.Status.Should().Be(AssemblyForgeStatus.Success);
    }

    [Fact]
    public async Task RunAsync_ExecutableArtifact_MissingEntrypoint_Fails()
    {
        var session = CreateSession(
            (_, _) => Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                Code = "class NoMainHere { }",
            })),
            options: new AssemblyForgeSessionOptions
            {
                ArtifactKind = AssemblyForgeArtifactKind.Executable,
            },
            pipelineOptions: new AssemblyForgePipelineOptions { MaxAttempts = 1 });

        var result = await session.RunAsync("make an app");

        result.Status.Should().Be(AssemblyForgeStatus.Failed);
    }

    #endregion

    #region NuGet packages are accumulated

    [Fact]
    public async Task RunAsync_ResponseWithNuGetPackages_PassesThemToCompiler()
    {
        var validCode = @"
using System;
using System.Windows.Controls;

namespace Neo.Dynamic
{
    public sealed class DynamicUserControl : UserControl { }
}";
        var session = CreateSession((_, _) =>
            Task.FromResult(MakeJsonResponse(new StructuredResponse
            {
                Code = validCode,
                NuGetPackages = new List<string> { "Newtonsoft.Json|default" },
            })));

        var result = await session.RunAsync("use json");

        result.Status.Should().Be(AssemblyForgeStatus.Success);
        result.StructuredResponse!.NuGetPackages.Should().Contain("Newtonsoft.Json|default");
    }

    #endregion

    #region Compilation failure triggers retry

    [Fact]
    public async Task RunAsync_CompilationFails_Retries()
    {
        int attempt = 0;
        var validCode = @"
using System;
using System.Windows.Controls;

namespace Neo.Dynamic
{
    public sealed class DynamicUserControl : UserControl { }
}";

        var failingCompiler = new FailOnceCompilationService();

        var session = CreateSession(
            (_, _) =>
            {
                attempt++;
                return Task.FromResult(MakeJsonResponse(new StructuredResponse
                {
                    Code = validCode,
                }));
            },
            pipelineOptions: new AssemblyForgePipelineOptions { MaxAttempts = 3 },
            compiler: failingCompiler);

        var result = await session.RunAsync("make control");

        // First attempt fails compilation, second succeeds
        result.Status.Should().Be(AssemblyForgeStatus.Success);
        attempt.Should().Be(2);
    }

    #endregion

    #region A patch that adds a method nothing calls

    private const string ControlWithUi = @"using System.Windows.Controls;

public sealed class DynamicUserControl : UserControl
{
    public DynamicUserControl()
    {
        BuildUi();
    }

    private void BuildUi()
    {
        Content = new Grid();
    }
}";

    private const string PatchAddingChart = @"--- a/./currentcode.cs
+++ b/./currentcode.cs
@@ -11,4 +11,6 @@
     {
         Content = new Grid();
     }
+
+    private Control Chart() => new Canvas();
 }";

    private const string PatchCallingChart = @"--- a/./currentcode.cs
+++ b/./currentcode.cs
@@ -10,5 +10,5 @@
     private void BuildUi()
     {
-        Content = new Grid();
+        Content = Chart();
     }";

    private const string PatchAddingChartAndItsCall = @"--- a/./currentcode.cs
+++ b/./currentcode.cs
@@ -10,5 +10,7 @@
     private void BuildUi()
     {
-        Content = new Grid();
+        Content = Chart();
     }
+
+    private Control Chart() => new Canvas();
 }";

    private const string PatchAddingLegend = @"--- a/./currentcode.cs
+++ b/./currentcode.cs
@@ -15,2 +15,4 @@
     private Control Chart() => new Canvas();
+
+    private Control Legend() => new Canvas();
 }";

    private const string PatchChangingSomethingElse = @"--- a/./currentcode.cs
+++ b/./currentcode.cs
@@ -11,3 +11,3 @@
     {
-        Content = new Grid();
+        Content = new Grid { Name = ""root"" };
     }";

    private const string PatchCallingChartAfterTheOtherChange = @"--- a/./currentcode.cs
+++ b/./currentcode.cs
@@ -11,3 +11,3 @@
     {
-        Content = new Grid { Name = ""root"" };
+        Content = Chart();
     }";

    private static (AssemblyForgeSession Session, List<AssemblyForgeCompletionRequest> Requests) SessionAnsweringWith(
        IEnumerable<string> patches, int maxAttempts = 5, ICompilationService? compiler = null)
    {
        var answers = patches.ToList();
        var requests = new List<AssemblyForgeCompletionRequest>();
        var session = CreateSession(
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(MakeJsonResponse(new StructuredResponse { Patch = answers[requests.Count - 1], Explanation = "From the test." }));
            },
            options: new AssemblyForgeSessionOptions { InitialCode = ControlWithUi },
            pipelineOptions: new AssemblyForgePipelineOptions { MaxAttempts = maxAttempts },
            compiler: compiler);
        return (session, requests);
    }

    [Fact]
    public async Task RunAsync_PatchAddsMethodNothingCalls_GoesBackToTheModelToFinishIt()
    {
        var (session, requests) = SessionAnsweringWith([PatchAddingChart, PatchCallingChart]);

        var result = await session.RunAsync("add a chart");

        result.Status.Should().Be(AssemblyForgeStatus.Success);
        result.AttemptsUsed.Should().Be(2);
        requests.Should().HaveCount(2);
        requests[1].Prompt.Should().Contain("add a chart");
        requests[1].Prompt.Should().Contain("nothing calls Chart");
        requests[1].Prompt.Should().Contain("was applied", "the model must not write the method a second time");
        requests[1].Prompt.Should().Contain("private Control Chart()", "the file it gets already holds its first patch");
        requests[1].Prompt.Should().NotContain("could not be applied");
        session.State.CurrentCode.Should().Contain("Content = Chart();");
        session.State.CurrentCode.Should().Contain("private Control Chart()");
    }

    [Fact]
    public async Task RunAsync_PatchGoesBack_TheReviewSeesTheWholeChangeOnce()
    {
        var (session, _) = SessionAnsweringWith([PatchAddingChart, PatchCallingChart]);
        var reviewed = new List<AssemblyForgeReviewContext>();

        await session.RunAsync("add a chart", default, (context, _) =>
        {
            reviewed.Add(context);
            return Task.FromResult(new AssemblyForgeReviewDecision(AssemblyForgeReviewAction.Accept));
        });

        reviewed.Should().ContainSingle();
        reviewed[0].Patch.Should().Contain("private Control Chart()").And.Contain("Content = Chart();");
    }

    [Fact]
    public async Task RunAsync_SecondPatchLeavesAMethodUnusedToo_IsAccepted()
    {
        var (session, requests) = SessionAnsweringWith([PatchAddingChart, PatchAddingLegend]);

        var result = await session.RunAsync("add a chart");

        result.Status.Should().Be(AssemblyForgeStatus.Success, "a model that insists must not use up the attempts");
        requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_UnfinishedPatchOnTheLastTry_IsAcceptedInsteadOfFailing()
    {
        var (session, requests) = SessionAnsweringWith([PatchAddingChart], maxAttempts: 1);

        var result = await session.RunAsync("add a chart");

        result.Status.Should().Be(AssemblyForgeStatus.Success);
        requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_PatchWithItsCall_DoesNotGoBack()
    {
        var (session, requests) = SessionAnsweringWith([PatchAddingChartAndItsCall]);

        var result = await session.RunAsync("add a chart");

        result.Status.Should().Be(AssemblyForgeStatus.Success);
        requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_CodeAnswerWithAnUnusedMethod_DoesNotGoBack()
    {
        var calls = 0;
        var session = CreateSession((_, _) =>
        {
            calls++;
            return Task.FromResult(MakeJsonResponse(new StructuredResponse { Code = ControlWithUi.Replace("private void BuildUi()", "private void Spare() { }\n    private void BuildUi()") }));
        });

        var result = await session.RunAsync("make a control");

        result.Status.Should().Be(AssemblyForgeStatus.Success);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_MethodFromAnEarlierTryOfThisRun_StillCounts_AndItsCompileErrorIsGone()
    {
        var (session, requests) = SessionAnsweringWith(
            [PatchAddingChart, PatchChangingSomethingElse, PatchCallingChartAfterTheOtherChange],
            compiler: new FailOnceCompilationService());

        var result = await session.RunAsync("add a chart");

        result.Status.Should().Be(AssemblyForgeStatus.Success);
        requests.Should().HaveCount(3);
        requests[2].Prompt.Should().Contain("nothing calls Chart");
        requests[2].Prompt.Should().NotContain("Simulated compilation failure", "that was fixed in the try before");
    }

    [Fact]
    public async Task RunAsync_PatchGoesBack_TheErrorTheRunIsAboutStaysInThePrompt()
    {
        var (session, requests) = SessionAnsweringWith([PatchAddingChart, PatchCallingChart]);
        session.State.LastErrorMessage = "The app threw a FormatException.";

        await session.RunAsync("please fix the error");

        requests[1].Prompt.Should().Contain("The app threw a FormatException.");
    }

    #endregion

    #region Test doubles

    private sealed class StubCompilationService : ICompilationService
    {
        public Task<string> CompileToDllAsync(
            IReadOnlyList<string> sourceFiles,
            string dllOutputPath,
            IReadOnlyList<string> nugetDllPaths,
            IReadOnlyList<string> additionalDllPaths,
            string assemblyName,
            CancellationToken cancellationToken)
            => Task.FromResult(dllOutputPath);

        public Task<string> CompileToExeAsync(
            IReadOnlyList<string> sourceFiles,
            string outputDirectory,
            IReadOnlyList<string> nugetDllPaths,
            IReadOnlyList<string> additionalDllPaths,
            string assemblyName,
            string mainTypeName,
            string compileType,
            CancellationToken cancellationToken)
            => Task.FromResult(Path.Combine(outputDirectory, assemblyName + ".exe"));
    }

    private sealed class FailOnceCompilationService : ICompilationService
    {
        private int _callCount;

        public Task<string> CompileToDllAsync(
            IReadOnlyList<string> sourceFiles,
            string dllOutputPath,
            IReadOnlyList<string> nugetDllPaths,
            IReadOnlyList<string> additionalDllPaths,
            string assemblyName,
            CancellationToken cancellationToken)
        {
            _callCount++;
            if (_callCount == 1)
                throw new InvalidOperationException("Simulated compilation failure CS0001");

            return Task.FromResult(dllOutputPath);
        }

        public Task<string> CompileToExeAsync(
            IReadOnlyList<string> sourceFiles,
            string outputDirectory,
            IReadOnlyList<string> nugetDllPaths,
            IReadOnlyList<string> additionalDllPaths,
            string assemblyName,
            string mainTypeName,
            string compileType,
            CancellationToken cancellationToken)
            => Task.FromResult(Path.Combine(outputDirectory, assemblyName + ".exe"));
    }

    private sealed class StubNuGetPackageService : INuGetPackageService
    {
        public Task<NuGetResult> LoadPackagesAsync(
            IReadOnlyDictionary<string, string> packages,
            IEnumerable<string> existingDlls,
            CancellationToken cancellationToken)
            => Task.FromResult(new NuGetResult(
                Array.Empty<string>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    #endregion
}
