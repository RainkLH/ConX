using Microsoft.AspNetCore.SignalR;

namespace ConX.Hubs;

public class TerminalHub : Hub
{
    public override Task OnConnectedAsync()
    {
        // Optionally add to groups based on user/circuit
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        return base.OnDisconnectedAsync(exception);
    }

    public Task JoinCircuitGroup(string circuitId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, circuitId);
    }

    public Task LeaveCircuitGroup(string circuitId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, circuitId);
    }
}
