using System;
using System.Threading.Tasks;
using Xunit;
using SipSoftphone;

namespace SipSoftphone.Tests;

/// <summary>
/// Headless call-state tests driving the real <see cref="MockCallService"/>
/// through <see cref="MainViewModel"/>. No window, no SIP traffic.
/// </summary>
public class CallStateTests
{
    private static (MainViewModel Vm, MockCallService Svc) NewVm()
    {
        var svc = new MockCallService();
        var vm = new MainViewModel(new AppConfig(), svc, null!);
        return (vm, svc);
    }

    [Fact]
    public void Initial_CallState_IsIdle()
    {
        var (_, svc) = NewVm();
        Assert.Equal(CallState.Idle, svc.CurrentCall.State);
    }

    [Fact]
    public void Initial_ViewModel_HasNoActiveCall()
    {
        var (vm, _) = NewVm();
        Assert.False(vm.IsIncoming);
        Assert.Null(vm.SelectedContact);
    }

    [Fact]
    public async Task Dial_Transitions_Idle_To_OutgoingRinging()
    {
        var (_, svc) = NewVm();
        var ok = await svc.CallAsync("9121234567");
        Assert.True(ok);
        Assert.Equal(CallState.OutgoingRinging, svc.CurrentCall.State);
    }

    [Fact]
    public async Task Dial_While_Busy_Is_Rejected()
    {
        var (_, svc) = NewVm();
        Assert.True(await svc.CallAsync("9121234567"));
        Assert.False(await svc.CallAsync("9129999999"));
        Assert.Equal(CallState.OutgoingRinging, svc.CurrentCall.State);
    }

    [Fact]
    public async Task Call_Reaches_Active_After_Answer_Delay()
    {
        var (_, svc) = NewVm();
        await svc.CallAsync("9121234567");
        Assert.Equal(CallState.OutgoingRinging, svc.CurrentCall.State);

        await Task.Delay(3200);

        Assert.Equal(CallState.Active, svc.CurrentCall.State);
        Assert.False(svc.CurrentCall.IsOnHold);
    }

    [Fact]
    public async Task Hangup_Ends_Active_Call()
    {
        var (_, svc) = NewVm();
        await svc.CallAsync("9121234567");
        await Task.Delay(3200);
        Assert.Equal(CallState.Active, svc.CurrentCall.State);

        svc.Hangup();

        Assert.Equal(CallState.Ended, svc.CurrentCall.State);
        Assert.False(svc.CurrentCall.IsOnHold);
    }

    [Fact]
    public void Hold_Only_Applies_To_Active_Call()
    {
        var (_, svc) = NewVm();
        svc.Hold();
        Assert.False(svc.CurrentCall.IsOnHold);
    }

    [Fact]
    public async Task ToggleHold_Flips_IsOnHold_On_Active_Call()
    {
        var (_, svc) = NewVm();
        await svc.CallAsync("9121234567");
        await Task.Delay(3200);
        Assert.Equal(CallState.Active, svc.CurrentCall.State);

        Assert.True(svc.ToggleHold());
        Assert.True(svc.CurrentCall.IsOnHold);

        Assert.False(svc.ToggleHold());
        Assert.False(svc.CurrentCall.IsOnHold);
    }

    [Fact]
    public async Task Resume_Clears_IsOnHold()
    {
        var (_, svc) = NewVm();
        await svc.CallAsync("9121234567");
        await Task.Delay(3200);

        svc.Hold();
        Assert.True(svc.CurrentCall.IsOnHold);

        svc.Resume();
        Assert.False(svc.CurrentCall.IsOnHold);
    }

    [Fact]
    public void RejectPending_Ignored_When_No_Incoming_Call()
    {
        var (_, svc) = NewVm();
        svc.RejectPending();
        Assert.Equal(CallState.Idle, svc.CurrentCall.State);
    }

    [Fact]
    public void RejectPending_Clears_Incoming_Call()
    {
        var (_, svc) = NewVm();
        svc.SimulateIncoming("09123456789", "Demo Caller");
        Assert.Equal(CallState.IncomingRinging, svc.CurrentCall.State);

        svc.RejectPending();

        Assert.Equal(CallState.Idle, svc.CurrentCall.State);
    }

    [Fact]
    public async Task AnswerPending_Activates_Incoming_Call()
    {
        var (_, svc) = NewVm();
        svc.SimulateIncoming("09123456789", "Demo Caller");
        Assert.Equal(CallState.IncomingRinging, svc.CurrentCall.State);

        await svc.AnswerPendingAsync();

        Assert.Equal(CallState.Active, svc.CurrentCall.State);
    }

    [Fact]
    public void SetMicrophoneMuted_Does_Not_Throw()
    {
        var (_, svc) = NewVm();
        svc.SetMicrophoneMuted(true);
        svc.SetMicrophoneMuted(false);
    }

    [Fact]
    public async Task BlindTransfer_Fails_When_Call_Not_Active()
    {
        var (_, svc) = NewVm();
        Assert.False(await svc.BlindTransferAsync("9125550123"));
    }

    [Fact]
    public async Task BlindTransfer_Succeeds_And_Ends_Call()
    {
        var (_, svc) = NewVm();
        await svc.CallAsync("9121234567");
        await Task.Delay(3200);
        Assert.Equal(CallState.Active, svc.CurrentCall.State);

        Assert.True(await svc.BlindTransferAsync("9125550123"));
        Assert.Equal(CallState.Ended, svc.CurrentCall.State);
    }
}
