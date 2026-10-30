# GarajeMetría Backend

API REST para eventos LPR, entradas, salidas, estancias y administración de talleres.

El nombre visible del proyecto es **GarajeMetría**. En código, espacios de nombres,
proyectos, ensamblados y rutas se utiliza `GarajeMetria`, sin acento.

## Estado actual

Implementación de RF-001 con .NET 10 y C# 14: autenticación corporativa, JWT,
sesiones persistidas en SQL Server, cuotas compartidas, antiforgery y limpieza.
Las pruebas HTTP usan un directorio sintético y SQL real; la verificación contra
AD de pruebas y la restauración física aislada siguen pendientes.
RF-009 incorpora consulta administrativa de talleres con fuente SQL auxiliar y copia
local SQL. Las asignaciones administrativas de RF-002 y los casos LPR siguen pendientes.
Las reglas de AGENTS.md rigen el desarrollo.

Las decisiones técnicas comunes se documentan en
[Arquitectura del backend](docs/02-architecture.md), incluida la utilización de
OpenAPI para el contrato HTTP y Swagger UI para su consulta interactiva.

## Estructura

```text
GarajeMetria.slnx
src/
  GarajeMetria.Domain/
  GarajeMetria.Application/
  GarajeMetria.Infrastructure/
  GarajeMetria.Api/
tests/
  GarajeMetria.Domain.Tests/
  GarajeMetria.Application.Tests/
  GarajeMetria.IntegrationTests/
  GarajeMetria.ArchitectureTests/
```

- Domain contiene entidades e invariantes sin dependencias externas.
- Application contiene casos de uso y contratos; depende de Domain.
- Infrastructure contiene persistencia y adaptadores; depende de Application y Domain.
- Api contiene transporte y composición; depende de Application e Infrastructure.
  El uso de Infrastructure se limita al registro de dependencias y arranque.

EF Core con SQL Server, MediatR y FluentValidation se utilizan en autenticación,
el primer tramo independiente. Sus reglas y contratos están definidos en
[RF-001](docs/requirements/RF-001-authentication.md) y
[DESIGN-001](docs/design/DESIGN-001-authentication.md); su trabajo está en
[TASKS-001](docs/tasks/TASKS-001-authentication.md). Las decisiones de ingesta LPR
pendientes se resuelven antes de implementar ese tramo.

## Requisitos y ejecución

Instalar el SDK .NET 10.0.401 o un parche compatible según global.json.
Preparar SQL Server, aplicar la migración revisada y configurar certificados y
el archivo privado externo indicado por `GARAJEMETRIA_PRIVATE_CONFIG`, según el
[runbook de autenticación](docs/runbooks/authentication.md). La API rechaza
configuración obligatoria ajena a SQL ausente o inválida y no aplica migraciones
al arrancar. SQL ausente, inválido o inaccesible permite iniciar el host para
diagnosticarlo; la autenticación dependiente de SQL devuelve `503` hasta verificar
las políticas compartidas. La comprobación se repite cada 30 segundos.

```sh
dotnet restore
dotnet run --project src/GarajeMetria.Api --launch-profile https
```

El perfil local escucha en https://localhost:7040 y http://localhost:5040,
con redirección a HTTPS. Para confiar en el certificado local de desarrollo:

```sh
dotnet dev-certs https --trust
```

## Verificación

Desde la raíz:

```sh
dotnet restore
dotnet build --no-restore --configuration Release
dotnet format --verify-no-changes --no-restore
dotnet test --no-build --configuration Release
```

GitHub Actions ejecuta estas comprobaciones. Las pruebas actuales verifican las
referencias efectivas entre proyectos, la ausencia de paquetes externos en
Domain y de dependencias de persistencia/HTTP en Application. Un análisis
semántico de C# con Roslyn verifica que Api no acceda a Infrastructure ni a
DbContext desde endpoints, también con alias o lambdas en Program.cs.
La única entrada de Infrastructure admitida es la fachada estática
`GarajeMetria.Infrastructure.DependencyInjection`, con `AddInfrastructure` y
`UseInfrastructure`, invocada desde Program.cs o métodos estáticos de clases estáticas en
`GarajeMetria.Api.Composition`.
Esta convención concreta el registro/arranque permitido; no habilita acceso a
persistencia desde esos archivos. Roslyn es dependencia privada de tests y no
se añade a producción. Ejecutar restore/build antes de los tests: se comprueban
los assets efectivos y el código de Api con sus referencias compiladas.
Domain y Application tienen pruebas unitarias sin red ni base de datos.
Las pruebas HTTP usan WebApplicationFactory; la persistencia utiliza bases
temporales en SQL Server aislado, sin EF InMemory ni sustitutos SQLite.
En Windows se utiliza la instancia local por memoria compartida; otra instancia
se configura con `GARAJEMETRIA_TEST_SQL`. Cada ejecución crea y elimina sus
propias bases `GarajeMetria_AuthTests_<UUID>`.
LDAPS real requiere `GARAJEMETRIA_AD_TEST_CONFIG`; sin él la prueba se omite,
sin presentarla como verificada. Los prerrequisitos y la evidencia se mantienen
en el runbook y TASKS-001.

