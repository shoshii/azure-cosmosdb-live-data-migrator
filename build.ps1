# please refer to the GitHub issue https://github.com/Azure-Samples/azure-cosmosdb-live-data-migrator/issues/57
param (
  [Parameter(Mandatory = $false, ParameterSetName = 'TargetPath', HelpMessage = 'target path to store packages')]
  [string[]]$TargetPath
)
if ($TargetPath) {
  $InstallationPackageTargetFolder = $TargetPath
} else {
  $InstallationPackageTargetFolder = "$(pwd)\target"
}

#echo $InstallationPackageTargetFolder

if (Test-Path $InstallationPackageTargetFolder\Migration.UI.WebApp.zip) {
  del $InstallationPackageTargetFolder\Migration.UI.WebApp.zip 
}
if (Test-Path $InstallationPackageTargetFolder\Temp\Migration.UI.WebApp) {
  del $InstallationPackageTargetFolder\Temp\Migration.UI.WebApp -Recurse
}
cd Migration.UI.WebApp
dotnet clean
dotnet publish -c Release
cd ..
if (-not (Test-Path $InstallationPackageTargetFolder\Temp)) {
New-Item -Path $InstallationPackageTargetFolder -Name Temp -ItemType Directory
}
New-Item -Path $InstallationPackageTargetFolder\Temp -Name Migration.UI.WebApp -ItemType Directory
  
Copy-Item -Path Migration.UI.WebApp\bin\release\netcoreapp3.1\publish\* -Destination $InstallationPackageTargetFolder\Temp\Migration.UI.WebApp -Recurse


# Create the installation package for the Executor WebJob
if (Test-Path $InstallationPackageTargetFolder\Migration.Executor.WebJob.zip) {
  del $InstallationPackageTargetFolder\Migration.Executor.WebJob.zip
}
if (Test-Path $InstallationPackageTargetFolder\Temp\Migration.Executor.WebJob) {
  del $InstallationPackageTargetFolder\Temp\Migration.Executor.WebJob -Recurse
}

cd Migration.Executor.WebJob
dotnet clean
dotnet publish -c Release
cd ..
if (-not (Test-Path $InstallationPackageTargetFolder\Temp)) {
  New-Item -Path $InstallationPackageTargetFolder -Name Temp -ItemType Directory
}
New-Item -Path $InstallationPackageTargetFolder\Temp -Name Migration.Executor.WebJob -ItemType Directory
New-Item -Path $InstallationPackageTargetFolder\Temp\Migration.Executor.WebJob -Name App_Data -ItemType Directory
New-Item -Path $InstallationPackageTargetFolder\Temp\Migration.Executor.WebJob\App_Data -Name jobs -ItemType Directory
New-Item -Path $InstallationPackageTargetFolder\Temp\Migration.Executor.WebJob\App_Data\jobs -Name continuous -ItemType Directory
New-Item -Path $InstallationPackageTargetFolder\Temp\Migration.Executor.WebJob\App_Data\jobs\continuous -Name Migration-Executor-Job -ItemType Directory

Copy-Item -Path Migration.Executor.WebJob\bin\release\netcoreapp3.1\publish\* -Destination $InstallationPackageTargetFolder\Temp\Migration.Executor.WebJob\App_Data\jobs\continuous\Migration-Executor-Job -Recurse

# Create the installation package for the Monitoring WebJob
if (Test-Path $InstallationPackageTargetFolder\Migration.Monitor.WebJob.zip) {
  del $InstallationPackageTargetFolder\Migration.Monitor.WebJob.zip
}
if (Test-Path $InstallationPackageTargetFolder\Temp\Migration.Monitor.WebJob) {
  del $InstallationPackageTargetFolder\Temp\Migration.Monitor.WebJob -Recurse
}

cd Migration.Monitor.WebJob
dotnet clean
dotnet publish -c Release
cd ..
if (-not (Test-Path $InstallationPackageTargetFolder\Temp)) {
  New-Item -Path $InstallationPackageTargetFolder -Name Temp -ItemType Directory
}
New-Item -Path $InstallationPackageTargetFolder\Temp -Name Migration.Monitor.WebJob -ItemType Directory
New-Item -Path $InstallationPackageTargetFolder\Temp\Migration.Monitor.WebJob -Name App_Data -ItemType Directory
New-Item -Path $InstallationPackageTargetFolder\Temp\Migration.Monitor.WebJob\App_Data -Name jobs -ItemType Directory
New-Item -Path $InstallationPackageTargetFolder\Temp\Migration.Monitor.WebJob\App_Data\jobs -Name continuous -ItemType Directory
New-Item -Path $InstallationPackageTargetFolder\Temp\Migration.Monitor.WebJob\App_Data\jobs\continuous -Name Migration-Monitor-Job -ItemType Directory

Copy-Item -Path Migration.Monitor.WebJob\bin\release\netcoreapp3.1\publish\* -Destination $InstallationPackageTargetFolder\Temp\Migration.Monitor.WebJob\App_Data\jobs\continuous\Migration-Monitor-Job -Recurse

Compress-Archive -Path $InstallationPackageTargetFolder\Temp\Migration.UI.WebApp\* -DestinationPath $InstallationPackageTargetFolder\Migration.UI.WebApp.zip
Compress-Archive -Path $InstallationPackageTargetFolder\Temp\Migration.Executor.WebJob\* -DestinationPath $InstallationPackageTargetFolder\Migration.Executor.WebJob.zip
Compress-Archive -Path $InstallationPackageTargetFolder\Temp\Migration.Monitor.WebJob\* -DestinationPath $InstallationPackageTargetFolder\Migration.Monitor.WebJob.zip