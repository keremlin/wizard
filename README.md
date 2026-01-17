# Wizard - Natural Language PowerShell Command Tool

Wizard is a C# console application that converts natural language commands into PowerShell commands using a local Ollama LLM, then executes them.

## Prerequisites

1. **.NET 8.0 SDK** - Download from [Microsoft's website](https://dotnet.microsoft.com/download)
2. **Ollama** - Install and run Ollama locally. Download from [ollama.ai](https://ollama.ai)
3. **Ollama Model** - Make sure you have a model installed (e.g., `llama3`, `llama2`, etc.)

## Building the Project

1. Open a terminal in the project directory
2. Run:
   ```bash
   dotnet restore
   dotnet build
   ```

## Configuration

The default Ollama endpoint is `http://localhost:11434`. If your Ollama instance runs on a different address, modify the `OllamaBaseUrl` constant in `Program.cs`.

The default model is `llama3`. To use a different model, change the `DefaultModel` constant in `Program.cs`.

## Usage

After building, you can run the wizard command:

```powershell
wizard list all process which has processName like ja
```

Or any other natural language PowerShell request:

```powershell
wizard get all services that are running
wizard show disk usage for C drive
wizard find files larger than 100MB in current directory
```

## How It Works

1. The application takes the natural language sentence from the command line
2. It sends a request to your local Ollama instance asking it to convert the sentence into a PowerShell command
3. Ollama returns a one-line command like this : Get-PhysicalDisk | Select-Object -Property FriendlyName, @{Name='SizeInMB';Expression={$_.Size / 1MB}} 
4. The application extracts the PowerShell command and executes it
5. The output is displayed in the console

## Example

Input:
```
wizard list all process which has processName like ja
```

The application will:
1. Query Ollama with: "do this PowerShell request only with one line powershell-command: list all process which has processName like ja in this format : {"command":"the_command","query": "original_query"}"
2. Ollama might return: `{"command":"Get-Process | Where-Object {$_.ProcessName -like '*ja*'}","query":"list all process which has processName like ja"}`
3. Execute: `Get-Process | Where-Object {$_.ProcessName -like '*ja*'}`

## Making it a PowerShell Command

To use `wizard` as a PowerShell command, you can:

1. **Add to PATH**: Add the build output directory to your system PATH
2. **Create an alias**: Add this to your PowerShell profile:
   ```powershell
   Set-Alias wizard "C:\path\to\wizard.exe"
   ```
3. **Install globally**: Copy `wizard.exe` to a directory in your PATH

## Troubleshooting

- **"Failed to connect to Ollama"**: Make sure Ollama is running. You can test by visiting `http://localhost:11434` in your browser
- **Model not found**: Make sure you have the specified model installed. Run `ollama list` to see installed models
- **Command execution fails**: The generated PowerShell command might be incorrect. Check the output for error messages
