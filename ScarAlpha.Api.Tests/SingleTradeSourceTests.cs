using System.Reflection;
using FluentAssertions;
using ScarAlpha.Application.Services;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// One writer places bot trades: the server-side worker.
///
/// <para>The signal endpoint is polled by every UI for whichever pair is on screen. When
/// it could also place, a user with the web app and the Telegram Mini App open at the
/// same time had three independent placers — worker, web, Mini App — each looking at a
/// different pair. The idempotency key embeds the asset
/// (<c>bot:{strategy}:{asset}:{bar}:{direction}</c>), so trades on different pairs carry
/// different keys and nothing deduplicated them. The user simply got two trades.</para>
///
/// <para>These tests pin the two structural facts that prevent it coming back.</para>
/// </summary>
public sealed class SingleTradeSourceTests
{
    private static MethodInfo ExecuteIfRequested =>
        typeof(RsiSignalAppService).GetMethod(
            "ExecuteIfRequestedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ExecuteIfRequestedAsync not found.");

    [Fact]
    public void The_execute_path_still_takes_an_autoExecute_decision()
    {
        // The parameter existed before but was never read — the guard checked only the bot
        // state. Losing the parameter again would silently reopen the endpoint as a
        // trade source, so its presence is worth pinning.
        ExecuteIfRequested.GetParameters()
            .Select(p => p.Name)
            .Should().Contain("autoExecute");
    }

    [Fact]
    public void Only_the_worker_entry_point_requests_execution()
    {
        // TryAutoExecuteAsync is what BotSignalWorker calls, and it is the only public
        // surface that asks for a placement.
        var publicEntry = typeof(RsiSignalAppService).GetMethod(
            nameof(RsiSignalAppService.TryAutoExecuteAsync),
            BindingFlags.Instance | BindingFlags.Public);

        publicEntry.Should().NotBeNull(
            "the worker needs an explicit execute entry point that polling does not share");

        // GetSignalAsync takes the flag from its caller; the HTTP endpoint always passes
        // false, so no request can turn itself into a placement.
        var poll = typeof(RsiSignalAppService).GetMethod(
            nameof(RsiSignalAppService.GetSignalAsync),
            BindingFlags.Instance | BindingFlags.Public);

        poll!.GetParameters().Select(p => p.Name).Should().Contain("autoExecute");
    }

    [Fact]
    public void The_signal_endpoint_never_exposes_autoExecute_to_callers()
    {
        // A query parameter would let any client opt back into placing. The endpoint is
        // read-only by construction, so the source must not mention the flag as a binding.
        var endpoints = File.ReadAllText(
            Path.Combine(RepoRoot(), "ScarAlpha.Api", "Endpoints", "ApiEndpoints.cs"));

        endpoints.Should().NotContain(
            "[FromQuery] bool autoExecute",
            "exposing it would make every open browser tab another trade source");

        endpoints.Should().Contain(
            "autoExecute: false",
            "the signal endpoint must pass the flag explicitly so the intent is visible");
    }

    /// <summary>Walks up from the test binary to the backend solution folder.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScarAlpha.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("ScarAlpha.sln not found.");
    }
}
