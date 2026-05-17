param(
    [Parameter(Mandatory = $true)]
    [string] $SchemaFilesPath,

    [Parameter()]
    [string] $OtherFilesPath = ''
)

$ErrorActionPreference = 'Stop'

function Resolve-JsonPointer {
    param(
        [Parameter(Mandatory = $true)]
        $Node,

        [Parameter(Mandatory = $true)]
        [string] $Pointer
    )

    if ($Pointer -eq '' -or $Pointer -eq '#') {
        return $Node
    }

    if (-not $Pointer.StartsWith('#/')) {
        throw "Unsupported JSON pointer '$Pointer'."
    }

    $current = $Node
    foreach ($segment in $Pointer.Substring(2).Split('/')) {
        $decodedSegment = $segment.Replace('~1', '/').Replace('~0', '~')

        if ($current -is [System.Collections.IDictionary]) {
            if (-not $current.Contains($decodedSegment)) {
                throw "Missing JSON pointer segment '$decodedSegment' in '$Pointer'."
            }

            $current = $current[$decodedSegment]
            continue
        }

        if ($current -is [pscustomobject]) {
            $property = $current.PSObject.Properties[$decodedSegment]
            if (-not $property) {
                throw "Missing JSON pointer segment '$decodedSegment' in '$Pointer'."
            }

            $current = $property.Value
            continue
        }

        if ($current -is [System.Collections.IEnumerable] -and -not ($current -is [string])) {
            $index = [int]$decodedSegment
            $current = @($current)[$index]
            continue
        }

        throw "Cannot resolve segment '$decodedSegment' in '$Pointer'."
    }

    return $current
}

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        $Node,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($Node -is [System.Collections.IDictionary]) {
        if ($Node.Contains($Name)) {
            return $Node[$Name]
        }

        return $null
    }

    if ($Node -is [pscustomobject]) {
        $property = $Node.PSObject.Properties[$Name]
        if ($property) {
            return $property.Value
        }

        return $null
    }

    return $null
}

function Test-JsonProperty {
    param(
        [Parameter(Mandatory = $true)]
        $Node,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($Node -is [System.Collections.IDictionary]) {
        return $Node.Contains($Name)
    }

    if ($Node -is [pscustomobject]) {
        return [bool]$Node.PSObject.Properties[$Name]
    }

    return $false
}

function Get-Refs {
    param(
        [Parameter(Mandatory = $true)]
        $Node
    )

    $refs = New-Object System.Collections.Generic.List[string]

    if ($Node -is [System.Collections.IDictionary]) {
        foreach ($property in $Node.GetEnumerator()) {
            if ($property.Key -eq '$ref' -and $property.Value -is [string]) {
                $refs.Add($property.Value)
            }
            else {
                foreach ($ref in Get-Refs -Node $property.Value) {
                    $refs.Add($ref)
                }
            }
        }
    }
    elseif ($Node -is [pscustomobject]) {
        foreach ($property in $Node.PSObject.Properties) {
            if ($property.Name -eq '$ref' -and $property.Value -is [string]) {
                $refs.Add($property.Value)
            }
            else {
                foreach ($ref in Get-Refs -Node $property.Value) {
                    $refs.Add($ref)
                }
            }
        }
    }
    elseif ($Node -is [System.Collections.IEnumerable] -and -not ($Node -is [string])) {
        foreach ($item in $Node) {
            foreach ($ref in Get-Refs -Node $item) {
                $refs.Add($ref)
            }
        }
    }

    return $refs
}

function Resolve-SchemaReferencePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Ref,

        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo] $SchemaFile
    )

    if ($Ref.StartsWith('#')) {
        return $null
    }

    $targetUri = $Ref
    if ($Ref.StartsWith('http://') -or $Ref.StartsWith('https://')) {
        $targetUri = $Ref.Split('#', 2)[0]
        if ($targetUri -notlike 'https://schemas.workspaces.phantom.to/workspaces/data/core/*') {
            return $null
        }

        $targetPath = [System.IO.Path]::GetFileName($targetUri)
        return Join-Path $SchemaFilesPath $targetPath
    }

    $relativePath = $Ref.Split('#', 2)[0]
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        return $null
    }

    return [System.IO.Path]::GetFullPath((Join-Path $SchemaFile.DirectoryName $relativePath))
}

function Get-LatestPackageAssemblyPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageName
    )

    $packageRoot = Join-Path $env:USERPROFILE ".nuget\packages\$PackageName"
    if (-not (Test-Path $packageRoot)) {
        throw "NuGet package '$PackageName' was not found at '$packageRoot'. Run restore first."
    }

    $preferredTfms = @('netstandard2.0', 'net8.0', 'net9.0', 'net10.0')
    $dll = $null

    foreach ($tfm in $preferredTfms) {
        $dll = Get-ChildItem -Path $packageRoot -Recurse -Filter '*.dll' -File |
            Where-Object { $_.FullName -match "\\lib\\$tfm\\" } |
            Select-Object -First 1

        if ($dll) {
            break
        }
    }

    if (-not $dll) {
        throw "No DLL found for NuGet package '$PackageName'."
    }

    return $dll.FullName
}

