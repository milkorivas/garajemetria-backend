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
    --context AuthenticationDbContext `
    --project src/GarajeMetria.Infrastructure `
    --startup-project src/GarajeMetria.Infrastructure `
    --configuration Release
```

Las bases temporales de los tests se migran automáticamente; la base local usada
por Postman requiere ejecutar este paso por separado. En particular, el binario
con RF-003 necesita `20261113115955_LprPersistence`: si falta, el listado
`GET /api/lpr/devices` falla con 500 porque todavía no existe `LprDevices`.
Comprobar `__EFMigrationsHistory` antes de diagnosticar el filtro de taller.

Si se aplica un script EF revisado mediante `sqlcmd`, usar `-I -b`:
`-I` habilita QUOTED_IDENTIFIER, necesario para sus índices filtrados, y `-b`
interrumpe ante errores. Tras un intento fallido, comprobar historial y tablas
antes de repetir. Preferir el comando EF anterior para la actualización habitual.

## Elimina la variable temporal de esta consola
```powershell
Remove-Item Env:\GARAJEMETRIA_MIGRATIONS_CONNECTION
```

## Migración LPR — T003-06/07

`20261113115955_LprPersistence` añade siete tablas: dispositivos, eventos,
auditoría y cuatro stores OpenIddict. No modifica columnas ni datos anteriores,
ni precarga dispositivos/clientes/secretos. La prueba automatizada verifica una
base vacía y la actualización desde `20261029161807_WorkshopAssignments`,
conservando datos sintéticos de autenticación, asignaciones y catálogo.

Antes de aplicar en un entorno compartido, revisar destino y script, preparar
copia/restauración según el procedimiento operativo del entorno y coordinar la
versión de API. No se aplican migraciones al arrancar. Las pruebas SQL aisladas
no acreditan la restauración física del backup del proyecto, aún pendiente.
Estas tareas no aplican la migración a la base habitual del desarrollador ni
autorizan cambios de producción.

Con la conexión temporal cargada como arriba, generar el script sin aplicarlo:

```powershell
dotnet ef migrations script 20261029161807_WorkshopAssignments 20261113115955_LprPersistence `
    --context AuthenticationDbContext `
    --project src/GarajeMetria.Infrastructure `
    --startup-project src/GarajeMetria.Infrastructure `
    --configuration Release `
    --output artifacts/LprPersistence.sql
```

El Up revisado crea tablas/índices y registra el historial dentro de una
transacción; todas sus FKs usan NO ACTION. Ante un fallo, comprobar historial y
estado antes de repetir; no ejecutar Down como reparación automática. Si ya hay
datos LPR, Down elimina las tablas nuevas y es destructivo: requiere preservarlos,
planificar recuperación y autorización explícita. Volver al binario anterior no
exige eliminar tablas. Conservar certificados y claves privados necesarios para
tokens si se restaura una copia del entorno.
