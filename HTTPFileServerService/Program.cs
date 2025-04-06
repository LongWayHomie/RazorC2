// HttpFileServerService/Program.cs
using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.FileProviders; // Required for UseStaticFiles
using Serilog;
using Serilog.Events;

namespace RazorC2.HttpFileServer
{
    public class FileServerProgram
    {
        internal static CancellationTokenSource ParentProcessExitTokenSource = new CancellationTokenSource();
        public class HttpHeadersConfig
        {
            public Dictionary<string, string> ResponseHeaders { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        public static async Task<int> Main(string[] args)
        {
            // --- Configure Serilog ---
            string logFilePath = Path.Combine(AppContext.BaseDirectory, "logs", "HTTPFileServerSerivce_log_.txt"); // Specific log file
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

            Log.Information("------------------------------------"); // Separator for new run
            Log.Information("ImplantListenerService Starting...");
            Log.Debug("Raw Args Received: {Args}", string.Join(" ", args));

            Log.Debug($"[HTTPServer Process DEBUG] Raw Args Received: {string.Join(" ", args)}");
            // --- Configuration Variables ---
            string listenIpStr = "127.0.0.1"; // Default IP
            int listenPort = 8081;          // Default Port (different from listener)
            string serveDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), "file_server_root"); // Default directory
            int? parentProcessId = null;     // Parent PID

