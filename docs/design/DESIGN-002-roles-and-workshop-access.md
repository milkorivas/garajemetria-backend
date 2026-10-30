# DESIGN-002 — Roles y acceso a talleres

| Campo | Valor |
| --- | --- |
| Requerimiento de origen | [RF-002 — Roles y acceso a talleres](../requirements/RF-002-roles-and-workshop-access.md) |
| Plan de implementación | [TASKS-002](../tasks/TASKS-002-roles-and-workshop-access.md) |
| Estado | Especificación técnica para desarrollo guiado por especificaciones (SDD) |
| Implementación | Administración implementada; verificación AD aislado pendiente |
| Plataforma | C# 14, .NET 10, ASP.NET Core, EF Core y SQL Server |
| Integración | [RF-001](../requirements/RF-001-authentication.md) y [DESIGN-001 — Autenticación](DESIGN-001-authentication.md) |

## 1. Propósito y alcance

Concretar RF-002 en contratos, invariantes, flujos, persistencia y pruebas.
Los identificadores RN, CA y CP de este documento pertenecen a RF-002, salvo
referencia expresa a RF-001. Las reglas funcionales de RF-002 prevalecen;
`AGENTS.md` establece los límites de arquitectura.

Reutiliza resolución de rol, identidad local y auditoría de aprovisionamiento
implementadas en RF-001. Incluye restricción por taller, consultas de usuarios/asignaciones y
mantenimiento transaccional del taller con auditoría y revocación de sesión.
No implementa administración de grupos o contraseñas AD, importación masiva,
asignación manual de roles, mantenimiento del catálogo de talleres ni endpoints
de imágenes, consultas de vehículos o correcciones. Sus permisos se expresan
aquí para que los casos de uso correspondientes los apliquen.

Los contratos administrativos están implementados sobre RF-001 y RF-009.
La evidencia de pruebas SQL/HTTP y sus límites se recoge en TASKS-002; la
verificación con AD real aislado sigue pendiente.

## 2. Identidad, roles y acceso

### 2.1. Resolución de roles

| Pertenencia efectiva en AD | Rol visible | Valor de transporte |
| --- | --- | --- |
| `web_GarajeMetria_Admin`, con o sin Advisor | Administrador | `administrator` |
| `web_GarajeMetria_Advisor`, sin Admin | Asesor | `advisor` |
| Ninguno | Sin acceso | No emitir sesión ni tokens |

La pertenencia incluye miembros directos y transitivos por grupos anidados.
Resolverla para el objectGUID de la identidad autenticada en el único dominio,
mediante la identidad de servicio LDAPS de DESIGN-001. Una consulta incompleta,
timeout, atributo ilegible o falta de permisos no equivale a pertenencia vacía
ni permite usar un rol local anterior como fallback.

Infrastructure determina la pertenencia completa necesaria a ambos grupos;
Application aplica la prioridad y obtiene un único rol. El mecanismo de consulta
debe manejar ciclos y no confundir lectura de grupos con pertenencia. Si se
recorren grupos, evitar visitas repetidas y consultas sin límites; si no puede
completarse de forma verificable, devolver indisponibilidad sin conceder acceso.
La estrategia se valida en AD aislado con grupos directos y anidados reales.

En login sin pertenencia autorizante: 403 y mensaje de falta de rol de RF-001,
solo después de validar credenciales. En login con lectura fallida: 503, sin
crear perfil ni sustituir una sesión existente.

Consultar de nuevo en cada refresh válido, antes del consumo. Comparar el rol
resuelto con la instantánea de sesión: si cambia o desaparece, cerrar la sesión
y devolver el 401 de sesión finalizada, sin nuevos tokens. Un fallo técnico
devuelve 503 sin consumir, cerrar ni extender sesión. Cambios de pertenencia
que no cambian el rol efectivo no provocan cierre.

No consultar AD en cada operación de negocio ni prometer revocación instantánea
desde el momento de un cambio en AD. Los accesos anteriores siguen sujetos a
su expiración y al estado local de sesión hasta detectar el cambio en refresh.

