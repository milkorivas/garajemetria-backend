# DESIGN-003 — Recepción de eventos LPR

| Campo | Valor |
| --- | --- |
| Requerimiento | [RF-003](../requirements/RF-003-lpr-event-ingestion.md) |
| Estado | Diseño técnico para implementación; código y verificaciones pendientes |
| Plataforma | C# 14, .NET 10, ASP.NET Core, EF Core y SQL Server |
| Dependencias | RF-001, RF-002 y RF-009; interpretación posterior en RF-004 |
| Plan de tareas | [TASKS-003](../tasks/TASKS-003-lpr-event-ingestion.md), redactado; ejecución pendiente |

## 1. Alcance y decisiones

Este documento concreta contratos, aislamiento, persistencia y pruebas de
RF-003. RN y CA se refieren a ese requerimiento. No implementa la interfaz del
panel, imágenes, consultas de historial ni cálculo de estancias. No implica
que las clases y rutas aquí propuestas ya existan.

La base inspeccionada contiene `SessionBearerHandler`, `AuthenticationDbContext`,
`SqlTransactions`, catálogo local y administración de usuarios. Se reutilizan
sus fronteras de persistencia, catálogo y sesión administrativa. No reutilizar
el JWT de usuario para dispositivos ni introducir dependencias EF/HTTP/OAuth
en Domain o Application.

Decisiones técnicas nuevas:

- OpenIddict como servidor OAuth y validación local, con persistencia EF.
- Un cliente confidencial OAuth por dispositivo; solo Client Credentials.
- Access token protegido por OpenIddict y ligado a una versión persistida de
  autorización del dispositivo. Sin cookies, refresh token ni sesión única.
- Escrituras coordinadas con `workshops:catalog`, como las operaciones actuales.
- UUID global único de evento; confirmación de duplicados sin efectos adicionales.
- Datos LPR inmutables, incluido texto original de matrícula y diagnóstico de formato.

## 2. OAuth y separación de identidades

### 2.1. Biblioteca y dependencias

Usar OpenIddict 7.7.2 como referencia estable de este diseño, con todos sus
paquetes en la misma versión. Su documentación declara soporte ASP.NET Core
10.0. Antes del primer restore de implementación, comprobar compatibilidad y
avisos de seguridad; si hay que cambiar esta referencia, registrar la versión
estable elegida, sin incorporar previews ni cambiar .NET 10 o EF Core 10.

Api referencia las integraciones de servidor y validación ASP.NET Core de
OpenIddict; Infrastructure referencia Core y EntityFrameworkCore. Registrar
Core/stores dentro de `AddInfrastructure`; Api configura transporte, pipeline,
claims y esquemas. Usar los gestores OpenIddict solo en Infrastructure para
clientes y secretos. Application trabaja con DTOs y resultados propios.
No añadir ASP.NET Identity, login interactivo ni un segundo servidor externo.

Mantener habilitados almacenamiento y validación de entradas de token:
`UseLocalServer()` y `EnableTokenEntryValidation()`. El control de versión de
3.2 es adicional: la biblioteca no invalida por sí sola todos los tokens al
cambiar un secreto de cliente. Deshabilitar caché de entidades OpenIddict para
no aceptar secretos o estados antiguos entre instancias. Verificar que las
consultas críticas no reutilicen entidades ya seguidas con estado obsoleto.