            // --- Parse Command Line Arguments ---
            // Use distinct prefixes for clarity (fs = file server)
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--fs-listen-ip", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { listenIpStr = args[i + 1]; }
                else if (args[i].Equals("--fs-listen-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { if (int.TryParse(args[i + 1], out int port)) { listenPort = port; } }
                else if (args[i].Equals("--fs-serve-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { serveDirectoryPath = args[i + 1]; } // Get directory from args
                else if (args[i].Equals("--parent-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { if (int.TryParse(args[i + 1], out int pid)) { parentProcessId = pid; } }
            }

            // --- Validation ---
            if (!IPAddress.TryParse(listenIpStr, out IPAddress? listenIpAddress)) { Log.Error($"[FileServer ERROR] Invalid Listen IP: {listenIpStr}. Exiting."); return 1; }
            if (listenPort <= 0 || listenPort > 65535) { Log.Error($"[FileServer ERROR] Invalid Listen Port: {listenPort}. Exiting."); return 1; }
            if (string.IsNullOrWhiteSpace(serveDirectoryPath)) { Log.Error($"[FileServer ERROR] Serve directory path is required. Exiting."); return 1; }

            // Ensure the serve directory exists
            try
            {
                if (!Directory.Exists(serveDirectoryPath))
                {
                    Log.Warning($"[FileServer INFO] Serve directory not found, creating: {serveDirectoryPath}");
                    Directory.CreateDirectory(serveDirectoryPath);
                }
                // Get full path for clarity in logs
                serveDirectoryPath = Path.GetFullPath(serveDirectoryPath);
            }
            catch (Exception ex)
            {
                Log.Error($"[FileServer ERROR] Cannot create or access serve directory '{serveDirectoryPath}': {ex.Message}. Exiting.");
                return 1;
            }


            Log.Information($"[FileServer Process] Starting...");
            Log.Information($"[FileServer Process] ==> Listening on: {listenIpAddress}:{listenPort}");
            Log.Information($"[FileServer Process] ==> Serving files from: {serveDirectoryPath}");

            // Start parent process monitoring if we have a parent PID
            if (parentProcessId.HasValue)
            {
                Log.Information($"[FileServer Process] ==> Monitoring parent process PID: {parentProcessId}");
                MonitorParentProcess(parentProcessId.Value);
            }

            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    Args = null, // Pass null to prevent builder from auto-parsing args for URLs etc.
                    EnvironmentName = "Production" // Force production mode to avoid dev settings
                });

                // Configure Kestrel directly
                builder.WebHost.ConfigureKestrel(options =>
                {
                    try
                    {
                        // Use the IPAddress object and port parsed earlier
                        options.Listen(listenIpAddress, listenPort);
                        Log.Information($"[HTTPServer Process] Kestrel explicit Listen() configured for: {listenIpAddress}:{listenPort}");
                        options.AddServerHeader = false; // Hack to change the Kestrel header to custom one from config file, followed later
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[HTTPServer Process ERROR] Kestrel Listen() failed for {listenIpAddress}:{listenPort} : {ex.ToString()}");
                        throw;
                    }
                });

                // Add lifetime service
                builder.Services.AddHostedService<FileServerLifetimeService>();

                // Configure minimal logging
                builder.Services.AddLogging(configure =>
                {
                    configure.ClearProviders();
                    configure.AddConsole();
                    configure.SetMinimumLevel(LogLevel.Information);
                    configure.AddFilter("Microsoft", LogLevel.Warning);
                    configure.AddFilter("System", LogLevel.Warning);
                });

                // Bind HTTP headers configuration
                var headersConfig = new HttpHeadersConfig();
                builder.Configuration.GetSection("HttpFileServer:ResponseHeaders").Bind(headersConfig.ResponseHeaders);

                var app = builder.Build();
                Log.Information($"[HTTPServer Process DEBUG] WebApplication built.");

                // 1. Custom Header Middleware (Place early)
                app.Use(async (context, next) =>
                {
                    context.Response.OnStarting(() => { // Use OnStarting to modify headers just before they are sent
                        // Add custom headers read from config
                        foreach (var header in headersConfig.ResponseHeaders)
                        {
                            // Use TryAdd for headers like Server that might cause issues if added twice
                            // Or just remove existing first if needed
                            context.Response.Headers.Remove(header.Key); //safety measures
                            context.Response.Headers.Append(header.Key, header.Value);
                        }
                        return Task.CompletedTask;
                    });
                    await next(context);
                });

                // FileServer specific setup using parsed 'serveDirectoryPath'
                var staticFileOptions = new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(serveDirectoryPath),
                    RequestPath = "", // Serve directly from root (e.g., http://ip:port/file.txt)
                    ServeUnknownFileTypes = true, // Serve files even if MIME type isn't known
                    DefaultContentType = "application/octet-stream" // Default for unknown types
                };
                app.UseStaticFiles(staticFileOptions);

                // Serve the root path as HTML content with proper content type
                app.MapGet("/", () => Results.Content(
                    "<html><body><h1>It works!</h1></body></html>",
                    contentType: "text/html"
                ));

                Log.Information($"[FileServer Process] Web server configured. Ready to serve files from '{serveDirectoryPath}' at http://{listenIpAddress}:{listenPort}/");

                // Run the file server application
                await app.RunAsync(ParentProcessExitTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Error("[FileServer Process] Shutting down due to parent process exit or cancel signal.");
            }
            catch (Exception ex)
            {
                Log.Error($"[FileServer ERROR] Error during startup or execution: {ex.ToString()}"); // Log full exception
                return 1;
            }

            Log.Error("[FileServer Process] Exited.");
            return 0; // Return success code
        }

        private static void MonitorParentProcess(int parentPid)
        {
            // (This code is identical to the one in ImplantListenerService - no changes needed)
            Task.Run(() =>
            {
                try
                {
                    Process? parentProcess = null;
                    try { parentProcess = Process.GetProcessById(parentPid); }
                    catch (ArgumentException)
                    {
                        Log.Error($"[FileServer Process] Parent process (PID: {parentPid}) not found on startup, exiting monitor task.");
                        ParentProcessExitTokenSource.Cancel(); // Trigger shutdown if parent gone immediately
                        return;
                    }

                    Log.Information($"[FileServer Process] Successfully attached to parent process (PID: {parentPid})");
                    parentProcess.WaitForExit();
                    Log.Warning($"[FileServer Process] Parent process (PID: {parentPid}) has exited. Initiating shutdown.");
                    ParentProcessExitTokenSource.Cancel();
                }
                catch (Exception ex)
                {
                    Log.Error($"[FileServer Process] Error monitoring parent process: {ex.Message}. Initiating shutdown.");
                    ParentProcessExitTokenSource.Cancel(); // Shutdown on error
                }
            });
        }
    }

    // Background service to handle graceful shutdown coordination (Identical to Listener's)
    public class FileServerLifetimeService : IHostedService
    {
        private readonly IHostApplicationLifetime _appLifetime;
        public FileServerLifetimeService(IHostApplicationLifetime appLifetime) { _appLifetime = appLifetime; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            FileServerProgram.ParentProcessExitTokenSource.Token.Register(() =>
            {
                Log.Warning("[FileServer Process] Parent process exit detected by token, stopping application.");
                _appLifetime.StopApplication();
            });
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}