### 2.2. Valores JSON y JWT

`user.role` de login es exactamente `administrator` o `advisor`. El access token
contiene un único claim `role` con el mismo valor y la sesión conserva ese rol
como instantánea. El refresh token no contiene `role`. No usar nombres de grupos,
traducciones, arrays de roles ni ordinales de enum como transporte.

Api aplica el mapeo y la validación de claims definidos en DESIGN-001. El rol
firmado nunca sustituye la comprobación de vigencia de sesión o autorización
del taller. No existe endpoint ni campo editable para asignar un rol.

### 2.3. Invariantes de taller

Aplicar [RF-009](../requirements/RF-009-workshop-catalog.md): el Asesor requiere
taller activo en la copia local. Solo asignar talleres activos; 404 si no
existe, 409 si está inactivo. Si no hay carga inicial completa, responder 503
cuando sea necesario validar taller. Ante caída del origen se permite la
última copia confirmada, incluso pasados 15 minutos, con incidencia registrada.
Esto no sustituye la comprobación obligatoria de sesión en SQL local.

Publicar la desactivación y cerrar las sesiones de rol `advisor` afectadas
atómicamente, conservando asignaciones e historial. Las sesiones de
`administrator` no se cierran por esa causa. La reactivación permite nuevo
login con la misma asignación, pero no reabre sesiones. Coordinar publicación,
login, refresh y asignación con revalidación transaccional del estado local;
no consultar el sistema corporativo en cada petición de negocio. El adaptador,
ejecutor y consulta del catálogo requieren el diseño pendiente de RF-009.

- Administrador puede consultar cualquier taller, imágenes y realizar
  correcciones conforme a sus requerimientos; puede tener taller null.
- Asesor consulta únicamente su taller asignado; no puede ver imágenes,
  realizar correcciones ni acceder al mantenimiento de usuarios/asignaciones.
- Seleccionar contexto de consulta como Administrador no escribe la asignación,
  no genera un cambio administrativo ni cierra la sesión.
- El Asesor pendiente de primera asignación puede tener perfil con taller null,
  pero no sesión. No es una excepción para acceder sin taller.
- Un Asesor asignado solo admite sustitución por otro taller existente. No
  retirar el taller para dejarlo sin asignación.
- Un cambio posterior de rol AD no concede acceso a un Asesor sin taller;
  debe recibir asignación antes de poder iniciar sesión.

El usuario autenticado y su taller se obtienen de contexto confiable y estado
local. Un identificador enviado en ruta, filtro o cuerpo no amplía permisos.
Las consultas de negocio deben restringirse por taller dentro del acceso a
datos detrás de Application; no cargar datos ajenos para filtrarlos en Api.

## 3. Modelo y arquitectura

| Concepto | Datos e invariantes |
| --- | --- |
| `LocalUser` | Identificador local, objectGUID único, nombre visible, nombre de cuenta, taller nullable y versión de asignación. No contiene contraseña ni un rol local editable. |
| `WorkshopAssignmentAuditEntry` | Id, autor local, usuario afectado, taller anterior/nuevo, motivo, occurredAt UTC y correlación. Historial inmutable. |
| Evento de aprovisionamiento | Identidad estable interna, instante UTC y correlación para la creación automática; sin Administrador o motivo ficticios. |
| Sesión de DESIGN-001 | Identidad local, instantánea del rol y vigencia; se revoca al cambiar efectivamente el taller. No duplicar su modelo. |

«Pendiente de primera asignación» se deriva de perfil sin taller y rol Asesor
resuelto desde AD; no necesita una concesión local de rol ni una segunda
fuente de autorización. La versión solo cambia ante una modificación efectiva
del taller, no al actualizar metadatos de nombre del perfil.

