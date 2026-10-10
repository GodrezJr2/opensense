using OpenSense.Core.Control;
using OpenSense.Core.Hardware;

namespace OpenSense.Core.Tests;

/// <summary>
/// The operating mode that was set stays set: Acer's agents and PredatorSense write their own after OpenSense's, and
/// OpenSense reads the firmware's mode back to notice.
/// </summary>
public class ModeGuardTests
{
    private sealed class Clock
    {
        public DateTime Now { get; private set; } = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

        public DateTime Read() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class FakeCompeting(params string[] running) : ICompetingSoftware
    {
        private readonly List<string> _running = [.. running];

        public int Closes { get; private set; }

        public void Start(string name) => _running.Add(name);

        public IReadOnlyList<string> Running() => [.. _running];

        public IReadOnlyList<string> Close()
        {
            Closes++;
            var closed = _running.ToList();
            _running.Clear();
            return closed;
        }
    }

    private sealed class Rig
    {
        public required FanControlService Service { get; init; }

        public required FakeFirmware Firmware { get; init; }

        public required Clock Clock { get; init; }

        public List<ControlNotice> Notices { get; } = [];

        /// <summary>One control step, a few seconds on.</summary>
        public void Step(double seconds = 5)
        {
            Clock.Advance(TimeSpan.FromSeconds(seconds));
            Service.Tick();
        }

        public int ModeWrites => Firmware.Calls.Count(c => c.Method == "SetGamingMiscSetting" && (c.Input & 0xFF) == 0x0B);
    }

    private static Rig Create(ControlProfile profile, ICompetingSoftware? competing = null)
    {
        var firmware = new FakeFirmware { SupportsModes = true };
        var device = new AcerDevice(firmware);
        var caps = CapabilityProbe.Probe(device);
        var clock = new Clock();
        var service = new FanControlService(device, caps, new FakeLoad(), new FakePower(), profile, null, clock.Read, competing: competing);
        var rig = new Rig { Service = service, Firmware = firmware, Clock = clock };
        service.Notice += rig.Notices.Add;
        firmware.Calls.Clear();
        return rig;
    }

    private static ControlProfile Performance(bool closeCompeting = false) => new()
    {
        OperatingMode = OperatingMode.Performance,
        Safety = new SafetySettings { CloseCompetingSoftware = closeCompeting },
    };

    [Fact]
    public void A_mode_that_is_held_is_not_written_again()
    {
        var rig = Create(Performance());
        rig.Service.Tick();
        Assert.Equal(OperatingMode.Performance, rig.Firmware.Mode);
        var writes = rig.ModeWrites;

        for (var i = 0; i < 20; i++)
            rig.Step();

        Assert.Equal(writes, rig.ModeWrites);
        Assert.DoesNotContain(rig.Notices, n => n.Kind == NoticeKind.OperatingModeRestored);
    }

    [Fact]
    public void A_mode_changed_behind_its_back_is_set_again()
    {
        var rig = Create(Performance());
        rig.Service.Tick();
        rig.Firmware.ChangeMode(OperatingMode.Quiet); // PredatorSense starts and applies its own

        rig.Step();
        rig.Step(0.1); // the re-send it asked for

        Assert.Equal(OperatingMode.Performance, rig.Firmware.Mode);
        var notice = Assert.Single(rig.Notices, n => n.Kind == NoticeKind.OperatingModeRestored);
        Assert.Equal(OperatingMode.Performance, notice.OperatingMode);
        Assert.Equal("Quiet", notice.Detail);
    }

    [Fact]
    public void A_firmware_that_keeps_changing_it_is_left_alone_after_a_few_tries()
    {
        var rig = Create(Performance());
        rig.Service.Tick();

        for (var i = 0; i < 8; i++)
        {
            rig.Firmware.ChangeMode(OperatingMode.Quiet);
            rig.Step();
            rig.Step(0.1);
        }

        Assert.Equal(3, rig.Notices.Count(n => n.Kind == NoticeKind.OperatingModeRestored));
        var held = Assert.Single(rig.Notices, n => n.Kind == NoticeKind.OperatingModeNotHeld);
        Assert.Equal(OperatingMode.Performance, held.OperatingMode);
        Assert.Equal(OperatingMode.Quiet, rig.Firmware.Mode); // no longer fought over
    }

