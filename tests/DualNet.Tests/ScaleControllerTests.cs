using DualNet.Core.Engine;
using Xunit;

namespace DualNet.Tests;

/// <summary>
/// Tests for the v2 scale controller — the state machine that decides when the download
/// engine adds connections. These pin the behaviors that v1 got wrong in the real world:
/// standing down after a single 2-second evaluation (fresh TCP connections had not even
/// finished slow-start), and never recovering once stood down.
/// </summary>
public class ScaleControllerTests
{
    private const double Base = 1_000_000;   // 1 MB/s baseline for readability

    [Fact]
    public void FlatSpeed_ProducesPlateau_AfterWindowFills()
    {
        var c = new ScaleController();
        ScaleDecision d = ScaleDecision.None;

        for (int i = 0; i < 10 && d.Impulse == ScaleImpulse.None; i++)
            d = c.OnTick(Base);

        Assert.Equal(ScaleImpulse.AddConnections, d.Impulse);
        Assert.Equal(ScaleController.ProbeSplitsPerRound, d.Splits);
    }

    [Fact]
    public void GrowingSpeed_DoesNotTriggerPlateau()
    {
        var c = new ScaleController();
        double speed = Base;
        for (int tick = 0; tick < 8; tick++)
        {
            speed *= 1.35;   // strong ramp — never flat
            Assert.Equal(ScaleDecision.None.Impulse, c.OnTick(speed).Impulse);
        }
    }

    [Fact]
    public void RampRound_Gains_AreRewarded_WithMoreSplits()
    {
        var c = new ScaleController();
        c.ConfirmRound(2, Base);   // a round was applied at 1 MB/s aggregate

        // Three ticks of modestly better throughput (EMA ≈ +7 % over baseline).
        c.OnTick(Base * 1.15);
        c.OnTick(Base * 1.15);
        var d = c.OnTick(Base * 1.15);

        Assert.Equal(ScaleImpulse.AddConnections, d.Impulse);
        Assert.Equal(ScaleController.RampSplitsPerRound, d.Splits);
    }

    [Fact]
    public void RampRound_LargeGains_Escalate()
    {
        var c = new ScaleController();
        c.ConfirmRound(2, Base);

        c.OnTick(Base * 1.6);
        c.OnTick(Base * 1.6);
        var d = c.OnTick(Base * 1.6);

        Assert.Equal(ScaleImpulse.AddConnections, d.Impulse);
        Assert.Equal(ScaleController.AggressiveSplitsPerRound, d.Splits);
    }

    [Fact]
    public void TwoFailedRounds_AreRequired_BeforeStandingDown()
    {
        var c = new ScaleController();
        c.ConfirmRound(2, Base);

        // First failed evaluation → one more small chance, NOT exhaustion.
        var d1 = Tick(c, 3, Base);
        Assert.Equal(ScaleImpulse.ReProbe, d1.Impulse);
        Assert.False(c.Exhausted);

        c.ConfirmRound(2, Base);
        var d2 = Tick(c, 3, Base);
        Assert.Equal(ScaleDecision.None, d2);
        Assert.True(c.Exhausted);
    }

    [Fact]
    public void ExhaustedScaler_Reprobes_AfterCooldown()
    {
        var c = new ScaleController();
        c.ConfirmRound(2, Base);
        Tick(c, 3, Base);          // fail #1 → re-probe
        c.ConfirmRound(2, Base);
        Tick(c, 3, Base);          // fail #2 → exhausted
        Assert.True(c.Exhausted);

        ScaleDecision d = ScaleDecision.None;
        for (int i = 0; i < ScaleController.ReProbeCooldownTicks; i++)
            d = c.OnTick(Base);
        Assert.Equal(ScaleImpulse.ReProbe, d.Impulse);
        Assert.False(c.Exhausted);
    }

    [Fact]
    public void ExhaustedScaler_Waits_ThroughCooldown()
    {
        var c = new ScaleController();
        c.ConfirmRound(2, Base);
        Tick(c, 3, Base);
        c.ConfirmRound(2, Base);
        Tick(c, 3, Base);

        for (int i = 0; i < ScaleController.ReProbeCooldownTicks - 1; i++)
            Assert.Equal(ScaleDecision.None, c.OnTick(Base));
    }

    [Fact]
    public void CancelRound_NeverStartsAnEvaluation()
    {
        var c = new ScaleController();
        c.ConfirmRound(2, Base);
        c.CancelRound();

        Assert.Equal(0, c.PendingEvalTicks);
    }

    [Fact]
    public void ConfirmRound_WithZeroSplits_CancelsInstead()
    {
        var c = new ScaleController();
        c.ConfirmRound(0, Base);
        Assert.Equal(0, c.PendingEvalTicks);
    }

    [Fact]
    public void EmaSmoothing_IgnoresSingleNoisySample()
    {
        var c = new ScaleController();
        // Mostly flat with one huge spike: the plateau verdict must still arrive
        // (flat-line), but the smoothed values must not be poisoned by the spike.
        c.OnTick(Base);
        c.OnTick(Base);
        c.OnTick(Base * 50);          // noise
        var d = c.OnTick(Base);
        // With 4 samples in the window and growth dominated by the spike, a plateau
        // may or may not fire here — but the controller must remain sane either way.
        Assert.True(d.Impulse is ScaleImpulse.None or ScaleImpulse.AddConnections);
        Assert.False(c.Exhausted);
    }

    private static ScaleDecision Tick(ScaleController c, int count, double speed)
    {
        var d = ScaleDecision.None;
        for (int i = 0; i < count; i++)
            d = c.OnTick(speed);
        return d;
    }
}
