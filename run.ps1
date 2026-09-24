$ErrorActionPreference = "Stop"
$project = "$PSScriptRoot\src\ExtremeEditor.Wpf\ExtremeEditor.Wpf.csproj"

if ($args.Count -gt 0) {
    dotnet run --project $project -- $args
}
else {
    dotnet run --project $project
}
