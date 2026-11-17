# Preparación de RF-004 — T004-01

Fecha: 17 de noviembre de 2026. Preparación del entorno y datos de
[TASKS-004](../tasks/TASKS-004-arrivals-departures-and-stays.md), basada en
[DESIGN-004 / 12.1](../design/DESIGN-004-arrivals-departures-and-stays.md#121-escenarios-de-regresión-de-las-decisiones-completadas).
La preparación T004-01 no implementa `VehicleStay`, `StayCalculator`, `IWorkshopCalendar`, tablas,
worker ni endpoints nuevos. Los fixtures describen resultados esperados,
no acreditan que el algoritmo pendiente ya los produzca.

## 1. Inventario de integración

| Archivo existente | Uso y cambio previsto en tareas posteriores |
| --- | --- |
| `src/GarajeMetria.Application/Lpr/ReceiveLprEventHandler.cs` | Comparación idempotente, autorización y ventana previas al evento; T004-14 añadirá recálculo antes del mismo commit, incluyendo el evento aún no guardado. |
| `src/GarajeMetria.Application/Lpr/Contracts.cs` | `ILprClock`, repositorios y `ILprUnitOfWork`; T004-07 añadirá los contratos de estancias/calendario/cierre y ampliará DTOs de dispositivo. |
| `src/GarajeMetria.Application/Lpr/Requests.cs`, `Validators.cs`, `DeviceAdministration.cs` | Ampliación compatible de ubicación y validación, sin nuevas concesiones OAuth. |
| `src/GarajeMetria.Domain/Lpr/LprDevice.cs`, `DeviceAdministrationAuditEntry.cs` | Ubicación y snapshot/auditoría `LocationChanged` en T004-12; conservar versiones de autorización. |
| `src/GarajeMetria.Infrastructure/Persistence/LprRepository.cs` | Implementa UoW, reloj y repositorios de RF-003; T004-10/14 añadirán partición al orden de locks y atomicidad con estancias. |
| `src/GarajeMetria.Infrastructure/Persistence/SqlTransactions.cs` | `Serializable`, `sp_getapplock`, rollback y resultado incierto; se reutiliza sin reintentos ciegos. |
| `src/GarajeMetria.Infrastructure/Persistence/AuthenticationDbContext.cs`, `LprMappings.cs`, `Migrations/` | T004-08 añadirá mappings/migración, orden UUID y relaciones restrictivas; no cambiar el esquema en T004-01. |
| `src/GarajeMetria.Application/DependencyInjection.cs` | MediatR, validación y behaviors administrativos/dispositivo/cuotas; T004-07/11 ampliarán registro Application. |
| `src/GarajeMetria.Infrastructure/DependencyInjection.cs` | Repositorios y UoW scoped, reloj y configuración actuales; T004-09/10/16 añadirán opciones, políticas y worker. |
| `src/GarajeMetria.Api/Lpr/LprAdministrationEndpoints.cs`, `LprOpenApi.cs` | T004-13 ampliará alta/GET y PUT de ubicación, respetando parser estricto, ETag y OpenAPI. |
| `src/GarajeMetria.Api/Lpr/LprEventEndpoints.cs`, `LprProblems.cs` | Mantener recibo 201/200, payload, errores y límites RF-003 al integrar estancias. |

Archivos nuevos previstos: `Domain/Stays/`, contratos/servicio Application de
estancias y adaptadores/mappings/worker Infrastructure descritos por DESIGN-004.
No se crean placeholders de producción en esta tarea.

## 2. Fixtures y puntos de control

`tests/Shared/StayTestData.cs` se compila por enlace en Domain.Tests,
Application.Tests e IntegrationTests desde `tests/Directory.Build.props`.
No añade referencias entre proyectos ni paquetes. Los datos son sintéticos y
usan captura UTC fija: lunes 16 de noviembre de 2026, con `Atlantic/Canary`
en su offset de invierno. Los UUID son válidos y repetibles por escenario;
no mezclar casos independientes en la misma base sin reasignar IDs.

`StayTestTimeProvider` permite establecer el instante sin esperas reales y
normaliza a UTC. `StayTestCalendar` es soporte de pruebas para fecha y límites
de día; no es una implementación adelantada del contrato Application futuro.
`StayFixtureTests` comprueba medianoche controlada y los días canarios de 23 y
25 horas de 2026. No prueba aún duración ni cierre de estancias.

| Escenario de DESIGN-004 / 12.1 | Datos reutilizables |
| --- | --- |
| CP-023: entradas 23:50/00:10 y worker antes/después | Caso `separate-days`; mismo conjunto para ambas secuencias de ejecución. Primera salida virtual esperada: martes 00:20. |
| CP-024: repetición que cruza medianoche y frontera inclusiva | `repeat-across-midnight`, `repeat-inclusive`, `repeat-outside`; R=60 s, primera lectura 23:59:40. Variar página/orden del cierre sin cambiar capturas. |
| CP-025: asociación de 00:05 a entrada de 23:50 | `unknown-next-day`. |
| CP-025: M menos un tick, exacto y más un tick | `unknown-before-maximum`, `unknown-at-maximum`, `unknown-outside-maximum`; M=48 h. |
| CP-025: salida cercana pero entrada fuera de M | `near-exit-but-entry-outside`. |
| CP-025: empate al anterior entre días | `equidistant-across-midnight`. Para candidata excluida por política, usar `OriginalPolicy` y `ChangedPolicy` con los mismos datos y un snapshot por candidata. |
| CP-026: creación frente a captura y políticas distintas | `FusionReadings`, `FusionWinner`, `FusionAbsorbed`. A se creó primero con 30/60/48; B tiene captura anterior pero se creó después con 45/90/72. Aporta estado previo para verificar identidad/política; el evento que causa la fusión se concretará con el calculador. |
| CP-026: empate de creación | Copiar los registros mediante `with`, igualar `CreatedAt` y aportar órdenes persistidos distintos en T004-08/11; ese contador todavía no existe. |
| CP-027: ancla en parte posterior y cambio de política | `DivisionReadings`, `DivisionOriginal`, `DivisionParts`, `ChangedPolicy`; ID original corresponde a la segunda parte, todas heredan `OriginalPolicy`. |
| CP-026/027: rollback, recálculo repetido y linaje | Reutilizar las identidades anteriores y `StayTestBarrier` en el punto de escritura que implementen T004-11/18. No se afirma probar tablas o linaje que aún no existen. |

Los escenarios con 48 horas de captura no deben enviarse de golpe por HTTP
como si RF-003 admitiera eventos nuevos antiguos. Para integración futura,
avanzar el reloj de recepción con cada captura, sembrar observaciones ya
aceptadas o ampliar explícitamente la ventana solo en configuración sintética.
Fusión/división son estados preparados, no nuevos criterios de segmentación.

## 3. Harness SQL y dos hosts

Reutilizar `SqlFixture`, `AuthenticationFactory` y `LprTestEnvironment` de RF-003:
base nueva `GarajeMetria_AuthTests_<UUID>`, migraciones vigentes, certificados
temporales compartidos, Data Protection compartida mediante `shared: first` y
directorio sintético. Cada host crea sus propios scopes/contextos. Disponer
primero la segunda factory y después la primera; la variable privada de proceso
se restaura en orden inverso. La asamblea ya deshabilita paralelismo por esta
variable; no deshabilitarlo adicionalmente ni reutilizar un DbContext concurrente.

Para hosts con fecha fija, `LprTestEnvironment(certificateInstant)` genera la
vigencia de los certificados LPR alrededor de ese instante, conservando el
comportamiento actual por defecto para RF-003. El harness usa el mismo origen
temporal para certificados y reloj; no deshabilita su validación de vigencia.

`StayPreparationTests` comprueba dos conexiones efectivas a esa base, migraciones
aplicadas, DI de reloj/UoW/repositorio, tiempo controlado compartido y cero llamadas
al directorio. Su segunda prueba mantiene un lock real de catálogo con una
conexión y observa desde otro host el intento de adquirirlo antes de liberar
la transacción. No utiliza sleeps: señal y timeout delimitan la carrera.

`StayTestBarrier` ofrece señal, llegada y liberación, con continuaciones asíncronas;
los interceptores posteriores pueden pausar mediante `PauseAsync(ct)`. Liberar
siempre en `finally` y acotar esperas/cancelación. Las barreras existentes
`SaveGate`/`CatalogWait` en `LprConcurrencyTests` siguen intactas. La preparación
comprueba coordinación actual RF-003, no el lock de partición ni worker futuros.

SQL Server debe ser exclusivamente de pruebas. En Windows se reutiliza el
acceso local del fixture; para otra instancia aislada, configurar
`GARAJEMETRIA_TEST_SQL` en el entorno privado. No imprimir la variable ni su
contenido. El fixture crea y elimina solo la base de prefijo y UUID comprobados;
las pruebas acreditan esos permisos mediante su ciclo completo. No se toca la
base de la API del desarrollador ni se cambia su configuración privada.

## 4. Ejecución y filtros

SDK fijado: 10.0.401. Sin nuevas dependencias. Usar salida separada para no
interferir con DLLs de una API local abierta:

```powershell
dotnet restore GarajeMetria.slnx --artifacts-path artifacts/agent-checks/rf004-build --disable-parallel -m:1 /nr:false
dotnet build GarajeMetria.slnx --artifacts-path artifacts/agent-checks/rf004-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false
dotnet test tests/GarajeMetria.Domain.Tests/GarajeMetria.Domain.Tests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --filter FullyQualifiedName~StayFixtureTests
dotnet test tests/GarajeMetria.IntegrationTests/GarajeMetria.IntegrationTests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --filter FullyQualifiedName~StayPreparationTests
```

Estos filtros existen y son preparación. La ejecución completa diaria puede
incluirlos sin repetirlos por separado. Registrar resultados reales en TASKS-004
y respetar `artifacts/agent-checks/full-suite.json`; un resultado previo de RF-003
no verifica estas nuevas pruebas.

Filtros existentes para regresiones posteriores según cambios: `ReceiveLprEventTests`
(Application), `LprPreparationTests`, `LprHttpTests`, `LprPersistenceTests`,
`LprMigrationTests`, `LprConfigurationTests` e infraestructura de arquitectura
`DependencyTests`/`ApiBoundaryTests`. Antes de usarlos comprobar descubrimiento
y número no nulo de tests; no inventar nombres de clases futuras de estancias.

Los resultados de esta preparación solo acreditan entorno, tiempo y coordinación
de base. CA/CP de cálculo quedan pendientes de T004-02 en adelante; SQL de
estancias, linaje, ubicación y cierre se verificará cuando exista su implementación.


## Verificación integrada posterior

T004-10 a T004-24 añaden pruebas Application con puertos en memoria, SQL real
para revisiones/linaje/ubicación/políticas, HTTP y barreras de concurrencia,
CLI como proceso real y carga sintética de referencia. Los filtros y resultados
finales están en TASKS-004 / 19. En Windows, el runner necesita acceso a la
identidad Windows para SQL local; un error SSPI del sandbox no acredita un
fallo de reglas ni una prueba SQL superada. Repetir solo lo afectado con el
acceso local autorizado, conservando el intento fallido.
