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

public sealed class CpuTuningDecodeTests
{
    private const double WattsPerUnit = 0.125;
    private const double SecondsPerUnit = 1.0 / 1024;

    [Fact]
    public void Power_limit_register_gives_both_limits_their_windows_and_flags()
    {
        // As ThrottleStop saved it: PL1 45 W for 112 s, PL2 65 W.
        var limits = CpuTuningDecode.PowerLimits(0x00438208_00E18168UL, WattsPerUnit, SecondsPerUnit);
        Assert.Equal(45, limits.Pl1W);
        Assert.True(limits.Pl1Enabled);
        Assert.True(limits.Pl1Clamp);
        Assert.Equal(112, limits.Pl1Window!.Value, 6);
        Assert.Equal(65, limits.Pl2W);
        Assert.True(limits.Pl2Enabled);
        Assert.False(limits.Locked);
    }

    [Fact]
    public void Power_limit_register_reports_its_lock()
    {
        Assert.True(CpuTuningDecode.PowerLimits(1UL << 63 | 0x8168, WattsPerUnit, SecondsPerUnit).Locked);
    }

    [Fact]
    public void Core_voltage_is_in_eighths_of_a_thousandth_of_a_volt()
    {
        Assert.Equal(1.4, CpuTuningDecode.Vid(0x2CCDUL << 32)!.Value, 3);
        Assert.Null(CpuTuningDecode.Vid(0));
    }

    [Fact]
    public void Pl4_is_in_power_units_with_a_lock_bit()
    {
        // 0x7B0 as read from this laptop's CPU, and as ThrottleStop had saved it: 246 W.
        var (watts, locked) = CpuTuningDecode.Pl4(0x8000_0000UL | 0x07B0, WattsPerUnit);
        Assert.Equal(246, watts);
        Assert.True(locked);
        Assert.False(CpuTuningDecode.Pl4(0x07B0, WattsPerUnit).Locked);
    }

    [Theory]
    [InlineData(0UL, 0.0)]
    [InlineData(0x79AUL << 21, -99.609375)] // 11-bit -102 counts of 1/1024 V
    [InlineData(0x066UL << 21, 99.609375)]
    public void Mailbox_answer_is_a_signed_offset(ulong reply, double expectedMv)
    {
        Assert.Equal(expectedMv, CpuTuningDecode.OffsetMv(reply)!.Value, 6);
    }

    [Fact]
    public void Mailbox_answer_still_busy_or_failed_is_no_answer()
    {
        Assert.Null(CpuTuningDecode.OffsetMv(CpuTuningDecode.MailboxReadRequest(CpuTuningDecode.Plane.Core)));
        Assert.Null(CpuTuningDecode.OffsetMv(ulong.MaxValue));
    }

    [Fact]
    public void Mailbox_read_request_names_the_plane()
    {
        Assert.Equal(0x8000_0010_0000_0000UL, CpuTuningDecode.MailboxReadRequest(CpuTuningDecode.Plane.Core));
        Assert.Equal(0x8000_0210_0000_0000UL, CpuTuningDecode.MailboxReadRequest(CpuTuningDecode.Plane.Cache));
    }
}
