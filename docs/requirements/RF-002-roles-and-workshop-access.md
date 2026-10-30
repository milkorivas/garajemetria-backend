# RF-002 — Roles y acceso a talleres

| ID | Requerimiento | Prioridad | Estado |
| --- | --- | --- | --- |
| RF-002 | Roles y acceso a talleres | Por definir | Implementado; verificación AD aislado pendiente |

## 1. Objetivo

Determinar el rol de los usuarios corporativos a partir de su pertenencia a
grupos de Active Directory y aplicar los permisos de acceso a talleres de
GarajeMetría. El rol no se asigna manualmente en la aplicación.

Referencias: [Visión y alcance](../00-vision-y-alcance.md),
[RF-001 — Autenticación](RF-001-authentication.md) y
[DESIGN-001 — Autenticación](../design/DESIGN-001-authentication.md).

Los componentes de roles y primer acceso compartidos se implementan en RF-001;
el mantenimiento administrativo de asignaciones está implementado en este tramo.
Su diseño técnico para SDD se desarrolla en
[DESIGN-002 — Roles y acceso a talleres](../design/DESIGN-002-roles-and-workshop-access.md).
Sustituye la decisión anterior de asignar los roles localmente en GarajeMetría.
El taller predeterminado conserva su asignación local.

## 2. Actores y responsabilidades

| Actor | Responsabilidad |
| --- | --- |
| Administración de AD | Mantiene las cuentas y la pertenencia a los grupos corporativos. |
| Identidad de servicio de la API | Consulta la pertenencia en el único dominio mediante LDAPS con permisos de lectura; no modifica grupos. |
| Administrador de GarajeMetría | Accede a cualquier taller y mantiene las asignaciones locales de taller. Este rol no concede permisos de administración de AD. |
| Asesor | Consulta exclusivamente el taller que tiene asignado. |
| API | Resuelve el rol desde AD y aplica autorización de operación y recurso mediante identidad confiable. |

## 3. Grupos y permisos

### 3.1. Correspondencia de grupos

| Grupo de Active Directory | Rol de GarajeMetría |
| --- | --- |
| `web_GarajeMetria_Admin` | Administrador |
| `web_GarajeMetria_Advisor` | Asesor |

Se admite pertenencia directa y transitiva mediante grupos anidados. Si un
usuario pertenece a ambos grupos, Administrador tiene prioridad. Poder consultar
un grupo o conocer su nombre no concede
el rol. Los nombres o roles enviados por el cliente nunca acreditan pertenencia.

### 3.2. Permisos ya acordados

| Operación | Administrador | Asesor |
| --- | --- | --- |
| Iniciar sesión sin taller asignado | Permitido | Rechazado |
| Consultar datos de talleres | Cualquier taller | Solo el taller asignado |
| Seleccionar taller de consulta | Cualquier taller | Sin cambio a otro taller |
| Consultar imágenes almacenadas | Permitido | Denegado |
| Realizar correcciones manuales | Permitido, con auditoría del requerimiento correspondiente | Denegado |
| Asignar/cambiar taller predeterminado | Permitido, con motivo y auditoría según 5.4 | Denegado |
| Asignar/cambiar rol en GarajeMetría | No existe esta operación; el rol procede de AD | No existe esta operación |

El rol Administrador no implica permisos adicionales a los expresamente
definidos ni derechos para modificar el directorio. Los detalles de imágenes,
correcciones y consultas pertenecen a sus respectivos requerimientos.

## 4. Reglas

Los identificadores RN, CA y CP se citan con su contexto, por ejemplo,
«RF-002 / RN-001», y no sustituyen los identificadores propios de RF-001.

