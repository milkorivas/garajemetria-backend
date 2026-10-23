# GarajeMetría — Pasos para actualizar la BD

## Si no tienes instalada la herramienta de EF Core


```powershell
dotnet tool install --global dotnet-ef --version 10.0.12
```

## Carga la conexión desde tu archivo privado, sin escribir credenciales en el historial
```powershell
$privateConfig = Get-Content -LiteralPath $env:GARAJEMETRIA_PRIVATE_CONFIG -Raw | ConvertFrom-Json

$env:GARAJEMETRIA_MIGRATIONS_CONNECTION = $privateConfig.ConnectionStrings.Authentication
```

## Aplica las migraciones
```powershell
dotnet ef database update `
    --project src/GarajeMetria.Infrastructure `
    --startup-project src/GarajeMetria.Infrastructure `
    --configuration Release
```

## Elimina la variable temporal de esta consola
```powershell
Remove-Item Env:\GARAJEMETRIA_MIGRATIONS_CONNECTION
```
