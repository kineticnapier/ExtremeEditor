$ErrorActionPreference = "Stop"
$project = "$PSScriptRoot\src\ExtremeEditor.Wpf"

if ($args.Count -gt 0) {
    dotnet run --project $project -- $args
}
else {
    dotnet run --project $project
}
