using System.Diagnostics;
using System.Runtime.Intrinsics.X86;

namespace OpenSense.Core.Monitoring;

/// <summary>A chip's electrical power draw.</summary>
public interface IPowerSensor : IDisposable
{
    /// <summary>Watts, or null when there is no reading (yet).</summary>
    double? Read();

    /// <summary>The sustained (PL1) and short-burst (PL2) power limits in watts, where the chip tells them.</summary>
    (double? Pl1, double? Pl2) Limits => (null, null);

    /// <summary>The processor's base power (TDP) in watts, where the chip says it.</summary>
    double? BaseTdp => null;

    /// <summary>The chip's power-limit and voltage registers as they are now; null where the chip doesn't say.</summary>
    CpuTuning? Tuning() => null;
}

/// <summary>Setting the CPU's sustained and burst power limits.</summary>
public interface ICpuPowerControl
{
    /// <summary>The limits as the CPU has them now; null where they can't be read.</summary>
    PowerLimitRegister? ReadLimits();

    /// <summary>Sets PL1, PL2 and PL1's window. False when the CPU refuses (the register is locked) or doesn't take the value.</summary>
    bool WriteLimits(double pl1W, double pl2W, double pl1WindowSeconds);
}

/// <summary>
/// Intel package power from the RAPL energy counter (MSR_PKG_ENERGY_STATUS), read through PawnIO like the temperature:
/// the counter only counts up, so the power is the energy since the last reading over the time since then. The first
/// reading has nothing to compare with and answers null.
/// </summary>
internal sealed class IntelPackagePower : IPowerSensor, ICpuPowerControl
{
    private const ulong PowerUnit = 0x606;    // MSR_RAPL_POWER_UNIT; bits 12:8 = energy unit, 1/2^n joule
    private const ulong PackageEnergy = 0x611; // MSR_PKG_ENERGY_STATUS; bits 31:0 = counter, wraps
    private const ulong PackagePowerInfo = 0x614;  // MSR_PKG_POWER_INFO; bits 14:0 = TDP, in power units
    private const ulong PackagePowerLimit = 0x610; // MSR_PKG_POWER_LIMIT; bits 14:0 = PL1, 46:32 = PL2, in power units
    private const ulong PerfStatus = 0x198;        // IA32_PERF_STATUS; bits 47:32 = core voltage, 1/8192 V
    private const ulong VrCurrentConfig = 0x601;   // MSR_VR_CURRENT_CONFIG; bits 12:0 = PL4, in power units
    private const ulong OcMailbox = 0x150;         // MSR_OC_MAILBOX: voltage offsets per plane

    private readonly PawnIOModule _module;
    private readonly double _joulesPerCount;
    private readonly double _wattsPerUnit;
    private readonly double _secondsPerUnit;
    private VoltageOffsets? _offsets; // asked once: the mailbox is written to, so not on every sample
    private string? _coreReply;
    private bool _offsetsRead;
    private uint _previous;
    private long _previousTicks;
    private bool _primed;

    private IntelPackagePower(PawnIOModule module, double joulesPerCount, double wattsPerUnit, double secondsPerUnit)
    {
        _module = module;
        _joulesPerCount = joulesPerCount;
        _wattsPerUnit = wattsPerUnit;
        _secondsPerUnit = secondsPerUnit;
    }

    public (double? Pl1, double? Pl2) Limits =>
        _module.Call("ioctl_read_msr", PackagePowerLimit) is { } limits
            ? (LimitWatts((int)(limits & 0x7FFF)), LimitWatts((int)((limits >> 32) & 0x7FFF)))
            : (null, null);

    public double? BaseTdp => _module.Call("ioctl_read_msr", PackagePowerInfo) is { } info ? LimitWatts((int)(info & 0x7FFF)) : null;

    public CpuTuning? Tuning()
    {
        var limits = _module.Call("ioctl_read_msr", PackagePowerLimit) is { } register ? CpuTuningDecode.PowerLimits(register, _wattsPerUnit, _secondsPerUnit) : null;
        var vid = _module.Call("ioctl_read_msr", PerfStatus) is { } status ? CpuTuningDecode.Vid(status) : null;
        var current = _module.Call("ioctl_read_msr", VrCurrentConfig) is { } config ? CpuTuningDecode.Pl4(config, _wattsPerUnit) : ((double?, bool)?)null;
        if (!_offsetsRead)
        {
            _offsetsRead = true;
            ReadOffsets();
        }
        return limits is null && vid is null && current is null && _offsets is null
            ? null
            : new CpuTuning(limits, vid, current?.Item1, current?.Item2, _offsets, _coreReply);
    }

    public PowerLimitRegister? ReadLimits() =>
        _module.Call("ioctl_read_msr", PackagePowerLimit) is { } register ? CpuTuningDecode.PowerLimits(register, _wattsPerUnit, _secondsPerUnit) : null;

