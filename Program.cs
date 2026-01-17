using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Wizard
{
    class Program
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private const string DefaultOllamaBaseUrl = "http://localhost:11434"; // Fallback if settings.json is not found
        private const string DefaultModel = "llama3.2:latest"; // Fallback if settings.json is not found
        private const string SettingsFileName = "settings.json";
        private const string LogFileName = "wizard.log";
        private static string? logFilePath = null;
        private static bool loggingEnabled = false;

        static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: wizard <natural language command>");
                Console.WriteLine("Example: wizard list all process which has processName like ja");
                return 1;
            }

            // Combine all arguments into a single sentence
            string sentence = string.Join(" ", args);

            try
            {
                // Load settings from settings.json
                var (model, traceMode, firstPrompt, validationPrompt, ollamaBaseUrl, logging) = LoadSettings();
                loggingEnabled = logging;
                
                WriteLog("Application started");
                WriteLog($"User input: {sentence}");
                WriteLog($"Using model: {model}");
                WriteLog($"Ollama URL: {ollamaBaseUrl}");
                
                Console.WriteLine($"Using model: {model}");
                
                // Install PSScriptAnalyzer if not already installed
                WriteLog("Checking PSScriptAnalyzer installation");
                await EnsurePSScriptAnalyzerInstalled();
                
                // Attempt to get valid PowerShell command (max 2 attempts)
                string? finalCommand = null;
                string? query = null;
                string? ollamaResponse = null;
                bool isValid = false;
                
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    WriteLog($"Attempt {attempt}: Starting command generation");
                    
                    // Step 1: Send sentence to Ollama + first-prompt
                    WriteLog($"Attempt {attempt}: Sending request to Ollama (first-prompt)");
                    var (powerShellCommand, step1Query, step1Response) = await GetPowerShellCommandFromOllama(sentence, model, firstPrompt, ollamaBaseUrl);
                    WriteLog($"Attempt {attempt}: Received response from Ollama: {powerShellCommand ?? "null"}");
                    
                    if (string.IsNullOrWhiteSpace(powerShellCommand))
                    {
                        if (attempt == 1)
                        {
                            Console.Error.WriteLine("Failed to get PowerShell command from Ollama. Retrying...");
                            continue;
                        }
                        else
                        {
                            Console.Error.WriteLine("Failed to get PowerShell command from Ollama after retry.");
                            if (traceMode)
                            {
                                Console.WriteLine();
                                Console.WriteLine($"Query sent to Ollama: {step1Query}");
                                Console.WriteLine($"Response from Ollama: {step1Response}");
                            }
                            return 1;
                        }
                    }
                    
                    // Step 2: Validate and regenerate the command if needed
                    WriteLog($"Attempt {attempt}: Sending validation request to Ollama");
                    var (validatedCommand, validationResponse) = await ValidateAndRegenerateCommand(powerShellCommand, model, sentence, validationPrompt, ollamaBaseUrl);
                    string candidateCommand = validatedCommand ?? powerShellCommand;
                    WriteLog($"Attempt {attempt}: Validation response: {candidateCommand}");
                    
                    if (traceMode)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"Attempt {attempt} - Initial command from Ollama: {powerShellCommand}");
                        if (!string.IsNullOrEmpty(validationResponse))
                        {
                            Console.WriteLine($"Validation response: {validationResponse}");
                        }
                        if (validatedCommand != null && validatedCommand != powerShellCommand)
                        {
                            Console.WriteLine($"Validated/Regenerated command: {validatedCommand}");
                        }
                    }
                    
                    // Step 3: Check with PSScriptAnalyzer if command is valid
                    WriteLog($"Attempt {attempt}: Validating command with PSScriptAnalyzer");
                    var (isValidCommand, analyzerErrors) = await ValidatePowerShellWithPSScriptAnalyzer(candidateCommand);
                    
                    if (isValidCommand)
                    {
                        WriteLog($"Attempt {attempt}: PSScriptAnalyzer validation passed");
                        finalCommand = candidateCommand;
                        query = step1Query;
                        ollamaResponse = step1Response;
                        isValid = true;
                        break;
                    }
                    else
                    {
                        WriteLog($"Attempt {attempt}: PSScriptAnalyzer validation failed: {analyzerErrors}");
                        if (traceMode)
                        {
                            Console.WriteLine($"PSScriptAnalyzer found errors: {analyzerErrors}");
                        }
                        
                        if (attempt == 1)
                        {
                            Console.WriteLine("PSScriptAnalyzer detected errors. Retrying with new command...");
                            WriteLog("Retrying command generation");
                        }
                        else
                        {
                            Console.Error.WriteLine($"PSScriptAnalyzer detected errors after retry: {analyzerErrors}");
                            Console.Error.WriteLine("Failed to generate a valid PowerShell command after 2 attempts.");
                            WriteLog("Command generation failed after 2 attempts");
                            return 1;
                        }
                    }
                }
                
                if (!isValid || finalCommand == null)
                {
                    Console.Error.WriteLine("Failed to generate a valid PowerShell command.");
                    return 1;
                }

                Console.WriteLine($"Executing: {finalCommand}");
                Console.WriteLine();

                // Execute the PowerShell command
                WriteLog($"Executing command: {finalCommand}");
                int exitCode = await ExecutePowerShellCommand(finalCommand);
                WriteLog($"Command execution completed with exit code: {exitCode}");
                
                // Show the query and response from Ollama if traceMode is enabled
                if (traceMode && query != null && ollamaResponse != null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Query sent to Ollama: {query}");
                    Console.WriteLine($"Response from Ollama: {ollamaResponse}");
                }
                
                return exitCode;
            }
            catch (Exception ex)
            {
                WriteLog($"Error occurred: {ex.Message}");
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        // Default prompts
        private const string DefaultFirstPrompt = "You are a PowerShell 5 expert.Rules:- Output ONLY valid PowerShell code - ONE LINE only - No markdown - No explanation - No comments - No extra spaces - No line breaks - Obey formatting EXACTLY If you break any rule, the answer is invalid.do this PowerShell request only with one line powershell-command, create command for this requirements: {sentence}";
        private const string DefaultValidationPrompt = "this is a powershell 5 command  : '{command}' and this is the requirements : '{requirements}', Is it one line? - Does it run in PowerShell 5? - Does it meet ALL rules? -If not, regenerate the command. Output only final answer. the response must create only one line powershell-command nothing more";

        static void WriteLog(string message)
        {
            if (!loggingEnabled) return;
            
            try
            {
                if (logFilePath == null)
                {
                    // Determine log file path (same directory as executable or current directory)
                    string? baseDir = AppContext.BaseDirectory;
                    if (string.IsNullOrEmpty(baseDir))
                    {
                        baseDir = Directory.GetCurrentDirectory();
                    }
                    logFilePath = Path.Combine(baseDir, LogFileName);
                }
                
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string logEntry = $"[{timestamp}] {message}";
                
                File.AppendAllText(logFilePath, logEntry + Environment.NewLine);
            }
            catch
            {
                // Silently fail if logging cannot be written
            }
        }

        static (string model, bool traceMode, string firstPrompt, string validationPrompt, string ollamaBaseUrl, bool logging) LoadSettings()
        {
            try
            {
                // Try to find settings.json in the current directory or executable directory
                string settingsPath = SettingsFileName;
                
                // If not found in current directory, try executable directory
                if (!File.Exists(settingsPath))
                {
                    // Use AppContext.BaseDirectory for single-file apps (Assembly.Location returns empty string)
                    string? exeDir = AppContext.BaseDirectory;
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        settingsPath = Path.Combine(exeDir, SettingsFileName);
                    }
                }
                
                if (File.Exists(settingsPath))
                {
                    string jsonContent = File.ReadAllText(settingsPath);
                    var settings = JsonConvert.DeserializeObject<Settings>(jsonContent);
                    
                    string model = DefaultModel;
                    bool traceMode = false;
                    string firstPrompt = DefaultFirstPrompt;
                    string validationPrompt = DefaultValidationPrompt;
                    string ollamaBaseUrl = DefaultOllamaBaseUrl;
                    bool logging = false;
                    
                    if (settings?.Options != null)
                    {
                        if (settings.Options.Model != null)
                        {
                            model = settings.Options.Model;
                        }
                        
                        // Check traceMode - can be string "true"/"false" or boolean
                        if (settings.Options.TraceMode != null)
                        {
                            if (settings.Options.TraceMode is bool boolValue)
                            {
                                traceMode = boolValue;
                            }
                            else if (settings.Options.TraceMode is string stringValue)
                            {
                                traceMode = stringValue.ToLower() == "true";
                            }
                        }
                        
                        // Check logging - can be string "true"/"false" or boolean
                        if (settings.Options.Logging != null)
                        {
                            if (settings.Options.Logging is bool boolValue)
                            {
                                logging = boolValue;
                            }
                            else if (settings.Options.Logging is string stringValue)
                            {
                                logging = stringValue.ToLower() == "true";
                            }
                        }
                        
                        // Load OllamaBaseUrl from settings, use default if not found
                        if (!string.IsNullOrWhiteSpace(settings.Options.OllamaBaseUrl))
                        {
                            ollamaBaseUrl = settings.Options.OllamaBaseUrl;
                        }
                        
                        // Load prompts from settings, use defaults if not found
                        if (!string.IsNullOrWhiteSpace(settings.Options.FirstPrompt))
                        {
                            firstPrompt = settings.Options.FirstPrompt;
                        }
                        
                        if (!string.IsNullOrWhiteSpace(settings.Options.ValidationPrompt))
                        {
                            validationPrompt = settings.Options.ValidationPrompt;
                        }
                    }
                    
                    return (model, traceMode, firstPrompt, validationPrompt, ollamaBaseUrl, logging);
                }
                else
                {
                    Console.Error.WriteLine($"Warning: {SettingsFileName} not found. Using default model: {DefaultModel}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to load settings from {SettingsFileName}: {ex.Message}");
                Console.Error.WriteLine($"Using default model: {DefaultModel}");
            }
            
            return (DefaultModel, false, DefaultFirstPrompt, DefaultValidationPrompt, DefaultOllamaBaseUrl, false);
        }

        static async Task<(string? command, string query, string response)> GetPowerShellCommandFromOllama(string sentence, string model, string promptTemplate, string ollamaBaseUrl)
        {
            // Construct the prompt using the template from settings
            string prompt = promptTemplate.Replace("{sentence}", sentence);
            
            try
            {
                // Prepare the request payload
                var requestBody = new
                {
                    model = model,
                    prompt = prompt,
                    stream = false
                };

                string jsonBody = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                WriteLog($"Sending POST request to {ollamaBaseUrl}/api/generate");
                // Send request to Ollama
                HttpResponseMessage response = await httpClient.PostAsync($"{ollamaBaseUrl}/api/generate", content);
                response.EnsureSuccessStatusCode();

                string responseBody = await response.Content.ReadAsStringAsync();
                WriteLog($"Received response from Ollama (status: {response.StatusCode})");
                
                // Parse the Ollama response
                var ollamaResponse = JsonConvert.DeserializeObject<OllamaResponse>(responseBody);
                
                // Store the raw response text for tracing (the actual LLM output)
                string rawResponse = ollamaResponse?.Response ?? string.Empty;
                
                if (ollamaResponse?.Response == null)
                {
                    return (null, prompt, rawResponse);
                }

                // Clean the response - remove markdown code blocks and trim
                string cleanedResponse = ollamaResponse.Response.Trim();
                
                // Remove markdown code blocks if present
                if (cleanedResponse.StartsWith("```powershell"))
                {
                    cleanedResponse = cleanedResponse.Substring(13);
                }
                else if (cleanedResponse.StartsWith("```"))
                {
                    cleanedResponse = cleanedResponse.Substring(3);
                }
                if (cleanedResponse.EndsWith("```"))
                {
                    cleanedResponse = cleanedResponse.Substring(0, cleanedResponse.Length - 3);
                }
                cleanedResponse = cleanedResponse.Trim();

                // The response is now just the PowerShell command directly
                string? powerShellCommand = cleanedResponse;
                
                return (powerShellCommand, prompt, rawResponse);
            }
            catch (HttpRequestException ex)
            {
                WriteLog($"Failed to connect to Ollama: {ex.Message}");
                Console.Error.WriteLine($"Failed to connect to Ollama at {ollamaBaseUrl}");
                Console.Error.WriteLine($"Make sure Ollama is running and accessible.");
                Console.Error.WriteLine($"Error: {ex.Message}");
                return (null, prompt, string.Empty);
            }
            catch (Exception ex)
            {
                WriteLog($"Error calling Ollama: {ex.Message}");
                Console.Error.WriteLine($"Error calling Ollama: {ex.Message}");
                return (null, prompt, string.Empty);
            }
        }

        static async Task<(string? command, string response)> ValidateAndRegenerateCommand(string command, string model, string requirements, string validationPromptTemplate, string ollamaBaseUrl)
        {
            // Construct the validation prompt using the template from settings
            string validationPrompt = validationPromptTemplate.Replace("{command}", command).Replace("{requirements}", requirements);
            
            try
            {
                // Prepare the request payload
                var requestBody = new
                {
                    model = model,
                    prompt = validationPrompt,
                    stream = false
                };

                string jsonBody = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                WriteLog($"Sending validation request to {ollamaBaseUrl}/api/generate");
                // Send request to Ollama
                HttpResponseMessage response = await httpClient.PostAsync($"{ollamaBaseUrl}/api/generate", content);
                response.EnsureSuccessStatusCode();

                string responseBody = await response.Content.ReadAsStringAsync();
                WriteLog($"Received validation response from Ollama (status: {response.StatusCode})");
                
                // Parse the Ollama response
                var ollamaResponse = JsonConvert.DeserializeObject<OllamaResponse>(responseBody);
                
                // Store the raw response text for tracing
                string rawResponse = ollamaResponse?.Response ?? string.Empty;
                
                if (ollamaResponse?.Response == null)
                {
                    return (null, rawResponse);
                }

                // Clean the response - remove markdown code blocks and trim
                string cleanedResponse = ollamaResponse.Response.Trim();
                
                // Remove markdown code blocks if present
                if (cleanedResponse.StartsWith("```powershell"))
                {
                    cleanedResponse = cleanedResponse.Substring(13);
                }
                else if (cleanedResponse.StartsWith("```"))
                {
                    cleanedResponse = cleanedResponse.Substring(3);
                }
                if (cleanedResponse.EndsWith("```"))
                {
                    cleanedResponse = cleanedResponse.Substring(0, cleanedResponse.Length - 3);
                }
                cleanedResponse = cleanedResponse.Trim();

                // The response is now just the PowerShell command directly
                string? validatedCommand = cleanedResponse;
                
                return (validatedCommand, rawResponse);
            }
            catch (HttpRequestException ex)
            {
                WriteLog($"Failed to validate command with Ollama: {ex.Message}");
                Console.Error.WriteLine($"Failed to validate command with Ollama: {ex.Message}");
                return (null, string.Empty);
            }
            catch (Exception ex)
            {
                WriteLog($"Error validating command: {ex.Message}");
                Console.Error.WriteLine($"Error validating command: {ex.Message}");
                return (null, string.Empty);
            }
        }

        static async Task EnsurePSScriptAnalyzerInstalled()
        {
            try
            {
                // Check if PSScriptAnalyzer is already installed
                WriteLog("Checking if PSScriptAnalyzer is installed");
                var checkProcess = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"Get-Module -ListAvailable -Name PSScriptAnalyzer | Select-Object -First 1\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var checkProc = new Process { StartInfo = checkProcess };
                checkProc.Start();
                string checkOutput = await checkProc.StandardOutput.ReadToEndAsync();
                await checkProc.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(checkOutput) || !checkOutput.Contains("PSScriptAnalyzer"))
                {
                    // Install PSScriptAnalyzer
                    WriteLog("PSScriptAnalyzer not found, installing...");
                    Console.WriteLine("Installing PSScriptAnalyzer module...");
                    var installProcess = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -Command \"Install-Module PSScriptAnalyzer -Scope CurrentUser -Force\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var installProc = new Process { StartInfo = installProcess };
                    installProc.Start();
                    string installOutput = await installProc.StandardOutput.ReadToEndAsync();
                    string installError = await installProc.StandardError.ReadToEndAsync();
                    await installProc.WaitForExitAsync();

                    if (installProc.ExitCode != 0 && !string.IsNullOrEmpty(installError))
                    {
                        WriteLog($"Failed to install PSScriptAnalyzer: {installError}");
                        Console.Error.WriteLine($"Warning: Failed to install PSScriptAnalyzer: {installError}");
                    }
                    else
                    {
                        WriteLog("PSScriptAnalyzer installed successfully");
                        Console.WriteLine("PSScriptAnalyzer installed successfully.");
                    }
                }
                else
                {
                    WriteLog("PSScriptAnalyzer is already installed");
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error checking/installing PSScriptAnalyzer: {ex.Message}");
                Console.Error.WriteLine($"Warning: Failed to check/install PSScriptAnalyzer: {ex.Message}");
            }
        }

        static async Task<(bool isValid, string errors)> ValidatePowerShellWithPSScriptAnalyzer(string command)
        {
            try
            {
                // Create a temporary script file for PSScriptAnalyzer to analyze
                string tempScript = Path.Combine(Path.GetTempPath(), $"wizard_temp_{Guid.NewGuid()}.ps1");
                await File.WriteAllTextAsync(tempScript, command);

                try
                {
                    // Run PSScriptAnalyzer on the script
                    var processStartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -Command \"Import-Module PSScriptAnalyzer -ErrorAction SilentlyContinue; $results = Invoke-ScriptAnalyzer -Path '{tempScript}' -ErrorAction SilentlyContinue; if ($results) {{ $results | ConvertTo-Json -Compress }} else {{ '{{}}' }}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = new Process { StartInfo = processStartInfo };
                    process.Start();
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    // If there's output (JSON with errors), the command has issues
                    if (!string.IsNullOrWhiteSpace(output) && output.Trim() != "{}" && !output.Contains("\"Message\":null"))
                    {
                        // Try to parse JSON to get error messages
                        try
                        {
                            var errors = JsonConvert.DeserializeObject<dynamic[]>(output);
                            if (errors != null && errors.Length > 0)
                            {
                                var errorMessages = new List<string>();
                                foreach (var err in errors)
                                {
                                    if (err?.Message != null)
                                    {
                                        errorMessages.Add(err.Message.ToString());
                                    }
                                }
                                return (false, string.Join("; ", errorMessages));
                            }
                        }
                        catch
                        {
                            // If JSON parsing fails, return the raw output
                            return (false, output.Trim());
                        }
                    }

                    // If no errors or empty output, command is valid
                    return (true, string.Empty);
                }
                finally
                {
                    // Clean up temp file
                    try
                    {
                        if (File.Exists(tempScript))
                        {
                            File.Delete(tempScript);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                // If validation fails due to error, assume command is valid to avoid blocking
                Console.Error.WriteLine($"Warning: PSScriptAnalyzer validation error: {ex.Message}");
                return (true, string.Empty);
            }
        }

        static async Task<int> ExecutePowerShellCommand(string command)
        {
            try
            {
                WriteLog($"Starting PowerShell execution: {command}");
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{command.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = processStartInfo };
                
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Console.WriteLine(e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Console.Error.WriteLine(e.Data);
                    }
                };

                process.Start();
                WriteLog("PowerShell process started");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Add a timeout of 30 seconds to prevent hanging
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    var waitTask = process.WaitForExitAsync();
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
                    
                    var completedTask = await Task.WhenAny(waitTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        // Timeout occurred
                        WriteLog("Command execution timed out after 30 seconds");
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("Error: Command execution timed out after 30 seconds.");
                        Console.Error.WriteLine("The command may be incomplete or waiting for input.");
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill();
                                WriteLog("PowerShell process killed due to timeout");
                            }
                        }
                        catch { }
                        return 1;
                    }
                    
                    // Wait for the process to actually exit
                    await waitTask;
                    WriteLog($"PowerShell process exited with code: {process.ExitCode}");
                }
                catch (OperationCanceledException)
                {
                    WriteLog("Command execution timed out after 30 seconds (OperationCanceledException)");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Error: Command execution timed out after 30 seconds.");
                    Console.Error.WriteLine("The command may be incomplete or waiting for input.");
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            WriteLog("PowerShell process killed due to timeout");
                        }
                    }
                    catch { }
                    return 1;
                }
                
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                WriteLog($"Error executing PowerShell command: {ex.Message}");
                Console.Error.WriteLine($"Error executing PowerShell command: {ex.Message}");
                return 1;
            }
        }
    }

    // Response classes for JSON deserialization
    class OllamaResponse
    {
        public string? Response { get; set; }
    }

    class CommandResponse
    {
        public string? Command { get; set; }
        public string? Query { get; set; }
    }

    // Settings classes for JSON deserialization
    class Settings
    {
        public SettingsOptions? Options { get; set; }
    }

    class SettingsOptions
    {
        public string? Model { get; set; }
        public object? TraceMode { get; set; } // Can be string "true"/"false" or boolean
        public string? OllamaBaseUrl { get; set; }
        public object? Logging { get; set; } // Can be string "true"/"false" or boolean
        
        [JsonProperty("first-prompt")]
        public string? FirstPrompt { get; set; }
        
        [JsonProperty("validation-prompt")]
        public string? ValidationPrompt { get; set; }
    }
}
