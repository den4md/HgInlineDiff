param(
    [string]$VisualStudioPath,
    [string]$MercurialPath = 'C:\Program Files\TortoiseHg\hg.exe'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$VisualStudioPath) {
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    $VisualStudioPath = & $vswhere -latest -products '*' -property installationPath
}
$testWork = Join-Path $projectRoot 'work'
$null = New-Item -ItemType Directory -Force -Path $testWork
$testExecutable = Join-Path $testWork 'HgVsSmokeTests.exe'
$editorPath = Join-Path $VisualStudioPath 'Common7\IDE\CommonExtensions\Microsoft\Editor'
$compiler = Join-Path $VisualStudioPath 'MSBuild\Current\Bin\Roslyn\csc.exe'
& $compiler /nologo /target:exe /langversion:latest /nullable:enable "/out:$testExecutable" `
    '/reference:C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\Facades\netstandard.dll' `
    "/reference:$(Join-Path $editorPath 'Microsoft.VisualStudio.Text.Data.dll')" `
    (Join-Path $projectRoot 'HgVS_2026\MercurialRepository.cs') `
    (Join-Path $projectRoot 'HgVS_2026\LineDiff.cs') `
    (Join-Path $PSScriptRoot 'SmokeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Smoke-test compilation failed.' }
& $testExecutable $VisualStudioPath $MercurialPath $testWork
if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