Con `Http:EnableApiDocumentation=true`, consultar `/openapi/v1.json` y
`/swagger/index.html` por HTTPS. Swagger UI permite explorar los cuatro endpoints
de autenticación y el diagnóstico; bootstrap y cabecera antiforgery siguen siendo
obligatorios para los POST.

## Diagnóstico del servicio

Consultar sin sesión por HTTPS:

```sh
curl https://localhost:7040/api/health
```

Ejemplo con SQL sin configurar y AD disponible:

```json
{
  "status": "ERROR",
  "modules": {
    "service": { "status": "OK" },
    "sql": { "status": "NOT_CONFIGURED" },
    "activeDirectory": { "status": "OK" }
  }
}
```

Cada módulo devuelve `OK`, `ERROR` o `NOT_CONFIGURED`. La respuesta es `200` y el
estado global `OK` solo si todos están disponibles; en otro caso es `503` y
`ERROR`, con el mismo contrato JSON y `Cache-Control: no-store`.
SQL ejecuta una consulta mínima de lectura; una cadena inválida o una conexión
fallida devuelve `ERROR`. AD realiza conexión LDAPS y bind con la cuenta de
servicio, sin consultar usuarios. Cada comprobación tiene un máximo de cinco
segundos y ambas se ejecutan independientemente. El servicio indica que la API
responde; no certifica todos sus casos de uso, el esquema SQL ni permisos de AD.
Se admiten 60 peticiones por minuto por instancia; los excesos reciben `429`
ProblemDetails. No se devuelven detalles internos ni secretos.
La configuración privada, los certificados y las opciones no SQL siguen siendo
obligatorios. Cambiar el archivo privado requiere reiniciar; recuperar la
conectividad de un SQL ya configurado no requiere reinicio.

## Catálogo de talleres (RF-009)

RF-009 lee treinta talleres sintéticos desde `GarajeMetriaAux.dbo.ActiveWorkshops`
y guarda su copia local en `GarajeMetria`. El mock en memoria se ha eliminado.
En `Development` se habilita `Source=SqlServer`; en otros entornos la
sincronización está deshabilitada por defecto. La conexión externa
`ConnectionStrings:WorkshopSource` y sus opciones se configuran en el archivo
privado externo indicado por `GARAJEMETRIA_PRIVATE_CONFIG`, según
[el ejemplo](docs/configuration/workshop-catalog.example.json).
El script [de la fuente auxiliar](docs/configuration/sql/workshop-source-local.sql)
crea la base, tabla `dbo.Workshops` (`Code`, `Name`, `IsActive`), vista de activos
y treinta registros. Se ejecuta manualmente y conserva datos existentes.
Para probar una baja, cambiar `IsActive` en la fuente; al sincronizar se conserva
la referencia local como inactiva. La API no escribe en la fuente externa.
Reiniciar la API después de cambiar el archivo privado o el adaptador.

Aplicar previamente la migración nueva mediante el procedimiento habitual
([migraciones](docs/configuration/migration-procedure.md)); el host no la aplica.
La primera sincronización se intenta al arrancar y se repite cada 5 minutos,
con timeout de 60 segundos. Si hay talleres previos sin código corporativo, la
migración se detiene y exige un plan de correspondencias que preserve sus IDs.

Consulta: `GET /api/workshops?pageNumber=1&pageSize=20&isActive=true`, con
access token Bearer de Administrador y sesión vigente. Sin primera publicación,
responde 503. No hay altas ni cambios manuales. El diseño y los pasos para
adoptar una futura vista corporativa están en [DESIGN-009](docs/design/DESIGN-009-workshop-catalog.md).

## RF-002 — Asignaciones de taller

Administración de perfiles y asignaciones con Bearer de Administrador:
`GET /api/users`, `GET /api/users/{userId}/default-workshop` y
`PUT /api/users/{userId}/default-workshop`. Los cambios requieren motivo e
If-Match y confirman asignación, auditoría y cierre de sesión conjuntamente.
Aplicar la migración `WorkshopAssignments` mediante el procedimiento habitual.
Contratos, configuración, errores y límites de verificación en el
[runbook de administración](docs/runbooks/user-administration.md).
