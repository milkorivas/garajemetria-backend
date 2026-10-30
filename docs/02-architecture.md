# GarajeMetría — Arquitectura del backend

## 1. Propósito y relación con la documentación

Este documento recoge las decisiones técnicas comunes del backend para quienes
lo desarrollan, mantienen o revisan. Complementa la
[visión y alcance](00-vision-y-alcance.md) y el
[catálogo de requerimientos](01-catalogo-requerimientos.md).
Los requisitos definen el comportamiento; los documentos de `design/` concretan
los contratos de cada tramo y los de `tasks/` planifican su implementación.

[AGENTS.md](../AGENTS.md) conserva las instrucciones para los agentes de IA.
Las decisiones de arquitectura recogidas allí también se explican aquí para
lectores humanos. Cuando cambie una decisión compartida, ambos documentos y
los diseños afectados deben mantenerse coherentes.

La solución implementa el tramo RF-001 con persistencia SQL Server y
OpenAPI/Swagger UI, además de sus pruebas y comprobaciones de arquitectura.
La verificación contra AD de pruebas y la restauración física aislada siguen
pendientes. Los casos administrativos y LPR no están implementados.
El estado y la preparación operativa se detallan en el
[runbook de autenticación](runbooks/authentication.md).

## 2. Plataforma y tecnologías

| Elemento | Decisión |
| --- | --- |
| Lenguaje y runtime | C# 14 y .NET 10. Un cambio de versión principal requiere una decisión explícita. |
| API | ASP.NET Core Web API; se admiten controllers o Minimal APIs. |
| Persistencia | Entity Framework Core con repositorios y unidad de trabajo. |
| Motor inicial | SQL Server. |
| Casos de uso | CQRS con MediatR por defecto. |
| Validación de Application | FluentValidation antes de ejecutar los casos de uso. |
| Contrato HTTP | OpenAPI. |
| Consulta interactiva del contrato | Swagger UI. |
| Alojamiento inicial | Windows/IIS, con entornos de desarrollo y producción. |

El SDK se fija en [global.json](../global.json). EF Core y su proveedor deben
alinearse con el runtime seleccionado. No se incorporan dependencias preview
sin una decisión explícita. El nombre visible es **GarajeMetría**; código,
namespaces, proyectos, ensamblados y rutas utilizan `GarajeMetria`.

## 3. Capas y dependencias

Se utiliza Clean Architecture centrada en el dominio. Las dependencias apuntan
hacia las capas internas y no se permiten dependencias circulares.
Las invariantes de negocio pertenecen a Domain y la orquestación de los casos
de uso a Application.

La solución es `GarajeMetria.slnx` y sus proyectos están bajo `src/`:

| Proyecto | Responsabilidad | Referencias permitidas a otros proyectos |
| --- | --- | --- |
| `GarajeMetria.Domain` | Entidades, objetos de valor, invariantes, enums y excepciones de dominio. Sin paquetes externos ni acceso a red, archivos o base de datos. | Ninguna |
| `GarajeMetria.Application` | Casos de uso, DTOs, validadores y contratos de persistencia, consultas y servicios externos. | Domain |
| `GarajeMetria.Infrastructure` | Adaptadores externos, DbContext, mappings, migraciones, repositorios y almacenamiento de archivos. | Application y Domain |
| `GarajeMetria.Api` | Endpoints, DTOs HTTP, middleware, configuración del host y raíz de composición. | Application; Infrastructure solo para registro y arranque |

Los endpoints usan contratos de Application; no acceden a DbContext ni a
implementaciones de Infrastructure. Application no depende de EF Core,
proveedores de base de datos ni frameworks HTTP y no expone `IQueryable`.
Los eventos de dominio son opcionales.

La composición utiliza la fachada
`GarajeMetria.Infrastructure.DependencyInjection`, con `AddInfrastructure` y
`UseInfrastructure`, desde `Program.cs` o métodos estáticos de clases estáticas
en `GarajeMetria.Api.Composition`, conforme a las comprobaciones actuales.

## 4. Casos de uso y contratos internos

Los comandos y consultas MediatR son la entrada habitual de los casos de uso.
Un comportamiento de validación asíncrono ejecuta FluentValidation antes de los
handlers. Para CRUD simple sin comportamiento de dominio se admite llamar
directamente a un servicio de Application. Ese servicio debe recibir
`IValidator<T>` y validar como primer paso, antes de lógica de dominio o
persistencia. Omitir MediatR no elimina autorización ni transacciones.

Los contratos de repositorios y unidad de trabajo pertenecen a Application;
Infrastructure los implementa. DbContext puede implementar la unidad de
trabajo. Cada caso de uso define su límite transaccional, sin exigir
repositorios genéricos ni duplicar innecesariamente las capacidades de EF Core.

