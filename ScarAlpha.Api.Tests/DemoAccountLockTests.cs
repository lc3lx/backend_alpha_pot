using FluentAssertions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Domain.Entities;
using Xunit;

namespace ScarAlpha.Api.Tests;

/// <summary>
/// The demo balance is now a privilege, not the default.
///
/// <para>The product used to force every account onto Binolla's demo balance and refuse
/// live outright. That is inverted: a connection with no account type stated is LIVE, and
/// demo is refused unless an admin has unlocked it for that user. These tests pin the
/// direction of that gate, because getting it backwards would either strand everyone on
/// fake money again or hand demo out to accounts that were never granted it.</para>
/// </summary>
public sealed class DemoAccountLockTests
{
    [Fact]
    public void A_new_user_has_no_demo_access()
    {
        // Nothing in the constructor may quietly grant it — the column also defaults to
        // false in the schema, so a row created outside this type behaves the same.
        new User().DemoAllowed.Should().BeFalse();
    }

    [Fact]
    public void Granting_demo_is_an_explicit_act()
    {
        var user = new User();
        user.DemoAllowed.Should().BeFalse();

        user.DemoAllowed = true;
        user.DemoAllowed.Should().BeTrue();
    }

    [Fact]
    public void The_lock_has_its_own_error_code()
    {
        // Distinct from REAL_TRADING_DISABLED: that meant "live is switched off for
        // everyone", which is the opposite of the situation this code reports. The
        // frontend needs to tell a locked demo from a disabled product.
        ApiErrorCodes.DemoAccountLocked.Should().Be("DEMO_ACCOUNT_LOCKED");
        ApiErrorCodes.DemoAccountLocked.Should().NotBe(ApiErrorCodes.RealTradingDisabled);
    }

    /// <summary>
    /// The request contracts must default to LIVE.
    ///
    /// <para>This is the exact bug that broke every login after the lock landed: the
    /// records defaulted <c>AccountType</c> to "Demo", so a caller that simply omitted the
    /// field was asking for the locked balance and was refused with DEMO_ACCOUNT_LOCKED.
    /// The frontends did exactly that.</para>
    /// </summary>
    [Fact]
    public void Connect_contracts_default_to_live()
    {
        new BinollaConnectRequest("ssid").AccountType.Should().Be("Real");
        new BinollaCredentialRequest("a@b.com", "pw").AccountType.Should().Be("Real");
    }

    [Fact]
    public void Marketing_demo_and_demo_balance_are_separate_switches()
    {
        // One shows synthetic data in the app; the other picks which Binolla balance is
        // traded. Conflating them would let a marketing account touch live money, or
        // block a real user's live trading for a display setting.
        var user = new User { IsMarketingDemo = true };
        user.DemoAllowed.Should().BeFalse();

        var live = new User { DemoAllowed = true };
        live.IsMarketingDemo.Should().BeFalse();
    }
}
