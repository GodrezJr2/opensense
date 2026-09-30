namespace OpenSense.Core.Monitoring;

/// <summary>Why an Intel CPU package is holding itself back right now (IA32_PACKAGE_THERM_STATUS status bits).</summary>
[Flags]
public enum CpuThrottleReason
{
    None = 0,

    /// <summary>The package is at its thermal limit and slowing down (bit 0).</summary>
    Thermal = 1,

    /// <summary>PROCHOT# was asserted, by the laptop's firmware or a hot component (bit 2).</summary>
    Prochot = 2,

    /// <summary>The package hit its critical temperature (bit 4).</summary>
    Critical = 4,

    /// <summary>A power limit (PL1, PL2) is holding the clocks down (bit 10).</summary>
    PowerLimit = 8,

    /// <summary>The current limit (ICCmax) is holding the clocks down (bit 12).</summary>
    CurrentLimit = 16,

    /// <summary>Another domain, e.g. the GPU, is pulling the package down (bit 14).</summary>
    CrossDomain = 32,
}

/// <summary>What the GPU driver says is holding the clocks down (NVML clocks event reasons).</summary>
public static class GpuThrottle
{
    public const ulong Idle = 0x1;
    public const ulong ApplicationClocks = 0x2;
    public const ulong SoftwarePowerCap = 0x4;
    public const ulong HardwareSlowdown = 0x8;
    public const ulong SyncBoost = 0x10;
    public const ulong SoftwareThermalSlowdown = 0x20;
    public const ulong HardwareThermalSlowdown = 0x40;
    public const ulong HardwarePowerBrake = 0x80;
    public const ulong DisplayClocks = 0x100;

    /// <summary>The reasons that mean the GPU is being held back by heat or power, not just idling.</summary>
    public const ulong Limiting = SoftwarePowerCap | HardwareSlowdown | SoftwareThermalSlowdown | HardwareThermalSlowdown | HardwarePowerBrake;

    public static IReadOnlyList<string> Describe(ulong reasons)
    {
        List<string> names = [];
        if ((reasons & Idle) != 0)
            names.Add("idle");
        if ((reasons & ApplicationClocks) != 0)
            names.Add("application clocks setting");
        if ((reasons & SoftwarePowerCap) != 0)
            names.Add("power cap (software)");
        if ((reasons & HardwareSlowdown) != 0)
            names.Add("hardware slowdown");
        if ((reasons & SyncBoost) != 0)
            names.Add("sync boost");
        if ((reasons & SoftwareThermalSlowdown) != 0)
            names.Add("thermal slowdown (software)");
        if ((reasons & HardwareThermalSlowdown) != 0)
            names.Add("thermal slowdown (hardware)");
        if ((reasons & HardwarePowerBrake) != 0)
            names.Add("power brake (hardware)");
        if ((reasons & DisplayClocks) != 0)
            names.Add("display clocks setting");
        var known = Idle | ApplicationClocks | SoftwarePowerCap | HardwareSlowdown | SyncBoost | SoftwareThermalSlowdown | HardwareThermalSlowdown | HardwarePowerBrake | DisplayClocks;
        if ((reasons & ~known) != 0)
            names.Add($"other (0x{reasons & ~known:X})");
        return names;
    }
}

/// <summary>Clocks, throttle reasons and the enforced power limit, as the NVIDIA driver reports them.</summary>
/// <param name="MaxPowerLimitWatts">The most the driver lets the board draw (the laptop's total graphics power, TGP).</param>
/// <param name="DefaultPowerLimitWatts">The power limit the GPU ships with.</param>
/// <param name="MemoryGb">Video memory.</param>
public sealed record GpuStatus(int? CoreMhz, int? MemoryMhz, ulong? ThrottleReasons, double? PowerLimitWatts,
    double? MaxPowerLimitWatts = null, double? DefaultPowerLimitWatts = null, double? MemoryGb = null);

public interface IGpuStatusSensor : IDisposable
{
    /// <summary>Null when the driver answers nothing; call only while the GPU is awake, like the temperature.</summary>
    GpuStatus? Read();
}

