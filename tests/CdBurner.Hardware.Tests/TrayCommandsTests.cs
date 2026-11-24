using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>Eject and load on a drive that closes its own tray and on a spring tray that does not.</summary>
public sealed class TrayCommandsTests
{
    /// <summary>GET CONFIGURATION RT=2 feature 0x0003, captured from the GP60NB50 on 2026-11-11.</summary>
    private const string Gp60nb50RemovableMedium = "0000000C0000000800030B042900000000000000";

    private const byte StartStopUnit = 0x1B;

    private static void NoSleep(TimeSpan _) { }

    private static ScsiExecutionResult TestUnitReady(IScsiTransport drive) =>
        drive.ExecuteCommand(new byte[6], Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(1));

    [Fact]
    public void Simulator_ReportsTheSameRemovableMediumFeatureAsTheGp60nb50()
    {
        var recording = new Recording(Convert.FromHexString(Gp60nb50RemovableMedium));

        Assert.Equal(TrayReload.ReadRemovableMedium(recording), TrayReload.ReadRemovableMedium(new VirtualMmcDrive()));
    }

    [Fact]
    public void Eject_OnADriveWithATray_OpensIt()
    {
        var drive = new VirtualMmcDrive { CanLoadTray = true };

        var result = TrayCommands.Eject(drive);

        Assert.True(result.Success);
        Assert.Equal(1, drive.TrayEjects);
        Assert.Equal("Tray opened.", result.Message);
        Assert.Equal(0x3A, TestUnitReady(drive).SenseInfo!.Asc); // medium not present while the tray is open
    }

    [Fact]
    public void Load_OnADriveWithATray_ClosesItAndWaitsUntilReady()
    {
        var drive = new VirtualMmcDrive { CanLoadTray = true };
        TrayCommands.Eject(drive);
        var waits = new List<TimeSpan>();

        var result = TrayCommands.Load(drive, waits.Add, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.CommandSent);
        Assert.False(result.NeedsManualClose);
        Assert.True(result.DiscPresent);
        Assert.Equal(1, drive.TrayLoads);
        Assert.NotEmpty(waits); // the drive answered "becoming ready" after the load and was polled again
        Assert.True(TestUnitReady(drive).IsSuccess);
    }

    [Fact]
    public void Load_OnAnEmptyDriveWithATray_ClosesItAndSaysThereIsNoDisc()
    {
        var drive = new VirtualMmcDrive { CanLoadTray = true, DiscInTray = false };
        TrayCommands.Eject(drive);

        var result = TrayCommands.Load(drive, NoSleep, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.DiscPresent);
        Assert.Contains("no disc", result.Message);
        Assert.Equal(1, drive.TrayLoads);
    }

    [Fact]
    public void Eject_OnASpringTray_OpensItAndSaysItHasToBePushedInByHand()
    {
        var drive = new VirtualMmcDrive();

        var result = TrayCommands.Eject(drive);

        Assert.True(result.Success);
        Assert.Equal(1, drive.TrayEjects);
        Assert.Contains("push it in by hand", result.Message);
    }

    [Fact]
    public void Load_OnASpringTray_SendsNothingAndSaysItCannotCloseItself()
    {
        var drive = new VirtualMmcDrive();
        TrayCommands.Eject(drive);

        var result = TrayCommands.Load(drive, NoSleep, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.CommandSent);
        Assert.True(result.NeedsManualClose);
        Assert.Contains("cannot close its own tray", result.Message);
        Assert.Contains("Push the tray in by hand", result.Message);
        Assert.Equal(0, drive.TrayLoads);
        Assert.False(TestUnitReady(drive).IsSuccess); // still open
    }

    [Fact]
    public void Load_OnAPopUpTrayEvenWithTheLoadBit_IsNotTried()
    {
        var drive = new VirtualMmcDrive { LoadingMechanism = 2, CanLoadTray = true };
        TrayCommands.Eject(drive);

        var result = TrayCommands.Load(drive, NoSleep, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.NeedsManualClose);
        Assert.Contains("pop-up tray", result.Message);
        Assert.Equal(0, drive.TrayLoads);
    }