function Import-JsonSchemaNet {
    $script:JsonPointerAssemblyPath = Get-LatestPackageAssemblyPath -PackageName 'JsonPointer.Net'

    [AppDomain]::CurrentDomain.add_AssemblyResolve({
        param($sender, $eventArgs)

        if ($eventArgs.Name -like 'JsonPointer.Net,*') {
            return [System.Reflection.Assembly]::LoadFrom($script:JsonPointerAssemblyPath)
        }

        return $null
    })

    [System.Reflection.Assembly]::LoadFrom((Get-LatestPackageAssemblyPath -PackageName 'JsonSchema.Net')) | Out-Null
}

function Get-JsonSchemaType {
    $type = [AppDomain]::CurrentDomain.GetAssemblies() |
        ForEach-Object { $_.GetTypes() } |
        Where-Object { $_.FullName -eq 'Json.Schema.JsonSchema' } |
        Select-Object -First 1

    if (-not $type) {
        throw "JsonSchema.Net could not be loaded."
    }

    return $type
}

function ConvertTo-JsonSchema {
    param(
        [Parameter(Mandatory = $true)]
        [string] $JsonText
    )

    $schemaType = Get-JsonSchemaType
    $parseMethods = $schemaType.GetMethods([System.Reflection.BindingFlags] 'Public, Static') |
        Where-Object { $_.Name -in @('FromText', 'Parse') }

    foreach ($method in $parseMethods) {
        $parameters = $method.GetParameters()
        if ($parameters.Count -ne 1) {
            continue
        }

        $parameterType = $parameters[0].ParameterType.FullName
        if ($parameterType -eq 'System.String') {
            return $method.Invoke($null, @($JsonText))
        }
    }

    throw "Could not find a JsonSchema.Net parsing method."
}

Import-JsonSchemaNet

$schemaFiles = Get-ChildItem -Path $SchemaFilesPath -Filter '*.json' -File
$ids = @{}

foreach ($schemaFile in $schemaFiles) {
    $jsonText = Get-Content -LiteralPath $schemaFile.FullName -Raw
    $schemaNode = $jsonText | ConvertFrom-Json -Depth 100

    if (-not ($schemaNode -is [System.Collections.IDictionary] -or $schemaNode -is [pscustomobject])) {
        throw "Schema '$($schemaFile.Name)' must be a JSON object."
    }

    if (-not (Test-JsonProperty -Node $schemaNode -Name '$id')) {
        throw "Schema '$($schemaFile.Name)' is missing a `$id value."
    }

    $schemaId = (Get-JsonPropertyValue -Node $schemaNode -Name '$id').ToString()
    if ($ids.ContainsKey($schemaId)) {
        throw "Duplicate `$id '$schemaId' found in '$($schemaFile.Name)' and '$($ids[$schemaId])'."
    }

    $ids[$schemaId] = $schemaFile.Name

    foreach ($ref in (Get-Refs -Node $schemaNode)) {
        $targetPath = Resolve-SchemaReferencePath -Ref $ref -SchemaFile $schemaFile
        if ($null -ne $targetPath) {
            $fragment = if ($ref.Contains('#')) { "#$($ref.Split('#', 2)[1])" } else { '' }

            if (-not (Test-Path $targetPath)) {
                throw "Reference '$ref' in '$($schemaFile.Name)' points to missing schema file '$([System.IO.Path]::GetFileNameWithoutExtension($targetPath)).json'."
            }

            if ($fragment) {
                $targetJsonText = Get-Content -LiteralPath $targetPath -Raw
                $targetJson = $targetJsonText | ConvertFrom-Json -Depth 100
                Resolve-JsonPointer -Node $targetJson -Pointer $fragment | Out-Null
            }
        }
    }

    # Parse the schema with JsonSchema.Net so the library participates in validation.
    ConvertTo-JsonSchema -JsonText $jsonText | Out-Null
}

if ($OtherFilesPath -and (Test-Path $OtherFilesPath)) {
    $otherFiles = Get-ChildItem -Path $OtherFilesPath -Filter '*.json' -File
    foreach ($otherFile in $otherFiles) {
        $null = Get-Content -LiteralPath $otherFile.FullName -Raw | ConvertFrom-Json -Depth 100
    }
}

Write-Host "Validated $($schemaFiles.Count) schema files in '$SchemaFilesPath'."
