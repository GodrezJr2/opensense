using OpenSense.Core.Control;
using OpenSense.Core.Monitoring;

namespace OpenSense.Core.Tests;

public sealed class PowerSensorTests
{
    private const double JoulesPerCount = 1.0 / 16384; // the usual 2^-14 J energy unit

    [Fact]
    public void Package_power_is_energy_over_time()
    {
        // 1,638,400 counts in 2 s is 100 J over 2 s.
        Assert.Equal(50.0, IntelPackagePower.Watts(1_000, 1_000 + 1_638_400, JoulesPerCount, 2.0)!.Value, 6);
    }

    [Fact]
    public void Package_power_survives_the_counter_wrapping()
    {
        Assert.Equal(50.0, IntelPackagePower.Watts(uint.MaxValue - 99, 1_638_300, JoulesPerCount, 2.0)!.Value, 6);
    }

    [Fact]
    public void Package_power_needs_a_long_enough_interval_and_a_plausible_result()
    {
        Assert.Null(IntelPackagePower.Watts(0, 1_000, JoulesPerCount, 0.05));
        Assert.Null(IntelPackagePower.Watts(0, 100_000_000, JoulesPerCount, 1.0));
    }

    [Fact]
    public void Total_power_adds_what_is_read_and_is_null_without_any()
    {
        Assert.Null(new Telemetry { Timestamp = DateTime.UtcNow }.TotalPower);
        Assert.Equal(40, new Telemetry { Timestamp = DateTime.UtcNow, CpuPower = 40 }.TotalPower);
        Assert.Equal(95, new Telemetry { Timestamp = DateTime.UtcNow, CpuPower = 45, GpuPower = 50 }.TotalPower);
    }
}
