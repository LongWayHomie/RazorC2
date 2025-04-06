using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent; // Use ConcurrentDictionary


// Define types of processes we manage
public enum ManagedProcessType
{
    ImplantListener,
    HttpFileServer,
    PayloadGenerationService
}

public class ProcessManagerService : IDisposable
{
    private readonly ILogger<ProcessManagerService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly IWebHostEnvironment _environment; // Need this for paths

    // Store process info (Process object, executable path, etc.)
    private class ManagedProcessInfo
    {
        public Process? Process { get; set; }
        public string ExecutablePath { get; set; } = string.Empty;
        public StringBuilder StartupOutput { get; } = new StringBuilder();
        public DateTime StartTime { get; set; }
    }

    // Use a ConcurrentDictionary to store managed processes
    private readonly ConcurrentDictionary<ManagedProcessType, ManagedProcessInfo> _managedProcesses = new();

    private readonly object _lock = new object(); // General lock remains useful

    public ProcessManagerService(
        ILogger<ProcessManagerService> logger,
        IConfiguration configuration,
        IHostApplicationLifetime appLifetime,
        IWebHostEnvironment environment) // Inject Environment
    {
        _logger = logger;
        _configuration = configuration;
        _appLifetime = appLifetime;
        _environment = environment; // Store environment

        // Resolve executable paths at startup
        InitializeProcessInfo(ManagedProcessType.ImplantListener, "ImplantListenerService.exe");
        InitializeProcessInfo(ManagedProcessType.HttpFileServer, "HttpFileServerService.exe");
        InitializeProcessInfo(ManagedProcessType.PayloadGenerationService, "PayloadGenerationService.exe");

        _appLifetime.ApplicationStopping.Register(StopAllManagedProcesses);
    }

    private void InitializeProcessInfo(ManagedProcessType type, string exeName)
    {
        string mainAppDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? _environment.ContentRootPath;
        string exePath = Path.Combine(mainAppDirectory, exeName);

        if (!File.Exists(exePath))
        {
            _logger.LogCritical($"[{type}] Executable not found at expected path: {exePath}. This process type cannot be started.");
            // Store info anyway, but it won't be startable
            _managedProcesses[type] = new ManagedProcessInfo { ExecutablePath = string.Empty }; // Mark as unstartable
        }
        else
        {
            _logger.LogInformation($"[{type}] Executable path resolved to: {exePath}");
            _managedProcesses[type] = new ManagedProcessInfo { ExecutablePath = exePath };
        }
    }

    // Helper for detailed logging
    private void LogDetailed(ManagedProcessType? type, string message, [CallerMemberName] string memberName = "")
    {
        string prefix = type.HasValue ? $"[{type}]" : "[General]";
        _logger.LogInformation($"[ProcessManagerService::{memberName}] {prefix} {message}");
    }
    private void LogErrorDetailed(ManagedProcessType? type, Exception? ex, string message, [CallerMemberName] string memberName = "")
    {
        string prefix = type.HasValue ? $"[{type}]" : "[General]";
        if (ex != null)
            _logger.LogError(ex, $"[ProcessManagerService::{memberName}] {prefix} ERROR: {message}");
        else
            _logger.LogError($"[ProcessManagerService::{memberName}] {prefix} ERROR: {message}");
    }
    private void LogWarnDetailed(ManagedProcessType? type, string message, [CallerMemberName] string memberName = "")
    {
        string prefix = type.HasValue ? $"[{type}]" : "[General]";
        _logger.LogWarning($"[ProcessManagerService::{memberName}] {prefix} WARN: {message}");
    }


