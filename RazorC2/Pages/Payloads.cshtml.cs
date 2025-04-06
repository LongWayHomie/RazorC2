using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;        

namespace RazorC2.Pages
{
    public class PayloadsModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<PayloadsModel> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IWebHostEnvironment _environment;

        public PayloadsModel(
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            ILogger<PayloadsModel> logger)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        // --- Properties for Form Binding ---
        [BindProperty]
        [Required(ErrorMessage = "Listener IP address is required.")]
        [RegularExpression(@"^((25[0-5]|(2[0-4]|1\d|[1-9]|)\d)\.?\b){4}$", ErrorMessage = "Invalid IP Address format.")] // Basic IP regex
        public string ListenerIp { get; set; } = "";

        [BindProperty]
        [Required(ErrorMessage = "Listener port is required.")]
        [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
        public int ListenerPort { get; set; }

        [BindProperty]
        [Required(ErrorMessage = "Default Sleep time is required.")]
        [Range(1, 86400, ErrorMessage = "Sleep must be between 1 and 86400 seconds.")]
        public int DefaultSleepSeconds { get; set; } = 10; // Set default value

        [BindProperty]
        [Required]
        public string OutputType { get; set; } = "exe"; // Default to exe


        // --- Status Message Properties ---
        [TempData]
        public string? StatusMessage { get; set; }
        [TempData]
        public bool? StatusIsError { get; set; }


        // --- OnGet: Pre-populate with current listener config ---
        public void OnGet()
        {
            ViewData["Title"] = "Implant Generation";
        }

        // --- OnPost: Handle Generation Request ---
        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            string payloadServiceUrl = _configuration.GetValue<string>("PayloadServiceUrl") ?? "http://localhost:5001";
            _logger.LogInformation("Requesting payload generation from service: Target={ListenerIp}:{ListenerPort}, Sleep={Sleep}", ListenerIp, ListenerPort, DefaultSleepSeconds);

            // Ensure OutputType is handled (only exe for now)
            if (OutputType != "exe")
            {
                ModelState.AddModelError(nameof(OutputType), "Selected output type is not supported yet.");
            }

            try
            {
                var client = _httpClientFactory.CreateClient();
                string requestUrl = $"{payloadServiceUrl}/generate?ip={Uri.EscapeDataString(ListenerIp)}&port={ListenerPort}&sleep={DefaultSleepSeconds}&format=exe"; // Add format if needed later

                HttpResponseMessage response = await client.PostAsync(requestUrl, null); // POST with empty body, info is in query

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Payload received successfully from service.");
                    var fileBytes = await response.Content.ReadAsByteArrayAsync();
                    var contentDisposition = response.Content.Headers.ContentDisposition;
                    string fileName = contentDisposition?.FileName ?? "implant.exe"; // Get filename from header or default

                    return File(fileBytes, response.Content.Headers.ContentType?.ToString() ?? "application/vnd.microsoft.portable-executable", fileName);
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Payload generation service returned error {StatusCode}: {Error}", response.StatusCode, errorContent);
                    ModelState.AddModelError(string.Empty, $"Generation service failed ({response.StatusCode}): {errorContent}");
                    return Page();
                }
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "HTTP error connecting to payload generation service at {Url}", payloadServiceUrl);
                ModelState.AddModelError(string.Empty, $"Error contacting generation service: {httpEx.Message}. Is it running?");
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during payload generation request.");
                ModelState.AddModelError(string.Empty, $"An unexpected error occurred: {ex.Message}");
                return Page();
            }
        }
    }
}