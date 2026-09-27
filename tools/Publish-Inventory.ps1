param([Parameter(Mandatory=$true)][string]$PublishDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$published=[IO.Path]::GetFullPath($PublishDirectory)
$releaseVersion=(Get-Content (Join-Path $PSScriptRoot 'release-policy.json') -Raw | ConvertFrom-Json).version
$packageMetadata=Get-Content (Join-Path $root 'licenses/nuget/packages.json') -Raw | ConvertFrom-Json
$assets=Get-Content (Join-Path $root 'src/Mp4Downloader.App/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$index=@{}
$wanted=@{}
$binaries=@(Get-ChildItem $published -Recurse -File | Where-Object {$_.Extension -in '.exe','.dll','.pyd','.winmd' -or $_.Name -eq 'python314.zip' -or $_.Name -eq 'yt-dlp'})
foreach($file in $binaries){$wanted[$file.Name]=$true}
foreach($metadata in $packageMetadata){
 $parts=$metadata.id.Split('/');$package=$null
 foreach($folder in $assets.packageFolders.Keys){$candidate=Join-Path $folder ($parts[0].ToLowerInvariant()+'/'+$parts[1]);if(Test-Path $candidate){$package=$candidate;break}}
 if(!$package){continue}
 foreach($file in Get-ChildItem $package -File -Recurse | Where-Object {$wanted.ContainsKey($_.Name)}){
  if(!$index.ContainsKey($file.Name)){$index[$file.Name]=@()}
  $index[$file.Name]+=@{file=$file.FullName;metadata=$metadata}
 }
}
$toolLock=Get-Content (Join-Path $PSScriptRoot 'tools.lock.json') -Raw | ConvertFrom-Json
$manifest=@()
foreach($file in $binaries){
 $relative=[IO.Path]::GetRelativePath($published,$file.FullName).Replace('\','/')
 $hash=(Get-FileHash $file.FullName).Hash.ToLowerInvariant()
 $component=$null;$license=$null;$upstream=$null;$version=$null;$build=$null;$source=$null
 if($relative.StartsWith('tools/')){
  $toolName=if($relative.StartsWith('tools/python/')){'python'}elseif($file.Name -eq 'ffprobe.exe'){'ffmpeg'}elseif($file.Name -eq 'yt-dlp'){'yt-dlp'}else{[IO.Path]::GetFileNameWithoutExtension($file.Name)}
  $tool=@($toolLock.tools | Where-Object name -eq $toolName)
  if($tool.Count -ne 1){throw ('Unknown tool binary: '+$relative)}
  $component=$tool[0].name;$license=$tool[0].license;$upstream=$tool[0].source;$version=$tool[0].version
  $build=if($toolName -eq 'ffmpeg'){'tools/Build-Media.sh; tools/media-provenance'}elseif($toolName -eq 'N_m3u8DL-RE'){'tools/Build-Streaming.ps1; tools/streaming-locks'}else{'Unmodified upstream official distribution; tools/tools.lock.json'}
  $source='Companion third-party-sources.zip; tools/third-party-sources.lock.json'
 }elseif($file.Name -like 'Mp4Downloader.*'){
  $component='MP4 Downloader';$license='MIT';$upstream='https://github.com/tanaka-note/mp4-downloader-windows';$version=$releaseVersion;$build='tools/Publish.ps1';$source='GitHub tag v'+$releaseVersion
 }else{
  foreach($candidate in $index[$file.Name]){
   if((Get-FileHash $candidate.file).Hash.ToLowerInvariant() -eq $hash){
    $metadata=$candidate.metadata;$component=$metadata.id;$license=$metadata.license
    if(!$license){$license=$metadata.licenseUrl}
    $version=$metadata.id.Split('/')[1];$upstream='https://www.nuget.org/packages/'+$metadata.id
    $build='Unmodified official NuGet asset; locked restore'
    $source=if($metadata.repository){$metadata.repository+' @ '+$metadata.commit}else{'Microsoft proprietary redistributable; original package metadata/terms in licenses/nuget'}
    break
   }
  }
  if(!$component){throw ('Unmapped publish binary: '+$relative)}
 }
 $manifest+=@{file=$relative;version=$version;fileVersion=$file.VersionInfo.FileVersion;sha256=$hash;component=$component;license=$license;upstream=$upstream;source=$source;build=$build;redistribution='Retain original licenses/notices; see THIRD_PARTY_TERMS.md and companion source package'}
}
@{format=1;files=$manifest;nugetPackages=$packageMetadata;toolArchives=$toolLock.tools} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $published 'THIRD_PARTY_MANIFEST.json') -Encoding utf8
Write-Host ('Publish provenance: '+$manifest.Count+' executable/library files mapped to exact hashes and origins')
