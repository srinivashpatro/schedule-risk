# Serves the browser app from this folder at http://127.0.0.1:<port>/ so it runs with no internet connection.
# Browsers will not start a WebAssembly app from file://, so a tiny local server is needed; this one uses only
# Windows PowerShell (no install). It listens on the loopback address only: nothing leaves this computer.
param([int]$Port = 5180)

$root = Join-Path $PSScriptRoot 'wwwroot'
$types = @{
    '.html' = 'text/html; charset=utf-8'; '.js' = 'text/javascript'; '.mjs' = 'text/javascript'
    '.css' = 'text/css'; '.json' = 'application/json'; '.wasm' = 'application/wasm'
    '.dll' = 'application/octet-stream'; '.pdb' = 'application/octet-stream'; '.dat' = 'application/octet-stream'
    '.blat' = 'application/octet-stream'; '.woff2' = 'font/woff2'; '.ttf' = 'font/ttf'; '.svg' = 'image/svg+xml'
    '.png' = 'image/png'; '.ico' = 'image/x-icon'; '.xer' = 'text/plain'; '.txt' = 'text/plain'
}

$listener = New-Object System.Net.HttpListener
for ($i = 0; $i -lt 20; $i++) {
    $listener.Prefixes.Clear()
    $listener.Prefixes.Add("http://127.0.0.1:$($Port + $i)/")
    try { $listener.Start(); $Port += $i; break } catch { }
}
if (-not $listener.IsListening) { Write-Host 'Could not open a local port.'; exit 1 }

$url = "http://127.0.0.1:$Port/"
Write-Host "Project Risk Analysis is running offline at $url"
Write-Host 'Keep this window open while you use the app; close it to stop.'
Start-Process $url

$rootFull = [System.IO.Path]::GetFullPath($root)
while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    try {
        $rel = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath).TrimStart('/')
        $path = [System.IO.Path]::GetFullPath((Join-Path $rootFull $rel))
        if (-not $path.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { $path = $null }
        elseif (-not [System.IO.File]::Exists($path)) {
            # App routes such as /results have no file of their own: hand back the app page.
            if ([System.IO.Path]::GetExtension($path) -eq '' -or $rel -eq '') { $path = Join-Path $rootFull 'index.html' }
            else { $path = $null }
        }
        if ($path) {
            $bytes = [System.IO.File]::ReadAllBytes($path)
            $type = $types[[System.IO.Path]::GetExtension($path).ToLowerInvariant()]
            $ctx.Response.ContentType = if ($type) { $type } else { 'application/octet-stream' }
            $ctx.Response.Headers['Cache-Control'] = 'no-cache'
            $ctx.Response.ContentLength64 = $bytes.Length
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        } else { $ctx.Response.StatusCode = 404 }
    } catch { $ctx.Response.StatusCode = 500 }
    finally { $ctx.Response.Close() }
}