    public bool IsRunning(ManagedProcessType type)
    {
        string state = "Unknown";
        bool isRunning = false;
        LogDetailed(type, "Checking process status...");

        if (!_managedProcesses.TryGetValue(type, out var processInfo) || processInfo.Process == null)
        {
            state = "Process info not found or process object is null.";
            isRunning = false;
            LogDetailed(type, state);
            return isRunning;
        }

        // Use lock primarily to access/modify the shared Process object reference safely
        lock (_lock)
        {
            // Re-check inside lock
            if (!_managedProcesses.TryGetValue(type, out processInfo) || processInfo.Process == null)
            {
                LogDetailed(type, "Process object became null inside lock check.");
                return false;
            }

            var currentProcess = processInfo.Process; // Local var inside lock

            try
            {
                if (currentProcess.HasExited)
                {
                    state = $"Process (PID maybe {currentProcess.Id}) has exited.";
                    LogDetailed(type, $"{state} - Cleaning up process object.");
                    currentProcess.Dispose();
                    processInfo.Process = null; // Nullify the reference in the dictionary entry
                    isRunning = false;
                }
                else
                {
                    // Optional: Verify process exists (can sometimes throw if access denied)
                    // try { Process.GetProcessById(currentProcess.Id); } catch { }
                    state = $"Process (PID {currentProcess.Id}) is active and running.";
                    isRunning = true;
                }
            }
            catch (InvalidOperationException ioEx)
            {
                state = "InvalidOperationException checking status (process likely stopped concurrently). Cleaning up.";
                LogErrorDetailed(type, ioEx, state);
                try { currentProcess.Dispose(); } catch { /* Ignore dispose error */ } // Attempt dispose
                processInfo.Process = null;
                isRunning = false;
            }
            catch (Exception ex)
            {
                state = $"Unexpected error checking status: {ex.Message}";
                LogErrorDetailed(type, ex, state);
                try { currentProcess.Dispose(); } catch { /* Ignore dispose error */ } // Attempt dispose
                processInfo.Process = null;
                isRunning = false;
            }
        } // End lock

        LogDetailed(type, $"Final Status Check: {state} -> IsRunning={isRunning}");
        return isRunning;
    }

