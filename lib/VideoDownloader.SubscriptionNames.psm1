#requires -Version 7.0
# Windows leaf names are validated identically even on non-Windows test hosts.
$ErrorActionPreference='Stop'

function Get-VdSubscriptionNameLeaf([AllowNull()][AllowEmptyString()][string]$Name) {
    $text=[Text.StringBuilder]::new()
    for($index=0;$index -lt $Name.Length;$index++) {
        $character=$Name[$index]
        if([char]::IsHighSurrogate($character)) {
            if($index+1 -lt $Name.Length -and [char]::IsLowSurrogate($Name[$index+1])) {
                [void]$text.Append($character);$index++;[void]$text.Append($Name[$index])
            } else {[void]$text.Append('_')}
        } elseif([char]::IsLowSurrogate($character) -or [char]::IsControl($character) -or '<>:"/\|?*'.Contains($character)) {
            [void]$text.Append('_')
        } else {[void]$text.Append($character)}
    }
    $leaf=[regex]::Replace($text.ToString(),'\s+',' ').Trim([char[]]@(' ','.'))
    $reserved='^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:[ .]|$)'
    if([regex]::IsMatch($leaf,$reserved,[Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {$leaf='_'+$leaf}
    if($leaf.Length -gt 80) {
        $length=if([char]::IsHighSurrogate($leaf[79])){79}else{80}
        $leaf=$leaf.Substring(0,$length).TrimEnd([char[]]@(' ','.'))
    }
    return $leaf
}

function ConvertTo-VdSubscriptionName {
    param([AllowNull()][AllowEmptyString()][string]$Name,[AllowEmptyString()][string]$Fallback='Подписка')
    $leaf=Get-VdSubscriptionNameLeaf $Name
    if(-not $leaf){$leaf=Get-VdSubscriptionNameLeaf $Fallback}
    if(-not $leaf){$leaf='Подписка'}
    return $leaf
}

function Test-VdSubscriptionName {
    param([AllowNull()][AllowEmptyString()][string]$Name)
    return -not [string]::IsNullOrEmpty($Name) -and [string]::Equals($Name,(Get-VdSubscriptionNameLeaf $Name),[StringComparison]::Ordinal)
}

function Resolve-VdSubscriptionOutput {
    param([Parameter(Mandatory)][string]$BaseDirectory,[AllowEmptyString()][string]$Name='')
    if([string]::IsNullOrWhiteSpace($BaseDirectory)){throw 'Укажите папку загрузок.'}
    $directory=[IO.Path]::GetFullPath($BaseDirectory)
    if(-not $Name){return $directory}
    if(-not (Test-VdSubscriptionName $Name)){throw 'Краткое имя подписки должно быть безопасным именем одной папки.'}
    return [IO.Path]::Combine($directory,$Name)
}

Export-ModuleMember -Function ConvertTo-VdSubscriptionName,Test-VdSubscriptionName,Resolve-VdSubscriptionOutput
