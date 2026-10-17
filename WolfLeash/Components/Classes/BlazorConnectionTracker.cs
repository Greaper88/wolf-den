using Microsoft.AspNetCore.Components.Server.Circuits;

namespace WolfLeash.Components.Classes;

public class BlazorConnectionTracker : CircuitHandler
{
    private readonly ActiveUserManager _userManager;
    private readonly WolfLeash.Dashboard.DashboardConnection _dashboard;

    public BlazorConnectionTracker(ActiveUserManager userManager, WolfLeash.Dashboard.DashboardConnection dashboard)
    {
        _userManager = userManager;
        _dashboard = dashboard;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _dashboard.SetConnected(true);
        _userManager.UserConnected();
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _dashboard.SetConnected(false);
        _userManager.UserDisconnected();
        return Task.CompletedTask;
    }
}