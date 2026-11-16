# RF-009 — Diseño del catálogo de talleres

## Alcance implementado

Fuente externa SQL Server con treinta talleres sintéticos en `GarajeMetriaAux`
y copia local persistente en `GarajeMetria`. No existe CRUD local ni endpoint HTTP para lanzar sincronizaciones.
La asignación administrativa sigue siendo RF-002 y no se incorpora en este tramo.

`IWorkshopSource` (Application) entrega una lectura materializada con señales
explícitas de completitud y de conjunto vacío confirmado. `SqlWorkshopSource`
(Infrastructure) consulta la vista `dbo.ActiveWorkshops` mediante un DbContext
externo de solo lectura, separado del contexto de autenticación. Domain,
Application y REST no dependen del motor ni de la conexión externa.

## Contrato de origen SQL auxiliar

`GarajeMetriaAux.dbo.Workshops`: `Code nvarchar(12)` obligatorio y clave primaria
con colación `Latin1_General_100_BIN2`, `Name nvarchar(256)` obligatorio e
`IsActive bit` obligatorio. Los códigos no admiten vacío ni espacios finales;
no se reutilizan. Nombres no blancos. La vista `dbo.ActiveWorkshops` proyecta
únicamente `Code` y `Name` de las filas con `IsActive = 1`. Un cambio de nombre
conserva código e identidad; marcar inactivo excluye la fila de la siguiente lectura.

La lectura usa una única consulta materializada sin tracking en una transacción
externa Serializable. Solo después de completar la consulta y confirmar esa
transacción devuelve `IsComplete` y `EmptyConfirmed`; cero filas es un conjunto
vacío confirmado. Fallos de conexión, consulta, cancelación o commit no devuelven
una lectura publicable. No existe una transacción distribuida con la base local.

`ConnectionStrings:WorkshopSource` se guarda en el archivo privado externo de
la API. `WorkshopCatalog:Source=SqlServer` habilita el adaptador cuando
`Enabled=true`; `WorkshopSource:CommandTimeoutSeconds=30` acota consultas.
La conexión exige timeout finito (15 segundos en la configuración local),
además del timeout general del ciclo de 60 segundos. No hay reintentos EF.
El DbContext externo rechaza SaveChanges y mapea una vista sin clave.
`ApplicationIntent=ReadOnly` expresa intención, no concede ni restringe permisos.
La identidad Windows local conserva sus permisos existentes; para despliegues
corporativos provisionar una identidad separada con solo SELECT sobre la vista.

El script manual `docs/configuration/sql/workshop-source-local.sql` crea la BD,
tabla, vista y códigos `000001` a `000030`, todos activos, si faltan. Se ejecuta
fuera del arranque y no sobrescribe datos existentes. Los primeros cuatro códigos
conservan las identidades generadas en la etapa mock. La tabla y la vista externas
no pertenecen a las migraciones de la base de autenticación.

## Identidad, lectura y publicación

Domain valida códigos y nombres no blancos y códigos únicos con comparación
ordinal. No se convierten códigos a números, no se recortan ni se normalizan.
El mapping local provisional admite 256 unidades UTF-16 para código y 2048 para
nombre. BIN2 conserva diferencias de mayúsculas; se rechazan códigos acabados
en espacio porque SQL Server los equipara al aplicar unicidad. Estas son
restricciones del adaptador local, no máximos definitivos del negocio; revisar
mapping y comparación antes de adoptar una vista corporativa diferente.

Cada lectura reserva una versión creciente en SQL, sin mantener una transacción
durante I/O externo. Una publicación anterior a la versión ya publicada se omite.
Si una lectura más reciente falla, una anterior completa aún puede publicarse
si supera la última versión confirmada. La versión publicada y sus fechas solo
avanzan con un commit correcto. La observación completa se fecha con el reloj
SQL al aceptar la lectura para publicación; no representa una fecha de captura
del sistema corporativo. El inicio de observación también usa el reloj SQL.

La publicación transaccional actualiza nombre y actividad, crea identificadores
opacos para códigos nuevos y conserva filas ausentes como inactivas. Cierra
sesiones abiertas de Asesor afectadas con `workshop_deactivated`; conserva
asignación y sesiones de Administrador. Los tokens dependen del estado persistido
de la sesión, por lo que no se recuperan tras reactivar el taller.

Se reutiliza el contexto local de RF-001, sin una segunda copia de talleres.
`sp_getapplock` transaccional `workshops:catalog` coordina publicación y las
transiciones de autenticación. El orden es catálogo y después usuario; login y
refresh revalidan el estado dentro de esa frontera. La consulta administrativa
también toma el bloqueo para mantener coherentes total y página. Es una decisión
conservadora que serializa estas transacciones cortas; medir contención antes
de sustituirla por coordinación más granular. Las futuras escrituras de RF-002
deberán respetar la misma frontera y orden.

## Ejecutor y continuidad