    [Fact]
    public void Waking_up_starts_the_count_again()
    {
        var rig = Create(Performance());
        rig.Service.Tick();
        for (var i = 0; i < 6; i++)
        {
            rig.Firmware.ChangeMode(OperatingMode.Quiet);
            rig.Step();
            rig.Step(0.1);
        }
        Assert.Equal(OperatingMode.Quiet, rig.Firmware.Mode);

        rig.Service.OnResume();
        rig.Step(15); // the re-send after waking

        Assert.Equal(OperatingMode.Performance, rig.Firmware.Mode);
    }

    [Fact]
    public void A_mode_the_user_chose_is_not_taken_for_a_loss()
    {
        var rig = Create(Performance());
        rig.Service.Tick();

        rig.Service.Update(Performance() with { OperatingMode = OperatingMode.Quiet });
        rig.Step();

        Assert.Equal(OperatingMode.Quiet, rig.Firmware.Mode);
        Assert.DoesNotContain(rig.Notices, n => n.Kind is NoticeKind.OperatingModeRestored or NoticeKind.OperatingModeNotHeld);
    }

    [Fact]
    public void Competing_software_is_only_reported_unless_the_user_asked_for_it_to_be_closed()
    {
        var competing = new FakeCompeting("PredatorSense");
        var rig = Create(Performance(), competing);

        for (var i = 0; i < 6; i++)
            rig.Step();

        Assert.Equal(0, competing.Closes);
        var notice = Assert.Single(rig.Notices, n => n.Kind == NoticeKind.CompetingSoftwareRunning); // once, not every check
        Assert.Equal("PredatorSense", notice.Detail);
    }

    [Fact]
    public void Competing_software_is_closed_when_asked_and_the_settings_go_out_again()
    {
        var competing = new FakeCompeting();
        var rig = Create(Performance(closeCompeting: true), competing);
        rig.Service.Tick();
        Assert.Equal(OperatingMode.Performance, rig.Firmware.Mode);

        competing.Start("PredatorSense");
        rig.Firmware.ChangeMode(OperatingMode.Quiet);
        rig.Step(); // found, closed
        rig.Step(2); // the re-send

        Assert.Equal(1, competing.Closes);
        var notice = Assert.Single(rig.Notices, n => n.Kind == NoticeKind.CompetingSoftwareClosed);
        Assert.Equal("PredatorSense", notice.Detail);
        Assert.Equal(OperatingMode.Performance, rig.Firmware.Mode);
    }

    [Fact]
    public void A_program_that_keeps_coming_back_is_closed_each_time_but_sends_the_settings_once_a_minute()
    {
        var competing = new FakeCompeting();
        var rig = Create(Performance(closeCompeting: true), competing);
        rig.Service.Tick();
        competing.Start("PredatorSense");
        rig.Step(); // closed, settings to be sent a second and six seconds on
        rig.Step(8); // both sent
        var writes = rig.ModeWrites;

        competing.Start("PredatorSense");
        rig.Step(); // closed again, within the minute
        rig.Step(8);

        Assert.Equal(2, competing.Closes);
        Assert.Equal(writes, rig.ModeWrites);
    }

    [Fact]
    public void Nothing_looks_for_competing_software_without_a_place_to_look()
    {
        var rig = Create(Performance(closeCompeting: true));
        for (var i = 0; i < 3; i++)
            rig.Step();

        Assert.DoesNotContain(rig.Notices, n => n.Kind is NoticeKind.CompetingSoftwareRunning or NoticeKind.CompetingSoftwareClosed);
    }
}