Las consultas e informes pueden usar contratos de lectura específicos que
devuelven DTOs materializados. Infrastructure utiliza proyecciones EF Core;
Dapper solo se incorpora si una necesidad medida lo justifica. Se prefieren
mapeos manuales explícitos. Una biblioteca de mapeo requiere una necesidad
concreta y una elección común al proyecto.

Se utiliza inyección de dependencias, con DbContext y repositorios scoped y
handlers preferentemente transitorios. No se comparte un DbContext entre
operaciones concurrentes ni se capturan servicios scoped desde singletons.
La E/S es asíncrona y propaga `CancellationToken` desde HTTP. El trabajo fuera
de solicitudes utiliza servicios en segundo plano supervisados.

## 5. Persistencia e independencia del proveedor

Domain, Application y los contratos REST permanecen independientes del motor.
Cambiar de proveedor puede exigir adaptar configuración, mappings, SQL y
migraciones de Infrastructure, pero no esas capas ni contratos. SQL Server es
el único motor inicial; esta separación no supone soporte multiproveedor ya
implementado ni migración automática de datos.

Los mappings son explícitos y separados, con longitudes, precisión, índices,
claves y restricciones que respalden las invariantes persistidas. Se prefieren
LINQ portable, proyecciones acotadas y `AsNoTracking` para lectura. El SQL y los
mecanismos de concurrencia propios del proveedor quedan en Infrastructure.

Las escrituras definen atomicidad y tratamiento de conflictos. No se mantienen
transacciones abiertas durante llamadas externas ni se reintentan escrituras
no idempotentes sin garantías. Los timeouts y reintentos deben estar acotados.
Los instantes se representan con `DateTimeOffset` en UTC; las zonas horarias
se aplican cuando lo exige el caso de uso.

Las migraciones se versionan y revisan. No se modifican las ya aplicadas en
entornos compartidos, no se sustituyen por `EnsureCreated` y no se aplican
automáticamente al arrancar en producción. Cada proveedor adoptado tendrá sus
migraciones y pruebas correspondientes.

## 6. Contrato HTTP y documentación de la API

**Se utilizará OpenAPI para describir el contrato HTTP y Swagger UI para su
consulta interactiva.** OpenAPI recoge la descripción estructurada de la API;
Swagger UI presenta esa descripción para explorar las operaciones y realizar
solicitudes de prueba bajo sus controles de seguridad.

La descripción debe mantenerse alineada con los endpoints implementados:
rutas, métodos, parámetros, cabeceras, cuerpos, esquemas, respuestas
satisfactorias, errores y requisitos de autenticación y autorización.
Los diseños concretan los contratos por funcionalidad; OpenAPI los refleja
para los consumidores y las pruebas HTTP verifican su comportamiento.

La generación de OpenAPI y la integración de Swagger UI pertenecen a Api.
La implementación de RF-001 utiliza `Microsoft.AspNetCore.OpenApi` para generar
la descripción y `Swashbuckle.AspNetCore.SwaggerUI` para presentarla. Publica
`/openapi/v1.json` y `/swagger/index.html` solo al habilitar explícitamente
`Http:EnableApiDocumentation`; la plantilla lo deja desactivado.
Swagger UI no sustituye las pruebas automatizadas ni modifica los controles
de autenticación, autorización, antiforgery o limitación de peticiones.
Sus ejemplos utilizan datos sintéticos y no contienen secretos.

La API utiliza rutas orientadas a recursos, semántica HTTP y DTOs explícitos;
no expone entidades de persistencia. Los errores siguen ProblemDetails con
manejo centralizado y distinción entre validación, autenticación, autorización,
ausencia de recursos y conflictos. No se publican trazas internas, SQL ni secretos.
Los cambios incompatibles requieren un plan de migración. Paginación, tamaños
de solicitud y campos de filtrado/ordenación están acotados.

## 7. Seguridad, configuración y operación

Los endpoints están protegidos por defecto y los accesos anónimos se justifican
explícitamente. La autorización comprueba tanto la operación como el recurso,
con aislamiento por taller derivado de identidad confiable. Un identificador
de taller enviado por el cliente no acredita acceso.

Para dispositivos LPR, [RF-003](requirements/RF-003-lpr-event-ingestion.md)
establece OAuth Client Credentials emitido por GarajeMetría con credenciales
propias por dispositivo, tokens de 15 minutos configurables y sin refresh token.
La revocación, rotación y desactivación invalidan inmediatamente los tokens
previos entre instancias; no basta validar firma y caducidad. La asociación
dispositivo/taller es inmutable y los registros no se borran físicamente.
El [diseño DESIGN-003](design/DESIGN-003-lpr-event-ingestion.md) selecciona
OpenIddict para servidor y validación local, con stores EF en Infrastructure
y protocolo HTTP en Api. Añade una versión persistida por dispositivo para
invalidación inmediata, coordinada con publicación de catálogo; las capas
internas no dependen de OpenIddict. La implementación está pendiente y no cambia
la autenticación de usuarios de RF-001. Los errores del endpoint de token
siguen OAuth, separados de ProblemDetails para recursos.

