param([string]$Destination=(Join-Path $PSScriptRoot '../licenses/nuget'))
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetPaths=@((Join-Path $root 'src/Mp4Downloader.App/obj/project.assets.json'))
$streamAssets=Join-Path $root 'tools/cache/n-source/src/N_m3u8DL-RE/obj/project.assets.json'
if(Test-Path $streamAssets){$assetPaths+=$streamAssets}
$packages=@{}
foreach($assetPath in $assetPaths){
 $assets=Get-Content -LiteralPath $assetPath -Raw | ConvertFrom-Json -AsHashtable
 foreach($entry in $assets.libraries.GetEnumerator()){
  if($entry.Value.type -ne 'package'){continue}
  foreach($folder in $assets.packageFolders.Keys){
   $package=Join-Path $folder $entry.Value.path
   if(Test-Path $package){$packages[$entry.Key]=$package;break}
  }
 }
 foreach($folder in $assets.packageFolders.Keys){
  foreach($id in @('Microsoft.NETCore.App.Runtime.win-x64','Microsoft.NETCore.App.Host.win-x64')){
   $package=Join-Path $folder ($id.ToLowerInvariant()+'/10.0.12');if(Test-Path $package){$packages[$id+'/10.0.12']=$package}
  }
 }
 foreach($framework in $assets.project.frameworks.Values){
  foreach($download in $framework.downloadDependencies){
   $version=($download.version -split ',')[0].Trim('[',']',' ')
   foreach($folder in $assets.packageFolders.Keys){$package=Join-Path $folder ($download.name.ToLowerInvariant()+'/'+$version);if(Test-Path $package){$packages[$download.name+'/'+$version]=$package;break}}
  }
 }
}
$inventory=@()
foreach($entry in $packages.GetEnumerator() | Sort-Object Name){
 $safe=$entry.Key.Replace('/','-');$target=Join-Path $Destination $safe
 New-Item -ItemType Directory -Path $target -Force | Out-Null
 $nuspec=Get-ChildItem -LiteralPath $entry.Value -Filter '*.nuspec' | Select-Object -First 1
 if(!$nuspec){throw ('Missing package metadata: '+$entry.Key)}
 Copy-Item -LiteralPath $nuspec.FullName -Destination $target -Force
 [xml]$metadata=Get-Content $nuspec.FullName -Raw
 $licenseFiles=@(Get-ChildItem -LiteralPath $entry.Value -Recurse -File | Where-Object {$_.Name -match '(?i)license|copying|third.?party.?notices|^notice'})
 foreach($file in $licenseFiles){$relative=[IO.Path]::GetRelativePath($entry.Value,$file.FullName);$copy=Join-Path $target $relative;New-Item -ItemType Directory -Path (Split-Path $copy) -Force | Out-Null;Copy-Item -LiteralPath $file.FullName -Destination $copy -Force}
 $inventory+=@{id=$entry.Key;license=[string]$metadata.package.metadata.license.InnerText;licenseUrl=[string]$metadata.package.metadata.licenseUrl;repository=[string]$metadata.package.metadata.repository.url;commit=[string]$metadata.package.metadata.repository.commit;notices=@($licenseFiles|ForEach-Object {[IO.Path]::GetRelativePath($entry.Value,$_.FullName).Replace('\','/')})}
}
foreach($notice in (Get-Content (Join-Path $PSScriptRoot 'streaming-notices.lock.json') -Raw | ConvertFrom-Json).notices){
 $relative=$notice.path.Substring('nuget/'.Length)
 $path=Join-Path $Destination $relative
 New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
 if(!(Test-Path $path)){Invoke-WebRequest $notice.url -OutFile $path}
 if((Get-FileHash $path).Hash.ToLowerInvariant() -ne $notice.sha256){throw 'Original upstream dependency license checksum mismatch'}
 foreach($record in $inventory){
  if($relative.StartsWith($record.id.Replace('/','-')+'/')){$record.notices=@($record.notices)+@([IO.Path]::GetFileName($relative)) | Sort-Object -Unique}
 }
}
$inventory | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Destination 'packages.json') -Encoding utf8
Write-Host ('Collected package metadata/notices: '+$inventory.Count)
