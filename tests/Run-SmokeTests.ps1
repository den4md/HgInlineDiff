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
$roslynPath = Join-Path $VisualStudioPath 'Common7\IDE\CommonExtensions\Microsoft\VBCSharp\LanguageServices'
$compiler = Join-Path $VisualStudioPath 'MSBuild\Current\Bin\Roslyn\csc.exe'
$framework = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2'
$shellRefs = @(
    "$env:USERPROFILE/.nuget/packages/microsoft.visualstudio.interop/17.14.40260/lib/net472/Microsoft.VisualStudio.Interop.dll",
    "$env:USERPROFILE/.nuget/packages/microsoft.visualstudio.shell.framework/17.14.40264/lib/net472/Microsoft.VisualStudio.Shell.Framework.dll",
    "$env:USERPROFILE/.nuget/packages/microsoft.visualstudio.shell.15.0/17.14.40264/lib/net472/Microsoft.VisualStudio.Shell.15.0.dll"
    "$env:USERPROFILE/.nuget/packages/microsoft.visualstudio.threading.only/17.14.15/lib/net472/Microsoft.VisualStudio.Threading.dll"
) | ForEach-Object { "/reference:$_" }
& $compiler /nologo /target:exe /langversion:latest /nullable:enable "/out:$testExecutable" `
    $shellRefs `
    "/reference:$(Join-Path $roslynPath 'Microsoft.CodeAnalysis.dll')" `
    "/reference:$(Join-Path $roslynPath 'Microsoft.CodeAnalysis.Workspaces.dll')" `
    "/reference:$(Join-Path $roslynPath 'Microsoft.CodeAnalysis.EditorFeatures.Text.dll')" `
    "/reference:$(Join-Path $roslynPath 'Microsoft.CodeAnalysis.Features.dll')" `
    "/reference:$(Join-Path $VisualStudioPath 'Common7\IDE\PublicAssemblies\System.Collections.Immutable.dll')" `
    '/reference:C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\Facades\netstandard.dll' `
    "/reference:$(Join-Path $editorPath 'Microsoft.VisualStudio.Text.Data.dll')" `
    "/reference:$(Join-Path $editorPath 'Microsoft.VisualStudio.Text.Logic.dll')" `
    "/reference:$(Join-Path $editorPath 'Microsoft.VisualStudio.Text.UI.dll')" `
    "/reference:$(Join-Path $editorPath 'Microsoft.VisualStudio.Text.UI.Wpf.dll')" `
    "/reference:$(Join-Path $editorPath 'Microsoft.VisualStudio.CoreUtility.dll')" `
    "/reference:$(Join-Path $framework 'System.ComponentModel.Composition.dll')" `
    "/reference:$(Join-Path $framework 'WindowsBase.dll')" `
    "/reference:$(Join-Path $framework 'PresentationCore.dll')" `
    "/reference:$(Join-Path $framework 'PresentationFramework.dll')" `
    "/reference:$(Join-Path $framework 'System.Xaml.dll')" `
    (Join-Path $projectRoot 'HgVS_2026\MercurialRepository.cs') `
    (Join-Path $projectRoot 'HgVS_2026\LineDiff.cs') `
    (Join-Path $projectRoot 'HgVS_2026\DiffUpdates.cs') `
    (Join-Path $projectRoot 'HgVS_2026\MarkerLayout.cs') `
    (Join-Path $projectRoot 'HgVS_2026\MercurialMargin.cs') `
    (Join-Path $projectRoot 'HgVS_2026\MercurialMarginProvider.cs') `
    (Join-Path $projectRoot 'HgVS_2026\ParentTextPopup.cs') `
    (Join-Path $projectRoot 'HgVS_2026\MercurialChangeSet.cs') `
    (Join-Path $projectRoot 'HgVS_2026\MercurialOverviewMargin.cs') `
    (Join-Path $PSScriptRoot 'PopupTestServices.cs') `
    (Join-Path $PSScriptRoot 'PopupHighlightingTests.cs') `
    (Join-Path $PSScriptRoot 'MarginUiTests.cs') `
    (Join-Path $PSScriptRoot 'DiffUpdateTests.cs') `
    (Join-Path $PSScriptRoot 'SmokeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Smoke-test compilation failed.' }
& $testExecutable $VisualStudioPath $MercurialPath $testWork
if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
