BeforeAll {
    $Script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $Script:InstallScript = Join-Path $Script:RepoRoot 'install.ps1'
    . $Script:InstallScript

    # Real published v0.0.2 win-x64 digest (see issue #1288).
    $Script:SampleDigest = 'ad343928788bfb4bb40eb5f49940e650f476cd6536458c96325b82b86b966cf9'
    $Script:SampleAssetName = 'Phantom.Workspaces-0.0.2-win-x64.zip'
}

Describe 'Get-Sha256DigestFromChecksumContent' {
    It 'ChecksumParser_SplitFormat_ExtractsDigest' {
        $input = "$Script:SampleDigest  $Script:SampleAssetName"
        Get-Sha256DigestFromChecksumContent -Content $input | Should -Be $Script:SampleDigest
    }

    It 'ChecksumParser_BsdBinaryFormat_ExtractsDigest' {
        $input = "$Script:SampleDigest *$Script:SampleAssetName"
        Get-Sha256DigestFromChecksumContent -Content $input | Should -Be $Script:SampleDigest
    }

    It 'ChecksumParser_HashOnly_ExtractsDigest' {
        Get-Sha256DigestFromChecksumContent -Content $Script:SampleDigest | Should -Be $Script:SampleDigest
    }

    It 'ChecksumParser_HashWithCrlf_ExtractsDigest' {
        $input = "$Script:SampleDigest  file`r`n"
        Get-Sha256DigestFromChecksumContent -Content $input | Should -Be $Script:SampleDigest
    }

    It 'ChecksumParser_HashWithBom_ExtractsDigest' {
        $bom = [char]0xFEFF
        $input = "$bom$Script:SampleDigest  file"
        Get-Sha256DigestFromChecksumContent -Content $input | Should -Be $Script:SampleDigest
    }

    It 'ChecksumParser_ByteArrayContent_ExtractsDigest' {
        # Simulates Invoke-WebRequest against Content-Type: application/octet-stream
        # (the actual bug from issue #1288 — .Content is [byte[]] not [string]).
        $text = "$Script:SampleDigest  $Script:SampleAssetName"
        [byte[]] $bytes = [System.Text.Encoding]::ASCII.GetBytes($text)
        Get-Sha256DigestFromChecksumContent -Content $bytes | Should -Be $Script:SampleDigest
    }

    It 'ChecksumParser_UppercaseHex_ReturnsLowercase' {
        $upper = $Script:SampleDigest.ToUpperInvariant()
        $input = "$upper  file"
        Get-Sha256DigestFromChecksumContent -Content $input | Should -Be $Script:SampleDigest
    }

    It 'ChecksumParser_NoDigest_Throws' {
        { Get-Sha256DigestFromChecksumContent -Content 'not a hash' -SourceName 'test.sha256' } |
            Should -Throw '*Could not locate SHA256 digest in test.sha256*'
    }

    It 'ChecksumParser_ShortHex_Throws' {
        # 63 hex chars — must not match.
        $short = 'a' * 63
        { Get-Sha256DigestFromChecksumContent -Content $short -SourceName 'x' } | Should -Throw
    }
}