    public bool StartProcess(ManagedProcessType type)
    {
        LogDetailed(type, "Attempting to start process...");

        if (!_managedProcesses.TryGetValue(type, out var processInfo) || string.IsNullOrEmpty(processInfo.ExecutablePath))
        {
            LogErrorDetailed(type, null, $"Cannot start process. Executable path not found or invalid for type {type}.");
            return false;
        }

        lock (_lock) // Lock for checking/setting process object
        {
            LogDetailed(type, "Acquired lock.");

            // Use IsRunning to perform cleanup check if necessary
            if (processInfo.Process != null && !IsRunning(type))
            {
                LogWarnDetailed(type, "Found non-null Process object that IsRunning reported as stopped. Overwriting.");
                processInfo.Process = null; // It should have been nulled by IsRunning, but be sure
            }
            // Re-check IsRunning *inside* the lock
            else if (IsRunning(type))
            {
                LogWarnDetailed(type, "StartProcess called, but process appears to be running.");
                LogDetailed(type, "Releasing lock (already running).");
                return true;
            }

            processInfo.StartupOutput.Clear();
            Process? tempProcess = null; // Temp variable

            try
            {
                // --- Get Configuration & Arguments ---
                string arguments;
                LogDetailed(type, "Reading configuration and constructing arguments...");
                try
                {
                    arguments = GetProcessArguments(type, processInfo.ExecutablePath);
                    if (string.IsNullOrEmpty(arguments)) return false; // GetProcessArguments logs errors
                }
                catch (Exception configEx)
                {
                    LogErrorDetailed(type, configEx, "Failed to get configuration/arguments.");
                    LogDetailed(type, "Releasing lock (config failed).");
                    return false;
                }
                LogDetailed(type, $"Constructed arguments: {arguments}");


                // --- Pre-checks (Port Binding, etc.) ---
                LogDetailed(type, "Performing pre-start checks...");
                if (!PerformPreStartChecks(type))
                {
                    LogDetailed(type, "Pre-start checks failed. Releasing lock.");
                    return false;
                }
                LogDetailed(type, "Pre-start checks passed.");


                // --- Process Setup ---
                LogDetailed(type, $"Setting up ProcessStartInfo for: {processInfo.ExecutablePath}");
                string? workingDir = Path.GetDirectoryName(processInfo.ExecutablePath);
                if (string.IsNullOrEmpty(workingDir))
                {
                    LogErrorDetailed(type, null, $"Could not determine working directory from {processInfo.ExecutablePath}");
                    LogDetailed(type, "Releasing lock (working dir error).");
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = processInfo.ExecutablePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir // Use EXE's directory
                };

                const string hostingStartupAssembliesEnvVar = "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES";
                // Clear the variable from the collection for the child process
                if (startInfo.EnvironmentVariables.ContainsKey(hostingStartupAssembliesEnvVar))
                {
                    startInfo.EnvironmentVariables.Remove(hostingStartupAssembliesEnvVar);
                    _logger.LogDebug("[{Type}] Removed inherited environment variable: {EnvVarKey}", type, hostingStartupAssembliesEnvVar);
                }
                // Setting it to empty string is also belt-and-suspenders approach
                startInfo.EnvironmentVariables[hostingStartupAssembliesEnvVar] = "";
                _logger.LogDebug("[{Type}] Cleared environment variable for child process: {EnvVarKey}", type, hostingStartupAssembliesEnvVar);

                LogDetailed(type, "Creating new Process object...");
                tempProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

                // --- Event Handlers ---
                LogDetailed(type, "Setting up event handlers...");
                // Capture processInfo in lambda closures carefully
                var capturedProcessInfo = processInfo;
                var capturedType = type;

                tempProcess.OutputDataReceived += (sender, e) => { if (e.Data != null) { capturedProcessInfo.StartupOutput.AppendLine(e.Data); _logger.LogInformation($"[{capturedType} Output] {e.Data}"); } };
                tempProcess.ErrorDataReceived += (sender, e) => { if (e.Data != null) { capturedProcessInfo.StartupOutput.AppendLine($"ERROR: {e.Data}"); _logger.LogError($"[{capturedType} Error] {e.Data}"); } };
                tempProcess.Exited += (sender, e) => {
                    int exitCode = -999;
                    Process? exitedProcess = sender as Process;
                    try { exitCode = exitedProcess?.ExitCode ?? -998; } catch { }

                    LogWarnDetailed(capturedType, $"Process exited unexpectedly EVENT. PID: {exitedProcess?.Id ?? -1}, Code: {exitCode}");
                    LogWarnDetailed(capturedType, $"Captured output at exit:\n{capturedProcessInfo.StartupOutput}");
                    lock (_lock)
                    {
                        LogDetailed(capturedType, "Exited event handler: Acquired lock.");
                        // Check if the exited process is the *current* one for this type
                        if (_managedProcesses.TryGetValue(capturedType, out var currentInfo) && currentInfo.Process == exitedProcess)
                        {
                            LogWarnDetailed(capturedType, "Exited event handler: Nullifying Process object.");
                            currentInfo.Process?.Dispose(); // Dispose if not null
                            currentInfo.Process = null;
                        }
                        else
                        {
                            LogWarnDetailed(capturedType, "Exited event handler: Exited process was not the current one. Ignoring.");
                            try { exitedProcess?.Dispose(); } catch { } // Dispose the sender anyway if possible
                        }
                        LogDetailed(capturedType, "Exited event handler: Releasing lock.");
                    }
                };

                // --- Process Start ---
                LogDetailed(type, "Attempting Process.Start()...");
                bool started = tempProcess.Start();
                LogDetailed(type, $"Process.Start() returned: {started}");
                if (!started)
                {
                    LogErrorDetailed(type, null, "Process.Start() returned false.");
                    tempProcess.Dispose();
                    LogDetailed(type, "Releasing lock (start failed).");
                    return false;
                }

                LogDetailed(type, $"Process started. PID: {tempProcess.Id}. Beginning ReadLines.");
                tempProcess.BeginOutputReadLine();
                tempProcess.BeginErrorReadLine();

                // --- Initial Wait/Check ---
                LogDetailed(type, "Entering initial wait loop (5 x 500ms)...");
                // ... (wait loop identical to before, using tempProcess, log with type) ...
                bool exitedPrematurely = false;
                for (int i = 1; i <= 5; i++)
                {
                    LogDetailed(type, $"Wait loop iteration {i}, sleeping 500ms...");
                    Thread.Sleep(500);
                    try
                    {
                        if (tempProcess.HasExited)
                        {
                            int exitCode = -997; try { exitCode = tempProcess.ExitCode; } catch { }
                            LogErrorDetailed(type, null, $"Process exited prematurely after ~{i * 500}ms with code {exitCode}");
                            LogErrorDetailed(type, null, $"Startup Output at premature exit:\n{processInfo.StartupOutput}");
                            exitedPrematurely = true;
                            break;
                        }
                    }
                    catch (Exception exCheck)
                    {
                        LogErrorDetailed(type, exCheck, $"Error checking HasExited in wait loop {i}. Assuming exit.");
                        exitedPrematurely = true;
                        break;
                    }
                } // End wait loop

                if (exitedPrematurely)
                {
                    LogDetailed(type, "Process exited prematurely. Disposing object.");
                    tempProcess.Dispose();
                    LogDetailed(type, "Releasing lock (exited prematurely).");
                    return false;
                }

                // --- Success ---
                LogDetailed(type, $"Process (PID: {tempProcess.Id}) survived initial checks. Assigning to managed process info.");
                processInfo.Process = tempProcess; // Assign the successfully started process
                processInfo.StartTime = DateTime.UtcNow;
                LogDetailed(type, "Process started successfully. Releasing lock.");
                return true;
            }
            catch (Exception ex)
            {
                LogErrorDetailed(type, ex, "Exception during process startup sequence.");
                tempProcess?.Dispose(); // Ensure disposal on exception
                LogDetailed(type, "Releasing lock (exception).");
                return false;
            }
        } // End Lock
    }

