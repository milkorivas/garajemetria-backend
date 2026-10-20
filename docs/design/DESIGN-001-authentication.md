# DESIGN-001 — Autenticación

| Campo | Valor |
| --- | --- |
| Requerimiento de origen | [RF-001 — Autenticación](../requirements/RF-001-authentication.md) |
| Plan de implementación | [TASKS-001](../tasks/TASKS-001-authentication.md) |
| Estado | Diseño técnico definido para el tramo RF-001 |
| Implementación | Código de RF-001 y pruebas automatizadas incorporados; verificación externa LDAPS y restauración física aislada pendientes, según TASKS-001 |
| Plataforma | C# 14, .NET 10, ASP.NET Core, EF Core y SQL Server |
| Políticas relacionadas | [RF-002](../requirements/RF-002-roles-and-workshop-access.md) y [RF-009](../requirements/RF-009-workshop-catalog.md); sus implementaciones no son prerrequisitos de este tramo |

## 1. Propósito y autoridad

Definir una base verificable para implementar la autenticación corporativa, sus
contratos y el ciclo de sesión. RF-001 es la fuente de las reglas funcionales;
esta especificación las concreta dentro de la arquitectura de `AGENTS.md`.
Los identificadores RN, CA y CP citados pertenecen a RF-001.

RF-001 es la autoridad de reglas funcionales y criterios de aceptación;
DESIGN-001 es la fuente de los contratos técnicos de autenticación, incluidos
valores exactos, DTOs, errores, configuración y persistencia. Los nombres
internos pueden ajustarse sin cambiar esos contratos ni las invariantes.
La definición no acredita implementación ni pruebas ejecutadas. Los cambios
funcionales se acuerdan en RF-001 y se reconcilian con diseño, tareas y pruebas.

## 2. Alcance

Incluye login contra Active Directory local mediante LDAPS, autorización inicial
según grupos AD y taller local, JWT de acceso y refrescamiento, sesión única,
renovación, logout, revocación inmediata, detección de reutilización, controles
HTTP, configuración y persistencia de sesiones.

Excluye autorregistro, gestión de contraseñas corporativas, autenticación LPR,
un servidor OAuth 2.0 completo, endpoints de mantenimiento de usuarios y
asignaciones, y altas administrativas manuales. El primer Administrador utiliza
el aprovisionamiento automático del login, incluido en RF-001. La selección de
taller de consulta del Administrador no cambia su
asignación ni su sesión y no se implementa en los endpoints de autenticación.
El diseño de esos casos de uso se desarrolla en
[DESIGN-002 — Roles y acceso a talleres](DESIGN-002-roles-and-workshop-access.md).

### 2.1. Entrega independiente de RF-001

RF-001 implementa y prueba por sí mismo resolución de grupos AD con prioridad
Administrador, perfil local único, auditoría de aprovisionamiento, sesiones,
consulta local de asignación/actividad de taller y controles de autenticación.
Estas piezas se implementan una vez aquí y RF-002/RF-009 las reutilizan.
No hay dependencia de ejecución con T002 ni con el adaptador corporativo.

No se implementan endpoints de asignación, auditoría administrativa de cambios,
consultas de negocio ni carga/sincronización de talleres en este tramo. La
persistencia incluye solo los datos locales necesarios para comprobar acceso;
una referencia de taller no permite editar ni importar el catálogo por la API.
Sin carga inicial completa el Asesor recibe 503 cuando necesita validar taller;
el perfil pendiente sin asignación recibe el rechazo específico y el
Administrador puede entrar sin taller. No introducir permisos por defecto ni
cargas corporativas ficticias para completar el tramo.

Unit tests usan dobles; los tests HTTP/persistencia verifican el contrato de
consulta local con estados y datos sintéticos preparados en SQL Server aislado.
Se prueba el cierre de sesión ante cambios controlados de estado local sin
atribuir esa prueba a una sincronización o mantenimiento aún no implementados.
Los productores externos de esos cambios se verifican después en sus tramos.

## 3. Modelo y responsabilidades

### 3.1. Identidad y autorización

- Resolver `usuario` usando el dominio predeterminado y `DOMINIO\usuario`
  usando el dominio explícito. El dominio resuelto debe pertenecer a la lista
  permitida antes de realizar una validación de credenciales contra AD.
- Normalizar dominio y usuario para resolver equivalencias y limitar intentos.
  No recortar ni transformar la contraseña.
- Vincular el usuario local al `objectGUID` de su objeto en el único dominio de AD.
  El nombre introducido no es la clave permanente de identidad.
- Obtener el rol de los grupos AD de RF-002 y el taller de la asignación local.
  Admin prevalece sobre Advisor; se admite pertenencia directa y anidada.
  El cliente no puede establecer estos valores en login ni existe override local.
- Rechazar a usuarios sin rol. Un Administrador puede acceder sin taller;
  un Asesor necesita taller asignado y solo puede acceder al suyo.
- Crear la identidad local automáticamente, única por objectGUID, tras validar
  credenciales y grupos autorizantes. El Asesor pendiente conserva su perfil
  pese al 403 por falta de taller; no se crea sesión ni se emiten tokens.
- No almacenar contraseñas. La comprobación del estado de AD en refresh debe
  funcionar sin conservar ni volver a solicitar la contraseña del login.

`ICorporateDirectory` incorpora la resolución de pertenencia directa y transitiva
de la identidad estable a los dos grupos de RF-002. Infrastructure no debe
limitarse a leer una lista de miembros directos ni devolver una pertenencia
parcial como definitiva si falla una consulta necesaria. Application aplica
prioridad Administrador y compara el rol efectivo con la instantánea de sesión.
La consulta SQL de asignaciones solo determina el taller, nunca concede roles.

### 3.1.1. Integración de identidad y AD

El contrato de integración concreta RF-001 / RN-001 y RN-009: un dominio, vínculo por `objectGUID` e identidad de servicio
con permisos de lectura mínimos. El usuario conserva su identificador local;
el GUID del directorio es un vínculo interno único, no sustituye al `sub` local.
Un nombre reutilizado por un objeto nuevo no hereda asignaciones ni sesiones.

Infrastructure resuelve el login como nombre de cuenta del dominio configurado,
obtiene el GUID y valida las credenciales contra el mismo objeto por LDAPS.
Una lectura de AD con la identidad de servicio no autentica al usuario.
Separar las conexiones/autenticaciones de servicio y usuario para no reutilizar
accidentalmente un bind con otra identidad. No usar bind anónimo como fallback.

Para consultar estado en refresh, buscar por el GUID asociado a la sesión en
el dominio configurado y solicitar explícitamente `userAccountControl` y
`msDS-User-Account-Control-Computed`. Evaluar deshabilitación con `0x0002` del
primero, bloqueo con `0x0010` y expiración de contraseña con `0x00800000` del
segundo. No reconstruir la política efectiva de AD con valores locales ni
considerar un atributo ausente como un estado válido. El GUID binario y los
filtros LDAP se manejan únicamente dentro del adaptador, con escape correcto.

`ICorporateDirectory` devuelve resultados explícitos de identidad/estado válido,
rechazo de cuenta, objeto no encontrado y consulta no disponible. No expone
tipos LDAP, bitmasks ni mensajes del servidor fuera de Infrastructure.
Application cierra la sesión ante cuenta rechazada u objeto confirmado
inexistente y devuelve el 401 de sesión finalizada. Ante permisos insuficientes,
atributos ausentes o errores de consulta devuelve el 503 temporal de
autenticación, sin consumo, cierre o ampliación de sesión. Los atributos
requeridos deben ser legibles antes de considerar la consulta satisfactoria.

La cuenta de servicio no administra usuarios ni contraseñas. Los parámetros de
conexión, alcance de búsqueda y autenticación se suministran en la configuración
privada; no se presupone que IIS esté unido al dominio ni se solicitan secretos
en la documentación. El responsable de AD confirmará permisos y habilitará
las pruebas aisladas antes de dar por verificada la integración. La selección
de controladores y su replicación no deben confundirse con revocación local
instantánea ni con una garantía de lectura global de todos los cambios de AD.
No inferir bloqueo de `lockoutTime > 0` ni calcular expiración con una duración
local fija. Verificar cambio obligatorio de contraseña y contraseña sin caducidad
contra las políticas reales de AD. Búsqueda completa por GUID sin objeto implica
rechazo confirmado; búsqueda incompleta o ambigua es un error técnico.

### 3.2. Modelo de sesión

| Concepto | Datos y responsabilidad |
| --- | --- |
| `AuthenticationSession` | Identificador de sesión, usuario local, inicio UTC, expiración absoluta UTC y cierre con fecha y motivo. Protege las transiciones y el límite temporal. |
| `RefreshTokenRecord` | Identificador de token, sesión/familia, huella SHA-256, emisión, expiración y estado de consumo o revocación. No contiene el JWT recuperable. |
| Identidad y asignación local | Identificador local, vínculo estable con AD, nombre visible y taller predeterminado. El rol resuelto desde AD se conserva como instantánea de la sesión, no como asignación independiente. |

Una familia es la cadena de refresh tokens de una sesión. Utilizar
el identificador de sesión como identificador de familia, sin una entidad
adicional mientras no exista una necesidad concreta.

Una sesión está activa si no se ha cerrado y `now < absoluteExpiresAt`.
Al alcanzar exactamente el límite absoluto deja de autorizar operaciones.
No es necesario ejecutar una tarea de limpieza para que la expiración sea efectiva.
Las fechas se representan con `DateTimeOffset` en UTC y un reloj inyectable
basado en `TimeProvider` permite pruebas deterministas.

| Suceso | Efecto |
| --- | --- |
| Login satisfactorio | Cierra la sesión anterior del usuario y crea una nueva. |
| Login fallido | Conserva la sesión existente. |
| Refresh satisfactorio | Conserva inicio y límite de sesión; consume un refresh token y crea su sucesor. |
| Logout válido | Cierra la sesión y deja sin autorización todos sus tokens. |
| Reutilización de refresh auténtico, consumido y aún vigente | Revoca toda la familia, incluidos los tokens recién emitidos. |
| Refresh expirado, incluso consumido | 401 sin revocar familia ni cambiar cookies o vigencias. |
| Cambio efectivo de taller local | Cierra inmediatamente la sesión como parte de la operación de RF-002. |
| Cambio efectivo de rol o pérdida de grupos AD | Cierra la sesión al detectarse en refresh; exige nuevo login. |
| Cuenta rechazada por AD al renovar | Cierra la sesión y rechaza la renovación. |
| AD temporalmente indisponible | No consume el refresh token ni cierra o extiende la sesión. |
| Límite absoluto alcanzado | Rechaza acceso y renovación; requiere un nuevo login. |

### 3.3. Distribución por capas

| Capa | Responsabilidades |
| --- | --- |
| Domain | Invariantes y transiciones de sesión y refresh token; sin EF, JWT, LDAPS, HTTP ni paquetes externos. |
| Application | `LoginCommand`, `RefreshSessionCommand`, `LogoutCommand` y sus handlers MediatR; validadores FluentValidation; resultados y contratos de directorio, tokens, asignaciones, sesión y unidad de trabajo. |
| Infrastructure | Adaptador LDAPS, firma y validación criptográfica, generación de huellas, repositorios, consultas materializadas, DbContext, mappings y migraciones SQL Server. |
| Api | DTOs HTTP, rutas, cookies, antiforgery, CORS, limitación, autenticación Bearer, ProblemDetails y composición. Consulta la sesión mediante Application. |

Contratos de Application: `ICorporateDirectory` para validar
credenciales y consultar el estado de una identidad estable; `IUserAccessQuery`
para asignaciones; `IAuthenticationSessionRepository` para transiciones
persistidas; `ISessionStatusQuery` para comprobar acceso; `ITokenService` para
emitir y validar tokens sin exponer tipos de bibliotecas JWT; e `IUnitOfWork`
para las transacciones. Las operaciones atómicas se expresan mediante métodos
específicos y resultados explícitos, sin `IQueryable` ni repositorios genéricos.

El comportamiento asíncrono de validación MediatR se ejecuta antes del handler.
Se propaga `CancellationToken` desde HTTP hasta las operaciones de I/O.
DbContext y repositorios son scoped y no se utilizan concurrentemente dentro
de una solicitud. Las dependencias apuntan hacia Domain y Application.

## 4. Contrato HTTP

### 4.1. Operaciones