| ID | Regla |
| --- | --- |
| RN-001 | El rol Administrador procede de la pertenencia a `web_GarajeMetria_Admin`; el rol Asesor, de la pertenencia a `web_GarajeMetria_Advisor`. |
| RN-002 | No existe asignación manual ni override local del rol. La API no administra la pertenencia a grupos de AD. |
| RN-003 | Una identidad con credenciales válidas que no pertenece a ninguno de los grupos autorizantes no obtiene sesión ni tokens. Se conserva el 403 y mensaje de falta de rol definido en RF-001. |
| RN-004 | El taller predeterminado se mantiene en GarajeMetría y se vincula al usuario local identificado de forma estable mediante objectGUID de AD. Los grupos anteriores no determinan por sí solos qué taller corresponde al Asesor. |
| RN-005 | El Administrador puede acceder sin taller asignado y seleccionar cualquier taller de consulta. Esta selección no modifica su taller predeterminado ni cierra la sesión. |
| RN-006 | El Asesor requiere un taller asignado para iniciar sesión. Todas sus lecturas y operaciones autorizadas se restringen a ese taller, aunque envíe otro identificador desde el cliente. |
| RN-007 | Solo el Administrador puede mantener asignaciones locales de taller. Un cambio efectivo de taller asignado cierra inmediatamente la sesión del usuario e invalida todos sus tokens, coordinando escritura y revocación de forma atómica. |
| RN-008 | La consulta de grupos se realiza en Infrastructure, mediante LDAPS y la identidad de servicio de lectura. Application recibe un resultado materializado; Domain y los contratos REST no dependen de tipos LDAP. |
| RN-009 | La pertenencia se asocia a la identidad AD autenticada, no a un nombre arbitrario enviado por el consumidor. El vínculo estable por objectGUID se conserva ante cambios de nombre. |
| RN-010 | Un error de lectura de grupos o permisos insuficientes no equivale a no pertenecer ni autoriza por defecto. No emitir tokens sin haber determinado el acceso; el tratamiento temporal sigue RF-001. |
| RN-011 | El primer Administrador obtiene su rol mediante pertenencia al grupo Admin gestionada fuera de la API. No crear una contraseña local ni una excepción de autorización para provisionarlo. |
| RN-012 | Un rol persistido o incluido en un JWT es una representación del rol resuelto, nunca una asignación local independiente que pueda contradecir AD. |
| RN-013 | La pertenencia puede ser directa o transitiva mediante grupos anidados. La resolución es Administrador si pertenece a Admin, Asesor si solo pertenece a Advisor y sin acceso si no pertenece a ninguno. |
| RN-014 | Consultar pertenencia en login y en cada renovación válida. Si el rol efectivo cambia respecto al de la sesión o desaparece la pertenencia autorizante, cerrar la sesión, devolver el 401 de sesión finalizada y exigir nuevo login, sin emitir tokens. Si el rol efectivo no cambia, continuar el refresh con las demás comprobaciones de RF-001. |
| RN-015 | Crear automáticamente la identidad local tras validar credenciales, identidad estable y pertenencia autorizante en AD, antes de comprobar el taller. La creación es única por objectGUID y no asigna rol manual ni taller por defecto. |
| RN-016 | Un Asesor recién registrado puede quedar pendiente de primera asignación, sin taller ni sesión y con el 403 de RF-001. Una vez asignado, solo se permite sustituir su taller por otro existente, nunca retirarlo sin sustitución. Esta condición no concede acceso a un Asesor sin taller por cambio posterior de rol AD. |
| RN-017 | Cada cambio efectivo de taller exige motivo y auditoría persistida con autor, usuario afectado, taller anterior/nuevo, instante UTC e identificador de correlación. Asignación, auditoría y cierre de sesión se confirman atómicamente. Si cualquiera falla, no hay cambio parcial. |
| RN-018 | Validar existencia de usuario y taller, campos permitidos, motivo y versión esperada antes de escribir. Un cambio sin efecto no incrementa la versión, no cierra sesión ni crea una entrada de cambio en auditoría. |
| RN-019 | Los fallos SQL y la imposibilidad de comprobar sesión siguen RF-001, sección 5.5.4: 503 para indisponibilidad temporal, 500 para error inesperado y 503 operation_outcome_unknown si no se conoce el resultado del commit. No repetir escrituras ni afirmar rollback sin confirmarlo. |
| RN-020 | Auditoría de asignaciones y eventos de aprovisionamiento siguen los plazos desde occurredAt y la limpieza coordinada de DESIGN-001 / 7.2. Solo se eliminan registros elegibles; no existen endpoints para modificar/borrar historial y esta política no elimina perfiles locales. |
| RN-021 | El taller del Asesor y el destino de una asignación deben existir y estar activos en la copia local de RF-009. La desactivación conserva asignación/historial y cierra sesiones de Asesor; la reactivación permite nuevo login con la misma asignación, sin recuperar tokens. La caída de la fuente permite usar la última copia confirmada; sin carga inicial completa, devolver 503 cuando se necesite validar taller. |

