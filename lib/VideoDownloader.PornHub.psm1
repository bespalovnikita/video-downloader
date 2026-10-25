#requires -Version 7.0
# Discovery-only adapter. Individual media URLs use the existing download engine.
$ErrorActionPreference='Stop'

function Get-VdPornHubListingUrl([string]$Url) {
    $source=$null
    if (-not [Uri]::TryCreate($Url,[UriKind]::Absolute,[ref]$source) -or $source.Scheme -notin @('http','https') -or
        $source.UserInfo -or -not $source.IsDefaultPort -or
        $source.Host -notmatch '^(?:[a-z0-9-]+\.)?pornhub(?:premium)?\.(?:com|net|org)$') { return $null }
    $match=[regex]::Match($source.AbsolutePath,'^/(?<kind>channels|model|pornstar|users)/(?<slug>[A-Za-z0-9_-]+)(?:/videos(?:/upload)?)?/?$')
    if (-not $match.Success) { return $null }
    $kind=$match.Groups['kind'].Value
    return ('https://'+$source.Host.ToLowerInvariant()+'/'+$kind+'/'+$match.Groups['slug'].Value+'/videos'+$(if($kind -ceq 'channels'){''}else{'/upload'}))
}

function Get-VdPornHubCollectorDirectory {
    $directory=Join-Path (Split-Path -Parent $PSScriptRoot) 'collection-plugins'
    $file=Join-Path $directory 'videodownloader/yt_dlp_plugins/extractor/vd_pornhub.py'
    if (-not [IO.File]::Exists($file)) { throw 'Не найден обработчик каналов Pornhub. Распакуйте полный архив приложения.' }
    return $directory
}

function ConvertFrom-VdPornHubScan {
    param([Parameter(Mandatory)][string]$Json,[Parameter(Mandatory)][string]$SourceUrl,
        [ValidateRange(1,10000)][int]$MaxItems=10000,[ValidateRange(1,500)][int]$MaxPages=500)
    $listing=Get-VdPornHubListingUrl $SourceUrl
    if (-not $listing) { throw 'Некорректный URL канала Pornhub.' }
    if ([Text.Encoding]::UTF8.GetByteCount($Json) -gt 32MB) { throw 'Ответ обработчика Pornhub превышает 32 МиБ.' }
    $root=ConvertFrom-Json -InputObject $Json -AsHashtable -ErrorAction Stop
    $proof=$root.vd_ph_scan
    if ($proof -isnot [Collections.IDictionary] -or $proof.version -isnot [long] -or $proof.version -ne 1 -or
        $proof.complete -isnot [bool] -or -not $proof.complete -or
        $proof.verified_first_page -isnot [bool] -or -not $proof.verified_first_page -or
        $proof.source_url -cne $SourceUrl -or $proof.listing_url -cne $listing) { throw 'Полнота списка публикаций Pornhub не подтверждена.' }
    if ($root.entries -isnot [array] -or $proof.pages -isnot [array] -or $proof.item_count -isnot [long] -or
        $proof.item_count -lt 0 -or $proof.item_count -gt $MaxItems -or $proof.item_count -ne $root.entries.Count -or
        $proof.pages.Count -lt 1 -or $proof.pages.Count -gt $MaxPages) { throw 'Список Pornhub обрезан или превышает предел.' }
    $counts=[int[]]::new($proof.pages.Count)
    for ($index=0;$index -lt $counts.Length;$index++) {
        $page=$proof.pages[$index]
        if ($page.number -isnot [long] -or $page.number -ne ($index+1) -or $page.count -isnot [long] -or
            $page.count -lt 0 -or $page.count -gt $MaxItems -or
            ($page.count -eq 0 -and ($proof.item_count -ne 0 -or $counts.Length -ne 1)) -or
            $page.has_next -isnot [bool] -or $page.has_next -ne ($index+1 -lt $counts.Length)) { throw 'Нарушена последовательность страниц Pornhub.' }
        $counts[$index]=[int]$page.count
    }
    $sourceHost=([Uri]$SourceUrl).Host
    $baseHost=[regex]::Match($sourceHost,'pornhub(?:premium)?\.(?:com|net|org)$',[Text.RegularExpressions.RegexOptions]::IgnoreCase).Value.ToLowerInvariant()
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $items=[Collections.Generic.List[object]]::new()
    foreach ($entry in $root.entries) {
        if ($entry -isnot [Collections.IDictionary] -or $entry._type -cne 'url' -or $entry.ie_key -cne 'PornHub' -or
            $entry.id -isnot [string] -or $entry.id -cnotmatch '^[a-z0-9]+$' -or $entry.url -isnot [string] -or
            $entry.url -cne ('https://www.'+$baseHost+'/view_video.php?viewkey='+$entry.id) -or -not $seen.Add($entry.url) -or
            $entry.vd_page -isnot [long] -or $entry.vd_page -lt 1 -or $entry.vd_page -gt $counts.Length) { throw 'В список Pornhub попал повторный или посторонний материал.' }
        $counts[[int]$entry.vd_page-1]--
        if ($counts[[int]$entry.vd_page-1] -lt 0) { throw 'Количество материалов на странице Pornhub не совпадает.' }
        $title=if ($entry.title -is [string] -and $entry.title) {$entry.title}else{$entry.url}
        $items.Add(@{Url=$entry.url;Title=$title})
    }
    if (@($counts | Where-Object {$_ -ne 0}).Count) { throw 'Количество материалов на страницах Pornhub не совпадает.' }
    $suggestedName=@('channel','uploader','title' | ForEach-Object {$root[$_]} | Where-Object {$_ -is [string] -and -not [string]::IsNullOrWhiteSpace($_)} | Select-Object -First 1)
    return @{Title=$(if($root.title -is [string] -and $root.title){$root.title}else{$SourceUrl});Items=@($items.ToArray());FeedUrl='';Warning='';SuggestedName=$(if($suggestedName.Count){[string]$suggestedName[0]}else{''})}
}

Export-ModuleMember -Function Get-VdPornHubListingUrl,Get-VdPornHubCollectorDirectory,ConvertFrom-VdPornHubScan
