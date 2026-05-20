param(
    [string]$Model = 'qwen3.6',
    [string]$BaseUrl = 'http://localhost:11434',
    [string]$Command = 'dotnet build Phantom.Workspaces.slnx',
    [switch]$ExecuteForReal,
    [bool]$TracePayloads = $true,
    [string]$LogPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$chatEndpoint = "$($BaseUrl.TrimEnd('/'))/api/chat"
$repoRoot = Split-Path -Parent $PSScriptRoot
$script:transcriptStarted = $false

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $PSScriptRoot ("invoke-ollama-execute-command-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
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

function Invoke-OllamaChatStream {
    param(
        [string]$Endpoint,
        [hashtable]$Body,
        [string]$RequestName = 'chat'
    )

    $json = $Body | ConvertTo-Json -Depth 100 -Compress
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $client = [System.Net.Http.HttpClient]::new($handler)

    try {
        if ($TracePayloads) {
            Write-Host "===== OUTBOUND ($RequestName) ====="
            Write-Host ($Body | ConvertTo-Json -Depth 100)
            Write-Host "===== END OUTBOUND ($RequestName) ====="
        }

        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $Endpoint)
        $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')

        $response = $client.SendAsync(
            $request,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
        ).GetAwaiter().GetResult()

        if (-not $response.IsSuccessStatusCode) {
            $bodyText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            throw "Ollama request failed: HTTP $($response.StatusCode) - $bodyText"
        }

        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $reader = [System.IO.StreamReader]::new($stream)

        $toolCalls = @()
        $lastMessage = $null

        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            if ($TracePayloads) {
                Write-Host "< $line"
            }

            $chunk = $line | ConvertFrom-Json -Depth 100

            $message = Get-OptionalPropertyValue -Object $chunk -Name 'message'
            if ($null -ne $message) {
                $lastMessage = $message

                $messageContent = Get-OptionalPropertyValue -Object $message -Name 'content' -Default ''
                if (-not [string]::IsNullOrEmpty($messageContent)) {
                    Write-Host -NoNewline $messageContent
                }

                $messageToolCalls = Get-OptionalPropertyValue -Object $message -Name 'tool_calls'
                if ($null -ne $messageToolCalls) {
                    $toolCalls += @($messageToolCalls)
                }
            }

            $isDone = Get-OptionalPropertyValue -Object $chunk -Name 'done' -Default $false
            if ($isDone -eq $true) {
                break
            }
        }

        $lastContent = Get-OptionalPropertyValue -Object $lastMessage -Name 'content' -Default ''
        if (-not [string]::IsNullOrEmpty($lastContent)) {
            Write-Host
        }

        return @{
            ToolCalls   = $toolCalls
            LastMessage = $lastMessage
        }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

$initialMessages = @(
    @{
        role    = 'system'
        content = 'You can execute commands via one MCP-style tool: execute_command. Call the tool when asked to run a command.'
    },
    @{
        role    = 'user'
        content = "Use execute_command to run this command exactly: $Command"
    }
)

$tools = @(
    @{
        type     = 'function'
        'function' = @{
            name        = 'execute_command'
            description = 'Execute a shell command and return stdout/stderr and exit code.'
            parameters  = @{
                type       = 'object'
                properties = @{
                    command       = @{ type = 'string' }
                    cwd           = @{ type = 'string' }
                    stream_output = @{ type = 'boolean' }
                }
                required   = @('command')
            }
        }
    }
)

Write-Host "Requesting tool call from model '$Model'..."

$firstResponse = Invoke-OllamaChatStream -Endpoint $chatEndpoint -Body @{
    model    = $Model
    messages = $initialMessages
    tools    = $tools
    stream   = $true
} -RequestName 'initial-tool-request'

$toolCall = $null
foreach ($candidate in @($firstResponse.ToolCalls)) {
    $candidateFunction = Get-OptionalPropertyValue -Object $candidate -Name 'function'
    $candidateFunctionName = Get-OptionalPropertyValue -Object $candidateFunction -Name 'name'
    if ($candidateFunctionName -eq 'execute_command') {
        $toolCall = $candidate
        break
    }
}

if ($null -eq $toolCall) {
    throw "Model did not return an execute_command tool call."
}

$toolArgs = @{}
$toolFunction = Get-OptionalPropertyValue -Object $toolCall -Name 'function'
$toolArguments = Get-OptionalPropertyValue -Object $toolFunction -Name 'arguments'
if ($toolArguments -is [string]) {
    if (-not [string]::IsNullOrWhiteSpace($toolArguments)) {
        $toolArgs = ($toolArguments | ConvertFrom-Json -Depth 100 -AsHashtable)
    }
}
elseif ($null -ne $toolArguments) {
    $toolArgs = @{}
    foreach ($prop in $toolArguments.PSObject.Properties) {
        $toolArgs[$prop.Name] = $prop.Value
    }
}

$resolvedCommand = if ($toolArgs.ContainsKey('command')) { [string]$toolArgs['command'] } else { $Command }
$resolvedCwd = if ($toolArgs.ContainsKey('cwd')) { [string]$toolArgs['cwd'] } else { $repoRoot }

if ($ExecuteForReal) {
    Write-Host "Executing command for real:"
    Write-Host "  cwd: $resolvedCwd"
    Write-Host "  cmd: $resolvedCommand"
    Write-Host "----- command output -----"

    $outputLines = [System.Collections.Generic.List[string]]::new()
    Push-Location $resolvedCwd
    try {
        & powershell -NoLogo -NoProfile -Command $resolvedCommand 2>&1 | ForEach-Object {
            $line = $_.ToString()
            $outputLines.Add($line)
            Write-Host $line
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    $commandResult = @{
        ExitCode = $exitCode
        Output   = ($outputLines -join [Environment]::NewLine)
        Simulated = $false
    }

    Write-Host "----- end command output (exit=$($commandResult.ExitCode)) -----"
}
else {
    Write-Host "Not executing command locally. Returning simulated tool data to Ollama."
    $commandResult = @{
        ExitCode = 0
        Output   = @"
[simulated] Running: $resolvedCommand
[simulated] Working directory: $resolvedCwd
  Determining projects to restore...
  Restored Phantom.Workspaces.slnx.
  Build succeeded.
  0 Warning(s)
  0 Error(s)
Time Elapsed 00:00:03.42
"@
        Simulated = $true
    }
}

$toolResultPayload = @{
    command  = $resolvedCommand
    cwd      = $resolvedCwd
    exitCode = $commandResult.ExitCode
    output   = $commandResult.Output
    simulated = $commandResult.Simulated
} | ConvertTo-Json -Depth 20 -Compress

$assistantToolMessage = if ($firstResponse.LastMessage) {
    $assistantContent = Get-OptionalPropertyValue -Object $firstResponse.LastMessage -Name 'content' -Default ''
    @{
        role       = 'assistant'
        content    = $assistantContent
        tool_calls = @($toolCall)
    }
}
else {
    @{
        role       = 'assistant'
        content    = ''
        tool_calls = @($toolCall)
    }
}

$toolResponseMessage = @{
    role      = 'tool'
    tool_name = 'execute_command'
    content   = $toolResultPayload
}

Write-Host "Sending tool result back to model and streaming final response..."

[void](Invoke-OllamaChatStream -Endpoint $chatEndpoint -Body @{
    model    = $Model
    messages = @($initialMessages + @($assistantToolMessage, $toolResponseMessage))
    tools    = $tools
    stream   = $true
} -RequestName 'tool-result-followup')

if ($script:transcriptStarted) {
    Stop-Transcript | Out-Null
    $script:transcriptStarted = $false
}