No se consulta la pertenencia en cada petición de negocio. Hasta la siguiente
renovación, los accesos existentes conservan las reglas y expiraciones de
RF-001 mientras la sesión local siga vigente. Un cambio en AD no implica una
notificación inmediata al backend. Al detectarse el cambio efectivo, el cierre
local invalida todos los tokens de la sesión, incluidos accesos anteriores.

## 5. Flujos y contratos

### 5.1. Inicio de sesión

Tras validar credenciales según RF-001, resolver el rol desde los grupos de la
misma identidad. Sin pertenencia autorizante, devolver el rechazo de falta de
rol sin tokens. Con rol Administrador, permitir acceso con o sin taller local;
con rol Asesor, exigir el taller asignado y aplicar su restricción.

Después de validar credenciales y pertenencia autorizante, crear la identidad
local si aún no existe, vinculada al objectGUID y con taller null. Usar unicidad
persistida y resolver intentos concurrentes sin duplicar identidades ni
sobrescribir un taller ya asignado. No crear perfiles por intentos fallidos,
cuentas sin grupos autorizantes o consultas de directorio incompletas.

El primer Administrador se registra de esta forma y puede entrar sin taller.
El primer intento de un Asesor registra su identidad, pero devuelve 403 sin
tokens hasta que un Administrador complete su asignación. La identidad se
conserva pese a ese rechazo de acceso; no tratarla como una sesión creada ni
deshacerla al devolver el 403. El Administrador la encuentra en la consulta de
usuarios locales y asigna el taller. No se importan cuentas en masa desde AD.

Los campos de login siguen siendo `username` y `password`. No añadir campos
`role`, `groups` o `workshopId` para permitir que el cliente elija autorización.
La respuesta mantiene `user.role` y `user.defaultWorkshopId` de RF-001;
el primero se deriva de AD y el segundo de la asignación local.

Si la pertenencia no se puede determinar por un fallo técnico, el login devuelve
el 503 temporal de RF-001, sin tokens ni sustitución de la sesión existente.

### 5.2. Cambios de acceso y asignaciones

El taller se cambia mediante un caso de uso autorizado de Application y una
transacción que coordina asignación, auditoría y cierre de sesión. Un cambio
sin efecto no se trata como una modificación efectiva. El contrato está en 5.4.
La primera asignación del Asesor admite null como valor anterior; después solo
admite sustitución por otro taller. Para un Administrador se permite mantener,
asignar, sustituir o retirar el taller. Retirar requiere comprobar su rol
efectivo actual en AD; si es Asesor o carece de rol, devolver 409, y si no puede
determinarse por un fallo técnico, devolver 503 sin cambios. AD puede cambiar
después de esa lectura; los permisos de login y refresh siguen comprobándose
según RF-001, sin presumir una transacción distribuida con el directorio.

Los cambios de grupo se realizan en AD. La renovación consulta pertenencia
directa y anidada y compara el rol efectivo con el de la sesión, antes de
consumir refresh. Si cambia o desaparece, confirma el cierre y devuelve 401.
Quitar o añadir pertenencia sin cambiar el rol efectivo no cierra la sesión.
Un fallo técnico de consulta devuelve 503 sin consumo ni cierre, conforme a
RF-001. No afirmar que
la API participa en una transacción con AD ni que detecta el cambio sin consultar.

### 5.3. Valores de rol en contratos

| Rol visible | `user.role` y claim JWT `role` |
| --- | --- |
| Administrador | `administrator` |
| Asesor | `advisor` |

Son valores string exactos, en inglés y minúsculas. No enviar etiquetas
traducidas, nombres de grupos AD ni ordinales de un enum como valores del rol.
Cada sesión tiene un único rol efectivo; la pertenencia simultánea se resuelve
antes de emitir tokens aplicando la prioridad Administrador.

El access token contiene exactamente un claim `role` con el mismo valor que
`user.role` en login. Las renovaciones conservan ese rol solo si sigue vigente
según AD; de lo contrario se cierra la sesión como ya define RN-014. El refresh
token no contiene el claim `role` ni permite autorizar operaciones de negocio.
La comprobación del rol no sustituye la comprobación de sesión ni del taller.

### 5.4. Mantenimiento del taller predeterminado

Estas operaciones requieren Bearer válido, sesión vigente y rol `administrator`.
Un Asesor no puede consultar el directorio de usuarios locales ni mantener
asignaciones. La API verifica autorización antes de revelar la existencia de
usuarios o talleres; los identificadores suministrados no conceden acceso.