Referencias: [servidor](https://documentation.openiddict.com/guides/getting-started/creating-your-own-server-instance),
[ASP.NET Core](https://documentation.openiddict.com/integrations/aspnet-core),
[almacenamiento y validación de tokens](https://documentation.openiddict.com/configuration/token-storage),
[paquete estable](https://www.nuget.org/packages/OpenIddict.EntityFrameworkCore/7.7.2).

### 2.2. Endpoint de token

`POST /connect/token`, exclusivamente HTTPS. No aceptar redirección HTTP como
sustituto de enviar credenciales por HTTPS. El endpoint queda fuera de la
autenticación de sesión de usuario, pero exige autenticación OAuth del cliente.
No utiliza antiforgery ni cookies. No permitir Client Credentials en un frontend
público; Postman o Bruno actúan como cliente de pruebas con secreto privado.

```http
POST /connect/token
Authorization: Basic <base64 de client_id:client_secret según OAuth>
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials&scope=lpr.events.write
```

Solo `client_secret_basic`; rechazar credenciales en URL, JSON o una segunda
fuente simultánea. Delegar parsing y autenticación de protocolo en OpenIddict.
El único scope permitido es `lpr.events.write`; si se omite, asignar ese scope
por defecto al único cliente autorizado, y devolverlo explícitamente. No
aceptar `offline_access`, `openid`, password, refresh o authorization code.
Conceder solo permisos OpenIddict de endpoint token, grant Client Credentials
y ese scope. No ofrecer registro dinámico, introspección pública ni revocación
anónima. Discovery/JWKS, si los publica la integración, solo exponen metadatos
y claves públicas; deben constar en OpenAPI/operación como accesos anónimos.

Respuesta 200, `Cache-Control: no-store`, `Pragma: no-cache`:

```json
{
  "access_token": "<token protegido por OpenIddict>",
  "token_type": "Bearer",
  "expires_in": 900,
  "scope": "lpr.events.write"
}
```

`expires_in` refleja la configuración efectiva; 900 segundos es el valor inicial.
No devolver refresh token, id token, secretos ni datos de usuario.

| Situación | Respuesta OAuth |
| --- | --- |
| Cliente desconocido, secreto incorrecto/revocado, dispositivo o taller inactivo | 401 `invalid_client`, descripción genérica y `WWW-Authenticate: Basic` |
| Petición de protocolo mal formada | 400 `invalid_request` |
| Grant distinto del permitido | 400 `unsupported_grant_type` |
| Scope ajeno | 400 `invalid_scope` |
| Cuota agotada | 429, JSON `error=temporarily_unavailable` y `Retry-After` |
| SQL/catálogo local temporalmente no verificable | 503, JSON `error=temporarily_unavailable` |
| Fallo inesperado o resultado de emisión indeterminado | 500, JSON `error=server_error` |

Los 429/503 son convenciones operativas explícitas de este endpoint. No envolver
estos errores en ProblemDetails ni revelar si existe el cliente. Un fallo AD
no afecta a la autenticación autónoma de dispositivos. Los errores de recursos
y administración sí siguen ProblemDetails. Aplicar los errores OAuth también
si falla el middleware antes de llegar al handler.

### 2.3. Tokens y esquemas

Usar el formato protegido predeterminado de OpenIddict: JWT firmado y cifrado,
con validación local en la propia API. El consumidor debe tratar el token como
opaco. Configurar certificados RSA distintos para firma y cifrado, separados
de HTTPS y de las claves JWT de RF-001; mismos certificados en todas las
instancias. Issuer HTTPS explícito y audiencia exclusiva `garajemetria-lpr`.
No claves efímeras ni certificados de desarrollo automáticos en IIS.
Rotar claves con solapamiento de validación durante la vida máxima de tokens
emitidos. No cambiar secretos de dispositivos al rotar claves del servidor.

Claims mínimos: `sub` = deviceId, identificador de cliente del protocolo,
`aud`, `iss`, `iat`, `exp`, identificador del token, scope,
`device_auth_version` y `site_id`. Sin `role`, `sid` de usuario, matrícula o secreto.
El transporte exacto de claims internos lo controla OpenIddict; no implementar
un firmador o cifrador alternativo. Destinar al access token solo los claims
necesarios. Validar expiración sin ampliar la vigencia mediante clock skew.

Conservar el esquema `Bearer` de RF-001 como predeterminado para usuarios.
Ingesta exige exclusivamente el esquema OpenIddict de dispositivos y política
`LprEventWriter`; administración exige exclusivamente `Bearer` y rol
`administrator`. No seleccionar esquema a partir de claims de un token sin
validar. Excluir `/connect/token` y las rutas LPR del `SessionBearerHandler` y
del middleware de disponibilidad de usuarios cuando proceda, mediante metadata
explícita, sin excluir controles de SQL propios de LPR. Verificar que el esquema
predeterminado no consulte sesiones de usuario para un token LPR.

## 3. Modelo e invariantes

### 3.1. Entidades y límites

| Concepto | Datos principales |
| --- | --- |
| `LprDevice` | `Id` UUID, `Name`, `WorkshopId` inmutable, `IsActive`, `HasCredentials`, `AuthorizationVersion` UUID, `Version` UUID para concurrencia, fechas UTC de alta/cambio. |
| Cliente OpenIddict | Relación 1:1 con dispositivo, clientId único estable, tipo confidencial, verificador de secreto y permisos. Nunca borrar al desactivar. |
| `LprEvent` | `EventId` UUID global, `DeviceId`, `WorkshopId`, `CapturedAt`, `ReceivedAt`, `PlateOriginal`, `PlateNormalized`, `PlateStatus`, `PlateFormat`, `PlateRulesVersion`, `Confidence`, `Direction`. |
| `DeviceAdministrationAuditEntry` | UUID, administrador local, dispositivo, operación, fecha UTC, versión anterior/nueva, snapshot no secreto y correlación. Sin motivo obligatorio. |

`Version` cambia en toda modificación efectiva, incluida revocación automática
por catálogo. `AuthorizationVersion` cambia solo cuando hay que invalidar tokens:
desactivar, reactivar, rotar, revocar o publicar la desactivación de su taller.
Cambiar nombre no revoca. Crear inicializa ambas con valores nuevos.

El secreto se genera con `RandomNumberGenerator`, 32 bytes aleatorios codificados
base64url, y se entrega una sola vez. OpenIddict crea y verifica su hash con su
gestor; no guardar una segunda copia, hash paralelo, secreto cifrado recuperable
ni snapshot con el secreto. `client_id` = `lpr_` más deviceId en formato `N`;
permanece estable en rotación/reactivación. Secretos nuevos no reutilizan valores.

### 3.2. Estados y revocación inmediata

| Operación | Precondición | Efecto |
| --- | --- | --- |
| Alta | Taller existente/activo y copia confirmada | Dispositivo activo, credenciales nuevas y auditoría. |
| Renombrar | Registro existente y versión vigente | Cambiar nombre y Version; si no cambia, no hay efecto ni auditoría nueva. |
| Desactivar | Versión vigente | `IsActive=false`, retirar secreto y marcar credenciales revocadas; cambiar ambas versiones y auditar. Repetir ya inactivo es sin efecto. |
| Revocar | Versión vigente | Retirar secreto y `HasCredentials=false`; cambiar ambas versiones y auditar si aún había credenciales. No cambia el taller. |
| Rotar | Dispositivo y taller activos, versión vigente | Nuevo secreto, `HasCredentials=true`, ambas versiones nuevas y auditoría; permite recuperar un dispositivo activo sin secreto. |
| Reactivar | Dispositivo inactivo, mismo taller activo, versión vigente | Activo, nuevo secreto, versiones nuevas y auditoría. Si ya está activo, 409; usar rotación. |

En cada solicitud LPR, además de validación OpenIddict, comprobar en SQL:
dispositivo activo, credenciales vigentes, versión igual al claim, mismo taller
y taller activo en catálogo confirmado. Fallo SQL: 503, nunca fallback permisivo.
Repetir esas comprobaciones dentro de la transacción de ingesta, incluida
expiración del token respecto del instante de decisión si hubo espera por locks.
Un cambio
confirmado antes de esa comprobación impide persistir; un evento confirmado
antes de la revocación sigue siendo histórico válido.

En una publicación RF-009 que desactiva talleres, cambiar `AuthorizationVersion`
y `Version` de todos sus dispositivos en la misma transacción del catálogo.
No cambiar la asociación, no borrar secretos ni poner `IsActive=false` por ese
motivo. Tras reactivar el taller se pueden obtener tokens nuevos si el dispositivo
sigue activo y con credenciales; los anteriores no recuperan validez. Esta
extensión del publicador es obligatoria en implementación de RF-003.

## 4. Contratos de recursos

### 4.1. Reglas comunes

HTTPS, `Cache-Control: no-store` y DTOs explícitos. JSON UTF-8, objeto raíz,
sin comentarios, propiedades desconocidas, duplicadas o con casing alternativo.
Rechazar números como strings, null en campos obligatorios y contenido sobrante.
No incluir valores sensibles en mensajes de validación. Limitar cuerpo a 8 KiB
en ingesta y escrituras administrativas; token a 4 KiB; profundidad JSON a 4.
413 al exceder bytes; 415 para un content-type no admitido. No body logging.

| Campo | Validación |
| --- | --- |
| event_id | UUID canónico con guiones, no Nil ni Max; aceptar versiones estándar RFC 9562, sin obligar a v4. Comparar UUID parseado. |
| device_id | UUID del registro en formato con guiones; comparar valor parseado. |
| site_id | String de 1–256 unidades UTF-16, id interno exacto de RF-009, sin normalizar códigos corporativos. |
| capture_timestamp | RFC 3339, `T` y `Z` mayúsculas o offset explícito conocido; rechazar `-00:00`, fechas sin zona y segundos intercalares no representables. Hasta 7 decimales de segundo; no truncar precisión excedida. |
| plate | String de 1–32 unidades UTF-16; rechazar si solo contiene espacios o separadores; no aceptar caracteres de control. Un formato no español se conserva inválido. |
| confidence | Decimal JSON finito en [0,1], representable exactamente con hasta 8 decimales significativos tras la coma. No redondear ni convertir a double. |
| direction | `entry`, `exit`, null o ausente. Otros valores: 400. |
| name administrativo | String de 1–128 unidades UTF-16 tras trim, no blanco ni caracteres de control. |

Los límites técnicos se fijan aquí; un cambio futuro requiere revisar contrato,
mapping y OpenAPI conjuntamente. Tiempo de recepción único obtenido del reloj
UTC compartido de SQL al comenzar la decisión transaccional, reutilizado para
ventana y registro. Application recibe el instante mediante contrato de reloj
transaccional; en pruebas usa uno determinista, sin SQL ni reloj global.

### 4.2. Ingesta

`POST /api/lpr/events`, Bearer de dispositivo con `lpr.events.write`.

```json
{
  "event_id": "8d663ca0-cb10-4b41-8dfb-c850d61637dc",
  "capture_timestamp": "2026-10-30T10:25:43.125Z",
  "device_id": "43cc450c-1431-4e9b-bb18-43fe078e927c",
  "site_id": "00000000000000000000000000000001",
  "plate": "1234 BCD",
  "confidence": 0.97,
  "direction": "entry"
}
```

Primera persistencia: 201. Repetición idéntica: 200. Ambos cuerpos usan snake_case:

```json
{
  "event_id": "8d663ca0-cb10-4b41-8dfb-c850d61637dc",
  "received_at": "2026-10-30T10:25:44.0000000Z",
  "duplicate": false,
  "plate_status": "valid"
}
```

En duplicado: `duplicate=true`, fecha y estado originales. Una matrícula no
reconocida produce `plate_status=invalid` y la misma semántica 201/200, no 400.
No devolver matrícula ni crear un GET de historial dentro de RF-003. No fabricar
Location hacia una ruta inexistente.

Orden: autenticar/autorizar, validar estructura, admitir cuota, abrir transacción,
revalidar autorización y taller, obtener recepción, consultar UUID, comparar si
existe, aplicar ventana solo si es nuevo, clasificar matrícula, insertar y commit.
La ventana es inclusiva: `receivedAt - MaxAge <= capturedAt <= receivedAt + MaxFuture`.
El evento puede llegar fuera de orden dentro de ella. No hay watermark ni
reordenación de estancias en este tramo. Comparación de reintento según RF-003 / 4.2:
ordinal del texto original decodificado, decimal exacto, instante UTC y dirección
nullable; no volver a clasificar con reglas de matrícula más recientes.

| Resultado | Estado y código ProblemDetails |
| --- | --- |
| Token ausente, inválido, vencido o versión revocada | 401 `invalid_device_token`, challenge Bearer |
| Scope insuficiente o device/site no autorizados | 403 `device_forbidden` |
| Taller publicado inactivo para un token criptográficamente válido | 403 `workshop_inactive`; si versión ya cambió, prevalece 401 |
| Entrada estructural inválida | 400 `invalid_input` |
| Captura nueva fuera de ventana | 422 `capture_outside_window` |
| UUID existente con datos distintos | 409 `event_conflict` |
| Cuota agotada | 429 `rate_limited`, Retry-After |
| SQL temporalmente inaccesible, copia inicial ausente o lock timeout | 503 `service_unavailable` |
| Commit indeterminado | 500 `operation_outcome_unknown` |
| Fallo inesperado | 500 `internal_error` |

No exponer si el UUID pertenece a otro taller, su matrícula o fecha: conflicto
genérico, sin datos del evento ajeno. La comparación se hace detrás del contrato
de persistencia y la respuesta exitosa solo puede referirse al propio dispositivo.

### 4.3. Administración

Rutas autenticadas con sesión de Administrador, sin añadir motivo obligatorio.
Cuerpos y respuestas administrativos usan camelCase como RF-002/RF-009.

| Método/ruta | Entrada | Éxito |
| --- | --- | --- |
| GET /api/lpr/devices | pageNumber=1, pageSize=20 (máximo 100), workshopId e isActive opcionales | 200 página |
| GET /api/lpr/devices/{id} | UUID | 200 detalle y ETag |
| POST /api/lpr/devices | `{ "name": "Acceso principal", "workshopId": "..." }` | 201 detalle, credenciales y ETag; Location al detalle |
| PUT /api/lpr/devices/{id}/name | `{ "name": "Acceso lateral" }`, If-Match | 204 y ETag |
| POST /api/lpr/devices/{id}/deactivate | Sin cuerpo, If-Match | 204 y ETag |
| POST /api/lpr/devices/{id}/reactivate | Sin cuerpo, If-Match | 200 detalle, credenciales nuevas y ETag |
| POST /api/lpr/devices/{id}/credentials/rotate | Sin cuerpo, If-Match | 200 credenciales nuevas y ETag |
| POST /api/lpr/devices/{id}/credentials/revoke | Sin cuerpo, If-Match | 204 y ETag |

Detalle: `id`, `name`, `workshopId`, `isActive`, `hasCredentials`, `createdAt`,
`updatedAt`. Página: `items`, `pageNumber`, `pageSize`, `totalCount`, orden fijo
por id ascendente, offset acotado a Int32. No filtros o parámetros repetidos o
desconocidos. Incluir activos e inactivos por defecto, página fuera de total vacía.
Los detalles no requieren que el taller siga activo ni leer el origen externo.

Credenciales solo en alta/rotación/reactivación: `clientId`, `clientSecret`,
`tokenEndpoint`, `scope`. En alta/reactivación, objeto `device` más `credentials`;
en rotación, objeto `credentials`. Nunca en GET ni en errores, cachés o auditoría.

ETag fuerte entre comillas con `Version` UUID formato N; If-Match exige un único
ETag fuerte exacto, no `*`, listas ni ETags débiles. Falta: 428; sintaxis inválida:
400; versión obsoleta: 412. Validar versión antes de un no-op; un no-op vigente
no cambia versión, fecha, auditoría ni tokens. Estado incompatible: 409.
404 recurso inexistente, 409 taller inactivo al crear/reactivar/rotar, 503 sin
copia inicial para comprobar taller activo. Autorizar antes de revelar existencia.
Para renombrar/desactivar/revocar no es necesario que el taller esté activo.

Un secreto cuya respuesta se pierda no puede recuperarse. Tras resultado incierto,
consultar detalle/ETag y ejecutar una rotación explícita si hace falta; no
reproducir automáticamente POST de alta o rotación. Alta sin respuesta requiere
localizar registro por lista y contexto administrativo; el nombre no es único ni
identifica de forma fiable un dispositivo físico. Documentar este procedimiento
en el futuro runbook. No generar un segundo dispositivo de forma automática.

## 5. Matrículas

La validación es sintáctica, sin consultar DGT ni probar que exista la matrícula.
Normalizar con mayúsculas invariantes y quitar separadores espacio Unicode y
guion ASCII `-`; conservar original, ceros y el resto de caracteres. No quitar
el distintivo `E` por heurística: podría ser un prefijo legítimo. Solo el número
de matrícula, sin eurobanda, distintivo H ni bandas de caducidad, es entrada esperada.

Clasificador puro Domain, versionado como `es-v1`. Ejecutar reglas ancladas a
cadena completa, sin backtracking ilimitado. Salida `valid`/`invalid`, familia
o null y versión. Si varias familias encajan, conservar `spanish_ambiguous`;
no adivinar provincia o categoría de vehículo. No fusionar identidades por familia.

Catálogo de familias, basado en [anexo XVIII del RGV](https://www.boe.es/buscar/act.php?id=BOE-A-1999-1826#anxviii):

| Familia | Composición normalizada |
| --- | --- |
| Ordinaria nacional | 4 dígitos y 3 letras de `BCDFGHJKLMNPRSTVWXYZ`. |
| Especial, remolque, ciclomotor | Prefijo E, R o C y composición nacional. |
| Histórica y turística | H o T y composición nacional. |
| Temporal | P, S o V y composición nacional; incluye uso museográfico bajo permiso S. |
| Diplomática | CD, CC, OI o TA y dos grupos numéricos; compatibilidad sintáctica de 2–6 dígitos totales tras quitar separadores. |
| Estatal/militar | ET, FN, EA, MF, MMA, PME, CNP, PGC o FAE; variante VE y secuencia de identificación. |

Reglas de compatibilidad adicionales del producto, que no pretenden verificar
asignaciones oficiales: para estatal/militar aceptar prefijo, VE opcional,
1–8 dígitos y hasta 3 letras finales ASCII. Para policías autonómicas aceptar
E más 1–8 dígitos, y CGPC más 1–8 dígitos con hasta 3 letras finales. Exigir
al menos un dígito. No aceptar cualquier prefijo desconocido por ser alfabético.
La [DGT](https://www.dgt.es/comunicacion/noticias/tipos-matricula) describe familias
ordinarias históricas y distintivos de cuerpos estatales y autonómicos.

Provinciales: prefijo de la tabla II.A del anexo y 1–6 dígitos (numérica), o
prefijo, 4 dígitos y 1–2 letras ASCII (alfanumérica). Para estas series no
reutilizar la restricción moderna de consonantes: el conjunto histórico es
distinto. Versionar los prefijos como datos estáticos Domain, sin base de datos.
El conjunto de prefijos actuales de referencia es:

```text
A AB AL AV B BA BI BU C CA CC CE CO CR CS CU GC GI GR GU
H HU IB J L LE LO LU M MA ML MU NA O OU P PO S SA SE SG
SO SS T TE TF TO V VA VI Z ZA
```

Añadir las denominaciones antiguas documentadas en la
[publicación histórica de DGT](https://www.dgt.es/export/sites/web-DGT/.galleries/downloads/noticias/la_nueva_matricula_europea.pdf),
incluidos PM, GE, OR y ALB, CAC, CAS, SEG, TER; no sustituirlas automáticamente
por códigos actuales. Los prefijos de tres letras se aplican a la serie numérica
histórica; PM, GE y OR admiten ambas series. Cubrir todos los prefijos con tests
por fila antes de dar CA-012 por verificado. No reproducir las erratas de nombres
de provincia de la publicación histórica; comparar identificadores, sin fabricar
correspondencias geográficas a partir de una lectura.

Las reglas históricas de compatibilidad admiten sintaxis, no prueban periodo de
emisión, provincia efectiva ni legalidad de circulación. El color y las bandas
de una placa no se deducen del texto. Una placa extranjera que coincida con una
sintaxis española no puede distinguirse sin un dato de país que RF-003 no recibe.

Cualquier lectura no reconocida se conserva `invalid`; nunca perder un evento
por un catálogo incompleto. Una ampliación versiona las reglas para nuevos
eventos y no reclasifica históricos ni reintentos. La verificación completa de
familias y prefijos es parte del trabajo de implementación, sin declarar hoy
validado el reconocimiento exhaustivo de todas las variantes históricas.

## 6. Capas, contratos y transacciones

| Capa | Responsabilidad nueva |
| --- | --- |
| Domain | LprDevice, LprEvent, normalización/clasificación, invariantes de asociación y estados, comparación y ventana con instantes suministrados. Sin paquetes. |
| Application | Comandos/consultas MediatR, validadores, autorización, decisiones de negocio y coordinación de unidad de trabajo. Sin ClaimsPrincipal, OpenIddict, EF o tipos HTTP. |
| Infrastructure | Stores y gestores OAuth, repositorios, reloj SQL, cuotas, mappings, migraciones, auditoría y retención. |
| Api | Lectores estrictos, esquemas, pipeline OpenIddict, proyección de claims validados, DTOs y errores/OpenAPI. Sin DbContext en endpoints. |

Contratos Application previstos: `ILprDeviceRepository`, `ILprEventRepository`,
`ILprDeviceAdministrationQuery`, `IDeviceAdministrationAuditRepository`,
`IDeviceCredentialService`, `IDeviceAuthorizationQuery`, `ILprUnitOfWork`,
`ILprClock` e `ILprRateLimitStore`. Reutilizar `ICurrentUserContext` para el
Administrador e introducir `ICurrentDeviceContext` para identidad verificada.
No exponer IQueryable, objetos OpenIddict o SQL. Mantener contratos concretos,
sin repositorio genérico. Agrupar contratos si la implementación permite hacerlo
sin mezclar decisiones con adaptadores.

Comandos: RegisterDevice, RenameDevice, DeactivateDevice, ReactivateDevice,
RotateDeviceCredentials, RevokeDeviceCredentials, ReceiveLprEvent y
PrepareDeviceTokenGrant. Queries: ListDevices, GetDevice, ValidateDeviceAccess.
Todos pasan por validación asíncrona MediatR antes de handlers. La validación de
protocolo OAuth sigue en su biblioteca; su adaptación no sustituye validación
de Application. No incluir secretos en ToString, logging behavior o excepciones.

### 6.1. Orden de coordinación

Reutilizar el contexto local `AuthenticationDbContext` y `SqlTransactions`,
con mappings separados y sin renombrar el contexto en este tramo. Su nombre
histórico no obliga a duplicar talleres ni crear otra conexión transaccional.

Orden global: `workshops:catalog`, usuario administrador cuando proceda,
`lpr:device:{id:N}` y `lpr:event:{eventId:N}`. Usar locks transaccionales con
timeout finito; `sp_getapplock` solo en Infrastructure. Ninguna ruta adquiere
catálogo después de dispositivo o evento. El publicador ya posee catálogo y
actualiza versiones en orden estable. Es conservador para 20–40 eventos/minuto;
medir contención antes de afinar granularidad.

En administración revalidar sesión/rol local dentro de la transacción y
confirmar dispositivo, gestor de credenciales y auditoría conjuntamente. Los
stores OpenIddict deben usar exactamente el mismo DbContext scoped y transacción;
un SaveChanges interno no confirma una transacción distinta. No transacciones
ambientales ni SQL abierto durante AD u otra red externa. Reloj compartido para
auditoría; versión opaca de aplicación, sin rowversion en contratos.

En emisión, OpenIddict valida el protocolo y después PrepareDeviceTokenGrant
revalida secreto y estado actual bajo lock; la segunda comprobación usa el
gestor, sin entidad/caché antigua. Devuelve un DTO con dispositivo, taller,
versión y expiración; Api lo convierte a principal del protocolo. Si hay rotación
entre preparar y firmar, el token conserva la versión anterior y será rechazado.
Nunca releer la versión nueva para conceder acceso a credenciales anteriores.
No prometer que un token siga utilizable si se revoca concurrentemente a su emisión.

En ingesta el lock de UUID serializa su comprobación/inserción entre instancias;
índice único global es la defensa persistida adicional. Si ocurre violación de
unicidad conocida pese al lock, abortar y releer en una nueva transacción con
autorización vigente, clasificar duplicado/conflicto; no convertir cualquier
DbUpdateException en duplicado. Nada invoca RF-004, envía mensajes o crea una
estancia todavía; no hace falta outbox sin un efecto externo acordado.

### 6.2. Fallos y resultado incierto

Reutilizar clasificación de errores de `SqlTransactions` y DESIGN-001 / 4.3.1:
timeout/deadlock con rollback conocido no implica escritura; pérdida de respuesta
de commit puede haber confirmado. No reintentos ciegos de EF ni éxito anticipado.
Una repetición explícita de ingesta con el mismo UUID reconcilia el resultado.
Administración con secreto perdido usa detalle/ETag y rotación, nunca recuperación
del secreto. Emisión fallida puede volver a solicitar un token: varios tokens
simultáneos están permitidos y siguen sujetos a versión vigente.

## 7. Persistencia y migraciones

| Tabla | Mapping y restricciones principales |
| --- | --- |
| LprDevices | UUID PK, WorkshopId longitud 256 FK restrict a WorkshopAccess, Name nvarchar(128), estados y UUIDs de versiones obligatorios, fechas datetimeoffset(7) UTC; índice WorkshopId/IsActive e índice único del vínculo OAuth. |
| LprEvents | EventId UUID PK sin generación en servidor, DeviceId FK restrict, WorkshopId FK restrict, CapturedAt/ReceivedAt datetimeoffset(7) UTC, PlateOriginal/Normalized nvarchar(32), Confidence decimal(9,8) con check [0,1], Direction nullable (entry/exit), PlateStatus (valid/invalid), PlateFormat nullable hasta 64 y PlateRulesVersion hasta 32. |
| DeviceAdministrationAudits | UUID PK, ActorId y DeviceId FKs restrict, Operation hasta 32, correlación hasta 128, snapshots tipados de valores no secretos y fecha datetimeoffset(7); índice OccurredAt/Id para limpieza y DeviceId/OccurredAt. |
| Tablas OpenIddict | Entidades y mappings oficiales en el mismo contexto, applicationId único por dispositivo, cliente confidencial y permisos limitados. Token storage habilitado, sin payload recuperable de secreto. |

Índices de eventos: `(WorkshopId,CapturedAt,EventId)` y
`(DeviceId,CapturedAt,EventId)`. No indexar PlateOriginal ni añadir índices de
búsqueda de historial antes de RF-005. Asegurar igualdad ordinal de textos
originales en comparación de duplicados, sin depender de colación SQL. Proyectar
solo el evento identificado para comparar, nunca una carga masiva. No lazy loading.

Un evento siempre referencia su taller de dispositivo: FK compuesta
`(DeviceId,WorkshopId)` a clave alternativa `(Id,WorkshopId)` en LprDevices.
Checks de fechas UTC, estados, versión de reglas y coherencia de formato/status;
restricciones SQL solo en Infrastructure. Clasificación no deducida por SQL.
No FK con borrado en cascada desde clientes/tokens hacia dispositivos o eventos.

Migración aditiva nueva con tablas/índices; no modificar migraciones aplicadas
ni sembrar credenciales conocidas o cámaras ficticias en producción. No aplicar
al arrancar. Publicador RF-009, emisión y autorización deben desplegarse juntos
para que no haya una ventana sin invalidación por catálogo. Revisar migración y
preparar backup/restauración aislada. Un rollback que elimine tablas nuevas es
destructivo y requiere preservar datos; deshabilitar rutas no implica borrarlos.
Incorporar migración y snapshot con `--context AuthenticationDbContext`.

## 8. Configuración, cuotas y conservación

Opciones tipadas obligatorias en archivo privado externo, sin datos reales en
ejemplos. Valores iniciales del diseño, configurables y validados al arranque:

| Grupo/opción | Valor inicial y validación |
| --- | --- |
| Lpr:MaxEventAgeMinutes / MaxFutureMinutes | 30 / 30; enteros entre 0 y 1440 como límite operativo de esta versión. |
| DeviceOAuth:AccessTokenMinutes | 15; positivo, máximo operativo 60. |
| DeviceOAuth:Issuer / Audience | Issuer HTTPS de despliegue; audience exclusiva garajemetria-lpr. |
| DeviceOAuth:SigningCertificate / EncryptionCertificate | Rutas privadas/certificados RSA con clave privada, vigentes y comunes a instancias; sin valores versionados. |
| Lpr:RequestTimeoutSeconds / LockTimeoutSeconds | 15 / 5; positivos, lock menor que timeout total. |
| Lpr:RateLimitWindowSeconds | 60; ventana exacta compartida en SQL. |
| Lpr:EventsPerDevice / EventsPerIp | 120 / 1200 por ventana. |
| DeviceOAuth:TokensPerClient / TokensPerIp | 10 / 120 por ventana. |
| Lpr:AdminWritesPerUser | 30 por ventana. |

Son límites de protección iniciales, no objetivos de capacidad prometidos.
Reutilizar el patrón SQL de cuotas, ampliando contratos/options sin acoplar LPR
a nombres de política de login. Namespaces separados `lpr:*`, reloj SQL y
fingerprint de configuración común entre instancias; desacuerdo o SQL fallido
produce 503. Verificar en pruebas la ampliación, porque el RateLimitStore actual
solo reconoce las políticas de autenticación de usuarios.

La cuota IP previa protege token/ingesta antes de hashing costoso. La cuota por
cliente se aplica al clientId parseable (sin distinguir existencia en respuesta),
y la de dispositivo tras autenticar en ingesta. Los duplicados consumen cuota.
No esperar en cola en memoria; Retry-After redondeado hacia arriba. No reservar
locks de catálogo mientras se solicita cuota; cuotas y transacción de negocio
se confirman por separado, por lo que un rechazo posterior puede consumir cuota.
No logs de IP/identificador en claro si no los necesita el diagnóstico existente;
usar particiones hash como el adaptador actual. Forwarded headers solo desde
proxies confiables configurados; no permitir falsificar IP con cabeceras libres.

Conservar indefinidamente dispositivos y eventos, sin TTL ni endpoint DELETE.
Integrar auditoría de dispositivos en supervisor, lease y lotes de DESIGN-001 / 7.2:
`AuditRetentionDays`, inicialmente 365. No limpiar eventos, relaciones o registros
de dispositivo al limpiar auditoría. Para metadatos de tokens de dispositivo,
integrar en ese supervisor un lote OpenIddict de tokens expirados/revocados con
margen de 24 horas configurable (`DeviceTokenRetentionHours`); no eliminar tokens
vigentes, clientes ni dispositivos. Deshabilitar tareas de pruning independientes
que eludan esa coordinación. No conservar cuerpos completos de tokens.

Logs estructurados: operación, resultado, correlación, dispositivo interno y
recuentos, sin matrícula, JSON completo, cabecera Authorization ni secreto.
Desactivar logging de payloads/tokens en OpenIddict/IdentityModel y sensible EF.
Métricas: aceptados, duplicados, conflictos, formatos inválidos, fuera de plazo,
fallos de autenticación/SQL y latencia, sin etiquetas con matrícula o clientId
arbitrario. Fallo del almacén OAuth/LPR hace no disponible el diagnóstico SQL
existente sin publicar cadenas o esquemas internos.

## 9. Pruebas y trazabilidad

Todas las pruebas siguientes están previstas, no ejecutadas. Domain/Application
usan reloj y repositorios dobles sin red o base de datos. HTTP con
WebApplicationFactory; persistencia y OAuth EF con SQL Server real aislado,
nunca EF InMemory ni SQLite. Datos sintéticos. La suite completa está sujeta a
la frecuencia diaria de AGENTS.md; documentación sola no la requiere.

| CP | CA cubiertos | Verificación |
| --- | --- | --- |
| CP-001 | CA-001, CA-003, CA-013 | Client Credentials real con biblioteca, dos dispositivos mismo taller, scope y 900 s configurables; sin refresh, cookies ni sesión de usuario. |
| CP-002 | CA-001, CA-002 | Asesor/dispositivo no administran; tokens de usuario no ingieren y tokens de dispositivo no acceden a rutas de usuario; identidad/cuerpo manipulados no amplían acceso. |
| CP-003 | CA-004, CA-014 | Desactivar/revocar/rotar y luego usar token anterior en otra instancia: 401; secreto anterior no emite. Sin caché de autorización. |
| CP-004 | CA-004, CA-015 | Catálogo desactiva concurrente con emisión/ingesta; reactivación de taller no recupera token previo, pero permite nuevo token si dispositivo sigue habilitado. |
| CP-005 | CA-005, CA-015 | Asociación no editable, FKs e historial conservados, sin DELETE; reactivar con mismo taller entrega secreto nuevo. |
| CP-006 | CA-006, CA-010 | Captura y recepción distintas, dirección opcional, baja confianza, sin imagen ni estancia creada. |
| CP-007 | CA-007, CA-008, CA-018 | Mismo UUID simultáneo en dos procesos genera uno; repetición fuera de plazo confirma original; cambios de datos o taller no sobrescriben ni revelan evento ajeno. |
| CP-008 | CA-009 | Fronteras exactas ±30 min, un tick fuera, timestamps equivalentes, recepción compartida y opciones modificadas con reloj controlado. |
| CP-009 | CA-011 | Fallo antes de commit y respuesta perdida/commit incierto; reenvío reconcilia; no éxito falso ni retry de escritura ciega. |
| CP-010 | CA-012 | Tabla de familias y prefijos oficiales: casos positivos/negativos por fila, históricos y especiales, ambigüedad, O/0, ceros, separadores y texto original. No afirmar exhaustividad sin esos fixtures. |
| CP-011 | CA-012, CA-017 | Matrícula inválida persistida y marcada; campo vacío/control/tamaño, JSON duplicado/desconocido, confianza o precisión excesivas producen error sin persistencia. |
| CP-012 | CA-014, CA-015, CA-016 | Secretos solo en respuesta de generación; GET/logs/auditoría sin secreto; ETags y respuesta perdida de rotación, recuperación mediante nueva rotación. |
| CP-013 | CA-016 | 428/412, no-op con ETag vigente, auditoría y estado atómicos, sin motivo obligatorio, rollback ante fallo de gestor/auditoría. |
| CP-014 | CA-001, CA-013, CA-017 | Basic/form OAuth y errores propios de token; grants/scopes/métodos ajenos rechazados; 401/403/413/415/422/429/503 y ProblemDetails de recursos. |
| CP-015 | CA-004, CA-011 | SQL inaccesible no autoriza ni devuelve éxito; no AD requerido para dispositivos; certificados/issuer y límites inválidos impiden arranque. |
| CP-016 | CA-016 | Lease/retención: limpia auditoría y metadatos elegibles de token, nunca dispositivo, secreto vigente, evento ni token no elegible. |
| CP-017 | CA-010, CA-017 | Cuotas SQL compartidas entre instancias, ventana exacta, duplicados consumen y Retry-After correcto; no se confunden con cuotas RF-001. |
| CP-018 | CA-002, CA-005 | Arquitectura: sin referencias OpenIddict/HTTP/EF en capas internas, sin DbContext en endpoints; migración preserva RF-001/002/009 y constraints reales. |

Verificación manual posterior con Postman/Bruno: login de Administrador, alta,
token por Basic/form, evento 201, reintento 200, conflicto 409, matrícula inválida
201 y revocación con token previo rechazado. Guardar colección sin secretos ni
datos reales. No declarar probada una cámara o proveedor por probar HTTP.

## 10. Preparación para implementación

El plan TASKS-003 organiza los tramos dependientes: persistencia/modelo; adaptación
OpenIddict y esquemas; administración/auditoría; publicación de catálogo e
invalidación; ingesta; límites/retención; OpenAPI y runbook; pruebas trazables.
Actualizar DESIGN-009 al implementar su extensión y registrar limitaciones de
verificación. No añadir tareas marcadas como completadas ni aplicar migraciones
por haber escrito este diseño.

OpenAPI deberá describir Client Credentials para ingesta, Basic/form para token,
Bearer de usuarios para administración, todos los DTOs, errores y precondiciones;
usar ejemplos sintéticos y campos de secreto write-only cuando corresponda.
No habilitar Swagger en producción salvo configuración explícita existente.

Fuentes adicionales: [OAuth 2.0, Client Credentials](https://www.rfc-editor.org/rfc/rfc6749.html#section-4.4),
[permisos OpenIddict](https://documentation.openiddict.com/configuration/application-permissions),
[validación local](https://documentation.openiddict.com/guides/getting-started/implementing-token-validation-in-your-apis),
[certificados](https://documentation.openiddict.com/configuration/encryption-and-signing-credentials),
[RFC 3339](https://www.rfc-editor.org/rfc/rfc3339.html) y
[RFC 9562](https://www.rfc-editor.org/rfc/rfc9562.html).