    [Fact]
    public void Load_OnTheGp60nb50AsCaptured_SendsNoStartStopUnit()
    {
        var transport = new Recording(Convert.FromHexString(Gp60nb50RemovableMedium));

        var result = TrayCommands.Load(transport, NoSleep, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.NeedsManualClose);
        Assert.DoesNotContain(StartStopUnit, transport.Opcodes);
    }

    [Fact]
    public void Load_WhenTheDriveDoesNotReportTheFeatureAndRefuses_SaysToPushTheTrayInByHand()
    {
        var transport = new Recording(featureReply: null, refuseStartStop: true);

        var result = TrayCommands.Load(transport, NoSleep, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.CommandSent);
        Assert.True(result.NeedsManualClose);
        Assert.Contains("push it in by hand", result.Message);
        Assert.Contains(StartStopUnit, transport.Opcodes);
    }

    [Fact]
    public void Eject_SendsStartStopUnitWithLoEjSetAndStartClear()
    {
        var transport = new Recording(Convert.FromHexString(Gp60nb50RemovableMedium));

        TrayCommands.Eject(transport);

        // MMC-6 START STOP UNIT: byte 4 bit 1 LoEj, bit 0 Start; LoEj=1 Start=0 ejects.
        byte[] cdb = transport.Cdbs.Single(c => c[0] == StartStopUnit);
        Assert.Equal(0x02, cdb[4] & 0x03);
    }

    [Fact]
    public void Load_RespectsCancellationBeforeSendingCommands()
    {
        var transport = new Recording(featureReply: null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => TrayCommands.Load(transport, NoSleep, cts.Token));
        Assert.Empty(transport.Cdbs);
    }

    [Fact]
    public void Load_WhenDriveNeverBecomesReady_TimesOutAndReturnsFailure()
    {
        var drive = new VirtualMmcDrive { CanLoadTray = true };
        TrayCommands.Eject(drive);
        var notReady = new AlwaysNotReadyAfterLoadTransport(drive);

        var result = TrayCommands.Load(notReady, NoSleep, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not ready within", result.Message);
    }

    private sealed class AlwaysNotReadyAfterLoadTransport(VirtualMmcDrive inner) : IScsiTransport
    {
        public ScsiExecutionResult ExecuteCommand(ReadOnlySpan<byte> cdb, Span<byte> data, byte direction, TimeSpan timeout)
        {
            if (cdb[0] == 0x00) // TEST UNIT READY
            {
                byte[] sense = new byte[18];
                sense[0] = 0x70; sense[2] = 0x02; sense[12] = 0x04; sense[13] = 0x01;
                return ScsiExecutionResult.CheckCondition(0x02, 0, sense);
            }
            return inner.ExecuteCommand(cdb, data, direction, timeout);
        }

        public void Dispose() { }
    }

    /// <summary>A transport that answers GET CONFIGURATION with a fixed reply (or fails it) and records every command.</summary>
    private sealed class Recording(byte[]? featureReply, bool refuseStartStop = false) : IScsiTransport
    {
        public List<byte[]> Cdbs { get; } = [];

        public IEnumerable<byte> Opcodes => Cdbs.Select(c => c[0]);

        public void Dispose()
        {
        }

        public ScsiExecutionResult ExecuteCommand(ReadOnlySpan<byte> cdb, Span<byte> data, byte direction, TimeSpan timeout)
        {
            Cdbs.Add(cdb.ToArray());
            if (cdb[0] == 0x46)
            {
                if (featureReply is null)
                {
                    return ScsiExecutionResult.Failed(5, "Not supported");
                }
                int n = Math.Min(featureReply.Length, data.Length);
                featureReply.AsSpan(0, n).CopyTo(data);
                return ScsiExecutionResult.Success((uint)n);
            }
            if (cdb[0] == StartStopUnit && refuseStartStop)
            {
                return ScsiExecutionResult.Failed(5, "Refused");
            }
            return ScsiExecutionResult.Success(0);
        }
    }
}
