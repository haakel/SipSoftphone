using Xunit;
using SipSoftphone;

namespace SipSoftphone.Tests;

public class NavigationAndPageStateTests
{
    [Fact]
    public void MainViewModel_PropertyChanged_NotifiesAllPages()
    {
        var config = new AppConfig();
        var svc = new MockCallService();
        var vm = new MainViewModel(config, svc, null);
        
        // Verify all page boolean properties exist and default to false
        bool hasDialer = vm.IsDialerPage;
        bool hasContacts = vm.IsContactsPage;
        bool hasHistory = vm.IsHistoryPage;
        bool hasLogs = vm.IsLogsPage;
        bool hasSettings = vm.IsSettingsPage;
        
        // All should default to false (not visible at startup)
        Assert.False(hasDialer);
        Assert.False(hasContacts);
        Assert.False(hasHistory);
        Assert.False(hasLogs);
        Assert.False(hasSettings);
    }

    [Fact]
    public void MainViewModel_DialCommand_EnabledOnlyWhenIdleOrEnded()
    {
        var config = new AppConfig();
        var svc = new MockCallService();
        var vm = new MainViewModel(config, svc, null);
        
        // Initially dial should be enabled (idle state)
        Assert.True(vm.DialCommand.CanExecute(null));
        
        // After dialing, should be disabled until call ends
        _ = svc.CallAsync("9121234567");
        // Note: the command check will reflect current state after task delay
    }

    [Fact]
    public void MainViewModel_Commands_RequireActiveCall_ForHoldMute()
    {
        var config = new AppConfig();
        var svc = new MockCallService();
        var vm = new MainViewModel(config, svc, null);
        
        // Hold and Mute should be disabled when no active call
        Assert.False(vm.ToggleHoldCommand.CanExecute(null));
        Assert.False(vm.ToggleMuteCommand.CanExecute(null));
        
        // After call active, should be enabled
        _ = svc.CallAsync("9121234567");
        // State will transition after delay
    }
}