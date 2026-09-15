param([string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$assemblies = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Security.dll','System.Xaml.dll','System.Xml.Linq.dll','System.Xml.dll')
foreach ($assembly in @('UIAutomationClient','UIAutomationTypes','WindowsBase','System.Speech','PresentationCore','PresentationFramework')) {
    $found = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly') -Recurse -Filter "$assembly.dll" | Select-Object -First 1
    if (-not $found) { throw "Missing assembly: $assembly" }
    $assemblies += $found.FullName
}
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output',"/out:$OutputDirectory\LinguaSelect.exe")
$arguments += $assemblies | ForEach-Object { "/reference:$_" }
$arguments += Join-Path $PSScriptRoot 'LinguaSelect.cs'
$arguments += Join-Path $PSScriptRoot 'GlassWindow.cs'
$arguments += Join-Path $PSScriptRoot 'ServicePresets.cs'
$arguments += Join-Path $PSScriptRoot 'Themes.cs'
$arguments += "/resource:$PSScriptRoot\themes.svg,LinguaSelect.themes.svg"
$arguments += "/resource:$PSScriptRoot\Card.xaml,LinguaSelect.Card.xaml"
$arguments += "/resource:$PSScriptRoot\icons.svg,LinguaSelect.icons.svg"
$arguments += "/win32manifest:$PSScriptRoot\app.manifest"
$arguments += "/win32icon:$PSScriptRoot\assets\app.ico"
$arguments += "/resource:$PSScriptRoot\assets\logo.png,LinguaSelect.logo.png"
$arguments += "/resource:$PSScriptRoot\assets\app.ico,LinguaSelect.app.ico"
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output "Built: $OutputDirectory\LinguaSelect.exe"