internal sealed unsafe class NvmlGpuStatusSensor : IGpuStatusSensor
{
    private const int ClockGraphics = 0;
    private const int ClockMemory = 2;

    private readonly NvmlLibrary _nvml;
    private readonly delegate* unmanaged<nint, int, uint*, int> _clock;
    private readonly delegate* unmanaged<nint, ulong*, int> _reasons;
    private readonly delegate* unmanaged<nint, uint*, int> _powerLimit;
    private readonly delegate* unmanaged<nint, uint*, uint*, int> _powerConstraints;
    private readonly delegate* unmanaged<nint, uint*, int> _powerDefault;
    private readonly delegate* unmanaged<nint, ulong*, int> _memory; // total, free, used
    private double? _maxLimit, _defaultLimit, _memoryGb; // fixed for the GPU: read once

    private NvmlGpuStatusSensor(NvmlLibrary nvml, nint clock, nint reasons, nint powerLimit)
    {
        _nvml = nvml;
        _powerConstraints = (delegate* unmanaged<nint, uint*, uint*, int>)nvml.Export("nvmlDeviceGetPowerManagementLimitConstraints");
        _powerDefault = (delegate* unmanaged<nint, uint*, int>)nvml.Export("nvmlDeviceGetPowerManagementDefaultLimit");
        _memory = (delegate* unmanaged<nint, ulong*, int>)nvml.Export("nvmlDeviceGetMemoryInfo");
        _clock = (delegate* unmanaged<nint, int, uint*, int>)clock;
        _reasons = (delegate* unmanaged<nint, ulong*, int>)reasons;
        _powerLimit = (delegate* unmanaged<nint, uint*, int>)powerLimit;
    }

    public static NvmlGpuStatusSensor? TryOpen(NvmlLibrary nvml)
    {
        var clock = nvml.Export("nvmlDeviceGetClockInfo");
        if (clock == 0)
            return null;
        // Newer drivers renamed the throttle reasons "clocks event reasons"; either answers the same bits.
        var reasons = nvml.Export("nvmlDeviceGetCurrentClocksEventReasons");
        if (reasons == 0)
            reasons = nvml.Export("nvmlDeviceGetCurrentClocksThrottleReasons");
        return new NvmlGpuStatusSensor(nvml, clock, reasons, nvml.Export("nvmlDeviceGetEnforcedPowerLimit"));
    }

    public GpuStatus? Read()
    {
        if (!_nvml.TryGetDevice(out var device))
            return null;
        uint value;
        int? core = _clock(device, ClockGraphics, &value) == NvmlLibrary.Success ? (int)value : null;
        int? memory = _clock(device, ClockMemory, &value) == NvmlLibrary.Success ? (int)value : null;
        ulong bits;
        ulong? reasons = _reasons != null && _reasons(device, &bits) == NvmlLibrary.Success ? bits : null;
        double? limit = _powerLimit != null && _powerLimit(device, &value) == NvmlLibrary.Success ? value / 1000.0 : null;
        ReadFixedFacts(device);
        return core is null && memory is null && reasons is null && limit is null ? null : new GpuStatus(core, memory, reasons, limit, _maxLimit, _defaultLimit, _memoryGb);
    }

    private void ReadFixedFacts(nint device)
    {
        if (_maxLimit is null && _powerConstraints != null)
        {
            uint min, max;
            if (_powerConstraints(device, &min, &max) == NvmlLibrary.Success && max > 0)
                _maxLimit = max / 1000.0;
        }
        if (_defaultLimit is null && _powerDefault != null)
        {
            uint value;
            if (_powerDefault(device, &value) == NvmlLibrary.Success && value > 0)
                _defaultLimit = value / 1000.0;
        }
        if (_memoryGb is null && _memory != null)
        {
            ulong* info = stackalloc ulong[3];
            if (_memory(device, info) == NvmlLibrary.Success && info[0] > 0)
                _memoryGb = Math.Round(info[0] / 1024.0 / 1024 / 1024, 1);
        }
    }

    public void Dispose()
    {
        // The library belongs to DirectSensors.
    }
}