    public bool WriteLimits(double pl1W, double pl2W, double pl1WindowSeconds)
    {
        if (_module.Call("ioctl_read_msr", PackagePowerLimit) is not { } register || (register & (1UL << 63)) != 0)
            return false;
        var wanted = CpuTuningDecode.WithPowerLimits(register, pl1W, pl2W, pl1WindowSeconds, _wattsPerUnit, _secondsPerUnit);
        return _module.Call("ioctl_write_msr", [PackagePowerLimit, wanted]) && _module.Call("ioctl_read_msr", PackagePowerLimit) == wanted;
    }

    /// <summary>Asks the CPU for each plane's voltage offset. Reading one is a request written to the mailbox, which sets nothing.</summary>
    private void ReadOffsets()
    {
        double?[] mv = new double?[5];
        for (var plane = 0; plane < mv.Length; plane++)
        {
            var reply = AskMailbox((CpuTuningDecode.Plane)plane);
            mv[plane] = reply is { } r ? CpuTuningDecode.OffsetMv(r) : null;
            if (plane == 0)
                _coreReply = reply is { } raw ? $"0x{raw:X16}" : null;
        }
        _offsets = new VoltageOffsets(mv[0], mv[1], mv[2], mv[3], mv[4]);
    }

    private ulong? AskMailbox(CpuTuningDecode.Plane plane)
    {
        if (!_module.Call("ioctl_write_msr", [OcMailbox, CpuTuningDecode.MailboxReadRequest(plane)]))
            return null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (_module.Call("ioctl_read_msr", OcMailbox) is { } reply && CpuTuningDecode.MailboxDone(reply))
                return reply;
            Thread.Sleep(1);
        }
        return null;
    }

    private double? LimitWatts(int units) => units > 0 ? units * _wattsPerUnit : null;

    public static IPowerSensor? TryOpen()
    {
        if (!X86Base.IsSupported || CpuTemperatureSensor.Vendor() != "GenuineIntel")
            return null;
        if (PawnIOModule.TryOpen("IntelMSR", null) is not { } module)
            return null;
        var unit = module.Call("ioctl_read_msr", PowerUnit) ?? 0;
        var bits = (int)((unit >> 8) & 0x1F);
        if (bits == 0)
        {
            module.Dispose();
            return null;
        }
        var sensor = new IntelPackagePower(module, 1.0 / (1L << bits), 1.0 / (1L << (int)(unit & 0xF)), 1.0 / (1L << (int)((unit >> 16) & 0xF)));
        sensor.Read(); // the first reading has nothing to compare with; it only tells whether the counter answers
        if (sensor._primed)
            return sensor;
        sensor.Dispose();
        return null;
    }

    public double? Read()
    {
        if (_module.Call("ioctl_read_msr", PackageEnergy) is not { } counter)
            return null;
        var current = (uint)counter;
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_previousTicks, now).TotalSeconds;
        var previous = _previous;
        var wasPrimed = _primed;
        _previous = current;
        _previousTicks = now;
        _primed = true;
        return wasPrimed ? Watts(previous, current, _joulesPerCount, elapsed) : null;
    }

    /// <summary>Watts from two readings of the 32-bit energy counter, which wraps; null when the interval is too short to trust.</summary>
    internal static double? Watts(uint previous, uint current, double joulesPerCount, double seconds)
    {
        if (seconds < 0.2)
            return null;
        var watts = unchecked(current - previous) * joulesPerCount / seconds;
        return watts is >= 0 and < 500 ? watts : null;
    }

    public void Dispose() => _module.Dispose();
}

/// <summary>
/// GPU board power from the NVIDIA driver (NVML). Like the temperature, call <see cref="Read"/> only while the GPU is
/// already awake: on Optimus laptops a query can power a sleeping GPU back up.
/// </summary>
internal sealed unsafe class NvmlGpuPower : IPowerSensor
{
    private readonly NvmlLibrary _nvml;
    private readonly delegate* unmanaged<nint, uint*, int> _powerUsage; // milliwatts

    private NvmlGpuPower(NvmlLibrary nvml, nint powerUsage)
    {
        _nvml = nvml;
        _powerUsage = (delegate* unmanaged<nint, uint*, int>)powerUsage;
    }

    public static NvmlGpuPower? TryOpen(NvmlLibrary nvml) =>
        nvml.Export("nvmlDeviceGetPowerUsage") is not 0 and var usage ? new NvmlGpuPower(nvml, usage) : null;

    public double? Read()
    {
        if (!_nvml.TryGetDevice(out var device))
            return null;
        uint milliwatts;
        return _powerUsage(device, &milliwatts) == NvmlLibrary.Success ? milliwatts / 1000.0 : null;
    }

    public void Dispose()
    {
        // The library belongs to DirectSensors.
    }
}