| Método y ruta | Entrada | Autenticación | Éxito |
| --- | --- | --- | --- |
| `GET /api/auth/antiforgery` | Sin cuerpo; cookie antiforgery existente opcional | Anónimo; controles de origen | `200 OK`, `requestToken`/`headerName` y cookie antiforgery |
| `POST /api/auth/login` | JSON `username`, `password` | Sin sesión previa; valida credenciales y controles de solicitud | `200 OK`, JSON de login y cookie de refresh |
| `POST /api/auth/refresh` | Refresh token exclusivamente en cookie | JWT de refresh y sesión vigente; no requiere access token vigente | `200 OK`, JSON de renovación y cookie rotada |
| `POST /api/auth/logout` | Refresh token exclusivamente en cookie | JWT de refresh vigente y sesión vigente; admite access token expirado | `204 No Content`, sin cuerpo, y eliminación de cookie |

Los endpoints de negocio se protegen por defecto mediante Bearer y verificación
de sesión. Refresh y logout tienen su validación propia del refresh token;
no deben quedar bloqueados por la expiración de un Bearer ni convertirse en
operaciones sin autenticación efectiva.

Ejemplo sintético de login:

```json
{
  "username": "DOMINIO_DE_PRUEBA\\usuario.prueba",
  "password": "valor-sintetico-solo-para-ejemplo"
}
```

Los siguientes son valores iniciales del proyecto, configurables explícitamente
en archivo. No son límites universales de Active Directory ni cifras impuestas
por OWASP. Una modificación debe mantener la compatibilidad con las cuentas
corporativas y actualizar la documentación y las pruebas de límites.

| Parámetro | Valor inicial | Regla |
| --- | --- | --- |
| Longitud máxima de `username` | 256 caracteres | Incluye dominio y separador cuando se proporcionan. Mínimo: un carácter en cada componente presente. |
| Longitud máxima de `password` | 1.024 caracteres | Mínimo de entrada: un carácter; no es una política de fortaleza de contraseña. El máximo configurable no será inferior a 64. |
| Tamaño máximo del cuerpo de login | 16 KiB (16.384 bytes) | Incluye JSON, espacios y escapes, medidos en bytes UTF-8 del cuerpo recibido. |
| Profundidad máxima del parser JSON | 2 niveles | Es una protección de lectura; el esquema solo admite el objeto raíz y dos valores string. |

«Caracteres» significa valores escalares Unicode después de decodificar JSON,
no bytes ni grafemas visuales. Rechazar UTF-8 inválido y sustitutos Unicode sin
pareja; no reemplazarlos silenciosamente. Los escapes JSON se decodifican una
sola vez según el protocolo, sin una decodificación URL o HTML posterior.

**Usuario:** aceptar únicamente `usuario` o `DOMINIO\usuario`, con cero o un
separador `\` y componentes no vacíos. Rechazar espacios Unicode al principio
o final de cada componente, caracteres de control, `/` y `@`; no admitir UPN
(`usuario@dominio`) ni separadores repetidos. No limitar arbitrariamente
el usuario a ASCII o a 20 caracteres ni eliminar puntuación para «sanearlo».
Los demás caracteres deben mantenerse para su resolución como nombre de cuenta
por el directorio; no interpretar la entrada como un DN ni como un filtro LDAP.
El adaptador debe construir consultas LDAP con el escape adecuado al contexto.
La validación no sustituye esa protección frente a inyección.

Para la resolución y la clave de limitación, comparar dominio y usuario sin
distinción de mayúsculas de forma independiente de la cultura, conservando los
caracteres y sin normalización Unicode de compatibilidad. Aplicar la misma
política de equivalencia a la lista de dominios. Un dominio sintácticamente
válido pero no permitido devuelve el 401 genérico; un formato incorrecto,
incluido UPN, devuelve 400 sin consultar credenciales.

**Contraseña:** aceptar Unicode válido, espacios iniciales/finales y caracteres
especiales, incluidos los significativos para JSON o LDAP, dentro del límite.
No exigir mayúsculas, números, símbolos ni una longitud de creación de cuenta.
Una cadena formada solo por espacios no se trata como vacía: se entrega sin
cambios a AD. Rechazar NUL (`U+0000`) evita ambigüedad entre adaptadores; nunca
realizar un bind anónimo o sin contraseña como alternativa a credenciales
rechazadas. Ni la contraseña ni su valor intentado se incluyen en errores.

**Cuerpo:** exigir `Content-Type: application/json`, sin charset o con
`charset=utf-8`; no admitir formularios, texto, JSON con otro charset ni cuerpos
comprimidos (`Content-Encoding` distinto de ausente o `identity`). Rechazar con
400 cuerpo ausente, JSON malformado, comentarios, comas finales, valores después
del objeto, arrays, objetos anidados, tipos incorrectos, nombres con otra
capitalización, campos ausentes/nulos, duplicados y campos adicionales. No
buscar credenciales en query string como alternativa al JSON.

Los errores 400 usan ProblemDetails con `detail` «La solicitud de inicio de
sesión no es válida» y pueden indicar nombres de campos y reglas incumplidas,
sin reproducir sus valores. Para 413 se utiliza «El cuerpo de la solicitud
supera el tamaño permitido»; para 415, «El formato del contenido no está
admitido». Las respuestas de validación no revelan si la cuenta existe.

La configuración inválida o ausente impide arrancar. El presupuesto de bytes
debe permitir representar los campos máximos en UTF-8 y sus escapes JSON; no
ajustar las longitudes por debajo de lo documentado sin revisar el contrato.
Los límites deben verificarse también en el alojamiento IIS y sus proxies,
evitando que un rechazo previo sustituya el contrato de errores de la API.

Referencias: [OWASP, validación de entrada](https://cheatsheetseries.owasp.org/cheatsheets/Input_Validation_Cheat_Sheet.html)
apoya validación de estructura, campos y límites antes del procesamiento;
[OWASP, autenticación](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)
recomienda permitir contraseñas largas, Unicode y espacios, sin truncamiento
silencioso. Aquí se validan credenciales existentes en AD; no se implanta una
política local de creación de contraseñas.

Api aplica el parser y límite de cuerpo; Application aplica formato y longitudes
mediante FluentValidation antes del handler. Infrastructure escapa las consultas
LDAP según contexto; ninguna capa reutiliza la contraseña como bind anónimo.
Documentar límites y respuestas en OpenAPI y comprobar el alojamiento efectivo.

### 4.2. Respuestas

| Campo JSON | Tipo | Login | Refresh |
| --- | --- | --- | --- |
| `accessToken` | string | JWT de acceso | Nuevo JWT de acceso |
| `tokenType` | string | `Bearer` | `Bearer` |
| `accessTokenExpiresAt` | string UTC `yyyy-MM-ddTHH:mm:ssZ` | Expiración exacta de exp, sin fracciones | Expiración exacta del nuevo exp |
| `user.id` | string | Identificador local | Ausente |
| `user.displayName` | string | Nombre visible | Ausente |
| `user.role` | string | `administrator` o `advisor`, derivado de AD según RF-002 | Ausente |
| `user.defaultWorkshopId` | string o null | Taller asignado; puede ser null para Administrador | Ausente |

No incluir `refreshToken` ni `refreshTokenExpiresAt` en JSON. El refresh token
y su caducidad se entregan mediante cookie. Las respuestas con tokens deben
incluir `Cache-Control: no-store`. No devolver contraseñas en ninguna respuesta.

### 4.3. Errores

Utilizar ProblemDetails y manejo centralizado. El texto acordado debe estar
presente en `detail`; no incluir trazas, SQL, credenciales, tokens ni detalles
del directorio. Los errores esperados se representan mediante resultados
explícitos y no mediante excepciones como flujo ordinario.

| HTTP | Condición | Mensaje acordado |
| --- | --- | --- |
| 400 | Entrada de login inválida | «La solicitud de inicio de sesión no es válida»; reglas por campo sin valores intentados |
| 413 | Cuerpo de login superior al límite | «El cuerpo de la solicitud supera el tamaño permitido» |
| 415 | Tipo, charset o codificación de contenido de login no admitidos | «El formato del contenido no está admitido» |
| 401 | Credenciales incorrectas, dominio no permitido o cuenta rechazada en login | «No se pudo iniciar sesión» |
| 403 | Credenciales válidas, sin pertenencia autorizante en AD | «No tienes un rol asignado en GarajeMetría» |
| 403 | Credenciales válidas, Asesor sin taller | «No tienes un taller asignado. Contacta con un administrador» |
| 403 | Credenciales y grupos válidos, Asesor con taller inactivo | «Tu taller asignado no está activo. Contacta con un administrador» |
| 401 | Refresh inválido o expirado, sesión cerrada o expirada, o rechazo confirmado de cuenta AD en refresh | «La sesión ha finalizado. Inicia sesión de nuevo» |
| 429 | Límite excedido | «Demasiados intentos. Inténtalo de nuevo más tarde» |
| 503 | AD indisponible o estado no verificable por error de lectura/permisos en login o refresh | «El servicio de autenticación no está disponible temporalmente. Inténtalo de nuevo más tarde» |

La respuesta 429 incluye `Retry-After` en segundos hasta que los límites
aplicables permitan reintentar. Los errores inesperados no se convierten en
éxitos ni se presentan automáticamente como fallos de credenciales. Los códigos
y mensajes de persistencia se aplican conforme a 4.3.1.
Los rechazos de origen/antiforgery son 403 conforme a 4.4.1 y no modifican sesión.

### 4.3.1. Errores de persistencia y resultado de escritura

Esta política se aplica a RF-001, a las operaciones administrativas de RF-002
y a la comprobación de sesión de las rutas protegidas. Un JWT válido no permite
continuar si SQL no puede confirmar la vigencia de su sesión. Un error técnico
al consultar no demuestra que la sesión esté cerrada o que las credenciales
sean incorrectas: no devolver 401/403 ni forzar logout por ese solo motivo.

| Situación | HTTP | code | title | detail |
| --- | --- | --- | --- | --- |
| Conexión de persistencia temporalmente no disponible, timeout o fallo transitorio identificado al leer o al escribir con certeza de no aplicación o rollback confirmado | 503 | `service_unavailable` | Servicio no disponible | «El servicio no está disponible temporalmente. Inténtalo de nuevo más tarde» |
| Error inesperado/no clasificado, fallo de programación o acceso persistente a datos mal configurado que no sea un rechazo funcional | 500 | `internal_error` | Error interno | «Se ha producido un error interno. Contacta con soporte si el problema persiste» |
| Interrupción de comunicación durante commit sin poder establecer si la escritura se confirmó | 503 | `operation_outcome_unknown` | Resultado no confirmado | «No se pudo confirmar el resultado de la operación. Comprueba su estado antes de volver a intentarlo» |

Los tipos de problema son identificadores estables del contrato:

- `https://api.garajemetria.com/problems/service-unavailable`.
- `https://api.garajemetria.com/problems/internal-error`.
- `https://api.garajemetria.com/problems/operation-outcome-unknown`.

No se presupone que sus páginas de documentación ya estén publicadas. La
respuesta es `Content-Type: application/problem+json` y `Cache-Control: no-store`.
`status` coincide con el código HTTP; `instance` es un identificador opaco de
ocurrencia `urn:uuid:<UUID generado por el servidor>` y `traceId` permite
correlación con logs internos. Ninguno contiene datos personales, query strings,
tokens o valores de configuración. El consumidor usa type/code, no compara los
mensajes traducidos para decidir su comportamiento.

Ejemplo sintético para una consulta de sesión que no puede completarse:

```json
{
  "type": "https://api.garajemetria.com/problems/service-unavailable",
  "title": "Servicio no disponible",
  "status": 503,
  "detail": "El servicio no está disponible temporalmente. Inténtalo de nuevo más tarde",
  "instance": "urn:uuid:10000000-0000-4000-8000-000000000001",
  "code": "service_unavailable",
  "traceId": "identificador-sintetico-de-correlacion"
}
```

Los errores AD conservan el 503 y el mensaje específico de 4.3;
no reemplazarlos por el mensaje general de persistencia. Los conflictos de
versión (412), asignación incompatible (409), recurso inexistente (404),
validación y demás resultados funcionales no se reclasifican como 500/503.
No mapear toda excepción SQL a conflicto ni toda excepción del sistema a 503.

**Escrituras y efectos:**

- Antes de enviar tokens o cookies, confirmar la transacción. Si una escritura
  falla antes de commit y se verifica el rollback, no hay cambios de esa
  transacción: no consumo de refresh, cambio de taller, auditoría parcial o
  cierre de sesión parcial.
- Los commits anteriores e independientes permanecen: por ejemplo, la creación
  local ya confirmada puede conservarse aunque falle la creación de sesión.
- Si no se conoce el resultado del commit, la transacción sigue siendo atómica,
  pero puede haberse aplicado completa o no haberse aplicado. No prometer
  que la sesión anterior, el refresh o la asignación sigan intactos.
- En logout o revocación por reuso/cambio de rol, no responder 204 o rechazo
  funcional afirmando un cierre confirmado si no se pudo persistir. Devolver
  el error técnico correspondiente; si SQL no permite verificar sesiones,
  las operaciones protegidas también se rechazan temporalmente.
