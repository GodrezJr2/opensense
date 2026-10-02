namespace OpenSense.Core.Monitoring;

/// <summary>
/// The CPU's power-limit register (MSR_PKG_POWER_LIMIT) as the firmware left it. The firmware changes it with the
/// operating mode; when <paramref name="Locked"/> the CPU ignores writes to it until the next reset.
/// </summary>
/// <param name="Pl1Window">PL1's averaging window (tau) in seconds.</param>
/// <param name="Pl1Clamp">The CPU may go below the OS-requested P-state to keep to the limit.</param>
public sealed record PowerLimitRegister(
    double? Pl1W, bool Pl1Enabled, bool Pl1Clamp, double? Pl1Window,
    double? Pl2W, bool Pl2Enabled, bool Pl2Clamp, double? Pl2Window,
    bool Locked);

/// <summary>Voltage offsets of the CPU's planes in millivolts (negative is an undervolt); null where the CPU didn't answer for a plane.</summary>
public sealed record VoltageOffsets(double? CoreMv, double? GpuMv, double? CacheMv, double? UncoreMv, double? SystemAgentMv)
{
    /// <summary>Whether the CPU answered for any plane.</summary>
    public bool Any => CoreMv is not null || GpuMv is not null || CacheMv is not null || UncoreMv is not null || SystemAgentMv is not null;
}

/// <summary>What the CPU currently has for its limits and voltage, read from its registers (see <see cref="CpuTuningDecode"/>).</summary>
/// <param name="VidV">The core voltage the CPU is requesting from the voltage regulator right now (volts).</param>
/// <param name="Pl4W">PL4, the peak power limit in watts, kept in MSR_VR_CURRENT_CONFIG on client chips.</param>
/// <param name="Pl4Locked">PL4 is locked.</param>
/// <param name="Offsets">Voltage offsets as the CPU reports them through its overclocking mailbox; null where it doesn't answer.</param>
/// <param name="CoreOffsetReply">The mailbox's raw answer for the core plane, in hex, for telling "no offset" from "mailbox ignores us".</param>
public sealed record CpuTuning(
    PowerLimitRegister? PowerLimits,
    double? VidV,
    double? Pl4W,
    bool? Pl4Locked,
    VoltageOffsets? Offsets,
    string? CoreOffsetReply);

/// <summary>Decoding of Intel's power, voltage and mailbox registers (Intel SDM vol. 4; the mailbox layout is the one ThrottleStop and XTU use). Pure.</summary>
public static class CpuTuningDecode
{
    /// <summary>Planes of the overclocking mailbox, in the order the CPU numbers them.</summary>
    public enum Plane { Core = 0, Gpu = 1, Cache = 2, Uncore = 3, SystemAgent = 4 }

    private const int MailboxReadCommand = 0x10;
    private const ulong MailboxBusy = 1UL << 63;

    /// <param name="register">MSR 0x610.</param>
    /// <param name="wattsPerUnit">From MSR_RAPL_POWER_UNIT bits 3:0.</param>
    /// <param name="secondsPerUnit">From MSR_RAPL_POWER_UNIT bits 19:16.</param>
    public static PowerLimitRegister PowerLimits(ulong register, double wattsPerUnit, double secondsPerUnit)
    {
        var pl1 = (int)(register & 0x7FFF);
        var pl2 = (int)((register >> 32) & 0x7FFF);
        return new PowerLimitRegister(
            pl1 > 0 ? pl1 * wattsPerUnit : null, (register & (1UL << 15)) != 0, (register & (1UL << 16)) != 0, Window(register >> 17, secondsPerUnit),
            pl2 > 0 ? pl2 * wattsPerUnit : null, (register & (1UL << 47)) != 0, (register & (1UL << 48)) != 0, Window(register >> 49, secondsPerUnit),
            (register & (1UL << 63)) != 0);
    }

