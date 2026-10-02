using OpenSense.Core.Monitoring;

namespace OpenSense.Core.Control;

/// <summary>How hard the CPU may run: what the CPU's power limits are held at, within a temperature the controller keeps under.</summary>
public enum CpuPowerPreset
{
    /// <summary>OpenSense leaves the CPU's limits to the firmware.</summary>
    Firmware,

    /// <summary>Cool and quiet: low sustained power, for battery-friendly everyday use.</summary>
    Eco,

    /// <summary>Most of the performance for a fraction of the noise: about 65 W, where the fans stay slow (measured on the PHN16-72: 84 % of full speed with the CPU fan near 2600 rpm, against 6600 rpm at full power).</summary>
    Quiet,

    /// <summary>The best clocks per watt for the chassis' cooling: below the firmware's limits, well under the thermal limit.</summary>
    Optimize,

    /// <summary>The firmware's own limits, with a longer burst; the usual full-power setting.</summary>
    Turbo,

    /// <summary>
    /// The highest limits OpenSense can write. On laptops whose firmware also holds the CPU through the memory-mapped
    /// limit registers (the PHN16-72 does, at 115 W) this adds only a few seconds of burst: the MSR can lower the limits,
    /// not raise them past the firmware's.
    /// </summary>
    Unleash,

    /// <summary>The values in <see cref="CpuPowerSettings.Custom"/>, e.g. what an agent worked out.</summary>
    Custom,
}

/// <param name="Pl1W">The sustained power limit in watts.</param>
/// <param name="Pl2W">The burst power limit in watts; never below PL1.</param>
/// <param name="Pl1WindowS">How long (seconds) the average power may exceed PL1 before the CPU settles to it.</param>
/// <param name="CeilingC">The CPU temperature the controller keeps under by backing the limits off.</param>
public sealed record CpuPowerTarget(double Pl1W, double Pl2W, double Pl1WindowS, int CeilingC);

/// <summary>What the user (or an agent) wants of the CPU's power: a preset, or custom values, and optionally another temperature target.</summary>
public sealed record CpuPowerSettings
{
    public CpuPowerPreset Preset { get; init; } = CpuPowerPreset.Firmware;

    /// <summary>The values for <see cref="CpuPowerPreset.Custom"/>.</summary>
    public CpuPowerTarget? Custom { get; init; }

    /// <summary>A temperature target in place of the preset's.</summary>
    public int? CeilingC { get; init; }

    /// <summary>What to hold the CPU's limits at, within <see cref="CpuPowerPresets.Bounds"/>; null leaves them to the firmware.</summary>
    public CpuPowerTarget? Target()
    {
        var target = Preset switch
        {
            CpuPowerPreset.Eco => CpuPowerPresets.Eco,
            CpuPowerPreset.Quiet => CpuPowerPresets.Quiet,
            CpuPowerPreset.Optimize => CpuPowerPresets.Optimize,
            CpuPowerPreset.Turbo => CpuPowerPresets.Turbo,
            CpuPowerPreset.Unleash => CpuPowerPresets.Unleash,
            CpuPowerPreset.Custom => Custom,
            _ => null,
        };
        if (target is null)
            return null;
        return CpuPowerPresets.Clamp(CeilingC is { } ceiling ? target with { CeilingC = ceiling } : target);
    }
}

/// <summary>
/// The presets and the bounds nothing may go outside. The presets are starting points for this class of laptop
/// (a 14900HX-class chip in a dual-fan chassis, which is cooling-limited well below the firmware's 115/157 W): the
/// controller never holds a limit the temperature doesn't allow, and an agent can refine them as Custom.
/// </summary>
public static class CpuPowerPresets
{
    public static CpuPowerTarget Eco { get; } = new(35, 65, 28, 78);
    public static CpuPowerTarget Quiet { get; } = new(65, 85, 96, 84);
    public static CpuPowerTarget Optimize { get; } = new(80, 115, 56, 86);
    public static CpuPowerTarget Turbo { get; } = new(115, 157, 96, 90);
    public static CpuPowerTarget Unleash { get; } = new(157, 200, 128, 94);

