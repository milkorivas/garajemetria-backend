# RF-009 — Catálogo corporativo de talleres

| ID | Requerimiento | Prioridad | Estado |
| --- | --- | --- | --- |
| RF-009 | Catálogo corporativo de talleres | Por definir | Implementación con fuente SQL auxiliar; origen corporativo definitivo pendiente |

## 1. Objetivo y alcance

Disponer de una copia local del catálogo corporativo para consultar talleres y
validar las asignaciones de [RF-002](RF-002-roles-and-workshop-access.md).
Los talleres se mantienen exclusivamente en el sistema externo. GarajeMetría
no ofrece altas, modificaciones ni bajas manuales del catálogo.

La fuente prevista es una base de datos o vista con acceso de solo lectura y devuelve
únicamente talleres activos. Se implementa una fuente SQL Server auxiliar
`GarajeMetriaAux`, con treinta talleres sintéticos y vista de activos,
según lo autorizado por el usuario; sustituye el mock en memoria.
El contrato auxiliar se documenta en DESIGN-009. El origen corporativo definitivo
requiere comprobar los datos de la sección 7.
Véanse [diseño](../design/DESIGN-009-workshop-catalog.md) y
[tareas](../tasks/TASKS-009-workshop-catalog.md).

## 2. Modelo mínimo

| Campo | Significado |
| --- | --- |
| `id` | Identificador interno estable y opaco de GarajeMetría; se transporta como string y es la referencia de asignaciones e historial. |
| `code` | Código corporativo único, permanente y nunca reutilizado; clave de correspondencia con el origen. |
| `name` | Nombre proporcionado por el sistema corporativo. |
| `isActive` | Estado local derivado de la presencia en la última lectura completa publicada. |

El código externo actual es `nvarchar(12)`. Esa es una característica de la
fuente actual, no un máximo permanente del contrato de negocio. Conservarlo
como texto, incluidos ceros iniciales; no convertirlo a número, truncarlo ni
inventar normalizaciones. Longitudes, equivalencia de códigos y mappings se
cerrarán con el esquema de integración. Los tipos del proveedor quedan en
Infrastructure; no se trasladan a Domain, Application ni REST.

No existe otro identificador externo estable. La reaparición del mismo código
recupera el mismo `id` interno. Cambiar un nombre conserva identidad. Una futura
migración que cambie códigos requiere correspondencias explícitas; no asociar
talleres automáticamente por semejanza de nombres.

## 3. Reglas

| ID | Regla |
| --- | --- |
| RN-001 | El sistema corporativo es la única autoridad del catálogo; la integración es de solo lectura. |
| RN-002 | Mantener `id`, `code`, `name` e `isActive` localmente, con unicidad persistida del código conforme al contrato de origen. Conservar el identificador interno en cada sincronización y reactivación. |
| RN-003 | En operación normal, reflejar cambios externos en un máximo de 15 minutos, incluida la desactivación. Planificar lecturas con margen para su duración y publicación; ejecutar cada 15 minutos por sí solo no garantiza ese plazo. |
| RN-004 | Solo una lectura completa, validada y publicada correctamente puede sustituir el estado confirmado. Taller presente: activo; taller local ausente de esa lectura: inactivo. No borrar talleres ni asignaciones, referencias o historial por ausencia. |
| RN-005 | Timeout, lectura parcial, filas inválidas, códigos duplicados o fallo de persistencia conservan la copia anterior y no avanzan la fecha de última lectura completa correcta. Una respuesta vacía solo es una lectura completa si se ha comprobado conforme al contrato del origen. |
| RN-006 | Si falla la fuente, incluso al superar 15 minutos, permitir acceso con la última copia confirmada y registrar la incidencia y su antigüedad. Un taller activo en esa copia sigue siendo utilizable; uno inactivo sigue denegado. Esta excepción permite retrasar la detección de desactivaciones durante una caída. |
| RN-007 | Sin copia completa publicada, devolver 503 cuando sea necesario comprobar un taller asignado al Asesor o validar un destino de asignación no nulo. El Asesor sin taller conserva el perfil pendiente y recibe 403, sin consultar el catálogo. El Administrador puede iniciar sesión sin taller y retirar una asignación a null según RF-002 sin consultar el catálogo. Una copia completa vacía es distinta de no haber sincronizado nunca. |
| RN-008 | Asignar o sustituir un taller exige que exista y esté activo en la copia confirmada: inexistente, 404; inactivo, 409. La antigüedad por fallo de la fuente no impide usar una copia previa conforme a RN-006. |
| RN-009 | Al publicar una desactivación, impedir nuevas sesiones y renovaciones de Asesor para ese taller y cerrar sus sesiones activas, invalidando todos sus tokens. Conservar la asignación para historial y eventual reactivación. No retirar el taller ni cerrar sesiones de Administrador solo por ese estado. |
| RN-010 | Un Asesor con taller inactivo recibe 403 en login, después de credenciales y grupos válidos, sin tokens. Una sesión cerrada por desactivación recibe 401 en acceso o renovación según RF-001. El Administrador puede sustituir la asignación por un taller activo, con motivo y auditoría de RF-002. |
| RN-011 | La reactivación permite un nuevo login con la misma asignación, sujeto a todas las comprobaciones de RF-001/RF-002. No exige confirmación administrativa ni recupera sesiones cerradas o tokens anteriores. |
| RN-012 | Todas las instancias comparten la copia y el estado de sincronización. Coordinar publicaciones para que una ejecución atrasada no sobrescriba otra más reciente. Login, refresh y asignación deben revalidar el estado local al confirmar para no omitir una desactivación concurrente. |
| RN-013 | Registrar ejecuciones, resultado, fecha UTC de observación completa, publicación, antigüedad y recuentos de altas/cambios/desactivaciones/reactivaciones, con correlación y errores seguros. No registrar credenciales, cadenas de conexión ni cargas completas. |

