// Pages/Index.cshtml.cs
using RazorC2.Models;
using RazorC2.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RazorC2.Pages
{
    // Add this to handle potential anti-forgery token issues with AJAX POSTs
    [IgnoreAntiforgeryToken]
    public class IndexModel : PageModel
    {
        private readonly ImplantManagerService _implantManager;
        private readonly ILogger<IndexModel> _logger;

        // Keep these BindProperty fields for the form model
        [BindProperty]
        public string? SelectedImplantId { get; set; }

        [BindProperty]
        public string CommandText { get; set; } = string.Empty;

        public IndexModel(ImplantManagerService implantManager, ILogger<IndexModel> logger)
        {
            _implantManager = implantManager;
            _logger = logger;
        }

        public class CommandInputModel
        {
            public string? SelectedImplantId { get; set; }
            public string? CommandText { get; set; }
        }

        // OnGet doesn't need to load data anymore, JS will do it.
        public void OnGet()
        {
            // Page initially loads empty, JS populates content.
        }

        // Modified OnPost to return JSON status for AJAX call
        public IActionResult OnPost([FromBody] CommandInputModel commandInput)
        {
            _logger.LogInformation("[OnPost] Handler executing. Received Input Model: ImplantId='{ImplantId}', CommandText='{Command}'",
                commandInput?.SelectedImplantId, commandInput?.CommandText); // Log received data

            // Validate using the input parameter properties
            if (commandInput == null || string.IsNullOrEmpty(commandInput.SelectedImplantId) || string.IsNullOrEmpty(commandInput.CommandText))
            {
                _logger.LogWarning("[OnPost] Validation failed: Input model or its properties are null/empty.");
                //_implantManager.Log("Command submission failed (server validation): Missing input data.");
                return BadRequest(new { message = "Implant ID and Command Text are required in the request body." });
            }

            _logger.LogInformation("[OnPost] Validation passed. Queuing command for {ImplantId}.", commandInput.SelectedImplantId);
            // Use the values from the parameter
            bool queued = _implantManager.QueueCommand(commandInput.SelectedImplantId, commandInput.CommandText);

            if (!queued)
            {
                _logger.LogWarning("[OnPost] Failed to queue command for implant {ImplantId}.", commandInput.SelectedImplantId);
                //_implantManager.Log($"Command submission failed for {commandInput.SelectedImplantId}. Implant not found?");
                return NotFound(new { message = $"Failed to queue command. Implant '{commandInput.SelectedImplantId}' not found or invalid." });
            }

            _logger.LogInformation("[OnPost] Command queued successfully for {ImplantId}.", commandInput.SelectedImplantId);
            return new OkObjectResult(new { message = "Command queued successfully." });
        }
    }
}