| Capa | Responsabilidades |
| --- | --- |
| Domain | Reutilizar identidad y roles de RF-001; implementar invariantes de asignación y auditoría administrativa, sin I/O, HTTP, LDAP o EF. |
| Application | Reutilizar resolución de rol y aprovisionamiento de RF-001; implementar consultas, validación y coordinación administrativa de asignación/auditoría/revocación. |
| Infrastructure | LDAPS y pertenencia transitiva, consultas EF, repositorios, mappings, migraciones, unidad de trabajo y concurrencia. |
| Api | DTOs explícitos, Bearer y políticas, rutas, JSON, ETag/If-Match y ProblemDetails; sin DbContext en endpoints. |

### 3.1. Contratos de Application

Reutilizar `ICorporateDirectory`, `IAuthenticationSessionRepository` e
`IUnitOfWork` de DESIGN-001. El directorio devuelve una identidad estable y
pertenencias verificadas o un resultado de fallo explícito; no tipos LDAP.

| Contrato | Responsabilidad |
| --- | --- |
| `ILocalUserRepository` | Obtener/crear identidad única y aplicar cambios condicionales de asignación. Nunca sobrescribir un taller al resolver una colisión de creación. |
| `IUserAccessQuery` | Obtener taller y datos locales necesarios para autenticación/autorización; no conceder roles desde SQL. Reutilizar el contrato de DESIGN-001. |
| `IUserAdministrationQuery` | Devolver página materializada de perfiles y lectura de asignación/versionado. No exponer IQueryable. |
| `IWorkshopQuery` | Obtener existencia, actividad y disponibilidad de copia inicial según RF-009, sin trasladar persistencia a Domain o Api. Revalidar estado en la transacción de asignación. |
| `IWorkshopAssignmentAuditRepository` | Incorporar auditoría inmutable en la misma unidad de trabajo que la asignación y el cierre. |
| Contexto de usuario actual | Obtener Id local, sesión y rol verificados, sin aceptar esos valores del cuerpo de solicitud ni resolver servicios dinámicamente. |

Usar MediatR para `ListLocalUsersQuery`, `GetDefaultWorkshopQuery` y
`SetDefaultWorkshopCommand`, con validación asíncrona FluentValidation previa
al handler. El aprovisionamiento es un paso de Application compartido con el
login; no duplicar la autenticación ni introducir una segunda transacción de
sesión. Verificar el permiso administrativo también al ejecutar el caso de uso,
antes de consultar existencia de recursos.

Mantener scoped DbContext/repositorios, handlers transitorios y cancelación
desde HTTP. No ejecutar operaciones concurrentes sobre el mismo DbContext.
Inyectar reloj basado en TimeProvider para fechas deterministas.

## 4. Flujos

### 4.1. Integración con el primer acceso de RF-001

