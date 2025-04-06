using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations; 
using System.Text.Json;   
using System.Text.Json.Nodes; 

namespace RazorC2.Pages
{
    public class ConfigurationModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment; // To find appsettings.json
        private readonly ProcessManagerService _processManager; 
        private readonly string _fileServerRootAbsolutePath;
        private readonly ILogger<ConfigurationModel> _logger; 
        public string FileServerServeDirectory { get; private set; } = string.Empty;
        public string FileServerUrl { get; private set; } = string.Empty;

        public ConfigurationModel(
            IConfiguration configuration,
            IWebHostEnvironment environment,
            ILogger<ConfigurationModel> logger,
            ProcessManagerService processManager)
        {
            _configuration = configuration;
            _environment = environment;
            _processManager = processManager;
            _logger = logger;

            // Calculate and store the absolute path to the file server root ONCE
            string relativeServeDir = _configuration.GetValue<string>("HttpFileServer:ServeFromDirectory") ?? "file_server_root";
            _fileServerRootAbsolutePath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, relativeServeDir));
            // Ensure root directory exists on startup (optional, File Server process also tries)
            try { Directory.CreateDirectory(_fileServerRootAbsolutePath); } catch { }
        }

        // --- NEW: Model for File Manager Items ---
        public class FileManagerItem
        {
            public string Name { get; set; } = string.Empty;
            public bool IsDirectory { get; set; }
            public string RelativePath { get; set; } = string.Empty; // Path relative to file server root
            public long SizeBytes { get; set; } // For files
            public DateTime LastModified { get; set; }
            public string SizeDisplay => IsDirectory ? "--" : FormatBytes(SizeBytes); 

            private static string FormatBytes(long bytes)
            {
                string[] suffix = { " B", " KB", " MB", " GB", " TB" };
                int i = 0;
                double dblSByte = bytes;
                if (bytes > 1024)
                {
                    for (i = 0; (bytes / 1024) > 0; i++, bytes /= 1024)
                    {
                        dblSByte = bytes / 1024.0;
                    }
                }
                return $"{dblSByte:0.##}{suffix[i]}";
            }
        }
        // --- END NEW MODEL ---

        // --- Listener Properties ---
        [BindProperty(SupportsGet = true)]
        [Required(ErrorMessage = "Listener IP Address is required.")]
        public string ListenerIpAddress { get; set; } = "127.0.0.1";

        [BindProperty(SupportsGet = true)]
        [Required(ErrorMessage = "Listener Port is required.")]
        [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
        public int ListenerPort { get; set; } = 80;
        public bool IsListenerRunning { get; private set; }

        // --- File Server Properties ---
        [BindProperty(SupportsGet = true)]
        [Required(ErrorMessage = "File Server IP Address is required.")]
        public string FileServerIpAddress { get; set; } = "127.0.0.1";

        [BindProperty(SupportsGet = true)]
        [Required(ErrorMessage = "File Server Port is required.")]
        [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
        public int FileServerPort { get; set; } = 8081;
        public bool IsFileServerRunning { get; private set; }

        // --- File Manager Properties ---
        [BindProperty(SupportsGet = true)] // Allows path in query string: ?CurrentRelativePath=subdir1
        public string? CurrentRelativePath { get; set; } // Path RELATIVE to file server root
        public List<FileManagerItem> FileManagerItems { get; private set; } = new List<FileManagerItem>();
        public string? ParentRelativePath { get; private set; } // Path to parent, null if in root
        public string CurrentPathDisplay => string.IsNullOrEmpty(CurrentRelativePath) ? "Root" : CurrentRelativePath.Replace('\\', '/'); // User-friendly path display

        // --- Upload Property ---
        [BindProperty]
        public IFormFile? UploadedFile { get; set; }

        // --- Status Message ---
        [TempData]
        public string? StatusMessage { get; set; }
        [TempData]
        public bool? StatusIsError { get; set; }

        // --- Security Helper: Validates and returns ABSOLUTE path ---
        private string? GetSafeCurrentAbsolutePath(string? relativePath, out bool pathIsSafe)
        {
            pathIsSafe = false;
            try
            {
                // Normalize separators and decode URL potentially
                string normalizedRelative = (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

                // Combine with root
                string absolutePath = Path.GetFullPath(Path.Combine(_fileServerRootAbsolutePath, normalizedRelative));

                // *** CRUCIAL: Check if the resulting path is ACTUALLY inside the root ***
                // Ensure it starts with the root path AND accounts for trailing separator differences
                if (absolutePath.StartsWith(_fileServerRootAbsolutePath, StringComparison.OrdinalIgnoreCase) ||
                   absolutePath.Equals(_fileServerRootAbsolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    // Ensure the resolved path actually exists (as a directory)
                    if (Directory.Exists(absolutePath))
                    {
                        pathIsSafe = true;
                        return absolutePath;
                    }
                    else
                    {
                        // Path is calculated within root but doesn't exist, treat as unsafe for listing
                        Console.WriteLine($"[File Manager Safety] Calculated path '{absolutePath}' does not exist as directory.");
                        return null;
                    }
                }
                else
                {
                    // Path traversal attempt or invalid path!
                    Console.WriteLine($"[File Manager Safety] Path traversal attempt or invalid path. Root: '{_fileServerRootAbsolutePath}', Relative: '{relativePath}', Calculated Absolute: '{absolutePath}'");
                    return null;
                }
            }
            catch (Exception ex)
            {
                // Path errors (invalid chars etc.)
                Console.WriteLine($"[File Manager Safety] Error getting safe absolute path for '{relativePath}': {ex.Message}");
                return null;
            }
        }

        // --- Security Helper: Check download/delete path ---
        private bool IsFilePathSafe(string relativeFilePath, out string absoluteFilePath)
        {
            absoluteFilePath = string.Empty;
            try
            {
                string normalizedRelative = (relativeFilePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                absoluteFilePath = Path.GetFullPath(Path.Combine(_fileServerRootAbsolutePath, normalizedRelative));

                // Check it's within the root directory
                if (!absoluteFilePath.StartsWith(_fileServerRootAbsolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[File Manager Safety] File path '{absoluteFilePath}' is outside root '{_fileServerRootAbsolutePath}'.");
                    return false;
                }
                // Optional: Check for tricky filenames, although Path.Combine should handle most '..'
                if (Path.GetFileName(absoluteFilePath).StartsWith("."))
                {
                    Console.WriteLine($"[File Manager Safety] File path uses potentially unsafe filename: '{Path.GetFileName(absoluteFilePath)}'.");
                    return false; // Disallow hidden files for safety?
                }

                return true; // It's within the root boundary
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[File Manager Safety] Error checking file path safety for '{relativeFilePath}': {ex.Message}");
                return false;
            }
        }

        public void OnGet()
        {
            ViewData["Title"] = "Configuration";
            IsListenerRunning = _processManager.IsRunning(ManagedProcessType.ImplantListener);
            IsFileServerRunning = _processManager.IsRunning(ManagedProcessType.HttpFileServer);
            LoadCurrentSettings(); // Load IP/Port settings

            // --- File Manager Logic ---
            FileManagerItems = new List<FileManagerItem>(); // Reset list

            string? currentAbsolutePath = GetSafeCurrentAbsolutePath(CurrentRelativePath, out bool pathIsSafe);

            if (!pathIsSafe || currentAbsolutePath == null)
            {
                // If path is unsafe or doesn't exist, default to root
                CurrentRelativePath = null; // Reset relative path
                currentAbsolutePath = _fileServerRootAbsolutePath; // Use root absolute path
                if (!Directory.Exists(currentAbsolutePath))
                {
                    // Handle case where even root doesn't exist (should be rare after constructor check)
                    StatusMessage = "Error: File server root directory not found or inaccessible.";
                    StatusIsError = true;
                    return; // Stop further processing
                }
            }

            // Calculate parent path (null if we are in root)
            if (!string.IsNullOrEmpty(CurrentRelativePath))
            {
                var parent = Path.GetDirectoryName(CurrentRelativePath.TrimEnd(Path.DirectorySeparatorChar));
                ParentRelativePath = string.IsNullOrEmpty(parent) ? null : parent.Replace(Path.DirectorySeparatorChar, '/');
            }
            else
            {
                ParentRelativePath = null;
            }

            try
            {
                // List Directories
                foreach (var dirPath in Directory.GetDirectories(currentAbsolutePath))
                {
                    var dirInfo = new DirectoryInfo(dirPath);
                    FileManagerItems.Add(new FileManagerItem
                    {
                        Name = dirInfo.Name,
                        IsDirectory = true,
                        RelativePath = Path.GetRelativePath(_fileServerRootAbsolutePath, dirInfo.FullName).Replace(Path.DirectorySeparatorChar, '/'),
                        LastModified = dirInfo.LastWriteTimeUtc
                    });
                }

                // List Files
                foreach (var filePath in Directory.GetFiles(currentAbsolutePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    FileManagerItems.Add(new FileManagerItem
                    {
                        Name = fileInfo.Name,
                        IsDirectory = false,
                        RelativePath = Path.GetRelativePath(_fileServerRootAbsolutePath, fileInfo.FullName).Replace(Path.DirectorySeparatorChar, '/'),
                        SizeBytes = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTimeUtc
                    });
                }

                // Sort (directories first, then by name)
                FileManagerItems = FileManagerItems
                    .OrderBy(item => !item.IsDirectory) // Directories first (false comes before true)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error listing directory contents: {ex.Message}";
                StatusIsError = true;
                // Log the full exception
                Console.WriteLine($"[ERROR] Failed listing directory '{currentAbsolutePath}': {ex.ToString()}");
            }

            ModelState.Clear();
        }

        // Main Save Handler (now only saves/restarts Listener settings)
        public async Task<IActionResult> OnPostSaveListenerSettingsAsync()
        {
            Console.WriteLine($"[Configuration Page] OnPostSaveListenerSettingsAsync handler executing...");
            Console.WriteLine($"[Configuration Page SAVE HANDLER] Received values: IP='{ListenerIpAddress}', Port={ListenerPort}");

            ViewData["Title"] = "Configuration";
            IsListenerRunning = _processManager.IsRunning(ManagedProcessType.ImplantListener);
            IsFileServerRunning = _processManager.IsRunning(ManagedProcessType.HttpFileServer);

            ModelState.Remove(nameof(FileServerIpAddress));
            ModelState.Remove(nameof(FileServerPort));
            ModelState.Remove(nameof(UploadedFile));
            Console.WriteLine($"[Configuration Page SAVE HANDLER] Cleared ModelState for FileServer properties and UploadedFile.");

            // Validate only listener properties if needed (or rely on client-side/model binding)
            if (!ModelState.IsValid)
            {
                Console.WriteLine("[Configuration Page SAVE HANDLER] ModelState Invalid (After Clearing Irrelevant). Returning Page.");
                // Log the REMAINING errors
                foreach (var modelStateKey in ViewData.ModelState.Keys)
                {
                    var value = ViewData.ModelState[modelStateKey];
                    if (value.ValidationState == Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Invalid)
                    {
                        foreach (var error in value.Errors)
                        {
                            Console.WriteLine($"[ModelState ERROR] Key: {modelStateKey}, Error: {error.ErrorMessage}");
                        }
                    }
                }
                LoadCurrentSettings();
                return Page();
            }

            Console.WriteLine("[Configuration Page SAVE HANDLER] ModelState is VALID for Listener settings.");

            // Existing IP Address format check (redundant if using proper validation attributes, but safe to keep)
            if (!System.Net.IPAddress.TryParse(ListenerIpAddress, out _))
            {
                ModelState.AddModelError(nameof(ListenerIpAddress), "Invalid IP Address format.");
                LoadCurrentSettings(); // Reload display values
                return Page();
            }

            bool settingsSaved = SaveSpecificSettings(ManagedProcessType.ImplantListener);

            if (settingsSaved)
            {
                Console.WriteLine("[Configuration Page SAVE HANDLER] Settings successfully saved. Triggering restart task...");
                // Trigger only Listener restart in the background
                _ = Task.Run(() =>
                {
                    try
                    {
                        Console.WriteLine("[Configuration Page] Background task starting Listener restart...");
                        bool restartSucceeded = _processManager.RestartProcess(ManagedProcessType.ImplantListener);
                        Console.WriteLine($"[Configuration Page] Background Listener restart task completed. Result: {restartSucceeded}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[!!! CRITICAL ERROR !!!] Background Listener restart task failed: {ex.ToString()}"); }
                });
                StatusMessage = "Listener configuration saved. Listener restart initiated...";
                StatusIsError = false;
            }
            else
            {
                Console.WriteLine("[Configuration Page SAVE HANDLER] SaveSpecificSettings returned false. Setting error status.");
                StatusMessage = "Failed to save Listener configuration. Check logs.";
                StatusIsError = true;
            }

            LoadCurrentSettings(forceReload: true);
            return Page();
        }

        // ADD Handler for Saving File Server Settings
        public async Task<IActionResult> OnPostSaveFileServerSettingsAsync()
        {
            Console.WriteLine($"[Configuration Page] OnPostSaveFileServerSettingsAsync handler executing...");
            Console.WriteLine($"[Configuration Page SAVE HANDLER] Received values: IP='{FileServerIpAddress}', Port={FileServerPort}"); // Log bound values

            ViewData["Title"] = "Configuration";
            IsListenerRunning = _processManager.IsRunning(ManagedProcessType.ImplantListener);
            IsFileServerRunning = _processManager.IsRunning(ManagedProcessType.HttpFileServer);

            // *** Clear errors for properties NOT related to this form ***
            ModelState.Remove(nameof(ListenerIpAddress));
            ModelState.Remove(nameof(ListenerPort));
            ModelState.Remove(nameof(UploadedFile)); // Assuming this isn't part of the FS save form
            Console.WriteLine($"[Configuration Page SAVE HANDLER] Cleared ModelState for Listener properties and UploadedFile.");

            // NOW check validation for the relevant properties (File Server)
            if (!ModelState.IsValid)
            {
                Console.WriteLine("[Configuration Page SAVE HANDLER] ModelState Invalid (After Clearing Irrelevant). Returning Page."); // Log error
                                                                                                                                        // Log the REMAINING errors
                foreach (var modelStateKey in ViewData.ModelState.Keys)
                {
                    var value = ViewData.ModelState[modelStateKey];
                    if (value.ValidationState == Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Invalid)
                    {
                        foreach (var error in value.Errors)
                        {
                            Console.WriteLine($"[ModelState ERROR] Key: {modelStateKey}, Error: {error.ErrorMessage}");
                        }
                    }
                }
                LoadCurrentSettings();
                return Page();
            }

            // --- If we reach here, ModelState IS valid for the File Server properties ---
            Console.WriteLine("[Configuration Page SAVE HANDLER] ModelState is VALID for File Server settings.");

            bool settingsSaved = SaveSpecificSettings(ManagedProcessType.HttpFileServer);

            if (settingsSaved)
            {
                Console.WriteLine("[Configuration Page SAVE HANDLER] Settings successfully saved. Triggering restart task...");

                _ = Task.Run(() =>
                {
                    try
                    {
                        Console.WriteLine("[Configuration Page] Background task starting File Server restart...");
                        bool restartSucceeded = _processManager.RestartProcess(ManagedProcessType.HttpFileServer);
                        Console.WriteLine($"[Configuration Page] Background File Server restart task completed. Result: {restartSucceeded}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[!!! CRITICAL ERROR !!!] Background File Server restart task failed: {ex.ToString()}"); }
                });
                StatusMessage = "File Server configuration saved. File Server restart initiated...";
                StatusIsError = false;
            }
            else
            {
                Console.WriteLine("[Configuration Page SAVE HANDLER] SaveSpecificSettings returned false. Setting error status."); // Log failure path
                StatusMessage = "Failed to save File Server configuration. Check logs.";
                StatusIsError = true;
            }

            LoadCurrentSettings(forceReload: true);
            return Page();
        }

        // File Upload Handler
        public async Task<IActionResult> OnPostUploadFileAsync() // Removed 'CurrentRelativePath' from params, using BindProperty
        {
            ViewData["Title"] = "Configuration";
            IsListenerRunning = _processManager.IsRunning(ManagedProcessType.ImplantListener);
            IsFileServerRunning = _processManager.IsRunning(ManagedProcessType.HttpFileServer);

            // Clear unrelated validation errors
            ModelState.Remove(nameof(ListenerIpAddress));
            ModelState.Remove(nameof(ListenerPort));
            ModelState.Remove(nameof(FileServerIpAddress));
            ModelState.Remove(nameof(FileServerPort));

            // Check file validity first
            if (UploadedFile == null || UploadedFile.Length == 0)
            {
                ModelState.AddModelError(nameof(UploadedFile), "Please select a file to upload.");
            }
            if (!ModelState.IsValid)
            {
                LoadCurrentSettings(); // Reload necessary state
                                       // Need to reload file manager items as well, as we are returning Page()
                OnGet(); // Re-run OnGet logic to repopulate FileManagerItems 
                return Page();
            }

            // Get the target directory based on CurrentRelativePath (sent with the form)
            string? targetAbsolutePath = GetSafeCurrentAbsolutePath(CurrentRelativePath, out bool pathIsSafe);

            if (!pathIsSafe || targetAbsolutePath == null)
            {
                StatusMessage = "Error: Invalid or unsafe target directory for upload.";
                StatusIsError = true;
                OnGet(); // Reload file list etc.
                return Page();
            }

            try
            {
                // Security: Sanitize filename (keep existing logic)
                string originalFileName = UploadedFile!.FileName; // Not null here due to validation check
                string safeFileName = Path.GetFileName(originalFileName);
                if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || safeFileName.StartsWith(".") || safeFileName.Equals(".."))
                {
                    StatusMessage = $"Invalid or potentially unsafe filename: '{originalFileName}'";
                    StatusIsError = true;
                    OnGet(); // Reload file list etc.
                    return Page();
                }

                var filePath = Path.Combine(targetAbsolutePath, safeFileName); // Combine SAFE absolute dir + SAFE filename

                Console.WriteLine($"[Configuration Page Upload] Attempting to save uploaded file '{safeFileName}' to: {filePath}");

                // Overwrite if exists, or add check here
                await using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await UploadedFile.CopyToAsync(stream);
                }

                StatusMessage = $"File '{safeFileName}' uploaded successfully to '{CurrentPathDisplay}'.";
                StatusIsError = false;
                Console.WriteLine($"[Configuration Page Upload] File '{safeFileName}' saved successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to upload file: {ex.ToString()}");
                StatusMessage = $"Error uploading file: {ex.Message}";
                StatusIsError = true;
            }

            // Redirect to the same relative path after upload to refresh the list
            return RedirectToPage(new { CurrentRelativePath = this.CurrentRelativePath });
        }

        // --- NEW Download Handler ---
        public IActionResult OnGetDownloadFile(string? relativeFilePath)
        {
            if (string.IsNullOrEmpty(relativeFilePath))
            {
                return NotFound("File path not provided.");
            }

            if (!IsFilePathSafe(relativeFilePath, out string absoluteFilePath) || !System.IO.File.Exists(absoluteFilePath))
            {
                StatusMessage = "Error: Invalid, unsafe, or non-existent file path requested for download.";
                StatusIsError = true;
                // Redirect back to the directory view instead of showing raw error
                // Try to determine the parent dir of the requested file
                string? redirectPath = null;
                try { redirectPath = Path.GetDirectoryName(relativeFilePath); } catch { }
                return RedirectToPage(new { CurrentRelativePath = redirectPath });
            }

            try
            {
                Console.WriteLine($"[File Manager Download] Serving file: {absoluteFilePath}");
                // Determine content type (optional but good practice)
                var contentType = "application/octet-stream"; // Default
                                                              // You could use a library or map extensions here if needed
                                                              // var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
                                                              // if (provider.TryGetContentType(absoluteFilePath, out var providerType)) { contentType = providerType; }

                // Return the file using PhysicalFileResult for efficient streaming
                return PhysicalFile(absoluteFilePath, contentType, Path.GetFileName(absoluteFilePath));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to download file '{absoluteFilePath}': {ex.ToString()}");
                StatusMessage = $"Error downloading file: {ex.Message}";
                StatusIsError = true;
                string? redirectPath = null;
                try { redirectPath = Path.GetDirectoryName(relativeFilePath); } catch { }
                return RedirectToPage(new { CurrentRelativePath = redirectPath });
            }
        }

        private void LoadCurrentSettings(bool forceReload = false)
        {
            try
            {
                if (forceReload && _configuration is IConfigurationRoot configRoot)
                {
                    // Log before/after values if debugging needed
                    try { configRoot.Reload(); Console.WriteLine("[Configuration Page] Config reloaded for UI."); }
                    catch (Exception ex) { Console.WriteLine($"[ERROR] Config reload for UI failed: {ex.Message}"); }
                }

                Console.WriteLine("[Configuration Page] Reading config for UI...");
                ListenerIpAddress = _configuration.GetValue<string>("Listeners:Implant:IpAddress") ?? "127.0.0.1";
                ListenerPort = _configuration.GetValue<int?>("Listeners:Implant:Port") ?? 80;

                FileServerIpAddress = _configuration.GetValue<string>("HttpFileServer:Settings:IpAddress") ?? "127.0.0.1";
                FileServerPort = _configuration.GetValue<int?>("HttpFileServer:Settings:Port") ?? 8081;
                FileServerServeDirectory = Path.GetFullPath(Path.Combine(
                    _environment.ContentRootPath,
                    _configuration.GetValue<string>("HttpFileServer:ServeFromDirectory") ?? "file_server_root"));
                // Construct the base URL for display
                FileServerUrl = $"http://{FileServerIpAddress}:{FileServerPort}/";

                Console.WriteLine($"[Configuration Page] UI Values: L IP={ListenerIpAddress}, L Port={ListenerPort}, FS IP={FileServerIpAddress}, FS Port={FileServerPort}");
            }
            catch (Exception ex) { Console.WriteLine($"[ERROR] Failed loading UI config: {ex.Message}"); /* Set defaults/error indicators */ }
        }

        private bool SaveSpecificSettings(ManagedProcessType typeToSave)
        {
            string targetConfigFile = $"appsettings.{_environment.EnvironmentName}.json";
            string targetConfigPath = Path.Combine(_environment.ContentRootPath, targetConfigFile);
            string baseConfigPath = Path.Combine(_environment.ContentRootPath, "appsettings.json");
            string pathToWrite = System.IO.File.Exists(targetConfigPath) ? targetConfigPath : baseConfigPath;
            Console.WriteLine($"[Configuration Page SAVE] Attempting to save settings for {typeToSave} to FILE: '{pathToWrite}'");

            try
            {
                if (!System.IO.File.Exists(pathToWrite))
                {
                    Console.WriteLine($"[Configuration Page SAVE] Target file '{pathToWrite}' not found. Creating empty file.");
                    System.IO.File.WriteAllText(pathToWrite, "{}"); // Ensure exists
                }
                else
                {
                    Console.WriteLine($"[Configuration Page SAVE] Target file '{pathToWrite}' exists.");
                }


                string jsonContent = System.IO.File.ReadAllText(pathToWrite);
                var jsonOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                using var jsonDoc = JsonDocument.Parse(jsonContent, jsonOptions);
                var root = JsonObject.Create(jsonDoc.RootElement.Clone()) ?? new JsonObject();
                string originalJson = root.ToJsonString(); // Log before change for comparison

                string valueToSaveIp = "ERROR";
                int valueToSavePort = -1;

                // Selectively update the section based on type
                switch (typeToSave)
                {
                    case ManagedProcessType.ImplantListener:
                        valueToSaveIp = ListenerIpAddress;
                        valueToSavePort = ListenerPort;
                        Console.WriteLine($"[Configuration Page SAVE] Preparing Listener values: IP='{valueToSaveIp}', Port={valueToSavePort} (From PageModel)");
                        var listenersNode = root["Listeners"]?.AsObject() ?? new JsonObject();
                        var implantNode = listenersNode["Implant"]?.AsObject() ?? new JsonObject();
                        Console.WriteLine($"[Configuration Page SAVE] Listener: Setting IP='{ListenerIpAddress}', Port={ListenerPort}");
                        implantNode["IpAddress"] = ListenerIpAddress;
                        implantNode["Port"] = ListenerPort;
                        listenersNode["Implant"] = implantNode;
                        root["Listeners"] = listenersNode;
                        break;
                    case ManagedProcessType.HttpFileServer:
                        valueToSaveIp = FileServerIpAddress;
                        valueToSavePort = FileServerPort;
                        Console.WriteLine($"[Configuration Page SAVE] Preparing FileServer values: IP='{valueToSaveIp}', Port={valueToSavePort} (From PageModel)");
                        var fsRootNode = root["HttpFileServer"]?.AsObject() ?? new JsonObject();
                        var fsSettingsNode = fsRootNode["Settings"]?.AsObject() ?? new JsonObject();
                        Console.WriteLine($"[Configuration Page SAVE] FileServer: Setting IP='{FileServerIpAddress}', Port={FileServerPort}");
                        fsSettingsNode["IpAddress"] = FileServerIpAddress;
                        fsSettingsNode["Port"] = FileServerPort;
                        fsRootNode["Settings"] = fsSettingsNode;
                        root["HttpFileServer"] = fsRootNode;
                        break;
                    default:
                        Console.WriteLine($"[ERROR] Unknown process type to save: {typeToSave}");
                        return false;
                }

                var writeOptions = new JsonSerializerOptions { WriteIndented = true };
                string newJson = root.ToJsonString(writeOptions);
                Console.WriteLine($"[Configuration Page SAVE] Writing NEW JSON to '{pathToWrite}':\n{newJson}");

                // *** CRITICAL: Add try-catch around the write itself ***
                try
                {
                    System.IO.File.WriteAllText(pathToWrite, newJson);
                    Console.WriteLine($"[Configuration Page SAVE] Successfully wrote settings for {typeToSave}.");
                }
                catch (IOException ioEx)
                {
                    Console.WriteLine($"[ERROR] FAILED TO WRITE to '{pathToWrite}': {ioEx.ToString()}"); 
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed during save operation for {typeToSave} settings: {ex.ToString()}");
                return false;
            }
        }

        public IActionResult OnPostStartFileServer()
        {
            Console.WriteLine("[Configuration Page] OnPostStartFileServer handler executing..."); // Log execution
            ModelState.Clear();
            try
            {
                bool success = _processManager.StartProcess(ManagedProcessType.HttpFileServer);
                if (success)
                {
                    StatusMessage = "Attempting to start HTTP File Server...";
                    StatusIsError = false;
                }
                else
                {
                    // StartProcess should log specific errors
                    StatusMessage = "Failed to start HTTP File Server. Check C2 console logs.";
                    StatusIsError = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Exception in OnPostStartFileServer: {ex.ToString()}");
                StatusMessage = $"Unexpected error trying to start File Server: {ex.Message}";
                StatusIsError = true;
            }
            // Redirect to refresh the page and show updated status/messages
            return RedirectToPage();
        }

        public IActionResult OnPostStopFileServer()
        {
            Console.WriteLine("[Configuration Page] OnPostStopFileServer handler executing..."); // Log execution
            ModelState.Clear();
            try
            {
                _processManager.StopProcess(ManagedProcessType.HttpFileServer);
                StatusMessage = "Attempting to stop HTTP File Server...";
                StatusIsError = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Exception in OnPostStopFileServer: {ex.ToString()}");
                StatusMessage = $"Unexpected error trying to stop File Server: {ex.Message}";
                StatusIsError = true;
            }
            // Redirect to refresh the page and show updated status/messages
            return RedirectToPage();
        }

        // Optional: Add handler to manually start/stop listener from UI
        public IActionResult OnPostStartListener()
        {
            _processManager.StartProcess(ManagedProcessType.ImplantListener);
            StatusMessage = "Attempted to start listener.";
            ModelState.Clear();
            IsListenerRunning = _processManager.IsRunning(ManagedProcessType.ImplantListener);
            LoadCurrentSettings(true); // Refresh UI
            return RedirectToPage(); // Refresh page state
        }

        public IActionResult OnPostStopListener()
        {
            _processManager.StopProcess(ManagedProcessType.ImplantListener);
            StatusMessage = "Attempted to stop listener.";
            ModelState.Clear();
            IsListenerRunning = _processManager.IsRunning(ManagedProcessType.ImplantListener);
            LoadCurrentSettings(true); // Refresh UI
            return RedirectToPage(); // Refresh page state
        }

        public async Task<IActionResult> OnPostDeleteFileAsync(string? relativeFilePath, string? currentRelativePath)
        {
            _logger.LogInformation("Attempting to delete file. Relative Path: {RelativePath}, Current View Path: {ViewPath}", relativeFilePath, currentRelativePath); // Add logger field if not present

            // 1. Basic Input Validation
            if (string.IsNullOrEmpty(relativeFilePath))
            {
                StatusMessage = "Error: File path not provided for deletion.";
                StatusIsError = true;
                return RedirectToPage(new { CurrentRelativePath = currentRelativePath }); // Return to previous view
            }

            // 2. Security Validation
            if (!IsFilePathSafe(relativeFilePath, out string absoluteFilePath))
            {
                StatusMessage = "Error: Invalid or unsafe file path provided for deletion.";
                StatusIsError = true;
                _logger.LogWarning("Unsafe file path detected for deletion: Relative='{RelativePath}', Absolute='{AbsolutePath}'", relativeFilePath, absoluteFilePath);
                return RedirectToPage(new { CurrentRelativePath = currentRelativePath });
            }

            // 3. Check if it's actually a file and exists
            if (!System.IO.File.Exists(absoluteFilePath))
            {
                // Check if it's a directory instead (shouldn't happen if button is only on files)
                if (System.IO.Directory.Exists(absoluteFilePath))
                {
                    StatusMessage = "Error: Cannot delete directories using this action.";
                    StatusIsError = true;
                    _logger.LogWarning("Attempted to delete a directory via file delete: {Path}", absoluteFilePath);
                }
                else
                {
                    StatusMessage = "Error: File not found for deletion.";
                    StatusIsError = true;
                    _logger.LogWarning("File not found for deletion: {Path}", absoluteFilePath);
                }
                return RedirectToPage(new { CurrentRelativePath = currentRelativePath });
            }

            // 4. Perform Deletion
            try
            {
                System.IO.File.Delete(absoluteFilePath);
                string fileName = Path.GetFileName(absoluteFilePath);
                StatusMessage = $"File '{fileName}' deleted successfully from '{CurrentPathDisplay}'."; // Use CurrentPathDisplay here
                StatusIsError = false;
                _logger.LogInformation("File deleted successfully: {Path}", absoluteFilePath);
            }
            catch (IOException ioEx) // Catch specific IO errors
            {
                StatusMessage = $"Error deleting file: {ioEx.Message}. Check file permissions or if it's in use.";
                StatusIsError = true;
                _logger.LogError(ioEx, "IO Error deleting file: {Path}", absoluteFilePath);
            }
            catch (UnauthorizedAccessException uaEx)
            {
                StatusMessage = $"Error deleting file: Permission denied. Check file permissions.";
                StatusIsError = true;
                _logger.LogError(uaEx, "Permission Error deleting file: {Path}", absoluteFilePath);
            }
            catch (Exception ex) // Catch other potential errors
            {
                StatusMessage = $"An unexpected error occurred while deleting the file: {ex.Message}";
                StatusIsError = true;
                _logger.LogError(ex, "Unexpected Error deleting file: {Path}", absoluteFilePath);
            }

            // 5. Redirect back to the same directory view
            return RedirectToPage(new { CurrentRelativePath = currentRelativePath });
        }
    }
}