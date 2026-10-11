param([switch]$BuildOnly)
#requires -version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-OutputName { return 'DL1TaskAssistant.exe' }

try {
    if (-not [Environment]::Is64BitProcess) {
        throw '64-bit Windows PowerShell is required. Use the included BAT launcher.'
    }

    $sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Source') -File -Filter '*.cs' | Sort-Object FullName)
    if ($sourceFiles.Count -eq 0) {
        throw 'No C# source files were found.'
    }

    # Keep every C# file as an independent compilation unit. Concatenating files into
    # one giant string breaks C# because later using-directives would appear after a
    # namespace declaration. Read explicitly as UTF-8 so Chinese source/file names are stable.
    $utf8 = New-Object System.Text.UTF8Encoding($true)
    $sources = New-Object 'System.Collections.Generic.List[string]'
    foreach ($file in $sourceFiles) {
        [void]$sources.Add([System.IO.File]::ReadAllText($file.FullName, $utf8))
    }

    Add-Type -AssemblyName Microsoft.CSharp
    $provider = New-Object Microsoft.CSharp.CSharpCodeProvider
    $cp = New-Object System.CodeDom.Compiler.CompilerParameters
    foreach ($r in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll')) {
        [void]$cp.ReferencedAssemblies.Add($r)
    }
    $cp.GenerateExecutable = $true
    $cp.GenerateInMemory = -not [bool]$BuildOnly
    $cp.IncludeDebugInformation = $false
    $cp.CompilerOptions = '/target:winexe /platform:x64 /optimize+ /main:DL1Assistant.Program'
    if ($BuildOnly) {
        $cp.OutputAssembly = Join-Path $PSScriptRoot (Get-OutputName)
    }

    $result = $provider.CompileAssemblyFromSource($cp, [string[]]$sources.ToArray())
    if ($result.Errors.HasErrors) {
        $messages = New-Object 'System.Collections.Generic.List[string]'
        foreach ($err in $result.Errors) {
            [void]$messages.Add(('{0}({1},{2}): {3} {4}' -f $err.FileName,$err.Line,$err.Column,$err.ErrorNumber,$err.ErrorText))
        }
        throw ('Build failed:' + [Environment]::NewLine + ($messages -join [Environment]::NewLine))
    }

    if ($BuildOnly) {
        Write-Host ('BUILD OK: ' + $cp.OutputAssembly)
        exit 0
    }

    # Invoke through reflection instead of a PowerShell type literal; the assembly was
    # compiled at runtime and is already loaded in this AppDomain.
    $programType = $result.CompiledAssembly.GetType('DL1Assistant.Program', $true)
    $flags = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static
    $runMethod = $programType.GetMethod('Run', $flags)
    if ($null -eq $runMethod) { throw 'DL1Assistant.Program.Run was not found.' }
    [void]$runMethod.Invoke($null, @($PSScriptRoot))
}
catch {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
    [System.Windows.Forms.MessageBox]::Show(
        $_.Exception.ToString(),
        '消逝的光芒任务助手启动失败',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error
    ) | Out-Null
    Write-Host $_.Exception.ToString()
    exit 1
}