    public void StopProcess(ManagedProcessType type)
    {
        LogDetailed(type, "Attempting to stop process...");
        Process? processToStop = null;
        int pid = -1;

        if (!_managedProcesses.TryGetValue(type, out var processInfo))
        {
            LogWarnDetailed(type, "StopProcess called, but no process info found for this type.");
            return;
        }

        lock (_lock) // Lock for reading/nullifying
        {
            LogDetailed(type, "Acquired lock.");
            if (processInfo.Process == null)
            {
                LogWarnDetailed(type, "StopProcess called, but Process object is already null.");
                LogDetailed(type, "Releasing lock.");
                return;
            }
            processToStop = processInfo.Process;
            pid = processToStop.Id; // Get PID before nullifying
            processInfo.Process = null; // Nullify immediately
            LogDetailed(type, $"Set Process object to null. Will attempt to stop PID {pid}.");
        } // Release lock before killing/waiting

        // --- Perform kill/wait outside the lock ---
        if (processToStop != null)
        {
            try
            {
                LogDetailed(type, $"Attempting to kill process PID: {pid}");
                processToStop.Kill(entireProcessTree: true);
                LogDetailed(type, $"Waiting up to 5000ms for process {pid} to exit...");
                if (processToStop.WaitForExit(5000)) { /* Log success */ LogDetailed(type, $"Process {pid} exited successfully."); }
                else { /* Log warning, double-check */ LogWarnDetailed(type, $"Process {pid} did not exit within timeout."); }
            }
            catch (Exception ex) { /* Log error */ LogErrorDetailed(type, ex, $"Error stopping process PID {pid}"); }
            finally { LogDetailed(type, $"Disposing process object for PID {pid}."); processToStop.Dispose(); }
        }
        else
        {
            LogDetailed(type, "processToStop was null after lock release (should not happen?).");
        }
    }


