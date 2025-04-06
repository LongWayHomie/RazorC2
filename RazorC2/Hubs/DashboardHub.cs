// Hubs/DashboardHub.cs
using Microsoft.AspNetCore.SignalR;
using RazorC2.Services;

namespace RazorC2.Hubs
{
    // This hub allows the server to push updates to connected UI clients.
    // We don't need client-to-server methods for this use case.
    public class DashboardHub : Hub
    {
        private readonly ImplantManagerService _implantManager;

        // --- Add Constructor Injection ---
        public DashboardHub(ImplantManagerService implantManager)
        {
            _implantManager = implantManager;
        }
        // ----------------------------------

        public override async Task OnConnectedAsync()
        {
            //Console.WriteLine($"[DashboardHub] UI Client Connected: {Context.ConnectionId}"); //noisy

            // *** Send current state to the connecting client ***
            try
            {
                var currentImplants = _implantManager.GetAllImplants();
                // Send only to the client that just connected
                await Clients.Caller.SendAsync("UpdateImplantList", currentImplants);
                //Console.WriteLine($"[DashboardHub] Sent initial implant list ({currentImplants.Count()} items) to {Context.ConnectionId}"); //noisy

                // Optionally send initial logs too?
                var currentLogs = _implantManager.GetLogMessages();
                await Clients.Caller.SendAsync("InitialLogView", currentLogs);
                //Console.WriteLine($"[DashboardHub] Sent initial log list ({currentLogs.Count()} items) to {Context.ConnectionId}"); //noisy

            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DashboardHub] Error sending initial state to {Context.ConnectionId}: {ex.Message}");
            }
            // ***********************************************

            await base.OnConnectedAsync();
        }

        // Optional: Called when a client disconnects.
        public override Task OnDisconnectedAsync(Exception? exception)
        {
            //Console.WriteLine($"[DashboardHub] UI Client Disconnected: {Context.ConnectionId}"); //noisy
            if (exception != null)
            {
                Console.WriteLine($"[DashboardHub] Disconnect Exception: {exception.Message}");
            }
            return base.OnDisconnectedAsync(exception);
        }
    }
}