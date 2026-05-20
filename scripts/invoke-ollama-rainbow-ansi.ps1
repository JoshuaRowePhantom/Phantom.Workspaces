param(
    [string]$Model = 'qwen3.6',
    [string]$BaseUrl = 'http://localhost:11434',
    [switch]$TracePayloads,
    [bool]$DisableThinking = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$generateEndpoint = "$($BaseUrl.TrimEnd('/'))/api/generate"

function Get-OptionalPropertyValue {
    param(
        [Parameter(Mandatory = $false)]
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $false)]
        [object]$Default = $null
    )

    if ($null -eq $Object) {
        return $Default
    }

    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return $Object[$Name]
        }

        return $Default
    }

    $prop = $Object.PSObject.Properties[$Name]
    if ($null -ne $prop) {
        return $prop.Value
    }

    return $Default
}

function Get-ConsoleColorFromAnsiCode {
    param([int]$Code)

    switch ($Code) {
        30 { return [ConsoleColor]::Black }
        31 { return [ConsoleColor]::Red }
        32 { return [ConsoleColor]::Green }
        33 { return [ConsoleColor]::Yellow }
        34 { return [ConsoleColor]::Blue }
        35 { return [ConsoleColor]::Magenta }
        36 { return [ConsoleColor]::Cyan }
        37 { return [ConsoleColor]::White }
        90 { return [ConsoleColor]::DarkGray }
        91 { return [ConsoleColor]::Red }
        92 { return [ConsoleColor]::Green }
        93 { return [ConsoleColor]::Yellow }
        94 { return [ConsoleColor]::Blue }
        95 { return [ConsoleColor]::Magenta }
        96 { return [ConsoleColor]::Cyan }
        97 { return [ConsoleColor]::White }
        default { return $null }
    }
}

function Write-AnsiColorizedText {
    param(
        [string]$Text,
        [ref]$CurrentForeground
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return
    }

    $escape = [char]27
    $pattern = [regex]"$([regex]::Escape($escape))\[(?<codes>[0-9;]*)m"
    $position = 0
    $matches = $pattern.Matches($Text)

    foreach ($match in $matches) {
        $segment = $Text.Substring($position, $match.Index - $position)
        if ($segment.Length -gt 0) {
            if ($null -eq $CurrentForeground.Value) {
                Write-Host -NoNewline $segment
            }
            else {
                Write-Host -NoNewline $segment -ForegroundColor $CurrentForeground.Value
            }
        }

        $codesValue = $match.Groups['codes'].Value
        if ([string]::IsNullOrEmpty($codesValue)) {
            $CurrentForeground.Value = $null
        }
        else {
            foreach ($codeText in $codesValue.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)) {
                $code = 0
                if (-not [int]::TryParse($codeText, [ref]$code)) {
                    continue
                }

                if ($code -eq 0) {
                    $CurrentForeground.Value = $null
                    continue
                }

                $mapped = Get-ConsoleColorFromAnsiCode -Code $code
                if ($null -ne $mapped) {
                    $CurrentForeground.Value = $mapped
                }
            }
        }

        $position = $match.Index + $match.Length
    }

    $tail = $Text.Substring($position)
    if ($tail.Length -gt 0) {
        if ($null -eq $CurrentForeground.Value) {
            Write-Host -NoNewline $tail
        }
        else {
            Write-Host -NoNewline $tail -ForegroundColor $CurrentForeground.Value
        }
    }
}

$prompt = @'
Output only ANSI-colored text (no markdown, no explanation) that prints the rainbow colors in order:
Red
Orange
Yellow
Green
Blue
Indigo
Violet

Use ANSI SGR foreground codes and reset formatting after each line. Use json string escaping to represent each escape sequence.
'@

$requestBody = @{
    model   = $Model
    prompt  = $prompt
    stream  = $true
    options = @{
        temperature = 0
    }
}

if ($DisableThinking) {
    $requestBody['think'] = $false
}

if ($TracePayloads) {
    Write-Host "===== OUTBOUND (rainbow-request) ====="
    Write-Host ($requestBody | ConvertTo-Json -Depth 20)
    Write-Host "===== END OUTBOUND (rainbow-request) ====="
}

$json = $requestBody | ConvertTo-Json -Depth 20 -Compress
$handler = [System.Net.Http.HttpClientHandler]::new()
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan

try {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $generateEndpoint)
    $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')

    $response = $client.SendAsync(
        $request,
        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
    ).GetAwaiter().GetResult()

    if (-not $response.IsSuccessStatusCode) {
        $errorBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        throw "Ollama request failed: HTTP $($response.StatusCode) - $errorBody"
    }

    $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $reader = [System.IO.StreamReader]::new($stream)

    $currentColor = $null

    while (-not $reader.EndOfStream) {
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($TracePayloads) {
            Write-Host "< $line"
        }

        $chunk = $line | ConvertFrom-Json -Depth 100
        $textChunk = Get-OptionalPropertyValue -Object $chunk -Name 'response' -Default ''
        Write-AnsiColorizedText -Text $textChunk -CurrentForeground ([ref]$currentColor)

        $isDone = Get-OptionalPropertyValue -Object $chunk -Name 'done' -Default $false
        if ($isDone -eq $true) {
            break
        }
    }

    if ($null -ne $currentColor) {
        Write-Host -NoNewline "$([char]27)[0m"
    }

    Write-Host
}
finally {
    $client.Dispose()
    $handler.Dispose()
}