    public bool RestartProcess(ManagedProcessType type)
    {
        LogDetailed(type, "RestartProcess called.");

        LogDetailed(type, "Executing StopProcess()...");
        StopProcess(type); // Stop the specific process

        // Optional: Orphaned process cleanup (might need refinement to get correct process name)
        LogDetailed(type, "Cleaning up potential orphaned processes...");
        CleanupOrphanedProcesses(type);

        LogDetailed(type, "Waiting ~3-5 seconds for resources..."); // Slightly shorter wait might be ok
        Thread.Sleep(3000); // Reduced wait
        GC.Collect();
        GC.WaitForPendingFinalizers();
        LogDetailed(type, "Wait finished.");

        // --- RELOAD CONFIGURATION (important!) ---
        LogDetailed(type, "Attempting configuration reload...");
        ReloadConfiguration(); // Call helper
                               // --- END RELOAD ---

        LogDetailed(type, "Starting restart attempts...");
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            LogDetailed(type, $"Restart Attempt {attempt}/3...");
            if (attempt > 1) { Thread.Sleep(1000 * attempt); } // Progressive delay

            LogDetailed(type, $"Calling StartProcess({type}) for attempt {attempt}...");
            bool success = StartProcess(type); // Start the specific process
            LogDetailed(type, $"StartProcess() attempt {attempt} returned: {success}");

            if (success) { LogDetailed(type, "Process restarted successfully."); return true; }

            LogWarnDetailed(type, $"Attempt {attempt} failed.");
            // Optional: Check port status after failure
        }