Servicio supervisado con primer intento al arrancar y espera de 300 segundos
tras cada intento. Timeout de 60 segundos para lectura y publicación. Validación
de configuración limita intervalo a 600 segundos y timeout a 120, con margen
respecto a 15 minutos en operación normal; la latencia y disponibilidad del
despliegue todavía requieren verificación operativa. Cada instancia puede leer;
la base coordina versiones y publicaciones. No hay caché local de autorización.

Un fallo conserva la copia y su fecha anterior; se registran resultado, fechas,
recuentos, correlación y, en fallo, antigüedad de la copia cuando SQL permite
consultarla. No hay reintentos automáticos de un commit incierto. El siguiente
ciclo relee y reconcilia por código. La copia antigua sigue siendo utilizable.
Sin publicación inicial la consulta devuelve 503; una copia vacía confirmada
devuelve 200 con lista vacía. Los controles de RF-001 sobre Asesor sin taller,
Administrador sin taller y errores de la base local permanecen aplicables.

## Contrato HTTP acordado

`GET /api/workshops` exige Bearer, sesión vigente y rol `administrator` antes de
validar o consultar datos. Respuesta `Cache-Control: no-store`.

| Parámetro | Regla |
| --- | --- |
| `pageNumber` | Entero positivo, por defecto 1. Rechazar desplazamiento superior a Int32. |
| `pageSize` | Entero positivo, por defecto 20 y máximo 100; límites explícitos configurables. |
| `isActive` | Booleano opcional; ausente devuelve activos e inactivos. |

Orden fijo por `id` ascendente. No se aceptan parámetros desconocidos o repetidos.
Respuesta 200: `items` (`id`, `code`, `name`, `isActive`), `pageNumber`, `pageSize`,
`totalCount`. Página fuera del total: lista vacía. Errores ProblemDetails: 400
entrada inválida; 401 sin sesión; 403 sin rol; 503 catálogo sin publicación o
fallo temporal local; 500 fallo inesperado. OpenAPI describe parámetros y Bearer.

## Migración local y futuro origen corporativo

La migración nueva añade código, nombre, índice único y estado de versiones;
no se aplica automáticamente al arrancar. Si la tabla previa contiene talleres,
la migración se detiene antes de modificarla: RF-001 carecía de código corporativo
y requiere un plan explícito de correspondencias que preserve IDs e historial.
No se fabrican códigos a partir del ID ni se borran referencias. El rollback
elimina las columnas nuevas; requiere preservación y revisión antes de usarlo
en una base compartida.

La fuente SQL auxiliar ya implementa ese contrato con datos de prueba. El mock
en memoria se ha eliminado. Antes de adoptar el origen corporativo definitivo,
comprobar su motor, esquema, campos, nulabilidad, longitudes, equivalencia de
códigos, coherencia y confirmación de vacío. Mantener la vista acordada o adaptar
únicamente Infrastructure, configurar credenciales de solo lectura externamente
y probar el origen aislado. Cambiar de motor puede requerir otro adaptador.
Las herramientas EF deben indicar `--context AuthenticationDbContext` al operar
las migraciones locales, porque existe también el contexto externo de lectura.

## Extensión RF-003 implementada en T003-12

[DESIGN-003](DESIGN-003-lpr-event-ingestion.md) requiere que una publicación que
desactive talleres renueve las versiones de autorización de sus dispositivos
en la misma transacción de catálogo. Esto impide recuperar tokens antiguos al
reactivar un taller, conservando registros, credenciales y asociaciones.
`SqlWorkshopCatalogStore` aplica la operación Domain
`InvalidateAuthorizationForWorkshop` a todos los dispositivos de los talleres
desactivados, también los inactivos o sin credenciales. Cambia ambas versiones
y UpdatedAt sin alterar su actividad propia, secreto, asociación ni historial.
Los dispositivos de talleres que siguen activos conservan sus versiones.

Emisión e ingesta toman primero `workshops:catalog` y revalidan estado local.
Las actualizaciones de talleres/dispositivos, cierres de sesiones de Asesor y
marcador de publicación se confirman o revierten conjuntamente. Reactivar el
taller conserva las versiones renovadas, requiere un token nuevo y no recupera
sesiones cerradas. El Administrador mantiene su acceso.

Las pruebas SQL de `LprIngestionTests` comprueban estados, credenciales y rollback;
`LprHttpTests` verifica tokens entre dos hosts después de desactivar/reactivar.
T003-17 añade barreras deterministas para ambos órdenes de catálogo/ingesta y
para cambios de catálogo durante la preparación del grant; verifica rollback,
rechazo del token anterior y ausencia de resurrección al reactivar. El cierre
de RF-003 y sus resultados están en
[TASKS-003 / 11](../tasks/TASKS-003-lpr-event-ingestion.md#11-cierre-de-t003-20);
el [runbook LPR](../runbooks/lpr-event-ingestion.md) documenta recuperación
y credenciales. Esta evidencia no acredita la fuente corporativa definitiva
ni cierra las verificaciones externas propias de RF-009.
Se mantiene la última copia confirmada ante caída del origen; sin publicación
inicial, las decisiones LPR que necesitan el catálogo devuelven ServiceUnavailable.
