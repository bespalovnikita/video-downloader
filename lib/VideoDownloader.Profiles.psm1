#requires -Version 7.0
# UI-localized profile captions, stable IDs shared with C# session.json.
$script:ProfileTitles = [ordered]@{
    'Universal'         = 'Универсальный'
    '4K archive'        = 'Архив 4K'
    'MP4 compatibility' = 'Совместимый MP4'
    'Music MP3'         = 'Музыка MP3'
    'Subtitles archive' = 'Архив с субтитрами'
}
function Get-VdProfileCaptions { return @($script:ProfileTitles.Values) }
function Get-VdProfileCaption {
    param([string]$Profile)
    foreach ($id in $script:ProfileTitles.Keys) {
        if ([string]::Equals($id,$Profile,[StringComparison]::Ordinal) -or
            [string]::Equals([string]$script:ProfileTitles[$id],$Profile,[StringComparison]::Ordinal)) {
            return [string]$script:ProfileTitles[$id]
        }
    }
    return [string]$script:ProfileTitles['Universal']
}
function Get-VdProfileId {
    param([string]$Profile)
    foreach ($id in $script:ProfileTitles.Keys) {
        if ([string]::Equals($id,$Profile,[StringComparison]::Ordinal) -or
            [string]::Equals([string]$script:ProfileTitles[$id],$Profile,[StringComparison]::Ordinal)) {
            return [string]$id
        }
    }
    return 'Universal'
}
Export-ModuleMember -Function Get-VdProfileCaptions,Get-VdProfileCaption,Get-VdProfileId