| Método y ruta | Entrada | Éxito |
| --- | --- | --- |
| `GET /api/users` | `pageNumber` y `pageSize` | 200 JSON con `items`, `pageNumber`, `pageSize`, `totalCount` |
| `GET /api/users/{userId}/default-workshop` | Identificador local | 200 JSON con `userId` y `workshopId` nullable; cabecera ETag |
| `PUT /api/users/{userId}/default-workshop` | JSON `workshopId` y `reason`, cabecera If-Match | 204 sin cuerpo, con ETag de la versión resultante |

La lista incluye únicamente identidades ya registradas localmente, con
`id`, `displayName`, `username` y `defaultWorkshopId`; no expone contraseñas,
tokens, GUID de AD ni un rol local editable. Paginación inicial: página 1,
20 elementos por página, máximo 100; valores positivos y orden fijo por
identificador local ascendente, sin filtros u ordenamientos arbitrarios.
Estos límites se configuran explícitamente en archivo. No se consulta ni
importa todo AD para responder a la lista. Respuestas administrativas no-store.

Ejemplo sintético de primera asignación o sustitución:

```json
{
  "workshopId": "workshop-example-002",
  "reason": "Asignación al equipo de recepción del taller"
}
```

`workshopId` es obligatorio: string con identificador existente o null únicamente
cuando el usuario objetivo tiene rol efectivo Administrador. No aceptar una
cadena vacía como forma de retirar el taller. El identificador local de usuario
y el de taller admiten hasta 128 valores escalares Unicode y no admiten espacios
de borde. Su existencia se comprueba tras validar el formato. `reason` es string
obligatorio de 1 a 500 valores escalares Unicode, con al menos un carácter no
blanco, sin caracteres de control. Se conserva el motivo validado y no se
utiliza como instrucción ni como contenido ejecutable.

El PUT admite únicamente esos dos campos JSON, sin duplicados ni campos de rol,
usuario autor, fechas o datos de auditoría manipulables por el cliente. Aplicar
JSON UTF-8 estricto y límite inicial configurable de 16 KiB antes de deserializar;
tipo o codificación no admitidos devuelven 415 y exceso de cuerpo, 413. Los
campos y el motivo se validan mediante FluentValidation antes del caso de uso.
Un PUT sin efecto también exige una solicitud válida, incluido el motivo.

La lectura de la asignación entrega un ETag fuerte y opaco, independiente del
proveedor de base de datos. El PUT requiere exactamente ese ETag en If-Match;
no admite `*` como sustituto de la versión leída. Falta de If-Match: 428;
formato no admitido: 400; versión desactualizada: 412, sin cambios ni auditoría
de cambio. La comparación y actualización de versión son atómicas. Un cambio
efectivo produce un ETag distinto; un cambio sin efecto conserva el anterior.
Si otro cambio ya se confirmó, releer la asignación antes de volver a decidir;
no reintentar escrituras a ciegas.

| HTTP | Situación |
| --- | --- |
| 400 | Entrada, paginación, motivo o formato de If-Match inválidos |
| 401 | Bearer inválido/expirado o sesión finalizada |
| 403 | Usuario autenticado sin permiso de Administrador |
| 404 | Usuario local o taller solicitado inexistentes, tras autorizar |
| 409 | Retirada de taller incompatible con el rol del usuario objetivo o taller destino inactivo |
| 412 | La versión de la asignación ya ha cambiado |
| 413 / 415 | Tamaño o formato del cuerpo no admitidos |
| 428 | Falta la precondición If-Match requerida |
| 503 | No se puede comprobar el rol AD necesario para retirar el taller |
| 500 / 503 | Fallo inesperado, persistencia temporalmente no disponible o resultado de commit indeterminado, según RF-001 / 5.5.4 |

Los errores utilizan ProblemDetails con mensajes seguros y sin valores
intentados ni detalles internos. Un fallo de persistencia devuelve el error
centralizado de RF-001 / 5.5.4; se revierte cuando no hay commit y se puede
confirmar el rollback. Ante resultado indeterminado no se asegura que no hubo
cambios; se relee la asignación y su ETag antes de decidir otra escritura. Estas rutas
se autentican exclusivamente con Bearer: ni refresh ni cookie antiforgery
autorizan su ejecución. Conservan los controles CORS de la API.

