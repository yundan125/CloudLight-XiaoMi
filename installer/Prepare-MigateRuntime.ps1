param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,
    [string] $BuildPython
)

$ErrorActionPreference = 'Stop'
$publishPath = [System.IO.Path]::GetFullPath($PublishDirectory)
if (-not (Test-Path -LiteralPath $publishPath -PathType Container)) {
    throw "Publish directory does not exist: $publishPath"
}

$runtimePath = Join-Path $publishPath 'migate-python'
if (Test-Path -LiteralPath $runtimePath) {
    $resolvedRuntime = [System.IO.Path]::GetFullPath($runtimePath)
    if (-not $resolvedRuntime.StartsWith($publishPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace runtime outside publish directory: $resolvedRuntime"
    }
    Remove-Item -LiteralPath $resolvedRuntime -Recurse -Force
}
New-Item -ItemType Directory -Path $runtimePath | Out-Null

$download = Join-Path ([System.IO.Path]::GetTempPath()) 'cloudlight-python-3.14.0-embed-amd64.zip'
$pythonUrl = 'https://mirrors.tuna.tsinghua.edu.cn/python/3.14.0/python-3.14.0-embed-amd64.zip'
try {
    Invoke-WebRequest -Uri $pythonUrl -OutFile $download
}
catch {
    Write-Warning 'TUNA rejected the runtime download; falling back to the official Python distribution.'
    Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue
    Invoke-WebRequest -Uri 'https://www.python.org/ftp/python/3.14.0/python-3.14.0-embed-amd64.zip' -OutFile $download
}
Expand-Archive -LiteralPath $download -DestinationPath $runtimePath -Force

$pthPath = Join-Path $runtimePath 'python314._pth'
@('python314.zip', '.', 'Lib\site-packages', 'import site') | Set-Content -LiteralPath $pthPath -Encoding ascii
New-Item -ItemType Directory -Path (Join-Path $runtimePath 'Lib\site-packages') -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($BuildPython)) {
    $BuildPython = (Get-Command python -ErrorAction Stop).Source
}
$requirements = Join-Path $PSScriptRoot 'requirements-migate-runtime.txt'
& $BuildPython -m pip install --disable-pip-version-check --no-compile `
    --index-url 'https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple' `
    --target (Join-Path $runtimePath 'Lib\site-packages') `
    --requirement $requirements
if ($LASTEXITCODE -ne 0) {
    Write-Warning 'TUNA package installation failed; falling back to official PyPI.'
    & $BuildPython -m pip install --disable-pip-version-check --no-compile `
        --target (Join-Path $runtimePath 'Lib\site-packages') `
        --requirement $requirements
    if ($LASTEXITCODE -ne 0) { throw "pip failed with exit code $LASTEXITCODE" }
}

& (Join-Path $runtimePath 'python.exe') -c "import migate, requests, rich; print('private migate runtime ready')"
if ($LASTEXITCODE -ne 0) { throw "Packaged Python runtime smoke test failed." }