Se utilizan HTTPS, CORS con orígenes explícitos, límites de peticiones y
protección CSRF cuando el uso de cookies lo exige. No se deshabilita la
validación de certificados. Las matrículas y los eventos asociados se tratan
como datos sensibles; no se registran matrículas completas, cargas LPR
completas, credenciales ni tokens.

Los secretos quedan fuera del repositorio y los logs. La configuración
obligatoria utiliza opciones tipadas validadas al arrancar. Los logs son
estructurados y tienen identificadores de correlación; los health checks no
exponen detalles internos. El archivo privado externo y los contratos exactos
de autenticación se definen en
[DESIGN-001](design/DESIGN-001-authentication.md).

`GET /api/health` es un diagnóstico operativo anónimo: debe poder consultarse
cuando SQL o el login no estén disponibles. Se excluye del procesamiento Bearer
y devuelve únicamente estados genéricos, sin datos internos. Su contrato contiene
`status` global y `modules.service`, `modules.sql`, `modules.activeDirectory`, cada
uno con `status`: `OK`, `ERROR` o `NOT_CONFIGURED`. Todos disponibles producen
HTTP `200` y estado global `OK`; cualquier otro resultado produce `503` y
`ERROR`, conservando el mismo JSON. Estos fallos son resultados del diagnóstico;
los errores inesperados usan ProblemDetails. Las respuestas no se almacenan en
caché. Un límite local de 60 peticiones/minuto por instancia devuelve `429`
ProblemDetails sin depender de SQL.

Api posee transporte y DTOs; una consulta MediatR de Application coordina los
contratos de diagnóstico implementados por Infrastructure. SQL abre la conexión
configurada y ejecuta `SELECT 1` sin escrituras; ausencia de cadena produce
`NOT_CONFIGURED`, cadena inválida o consulta fallida produce `ERROR`. AD conecta
por LDAPS y realiza bind con la cuenta de servicio, con la validación de
certificados y servidores alternativos existentes, sin consultar usuarios.
SQL y AD se comprueban independientemente, con cancelación HTTP y un máximo de
cinco segundos por módulo. El estado del servicio acredita que la API responde,
no la corrección integral de sus funciones.

La ausencia, invalidez o indisponibilidad de SQL ya no impide arrancar el host.
La verificación de políticas compartidas se ejecuta en un servicio supervisado
con comprobaciones cada 30 segundos; las operaciones HTTP dependientes de SQL
devuelven `503` hasta la primera verificación correcta y mientras se detecte un
fallo posterior. No hay almacenamiento alternativo ni relajación de cuotas.
La recuperación de conectividad permite reanudar operaciones sin reinicio;
cambiar la configuración privada sí requiere reiniciar. Las demás validaciones
de configuración y certificados siguen siendo obligatorias y no se aplican
migraciones al arrancar.

Los roles y el acceso a talleres se desarrollan en
[DESIGN-002](design/DESIGN-002-roles-and-workshop-access.md).
Los plazos de retención se mantienen en sus documentos correspondientes.

## 8. Pruebas y verificación

Las pruebas se organizan bajo `tests/` en los proyectos
`GarajeMetria.Domain.Tests`, `GarajeMetria.Application.Tests`,
`GarajeMetria.IntegrationTests` y `GarajeMetria.ArchitectureTests`.

| Nivel | Decisión |
| --- | --- |
| Domain y Application | Pruebas unitarias deterministas, sin red ni base de datos; reloj controlable y dobles de los contratos cuando corresponda. |
| HTTP | Pruebas del contrato con WebApplicationFactory. |
| Persistencia | SQL Server real y aislado inicialmente; sin EF InMemory ni SQLite como sustitutos. Verificar restricciones, consultas, transacciones y concurrencia. |
| Arquitectura | Comprobaciones automatizadas de dependencias y del límite entre Api e Infrastructure. |

Cada proveedor adicional requiere pruebas de integración propias. Se utilizan
datos sintéticos, nunca recursos ni copias de datos de producción. Las pruebas
deben comprobar comportamiento e invariantes y ser proporcionales al riesgo.

