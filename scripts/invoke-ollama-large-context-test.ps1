param(
    [string]$Model = 'qwen3.6',
    [string]$BaseUrl = 'http://localhost:11434',
    [int]$ProjectedTokenTarget = 1100000,
    [double]$AvgCharsPerToken = 4.0,
    [bool]$DisableThinking = $true,
    [string]$LogPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$endpoint = "$($BaseUrl.TrimEnd('/'))/api/generate"
$script:transcriptStarted = $false

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $PSScriptRoot ("invoke-ollama-large-context-test-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
}

trap {
    if ($script:transcriptStarted) {
        Stop-Transcript | Out-Null
        $script:transcriptStarted = $false
    }

    throw
}

Start-Transcript -Path $LogPath -Force | Out-Null
$script:transcriptStarted = $true

Write-Host "Logging output to: $LogPath"
Write-Host "Generating prompt for projected token target: $ProjectedTokenTarget"

$requiredChars = [Math]::Ceiling($ProjectedTokenTarget * $AvgCharsPerToken)
$chunk = "token "
$sb = [System.Text.StringBuilder]::new([int]$requiredChars + 1024)

while ($sb.Length -lt $requiredChars) {
    [void]$sb.Append($chunk)
}

$prompt = $sb.ToString()
$projectedTokens = [Math]::Floor($prompt.Length / $AvgCharsPerToken)

Write-Host "Prompt length (chars): $($prompt.Length)"
Write-Host "Projected tokens: $projectedTokens"
Write-Host "Sending request to: $endpoint"

$requestBody = @{
    model = $Model
    prompt = $prompt
    stream = $false
    options = @{
        num_predict = 1
        temperature = 0
    }
}

if ($DisableThinking) {
    $requestBody['think'] = $false
}

$json = $requestBody | ConvertTo-Json -Depth 20 -Compress
$handler = [System.Net.Http.HttpClientHandler]::new()
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan

try {
    Write-Host "===== OUTBOUND (summary) ====="
    Write-Host ("Model: {0}" -f $Model)
    Write-Host ("Prompt chars: {0}" -f $prompt.Length)
    Write-Host ("Projected tokens: {0}" -f $projectedTokens)
    Write-Host ("JSON bytes: {0}" -f ([System.Text.Encoding]::UTF8.GetByteCount($json)))
    Write-Host "===== END OUTBOUND (summary) ====="

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $endpoint)
    $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')

    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    $responseText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

    Write-Host "HTTP Status: $([int]$response.StatusCode) $($response.StatusCode)"
    Write-Host "===== RESPONSE BODY ====="
    Write-Host $responseText
    Write-Host "===== END RESPONSE BODY ====="

    if (-not $response.IsSuccessStatusCode) {
        Write-Host "Result: Request failed as expected for oversized context."
    }
    else {
        Write-Host "Result: Request succeeded (model/runtime may have accepted or truncated input)."
    }
}
finally {
    $client.Dispose()
    $handler.Dispose()

    if ($script:transcriptStarted) {
        Stop-Transcript | Out-Null
        $script:transcriptStarted = $false
    }
}