El primer acceso y el aprovisionamiento se implementan una sola vez en
[DESIGN-001 / 5.1](DESIGN-001-authentication.md#51-login), incluidos identidad
única por GUID, auditoría, roles y rechazo del Asesor sin taller conservando
perfil. RF-002 no añade otro handler, transacción de alta ni mecanismo de
provisión del primer Administrador.

Las consultas administrativas utilizan los perfiles existentes para localizar
al Asesor pendiente y asignarle taller mediante 4.3. Verificar la integración
con altas concurrentes y con el perfil conservado tras un 403, sin sobrescribir
asignaciones. Si RF-002 incorpora una versión de asignación adicional, su
migración inicializa los perfiles existentes sin recrear identidades ni
registros de aprovisionamiento.

### 4.2. Consultas administrativas

Autorizar Administrador con sesión vigente antes de consultar usuarios/talleres.
La lista solo accede a SQL y devuelve datos locales; no lee ni importa AD.
La lectura de asignación devuelve la versión con la que el cliente podrá
condicionar una modificación. Las consultas son AsNoTracking y proyectan solo
los campos del contrato.

### 4.3. Asignación, sustitución y retirada

1. Autorizar al actor y validar ruta, cuerpo, motivo y precondición If-Match.
2. Leer la asignación del usuario objetivo; si no existe, devolver 404. Si la
   versión ya difiere, devolver 412 sin cambios.
3. Si workshopId no es null, verificar existencia y actividad del taller,
   incluso si coincide con la asignación actual: 404 inexistente, 409 inactivo,
   503 sin copia inicial completa. No seleccionar
   un taller implícito ni interpretar cadena vacía como retirada.
4. Si workshopId es null, consultar el rol efectivo actual del objetivo por
   objectGUID mediante AD: solo Administrador admite el valor. Asesor o sin
   rol: 409; lectura no verificable: 503. Hacer esta consulta fuera de SQL.
   Esta condición se aplica también a una solicitud null sin efecto.
5. Abrir transacción y volver a comprobar usuario, versión y referencias
   necesarias. Serializar por usuario con los cambios de sesión de DESIGN-001.
6. Si el taller no cambia, conservar versión y sesión y no crear auditoría de
   cambio. Devolver 204 y el mismo ETag, con solicitud/motivo previamente válidos.
7. Si cambia, aplicar asignación y nueva versión, registrar motivo y auditoría,
   y cerrar la sesión activa del usuario objetivo como una operación atómica.
8. Confirmar y devolver 204 sin cuerpo y ETag resultante. Si una escritura falla
   antes de commit, revertir la operación completa; no devolver éxito parcial.
   Si la comunicación de commit se interrumpe, aplicar el resultado indeterminado
   de DESIGN-001 / 4.3.1 sin afirmar que hubo rollback.

El cambio propio del Administrador puede terminar con 204, pero sus siguientes
peticiones fallan por cierre de sesión. La comprobación AD del objetivo no
constituye una transacción con el directorio ni evita un cambio externo
posterior; login/refresh siguen aplicando sus reglas de detección.

## 5. Contratos HTTP

Todas estas rutas requieren Bearer de Administrador y sesión vigente.
Cookies de refresh o antiforgery no las autentican. Usar DTOs de transporte
sin vincular entidades y `Cache-Control: no-store` en respuestas administrativas.

| Método y ruta | Entrada | Respuesta satisfactoria |
| --- | --- | --- |
| `GET /api/users` | pageNumber, pageSize | 200: items, pageNumber, pageSize, totalCount |
| `GET /api/users/{userId}/default-workshop` | Identificador local | 200: userId, workshopId nullable; ETag |
| `PUT /api/users/{userId}/default-workshop` | If-Match; JSON workshopId, reason | 204 sin cuerpo; ETag resultante |

### 5.1. Consultas

Cada elemento de items contiene exactamente `id`, `displayName`, `username` y
`defaultWorkshopId` nullable. No incluir objectGUID, credenciales ni un rol
editable. Identificadores como cadenas; no serializar entidades EF.

Valores iniciales configurables de paginación: página 1, tamaño 20, máximo 100.
Rechazar valores no enteros, no positivos o superiores al máximo. Orden fijo
por identificador local ascendente, sin campos de filtro/sort arbitrarios.
Calcular desplazamientos sin desbordamientos y devolver una página vacía si
no hay resultados para la página solicitada. No prometer una instantánea del
directorio ni consistencia entre páginas obtenidas durante cambios concurrentes.

### 5.2. Escritura y validación

```json
{
  "workshopId": "workshop-example-002",
  "reason": "Asignación al equipo de recepción del taller"
}
```

| Campo/control | Regla |
| --- | --- |
| userId | String no vacío, sin espacios de borde, máximo 128 valores escalares Unicode; identidad local existente |
| workshopId | Propiedad obligatoria, string con las mismas restricciones de identificador y taller existente, o null para objetivo Administrador según AD |
| reason | String obligatorio, 1 a 500 valores escalares Unicode, al menos un carácter no blanco, sin controles; preservar el motivo validado |
| JSON | Objeto UTF-8 plano con esos dos campos exactos; rechazar desconocidos, duplicados, tipos erróneos y Unicode inválido |
| Cuerpo | Límite inicial configurable de 16 KiB antes de deserializar; no aceptar los formatos/codificaciones alternativos rechazados por la política JSON de RF-001 |
| If-Match | Un único ETag fuerte concreto obtenido de la lectura; sin wildcard ni etiquetas débiles |

El autor, rol, fechas, versiones nuevas y correlación de auditoría proceden del
servidor. Un PUT sin efecto sigue exigiendo todos los campos y motivo válidos.
Application valida formatos y límites con FluentValidation; Api controla JSON,
tamaño y cabeceras. Contar valores escalares, no unidades UTF-16 de string.Length.

### 5.3. Concurrencia y errores

Representar la versión de asignación con un valor opaco generado por la
aplicación y persistido; no publicar rowversion ni otro tipo del proveedor.
Api lo codifica como ETag fuerte entre comillas. Su identidad/versionado debe
corresponder al recurso leído; no reutilizar etiquetas entre usuarios.
Una modificación efectiva genera otra versión incluso si luego se vuelve al
taller anterior. Una operación sin efecto conserva la existente.

| HTTP | Condición |
| --- | --- |
| 400 | Formato, campos, motivo, paginación o If-Match no admitidos |
| 401 | Bearer inválido/expirado o sesión finalizada |
| 403 | Actor autenticado sin permiso administrativo |
| 404 | Usuario local o taller solicitado inexistente, después de autorizar |
| 409 | workshopId null incompatible con rol efectivo del objetivo o taller destino inactivo |
| 412 | If-Match no coincide con la versión actual |
| 413 / 415 | Cuerpo excesivo o formato/codificación no admitidos |
| 428 | Falta If-Match |
| 503 | No puede verificarse el rol AD necesario para aceptar null |
| 500 / 503 | Fallo inesperado, indisponibilidad temporal de SQL o resultado de commit indeterminado, según DESIGN-001 / 4.3.1 |

Usar ProblemDetails y mensajes seguros sin valores intentados, SQL o detalles
del directorio. Una indisponibilidad al comprobar la sesión y los fallos
inesperados siguen el manejo centralizado de DESIGN-001; nunca conceder acceso
si la vigencia no puede verificarse ni convertir excepciones inesperadas en 204.
El contrato está cerrado en DESIGN-001 / 4.3.1 y RF-001 / 5.5.4. Si el commit
no puede confirmarse, no afirmar rollback: devolver operation_outcome_unknown
cuando pueda responderse y releer asignación/ETag antes de otra escritura.

La versión se comprueba de nuevo dentro de la escritura condicional; una
lectura previa no basta. Si dos PUT compiten con el mismo ETag y ambos cambian
asignación, solo uno confirma; el otro recibe 412. No reintentar automáticamente
una escritura que perdió la respuesta: releer, valorar el resultado y decidir
de nuevo con la versión vigente. No duplicar auditoría al repetir una operación.

Mantener CORS explícito de DESIGN-001 y permitir If-Match para estas rutas;
exponer ETag a los orígenes autorizados para que el navegador pueda leerlo.
No añadir modos de autenticación por plataforma ni permitir que cookies
autoricen estas rutas administrativas.

## 6. Persistencia, auditoría y carreras

Mappings separados en Infrastructure: unicidad de objectGUID, claves foráneas
para taller y usuarios de auditoría, motivo limitado a 500 valores escalares
admitidos, precisión temporal explícita e índices para orden/paginación y
consulta de asignación. Dimensionar el almacenamiento Unicode para caracteres
suplementarios; no suponer que 500 valores escalares ocupan 500 unidades UTF-16.
Usar DateTimeOffset UTC y no borrar historial en cascada al modificar perfiles.

Mantener una sola unidad de trabajo para asignación, auditoría y revocación.
No guardar auditoría únicamente en logs ni en una segunda transacción que
pueda confirmar separadamente. La entrada incluye Id, actor local confiable,
usuario afectado, anterior/nuevo, motivo, occurredAt y correlación; no puede
editarse o borrarse mediante esta API. El evento de aprovisionamiento no
requiere motivo administrativo y se confirma con la creación del perfil.

Login y mantenimiento deben compartir una coordinación por usuario en SQL:
si el cambio se confirma antes, login usa la asignación actual; si login se
confirma primero, la modificación cierra esa sesión. Refresh revalida sesión
y versión/estado pertinente antes de confirmar para no reactivar una sesión
cerrada. Implementar bloqueo o actualización condicional adecuados dentro de
Infrastructure y probar el comportamiento con SQL Server real.

La revocación impide autorizaciones posteriores al commit; no cancela
retroactivamente una operación que ya se autorizó. No usar caché de sesión que
prolongue acceso tras el cambio. No abrir transacciones durante LDAPS ni
intentar coordinar AD y SQL con una transacción distribuida.

## 7. Configuración y seguridad operativa

Reutilizar opciones LDAPS, identidad de servicio y configuración privada de
DESIGN-001. Verificar existencia de los grupos y permisos efectivos de lectura,
incluida la consulta transitiva, con el administrador de AD. No versionar
conexiones, secretos técnicos ni material de certificados.

Configurar explícitamente paginación y límite de cuerpo con los valores
iniciales de RF-002, validándolos al arrancar. Mantener los límites funcionales
de campos en coherencia con DTOs, validadores, mappings y OpenAPI.

Registrar fallos y correlación sin contraseñas, JWT, cookies, payloads completos
ni motivos de auditoría volcados indiscriminadamente en logs. La auditoría de
asignaciones y aprovisionamiento sigue los plazos de DESIGN-001 / 7.2. Integrar
su eliminación con el único proceso diario por lotes de DESIGN-001 / 7.2;
no duplicar un supervisor por módulo ni eliminar perfiles locales por esta política.
No publicar endpoints de edición/borrado de auditoría, alta manual de roles,
administración de grupos o sincronización masiva.

## 8. Pruebas y trazabilidad

La matriz guía la cobertura de autenticación, catálogo y administración; las
pruebas contra AD real aislado siguen pendientes. Mantener referencias
RF-002 / CA-XXX y CP-XXX en las pruebas; la matriz de permisos no implica que
los endpoints de otros requerimientos estén implementados en este alcance.

| Escenario | Reglas | Criterios/casos | Nivel |
| --- | --- | --- | --- |
| Admin/Advisor, prioridad y pertenencia anidada | RN-001, 002, 003, 008, 009, 013 | CA/CP-001, 002, 003, 008, 011 | Domain/Application; AD aislado |
| Restricción por taller y operación | RN-004, 005, 006 | CA/CP-004, 005, 006 | Application y HTTP; contratos de otros requerimientos |
| Revocación por cambio local o grupos | RN-007, 010, 014 | CA/CP-007, 009, 012 | Application, HTTP, SQL Server y AD aislados |
| Rol JSON/JWT, sin override local | RN-002, 012, 013 | CA/CP-008, 013 | Application y HTTP/validación de tokens |
| Primer Administrador y Asesor pendiente | RN-009, 011, 015, 016 | CA/CP-010, 014, 015 | Application, HTTP y SQL Server |
| Motivo, auditoría y transacción | RN-007, 017, 018 | CA/CP-016 | Domain/Application y SQL Server |
| ETag, ausencia de cambio y consultas administrativas | RN-018 | CA/CP-017, 018 | HTTP y SQL Server concurrente |
| Retirada solo para Administrador objetivo | RN-005, 016 | CA/CP-019 | Application, HTTP y AD aislado |
| Fallos de infraestructura y commit indeterminado | RN-019 | CA/CP-020 | Application, HTTP y SQL Server con fallos controlados |
| Retención de auditoría y aprovisionamiento | RN-020 | CA/CP-021 | Reloj determinista y SQL Server aislado con limpieza coordinada |
| Catálogo activo y ciclo de desactivación/reactivación | RN-021 | CA/CP-022 | Application/HTTP y SQL Server con publicación concurrente según RF-009 |

Pruebas adicionales necesarias para verificar la coordinación:

- Primer acceso concurrente: un perfil y un evento de aprovisionamiento, sin
  sobrescribir asignación existente; 403 del Asesor conserva perfil sin sesión.
- Dos cambios de taller con mismo ETag: un ganador y un 412, una sola auditoría.
- Error al escribir auditoría o cerrar sesión: asignación, versión y sesión
  anteriores conservadas tras rollback.
- Carrera con login/refresh: ninguna sesión anterior al cambio queda autorizando
  con asignación obsoleta y ninguna renovación reactiva sesiones cerradas.
- PUT propio del Administrador: 204 y acceso posterior denegado por revocación.
- Solicitud null sin efecto para Asesor pendiente: 409, sin auditoría ni cierre.
- Cambio de nombre AD conserva identidad; recreación con mismo nombre no hereda
  perfil/taller. Fallo al consultar grupos nunca concede rol por caché local.
- Rechazos antes de consultar existencia para Asesor y llamadas sin sesión;
  cookies sin Bearer no autorizan mantenimiento.
- Motivos/identificadores con Unicode suplementario y límites exactos; JSON
  duplicado, sobreasignación, cuerpo excesivo y paginación inválida.

Domain y Application usan dobles y reloj controlado, sin red ni base de datos.
HTTP se prueba con WebApplicationFactory. Persistencia usa SQL Server aislado,
nunca EF InMemory o SQLite. Para LDAPS usar cuentas y grupos sintéticos con el
responsable de AD, sin datos o recursos de producción. Mantener pruebas de
arquitectura y comprobar las restricciones de dependencia de cada capa.

## 9. Secuencia de implementación SDD

1. Convertir la matriz CA/CP administrativa en escenarios; identificar las
   pruebas de integración con autenticación y catálogo ya descritas.
2. Reutilizar roles, prioridad, perfil y aprovisionamiento de RF-001. Implementar
   únicamente las invariantes de asignación, motivo y auditoría administrativa.
3. Definir consultas y handlers administrativos con validación y autorización,
   reutilizando los contratos de identidad, directorio y sesión de DESIGN-001.
4. Ampliar mappings y migraciones de los modelos existentes para versionado y
   auditoría administrativa. Verificar escrituras condicionales, atomicidad y
   carreras con login/refresh, sin recrear el perfil o la persistencia de sesión.
5. Verificar que las operaciones administrativas reutilizan la resolución LDAPS
   de RF-001 y manejan sus resultados; no implementar otro adaptador de grupos.
6. Implementar DTOs/rutas, ETag, ProblemDetails y paginación; verificar Bearer,
   autorización de operación/recurso y CORS para If-Match/ETag.
7. Integrar la auditoría administrativa en la limpieza existente de DESIGN-001 / 7.2.
   Actualizar OpenAPI y preparar los entornos. Ejecutar desde la raíz:
   `dotnet restore`, `dotnet build --no-restore --configuration Release`,
   `dotnet format --verify-no-changes --no-restore` y
   `dotnet test --no-build --configuration Release`.

## 10. Condiciones de cierre

Los contratos administrativos de RF-002 están definidos para su alcance;
reutilizan autenticación de RF-001 sin convertir RF-002 en un prerrequisito suyo.
La implementación se completa cuando los 22 criterios están cubiertos, se
verifica la integración con DESIGN-001, pasan los checks aplicables sin nuevas
advertencias y la documentación/OpenAPI reflejan los contratos reales.

La comprobación de grupos/permisos AD, configuración privada, provisión de SQL
Server aislado y ejecución de pruebas siguen siendo prerrequisitos de
verificación, no evidencia de funcionalidad implementada. La retención de la
base operativa está aprobada en RF-001 / 5.7 y diseñada en DESIGN-001 / 7.2;
las copias tienen política separada. No desplegar ni habilitar procesos de limpieza
como consecuencia de crear esta especificación.