    /// <summary>A time window field: bits 4:0 the exponent Y, bits 6:5 the fraction F; 2^Y * (1 + F/4) time units.</summary>
    private static double? Window(ulong field, double secondsPerUnit)
    {
        var y = (int)(field & 0x1F);
        var f = (int)((field >> 5) & 0x3);
        var seconds = (1L << y) * (1 + f / 4.0) * secondsPerUnit;
        return seconds > 0 ? seconds : null;
    }

    /// <param name="perfStatus">MSR 0x198 (IA32_PERF_STATUS): bits 47:32 are the core voltage in 1/8192 V.</param>
    public static double? Vid(ulong perfStatus)
    {
        var raw = (perfStatus >> 32) & 0xFFFF;
        return raw is 0 ? null : raw / 8192.0;
    }

    /// <summary>
    /// <paramref name="register"/> (MSR 0x610) with PL1, PL1's window and PL2 replaced and both limits enabled. The clamp
    /// bits, PL2's window and the lock bit stay as they are.
    /// </summary>
    public static ulong WithPowerLimits(ulong register, double pl1W, double pl2W, double pl1WindowSeconds, double wattsPerUnit, double secondsPerUnit)
    {
        var pl1 = (ulong)Math.Clamp((int)Math.Round(pl1W / wattsPerUnit), 1, 0x7FFF);
        var pl2 = (ulong)Math.Clamp((int)Math.Round(pl2W / wattsPerUnit), 1, 0x7FFF);
        const ulong pl1Bits = 0x7FFFUL | (1UL << 15) | (0x7FUL << 17);
        const ulong pl2Bits = (0x7FFFUL << 32) | (1UL << 47);
        return (register & ~(pl1Bits | pl2Bits))
            | pl1 | (1UL << 15) | (EncodeWindow(pl1WindowSeconds, secondsPerUnit) << 17)
            | (pl2 << 32) | (1UL << 47);
    }

    /// <summary>A time window as the register has it: bits 4:0 the exponent Y, bits 6:5 the fraction F, nearest to <paramref name="seconds"/>.</summary>
    public static ulong EncodeWindow(double seconds, double secondsPerUnit)
    {
        var units = Math.Max(seconds / secondsPerUnit, 1);
        ulong best = 0;
        var bestError = double.MaxValue;
        for (var y = 0; y < 32; y++)
        {
            for (var f = 0; f < 4; f++)
            {
                var error = Math.Abs((1L << y) * (1 + f / 4.0) - units);
                if (error < bestError)
                {
                    bestError = error;
                    best = (ulong)(y | (f << 5));
                }
            }
        }
        return best;
    }

    /// <param name="vrCurrentConfig">MSR 0x601: bits 12:0 are PL4 in power units, bit 31 the lock.</param>
    public static (double? Watts, bool Locked) Pl4(ulong vrCurrentConfig, double wattsPerUnit)
    {
        var units = (int)(vrCurrentConfig & 0x1FFF);
        return (units > 0 ? units * wattsPerUnit : null, (vrCurrentConfig & (1UL << 31)) != 0);
    }

    /// <summary>The value to write to MSR 0x150 to ask for <paramref name="plane"/>'s voltage offset.</summary>
    public static ulong MailboxReadRequest(Plane plane) => MailboxBusy | ((ulong)plane << 40) | ((ulong)MailboxReadCommand << 32);

    /// <summary>Whether the mailbox has finished (it clears the top bit when it has answered).</summary>
    public static bool MailboxDone(ulong reply) => (reply & MailboxBusy) == 0;

    /// <summary>
    /// The offset in millivolts from a mailbox answer: bits 31:21 are a signed 11-bit count of 1/1024 V. Null when the
    /// answer isn't one (the mailbox is still busy, or the read failed). A zero answer is a zero offset, though a
    /// mailbox that ignores the request can look the same, which is why the raw answer is reported too.
    /// </summary>
    public static double? OffsetMv(ulong reply)
    {
        if (!MailboxDone(reply) || reply == ulong.MaxValue)
            return null;
        var count = (int)((reply >> 21) & 0x7FF);
        if (count >= 0x400)
            count -= 0x800;
        return count * 1000.0 / 1024.0;
    }
}
