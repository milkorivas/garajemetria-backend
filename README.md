# GarajeMetría Backend

API REST para eventos LPR, entradas, salidas, estancias y administración de talleres.

El nombre visible del proyecto es **GarajeMetría**. En código, espacios de nombres,
proyectos, ensamblados y rutas se utiliza `GarajeMetria`, sin acento.

## Estado actual

Implementación de RF-001 con .NET 10 y C# 14: autenticación corporativa, JWT,
sesiones persistidas en SQL Server, cuotas compartidas, antiforgery y limpieza.
Las pruebas HTTP usan un directorio sintético y SQL real; la verificación contra
AD de pruebas y la restauración física aislada siguen pendientes.
No están implementados los endpoints administrativos ni los casos LPR.
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
configuración ausente o inválida y no aplica migraciones al arrancar.

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
`/swagger/index.html` por HTTPS. Swagger UI permite explorar los cuatro endpoints;
bootstrap y cabecera antiforgery siguen siendo obligatorios para los POST.
