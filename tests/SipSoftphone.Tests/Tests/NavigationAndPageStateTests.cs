using System;
using Xunit;
using SipSoftphone;

namespace SipSoftphone.Tests;

/// <summary>
/// Headless tests for navigation state and command enablement in
/// <see cref="MainViewModel"/>. No window is created.
/// </summary>
public class NavigationAndPageStateTests
{
    private static (MainViewModel Vm, MockCallService Svc) NewVm()
    {
        var svc = new MockCallService();
        var vm = new MainViewModel(new AppConfig(), svc, null!);
        return (vm, svc);
    }

    [Fact]
    public void At_Startup_Calls_Page_Is_Visible_By_Default()
    {
        var (vm, _) = NewVm();

        Assert.True(vm.IsCallsPageVisible);
        Assert.False(vm.IsContactsPageVisible);
        Assert.False(vm.IsHistoryPageVisible);
        Assert.False(vm.IsSystemLogsPageVisible);
    }

    [Fact]
    public void At_Startup_Exactly_One_Page_Is_Visible()
    {
        var (vm, _) = NewVm();

        int visible = (vm.IsCallsPageVisible ? 1 : 0)
                   + (vm.IsContactsPageVisible ? 1 : 0)
                   + (vm.IsHistoryPageVisible ? 1 : 0)
                   + (vm.IsSystemLogsPageVisible ? 1 : 0);

        Assert.Equal(1, visible);
    }

    [Fact]
    public void Navigation_Switches_Visible_Page()
    {
        var (vm, _) = NewVm();

        vm.SetHistoryPageCommand.Execute(null);
        Assert.True(vm.IsHistoryPageVisible);
        Assert.False(vm.IsCallsPageVisible);

        vm.SetSystemLogsPageCommand.Execute(null);
        Assert.True(vm.IsSystemLogsPageVisible);
        Assert.False(vm.IsHistoryPageVisible);

        vm.SetContactsPageCommand.Execute(null);
        Assert.True(vm.IsContactsPageVisible);
        Assert.False(vm.IsSystemLogsPageVisible);

        vm.SetCallsPageCommand.Execute(null);
        Assert.True(vm.IsCallsPageVisible);
        Assert.False(vm.IsContactsPageVisible);
    }

    [Fact]
    public void PropertyChanged_Fires_For_Page_Visibility()
    {
        var (vm, _) = NewVm();

        bool fired = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsCallsPageVisible)) fired = true;
        };

        vm.SetHistoryPageCommand.Execute(null);
        vm.SetCallsPageCommand.Execute(null);

        Assert.True(fired);
    }

    [Fact]
    public void CallState_Is_Idle_At_Startup()
    {
        var (vm, _) = NewVm();

        Assert.Equal("Idle", vm.CallStateKey);
        Assert.True(vm.IsIdle);
        Assert.False(vm.IsActive);
        Assert.False(vm.IsRinging);
        Assert.False(vm.IsIncoming);
        Assert.False(vm.IsOngoingCall);
    }

    [Fact]
    public void EndCallLabel_Is_End_Call_When_Idle()
    {
        var (vm, _) = NewVm();
        Assert.Equal("End Call", vm.EndCallLabel);
    }

    [Fact]
    public void DurationText_Starts_Empty()
    {
        var (vm, _) = NewVm();
        Assert.Equal("", vm.DurationText);
    }

    [Fact]
    public void KeypadOpen_Defaults_To_True()
    {
        var (vm, _) = NewVm();
        Assert.True(vm.KeypadOpen);
    }

    [Fact]
    public void TransferPanel_Toggles_Open_State()
    {
        var (vm, _) = NewVm();

        Assert.False(vm.TransferPanelOpen);
        vm.ToggleTransferPanelCommand.Execute(null);
        Assert.True(vm.TransferPanelOpen);
        vm.ToggleTransferPanelCommand.Execute(null);
        Assert.False(vm.TransferPanelOpen);
    }

    [Fact]
    public void Rtl_Can_Be_Toggled()
    {
        var (vm, _) = NewVm();

        bool before = vm.IsRtl;
        vm.ToggleRtl();
        Assert.NotEqual(before, vm.IsRtl);
    }

    [Fact]
    public void Collections_Are_Exposed()
    {
        var (vm, _) = NewVm();
        Assert.NotNull(vm.Contacts);
        Assert.NotNull(vm.FilteredContacts);
        Assert.NotNull(vm.SpeedDials);
        Assert.NotNull(vm.History);
        Assert.NotNull(vm.LogItems);
    }

    [Fact]
    public void Collection_Views_Are_Null_Without_A_Dispatcher()
    {
        // The default collection views belong to the UI thread's dispatcher; headless
        // runs have none, so the view model exposes the raw collections instead.
        var (vm, _) = NewVm();
        Assert.Null(vm.HistoryView);
        Assert.Null(vm.LogItemsView);
    }

    [Fact]
    public void DialCommand_Disabled_When_Dial_Text_Empty()
    {
        var (vm, _) = NewVm();

        vm.DialText = "";
        Assert.False(vm.CallCommand.CanExecute(null));

        vm.DialText = "9121234567";
        Assert.True(vm.CallCommand.CanExecute(null));
    }

    [Fact]
    public void Hold_And_Mute_Disabled_Without_Active_Call()
    {
        var (vm, _) = NewVm();

        Assert.False(vm.ToggleHoldCommand.CanExecute(null));
        Assert.False(vm.ToggleMuteCommand.CanExecute(null));
    }
}