    public const double MinPl1W = 15;
    public const double MaxPl1W = 157;
    public const double MaxPl2W = 200;
    public const double MinWindowS = 8;
    public const double MaxWindowS = 128;
    public const int MinCeilingC = 70;
    public const int MaxCeilingC = 95;

    /// <summary>The target with each value inside the bounds, and PL2 not below PL1.</summary>
    public static CpuPowerTarget Clamp(CpuPowerTarget target)
    {
        var pl1 = Math.Clamp(target.Pl1W, MinPl1W, MaxPl1W);
        return new CpuPowerTarget(pl1, Math.Clamp(target.Pl2W, pl1, MaxPl2W), Math.Clamp(target.Pl1WindowS, MinWindowS, MaxWindowS),
            Math.Clamp(target.CeilingC, MinCeilingC, MaxCeilingC));
    }
}

/// <summary>What the controller is doing about the CPU's limits.</summary>
public enum CpuPowerState
{
    /// <summary>Nothing asked and nothing of OpenSense's in the CPU: the firmware's limits stand.</summary>
    Idle,

    /// <summary>Moving the limits toward the target, a step at a time.</summary>
    Ramping,

    /// <summary>At the target, or held where the temperature allows.</summary>
    Holding,

    /// <summary>Too hot: the limits were stepped down.</summary>
    BackingOff,

    /// <summary>On battery: never above what the firmware had.</summary>
    OnBattery,

    /// <summary>The CPU's power-limit register is locked, or doesn't take the value.</summary>
    Refused,
}

/// <param name="Applied1W">PL1 in the CPU as of the last step; null when OpenSense hasn't taken over.</param>
/// <param name="Overwrites">How many times something else changed the limits after OpenSense set them (the firmware, another tool).</param>
public sealed record CpuPowerTelemetry(
    CpuPowerPreset Preset, CpuPowerState State, double? Target1W, double? Target2W, double? Applied1W, double? Applied2W, int? CeilingC, int Overwrites);

/// <summary>
/// Holds the CPU's PL1/PL2 at <see cref="CpuPowerSettings"/>, gradually: up a few watts every few seconds while the
/// temperature has room, down at once when it hasn't, never above the firmware's own values on battery. What it found in
/// the CPU is put back when it stops or the settings ask for nothing. Used from the control thread.
/// </summary>
internal sealed class CpuPowerController(ICpuPowerControl control)
{
    internal static readonly TimeSpan StepEvery = TimeSpan.FromSeconds(4);
    internal const double UpStepW = 5;
    internal const double Pl2UpStepW = 8;
    internal const double DownStepW = 10;
    internal const double BackOffStepW = 5;

    /// <summary>Degrees under the ceiling where the limits may still go up.</summary>
    internal const int Headroom = 5;

    private PowerLimitRegister? _original;
    private double _pl1, _pl2;
    private double _window;
    private bool _owned;
    private DateTime _lastStep = DateTime.MinValue;
    private int _overwrites;
    private bool _refused;
    private CpuPowerState _state = CpuPowerState.Idle;

    /// <param name="onAc">Whether the adapter is in; on battery nothing goes above what the firmware had.</param>
    /// <param name="tempC">The CPU's temperature; without it the limits don't go up.</param>
    /// <param name="hot">The CPU says it is slowing down for heat (thermal, PROCHOT, critical).</param>
    /// <param name="thermalLimitC">Where the CPU starts slowing down on its own; the ceiling stays under it.</param>
    public CpuPowerTelemetry? Step(CpuPowerSettings settings, bool onAc, double? tempC, bool hot, int thermalLimitC, DateTime now)
    {
        var target = settings.Target();
        if (target is null && !_owned)
            return null;
        if (target is null)
        {
            Restore();
            return new CpuPowerTelemetry(settings.Preset, CpuPowerState.Idle, null, null, null, null, null, _overwrites);
        }

        var ceiling = Math.Min(target.CeilingC, thermalLimitC - 1);
        if (!_owned && !Capture())
            return Report(settings, target, ceiling, CpuPowerState.Refused);

        // On battery the firmware's own values are the most there is.
        var wanted1 = onAc ? target.Pl1W : Math.Min(target.Pl1W, _original!.Pl1W ?? target.Pl1W);
        var wanted2 = onAc ? target.Pl2W : Math.Min(target.Pl2W, _original!.Pl2W ?? target.Pl2W);

        if (now - _lastStep >= StepEvery)
        {
            _lastStep = now;
            _state = Advance(wanted1, wanted2, target.Pl1WindowS, onAc, tempC, hot, ceiling, Verify());
        }
        return Report(settings, target, ceiling, _refused ? CpuPowerState.Refused : _state);
    }

