using System.Diagnostics;
using System.Runtime.Intrinsics.X86;

namespace OpenSense.Core.Monitoring;

/// <summary>A chip's electrical power draw.</summary>
public interface IPowerSensor : IDisposable
{
    /// <summary>Watts, or null when there is no reading (yet).</summary>
    double? Read();
}

/// <summary>
/// Intel package power from the RAPL energy counter (MSR_PKG_ENERGY_STATUS), read through PawnIO like the temperature:
/// the counter only counts up, so the power is the energy since the last reading over the time since then. The first
/// reading has nothing to compare with and answers null.
/// </summary>
internal sealed class IntelPackagePower : IPowerSensor
{
    private const ulong PowerUnit = 0x606;    // MSR_RAPL_POWER_UNIT; bits 12:8 = energy unit, 1/2^n joule
    private const ulong PackageEnergy = 0x611; // MSR_PKG_ENERGY_STATUS; bits 31:0 = counter, wraps

    private readonly PawnIOModule _module;
    private readonly double _joulesPerCount;
    private uint _previous;
    private long _previousTicks;
    private bool _primed;

    private IntelPackagePower(PawnIOModule module, double joulesPerCount)
    {
        _module = module;
        _joulesPerCount = joulesPerCount;
    }

    public static IPowerSensor? TryOpen()
    {
        if (!X86Base.IsSupported || CpuTemperatureSensor.Vendor() != "GenuineIntel")
            return null;
        if (PawnIOModule.TryOpen("IntelMSR", null) is not { } module)
            return null;
        var bits = module.Call("ioctl_read_msr", PowerUnit) is { } unit ? (int)((unit >> 8) & 0x1F) : 0;
        if (bits == 0)
        {
            module.Dispose();
            return null;
        }
        var sensor = new IntelPackagePower(module, 1.0 / (1L << bits));
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
