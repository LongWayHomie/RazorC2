// Models/ImplantInfo.cs
using System.Collections.Concurrent;
using System.Collections.Generic; // Required for List

namespace RazorC2.Models
{
    public class ImplantInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public string? RemoteAddress { get; set; }
        public string? Hostname { get; set; }
        public string? Username { get; set; }
        public string? ProcessName { get; set; }
        public int ProcessId { get; set; }

        // Queue for commands waiting to be picked up by the implant
        public ConcurrentQueue<CommandTask> PendingCommands { get; } = new ConcurrentQueue<CommandTask>();

        // NEW: History of all commands issued to this implant
        // NOTE: List<T> is NOT thread-safe for concurrent writes. We MUST use 'lock' in the service.
        public List<CommandTask> CommandHistory { get; } = new List<CommandTask>();

        public int CurrentSleepTime { get; set; } = 30;
    }

    // CommandTask and CommandStatus remain the same
    public class CommandTask
    {
        public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
        public string CommandText { get; set; } = string.Empty;
        public DateTime IssuedAt { get; set; } = DateTime.UtcNow; // When the task was created/queued
        public DateTime? SentToImplantAt { get; set; } // When GetPendingCommand was called
        public CommandStatus Status { get; set; } = CommandStatus.Pending;
        public string? Result { get; set; }
        public DateTime? CompletedAt { get; set; } // When result was received
    }

    public enum CommandStatus
    {
        Pending,    // Created, not yet sent
        Issued,     // Sent to implant, awaiting result
        Processing, // (Optional future state)
        Completed,  // Result received successfully
        Error       // Result received indicates an error during execution
    }

    // CommandResult remains the same (used for implant reporting results)
    public class CommandResult
    {
        public string? CommandId { get; set; }
        public string? Output { get; set; }
        public bool HasError { get; set; }
    }
}