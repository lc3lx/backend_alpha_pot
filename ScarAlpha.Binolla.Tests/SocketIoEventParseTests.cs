using ScarAlpha.Binolla.Session;
using Xunit;

namespace ScarAlpha.Binolla.Tests;

public class SocketIoEventParseTests
{
    [Fact]
    public void TryParseSocketIoEvent_parses_named_object_payload()
    {
        var ok = SessionMessageRouter.TryParseSocketIoEvent(
            """42["s_balances/list",{"demoBalance":1000,"liveBalance":0}]""",
            out var name,
            out var payload);

        Assert.True(ok);
        Assert.Equal("s_balances/list", name);
        Assert.Contains("demoBalance", payload);
    }

    [Fact]
    public void TryParseSocketIoEvent_parses_array_payload()
    {
        var ok = SessionMessageRouter.TryParseSocketIoEvent(
            """42["s_quotes/list",[["EURUSD_otc",1,1.1]]]""",
            out var name,
            out var payload);

        Assert.True(ok);
        Assert.Equal("s_quotes/list", name);
        Assert.StartsWith("[", payload);
    }
}