- Ante un error, no rotar/eliminar cookies ni devolver nuevos JWT. Si la
  respuesta ya comenzó o la conexión se perdió, no intentar sustituirla por
  otro JSON: registrar la incidencia y cortar la respuesta según el framework.

**Reintentos:** no repetir automáticamente escrituras de login, refresh, logout
o asignación por recibir 500/503, ni mediante reintentos genéricos de base de
datos. El cliente con refresh de resultado desconocido debe volver a iniciar
sesión al recuperar el servicio; reenviar el mismo refresh puede revocar la
familia. Para una asignación incierta, releer el taller y su ETag antes de
decidir otra escritura. No se añade un endpoint de recuperación de transacciones.

En estos errores 500 y 503 de persistencia se omite Retry-After: no hay un plazo
de recuperación fiable y el estado HTTP no garantiza que repetir una escritura
sea seguro. El 429 mantiene su Retry-After obligatorio ya definido. La falta
de respuesta o una cancelación tampoco prueba que no hubo commit; no inventar
un 499 como respuesta pública de la API.

Referencias: [RFC 9457, ProblemDetails](https://www.rfc-editor.org/rfc/rfc9457.html)
y [RFC 9110, 503](https://www.rfc-editor.org/rfc/rfc9110.html#name-503-service-unavailable).
Los mensajes, códigos de aplicación y reglas de reintento son decisiones de
GarajeMetría. La política cubre errores controlados por la API, no garantiza un
cuerpo JSON cuando IIS, la red o un proxy terminan la conexión antes de ella.

Infrastructure clasifica los fallos del proveedor y comunica resultados o
excepciones técnicas propios de Application. Api centraliza ProblemDetails;
no inspecciona SqlException ni convierte toda excepción en indisponibilidad.
Una consulta de sesión fallida no ejecuta el caso de uso protegido.

### 4.4. Cookie y protección de solicitudes

- Refresh token exclusivamente en la cookie definida en la tabla de esta
  sección; su expiración coincide con la del token.
- Login establece la cookie; refresh la reemplaza tras una rotación confirmada;
  logout la elimina usando el mismo nombre y alcance tras confirmar el cierre.
- La web y la API de producción comparten sitio bajo HTTPS; verificar el
  envío de cookies con los atributos de la tabla desde la web con
  credenciales y CORS para el origen autorizado.
- Todos los consumidores usan antiforgery del framework con token en
  `X-CSRF-Token` para login, refresh y logout. El bootstrap anónimo no entrega
  credenciales de autenticación; el contrato se define en 4.4.1.
- CORS admite únicamente orígenes explícitos; nunca combinar credenciales y
  origen comodín. No sustituye la autorización ni determina por sí solo quién
  puede consumir la API desde una aplicación HTTP externa.
- El contrato de tokens es único para todos los consumidores. Sin `Origin`
  ni `Referer`, los POST requieren igualmente antiforgery válido; no inferir
  confianza ni exceptuar la validación por esa ausencia.
- Una solicitud rechazada por los controles de origen o antiforgery no modifica
  la sesión, no consume tokens ni provoca revocación por reutilización.

El nombre de refresh es un literal del contrato de esta sección, compartido
por emisión, lectura, rotación y eliminación; no aceptar aliases ni nombres
distintos por consumidor. Api utiliza ese nombre de forma consistente sin
exponer cookies en Application o Domain. HTTPS también se mantiene en desarrollo.
La cookie antiforgery conserva su nombre distinto y su Path `/` de 4.4.1.

| Opción de cookie de refresh | Valor |
| --- | --- |
| Name | `__Secure-GarajeMetria.RefreshToken` |
| HttpOnly / SecurePolicy | `true` / `Always` |
| SameSite / Path / Domain | `Strict` / `/api/auth` / sin atributo Domain |
| Expires | Instante UTC de exp del refresh, según sección 6 |

El prefijo `__Secure-` es compatible con el alcance acordado; `__Host-` exigiría
Path `/`. No deducir que `__Secure-` impone por sí mismo ausencia de Domain:
configurar siempre todos los atributos de esta sección.

### 4.4.1. Contrato e implementación antiforgery

Se utiliza la pareja cookie/token generada y validada por ASP.NET Core, sin
implementar criptografía propia ni aceptar el valor de la cookie como token
de solicitud. Este es un contrato único para navegadores y clientes HTTP.

| Elemento | Contrato |
| --- | --- |
| Bootstrap | `GET /api/auth/antiforgery`, sin sesión ni Bearer obligatorio |
| Éxito | 200 JSON con `requestToken` (string opaco) y `headerName` igual a `X-CSRF-Token` |
| Cookie antiforgery | `__Host-GarajeMetria.Antiforgery`; Secure, HttpOnly, SameSite=Strict, Path=/ y sin Domain |
| Entrega del token de solicitud | Solo en JSON del bootstrap; el cliente lo conserva en memoria y lo envía en `X-CSRF-Token` |
| POST protegidos | `/api/auth/login`, `/api/auth/refresh` y `/api/auth/logout`: requieren cookie antiforgery y cabecera de solicitud válidas y correspondientes |
| Caché | Bootstrap con `Cache-Control: no-store`; no almacenar respuestas en cachés intermedias |

Ejemplo de respuesta, con un valor ilustrativo que no es un token válido:

```json
{
  "requestToken": "valor-opaco-generado-por-el-framework",
  "headerName": "X-CSRF-Token"
}
```

El bootstrap crea o reutiliza únicamente la cookie antiforgery. No establece,
rota ni elimina la cookie de refresh; no abre ni sustituye sesiones, no consulta
AD y no requiere el propio token antiforgery para arrancar el flujo. Su token
no concede acceso ni permite renovar sin un refresh válido. No se publica una
duración propia del token antiforgery distinta de la semántica del framework;
si la pareja deja de ser válida, se obtiene mediante un nuevo bootstrap.

La cookie antiforgery es de sesión del navegador, sin Expires/Max-Age explícitos.
Un logout satisfactorio elimina también esta cookie con su nombre y alcance
originales; el siguiente login obtiene un nuevo bootstrap. El refresh mantiene
la pareja antiforgery mientras siga siendo válida; su token de solicitud no
es de un solo uso ni rota junto al refresh. Nunca registrar ninguno de los dos
tokens ni sus cookies.

**Reglas de origen para bootstrap y POST de autenticación:**

1. Si `Origin` existe, exigir un único origen HTTP(S) bien formado y permitido.
   Rechazar `null`, vacío, valores múltiples, rutas y orígenes no permitidos.
   No usar Referer para rescatar un Origin rechazado.
2. Si falta Origin y existe `Referer`, extraer su origen y comprobarlo contra
   la misma lista; rechazar Referer vacío, malformado o de origen no permitido.
3. Si faltan ambos, permitir el bootstrap, que solo entrega protección
   antiforgery. Para los POST exigir igualmente la pareja antiforgery válida.
   No tratar esa ausencia como prueba de que el consumidor es confiable.
4. Si `Sec-Fetch-Site` está presente con valor `cross-site`, rechazar la
   solicitud. Su ausencia no exime de ningún otro control. Una señal
   `same-site` no sustituye la lista de orígenes ni valida otros subdominios.

La comparación de orígenes usa esquema, host y puerto efectivo, sin prefijos,
coincidencias de sufijo ni comodines. La lista se obtiene exclusivamente de
`Http:AllowedOrigins` en todos los entornos y exige orígenes HTTPS explícitos.
Los dominios del despliegue se configuran externamente: no hay nombres
obligatorios ni orígenes permitidos implícitos en código. Los orígenes HTTPS
locales de desarrollo también se configuran por entorno.
No se desactiva Secure ni se confía en Host o cabeceras reenviadas
desde proxies no autorizados para construir esta lista.

Los navegadores realizan bootstrap y POST con envío de cookies; CORS permite
credenciales y `X-CSRF-Token` solo para los orígenes configurados. Los clientes
HTTP sin Origin utilizan el mismo bootstrap, su almacén de cookies y la misma
cabecera. No necesitan inventar un Origin para eludir controles. No se admiten
tokens CSRF por query string, formulario o campos JSON en lugar de la cabecera.
OPTIONS de preflight no requiere token ni modifica cookies o sesión; su
aceptación CORS no autoriza el POST posterior.

Los cuatro endpoints de `/api/auth` mantienen un contexto anónimo consistente
para generar y validar antiforgery; no vinculan ese token al principal Bearer.
La autenticación efectiva de login se realiza con sus credenciales y la de
refresh/logout con su refresh token. Así, la expiración del access token o
su presencia accidental no impide el logout ni cambia la pareja antiforgery.
Los endpoints de negocio mantienen su autenticación Bearer y consulta de sesión.

| Rechazo | HTTP y ProblemDetails |
| --- | --- |
| Origen/Referer rechazado o señal cross-site | 403; `detail`: «El origen de la solicitud no está permitido»; extensión `code`: `origin_not_allowed` |
| Cookie o cabecera antiforgery ausente, duplicada, inválida o no correspondiente | 403; `detail`: «No se pudo validar la protección de la solicitud»; extensión `code`: `antiforgery_validation_failed` |

Los mensajes no incluyen orígenes recibidos ni tokens. La comprobación de origen
y antiforgery precede a cualquier cambio de sesión o consumo de refresh. El
cliente puede repetir el bootstrap ante un 403 explícito de antiforgery; esto
no habilita reintentos automáticos de refresh con resultado desconocido.

Referencias: [ASP.NET Core, antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0)
y [OWASP, prevención CSRF](https://cheatsheetseries.owasp.org/cheatsheets/Cross-Site_Request_Forgery_Prevention_Cheat_Sheet.html).
La ruta, los nombres y la política sin cabeceras de origen son decisiones de
este contrato; la protección se conserva mediante validación obligatoria de la
pareja antiforgery y los controles complementarios descritos.

**Implementación en Api:** usar `IAntiforgery.GetAndStoreTokens` en bootstrap y
`IAntiforgery.ValidateRequestAsync` antes de ejecutar los handlers protegidos.
No construir criptografía propia ni comparar simplemente cabecera y cookie.
No trasladar HttpContext ni tipos antiforgery a Application o Domain.

Excluir los cuatro endpoints de auth del procesamiento automático de Bearer
para mantener el contexto anónimo descrito; no alterar el principal de negocio.
Verificar el ciclo con Bearer ausente, válido y expirado. Un fallo criptográfico
inesperado sigue el manejo de errores técnicos y no se oculta como validación
satisfactoria ni se convierte indiscriminadamente en rechazo funcional.

Persistir el key ring de ASP.NET Core Data Protection fuera de contenido web,
protegerlo en Windows y compartir claves/discriminador entre todos los procesos
y servidores de esta API. Un reciclado no invalida las parejas. Las claves no
se versionan ni se registran; documentar ubicación y permisos de despliegue.
Referencia: [Data Protection](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).

## 5. Flujos de aplicación

### 5.1. Login

1. Aplicar límites de solicitud, controles HTTP y limitación por IP. Validar
   entrada; resolver y normalizar dominio/usuario y aplicar el límite por usuario.
2. Si el dominio no está permitido, devolver el rechazo genérico sin validar
   credenciales contra AD.
3. Validar credenciales mediante LDAPS con certificado válido y timeout acotado.
   Diferenciar rechazo de credenciales e indisponibilidad sin revelar detalles.
4. Resolver el rol desde los grupos de la identidad AD autenticada y consultar
   la identidad local. Sin rol, devolver el 403 correspondiente sin crear perfil.
   Con rol válido, crear el perfil si no existe con unicidad por GUID y sin
   sobrescribir asignaciones previas; resolver colisiones concurrentes releyendo
   la identidad. Confirmar este aprovisionamiento antes de comprobar el taller:
   si es Asesor sin taller, devolver 403 conservando el perfil, sin sesión ni
   tokens. Un fallo técnico al consultar grupos produce 503 y no crea perfil.
5. Calcular inicio, límite absoluto y expiraciones. Preparar los tokens y sus
   metadatos sin entregarlos al cliente.
6. En una transacción, comprobar que la asignación local usada sigue vigente,
   cerrar la sesión anterior y persistir la nueva sesión y huella de refresh.
   Serializar la sustitución por usuario y respaldarla con restricciones.
7. Tras confirmar la transacción, devolver JSON y cookie. Si falla, no entregar
   tokens ni dejar una sustitución parcial de la sesión anterior.

La validación de credenciales se realiza fuera de la transacción. Dos logins
concurrentes pueden responder satisfactoriamente, pero al finalizar sus commits
solo la sesión del último cambio confirmado permanece activa. Un login fallido
no debe cerrar la sesión anterior.

### 5.2. Refresh

Login y refresh requieren taller activo para `advisor`, según
[RF-009](../requirements/RF-009-workshop-catalog.md). Login con taller inactivo
devuelve 403 y el mensaje de 4.3; sin carga inicial completa,
devuelve 503. Una caída del origen permite usar la última copia confirmada,
incluso tras 15 minutos, sin omitir la comprobación de sesión en SQL local.
La publicación de una desactivación y el cierre de sesiones de Asesor deben
ser atómicos. Login/rotación revalidan actividad dentro de su transacción y
se coordinan con la publicación para evitar sesiones posteriores al cierre.
Las sesiones cerradas reciben el 401 habitual. Reactivar permite nuevo login
con la asignación conservada y nunca recupera una sesión o token anterior.

1. Aplicar controles HTTP y límite por IP. Leer y validar el JWT de la cookie:
   firma, algoritmo, clave, emisor, audiencia, tipo y vigencia.
2. Resolver la sesión y comprobar asociación con el usuario y el token
   persistido mediante su huella; aplicar el límite por usuario autenticado.
3. Tras los controles de admisión, un JWT expirado devuelve el 401 de sesión
   finalizada sin consultar consumo para revocar, sin cambiar cookies y sin
   cerrar o ampliar una familia que siga activa. Esto incluye un token consumido
   con sucesor vigente. Un token falso tampoco permite revocar sesiones ajenas.
   Rechazar sesión cerrada/expirada o token no válido. Para un token consumido
   auténtico y asociado a la sesión, revalidar su vigencia en la transacción
   antes de decidir el cierre: solo el reuso aún vigente revoca la familia.
4. Para un token vigente y no consumido, comprobar el estado actual de la
   identidad estable en AD y resolver sus grupos directos/anidados, fuera de
   cualquier transacción SQL. Comparar el rol efectivo con la instantánea de sesión.
5. Si AD rechaza el estado de cuenta, cerrar la sesión y rechazar sin revelar
   el estado específico. También cerrar si cambia el rol efectivo o desaparece
   la pertenencia autorizante, con 401 de sesión finalizada y sin tokens nuevos.
   Si el rol no cambia, continuar. Un fallo de lectura de estado o grupos
   produce 503 sin consumo, cierre o ampliación temporal.
6. Preparar los nuevos tokens. En una transacción, volver a comprobar sesión,
   límite absoluto y vigencia; consumir condicionalmente el refresh recibido y
   persistir el sucesor y su huella. La comprobación inicial no sustituye a esta.
7. Si otro refresh ya lo consumió y el token sigue vigente al comprobarlo en
   la transacción, confirmar la revocación de la familia y devolver 401. Si
   expiró mientras esperaba, devolver 401 sin revocar, consumir ni rotar.
   Distinguir ambas causas al fallar una actualización condicional; cero filas
   afectadas no demuestra por sí solo reutilización. Si logout, nuevo login o
   cambio de asignación cerró la sesión, no emitir tokens ni reactivarla.
8. Tras confirmar la rotación, devolver únicamente el JSON de renovación y la
   cookie nueva. El access token anterior conserva su vigencia mientras la
   sesión permanezca activa.

No reintentar automáticamente un consumo no idempotente. Si el cliente pierde
la respuesta después del commit, reenviar el mismo refresh puede revocar la
sesión. Los consumidores coordinan una sola renovación a la vez y no reintentan
automáticamente cuando desconocen el resultado.

### 5.3. Logout y comprobación de acceso

Logout valida la cookie y la sesión sin exigir un access token vigente, confirma
el cierre de la sesión en persistencia y elimina la cookie. Un refresh inválido,
expirado o asociado a una sesión finalizada produce 401; no se presupone un
logout idempotente con 204 para cualquier cookie. La reutilización de tokens
consumidos solo revoca si el token sigue vigente al decidir, conforme a RN-010.
Un token ya expirado, consumido o no, recibe 401 sin revocar ni cambiar cookies.

Cada solicitud protegida valida primero el JWT de acceso y después consulta la
sesión en servidor. Debe comprobar correspondencia entre `sub`, `sid` y usuario
persistido, cierre y expiración absoluta. No usar caché que prolongue el acceso
tras revocación. Si no se puede consultar el estado, denegar temporalmente.
No se exige consultar AD en cada operación de negocio.

La revocación impide las comprobaciones de autorización posteriores al commit;
no supone cancelar retroactivamente operaciones que ya se habían autorizado.
RF-002 debe coordinar el cambio de taller local y cierre de sesión de forma
atómica, también frente a logins o renovaciones concurrentes. Los cambios de
grupos AD se detectan en login/refresh y no forman parte de esa transacción.
Los accesos previos pueden seguir vigentes hasta detectar el cambio en refresh,
sujetos a expiración y estado local de sesión. No se consulta AD en cada petición.

El contrato administrativo se define en RF-002, secciones 5.4 y 5.5: consulta
paginada de identidades locales, lectura de asignación con ETag y PUT con
If-Match, taller y motivo obligatorio. Solo Bearer de Administrador con sesión
vigente puede ejecutarlo. La primera asignación cubre al Asesor pendiente;
después solo admite sustitución, sin retirada. El Administrador puede quedar
sin taller, conforme a la comprobación de rol definida allí. Application valida
entrada y coordina asignación, auditoría y revocación en una transacción;
Infrastructure implementa las restricciones y comparación atómica de versión
sin exponer tipos de concurrencia de SQL Server en los contratos.

## 6. Tokens y tiempo

Firma asimétrica RS256 mediante bibliotecas establecidas. Claves privadas fuera
del repositorio y configuración compartida; referencia al certificado desde
configuración privada, `kid` y claves de verificación para rotación. No aceptar
algoritmos o claves elegidos por el cliente fuera de la configuración permitida.

Ambos JWT incluyen `iss`, `aud`, `sub`, `iat`, `nbf`, `exp`, `jti`, `sid` y
`token_use`. Acceso y refresh usan audiencias separadas y un tipo de uso
distinguible, sin intercambio entre ellos. `sub` identifica al usuario local,
`sid` a la sesión y `jti` es único por token. Los refresh tokens no incorporan
roles ni permisos utilizables por endpoints y no contienen claim `role`.
El access token incluye un único claim `role` con valor exacto `administrator`
o `advisor`, coincidente con el rol efectivo de la sesión y el JSON de login.
Validar su presencia, unicidad y pertenencia al conjunto permitido sin aceptar
alias traducidos ni nombres de grupos como roles. En Api configurar
`MapInboundClaims=false` y `TokenValidationParameters.RoleClaimType="role"`
para aplicar las políticas a ese claim explícito, sin depender de conversiones
implícitas. Referencia: [mapeo de claims en ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/claims?view=aspnetcore-10.0).
`token_use` es un único string obligatorio: `access` o `refresh`, con comparación
ordinal sensible a mayúsculas y solo en su flujo/audiencia correspondiente.
Rechazar ausencia, duplicados, otros tipos y valores alternativos. Los claims
requeridos son los aquí definidos, incluido `role` solo en acceso; no se requiere
añadir más claims de autorización para cerrar este alcance.

El contrato de uso y precisión temporal se define íntegramente en esta sección.
Para una sesión iniciada en `startedAt`, donde `floorSecond` trunca a segundos UTC:

```text
absoluteExpiresAt = startedAt + MaxSessionLifetime
issuedAt          = floorSecond(clock.GetUtcNow())
iat = nbf         = issuedAt.ToUnixTimeSeconds()
refreshExpiresAt  = floorSecond(min(issuedAt + RefreshTokenLifetime, absoluteExpiresAt))
accessExpiresAt   = floorSecond(min(issuedAt + AccessTokenLifetime, absoluteExpiresAt))
exp               = correspondingExpiresAt.ToUnixTimeSeconds()
```

Limitar también `accessExpiresAt` concreta el límite absoluto de RN-008;
la consulta de sesión sigue siendo obligatoria. Las renovaciones no cambian
`startedAt` ni `absoluteExpiresAt`. No se añade timeout de inactividad.
La tolerancia temporal es 0 segundos en ambos tipos de token. Configurar
explícitamente ClockSkew desde `Jwt:ClockSkewSeconds=0`, sin depender del valor
predeterminado de la biblioteca. El parámetro es obligatorio y solo admite 0
en el contrato actual; las duraciones de vida son positivas y en segundos completos.
No confundir la precisión de representación con una tolerancia de aceptación.

Exigir claims temporales únicos, enteros NumericDate y dentro del rango
DateTimeOffset soportado. No aceptar strings, fracciones o sustituciones
silenciosas. Al emitir, iat y nbf coinciden y exp debe ser posterior a ambos.
Al validar, usar TimeProvider UTC sin truncar la hora actual y comprobar
`nbf <= now < exp`, orden temporal coherente y sesión aún vigente. No heredar
una comparación inclusiva de exp de la biblioteca: verificar expresamente
`now == exp` en tests y añadir la comprobación estricta necesaria sin desactivar
firma, algoritmo, emisor, audiencia, tipo o demás validaciones.

DateTimeOffset de emisión/expiración de tokens conserva el segundo representado
por NumericDate. El JSON `accessTokenExpiresAt` se obtiene del mismo exp y se
serializa con Z y sin fracciones; la fecha Expires de cookie y el registro SQL
de refresh coinciden con el exp del refresh. No redondear el límite absoluto
hacia arriba ni truncar el reloj de comprobación para prolongar acceso. Mantener
el inicio real de sesión y precisión de auditoría; no imponerles la truncación
propia del transporte JWT. Si ya se alcanzó exp al terminar la emisión, no
entregar tokens como un resultado satisfactorio.

Referencia de configuración: [ClockSkew de IdentityModel](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.tokens.tokenvalidationparameters.clockskew?view=msal-web-dotnet-latest).
La tolerancia cero y los enteros son política del proyecto, no una obligación
universal de JWT. Comprobar sincronización de relojes en el despliegue.

## 7. Persistencia y concurrencia

EF Core con SQL Server en Infrastructure. Modelo relacional:

| Registro | Campos mínimos |
| --- | --- |
| Sesión | `Id`, `UserId`, rol efectivo al crear la sesión, `StartedAt`, `AbsoluteExpiresAt`, `ClosedAt`, `CloseReason` y control de concurrencia |
| Refresh token | `Id`/`jti`, `SessionId`, `TokenHash`, `IssuedAt`, `ExpiresAt`, `ConsumedAt`, `RevokedAt` y control de concurrencia |

Definir claves foráneas, longitudes, precisión temporal y restricciones
explícitas en mappings separados. La huella SHA-256 y el identificador del token
deben tener unicidad. Un índice único filtrado por sesión no cerrada para cada
usuario puede respaldar la sesión única en SQL Server; este mecanismo pertenece
exclusivamente a Infrastructure. Una sesión vencida aún no marcada como cerrada
se cierra dentro de la sustitución transaccional al crear otra.

El consumo usa una actualización condicional que exige sesión activa, token
vigente y no consumido; el número de filas afectadas determina el resultado.
Combinarlo con serialización de las transiciones de sesión para impedir que una
rotación posterior a un cierre vuelva a permitir acceso. No mantener
transacciones SQL abiertas durante LDAPS, ni compartir DbContext concurrentemente.

El cierre persistido de sesión invalida todos sus accesos y refresh tokens,
aunque no se actualice físicamente cada registro hijo. La revocación por
reutilización debe confirmarse aun cuando el resultado HTTP sea un rechazo;
no revertirla al tratar el caso como error de aplicación.

Conservar registros consumidos mientras exista una familia revocable para
detectar reutilización y aplicar después la retención aprobada en 7.2.
Las migraciones se versionan y revisan; no usar
`EnsureCreated` ni aplicar migraciones de producción automáticamente al arrancar.

### 7.1. Limitación compartida de autenticación

Esta sección concreta RF-001 / RN-016. Producción utiliza varios procesos/servidores y SQL Server
como autoridad compartida de admisión. No usar cuatro limiters independientes
en memoria como sustituto de las cuotas globales.

**Contrato de Application:** `IAuthenticationRateLimitStore` expone admisión
asíncrona de una cuota por clave lógica y devuelve permitido/rechazado, instante
UTC de decisión y próximo instante conocido de capacidad. No acepta HttpContext,
direcciones de cabeceras sin validar, tipos SQL o IQueryable. Los fallos de
almacén siguen los resultados técnicos y ProblemDetails de 4.3.1.

Api aplica la etapa IP antes del procesamiento de cuerpo/credenciales/tokens.
Tras los controles HTTP y validación de username, el login aplica usuario; el
refresh lo aplica después de verificar JWT y asociación con sesión. Reutilizar
una sola admisión por etapa, sin contar de nuevo en middleware y handler.
El resultado de limitación impide invocar los pasos de AD y mutación de sesión.

| Política | Partición lógica | Límite inicial / ventana |
| --- | --- | --- |
| login-ip | Namespace del entorno/API + IP canónica | 300 / 60 segundos |
| login-user | Namespace + dominio/usuario normalizados | 5 / 60 segundos |
| refresh-ip | Namespace + IP canónica | 600 / 60 segundos |
| refresh-user | Namespace + Id local del usuario validado | 10 / 60 segundos |

Usar la misma normalización ordinal de dominio/usuario que la autenticación;
usuario sin dominio y con dominio predeterminado explícito comparten clave.
No usar jti/sid, contraseña o token como clave por usuario. Para IP, normalizar
representaciones equivalentes y IPv4 mapeada en IPv6; aplicar solo proxies/redes
de confianza configurados. Ausencia de IP fiable usa una partición desconocida
común, no una clave aleatoria por solicitud. No registrar claves de identidad
o IP indiscriminadamente en logs.

Infrastructure almacena una huella SHA-256 de una representación canónica y
sin ambigüedad de namespace/política/sujeto para tamaño de clave acotado; nunca
incluye contraseñas o JWT. La huella no se trata como anonimización de datos.
Todas las instancias utilizan el mismo algoritmo y política de canonicalización.

**Modelo relacional de admisión:**

| Registro | Datos mínimos |
| --- | --- |
| Política compartida | Namespace, identificador de política, versión/huella de configuración y parámetros de límite/ventana |
| Partición | Política, huella del sujeto y mecanismo de serialización; clave única |
| Permiso admitido | Id único, partición y AcceptedAt UTC con precisión de almacén |

En una transacción corta por partición: obtener/crear la fila con unicidad,
serializar acceso, obtener t del reloj de SQL Server y evaluar permisos con
AcceptedAt dentro de `(t - ventana, t]`. Con capacidad, insertar uno y confirmar;
sin capacidad, no insertar y obtener el instante en que expiran los permisos
necesarios para admitir el siguiente. No usar ventanas fijas ni segmentadas.
El reloj compartido y las expresiones SQL quedan exclusivamente en Infrastructure.

La combinación lectura/conteo/inserción exige bloqueo o condición transaccional
que proteja contra inserciones concurrentes de la misma partición, también al
crear su primera fila. Una lectura snapshot/no-tracking por sí sola no serializa
admisión. Indexar partición y AcceptedAt para no escanear todos los sujetos.
La creación concurrente de partición no puede convertirse en otro presupuesto.

No mantener esta transacción abierta mientras se valida AD, firma tokens o
modifica sesiones. Los permisos ya confirmados permanecen ante un error
posterior; las dos etapas IP/usuario no son una reserva conjunta reembolsable.
Si falla SQL o no puede confirmarse el permiso, no admitir la operación ni
reintentar automáticamente la inserción. Un commit de permiso incierto devuelve
el resultado técnico de 4.3.1 y no ejecuta autenticación después de la incertidumbre.

Calcular Retry-After con los próximos instantes conocidos y el reloj de
decisión compartido: `max(1, ceil((nextAllowedAt - decisionTime).TotalSeconds))`.
Si varias cuotas conocidas están bloqueadas, usar el máximo de sus esperas.
Para una partición con N permisos y límite L, liberar los suficientes para
quedar por debajo de L; normalmente N=L y basta la expiración del más antiguo.
Si IP rechaza, no resolver otra identidad para estimar su espera. El resultado
es una estimación de capacidad sin reserva frente a otras solicitudes.

La respuesta 429 conserva el mensaje de 4.3, ProblemDetails y no-store;
no cambia cookies, consume refresh, revoca familias ni consulta credenciales.
No encolar ni dormir solicitudes en el servidor (`QueueLimit=0` si se usa una
integración del middleware). SQL no disponible es 503, nunca falso 429 ni
fallback en memoria. Otros limitadores de borde no sustituyen este contrato.

Persistir permisos a través de reciclados, reinicios y cambios de instancia.
Al iniciar, comprobar que la configuración local validada coincide con la
versión activa compartida; una discrepancia impide habilitar esa instancia,
sin reescribir parámetros o borrar permisos para acomodarla. No recarga dinámica
independiente. Un cambio de política requiere despliegue coordinado que preserve
histórico relevante; si se amplía ventana sin histórico suficiente, detener
admisiones durante la nueva ventana antes de activar la política ampliada.

Excluir permisos vencidos del cálculo aunque su borrado físico sea posterior.
Permitir limpieza acotada de permisos/particiones que ya no influyen en una
política, sin borrar datos activos ni confundir esta expiración operativa con
retención de sesiones, refresh o auditoría. Probar carga y concurrencia antes
de desplegar: la estimación de 200 usuarios no demuestra capacidad del almacén.

### 7.2. Retención y limpieza diaria

Esta sección concreta RF-001 / RN-019. La política aprobada distingue validez de autorización,
elegibilidad de borrado y ejecución física del borrado.

```text
endedAt = min(AbsoluteExpiresAt, ClosedAt si existe)
sessionEligibleAt = endedAt + 30 días
refreshRecordsEligibleAt = endedAt + 24 horas
auditEntryEligibleAt = occurredAt + 365 días
```

UTC y días de 24 horas; elegible si `now >= eligibleAt`. Una sesión sin cierre
usa su expiración absoluta, sin requerir un job que la marque cerrada. No
reiniciar el plazo con un cierre tardío ni eliminar huellas de tokens consumidos
o individualmente expirados de una familia aún revocable. Consultar siempre
sesión/expiración antes de autorizar, aunque sus registros se conserven 30 días.

| Opción tipada configurable | Valor inicial |
| --- | --- |
| SessionRetentionDays | 30 |
| RefreshRetentionHoursAfterSessionEnd | 24 |
| AuditRetentionDays | 365 |
| CleanupIntervalHours | 24 |

Validar presencia y valores positivos al arrancar. La conservación de sesión
debe cubrir al menos el margen de huellas. El tamaño de lote es una opción
operativa positiva y acotada, documentada con la implementación; no fija el
plazo de retención ni permite adelantar la elegibilidad.

En RF-001 el supervisor limpia sesiones, huellas y eventos de aprovisionamiento
propios de autenticación. La auditoría administrativa se integra cuando RF-002
incorpore su persistencia, reutilizando el mismo supervisor y política; no se
consulta una tabla inexistente ni se exige implementar RF-002 para limpiar o
verificar RF-001. No crear un segundo ejecutor para el tramo administrativo.

**Contratos y responsabilidades:** Application define el caso de uso de
retención y resultados de lote, con reloj controlable y contratos de lectura/
eliminación condicional y coordinación. Infrastructure implementa consultas,
borrado EF/SQL, lease compartido y el servicio en segundo plano supervisado.
Domain no depende de temporizadores, hosting o persistencia. No crear endpoints
de borrado ni permitir que una petición cliente cambie los umbrales.

El supervisor comprueba el calendario compartido y ejecuta cuando corresponde,
cada 24 horas; si el servicio estuvo detenido, procesa los elegibles pendientes
al recuperar la ejecución. No iniciar fire-and-forget ni realizar borrado
masivo en el arranque antes de registrar/coordinar el trabajo. Cada lote usa
un scope de DbContext independiente y propaga cancelación.

Guardar lease, propietario, vencimiento y progreso/calendario en SQL Server,
con un único propietario vigente por entorno. Adquirir/renovar de forma atómica
y verificar la propiedad dentro de cada lote; un ejecutor que perdió el lease
no continúa borrando mientras otro ya es propietario. Reloj de coordinación
compartido, sin locks exclusivos en memoria por servidor.

Tomar el instante UTC de corte del almacén compartido para cada lote, sin
permitir que el reloj adelantado de una instancia anticipe el borrado.
Seleccionar por fecha de elegibilidad y orden determinista, con índices para
evitar cargar todo el historial. Revalidar la condición al eliminar. Eliminar
primero registros de refresh elegibles y después sesiones elegibles que ya no
tengan referencias pendientes; la auditoría elegible se procesa separadamente.
No ejecutar cascadas hacia LocalUser, talleres, asignaciones, datos LPR o
historial todavía dentro de plazo. La auditoría es inmutable durante su
conservación y solo admite esta eliminación al llegar al umbral aprobado.

Confirmar lotes cortos, registrar cantidades/resultados/correlación sin datos
eliminados y conservar incidencias de limpieza para diagnóstico. Un fallo deja
el lote sin confirmar y el trabajo pendiente puede retomarse. Si se pierde
confirmación de un lote, releer elegibilidad/progreso antes de continuar;
repetir una eliminación condicional de datos elegibles es seguro, sin repetir
comandos no idempotentes de autenticación. El error no reactiva sesiones,
reduce plazos, elimina auditoría adicional ni se presenta como limpieza completa.

**Restauración:** estos plazos solo cubren la base operativa. El runbook de
copias documenta por separado su conservación. Antes de abrir tráfico tras
restaurar, reaplicar retención y comprobar expiraciones/revocaciones. Si no
pueden reconciliarse revocaciones posteriores al punto restaurado, no habilitar
esas sesiones: invalidarlas antes de permitir nuevos logins. No dar por segura
una sesión únicamente porque figura activa en una copia antigua. Verificar
este procedimiento con una restauración aislada, sin acceder a producción.

## 8. Configuración y despliegue

Desarrollo y producción; alojamiento inicial Windows/IIS con varios procesos
o servidores de API y estado SQL compartido. Web de producción
`https://garajemetria.com` y API `https://api.garajemetria.com`. La estimación de
200 usuarios no acredita concurrencia ni rendimiento.

`appsettings.json` y sus variantes contienen solo valores compartibles. Un
archivo JSON privado externo obligatorio se localiza mediante
`GARAJEMETRIA_PRIVATE_CONFIG`; la variable contiene la ruta, no los secretos.
No versionar, publicar como contenido web ni incluir el archivo en artefactos.
Restringir permisos NTFS del archivo y certificado a las identidades autorizadas.

| Grupo | Contenido |
| --- | --- |
| `Directory` | Servidores LDAPS, dominio predeterminado y permitidos, timeouts y credenciales técnicas si son necesarias |
| `ConnectionStrings` | Conexión SQL Server y modalidad de autenticación del entorno |
| `Jwt` | Emisor, audiencias separadas, referencia de certificado y claves de verificación |
| `Http` | Orígenes, proxies confiables y parámetros del despliegue |
| Opciones de sesión, entrada y limitación | Duraciones, límites y ventanas; nombres de claves a concretar en implementación |

| Duración configurable | Valor inicial |
| --- | --- |
| Access token | 10 minutos |
| Refresh token desde su emisión | 30 minutos, sujeto al límite absoluto |
| Sesión desde login | 8 horas |

Los límites de entrada se definen en 4.1, las cuotas en 7.1, la tolerancia JWT
en 6 y los plazos de retención en 7.2. Esas secciones son las fuentes de sus
valores iniciales; no mantener otra copia en la configuración documentada.

Estos valores se suministran explícitamente por archivo; no son constantes ni
fallbacks silenciosos del código. Validar opciones tipadas al arrancar:
duraciones y límites positivos, unidades explícitas, emisor y audiencias,
configuración de firma y LDAPS, dominio predeterminado permitido y parámetros
obligatorios completos. La ausencia o invalidez impide el arranque y genera un
diagnóstico sin secretos.

Validar las opciones de entrada conforme al presupuesto y las restricciones
establecidas en 4.1; no introducir reglas locales de fortaleza de contraseña.

El límite por usuario usa dominio/usuario normalizados en login y la identidad
de sesión validada en refresh. El límite por IP también protege solicitudes
sin identidad válida. Aceptar cabeceras reenviadas solo de proxies confiables.
Contraseñas y tokens nunca forman parte de claves de limitación ni logs.

## 9. Observabilidad y fallos

Registrar eventos estructurados de éxito, rechazo, indisponibilidad, cierre,
reutilización y limitación con correlación de solicitud. No registrar cuerpos
de login, cabeceras de autorización, cookies, contraseñas, JWT ni secretos de
configuración. Mantener diagnósticos internos separados de los mensajes públicos.

Aplicar timeouts acotados y cancelación. Un fallo SQL no permite emitir tokens
sin estado confirmado ni autorizar operaciones sin verificar sesión. Un fallo
de firma no puede dejar una rotación parcial confirmada. La pérdida de respuesta
tras un commit no implica que el servidor haya revertido la operación.

## 10. Verificación y trazabilidad

El catálogo conserva los 85 CP y sus resultados esperados, agrupados en los 20
criterios de RF-001. El estado de implementación, pruebas ejecutadas y
verificaciones externas pendientes se registra en TASKS-001. No interpretar
la presencia de un escenario como evidencia de ejecución. Mantener referencias
de CA y CP sin asumir una correspondencia uno a uno entre sus números.

### 10.1. Matriz de aceptación y cobertura

| Criterio de RF-001 | Reglas funcionales | Comportamiento | Casos de prueba |
| --- | --- | --- | --- |
| CA-001 | RN-004 | Inicio de sesión autorizado | CP-001, CP-046 |
| CA-002 | RN-005 | Validación de entrada | CP-004, CP-057, CP-058, CP-059, CP-060 |
| CA-003 | RN-001 | Identidad corporativa | CP-012, CP-013, CP-014, CP-061 |
| CA-004 | RN-002, RN-014 | Rechazo de autenticación y autorización | CP-002, CP-008, CP-015, CP-036, CP-037 |
| CA-005 | RN-002, RN-003 | Rol y acceso inicial | CP-009, CP-010, CP-011, CP-027, CP-068, CP-069 |
| CA-006 | RN-007 | Sesión única | CP-016, CP-017, CP-018, CP-042 |
| CA-007 | RN-008 | Vigencia temporal | CP-005, CP-024, CP-031, CP-032, CP-033, CP-034, CP-071, CP-072 |
| CA-008 | RN-009 | Renovación autorizada | CP-007, CP-020, CP-021, CP-028, CP-029, CP-062, CP-063 |
| CA-009 | RN-010 | Detección de reutilización | CP-022, CP-023, CP-054 |
| CA-010 | RN-011 | Cierre y sesión finalizada | CP-019, CP-025, CP-048, CP-050 |
| CA-011 | RN-015 | Verificación de acceso | CP-052, CP-053, CP-070 |
| CA-012 | RN-004 | Contrato de transporte | CP-039, CP-040, CP-044, CP-045, CP-049, CP-056 |
| CA-013 | RN-017 | Protección antiforgery y origen | CP-055, CP-064, CP-065, CP-066, CP-067 |
| CA-014 | RN-013, RN-014 | Fallos y atomicidad | CP-003, CP-030, CP-038, CP-047, CP-073, CP-074, CP-075, CP-076 |
| CA-015 | RN-016 | Limitación compartida | CP-043, CP-051, CP-077, CP-078, CP-079, CP-080 |
| CA-016 | RN-001, RN-006 | Protección de credenciales y conexión | CP-006, CP-041 |
| CA-017 | RN-018 | Configuración válida | CP-035 |
| CA-018 | RN-019 | Retención operativa | CP-081, CP-082, CP-083 |
| CA-019 | RN-019 | Recuperación de servicio | CP-084 |
| CA-020 | RN-012 | Cambios de autorización | CP-026, CP-085 |

### 10.2. Casos de prueba y resultados esperados

Los identificadores CP mantienen la numeración anterior a la agrupación. Cada
CP conserva el resultado verificable del antiguo CA del mismo número; ese CA
antiguo queda sustituido por el criterio agrupado indicado en esta tabla.
Los contratos técnicos de las secciones anteriores siguen siendo obligatorios.

| Caso | Criterio actual | Escenario y variantes | Resultado esperado |
| --- | --- | --- | --- |
| CP-001 | CA-001 | login satisfactorio. | Con credenciales válidas y acceso autorizado, el login devuelve un JWT de acceso y un JWT de refrescamiento. |
| CP-002 | CA-004 | credenciales inválidas. | Con credenciales rechazadas por el directorio, no se emite ninguno de los tokens. |
| CP-003 | CA-014 | fallo del directorio. | Si Active Directory no está disponible o la validación falla inesperadamente, no se emiten tokens ni se comunica éxito. |
| CP-004 | CA-002 | campos ausentes/nulos/vacíos, tipos, formatos y longitudes en el límite y por encima. | Una solicitud que incumple el esquema, formato o longitudes de 4.1 se rechaza antes de validar credenciales o modificar sesiones. |
| CP-005 | CA-007 | configuración temporal y expiración con reloj determinista. | Los tiempos del ciclo de sesión proceden del archivo de configuración y se aplican a los tokens emitidos conforme a las políticas acordadas. |
| CP-006 | CA-016 | persistencia, logs y errores sin credenciales ni tokens. | La aplicación no almacena contraseñas ni incluye contraseñas o tokens en logs o errores. |
| CP-007 | CA-008 | renovación rechazada. | Un token de refrescamiento inválido o expirado no permite renovar la sesión. |
| CP-008 | CA-004 | usuario sin rol derivado de AD. | Con credenciales válidas pero sin pertenencia autorizante a los grupos de RF-002, se rechaza el acceso y no se emiten tokens. |
| CP-009 | CA-005 | Administrador sin taller. | Con credenciales válidas y rol Administrador, la ausencia de taller asignado no impide el login ni seleccionar cualquier taller. |
| CP-010 | CA-005 | Asesor con taller. | Con credenciales válidas, rol Asesor y taller asignado, el login permite acceder únicamente al taller asociado. |
| CP-011 | CA-005 | Asesor sin taller. | Con credenciales válidas y rol Asesor sin taller asignado, se rechaza el acceso y no se emiten tokens. |
| CP-012 | CA-003 | usuario sin dominio explícito. | Si el login recibe solo `usuario`, la validación se realiza con el dominio predeterminado del archivo de configuración. |
| CP-013 | CA-003 | usuario con dominio explícito. | Si el login recibe `DOMINIO\usuario`, la validación utiliza el dominio indicado en lugar del predeterminado. |
| CP-014 | CA-003 | dominio no permitido. | Si el dominio resuelto no está en la lista configurable de dominios permitidos, se rechaza el acceso sin validar credenciales contra el directorio ni emitir tokens. |
| CP-015 | CA-004 | rechazo genérico para cada estado de cuenta. | Una cuenta bloqueada, deshabilitada o con contraseña expirada no obtiene tokens y recibe el mismo mensaje «No se pudo iniciar sesión», sin detalles sobre su estado. |
| CP-016 | CA-006 | sustitución de la sesión anterior. | Tras un nuevo login satisfactorio, la sesión anterior del mismo usuario no permite operaciones protegidas ni renovación; la nueva sí permite el acceso autorizado. |
| CP-017 | CA-006 | fallo de login con sesión existente. | Un intento de login fallido no cierra la sesión activa del usuario. |
| CP-018 | CA-006 | concurrencia en el inicio de sesión. | Los logins concurrentes del mismo usuario no dejan más de una sesión activa. |
| CP-019 | CA-010 | invalidación inmediata de ambos tokens al cerrar sesión. | Tras cerrar sesión explícitamente, ni el token de acceso ni el de refrescamiento permiten nuevas operaciones o renovación, aun sin haber expirado. |
| CP-020 | CA-008 | rotación y uso único del token de refrescamiento. | Una renovación satisfactoria devuelve nuevos tokens de acceso y refrescamiento e invalida el token de refrescamiento utilizado. Un segundo uso del anterior se rechaza. |
| CP-021 | CA-008 | consumo concurrente del token de refrescamiento. | Ante renovaciones concurrentes con el mismo token de refrescamiento, como máximo una puede consumirlo satisfactoriamente. |
| CP-022 | CA-009 | reuso consumido justo antes/en/después de exp, con sucesor y sesión activos. | Antes de exp, el reuso auténtico y consumido revoca la familia, incluido el sucesor. En exp o después devuelve 401 sin revocar ni cambiar cookies/vigencias; el sucesor sigue utilizable si cumple sus propias condiciones. |
| CP-023 | CA-009 | renovaciones concurrentes y espera de transacción que cruza exp. | Solo una consume. Si la otra detecta reuso mientras el token sigue vigente, revoca también los tokens ganadores; si el token ya expiró al decidir, devuelve 401 sin revocar el sucesor. |
| CP-024 | CA-007 | vigencia del acceso anterior con reloj determinista. | Tras una renovación normal, el token de acceso anterior sigue permitiendo operaciones autorizadas hasta su expiración mientras la sesión permanezca activa. Expirado, se rechaza. |
| CP-025 | CA-010 | invalidación de accesos anteriores al cerrar o revocar la sesión. | El cierre de sesión, su sustitución por un nuevo login o la revocación por reutilización de un refresh token invalidan todos los tokens de acceso aún vigentes de esa sesión, incluidos los de renovaciones anteriores. |
| CP-026 | CA-020 | cambio de taller, cambio de rol y pérdida de grupos, respetando el momento de detección. | El cambio local efectivo de taller invalida la sesión inmediatamente. El cambio efectivo de rol en AD o pérdida de acceso cierra la sesión al detectarse en refresh, con 401 y sin nuevos tokens; exige nuevo login. |
| CP-027 | CA-005 | cambio de contexto de consulta del Administrador. | Un Administrador puede seleccionar otro taller de consulta sin que esa selección cierre su sesión ni modifique su taller asignado. |
| CP-028 | CA-008 | comprobación de estado en cada renovación. | Cada renovación con un token de refrescamiento válido comprueba el estado del usuario en Active Directory antes de emitir nuevos tokens. |
| CP-029 | CA-008 | cada estado de cuenta rechazado durante la renovación. | Si el usuario está deshabilitado, bloqueado o con contraseña expirada al renovar, no se emiten nuevos tokens y todos los tokens vigentes de su sesión dejan de permitir acceso o renovación. No se revela el estado específico. |
| CP-030 | CA-014 | fallo temporal del directorio y reintento de renovación. | Si Active Directory no está disponible, se rechaza temporalmente la renovación sin emitir tokens, cerrar la sesión ni consumir el token de refrescamiento. Al recuperarse el directorio, el mismo token permite reintentar si sigue vigente y la sesión está activa. |
| CP-031 | CA-007 | expiración sin extensión durante una indisponibilidad del directorio. | Un fallo temporal del directorio durante la renovación no modifica la expiración de los tokens existentes; los de acceso siguen sujetos a su vigencia normal. |
| CP-032 | CA-007 | renovaciones sucesivas con reloj determinista. | Renovar una sesión no modifica su inicio ni su límite absoluto, calculado desde el login con la duración máxima del archivo de configuración. |
| CP-033 | CA-007 | límite absoluto de sesión y nuevo login. | Al alcanzar la duración máxima de la sesión, se rechazan operaciones protegidas y renovaciones, incluso con tokens cuya expiración individual sea posterior. Solo un nuevo login permite crear otra sesión. |
| CP-034 | CA-007 | expiración de tokens de refrescamiento al inicio y cerca del límite de sesión, con reloj determinista. | El token de refrescamiento emitido en el login o en una renovación expira en el menor instante entre su emisión más la duración configurada y el límite absoluto de la sesión. |
| CP-035 | CA-017 | configuración incompleta o inválida. | La configuración obligatoria ausente o inválida impide el arranque y produce un diagnóstico en logs sin secretos. |
| CP-036 | CA-004 | autenticación rechazada sin divulgación de información. | Credenciales incorrectas y dominio no permitido producen el mensaje genérico «No se pudo iniciar sesión», sin detalles del rechazo. |
| CP-037 | CA-004 | separación entre autenticación y autorización. | Tras autenticar las credenciales, la ausencia de rol o de taller para un Asesor produce el mensaje descriptivo correspondiente de 4.3, sin tokens. Esos mensajes no se muestran si las credenciales no son válidas. |
| CP-038 | CA-014 | mensaje de indisponibilidad en ambos flujos. | La indisponibilidad de Active Directory produce el mensaje temporal de 4.3 tanto en login como en renovación, respetando RN-013 en esta última. |
| CP-039 | CA-012 | nombre exacto, uniformidad de entrega y atributos de cookie. | Todos los consumidores reciben el access token en JSON y el refresh token exclusivamente en `__Secure-GarajeMetria.RefreshToken`, HttpOnly y Secure con `SameSite=Strict`, sin selección por plataforma. |
| CP-040 | CA-012 | consumo del mismo contrato desde aplicaciones HTTP independientes. | El token de acceso se recibe en JSON y se presenta en `Authorization: Bearer` desde cualquier consumidor autorizado. |
| CP-041 | CA-016 | conexión LDAPS y rechazo de certificado inválido en entorno aislado. | La integración con Active Directory usa LDAPS y rechaza certificados de servidor no válidos sin emitir tokens ni recurrir a una conexión sin validación. |
| CP-042 | CA-006 | persistencia de sesiones y familias contra SQL Server aislado. | El estado persistido en SQL Server permite aplicar sesión única, revocación y detección de reutilización conforme a los criterios anteriores. |
| CP-043 | CA-015 | límites por IP y usuario con las ventanas configuradas. | Login y renovación aplican los límites configurados por IP y por usuario. |
| CP-044 | CA-012 | rutas y métodos HTTP de autenticación. | Las operaciones de login, renovación y logout se exponen mediante POST en las rutas de 4.1. |
| CP-045 | CA-012 | solicitudes, nombre de cookie y rechazo de transportes/nombres alternativos. | El login acepta JSON con `username` y `password`; refresh y logout leen exclusivamente la cookie `__Secure-GarajeMetria.RefreshToken`, sin nombres alternativos ni campo JSON `refreshToken`. |
| CP-046 | CA-001 | respuesta uniforme de login y Administrador sin taller. | Un login satisfactorio incluye los datos de 4.2 mediante un contrato único y admite Administrador sin taller predeterminado. |
| CP-047 | CA-014 | códigos HTTP y formato de errores. | Los errores de entrada, autenticación, autorización, tamaño de cuerpo, formato de contenido, limitación, error inesperado e indisponibilidad devuelven respectivamente 400, 401, 403, 413, 415, 429, 500 y 503 con ProblemDetails, según 4.3.1. |
| CP-048 | CA-010 | logout con acceso expirado desde cualquier consumidor. | Con access token expirado pero refresh token vigente y sesión activa, el logout permite cerrar la sesión e invalida todos sus tokens, sin renovación previa. |
| CP-049 | CA-012 | códigos de éxito y respuesta de renovación sin perfil. | Login y renovación satisfactorios devuelven 200; logout devuelve 204 sin cuerpo. La renovación no contiene el objeto de usuario ni datos de rol o taller. |
| CP-050 | CA-010 | sesión finalizada y token de refrescamiento no válido. | Un refresh token inválido o expirado o una sesión cerrada o expirada produce 401 con el mensaje de 4.3. |
| CP-051 | CA-015 | mensaje y tiempo de reintento de limitación. | Superar los límites produce 429, el mensaje de 4.3 y Retry-After en segundos. |
| CP-052 | CA-011 | validación completa y separación de tipos de token. | Se rechazan JWT con firma, algoritmo, emisor, audiencia, tipo o vigencia incorrectos; un refresh token no autoriza endpoints de negocio. |
| CP-053 | CA-011 | validación de estado de sesión y fallo temporal de persistencia. | Un access token válido no autoriza operaciones si su sesión está revocada o expirada o no puede verificarse; no se conserva acceso mediante caché obsoleta. |
| CP-054 | CA-009 | persistencia de huellas y detección de reutilización. | SQL Server conserva huellas y metadatos de refresh tokens sin valores recuperables, y permite detectar el reuso de tokens consumidos de la familia activa. |
| CP-055 | CA-013 | controles de cookie, CSRF y eliminación al cerrar sesión. | Todos los consumidores aplican los controles antiforgery y de origen de 4.4.1 sin mutar la sesión en solicitudes rechazadas; logout elimina las cookies con sus alcances originales. |
| CP-056 | CA-012 | cabeceras de caché y ausencia de refresh token en JSON. | Las respuestas con tokens utilizan Cache-Control no-store y nunca incluyen el refresh token en el cuerpo JSON. |
| CP-057 | CA-002 | tamaño exacto y un byte superior, lectura por fragmentos y despliegue IIS aislado. | El límite de cuerpo se aplica con y sin Content-Length; excederlo devuelve 413 antes de autenticar o modificar sesiones. |
| CP-058 | CA-002 | matriz de transporte y esquema estricto. | Tipos, charset o Content-Encoding no admitidos producen 415; JSON malformado, duplicados, campos adicionales o capitalización distinta producen 400. |
| CP-059 | CA-002 | preservación exacta, máximo de longitud, un carácter superior y ausencia de valores en errores/logs. | Contraseñas con espacios, Unicode y caracteres especiales llegan intactas al adaptador; vacías, NUL o Unicode inválido se rechazan sin bind anónimo. |
| CP-060 | CA-002 | formato, espacios de borde, Unicode, separadores, UPN y equivalencia independiente de cultura. | Usuario simple y dominio explícito se validan sin truncar ni sanear; formatos alternativos se rechazan y las variantes de mayúsculas comparten resolución y limitación. |
| CP-061 | CA-003 | identidad estable y ausencia de reasignación por nombre. | Renombrar o mover una cuenta conserva el vínculo por objectGUID; eliminarla y recrearla con el mismo nombre no transfiere acceso local ni sesiones. |
| CP-062 | CA-008 | cuenta habilitada, deshabilitada, bloqueada, contraseña expirada, cambio obligatorio y contraseña sin caducidad. | Cada renovación consulta mediante la identidad de servicio los estados AD definidos en 3.1.1 sin la contraseña del usuario y aplica los cierres correspondientes. |
| CP-063 | CA-008 | resultados distintos para ausencia de objeto y estado indeterminado, y reintento tras recuperar permisos/conectividad. | Permisos insuficientes, atributos requeridos ausentes o errores de consulta devuelven 503 sin consumir refresh ni cerrar sesión; un objeto confirmado inexistente produce cierre y 401. |
| CP-064 | CA-013 | bootstrap anónimo, con cookie existente y con Bearer expirado. | El bootstrap devuelve requestToken/headerName, cookie antiforgery con los atributos definidos y no-store, sin sesión de usuario, tokens de autenticación ni cambios en la cookie de refresh. |
| CP-065 | CA-013 | pareja válida, ausente, duplicada, alterada y de otro almacén de cookies; navegador y cliente HTTP. | Login, refresh y logout requieren la pareja cookie/cabecera válida para todos los consumidores, también sin Origin ni Referer. |
| CP-066 | CA-013 | matriz de cabeceras, puertos, orígenes y Sec-Fetch-Site, incluidos ambos ausentes. | Origin/Referer se validan según precedencia y lista exacta; null y cross-site se rechazan con 403 sin cambios de sesión ni cookies. |
| CP-067 | CA-013 | errores ProblemDetails, ausencia de efectos y ciclo bootstrap/login/refresh/logout con contexto antiforgery consistente. | Los rechazos antiforgery devuelven el código y mensaje acordados sin consumir refresh; logout satisfactorio elimina ambas cookies con su alcance correcto y un nuevo bootstrap permite iniciar otro flujo. |
| CP-068 | CA-005 | matriz de roles y renovación, coordinada con los casos de RF-002. | Login y refresh resuelven roles desde grupos directos/anidados con prioridad Admin; lectura fallida no concede acceso ni consume refresh y un cambio efectivo exige nuevo login. |
| CP-069 | CA-005 | primer Administrador, Asesor pendiente, alta concurrente y exclusión de intentos no autenticados/sin rol. | El primer login con credenciales y grupos válidos registra una sola identidad local por objectGUID; un Asesor sin taller conserva su perfil con 403, sin sesión ni tokens, hasta recibir asignación. |
| CP-070 | CA-011 | matriz `access`/`refresh`, mayúsculas, ausencia, duplicados y uso cruzado. | Ambos tipos de JWT incluyen el `token_use` exacto; valores ausentes, duplicados, de tipo incorrecto o intercambiados se rechazan, aunque la firma sea válida. |
| CP-071 | CA-007 | reloj determinista justo antes/en/después de nbf, exp y límite absoluto; configuración de skew ausente o distinta de cero. | No se acepta un token antes de nbf ni en/después de exp, y no hay tolerancia ni extensión del límite de sesión. |
| CP-072 | CA-007 | emisión y sesión con fracciones, proximidad al límite absoluto, formato UTC con Z y rechazo de NumericDate inválidos. | Los claims temporales emitidos son enteros; JSON, cookie y persistencia coinciden con exp, sin redondeo hacia arriba. |
| CP-073 | CA-014 | token válido, fallo de lectura y recuperación sin invalidación artificial de credenciales. | Si SQL no permite verificar una sesión, la operación devuelve 503 ProblemDetails sin autorizar, cerrar la sesión ni emitir un 401/403 por ese fallo. |
| CP-074 | CA-014 | login, refresh, logout, revocación y clasificación segura de fallos. | Un fallo de escritura con rollback conocido no deja efectos parciales ni entrega tokens/cookies; errores transitorios producen 503 y los inesperados, 500. |
| CP-075 | CA-014 | pérdida de confirmación, ausencia de reintento automático y recuperación conforme al flujo. | Una interrupción de commit con resultado indeterminado no se presenta como rollback ni éxito: produce operation_outcome_unknown cuando puede responderse, sin repetir la escritura. |
| CP-076 | CA-014 | ProblemDetails, no-store, valores seguros y respuesta ya iniciada/conexión interrumpida. | Los errores de infraestructura cumplen el formato, códigos, mensajes y correlación de 4.3.1, sin información sensible, Retry-After inventado ni cambios de cookies. |
| CP-077 | CA-015 | límite exacto, una solicitud superior y fronteras temporales con reloj controlado. | La ventana deslizante exacta conserva las cuotas acordadas sin ráfagas por cambio de minuto, y se libera capacidad cuando los permisos alcanzan su antigüedad de ventana. |
| CP-078 | CA-015 | al menos dos instancias contra el mismo SQL Server aislado y reinicio/solapamiento de procesos. | Procesos/servidores concurrentes comparten una sola cuota; reiniciar o reciclar una instancia no la reinicia ni permite excederla. |
| CP-079 | CA-015 | entradas inválidas, fallos AD, IP desconocida/proxies, normalización y rechazo por usuario después de admisión IP. | IP y usuario se cuentan por etapas; errores posteriores no devuelven permisos, los rechazos no prolongan la ventana y alias admitidos comparten la clave definida. |
| CP-080 | CA-015 | espera máxima conocida, indisponibilidad y commit de permiso incierto sin reintento ni ejecución de autenticación. | Una cuota agotada produce 429/Retry-After calculado, sin cola ni efectos en sesión; un almacén no verificable produce 503, sin permisos locales de emergencia. |
| CP-081 | CA-018 | antes/en/después de 30 días y 24 horas, cierre previo/posterior al límite absoluto y tokens consumidos/expirados en familia activa. | Sesiones y huellas solo se eliminan al ser elegibles según fin efectivo y sus plazos, sin borrar huellas de familias activas ni extender autorización. |
| CP-082 | CA-018 | frontera de 365 días, motivo inalterado y eliminación sin datos relacionados ajenos. | Auditoría administrativa y aprovisionamiento se conservan 365 días desde cada evento y se eliminan después por limpieza, sin edición de historial ni cascada sobre perfiles. |
| CP-083 | CA-018 | al menos dos ejecutores con SQL aislado, caída entre lotes, recuperación y configuración inválida. | La limpieza es diaria, por lotes y con coordinación compartida; reinicios o ejecutores concurrentes no borran datos no elegibles. |
| CP-084 | CA-019 | restauración aislada, sesiones vencidas/revocaciones posteriores al punto restaurado y exclusión de perfiles, talleres y datos LPR. | Una restauración no habilita acceso antes de verificar/reaplicar retención y revocaciones; los plazos de la base operativa no se presentan como borrado de copias. |
| CP-085 | CA-020 | publicación concurrente con login/refresh, sesiones antiguas tras reactivar, copia antigua durante caída y ausencia de carga inicial (503), según RF-009. | Un Asesor con taller inactivo recibe 403 en login; la desactivación publicada cierra sesiones y sus tokens reciben 401. La reactivación permite nuevo login con la misma asignación. |

### 10.3. Niveles de prueba y dependencias entre tramos

Las decisiones sobre pertenencia, asignación y actividad de taller se prueban
con dobles de contratos en Application durante RF-001. CP-010/027 incluyen
comprobaciones de recursos de negocio que se ejecutarán al integrar esos casos
de uso. CP-026 exige integración con mantenimiento de asignaciones de RF-002;
CP-082, su auditoría administrativa (la de aprovisionamiento sí se prueba aquí); CP-085, publicación de catálogo de RF-009.
Probar ahora la reacción de autenticación a sus estados/resultados no acredita
la implementación de esos productores. Las pruebas reales entre tramos quedan
identificadas como pendientes hasta integrar las funcionalidades correspondientes.
El aprovisionamiento propio del login y su auditoría sí se verifican en RF-001.

| Grupo de escenarios | Trazabilidad RF-001 | Nivel principal |
| --- | --- | --- |
| Credenciales, errores AD y validación previa | CP-001 a 004, 006, 015, 036 a 038 | Application y HTTP |
| Rol, taller e identidad de dominio | CP-008 a 014, 026, 027 | Domain/Application; integración con RF-002 |
| Expiraciones y configuración temporal | CP-005, 024, 031 a 035 | Domain/Application con reloj determinista; arranque |
| Sesión única y login fallido | CP-016 a 018 | Application y SQL Server concurrente |
| Logout e invalidación de accesos | CP-019, 025, 048 | Application, HTTP y SQL Server |
| Rotación, consumo único y reutilización | CP-007, 020 a 023, 028 a 030 | Application y SQL Server concurrente |
| Entrega uniforme, cookie, solicitudes y respuestas | CP-039, 040, 044 a 046, 049 | HTTP con WebApplicationFactory |
| LDAPS y certificados | CP-041 | Integración LDAPS aislada |
| Persistencia de sesiones y huellas | CP-042, 054 | SQL Server aislado |
| Rate limits y errores HTTP | CP-043, 047, 050, 051 | HTTP y configuración |
| Validación JWT y verificación de sesión | CP-052, 053 | Integración criptográfica y HTTP |
| CSRF, origen, eliminación de cookie y no-store | CP-055, 056 | HTTP; navegador para SameSite y CORS |
| Límite de cuerpo y formato estricto de entrada | CP-057, 058 | HTTP y alojamiento IIS aislado |
| Preservación de contraseña y formatos de identidad | CP-059, 060 | Application, HTTP y adaptador LDAPS aislado |
| Identidad estable y lectura de estado mediante servicio | CP-061 a 063 | Application y AD aislado con permisos reales de lectura |
| Bootstrap, antiforgery obligatorio y validación de origen | CP-064 a 067 | HTTP, navegador y cliente HTTP independiente |
| Roles AD y comprobación durante renovación | CP-068 | Application y AD aislado; matriz de RF-002 |
| Creación automática de identidad y Asesor pendiente | CP-069 | Application, HTTP y SQL Server aislado; primer acceso de RF-001, reutilizado por RF-002 |
| Contrato de uso y tiempo JWT | CP-070 a 072 | Validación criptográfica, reloj determinista, HTTP y mapeo temporal SQL Server |
| Fallos SQL y resultado de commit | CP-073 a 076 | Application, HTTP y SQL Server aislado con fallos controlados |
| Ventana exacta y cuotas compartidas | CP-077 a 080 | Application/HTTP y al menos dos instancias contra SQL Server aislado |
| Retención, limpieza y restauración | CP-081 a 084 | Reloj determinista, SQL Server aislado y dos ejecutores de limpieza |
| Actividad de taller y continuidad del catálogo | CP-085 | Application/HTTP y SQL Server concurrente; RF-009 |

Escenarios adicionales para comprobar la coordinación del diseño:

- Logout, cambio de asignación o nuevo login entre la consulta AD y el commit
  de refresh: no reactivar sesión ni entregar tokens autorizantes de la anterior.
- Dos refresh con el mismo token: como máximo uno consume. El segundo revoca
  los tokens del primero solo si el token reutilizado sigue vigente al decidir;
  si ya expiró, devuelve 401 sin revocar ni cambiar cookies. Probar espera SQL
  que cruce exp y reuso posterior con sucesor aún activo.
- Fallo SQL al sustituir sesión o rotar: sin sustitución/consumo parcial y sin
  tokens entregados; fallo al consultar sesión: operación protegida denegada.
- Instantes justo antes, en y después del límite absoluto, y tolerancia JWT:
  ningún token permite acceso más allá del límite de sesión.
- Tokens falsificados, de otro tipo o con asociación `sub`/`sid` incorrecta:
  no autorizan ni permiten revocar sesiones ajenas.
- Controles HTTP rechazados antes de ejecutar una mutación: sin consumo o cierre.
- Cambio de nombre de cuenta AD conservando identidad estable: sin crear una
  segunda identidad local por basarse exclusivamente en el nombre introducido.
- Límites exactos y un carácter superior usando caracteres BMP y suplementarios,
  con JSON UTF-8 literal y escapado; contar valores escalares, no unidades UTF-16.
- Password con espacios de borde, solo espacios, comillas y barras: preservar
  el valor; NUL, vacío y Unicode malformado: rechazar antes de usar el directorio.
- JSON duplicado/desconocido, tipos incorrectos y cuerpo excesivo fragmentado:
  rechazo seguro sin consumo, sustitución de sesión ni datos intentados en errores.
- Bootstrap con Bearer ausente/válido/expirado, posterior login/refresh/logout:
  mismo contexto antiforgery y sin exigir un acceso vigente para cerrar sesión.
- Orígenes válidos/inválidos/null, Referer de respaldo, ambos ausentes y señal
  cross-site; rechazo sin consumo de refresh ni revocación por reutilización.
- Pareja antiforgery tras reciclado y entre procesos IIS con key ring compartido;
  logout elimina cookies y el siguiente bootstrap permite iniciar un flujo nuevo.
- Nombre exacto `__Secure-GarajeMetria.RefreshToken` en login, refresh y logout,
  con atributos y alcance originales; una cookie con otro nombre no satisface
  el transporte de refresh. La cookie antiforgery no se confunde con ella.
- Primer acceso concurrente con mismo objectGUID: un único perfil, sin
  sobrescribir taller ni crear sesión para Asesor pendiente. El 403 conserva
  el perfil; credenciales inválidas o falta de rol no lo crean.
- Asignación concurrente con login/refresh: versión, auditoría y cierre coherentes;
  retirar taller a Asesor rechaza sin cambios. El mantenimiento administrativo
  conserva los casos de prueba propios de RF-002.
- JWT firmado con token_use ausente/duplicado, valor con otra capitalización o
  tipo distinto; validar además rechazo cruzado entre acceso y refresh.
- Justo antes/en/después de nbf y exp, incluyendo igualdad exacta con exp y
  fracciones en el límite de sesión. Expires de cookie, JSON y SQL coinciden
  con los segundos del JWT y nunca sobrepasan el límite absoluto.
- Lectura de sesión fallida con JWT válido: 503, sin challenge 401, ejecución
  protegida ni cambio de cookies. Tras recuperar SQL, aplicar el estado real.
- Fallo antes de commit con rollback confirmado y pérdida de confirmación
  durante commit: resultados distintos, sin afirmar rollback en el segundo
  ni ejecutar de nuevo el comando. Verificar también la respuesta ya iniciada.
- Dos o más instancias compiten por la última capacidad de una partición:
  solo una admite. Reinicios/reciclado no devuelven capacidad activa.
- Fronteras de ventana, alias equivalentes y fallo posterior al permiso IP:
  conteo exacto, sin reembolso por rechazo de usuario ni bloqueo prolongado
  por reintentos rechazados. Retry-After usa redondeo hacia arriba.
- Almacén indisponible, commit de permiso incierto y política discrepante:
  sin fallback de admisión; permisos vencidos no cuentan aunque no se hayan
  borrado. No introducir una segunda cuota por instancia.
- Retención antes/en/después del umbral, cierre temprano y cierre posterior a
  expiración absoluta; huellas individuales expiradas en familia activa se conservan.
- Dos ejecutores compiten por limpieza; pérdida de lease y caída entre lotes:
  sin borrado prematuro, sin cascadas y con recuperación de trabajo pendiente.
- Restauración aislada: reaplicar retención y no reabrir sesiones sin verificar
  revocaciones posteriores; la base operativa no acredita eliminación en backups.

Unit tests de Domain y Application sin red ni base de datos, con dobles de
repositorio/directorio y reloj controlado. Integración de persistencia solo
contra SQL Server aislado: prohibidos EF InMemory y SQLite como sustitutos.
Las pruebas LDAPS requieren un entorno y cuentas sintéticas, nunca producción.
El responsable de AD facilitará la validación de permisos y estados: bloqueo,
deshabilitación, expiración, cambio obligatorio, contraseña sin caducidad,
renombrado/movimiento y eliminación/recreación. Probar también denegación de
lectura y atributos ausentes sin convertirlos en cuenta habilitada. Estas
pruebas siguen pendientes de ejecución; la decisión de diseño ya está tomada.
Mantener las comprobaciones de arquitectura y agregar las dependencias reales
solo al implementar cada componente.

## 11. Secuencia de implementación SDD

1. Revisar los contratos definidos de autenticación y convertir los
   CA y CP correspondientes en escenarios verificables.
2. Implementar invariantes de Domain y pruebas temporales/de transición.
3. Incorporar MediatR y FluentValidation en Application; implementar contratos,
   comportamiento de validación y handlers con dobles de sus dependencias.
4. Implementar persistencia EF Core, mappings, migraciones y pruebas de
   restricciones, atomicidad y concurrencia contra SQL Server aislado.
5. Implementar LDAPS, JWT y configuración privada con validación al arranque;
   comprobar certificados, separación de tokens y fallos de infraestructura.
6. Implementar transporte HTTP, Bearer con consulta de sesión, cookie,
   antiforgery, CORS, límites y ProblemDetails; verificar con WebApplicationFactory.
7. Verificar los contratos de revocación con cambios controlados de estado local;
   registrar las pruebas de productores de RF-002/RF-009 para sus propios tramos.
8. Verificar flujos de navegador y consumidores HTTP, actualizar OpenAPI y
   documentar preparación del entorno y ejecución de pruebas.

Para dar por implementada la especificación: criterios aplicables cubiertos,
decisiones pendientes que afecten al alcance resueltas, documentación y OpenAPI
actualizados, sin nuevas advertencias ni cambios ajenos, y checks reales desde
la raíz: `dotnet restore`, `dotnet build --no-restore --configuration Release`,
`dotnet format --verify-no-changes --no-restore` y
`dotnet test --no-build --configuration Release`. Documentar los prerrequisitos
de SQL Server y LDAPS y cualquier prueba no ejecutada.

## 12. Estado de definición y condiciones de cierre

Las decisiones funcionales y los contratos técnicos necesarios para RF-001 están
definidos. La configuración privada, los entornos y la ejecución de pruebas son
trabajo de preparación/verificación, no decisiones funcionales abiertas.
La política operativa de backups se documenta antes de habilitar recuperación;
RF-001 verifica su protección de sesiones mediante restauraciones aisladas.

RF-001 se completa con sus casos de uso y adaptadores reales implementados,
pruebas propias unitarias, HTTP, SQL Server y LDAPS ejecutadas, checks sin nuevas
advertencias y documentación/OpenAPI actualizados. Los dobles no sustituyen las
pruebas reales de sus adaptadores. Las integraciones de mantenimiento de
asignaciones y publicación corporativa se registran en sus tramos y no impiden
cerrar RF-001. No marcar como verificadas funcionalidades de RF-002/RF-009 que
no se hayan implementado y probado. La frontera exacta está en 2.1 y 10.3.

## 13. Trazabilidad de reglas funcionales

RF-001 agrupa las antiguas 68 entradas en 19 reglas funcionales. En la columna
«Numeración anterior» se indican los sufijos de RN de la versión previa; son
referencias históricas y no reglas adicionales vigentes. Las citas en los
contratos y casos de prueba se han actualizado a la numeración actual.

Los contratos técnicos siguen definidos en las secciones de este diseño y en
RF-001 / 5. La agrupación no los vuelve opcionales ni cambia sus valores.

| Regla actual | Comportamiento | Numeración anterior | Concreción en DESIGN-001 | Criterios de RF-001 |
| --- | --- | --- | --- | --- |
| RN-001 | Identidad corporativa | 001, 002, 008, 013, 014, 046, 047 | 3.1.1 y 4.1 | CA-003, CA-016 |
| RN-002 | Autorización por rol y taller | 006, 009, 010, 011, 012, 052 | 2 y 5.3 | CA-004, CA-005 |
| RN-003 | Primer acceso local | 053 | 5.1 | CA-005 |
| RN-004 | Emisión y contrato único | 003, 028, 029, 032, 033, 034, 037, 064 | 4.1 a 4.4 | CA-001, CA-012 |
| RN-005 | Entrada válida y contraseña intacta | 042, 043, 044, 045 | 4.1 | CA-002 |
| RN-006 | Confidencialidad | 007 | 9 | CA-016 |
| RN-007 | Sesión única | 016 | 5.1 y 7 | CA-006 |
| RN-008 | Vigencia y límite absoluto | 005, 019, 023, 024, 055, 056 | 6 | CA-007 |
| RN-009 | Renovación con comprobación actual | 004, 021, 048 | 3.1.1, 5.2 y 7 | CA-008 |
| RN-010 | Reutilización de refresh | 018, 041 | 5.2 y 7 | CA-009 |
| RN-011 | Cierre y sesión finalizada | 017, 036, 038 | 5.3 | CA-010 |
| RN-012 | Cambios de autorización | 020, 068 | 2, 5.2 y 5.3 | CA-020 |
| RN-013 | Fallos y efectos confirmados | 022, 027, 057, 058, 059 | 4.3.1 y 9 | CA-014 |
| RN-014 | Rechazos seguros y comprensibles | 015, 026, 035 | 4.3 y 4.3.1 | CA-004, CA-014 |
| RN-015 | Acceso sujeto a sesión vigente | 030, 040, 054 | 5.3, 6 y 7 | CA-011 |
| RN-016 | Limitación global | 031, 039, 060, 061, 062, 063 | 7.1 | CA-015 |
| RN-017 | Antiforgery y origen | 049, 050, 051 | 4.4.1 | CA-013 |
| RN-018 | Configuración obligatoria | 025 | 8 | CA-017 |
| RN-019 | Retención y recuperación | 065, 066, 067 | 7.2 | CA-018, CA-019 |
