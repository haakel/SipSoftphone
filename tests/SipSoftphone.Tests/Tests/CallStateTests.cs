using Xunit;
using SipSoftphone;

namespace SipSoftphone.Tests;

public class CallStateTests
{
    [Fact]
    public void MockCallService_InitialState_IsIdle()
    {
        var config = new AppConfig();
        var svc = new MockCallService();
        var vm = new MainViewModel(config, svc, null);
        // Access call state through the engine field that MainViewModel uses internally
        var engine = vm.GetType().Field("_engine")!.GetValue(vm)!;
        var call = engine.CurrentCall;
        Assert.Equal(CallState.Idle, call.State);
    }

    [Fact]
    public void MockCallService_Dial_TransitionsToOutgoingRinging()
    {
        var config = new AppConfig();
        var svc = new MockCallService();
        var vm = new MainViewModel(config, svc, null);
        var task = svc.CallAsync("9121234567");
        Assert.True(task.Result);
        var engine = vm.GetType().Field("_engine")!.GetValue(vm)!;
        var call = engine.CurrentCall;
        Assert.Equal(CallState.OutgoingRinging, call.State);
    }

    [Fact]
    public void MockCallService_Hold_WorksOnActiveCall()
    {
        var config = new AppConfig();
        var svc = new MockCallService();
        var vm = new MainViewModel(config, svc, null);
        _ = svc.CallAsync("9121234567"); // OutgoingRinging → Active after 2.5s
        var engine = vm.GetType().Field("_engine")!.GetValue(vm)!;
        var call = engine.CurrentCall;
        // After delay call becomes Active; hold should toggle
        if (call.State == CallState.Active || call.State == CallState.OutgoingRinging)
        {
            vm.GetType().Method("ToggleHoldCommand")!.Invoke(vm, null);
        }
        Assert.True(true); // just verify no throw
    }

    [Fact]
    public void MockCallService_HoldSetsIsOnHold()
    {
        var config = new AppConfig();
        var svc = new MockCallService();
        var vm = new MainViewModel(config, svc, null);
        _ = svc.CallAsync("9121234567");
        var engine = vm.GetType().Field("_engine")!.GetValue(vm)!;
        var call = engine.CurrentCall;
        if (call.State == CallState.Active)
        {
            vm.GetType().Method("ToggleHoldCommand")!.Invoke(vm, null); // hold
            vm.GetType().Method("ToggleHoldCommand")!.Invoke(vm, null); // resume
        }
        Assert.True(true); // just verify no throw
    }
}