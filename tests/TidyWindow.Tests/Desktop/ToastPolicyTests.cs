using TidyWindow.Next.Desktop;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class ToastPolicyTests
{
    [Fact]
    public void AnExplicitTimeIsKeptAndZeroMeansUntilDismissed()
    {
        Assert.Equal(15, ToastPolicy.Seconds(ToastKind.Warning, "t", "d", 15));
        Assert.Equal(0, ToastPolicy.Seconds(ToastKind.Warning, "t", new string('x', 5000), 0));
    }

    [Fact]
    public void ShortMessagesGetTheUsualTimeForTheirKind()
    {
        Assert.Equal(8, ToastPolicy.Seconds(ToastKind.Success, "Saved", ""));
        Assert.Equal(10, ToastPolicy.Seconds(ToastKind.Warning, "Could not save", "Try again."));
    }

    [Fact]
    public void LongMessagesStayLongEnoughToReadButNotForever()
    {
        var medium = ToastPolicy.Seconds(ToastKind.Warning, "Could not finish", new string('x', 300));
        var huge = ToastPolicy.Seconds(ToastKind.Warning, "Could not finish", new string('x', 4000));
        Assert.InRange(medium, 11, 30);
        Assert.Equal(30, huge);
    }

    [Fact]
    public void ClampKeepsShortTextAndCutsLongTextWithAnEllipsis()
    {
        Assert.Equal("short", ToastPolicy.Clamp("  short ", 10));
        var cut = ToastPolicy.Clamp(new string('a', 50), 10);
        Assert.Equal(10, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public void BriefFlattensLinesAndCuts()
    {
        Assert.Equal("one two three", ToastPolicy.Brief("one\r\ntwo\n\nthree"));
        Assert.True(ToastPolicy.Brief(new string('a', 500)).Length <= 240);
    }
}

public sealed class CountdownTests
{
    private long clock;
    private Countdown Make(double seconds = 10) => new(TimeSpan.FromSeconds(seconds), () => clock);

    [Fact]
    public void RunsDownAndExpires()
    {
        var countdown = Make();
        clock = 4000;
        Assert.Equal(TimeSpan.FromSeconds(6), countdown.Remaining);
        Assert.False(countdown.Expired);
        clock = 10000;
        Assert.True(countdown.Expired);
        Assert.Equal(0, countdown.Fraction);
    }

    [Fact]
    public void StandsStillWhileHeldAndCarriesOnFromWhereItWas()
    {
        var countdown = Make();
        clock = 3000;
        Assert.True(countdown.Hold("pointer", true));
        clock = 60000;
        Assert.False(countdown.Expired);
        Assert.Equal(TimeSpan.FromSeconds(7), countdown.Remaining);
        Assert.True(countdown.Hold("pointer", false));
        clock = 62000;
        Assert.Equal(TimeSpan.FromSeconds(5), countdown.Remaining);
    }

    [Fact]
    public void StaysHeldUntilEveryReasonIsGone()
    {
        var countdown = Make();
        Assert.True(countdown.Hold("pointer", true));
        Assert.False(countdown.Hold("open", true));
        Assert.False(countdown.Hold("pointer", false));
        Assert.True(countdown.IsHeld);
        clock = 50000;
        Assert.False(countdown.Expired);
        Assert.True(countdown.Hold("open", false));
        clock = 60000;
        Assert.True(countdown.Expired);
    }

    [Fact]
    public void HoldingTheSameReasonTwiceChangesNothing()
    {
        var countdown = Make();
        Assert.True(countdown.Hold("away", true));
        Assert.False(countdown.Hold("away", true));
        Assert.False(countdown.Hold("other", false));
    }

    [Fact]
    public void RestartGivesTheFullTimeAgain()
    {
        var countdown = Make();
        clock = 9000;
        countdown.Restart();
        clock = 12000;
        Assert.Equal(TimeSpan.FromSeconds(7), countdown.Remaining);
        countdown.Restart(TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.FromSeconds(20), countdown.Total);
        Assert.Equal(1, countdown.Fraction);
    }

    [Fact]
    public void ZeroTotalNeverExpires()
    {
        var countdown = Make(0);
        clock = long.MaxValue / 4;
        Assert.True(countdown.Endless);
        Assert.False(countdown.Expired);
        Assert.Equal(1, countdown.Fraction);
    }
}
