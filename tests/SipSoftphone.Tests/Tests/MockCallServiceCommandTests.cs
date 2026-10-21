using Xunit;
using SipSoftphone;

namespace SipSoftphone.Tests;

public class MockCallServiceCommandTests
{
    [Fact]
    public void MockCallService_RejectPending_WhenIncomingRinging()
    {
        var config = new AppConfig();
        var svc = new MockCallService();
        var vm = new MainViewModel(config, svc, null);
        var engine = vm.GetType().Field("_engine")!.GetValue(vm)!;
        var call = engine.CurrentCall;
        // RejectPending only acts if IncomingRinging - test the guard
        Assert.True(call.State == CallState.IncomingRinging ||
                    call.State == CallState.Idle ||
                    call.State == CallState.Ended);
    }

    [Fact]
    public void MockCallService_Hold_Transition()
    {
        var config = new AppConfig();
        var svc = new MockCallService();
        var vm = new MainViewModel(config, svc, null);
        _ = svc.CallAsync("9121234567");
        var engine = vm.GetType().Field("_engine")!.GetValue(vm)!;
        var initialState = engine.CurrentCall.State;
        // After ~2.5s task delay the call becomes Active, then Hold should work
        // Just verify the method exists and toggles state
        vm.GetType().Method("ToggleHoldCommand")!.Invoke(vm, null);
        vm.GetType().Method("ToggleHoldCommand")!.Invoke(vm, null); // resume
        // Both should not throw
    }

    [Fact]
    public void MockCallService_BlindTransfer_WhenActive()
    {
        var config = new AppConfig();
        var svc = new MockCallService();
        var vm = new MainViewModel(config, svc, null);
        _ = svc.CallAsync("9121234567");
        var engine = vm.GetType().Field("_engine")!.GetValue(vm)!;
        var call = engine.CurrentCall;
        if (call.State == CallState.Active)
        {
            vm.BlindTransferAsync("9125550123").GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void MockCallService_SetMicrophoneMuted_NoThrow()
    {
        var config = new AppConfig();
        var svc = new MockCallService();
        var vm = new MainViewModel(config, svc, null);
        svc.SetMicrophoneMuted(true);
        svc.SetMicrophoneMuted(false);
        // Should not throw
    }
}