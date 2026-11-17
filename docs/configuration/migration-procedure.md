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

## Migración de estancias — T004-08

`20261117125011_StayPersistence` añade las diez tablas de DESIGN-004 / 5,
`CurrentLocationId` nullable, la clave UUID calculada y las columnas/checks de
auditoría de ubicación. La generación no aplica cambios a ninguna base. El
script de actualización revisable está en
[20261117125011-stay-persistence.sql](sql/20261117125011-stay-persistence.sql).
Parte de `20261113115955_LprPersistence`, requiere su esquema e historial y
es idempotente respecto al ID de la nueva migración. Una base vacía necesita
la cadena completa desde `InitialAuthentication`; este archivo no sustituye
las migraciones anteriores.

Para regenerarlo sin aplicarlo, con EF Core 10.0.12 y la conexión de diseño
cargada en la consola:

```powershell
dotnet ef migrations script 20261113115955_LprPersistence 20261117125011_StayPersistence `
    --context AuthenticationDbContext `
    --project src/GarajeMetria.Infrastructure `
    --startup-project src/GarajeMetria.Infrastructure `
    --configuration Release `
    --idempotent `
    --output docs/configuration/sql/20261117125011-stay-persistence.sql
```

Up no elimina ni actualiza cargas LPR, credenciales, UUID o auditorías previas.
La columna calculada persistida se obtiene también para los eventos anteriores;
no se crea una ubicación histórica con la descripción actual. Se agregan claves
alternativas e índices y se sustituye únicamente el check de operaciones de
auditoría para admitir `LocationChanged`. La construcción de índices/columnas
persistidas requiere recursos y locks: revisar tamaño y ventana de actualización
antes de un despliegue. El nuevo binario de persistencia necesita este esquema,
junto a la política activada de forma explícita y la integración RF-004.

El script configura ANSI_NULLS, ANSI_PADDING, ANSI_WARNINGS, ARITHABORT,
CONCAT_NULL_YIELDS_NULL y QUOTED_IDENTIFIER en ON y NUMERIC_ROUNDABORT en OFF.
Con `sqlcmd`, mantener `-I -b`; no continuar después de un error ni marcar
historial manualmente. El interceptor de las conexiones asíncronas de la API
configura estas opciones para escritura con índices calculados. Cualquier
herramienta externa que escriba necesita las mismas opciones.

Los ensayos sintéticos de migración, conservación, orden, constraints,
activación y recuperación usan bases SQL Server aisladas. Consultar los
resultados efectivamente obtenidos en TASKS-004 / 19. No acreditan una
restauración física corporativa ni autorizan una migración compartida.

Antes de una aplicación compartida siguen siendo necesarios revisar destino,
script y recuperación. No ejecutar automáticamente al arrancar. `Down` comprueba
primero que las diez tablas nuevas y las referencias/auditorías de ubicación
están vacías; si detecta uso de RF-004 lanza un error antes del DDL destructivo.
Una vez haya datos, conservar el esquema y resolver la recuperación con un plan
revisado de preservación/restauración y autorización expresa. Esta entrega no
aplica Up ni Down a la base local, compartida o de producción.

Desde T004-09, después de preparar el esquema también se requiere una política
activa coincidente para admitir eventos nuevos. El arranque no la crea.
La activación explícita, su control de versión y el orden de despliegue están en
[configuración de estancias](stays-configuration.md). El script de migración
no ejecuta esa activación. Los ensayos de activación usan exclusivamente bases
sintéticas; no se ha activado una política en un entorno compartido.
