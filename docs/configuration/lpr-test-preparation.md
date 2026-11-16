# Preparación y operación del backend RF-003 — T003-01 a T003-19

El [runbook de ingesta](../runbooks/lpr-event-ingestion.md) incluye el flujo
sintético completo y la colección Postman sin secretos, con recuperación de
credenciales, reenvíos y límites de la evidencia automatizada.

## Dependencias y alcance

OpenIddict queda fijado en `Directory.Build.props` mediante `OpenIddictVersion`
7.7.2, común a Core/EntityFrameworkCore en Infrastructure y Server.AspNetCore/
Validation.AspNetCore/Validation.ServerIntegration en Api. Domain y Application no incorporan estos paquetes.
La preparación inicial no registró stores ni transporte OAuth. T003-06 incorpora
Core/stores; T003-10/11 integran middleware y endpoints. No cambia .NET 10
ni EF Core 10.

Consulta del 13 de noviembre de 2026: la [documentación de OpenIddict](https://documentation.openiddict.com/integrations/aspnet-core)
declara soporte para ASP.NET Core 10 y el [paquete 7.7.2](https://www.nuget.org/packages/OpenIddict.EntityFrameworkCore/7.7.2)
incluye un destino net10.0. Se conserva la versión estable prevista, sin adoptar
previews. La comprobación efectiva de dependencias usa NuGet Audit para todo el
grafo; no equivale a garantizar ausencia de vulnerabilidades futuras. Volver a
consultar los [avisos del proyecto](https://github.com/openiddict/openiddict-core/security/advisories)
y repetir auditoría al integrar OAuth o cambiar paquetes.

Restore y consulta `dotnet list GarajeMetria.slnx package --vulnerable
--include-transitive --no-restore --format json --output-version 1` completados
sin vulnerabilidades conocidas informadas. Las ocho dependencias OpenIddict
resueltas usan 7.7.2. Build Release compatible, sin advertencias.

```powershell
dotnet restore GarajeMetria.slnx --disable-parallel -p:NuGetAudit=true -p:NuGetAuditMode=all -m:1 /nr:false
```

## Entorno reproducible

`SqlFixture` crea una base `GarajeMetria_AuthTests_<UUID>` en SQL Server real y
aplica las migraciones existentes. Al terminar solo elimina esa base, con
verificación del nombre. En Windows utiliza memoria compartida local; para
otro servidor de pruebas establecer `GARAJEMETRIA_TEST_SQL` fuera del repositorio.
La cuenta necesita permiso para crear y eliminar bases en una instancia aislada.
No apuntar esa variable a recursos de producción ni copiar datos reales.

`LprTestEnvironment` prepara dos certificados RSA privados distintos, uno de
firma y otro de cifrado, con usos de clave explícitos y vigencia de dos días.
Los PFX y la contraseña aleatoria solo existen en el directorio temporal privado
de la ejecución; se eliminan al disponer el entorno. No son certificados de
despliegue y no se instalan en el almacén del equipo.

Las dos `AuthenticationFactory` reciben el mismo entorno LPR y SQL, con sus
propios hosts y scopes. Reutilizan Data Protection mediante `shared: first`.
Cada archivo privado temporal contiene `DeviceOAuth` (issuer sintético, audience,
rutas y contraseñas de certificados, duración y cuotas) y `Lpr` (ventanas,
timeouts y cuotas) según DESIGN-003. No modifica la configuración privada del
desarrollador; la variable de proceso se restaura al disponer las factories en
orden inverso. Las pruebas no deben ejecutarse en paralelo por esa variable.

`TestDirectory` sustituye AD corporativo. La prueba de preparación comprueba
ambos hosts, claves compartidas, contextos separados, migraciones existentes y
cero llamadas al directorio. No prueba todavía emisión, ingesta o revocación:
las carreras de dos instancias se completan en T003-11/17. Las nuevas opciones
se validan al arranque desde T003-08, incluidos certificados y timeouts.

```powershell
dotnet test tests/GarajeMetria.Domain.Tests/GarajeMetria.Domain.Tests.csproj --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --filter "FullyQualifiedName~LprDeviceTests|FullyQualifiedName~SpanishPlateClassifierTests"
dotnet test tests/GarajeMetria.IntegrationTests/GarajeMetria.IntegrationTests.csproj --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --filter FullyQualifiedName~LprPreparationTests
```

Son filtros focalizados; respetar la frecuencia de suite completa de AGENTS.md.
Un éxito de preparación no cierra los CP HTTP/OAuth pendientes de DESIGN-003.
Si la API local mantiene ocupadas las DLL de Release, usar
`--artifacts-path artifacts/agent-checks/rf003-build` tanto en restore como en
build/test, sin detener el proceso del desarrollador.

## Reglas de dominio entregadas

`LprDevice` mantiene taller inmutable, estados, fechas UTC y versiones separadas.
Los métodos reciben fechas y el estado de taller desde el caso de uso; no leen
reloj ni base de datos. Cambiar nombre conserva autorización; desactivar,
revocar, rotar, reactivar o invalidar por catálogo renuevan autorización.
Los no-ops conservan fechas y versiones. El dominio representa vigencia de
credenciales, pero no genera, almacena ni verifica secretos. La integración
transaccional administrativa con gestores OAuth se implementa en T003-09 sobre
la unidad de trabajo de T003-07. T003-12 incorpora la extensión del publicador
de catálogo para invalidar versiones de dispositivos en la misma transacción.

La auditoría conserva actor, operación, correlación y snapshots no secretos e
inmutables. No exige motivo. Su almacenamiento se implementará posteriormente.

## Catálogo sintáctico `es-v1`

El código sigue DESIGN-003 / 5. Referencias consultadas el 13 de noviembre:

- [RGV, anexo XVIII](https://www.boe.es/buscar/act.php?id=BOE-A-1999-1826#anxviii): composición nacional, familias especiales, prefijos provinciales y estatales.
- [Publicación histórica DGT](https://www.dgt.es/export/sites/web-DGT/.galleries/downloads/noticias/la_nueva_matricula_europea.pdf): series anteriores y prefijos antiguos PM, GE, OR, ALB, CAC, CAS, SEG y TER.
- [Tipos de matrícula, DGT](https://www.dgt.es/comunicacion/noticias/tipos-matricula): familias y distintivos autonómicos E y CGPC.

Los tests enumeran todos los prefijos documentados, con positivos y negativos,
y las familias nacional, especial, remolque, ciclomotor, histórica, turística,
temporal, diplomática, estatal y autonómica. También cubren normalización,
ceros, ambigüedad, texto original, controles, Unicode mal formado y longitud.

Las reglas diplomáticas (2–6 dígitos concatenados), estatales (VE opcional,
1–8 dígitos, hasta tres letras), autonómicas e históricas son reglas de
compatibilidad del producto, no una reproducción exhaustiva de asignaciones
administrativas. Varios formatos coincidentes producen `spanish_ambiguous`
válido. No se deduce país, periodo, color o existencia del vehículo.
Un formato no reconocido se conserva inválido; texto mal formado es error de
contrato. El original y el normalizado son sensibles; `ToString` no los muestra.
No se consulta DGT ni se corrigen caracteres OCR. No se declara reconocimiento
exhaustivo de todas las matrículas emitidas históricamente.

## Continuación T003-04 y T003-05

El dominio añade `LprEvent`: conserva captura/recepción UTC, confianza decimal,
dirección nullable y diagnóstico original de placa. `Matches` compara UUID,
dispositivo, taller ordinal, instante, texto original ordinal, confianza y
dirección, excluyendo recepción y diagnóstico generado. `LprCaptureWindow`
recibe tolerancias independientes e incluye las fronteras; el caso de uso
futuro debe consultar duplicados antes de aplicar esa ventana a eventos nuevos.

Application define comandos, queries, DTOs y contratos de repositorios, reloj,
unidad de trabajo, credenciales, auditoría, autorización y cuotas sin dependencias
HTTP/OAuth/EF. Los validadores se registran por el assembly existente; una entrada
inválida devuelve InvalidInput sin invocar el handler. La validación de JSON,
formato textual RFC 3339 y precisión antes de convertir números sigue en Api,
pendiente de T003-10/14. Los contratos ya tipados no pueden acreditar esas reglas
de transporte por sí mismos.

Los comportamientos de autorización LPR comprueban administrador vigente o
dispositivo autorizado antes de delegar. Sus lecturas iniciales no garantizan
atomicidad frente a una revocación concurrente: los handlers futuros deben
revalidar bajo locks y confirmar en ILprUnitOfWork. Ningún handler de escritura ni
endpoint LPR está implementado en ese tramo. T003-06/07 añaden los almacenes.

Pruebas focalizadas: `LprEventTests`, `LprContractsTests` y regresión
`LoggingBehaviorTests`; los dobles de Application no utilizan red ni SQL.

## Continuación T003-06 y T003-07

`LprPersistenceTests` y `LprMigrationTests` verifican la base persistente en SQL
Server real aislado. Cubren actualización con datos anteriores, FKs, checks,
UUID global único, roundtrip temporal/decimal exacto, lectura de snapshots,
versiones condicionales, consultas frescas y comparación ordinal sin reclasificar.
Se incluyen stores Core reales en una transacción común con auditoría/dispositivo,
SaveChanges interno seguido de rollback, entregas concurrentes, locks, timeout,
cancelación y pérdida de confirmación después de un commit real, sin retry.

La propiedad shadow requerida `OAuthApplicationId` de LprDevices contiene
`deviceId:N`, clave interna del cliente OpenIddict. El adaptador de credenciales
de T003-09 crea la aplicación con ese Id y con client_id `lpr_<deviceId:N>`.
No añadir una segunda aplicación al rotar/reactivar; conservar cliente e historial.

`LprPersistenceOptions` lee únicamente `Lpr:RequestTimeoutSeconds` (15) y
`LockTimeoutSeconds` (5), positivos, lock menor que total y valores convertibles
a milisegundos Int32. T003-08 completa configuración y cuotas. Un Outcome
fallido revierte todas las escrituras, también las guardadas por gestores OAuth.
El timeout interno con rollback conocido se clasifica ServiceUnavailable;
cancelación del consumidor se propaga y el commit incierto conserva su código.

El script y recuperación se describen en [migration-procedure](migration-procedure.md).
Las pruebas crean y eliminan sus propias bases; no aplicar migraciones compartidas
por ejecutar estos filtros. La limpieza de auditoría/tokens corresponde a T003-15;
el registro Core no inicia tareas de pruning ni habilita emisión OAuth HTTP.

## Configuración obligatoria desde T003-08

Antes de arrancar el nuevo binario, incorporar las secciones de
[lpr.example.json](lpr.example.json) al archivo privado externo ya usado por
`GARAJEMETRIA_PRIVATE_CONFIG`, conservando las secciones existentes. Sustituir
issuer, rutas y contraseñas por valores del entorno. Los ejemplos no contienen
claves utilizables y no se han copiado a la configuración privada del desarrollador.

Se requieren PFX RSA con clave privada, al menos 2048 bits, vigentes y fuera del
content root. Los usos de clave declarados deben permitir firma y cifrado,
respectivamente. Provisionar material de despliegue por el procedimiento del
entorno; no utilizar los certificados temporales sintéticos de tests en IIS.
Issuer debe ser HTTPS y distinto al de usuario, audience `garajemetria-lpr`.
Los límites numéricos y la coherencia timeout/lock se validan al iniciar.
No desactivar validación de TLS ni imprimir contraseñas al diagnosticar errores.

Compartir namespace LPR, opciones y material criptográfico entre instancias.
El fingerprint SQL usa opciones y thumbprints, excluyendo rutas/contraseñas.
Una discrepancia rechaza admisiones con 503, sin corregir la fila automáticamente.
Al cambiar la generación de configuración, coordinar un nuevo RateLimitNamespace
en todas las instancias; esto reinicia sus cuotas. Las políticas LPR tienen
allowlist propia y no dependen de disponibilidad de login ni de AD.

Se reutilizan las tablas de cuota existentes con particiones hash separadas.
El lock de partición mantiene el prefijo `authentication:quota:` para compartir
la exclusión con la limpieza existente. La limpieza conserva la mayor ventana
de login y LPR; no elimina eventos, dispositivos o secretos. No confundir este
ajuste de permisos de cuota con la limpieza OAuth/auditoría pendiente de T003-15.

`LprTrafficMetadata` activa el middleware de cuota IP y timeout para Token/Event;
Administration solo activa timeout. Usar las metadata al publicar las rutas en
T003-10/11/14. El clientId parseable se limitará con `lpr:tokens-client` desde
la integración OpenIddict antes de autenticar el secreto, sin un parser OAuth
alternativo. Los errores previos al handler usan JSON OAuth para token y
ProblemDetails para recursos, con Retry-After y sin datos del sujeto.

La cuota por dispositivo ya está en el pipeline Application, después de
autorización y antes del handler, también para duplicados. La cuota por usuario
administrador está antes de su transacción; no duplicarlas en Api.

## Administración desde T003-09

Los ocho handlers MediatR implementan registro, lista, detalle, renombrado,
desactivación, reactivación, rotación y revocación. La coordinación compartida
solo expone métodos internos a esos handlers; la entrada sigue siendo ISender,
con FluentValidation y autorización. Sesión, rol y versión se revalidan dentro
de la transacción; los no-ops requieren versión vigente y no cambian auditoría,
fechas o secretos. Alta/reactivación/rotación exigen taller activo confirmado.
No hay operación para trasladar o borrar un dispositivo, ni motivo obligatorio.

Los secretos tienen 32 bytes aleatorios base64url y solo se devuelven al generar.
OpenIddict gestiona hash y verificación. La revocación quita el verificador y los
permisos mediante el store oficial, manteniendo el cliente confidencial y su Id;
su gestor exige secreto/clave para actualizar normalmente un cliente confidencial.
La rotación restaura permisos y genera un hash nuevo mediante el gestor. No se
reclasifica el cliente como público y no se guarda copia recuperable del secreto.

Dispositivo, credenciales, versiones y auditoría se confirman conjuntamente.
Un fallo de gestor o auditoría revierte incluso SaveChanges internos ya ejecutados;
un commit incierto no entrega una confirmación falsa ni permite recuperar el
secreto. Las versiones cambian para la validación de tokens que conectará T003-11.
Las pruebas actuales no acreditan todavía revocación de access tokens emitidos.
No se publicaron rutas administrativas ni `/connect/token` en T003-09;
T003-10/11 las incorporan como se detalla a continuación.

## HTTP administrativo y OAuth desde T003-10/11

Antes de probar manualmente, tener aplicada `20261113115955_LprPersistence` en
la base de desarrollo y configurar los dos PFX privados, issuer HTTPS y namespace
según el ejemplo. Este tramo solo migra las bases temporales de pruebas.

1. Iniciar sesión como Administrador por el flujo RF-001 y guardar su access token.
2. Enviar `POST /api/lpr/devices` con ese Bearer, `Content-Type: application/json`
   y `{"name":"Cámara de pruebas","workshopId":"<id real del catálogo local>"}`.
   El taller debe estar activo. Guardar el `id`, `credentials.clientId`,
   `credentials.clientSecret`, `credentials.tokenEndpoint` y el encabezado ETag.
   El secreto solo se entrega en esa respuesta; custodiarlo fuera del repositorio.
3. En Postman, enviar POST a `credentials.tokenEndpoint`, con Basic Auth:
   Username = clientId y Password = clientSecret. En Body / x-www-form-urlencoded:
   `grant_type=client_credentials` y `scope=lpr.events.write`. Usar HTTPS;
   no enviar credenciales en el formulario ni reutilizar el Bearer del Administrador.
4. Comprobar 200, `token_type=Bearer`, `scope=lpr.events.write`, `expires_in`
   próximo a 900 y ausencia de refresh token/cookies. Tratar `access_token` como opaco.
5. Consultar GET `/api/lpr/devices/{id}` con el Bearer del Administrador: devuelve
   ETag y datos del dispositivo, sin secretos. Lista: GET `/api/lpr/devices`, con
   `pageNumber`, `pageSize` (máximo 100), `workshopId` e `isActive` opcionales.
6. Para rotar, enviar POST `/api/lpr/devices/{id}/credentials/rotate`, sin cuerpo,
   con Bearer del Administrador e `If-Match` igual al ETag actual, incluidas las
   comillas. Guardar el nuevo secreto y ETag. El anterior deja de emitir tokens;
   todos los tokens anteriores dejan de autenticar dispositivos entre instancias.

También existen PUT `/{id}/name` con `{"name":"Nuevo nombre"}` y POST sin cuerpo
`/{id}/deactivate`, `/{id}/reactivate`, `/{id}/credentials/revoke`, bajo
`/api/lpr/devices`. Las modificaciones exigen un ETag vigente: falta 428,
sintaxis inválida 400, versión antigua 412. Reactivar devuelve nuevas credenciales.

Solo Basic/form está habilitado en OAuth. No se publican discovery/JWKS,
introspección, registro dinámico o revocación anónima. Errores del token son JSON
OAuth; recursos administrativos usan ProblemDetails. Cuotas agotadas devuelven
429 y Retry-After, con admisión IP previa al protocolo y por cliente antes del
secreto. Las respuestas nuevas son no-store y no registran credenciales/tokens.

`LprHttpTests` comprueba el circuito con SQL real y un endpoint protegido solo
del host de pruebas: expiración, entrada revocada, certificados distintos,
tokens simultáneos, separación de usuarios, rotación/revocación entre hosts y
rotación tras preparar el grant. No publica una ruta de diagnóstico en la API.
Todavía no existe POST `/api/lpr/events`; su prueba manual llegará en T003-14.
T003-12 completa la invalidación por publicación/reactivación de taller.

## Catálogo e ingesta Application desde T003-12/13

Al publicar una desactivación, el catálogo renueva ambas versiones y UpdatedAt de
todos sus dispositivos; conserva su actividad propia, secreto, taller e historial.
La reactivación del taller permite obtener un token nuevo con las credenciales
vigentes del dispositivo, pero no recupera la validez de tokens anteriores.
Se mantienen la última copia confirmada y los efectos RF-009 sobre usuarios.

`ReceiveLprEventCommand` ya tiene handler MediatR con persistencia atómica.
Revalida autorización y expiración bajo locks, consulta UUID antes de ventana y
confirma duplicados con recepción/diagnóstico originales. Un UUID conflictivo no
revela el evento ajeno. Las lecturas de matrícula inválida y confianza cero se
conservan; no se deducen estancias. Una colisión de PK conocida y revertida solo
habilita una relectura autorizada en otra transacción, nunca una segunda inserción.
Un commit incierto requiere repetición explícita del consumidor con el mismo UUID.

`ReceiveLprEventTests` usa dobles sin SQL/red para decisiones; `LprIngestionTests`
usa SQL Server real para concurrencia entre hosts, colisiones, rollback y pérdida
de confirmación, además de publicación atómica y disponibilidad del catálogo.
`LprHttpTests` comprueba tokens reales tras publicar desactivación/reactivación.
Para ejecutar únicamente las pruebas nuevas:

```powershell
dotnet test tests/GarajeMetria.Application.Tests --configuration Release --filter FullyQualifiedName~ReceiveLprEventTests
dotnet test tests/GarajeMetria.IntegrationTests --configuration Release --filter FullyQualifiedName~LprIngestionTests
```

T003-14 añade POST `/api/lpr/events`, como se describe a continuación. El tramo
Application previo verifica el caso de uso y sus contratos de persistencia.

## Recepción HTTP y retención desde T003-14/15

Después de recompilar y reiniciar la API con el binario actualizado, enviar
POST `{{baseUrl}}/api/lpr/events` con Authorization / Bearer Token igual al
access_token del dispositivo. En Body / raw / JSON:

```json
{
  "event_id": "{{eventId}}",
  "device_id": "{{deviceId}}",
  "site_id": "{{siteId}}",
  "capture_timestamp": "{{capturedAtUtc}}",
  "plate": "0000 BBB",
  "confidence": 0.97,
  "direction": "entry"
}
```

deviceId/siteId son los del registro del dispositivo. Crear eventId como UUID
con guiones y capturedAtUtc con la fecha UTC actual, por ejemplo con
`[Guid]::NewGuid().ToString('D')` y `[DateTime]::UtcNow.ToString('O')` en PowerShell.
Guardar ambos valores antes de enviar: para un reintento conservar el JSON
original, incluida fecha y UUID. El token de Administrador no sirve para ingesta.

Primera recepción: 201 con event_id, received_at, duplicate=false y plate_status.
Repetición idéntica: 200, duplicate=true y recibo original, incluso si la captura
ya salió de ventana pero la autorización sigue vigente. Un UUID existente con
datos cambiados devuelve 409; una captura nueva fuera de ventana, 422. Matrículas
no reconocidas se conservan con plate_status=invalid y admiten la misma semántica.
No se devuelve matrícula ni Location. Las respuestas no contienen datos de eventos
ajenos. Las cuotas IP y de dispositivo también cuentan reintentos.

JSON UTF-8 estricto de hasta 8192 bytes y profundidad 4: nombres exactos,
propiedades sin repetir, confianza numérica exacta de hasta 8 posiciones tras
normalizar ceros y exponente, timestamps con zona conocida y hasta 7 decimales
de segundo. Se rechaza el exceso de precisión en lugar de redondear.

La limpieza existente incorpora auditoría de dispositivos con AuditRetentionDays
(365 inicialmente) y metadatos de access tokens con DeviceTokenRetentionHours
(24 inicialmente). El margen de token se aplica a su expiración, también en
revocados, porque no existe una fecha de revocación persistida. Fechas desconocidas,
estados/tipos ajenos, expiraciones futuras o actividad reciente no son elegibles.
Conserva eventos, dispositivos, talleres, clientes, secretos y referencias.
Usa el mismo supervisor, lease, calendario SQL y lotes; un lease vencido durante
el lote o cancelación revierte toda la transacción. No existe endpoint de limpieza.

Las pruebas focalizadas cubren HTTP con OAuth/SQL reales, precisión, límites,
errores seguros y commit incierto reconciliable, más retención con frontera de
un tick, propietarios concurrentes y rollback. No hay migración ni opciones
privadas nuevas en T003-14/15. La observabilidad y verificación transversal de
T003-16/17 se describen debajo; el runbook final sigue en T003-18.

## Observabilidad y contrato operativo desde T003-16

El ejemplo `lpr.example.json` incluye `Http:EnableApiDocumentation=false`.
Integrar esa propiedad en la sección Http existente, conservando orígenes,
proxies, certificados y Data Protection; no sustituir toda la sección por el
fragmento del ejemplo. Habilitar explícitamente OpenAPI/Swagger solo donde
corresponda. Deshabilitar documentación mantiene OAuth e ingesta operativos.
OpenIddict no publica discovery, JWKS, introspección, revocación o registro
dinámico anónimos. `/connect/token` no requiere sesión de usuario, pero sí
credenciales Basic del dispositivo.

OpenAPI diferencia Basic/form para emisión, OAuth `lpr.events.write` para
eventos y Bearer de Administrador para dispositivos. Describe Cache-Control,
Pragma, WWW-Authenticate, Retry-After, Location de alta y ETag/If-Match. CORS
mantiene orígenes HTTPS explícitos y métodos GET/POST/PUT/OPTIONS; permite
If-Match y expone ETag, Location y Retry-After, sin orígenes comodín.

El meter `GarajeMetria.Lpr` emite instrumentación estándar .NET:

| Instrumento | Unidad | Uso |
| --- | --- | --- |
| `garajemetria.lpr.requests` | `{request}` | Intentos HTTP y resultados, incluidos rechazos anteriores al handler. |
| `garajemetria.lpr.request.duration` | `s` | Duración completa, incluidas cuotas, autenticación y persistencia. |

Etiquetas acotadas: traffic (`token`, `event`, `administration`), outcome
(`success`, `accepted`, `duplicate`, `conflict`, `outside_window`, `invalid`,
`unauthorized`, `forbidden`, `rate_limited`, `unavailable`, `failed`, `cancelled`),
status_code y plate_status (`valid`, `invalid`, `none`). No se etiquetan IDs,
talleres, clientId, IP, matrículas o tokens. `accepted`/`duplicate` proceden del
recibo; los demás valores describen resultados HTTP. Los preflight y rechazos
también cuentan. El colector del entorno puede suscribirse al meter; no se añade
un endpoint público de métricas.

El log `LPR request completed` registra resultado y latencia con la correlación
existente, sin cuerpos, credenciales o matrículas. Se mantiene el filtro absoluto
de categorías OpenIddict, IdentityModel, EF y ASP.NET Core potencialmente sensibles,
incluso si aumenta su verbosidad.

El diagnóstico SQL comprueba acceso a tablas y columnas requeridas de los siete
almacenes LPR/OAuth mediante proyecciones de cero filas, dentro del timeout de
cinco segundos. Una conexión válida con una migración o permiso requerido ausente
ya no aparece como SQL correcto. Health solo muestra estado; su log seguro omite
SQL, esquema y cadena de conexión.

## Carreras y trazabilidad T003-17

`LprConcurrencyTests.cs` comparte la clase parcial `LprHttpTests` y sus dos
hosts WebApplicationFactory independientes. Las barreras de guardado y señales
de entrada a `sp_getapplock` explicitan qué transacción espera: duplicado HTTP,
expiración durante espera y ambos órdenes entre catálogo e ingesta. La emisión
se pausa tras preparar el grant para rotar, revocar o desactivar/reactivar el
taller antes de firmar; no se actualiza el grant con una versión nueva.
Una rotación con commit sin acuse se recupera con GET/ETag y otra rotación,
sin recuperar el secreto perdido.

La trazabilidad CP-001 a CP-018 y las regresiones se registran en TASKS-003.
La suite completa respeta la frecuencia Atlantic/Canary de AGENTS.md y registra
su resultado en artifacts/agent-checks/full-suite.json. Esto no acredita hardware,
AD corporativo, restauración física ni despliegue.