El SDK, nullable, analizadores y advertencias como errores se configuran en los
archivos compartidos de la solución; `.editorconfig` define el formato.
La CI ejecuta restore, build Release, verificación de formato y tests, con los
comandos documentados en [README.md](../README.md#verificación).
Completar un tramo requiere criterios cubiertos, arquitectura preservada,
documentación y OpenAPI actualizados y comprobaciones aplicables superadas.
Las pruebas no ejecutadas se identifican como tales.

## 9. Logging transversal

Application utiliza `ILogger<T>` de .NET, sin una interfaz `ILogService` ni
dependencias de Serilog. `LoggingBehavior` aplica Decorator mediante el pipeline
de MediatR, por fuera de la validación: inicio y cancelación solicitada en Debug;
finalización en Information con operación, resultado y duración. `IOutcome`
permite consultar el resultado sin inspeccionar ni serializar los DTOs. Las
respuestas sin ese contrato se registran como `completed`, sin inferir negocio.

La API configura Serilog y el sink de archivos JSON compactos. Un ámbito HTTP
añade `RequestId` (el `traceId` de ProblemDetails) y `TraceId` de Activity. Los
casos de uso sin Activity crean una actividad propia. El middleware estándar
`UseExceptionHandler` y `ApiExceptionHandler` conservan los contratos HTTP y
registran una sola vez los errores: persistencia conocida en Warning e
inesperados en Error, solo con código y tipo seguro. Los supervisores conservan
la responsabilidad de los fallos fuera de HTTP. Cancelaciones de transporte y
respuestas iniciadas permanecen bajo el comportamiento del framework.

No se serializan solicitudes, respuestas, usuarios, matrículas, tokens,
credenciales, cookies ni objetos/mensajes de excepción. El formato JSON transforma
eventos externos con excepciones adjuntas en metadatos seguros: tipo de excepción
y causa, categoría y correlación. Descarta su mensaje, pila y propiedades
arbitrarias. Las categorías automáticas de ASP.NET Core y EF Core siguen
excluidas porque sus diagnósticos pueden contener datos sin filtrar: las
fronteras propias de HTTP, persistencia y supervisión registran esos fallos.
No se activa logging de cuerpos ni de peticiones HTTP completas.

Las excepciones capturadas y convertidas en resultados también se registran:
sondeos SQL/AD, failover y bind LDAP, validación JWT, JSON y antiforgery.
Los rechazos esperados y las cancelaciones se registran en Information, sin
convertirse en errores 500. Las excepciones propagadas se registran en su
frontera responsable, evitando duplicarlas en el pipeline. Un fallo secundario
de rollback o liberación se registra separado y no oculta el fallo principal.
`SafeExceptionLogging` es una extensión de `ILogger`, sin servicios ni interfaces
de logging adicionales. Los fallos del host antes de disponer del proveedor
se emiten como JSON seguro a stderr y terminan con código de salida 1.

Un Decorator de `IHealthDiagnostics` observa los resultados con estado compartido
por proceso: Warning al detectar indisponibilidad, Information al recuperarse y
Debug para observaciones repetidas. Cada excepción técnica capturada conserva
su propio evento en Information (también si el estado no cambia), con operación,
tipo, causa y código numérico cuando existe. Solo se observa health cuando se
consulta; no se añade un monitor periódico ni se modifica la respuesta pública.

Configuración y operación:
[guía local](configuration/local-configuration.md#logs-json-con-serilog) y
[runbook](runbooks/authentication.md#7-logs-operativos).

## 10. Evolución por tramos

RF-002 incorpora consultas administrativas de perfiles y asignación de taller
con ETag/If-Match. Application autoriza al actor desde contexto confiable y
coordina cambio, auditoría y cierre de sesión mediante la unidad de trabajo
compartida. La versión opaca no depende de rowversion. Infrastructure reutiliza
el bloqueo catálogo/usuario de RF-009 y mantiene LDAPS fuera de SQL. La auditoría
usa la limpieza y retención existentes. Contratos y operación:
[runbook de administración](runbooks/user-administration.md).

RF-009 incorpora una fuente SQL auxiliar de lectura y copia SQL local,
consulta administrativa paginada y publicación coordinada con autenticación.
Sus decisiones, límites provisionales de mapping y futuro adaptador se describen
en [DESIGN-009](design/DESIGN-009-workshop-catalog.md).

La implementación avanza por requerimientos, con su diseño y plan de tareas.
RF-001 es el primer tramo independiente; la integración corporativa de talleres
y los casos LPR se implementan en sus respectivos tramos. Sus reglas deben
definirse antes de generar el código afectado, sin inferir decisiones de negocio
desde una tecnología o un proveedor.

Se conservan las fronteras de persistencia necesarias para la independencia
del motor y se evita añadir abstracciones, bibliotecas o patrones sin una
necesidad concreta. La elección de OpenAPI y Swagger UI documentada aquí se
implementará y verificará con los contratos HTTP del tramo correspondiente.