Referencias de concurrencia HTTP: [If-Match, RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#name-if-match)
y [precondición requerida, RFC 6585](https://www.rfc-editor.org/rfc/rfc6585.html#section-3).

El catálogo y su consulta se definen en
[RF-009](RF-009-workshop-catalog.md). Para asignaciones, un taller inexistente
devuelve 404 y uno inactivo, 409, sin cambios. Validar actividad también dentro
de la escritura, incluso en una solicitud sin efecto, coordinada con la
publicación del catálogo. El Asesor requiere taller activo en login y refresh;
la desactivación publicada cierra sus sesiones. No borrar ni retirar su
asignación y no exigir confirmación administrativa si el taller se reactiva.
La última copia puede utilizarse aunque supere 15 minutos por caída de la fuente;
esta excepción no autoriza ignorar fallos de comprobación en la base local.

### 5.5. Auditoría y atomicidad

Cada primera asignación, sustitución o retirada permitida genera una entrada
inmutable de auditoría con identificador propio, identificador local del autor
obtenido de su identidad autenticada, identificador del usuario afectado,
taller anterior y nuevo, motivo obligatorio, `occurredAt` UTC y correlación de
la solicitud. No almacenar credenciales, tokens ni payloads completos.
No ofrecer endpoints para editar o eliminar este historial.

La transacción verifica versión, cambia asignación, registra auditoría e invalida
la sesión activa del usuario objetivo. No mantener transacciones SQL abiertas
durante consultas AD. Si falla la auditoría o el cierre, revertir también la
asignación. Una operación sin efecto no provoca cierre ni historial de cambio.
Si el Administrador cambia su propia asignación, la operación autorizada puede
terminar con 204 y su sesión queda cerrada para las siguientes solicitudes.

La creación automática de una identidad sin taller no es una asignación
administrativa y no requiere inventar un motivo de usuario. Se registra como
evento separado de aprovisionamiento, con identidad estable interna, fecha UTC
y correlación, sin credenciales y sin atribuirlo a un Administrador ficticio.
Los plazos de auditoría de cambios y aprovisionamiento se definen únicamente
en [DESIGN-001 / 7.2](../design/DESIGN-001-authentication.md#72-retención-y-limpieza-diaria).
El historial sigue siendo inmutable durante su conservación. No borrar perfiles
locales ni talleres como parte de esa limpieza ni añadir endpoints de borrado.

## 6. Criterios de aceptación y pruebas previstas

| Criterio | Resultado esperado | Caso de prueba |
| --- | --- | --- |
| CA-001 | Un usuario perteneciente únicamente al grupo Admin obtiene rol Administrador tras autenticar. | CP-001: grupo Admin con y sin taller. |
| CA-002 | Un usuario perteneciente únicamente al grupo Advisor obtiene rol Asesor si tiene taller asignado. | CP-002: grupo Advisor con taller. |
| CA-003 | Sin pertenencia a ninguno de los dos grupos, el login devuelve 403 sin tokens, aunque las credenciales sean válidas. | CP-003: sin grupos autorizantes. |
| CA-004 | Un Asesor sin taller recibe el rechazo acordado en RF-001 y no obtiene tokens. | CP-004: Advisor sin taller. |
| CA-005 | El Asesor no puede consultar otro taller ni imágenes ni realizar correcciones manuales. | CP-005: autorización por operación y recurso, incluidos identificadores manipulados. |
| CA-006 | Seleccionar otro taller de consulta como Administrador no modifica su asignación ni cierra su sesión. | CP-006: selección de contexto. |
| CA-007 | Cambiar efectivamente el taller asignado cierra la sesión y deniega sus tokens de acceso y refresh inmediatamente. | CP-007: cambio de taller concurrente con login/refresh. |
| CA-008 | Campos del cliente o datos locales antiguos no pueden conceder un rol distinto del resuelto desde AD. | CP-008: overposting e intento de override local. |
| CA-009 | Un error de lectura de grupos no se interpreta como autorización válida; login devuelve 503 sin alterar una sesión existente. | CP-009: permisos, timeout y directorio indisponible. |
| CA-010 | El primer Administrador puede obtener el rol desde el grupo sin un taller previo y sin un mecanismo local de concesión de roles. | CP-010: primer acceso con identidad local y pertenencia válidas. |
| CA-011 | La pertenencia anidada concede el mismo rol que la directa; Admin prevalece si ambos grupos aplican. | CP-011: pertenencia directa, transitiva y simultánea; ciclos de grupos sin bucles ni permisos añadidos. |
| CA-012 | Cada refresh comprueba los grupos antes de consumir el token; un cambio de rol o pérdida de acceso cierra la sesión y devuelve 401 sin nuevos tokens. | CP-012: Admin a Advisor, Advisor a Admin, pérdida de ambos y cambios sin efecto en el rol; indisponibilidad devuelve 503 sin consumo. |
| CA-013 | Login devuelve `administrator` o `advisor` y el access token incluye un único claim `role` coincidente; refresh no contiene ese claim. | CP-013: valores exactos, prioridad Admin y ausencia de roles múltiples o traducidos. |
| CA-014 | El primer intento autorizado por AD y grupos crea una identidad local única; el Administrador entra sin taller y el Asesor queda pendiente con 403 sin tokens. | CP-014: primer acceso, intentos concurrentes, reintento y conservación de perfil pese al 403. |
| CA-015 | El Asesor pendiente recibe primera asignación y después solo puede tener sustituciones, nunca una retirada a null. | CP-015: primera asignación, sustitución y retirada rechazada; sin cambios ni revocación al rechazar. |
| CA-016 | Cada cambio efectivo exige motivo y confirma asignación, auditoría y cierre de sesión conjuntamente. | CP-016: motivo vacío/excesivo, datos de auditoría, rollback ante fallo y cambio propio del Administrador. |
| CA-017 | Versión obsoleta produce 412, falta If-Match produce 428 y solicitudes sin efecto no cierran sesión ni duplican auditoría. | CP-017: escrituras concurrentes, repetición tras respuesta perdida y PUT sin cambio con versión vigente. |
| CA-018 | Solo Administradores acceden a las rutas de consulta y mantenimiento; la lista está paginada y ordenada, y los datos inexistentes o inválidos reciben los errores definidos. | CP-018: permisos, límites de página, inexistencia y cuerpo estricto. |
| CA-019 | Un Administrador puede quedar sin taller; su retirada se verifica con el rol AD efectivo y un fallo de lectura no modifica datos. | CP-019: retirada válida, rol Advisor/ausente, fallo AD y cambio posterior de rol detectado en login/refresh. |
| CA-020 | Los fallos de SQL devuelven los ProblemDetails de RF-001 / 5.5.4 sin conceder acceso o éxito parcial. Un commit indeterminado no implica rollback ni repetición de la escritura. | CP-020: consulta de sesión, cambio de taller con rollback conocido/resultado incierto y relectura de asignación/ETag. |
| CA-021 | Auditoría y aprovisionamiento se conservan durante los plazos de DESIGN-001 / 7.2 y solo se eliminan al ser elegibles mediante su limpieza coordinada, sin editar historial ni borrar perfiles. | CP-021: frontera temporal calculada con las opciones vigentes, lotes concurrentes y ausencia de cascadas sobre identidad/asignación. |
| CA-022 | Solo se asignan talleres activos; la desactivación cierra sesiones de Asesor conservando asignación y la reactivación permite nuevo login sin recuperar tokens previos. | CP-022: destino inexistente/inactivo, publicación concurrente, última copia durante caída y ausencia de carga inicial; coordinación con RF-009. |

La cobertura automatizada administrativa se implementa en `UserAdministrationTests`,
`WorkshopAssignmentTests` y `WorkshopAssignmentMigrationTests`, reutilizando las
pruebas de autenticación y catálogo. Los CA-005/006 se integrarán con los endpoints
de negocio de sus respectivos requerimientos. La verificación de grupos y
permisos con AD aislado sigue pendiente. Application usa dobles sin red ni base de datos;
las pruebas LDAPS y SQL Server utilizan recursos aislados y datos sintéticos.
No usar EF InMemory ni SQLite como sustitutos de persistencia SQL Server.

## 7. Estado de definición y verificación

Las decisiones funcionales de este requerimiento están definidas para el alcance
descrito: roles, primer acceso, mantenimiento del taller, validación, concurrencia
y auditoría. El alcance administrativo está implementado; el runbook
[de administración](../runbooks/user-administration.md) describe sus contratos,
migración y límites de verificación. No se acredita integración con AD real.

Los nombres de servidores, grupos resueltos en el entorno, permisos y demás
parámetros operativos se verifican con el administrador de AD y se configuran
privadamente. No se presupone que los grupos ya existan o que sus permisos de
lectura estén probados por el hecho de documentar sus nombres.
