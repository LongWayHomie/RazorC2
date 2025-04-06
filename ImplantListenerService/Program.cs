using System.Net;
using System.Diagnostics;
using System.Text;
using System.Net.Http.Headers;
using Serilog;
using Serilog.Events;

// Define a namespace that is unique (different from the assembly name)
namespace RazorC2.ImplantListener
{
    // Main program class
    public class ListenerProgram
    {
        internal static CancellationTokenSource ParentProcessExitTokenSource = new CancellationTokenSource();

        // Define the async Main method
        public static async Task<int> Main(string[] args)
        {
            // --- Configure Serilog ---
            string logFilePath = Path.Combine(AppContext.BaseDirectory, "logs", "ImplantListenerService_log_.txt"); // Specific log file
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

            Log.Debug($"[ImplantListener Process DEBUG] Raw Args Received: {string.Join(" ", args)}");
            // --- Configuration Variables ---
            string listenIpStr = "127.0.0.1";
            int listenPort = 8080; // Default internal port, will be overridden by args
            string c2ApiBaseUrl = "http://localhost:5000"; // Default C2 URL, overridden by args
            int? parentProcessId = null; // Will hold the parent process ID if provided

            // --- Parse Command Line Arguments ---
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--listen-ip", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { listenIpStr = args[i + 1]; }
                else if (args[i].Equals("--listen-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { if (int.TryParse(args[i + 1], out int port)) { listenPort = port; } }
                else if (args[i].Equals("--c2-api-base", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { c2ApiBaseUrl = args[i + 1]; }
                else if (args[i].Equals("--parent-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { if (int.TryParse(args[i + 1], out int pid)) { parentProcessId = pid; } }
            }

            // --- Validation ---
            if (!IPAddress.TryParse(listenIpStr, out IPAddress? listenIpAddress)) { Log.Error($"[Listener Process ERROR] Invalid Listen IP: {listenIpStr}. Exiting."); return 1; } // Return error code
            if (listenPort <= 0 || listenPort > 65535) { Log.Error($"[Listener Process ERROR] Invalid Listen Port: {listenPort}. Exiting."); return 1; }
            if (!Uri.TryCreate(c2ApiBaseUrl, UriKind.Absolute, out _)) { Log.Error($"[Listener Process ERROR] Invalid C2 API Base URL: {c2ApiBaseUrl}. Exiting."); return 1; }

            Log.Information($"[Listener Process] Starting...");
            Log.Information($"[Listener Process] ==> Listening for implants on: {listenIpAddress}:{listenPort}");
            Log.Information($"[Listener Process] ==> Forwarding implant requests to: {c2ApiBaseUrl}");

            // Start parent process monitoring if we have a parent PID
            if (parentProcessId.HasValue)
            {
                Log.Information($"[Listener Process] ==> Monitoring parent process PID: {parentProcessId}");
                MonitorParentProcess(parentProcessId.Value);
            }

            try
            {
                // === Create a completely minimal WebApplication ===
                // This approach avoids most of the ASP.NET Core initialization that might trigger assembly loading
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    // Explicitly DO NOT pass the original 'args' here if they contain --urls or Kestrel settings
                    // We are handling binding manually.
                    Args = null, // Pass null to prevent builder from auto-parsing args for URLs etc.
                    EnvironmentName = "Production" // Force production mode to avoid dev settings
                });


                // Explicitly disable development-time features
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
                Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
                Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", "");
                Environment.SetEnvironmentVariable("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "");

                // *** CRITICAL: Clear default configuration providers ***
                // This prevents loading appsettings.json, environment variables (ASPNETCORE_URLS), etc.
                builder.Configuration.Sources.Clear();
                Log.Debug($"[Implant Listener Process DEBUG] Configuration sources CLEARED.");

                // Configure Kestrel directly
                builder.WebHost.ConfigureKestrel(options =>
                {
                    try
                    {
                        // Use the IPAddress object and port parsed earlier
                        options.Listen(listenIpAddress, listenPort);
                        Log.Debug($"[ImplantListener Process] Kestrel explicit Listen() configured for: {listenIpAddress}:{listenPort}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[ImplantListener Process ERROR] Kestrel Listen() failed for {listenIpAddress}:{listenPort} : {ex.ToString()}");
                        throw;
                    }
                });

                // Add HttpClient for Listener (using manually parsed c2ApiBaseUrl)
                // Removed the conditional check that was causing errors
                builder.Services.AddHttpClient("C2Forwarder", client =>
                {
                    client.BaseAddress = new Uri(c2ApiBaseUrl); // Use the variable parsed from args
                    client.Timeout = TimeSpan.FromSeconds(120);
                });
                Log.Information($"[ImplantListener Process DEBUG] C2 API Base URL: {c2ApiBaseUrl}");

                // Configure minimal logging
                builder.Services.AddLogging(configure =>
                {
                    configure.ClearProviders();
                    configure.AddConsole();
                    configure.SetMinimumLevel(LogLevel.Information);
                    configure.AddFilter("Microsoft", LogLevel.Warning);
                    configure.AddFilter("System", LogLevel.Warning);
                });

                builder.Services.AddHostedService<ListenerLifetimeService>();

                // Build the app with minimal services
                var app = builder.Build();
               Log.Debug($"[Implant Listener Process DEBUG] Configuration sources CLEARED.");

                // Get logger with a simple string name to avoid type resolution issues
                var logger = app.Services.GetRequiredService<ILogger<RazorC2.ImplantListener.ListenerProgram>>();

                // === Define Request Forwarding Middleware ===

                app.Run(async context =>
                {
                    var httpClientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
                    var forwarderClient = httpClientFactory.CreateClient("C2Forwarder");
                    var originalRemoteIp = context.Connection.RemoteIpAddress;

                    var request = context.Request;
                    var response = context.Response;

                    var targetPath = $"/razor-int{request.Path}{request.QueryString}";
                    var targetUri = new Uri(forwarderClient.BaseAddress!, targetPath);

                    try
                    {
                        // Create the forwarding request
                        var forwardRequest = new HttpRequestMessage(new HttpMethod(request.Method), targetUri);

                        // Copy body with proper buffering
                        if (request.ContentLength > 0 || request.Headers.ContainsKey("Content-Length"))
                        {
                            byte[] bodyBytes;
                            using (var memoryStream = new MemoryStream())
                            {
                                await request.Body.CopyToAsync(memoryStream);
                                bodyBytes = memoryStream.ToArray();

                                // Log the body content for debug (ONLY for /api/implant/hello)
                                if (request.Path.Value?.Contains("/api/implant/hello") == true)
                                {
                                    logger.LogDebug("[Debug] Request body: {Body}",
                                        Encoding.UTF8.GetString(bodyBytes));
                                }

                                // Reset the body stream position
                                var newBodyStream = new MemoryStream(bodyBytes);
                                newBodyStream.Position = 0;
                                request.Body = newBodyStream;
                            }

                            // Add the body content to the forwarding request
                            forwardRequest.Content = new ByteArrayContent(bodyBytes);

                            // Make sure Content-Type is preserved
                            if (request.ContentType != null)
                            {
                                forwardRequest.Content.Headers.ContentType =
                                    MediaTypeHeaderValue.Parse(request.ContentType);
                            }
                            else
                            {
                                // Default to application/json for API calls
                                forwardRequest.Content.Headers.ContentType =
                                    MediaTypeHeaderValue.Parse("application/json; charset=utf-8");
                            }
                        }

                        bool xForwardedForExists = false;
                        // Copy important headers
                        foreach (var header in request.Headers)
                        {
                            if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
                                !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                                !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                            {
                                forwardRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                                if (header.Key.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase))
                                {
                                    xForwardedForExists = true;
                                }
                            }
                        }

                        // *** Add/Append X-Forwarded-For Header ***
                        if (originalRemoteIp != null)
                        {
                            string forwardedForValue = originalRemoteIp.ToString();
                            if (xForwardedForExists)
                            {
                                // Append if header already exists (standard practice for multiple proxies)
                                forwardRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", $"{request.Headers["X-Forwarded-For"]}, {forwardedForValue}");
                            }
                            else
                            {
                                forwardRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedForValue);
                            }
                            //Console.WriteLine($"[Listener Forwarding] Added X-Forwarded-For: {forwardedForValue}"); // Log for debugging //noisy
                        }

                        // *** Add X-Forwarded-Proto Header ***
                        // We know the implant connects via HTTP to the listener
                        forwardRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
                        // *** Add X-Forwarded-Host Header (Use original Host if available) ***
                        if (request.Headers.TryGetValue("Host", out var originalHost))
                        {
                            forwardRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", originalHost.ToString());
                        }
                        else // Fallback to listener's own address
                        {
                            // You might need to construct this based on listenerIpAddress/listenPort if Host isn't present
                            // this is just a method to keep it sane
                            forwardRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", $"{listenIpAddress}:{listenPort}");
                        }

                        // Send the request to the C2 server
                        using var c2Response = await forwarderClient.SendAsync(forwardRequest);

                        // Copy status code
                        response.StatusCode = (int)c2Response.StatusCode;

                        // Copy response headers, but handle Content-Length specially
                        foreach (var header in c2Response.Headers)
                        {
                            if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                            {
                                response.Headers[header.Key] = header.Value.ToArray();
                            }
                        }

                        // Copy content headers
                        foreach (var header in c2Response.Content.Headers)
                        {
                            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                            {
                                response.Headers[header.Key] = header.Value.ToArray();
                            }
                        }

                        // Check if we can write a response body
                        if (response.StatusCode != 204) // Don't try to write a body for 204 responses
                        {
                            // Read the entire response into memory first
                            var responseBody = await c2Response.Content.ReadAsByteArrayAsync();

                            // Log response for debug (ONLY for /api/implant/hello)
                            /* if (request.Path.Value?.Contains("/api/implant/hello") == true)
                            {
                                logger.LogDebug("[Debug] Response body: {Body}",
                                    Encoding.UTF8.GetString(responseBody));
                            }*/

                            // Only set Content-Length and write body if we have content and status code allows it
                            if (responseBody.Length > 0)
                            {
                                // Set the Content-Length header explicitly
                                response.ContentLength = responseBody.Length;

                                // Write response body to the output stream
                                await response.Body.WriteAsync(responseBody, 0, responseBody.Length);
                            }
                        }

                        // Produces a lot of output because of check-ins, we don't need it for now
                        //logger.LogInformation("[Listener Process] Forwarded {Method} {Path} -> {TargetUri} completed with Status {StatusCode}", request.Method, request.Path.Value, targetUri, response.StatusCode);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[Listener Process] Error forwarding {Method} {Path}: {ErrorMessage}",
                            request.Method, request.Path.Value, ex.Message);

                        if (!response.HasStarted)
                        {
                            response.StatusCode = 500;
                            await response.WriteAsync($"Error forwarding request: {ex.Message}");
                        }
                    }
                });

                // Run the listener application
                await app.RunAsync(ParentProcessExitTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Error("[Listener Process] Shutting down due to parent process exit");
            }
            catch (Exception ex)
            {
                Log.Error($"[Listener Process] Error starting listener: {ex.Message}");
                return 1;
            }

            Log.Information("[Listener Process] Exited.");
            return 0; // Return success code
        }

        private static void MonitorParentProcess(int parentPid)
        {
            // Start a background task to monitor the parent process
            Task.Run(() =>
            {
                try
                {
                    // Try to get the parent process - if it fails, it might already be gone
                    Process? parentProcess = null;

                    try
                    {
                        parentProcess = Process.GetProcessById(parentPid);
                    }
                    catch (ArgumentException)
                    {
                        Log.Error($"[Listener Process] Parent process (PID: {parentPid}) not found, exiting.");
                        ParentProcessExitTokenSource.Cancel();
                        return;
                    }

                    Log.Information($"[Listener Process] Successfully attached to parent process (PID: {parentPid})");

                    // Wait for the parent to exit
                    parentProcess.WaitForExit();
                    Log.Information($"[Listener Process] Parent process (PID: {parentPid}) has exited. Initiating shutdown.");

                    // Cancel the token to trigger application shutdown
                    ParentProcessExitTokenSource.Cancel();
                }
                catch (Exception ex)
                {
                    Log.Error($"[Listener Process] Error monitoring parent process: {ex.Message}");
                    // If we can't monitor the parent, better to exit
                    ParentProcessExitTokenSource.Cancel();
                }
            });
        }
    }

    // Background service to handle the application lifetime
    public class ListenerLifetimeService : IHostedService
    {
        private readonly IHostApplicationLifetime _appLifetime;

        public ListenerLifetimeService(IHostApplicationLifetime appLifetime)
        {
            _appLifetime = appLifetime;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Register a callback that will be triggered when the application is stopping
            // Using fully qualified name to avoid conflicts
            RazorC2.ImplantListener.ListenerProgram.ParentProcessExitTokenSource.Token.Register(() =>
            {
                Log.Information("[Listener Process] Parent process exit detected, stopping application");
                _appLifetime.StopApplication();
            });

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}