## 4. Lectura y publicación

Infrastructure lee la fuente detrás de un contrato de Application que devuelve
datos materializados, sin SQL, conexiones ni IQueryable en las capas internas.
Usar credenciales de solo lectura, configuración privada, timeouts acotados y
cancelación. No mantener una transacción local abierta durante la lectura externa.

Preparar y validar la lectura completa antes de publicar. No publicar páginas
parciales ni tratar una lectura interrumpida como una lista de bajas. El contrato
del origen debe permitir reconocer la completitud y la coherencia de la lectura,
también si hay paginación o cambios concurrentes en la vista.

Publicar la nueva copia y sus efectos de autorización de forma atómica en la
base local: estados de talleres y cierre de sesiones de Asesor afectadas deben
ser coherentes. Coordinar la transacción con creación de sesión, refresh y
asignación. Una operación anterior a la publicación queda sujeta al cierre; una
posterior utiliza el estado publicado. No confiar exclusivamente en cachés por
proceso ni en el taller incluido en un JWT. No consultar la fuente corporativa
en cada petición protegida.

El límite de 15 minutos corresponde a operación normal; ante fallo se aplica
expresamente la continuidad aprobada con la última copia. Exponer el estado
de la integración en observabilidad sin declarar actualizada una copia antigua.
Los errores de la base local siguen RF-001 / 5.5.4: esta continuidad no permite
conceder acceso si SQL no puede comprobar la sesión o el estado local necesario.

## 5. Consulta del catálogo

El Administrador consulta la copia local para seleccionar un taller activo al
mantener asignaciones; puede identificar talleres inactivos para consultar
referencias históricas. El Asesor sigue restringido al taller autorizado y no
obtiene acceso al catálogo administrativo completo.

La consulta administrativa debe estar autenticada, paginada, con orden estable
y DTO explícito con `id`, `code`, `name` e `isActive`. El contrato HTTP exacto,
sus filtros y límites están acordados en DESIGN-009: `GET /api/workshops`,
`pageNumber=1`, `pageSize=20` (máximo 100), `isActive` opcional y orden por id.
No añadir endpoints de escritura del catálogo ni de sincronización
anónima como parte de esta definición.

## 6. Criterios de aceptación y pruebas previstas

| Criterio | Resultado esperado | Caso de prueba |
| --- | --- | --- |
| CA-001 | El mismo código conserva su id interno al actualizar nombre o reactivarse; no se trunca ni convierte a número. | CP-001: importación repetida, ceros iniciales y reaparición. |
| CA-002 | Una lectura completa activa los presentes y desactiva los ausentes, conservando referencias e historial. | CP-002: altas, modificaciones, ausencias y conjunto vacío confirmado. |
| CA-003 | Lecturas parciales, duplicadas, inválidas o fallidas conservan copia y fecha de éxito anteriores. | CP-003: fallos de lectura/publicación y validación de completitud. |
| CA-004 | En operación normal los cambios se publican en un máximo de 15 minutos; durante caída se permite la última copia y se registra antigüedad/incidencia. | CP-004: reloj controlado, latencia de lectura y caída superior a 15 minutos. |
| CA-005 | Sin copia inicial completa, comprobar un taller asignado o un destino no nulo devuelve 503; Asesor sin asignación recibe 403 y conserva perfil; Administrador sin taller puede entrar. Retirar a null conforme a RF-002 no requiere catálogo. | CP-005: arranque sin carga, copia completa vacía, primer Asesor sin taller, Asesor con asignación y retirada administrativa permitida a null. |
| CA-006 | Taller inexistente o inactivo no puede asignarse: 404 o 409; no hay escritura, auditoría de cambio ni revocación por el rechazo. | CP-006: primera asignación, sustitución y copia antigua permitida. |
| CA-007 | Desactivar cierra las sesiones de Asesor y deniega tokens; conserva asignación y sesiones de Administrador. | CP-007: login, refresh y asignación concurrentes con publicación. |
| CA-008 | Reactivar permite nuevo login con la misma asignación, sin intervención administrativa; los tokens antiguos siguen denegados. | CP-008: desactivación y reactivación con sesión previa. |
| CA-009 | Instancias concurrentes no publican estados parciales ni sobrescriben una observación nueva con una atrasada. | CP-009: dos ejecutores, interrupción y recuperación con SQL Server aislado. |
| CA-010 | La consulta administrativa requiere Administrador, paginación y DTO acotados; no hay mantenimiento local del catálogo. | CP-010: permisos y contrato HTTP cuando se cierre el diseño. |

Se han añadido pruebas automatizadas para el tramo SQL auxiliar/local. La integración
corporativa real y la verificación operativa siguen pendientes. Application usa dobles sin red ni base de
datos. Persistencia se verifica con SQL Server aislado y el adaptador externo
con una fuente de prueba del motor adoptado; no EF InMemory ni SQLite sustitutos.

## 7. Definición pendiente para implementar la integración

1. Motor y esquema de la base/vista, campos de código y nombre, nulabilidad,
   longitudes, equivalencia de códigos (mayúsculas, espacios y colación) y
   garantías de lectura completa/coherente, incluida una lista vacía válida.
2. Verificación operativa de la cadencia, latencia, coordinación y recuperación
   con el origen corporativo definitivo. El contrato HTTP, la fuente SQL auxiliar
   y el ejecutor se definen en DESIGN-009.

Las reglas de continuidad, desactivación y reactivación ya están acordadas.
No se necesitan secretos en estos documentos ni se presupone acceso probado a
la fuente por haber definido su mecanismo de lectura.
