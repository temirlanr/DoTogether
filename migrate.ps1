param(
    [Parameter(Mandatory)]
    [ValidateSet("add", "apply", "list", "remove")]
    [string]$Action,

    [string]$Name
)

$startupProject  = "DoTogether"
$migrationsProject = "DoTogether.Infrastructure"
$dbContext       = "AppDbContext"

function Ensure-EfToolsInstalled {
    $installed = dotnet tool list --global | Select-String "dotnet-ef"
    if (-not $installed) {
        Write-Host "Installing dotnet-ef..." -ForegroundColor Yellow
        dotnet tool install --global dotnet-ef
    }
}

Ensure-EfToolsInstalled

$baseArgs = @(
    "--project",         $migrationsProject,
    "--startup-project", $startupProject,
    "--context",         $dbContext
)

switch ($Action) {
    "add" {
        if (-not $Name) {
            Write-Error "Action 'add' requires -Name parameter. Example: .\migrate.ps1 -Action add -Name InitialCreate"
            exit 1
        }
        Write-Host "Creating migration '$Name'..." -ForegroundColor Cyan
        dotnet ef migrations add $Name @baseArgs
    }
    "apply" {
        Write-Host "Applying migrations to database..." -ForegroundColor Cyan
        dotnet ef database update @baseArgs
    }
    "list" {
        Write-Host "Listing migrations:" -ForegroundColor Cyan
        dotnet ef migrations list @baseArgs
    }
    "remove" {
        Write-Host "Removing last migration..." -ForegroundColor Yellow
        dotnet ef migrations remove @baseArgs
    }
}