        LogErrorDetailed(type, null, "Failed to restart process after 3 attempts.");
        return false;
    }

    // --- Helper Methods ---

    private void ReloadConfiguration()
    {
        LogDetailed(null, "[ReloadConfiguration] Attempting configuration reload..."); // Add marker

        // Determine the primary config file path (consistent with SaveSpecificSettings)
        string environmentName = _environment.EnvironmentName ?? "Production"; // Get current env
        string targetConfigFile = $"appsettings.{environmentName}.json";
        string targetConfigPath = Path.Combine(_environment.ContentRootPath, targetConfigFile);
        string baseConfigPath = Path.Combine(_environment.ContentRootPath, "appsettings.json");
        string configPathToRead = System.IO.File.Exists(targetConfigPath) ? targetConfigPath : baseConfigPath;
        LogDetailed(null, $"[ReloadConfiguration] Identified config file to check: {configPathToRead}");


        if (_configuration is IConfigurationRoot configRoot)
        {
            try
            {
                // --- Log file content BEFORE reload ---
                try
                {
                    if (File.Exists(configPathToRead))
                    {
                        string fileContentBefore = File.ReadAllText(configPathToRead);
                        LogDetailed(null, $"[ReloadConfiguration] File content BEFORE configRoot.Reload():\n{fileContentBefore}\n--------------------");
                    }
                    else
                    {
                        LogWarnDetailed(null, $"[ReloadConfiguration] Config file '{configPathToRead}' not found before reload.");
                    }
                }
                catch (Exception exRead) { LogErrorDetailed(null, exRead, "[ReloadConfiguration] Error reading file content BEFORE reload"); }
                // --- End log ---

                int lPortBefore = _configuration.GetValue<int?>("Listeners:Implant:Port") ?? -1;
                string lIpBefore = _configuration.GetValue<string>("Listeners:Implant:IpAddress") ?? "N/A";
                int fsPortBefore = _configuration.GetValue<int?>("HttpFileServer:Settings:Port") ?? -1;
                string fsIpBefore = _configuration.GetValue<string>("HttpFileServer:Settings:IpAddress") ?? "N/A";
                LogDetailed(null, $"[ReloadConfiguration] Values in IConfiguration BEFORE reload: L={lIpBefore}:{lPortBefore}, FS={fsIpBefore}:{fsPortBefore}");

                // Perform the reload
                configRoot.Reload();
                LogDetailed(null, "[ReloadConfiguration] configRoot.Reload() called.");


                // --- Log file content AFTER reload ---
                try
                {
                    if (File.Exists(configPathToRead))
                    {
                        // Short delay - might help if file system write wasn't fully flushed?
                        System.Threading.Thread.Sleep(100); // Small delay
                        string fileContentAfter = File.ReadAllText(configPathToRead);
                        LogDetailed(null, $"[ReloadConfiguration] File content AFTER configRoot.Reload():\n{fileContentAfter}\n--------------------");
                    }
                    else
                    {
                        LogWarnDetailed(null, $"[ReloadConfiguration] Config file '{configPathToRead}' not found after reload.");
                    }
                }
                catch (Exception exRead) { LogErrorDetailed(null, exRead, "[ReloadConfiguration] Error reading file content AFTER reload"); }
                // --- End log ---

                int lPortAfter = _configuration.GetValue<int?>("Listeners:Implant:Port") ?? -2;
                string lIpAfter = _configuration.GetValue<string>("Listeners:Implant:IpAddress") ?? "N/A";
                int fsPortAfter = _configuration.GetValue<int?>("HttpFileServer:Settings:Port") ?? -2;
                string fsIpAfter = _configuration.GetValue<string>("HttpFileServer:Settings:IpAddress") ?? "N/A";
                LogDetailed(null, $"[ReloadConfiguration] Values in IConfiguration AFTER reload: L={lIpAfter}:{lPortAfter}, FS={fsIpAfter}:{fsPortAfter}");
            }
            catch (Exception ex) { LogErrorDetailed(null, ex, "[ReloadConfiguration] Failed during configRoot.Reload() or subsequent read."); }
        }
        else { LogWarnDetailed(null, "[ReloadConfiguration] Cannot reload configuration: IConfiguration is not IConfigurationRoot."); }
    }

    private string GetProcessArguments(ManagedProcessType type, string executablePath)
    {
        string mainC2InternalUrl = "http://localhost:5000"; // Assuming constant
        int parentPid = Process.GetCurrentProcess().Id;
        string args = $"--parent-pid {parentPid}";

        try
        {
            switch (type)
            {
                case ManagedProcessType.ImplantListener:
                    var listenerConfig = _configuration.GetSection("Listeners:Implant");
                    string listenerIp = listenerConfig.GetValue<string>("IpAddress") ?? "127.0.0.1";
                    int listenerPort = listenerConfig.GetValue<int?>("Port") ?? 80;
                    args += $" --listen-ip \"{listenerIp}\" --listen-port {listenerPort} --c2-api-base \"{mainC2InternalUrl}\"";
                    break;

                case ManagedProcessType.HttpFileServer:
                    var fsConfig = _configuration.GetSection("HttpFileServer:Settings");
                    string fsIp = fsConfig.GetValue<string>("IpAddress") ?? "127.0.0.1";
                    int fsPort = fsConfig.GetValue<int?>("Port") ?? 8081;
                    // Get the *relative* path from config, make it absolute
                    string relativeServeDir = _configuration.GetValue<string>("HttpFileServer:ServeFromDirectory") ?? "file_server_root";
                    string absoluteServeDir = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, relativeServeDir)); // Use ContentRootPath
                    args += $" --fs-listen-ip \"{fsIp}\" --fs-listen-port {fsPort} --fs-serve-dir \"{absoluteServeDir}\"";
                    break;

                case ManagedProcessType.PayloadGenerationService:
                    //Get the URL from config
                    string payloadServiceUrl = _configuration.GetValue<string>("PayloadServiceUrl") ?? "http://localhost:5001";
                    args += $" --urls \"{payloadServiceUrl}\""; // Pass URL via standard --urls arg
                    break;

                default:
                    LogErrorDetailed(type, null, "Unknown process type in GetProcessArguments.");
                    return string.Empty;
            }
        }
        catch (Exception ex)
        {
            LogErrorDetailed(type, ex, "Error reading configuration for process arguments.");
            return string.Empty;
        }
        return args;
    }

    private bool PerformPreStartChecks(ManagedProcessType type)
    {
        // Currently only checks ports
        try
        {
            string ip = "127.0.0.1"; // Default/fallback
            int port = 0;
            Uri? serviceUri = null;

            switch (type)
            {
                case ManagedProcessType.ImplantListener:
                    ip = _configuration.GetValue<string>("Listeners:Implant:IpAddress") ?? ip;
                    port = _configuration.GetValue<int?>("Listeners:Implant:Port") ?? 80;
                    break;
                case ManagedProcessType.HttpFileServer:
                    ip = _configuration.GetValue<string>("HttpFileServer:Settings:IpAddress") ?? ip;
                    port = _configuration.GetValue<int?>("HttpFileServer:Settings:Port") ?? 8081;
                    break;
                case ManagedProcessType.PayloadGenerationService:
                    string payloadServiceUrl = _configuration.GetValue<string>("PayloadServiceUrl") ?? "http://localhost:5001";
                    if (Uri.TryCreate(payloadServiceUrl, UriKind.Absolute, out serviceUri))
                    {
                        // Extract host/IP and port for checking
                        // Need to handle potential '*' or '+' bindings if service uses those
                        ip = serviceUri.Host;
                        // Handle common Kestrel binding addresses
                        if (ip == "localhost") ip = "127.0.0.1";
                        if (ip == "*" || ip == "+")
                        {
                            LogWarnDetailed(type, "Service configured to bind to all interfaces ('*' or '+'). Cannot perform specific IP port check. Skipping port check.");
                            // Optionally perform a check on 127.0.0.1 for that port?
                            // return TestPortBinding("127.0.0.1", serviceUri.Port);
                            return true; // Skip check if binding to all
                        }
                        port = serviceUri.Port;
                    }
                    else
                    {
                        LogErrorDetailed(type, null, $"Invalid PayloadServiceUrl configured: {payloadServiceUrl}");
                        return false;
                    }
                    break;
                default:
                    LogWarnDetailed(type, "No pre-start checks defined for this process type.");
                    return true; // No checks to fail
            }

            if (port == 0)
            {
                LogErrorDetailed(type, null, "Port resolved to 0 during pre-start check.");
                return false;
            }

            LogDetailed(type, $"Checking port {ip}:{port}...");
            if (!TestPortBinding(ip, port)) return false;
            if (IsPortInUse(ip, port)) return false; // IsPortInUse logs warnings
            LogDetailed(type, $"Port {port} is available.");

            // Add other checks here if needed (e.g., directory existence/permissions for FileServer)
            if (type == ManagedProcessType.HttpFileServer)
            {
                string relativeServeDir = _configuration.GetValue<string>("HttpFileServer:ServeFromDirectory") ?? "file_server_root";
                string absoluteServeDir = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, relativeServeDir));
                if (!Directory.Exists(absoluteServeDir))
                {
                    LogWarnDetailed(type, $"Serve directory '{absoluteServeDir}' does not exist. StartProcess *might* create it, but check permissions if it fails.");
                    // Optionally create it here, but the FileServer process also tries
                    // Directory.CreateDirectory(absoluteServeDir);
                }
                else
                {
                    LogDetailed(type, $"Serve directory '{absoluteServeDir}' exists.");
                }
            }


        }
        catch (Exception ex)
        {
            LogErrorDetailed(type, ex, "Exception during pre-start checks.");
            return false;
        }
        return true;
    }

    private void CleanupOrphanedProcesses(ManagedProcessType? specificType = null)
    {
        LogDetailed(specificType, "Running cleanup for orphaned processes...");
        IEnumerable<ManagedProcessType> typesToClean = specificType.HasValue
              ? new[] { specificType.Value }
              : _managedProcesses.Keys; // Clean all known types if specificType is null

        foreach (var type in typesToClean)
        {
            if (!_managedProcesses.TryGetValue(type, out var info) || string.IsNullOrEmpty(info.ExecutablePath)) continue;

            string processName = Path.GetFileNameWithoutExtension(info.ExecutablePath);
            if (string.IsNullOrEmpty(processName))
            {
                LogWarnDetailed(type, $"Could not determine process name from path: {info.ExecutablePath}. Skipping orphan check for this type.");
                continue;
            }

            LogDetailed(type, $"Checking for orphans named '{processName}'...");
            try
            {
                var processes = Process.GetProcessesByName(processName);
                int currentPid = Process.GetCurrentProcess().Id;
                bool foundOrphans = false;
                foreach (var process in processes)
                {
                    bool isManaged = false;
                    lock (_lock)
                    { // Check against current managed PIDs under lock
                        isManaged = _managedProcesses.Values.Any(pinfo => pinfo.Process?.Id == process.Id);
                    }

                    // Kill if it's not the C2 itself and not currently tracked as running by us
                    if (process.Id != currentPid && !isManaged)
                    {
                        foundOrphans = true;
                        try
                        {
                            LogWarnDetailed(type, $"Attempting to kill potential orphan PID={process.Id}");
                            process.Kill(true);
                            LogDetailed(type, $"Killed orphan process {process.Id}.");
                        }
                        catch (Exception exKill) { LogErrorDetailed(type, exKill, $"Error killing process {process.Id}"); }
                    }
                    process.Dispose(); // Dispose the handle
                }
                if (!foundOrphans) LogDetailed(type, "No orphaned processes found.");

            }
            catch (Exception ex) { LogErrorDetailed(specificType ?? type, ex, $"Error querying/cleaning processes for '{processName}'."); }
        }
    }

    private void StopAllManagedProcesses()
    {
        LogDetailed(null, "StopAllManagedProcesses called (Application Stopping).");
        // Stop in a specific order if needed, otherwise just iterate
        foreach (var type in _managedProcesses.Keys.ToList()) // ToList to avoid modification issues
        {
            StopProcess(type);
        }
        // Optionally run orphan check one last time
        // CleanupOrphanedProcesses();
        LogDetailed(null, "Finished stopping managed processes.");
    }

    private bool IsPortInUse(string ipAddress, int port)
    {
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(ipAddress, out var parsedIp)
                ? new[] { parsedIp }
                : Dns.GetHostAddresses(ipAddress);

            foreach (IPAddress address in addresses)
            {
                try
                {
                    using var listener = new System.Net.Sockets.TcpListener(address, port);
                    listener.Start();
                    listener.Stop();
                }
                catch
                {
                    _logger.LogWarning($"Port {port} is already in use on {address}");
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error checking if port {port} is in use");
            return false;
        }
    }

    // New diagnostic method to run directly in the C2 process to detect any port binding issues
    public bool TestPortBinding(string ip, int port)
    {
        try
        {
            _logger.LogInformation($"Testing direct port binding on {ip}:{port}...");

            IPAddress address = IPAddress.Parse(ip);
            var listener = new System.Net.Sockets.TcpListener(address, port);

            try
            {
                listener.Start();
                _logger.LogInformation($"Successfully bound to {ip}:{port}");
                listener.Stop();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error binding to port {port}: {ex.Message}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Port binding test failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        LogDetailed(null, "Dispose called.");
        StopAllManagedProcesses();
        GC.SuppressFinalize(this);
        LogDetailed(null, "Dispose finished.");
    }
}