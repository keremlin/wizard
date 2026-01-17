# PowerShell script to download and install .NET 8.0 SDK using official installer script
Write-Host "Installing .NET 8.0 SDK using official installer..." -ForegroundColor Green

# Download the official dotnet-install script
$installScriptUrl = "https://dot.net/v1/dotnet-install.ps1"
$installScriptPath = "$env:TEMP\dotnet-install.ps1"

# Install to user directory (no admin required)
$installDir = "$env:USERPROFILE\.dotnet"

try {
    Write-Host "Downloading dotnet-install script..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $installScriptUrl -OutFile $installScriptPath -UseBasicParsing
    
    Write-Host "Running installer for .NET 8.0 SDK..." -ForegroundColor Yellow
    Write-Host "Installing to: $installDir" -ForegroundColor Yellow
    Write-Host "This may take a few minutes..." -ForegroundColor Yellow
    
    # Run the installer script for .NET 8.0 SDK
    & $installScriptPath -Channel 8.0 -InstallDir $installDir
    
    # Add to PATH if not already there
    $currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
    
    if ($currentPath -notlike "*$installDir*") {
        Write-Host "Adding dotnet to user PATH..." -ForegroundColor Yellow
        [Environment]::SetEnvironmentVariable("Path", "$currentPath;$installDir", "User")
        $env:Path += ";$installDir"
    }
    
    Write-Host ""
    Write-Host "Installation completed!" -ForegroundColor Green
    Write-Host "Installation directory: $installDir" -ForegroundColor Green
    Write-Host ""
    Write-Host "IMPORTANT: Please close and reopen your terminal/PowerShell window for PATH changes to take effect." -ForegroundColor Yellow
    Write-Host "Then run: dotnet --version" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "If dotnet is still not found after reopening, you can manually add to PATH:" -ForegroundColor Yellow
    Write-Host "  $installDir" -ForegroundColor Cyan
    
    # Clean up
    Remove-Item $installScriptPath -ErrorAction SilentlyContinue
    
} catch {
    Write-Host "Error occurred: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "Alternative: Please download and install manually from:" -ForegroundColor Yellow
    Write-Host "https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Or run this script as Administrator to install system-wide." -ForegroundColor Yellow
    exit 1
}
