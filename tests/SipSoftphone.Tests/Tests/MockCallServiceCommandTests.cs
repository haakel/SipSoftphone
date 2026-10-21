using System;
using System.Threading.Tasks;
using Xunit;
using SipSoftphone;

namespace SipSoftphone.Tests;

/// <summary>
/// Headless tests for the command surface of <see cref="MainViewModel"/> and
/// <see cref="MockCallService"/>: answer / reject / hold / mute / transfer.
/// No window is created and no SIP traffic is generated.
/// </summary>
public class MockCallServiceCommandTests
{
    private static (MainViewModel Vm, MockCallService Svc) NewVm()
    {
        var svc = new MockCallService();
        var vm = new MainViewModel(new AppConfig(), svc, null!);
        return (vm, svc);
    }

    [Fact]
    public void Commands_Are_Exposed_And_Not_Null()
    {
        var (vm, _) = NewVm();

        Assert.NotNull(vm.CallCommand);
        Assert.NotNull(vm.AnswerCommand);
        Assert.NotNull(vm.RejectCommand);
        Assert.NotNull(vm.HangupCommand);
        Assert.NotNull(vm.HoldCommand);
        Assert.NotNull(vm.ToggleMuteCommand);
        Assert.NotNull(vm.ToggleHoldCommand);
        Assert.NotNull(vm.TransferConfirmCommand);
        Assert.NotNull(vm.DialContactCommand);
        Assert.NotNull(vm.RedialCommand);
        Assert.NotNull(vm.PadCommand);
    }

    [Fact]
    public void Navigation_Commands_Are_Exposed()
    {
        var (vm, _) = NewVm();

        Assert.NotNull(vm.SetCallsPageCommand);
        Assert.NotNull(vm.SetContactsPageCommand);
        Assert.NotNull(vm.SetHistoryPageCommand);
        Assert.NotNull(vm.SetSystemLogsPageCommand);
    }

    [Fact]
    public void SetCallsPageCommand_Makes_Calls_Page_Visible()
    {
        var (vm, _) = NewVm();

        vm.SetCallsPageCommand.Execute(null);

        Assert.True(vm.IsCallsPageVisible);
        Assert.False(vm.IsContactsPageVisible);
        Assert.False(vm.IsHistoryPageVisible);
        Assert.False(vm.IsSystemLogsPageVisible);
    }

    [Fact]
    public void SetContactsPageCommand_Makes_Contacts_Page_Visible()
    {
        var (vm, _) = NewVm();

        vm.SetContactsPageCommand.Execute(null);

        Assert.True(vm.IsContactsPageVisible);
        Assert.False(vm.IsCallsPageVisible);
    }

    [Fact]
    public void SetHistoryPageCommand_Makes_History_Page_Visible()
    {
        var (vm, _) = NewVm();

        vm.SetHistoryPageCommand.Execute(null);

        Assert.True(vm.IsHistoryPageVisible);
        Assert.False(vm.IsCallsPageVisible);
    }

    [Fact]
    public void SetSystemLogsPageCommand_Makes_Logs_Page_Visible()
    {
        var (vm, _) = NewVm();

        vm.SetSystemLogsPageCommand.Execute(null);

        Assert.True(vm.IsSystemLogsPageVisible);
        Assert.False(vm.IsCallsPageVisible);
    }

    [Fact]
    public void ToggleMuteCommand_Toggles_Mute_State()
    {
        var (vm, _) = NewVm();

        var before = vm.MuteChecked;
        vm.ToggleMuteCommand.Execute(null);
        Assert.NotEqual(before, vm.MuteChecked);

        vm.ToggleMuteCommand.Execute(null);
        Assert.Equal(before, vm.MuteChecked);
    }

    [Fact]
    public void Backspace_And_Clear_Edit_Dial_Text()
    {
        var (vm, _) = NewVm();

        vm.DialText = "9121234567";
        vm.BackspaceDialCommand.Execute(null);
        Assert.Equal("912123456", vm.DialText);

        vm.ClearDialCommand.Execute(null);
        Assert.Equal("", vm.DialText);
    }