    /// <summary>What the CPU had before OpenSense, back in it.</summary>
    public void Restore()
    {
        if (!_owned || _original is not { Pl1W: { } pl1, Pl2W: { } pl2 })
        {
            _owned = false;
            return;
        }
        control.WriteLimits(pl1, pl2, _original.Pl1Window ?? _window);
        _owned = false;
        _refused = false;
        _state = CpuPowerState.Idle;
    }

    private bool Capture()
    {
        if (control.ReadLimits() is not { Pl1W: { } pl1, Pl2W: { } pl2 } limits || limits.Locked)
        {
            _refused = true;
            return false;
        }
        _refused = false;
        _original = limits;
        _pl1 = pl1;
        _pl2 = pl2;
        _window = limits.Pl1Window ?? 28;
        _owned = true;
        return true;
    }

    /// <summary>Something else changed the limits after we set them: count it, and set them again at this step.</summary>
    private bool Verify()
    {
        if (control.ReadLimits() is not { Pl1W: { } pl1, Pl2W: { } pl2 })
            return false;
        if (Math.Abs(pl1 - _pl1) <= 0.2 && Math.Abs(pl2 - _pl2) <= 0.2)
            return false;
        _overwrites++;
        return true;
    }

    private CpuPowerState Advance(double wanted1, double wanted2, double window, bool onAc, double? tempC, bool hot, int ceiling, bool overwritten)
    {
        double next1 = _pl1, next2 = _pl2;
        var state = CpuPowerState.Holding;
        var tooHot = hot || tempC >= ceiling - 1;
        var room = tempC is { } t && t < ceiling - Headroom && !hot;

        if (tooHot)
        {
            next1 = Math.Max(CpuPowerPresets.MinPl1W, _pl1 - BackOffStepW);
            next2 = Math.Max(next1, _pl2 - BackOffStepW);
            state = CpuPowerState.BackingOff;
        }
        else if (_pl1 > wanted1 + 0.01 || _pl2 > wanted2 + 0.01)
        {
            next1 = Math.Max(wanted1, _pl1 - DownStepW);
            next2 = Math.Max(Math.Max(wanted2, next1), _pl2 - DownStepW);
        }
        else if (room && (_pl1 < wanted1 - 0.01 || _pl2 < wanted2 - 0.01))
        {
            next1 = Math.Min(wanted1, _pl1 + UpStepW);
            next2 = Math.Min(wanted2, Math.Max(next1, _pl2 + Pl2UpStepW));
            state = CpuPowerState.Ramping;
        }
        else if (_pl1 < wanted1 - 0.01 || _pl2 < wanted2 - 0.01)
        {
            state = CpuPowerState.Holding; // wanting more, no room
        }
        if (!onAc && (_pl1 >= wanted1 - 0.01 && _pl2 >= wanted2 - 0.01))
            state = CpuPowerState.OnBattery;

        if (Math.Abs(next1 - _pl1) > 0.01 || Math.Abs(next2 - _pl2) > 0.01 || Math.Abs(window - _window) > 0.5 || overwritten)
        {
            if (control.WriteLimits(next1, next2, window))
            {
                _pl1 = next1;
                _pl2 = next2;
                _window = window;
                _refused = false;
            }
            else
            {
                _refused = true;
            }
        }
        return state;
    }

    private CpuPowerTelemetry Report(CpuPowerSettings settings, CpuPowerTarget target, int ceiling, CpuPowerState state) =>
        new(settings.Preset, state, target.Pl1W, target.Pl2W, _owned ? _pl1 : null, _owned ? _pl2 : null, ceiling, _overwrites);
}
