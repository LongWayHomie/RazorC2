using System;

namespace RazorC2.ImplantListener.Models
{
    // These models should match the corresponding models in the implant
    public class ImplantRegistrationInfo
    {
        public string? Hostname { get; set; }
        public string? Username { get; set; }
        public string? ProcessName { get; set; }
        public int? ProcessId { get; set; }
    }

    public class CommandTask
    {
        public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
        public string CommandText { get; set; } = string.Empty;
    }

    public class CommandResult
    {
        public string? CommandId { get; set; }
        public string? Output { get; set; }
        public bool HasError { get; set; }
    }
}