    [Fact]
    public void PadCommand_Appends_Digits_When_Idle()
    {
        var (vm, _) = NewVm();

        vm.DialText = "";
        vm.PadCommand.Execute("1");
        vm.PadCommand.Execute("2");

        Assert.Equal("12", vm.DialText);
    }

    [Fact]
    public async Task CallCommand_Dials_Number_And_Reaches_Active()
    {
        var (vm, svc) = NewVm();

        vm.DialText = "9121234567";
        vm.CallCommand.Execute(null);

        // AsyncRelayCommand fires and forgets; give it a tick
        await Task.Delay(300);
        Assert.Equal(CallState.OutgoingRinging, svc.CurrentCall.State);

        await Task.Delay(3200);
        Assert.Equal(CallState.Active, svc.CurrentCall.State);
    }

    [Fact]
    public void AnswerCommand_Is_Noop_Without_Incoming_Call()
    {
        var (vm, svc) = NewVm();

        vm.AnswerCommand.Execute(null);

        Assert.Equal(CallState.Idle, svc.CurrentCall.State);
    }

    [Fact]
    public void AnswerCommand_Activates_Incoming_Call()
    {
        var (vm, svc) = NewVm();
        svc.SimulateIncoming("09123456789", "Demo Caller");

        vm.AnswerCommand.Execute(null);

        Assert.Equal(CallState.Active, svc.CurrentCall.State);
    }

    [Fact]
    public async Task HangupCommand_Ends_Call()
    {
        var (vm, svc) = NewVm();
        await svc.CallAsync("9121234567");

        vm.HangupCommand.Execute(null);

        Assert.Equal(CallState.Ended, svc.CurrentCall.State);
    }

    [Fact]
    public void RejectCommand_Clears_Incoming_Call()
    {
        var (vm, svc) = NewVm();
        svc.SimulateIncoming("09123456789", "Demo Caller");

        vm.RejectCommand.Execute(null);

        Assert.Equal(CallState.Idle, svc.CurrentCall.State);
    }

    [Fact]
    public void TransferConfirmCommand_Disabled_Without_Active_Call()
    {
        var (vm, _) = NewVm();

        vm.TransferText = "9125550123";
        vm.DialText = "";

        Assert.False(vm.TransferConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task TransferConfirmCommand_Reports_Missing_Target()
    {
        var (vm, svc) = NewVm();
        await svc.CallAsync("9121234567");
        await Task.Delay(3200);
        Assert.Equal(CallState.Active, svc.CurrentCall.State);

        vm.TransferText = "";
        vm.DialText = "";
        vm.TransferConfirmCommand.Execute(null);
        await Task.Delay(200);

        Assert.Equal("enter a target number", vm.TransferStatus);
    }

    [Fact]
    public void ClearHistoryCommand_Empties_History()
    {
        var (vm, _) = NewVm();

        vm.History.Add(new HistoryItem { Phone = "9121234567", Time = "now" });
        Assert.NotEmpty(vm.History);

        vm.ClearHistoryCommand.Execute(null);

        Assert.Empty(vm.History);
    }

    [Fact]
    public void ClearLogCommand_Empties_Log_Items()
    {
        var (vm, _) = NewVm();

        vm.AppendLog("headless test log line");
        Assert.NotEmpty(vm.LogItems);

        vm.ClearLogCommand.Execute(null);

        Assert.Empty(vm.LogItems);
    }

    [Fact]
    public void AppendLog_Adds_Log_Item()
    {
        var (vm, _) = NewVm();

        int before = vm.LogItems.Count;
        vm.AppendLog("headless test log line");

        Assert.Equal(before + 1, vm.LogItems.Count);
        Assert.Contains("headless test log line", vm.LogItems[^1].Text);
    }

    [Fact]
    public void LogPause_Toggles_Paused_Flag()
    {
        var (vm, _) = NewVm();

        Assert.False(vm.LogPaused);
        vm.ToggleLogPauseCommand.Execute(null);
        Assert.True(vm.LogPaused);
        vm.ToggleLogPauseCommand.Execute(null);
        Assert.False(vm.LogPaused);
    }
}