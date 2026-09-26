function Assert-MediaLicenseConfiguration([string]$Configuration){
 if($Configuration -match '--enable-nonfree|--enable-autodetect' -or $Configuration -notmatch '--disable-autodetect' -or $Configuration -notmatch '--enable-gpl' -or $Configuration -notmatch '--enable-version3' -or $Configuration -notmatch '--disable-network'){throw 'Forbidden/unreviewed FFmpeg build configuration'}
 $libraries=@([regex]::Matches($Configuration,'--enable-lib[a-z0-9]+') | ForEach-Object {$_.Value} | Sort-Object -Unique)
 if(($libraries -join ',') -ne '--enable-libopus,--enable-libvpx,--enable-libx264'){throw 'Unreviewed external FFmpeg library'}
}
