# RF-001 — Autenticación

| ID | Requerimiento | Prioridad | Estado |
| --- | --- | --- | --- |
| RF-001 | Autenticación | Alta | Definido |

## 1. Objetivo

Permitir que los usuarios corporativos accedan a GarajeMetría mediante sus
credenciales de Active Directory local y obtengan una sesión gestionada por el
backend con tokens JWT de acceso y refrescamiento.

Referencias: [Visión y alcance, sección 3.1](../00-vision-y-alcance.md#31-identidad-y-sesión)
y [Catálogo de requerimientos](../01-catalogo-requerimientos.md).

Este documento especifica el comportamiento previsto. El requerimiento está
definido para su alcance; esto no significa que esté implementado ni probado.
La sección 7 distingue su estado de definición de la preparación operativa.

## 2. Actores y dependencias

- **Usuario corporativo:** proporciona su usuario y contraseña.
- **Aplicaciones consumidoras:** cualquier aplicación autorizada puede utilizar
  el mismo contrato HTTP, independientemente de su tecnología o plataforma.
- **Active Directory local:** valida las credenciales corporativas.
- **GarajeMetría:** controla el flujo de autenticación y emite sus tokens JWT.

[RF-002](RF-002-roles-and-workshop-access.md) define los roles derivados de los
grupos de AD y el taller predeterminado asignado localmente en GarajeMetría.
Una identidad válida del directorio sin pertenencia autorizante no puede
iniciar sesión. El Administrador puede acceder sin taller asignado y seleccionar
cualquier taller. El Asesor puede acceder si tiene un taller asignado; si no lo
tiene, se rechaza el inicio de sesión.

## 3. Alcance

Incluye inicio de sesión, emisión de tokens JWT de acceso y refrescamiento,
renovación y definición del ciclo de sesión: expiración, rotación, revocación,
cierre de sesión y tratamiento de usuarios deshabilitados.

Quedan fuera el autorregistro, la creación o mantenimiento de cuentas y
contraseñas del directorio y la autenticación de dispositivos LPR.

### 3.1. Independencia del tramo

RF-001 incluye los componentes de identidad, resolución de grupos, perfil local,
auditoría de aprovisionamiento y consulta de acceso necesarios para autenticar.
Implementa sus adaptadores AD/SQL y controles HTTP sin depender de tareas de
RF-002/RF-009. Las políticas de esos requisitos se respetan mediante contratos;
no se incluyen sus endpoints administrativos ni la sincronización corporativa.
La frontera y las pruebas propias están en
[DESIGN-001 / 2.1](../design/DESIGN-001-authentication.md#21-entrega-independiente-de-rf-001).

## 4. Reglas funcionales

Los identificadores RN, CA y CP se citan junto con RF-001 para indicar su
contexto. Estas 19 reglas agrupan las 68 entradas anteriores, que mezclaban
comportamiento funcional y concreciones técnicas. Los nombres de cookies,
claims, códigos, límites y mecanismos de persistencia siguen siendo contratos
obligatorios, descritos en la sección 5 y en DESIGN-001; no se duplican como
reglas funcionales independientes.

La correspondencia con la numeración anterior y los criterios actuales se
conserva en [DESIGN-001, sección 13](../design/DESIGN-001-authentication.md#13-trazabilidad-de-reglas-funcionales).
La agrupación no modifica las decisiones de autenticación ni los 85 CP.

| ID | Regla funcional |
| --- | --- |
| RN-001 | El usuario debe existir en el único dominio corporativo autorizado y autenticarse contra AD; GarajeMetría no registra cuentas ni mantiene sus contraseñas. El vínculo local conserva la identidad ante renombrados y no transfiere acceso a una cuenta recreada. |
| RN-002 | El acceso depende del rol obtenido de los grupos AD y del taller local según RF-002, nunca de datos de autorización enviados por el cliente. El Administrador puede entrar sin taller; el Asesor requiere taller asignado activo y queda restringido a él. Mantener asignaciones corresponde exclusivamente a Administradores. |
| RN-003 | Tras validar credenciales, identidad y grupos autorizantes, crear una sola identidad local si no existe, sin asignar taller automáticamente. El perfil del Asesor pendiente de asignación se conserva, pero no obtiene sesión. Intentos no autenticados o sin rol no crean perfiles. |
| RN-004 | Un login autorizado crea sesión y entrega acceso, refresh y datos de usuario mediante un contrato único para todos los consumidores. La renovación entrega nuevos tokens y sus vigencias sin devolver el perfil. El transporte y las respuestas se ajustan al contrato técnico acordado. |
| RN-005 | Validar la solicitud antes de consultar credenciales o modificar sesiones. Rechazar formatos, tamaños y valores no admitidos sin efectos de autenticación. Preservar exactamente la contraseña válida; su política de fortaleza y caducidad pertenece a AD. |
| RN-006 | No revelar contraseñas, tokens, secretos ni detalles internos del directorio o infraestructura en logs o errores. No conservar contraseñas de usuario. |
| RN-007 | Cada usuario mantiene como máximo una sesión activa, incluso ante concurrencia. Un nuevo login satisfactorio sustituye y cierra la anterior; un intento fallido la conserva. |
| RN-008 | Las duraciones se configuran externamente. Renovar no cambia el inicio ni amplía el límite absoluto de sesión; ningún token autoriza después de ese límite. La renovación normal conserva la vigencia propia del acceso anterior mientras la sesión siga activa. |
| RN-009 | Cada renovación exige refresh válido, sesión vigente y comprobación actual de cuenta y pertenencia en AD, sin conservar la contraseña. El refresh solo se consume una vez de forma atómica; un rechazo confirmado de cuenta cierra la sesión y no emite tokens. |
| RN-010 | Reutilizar un refresh auténtico, consumido y aún vigente revoca todos los tokens de su familia, incluidos los de una renovación concurrente ganadora. Si ya expiró al decidir sobre el reuso, devolver 401 sin revocar, aunque estuviera consumido y exista un sucesor vigente. No modificar cookies ni ampliar la sesión por ese rechazo. Conservar la información necesaria sin guardar el token recuperable. |
| RN-011 | Logout requiere refresh vigente y sesión activa, aunque el acceso haya expirado. El cierre explícito, sustitución o revocación invalida inmediatamente todos los tokens de esa sesión; una sesión finalizada o un refresh no vigente no permite renovarla. |
| RN-012 | Cambiar efectivamente el taller o publicar su desactivación cierra las sesiones afectadas según RF-002/RF-009. Detectar en refresh cambio de rol o pérdida de pertenencia exige nuevo login. Seleccionar contexto de consulta como Administrador no modifica la asignación ni cierra sesión. Reactivar un taller permite nuevo login con la misma asignación sin recuperar sesiones previas. |
| RN-013 | Un fallo técnico no concede acceso ni se confunde con un rechazo confirmado de credenciales o cuenta. Un fallo temporal de AD al renovar conserva sesión, refresh y vigencias. Las escrituras solo comunican éxito tras confirmación; un commit incierto no se afirma revertido ni se repite automáticamente. Los errores no entregan tokens ni modifican cookies. |
| RN-014 | La autenticación rechazada usa el mensaje genérico acordado sin revelar su causa. Solo tras validar credenciales se comunican falta de rol o de taller. Los errores HTTP mantienen las categorías y mensajes del contrato técnico. |
| RN-015 | Proteger las operaciones por defecto y autorizar únicamente con tokens válidos para su uso y una sesión persistida comprobada vigente. Un refresh no autoriza operaciones de negocio; no mantener acceso por caché obsoleta o si no puede verificarse la sesión. |
| RN-016 | Login y refresh aplican cuotas configuradas por IP y usuario compartidas entre todas las instancias. Los rechazos no ejecutan autenticación ni modifican sesión; los permisos admitidos no se devuelven por fallos posteriores. No admitir solicitudes si la cuota no puede comprobarse. |
| RN-017 | Todos los consumidores cumplen el mismo bootstrap y controles de origen y antiforgery para login, refresh y logout. El bootstrap no crea sesión ni cambia refresh. Un rechazo ocurre antes de mutar la sesión y no modifica cookies ni provoca revocación por reutilización. |
| RN-018 | La configuración ausente o inválida impide arrancar y genera diagnóstico seguro. Suministrar los valores privados corresponde a la preparación del entorno y no es una decisión funcional pendiente. |
| RN-019 | Conservar y eliminar sesiones, huellas y auditoría conforme a los plazos aprobados, sin eliminar familias revocables ni datos ajenos por cascada. La limpieza se coordina entre instancias. Tras restaurar, verificar retención y revocaciones antes de habilitar acceso; la conservación de backups tiene política independiente. |

Los endpoints están protegidos por defecto. El login necesita admitir una
solicitud sin sesión previa para iniciar la autenticación; esta excepción no
autoriza el acceso a las demás operaciones. La renovación y el logout admiten
el token de refrescamiento vigente, sujeto a su validación y al estado de sesión.

## 5. Flujos y referencias de contrato

DESIGN-001 es la fuente única de los contratos técnicos de autenticación. Las
secciones siguientes conservan referencias estables desde otros documentos;
no son copias de los valores técnicos. Los cambios de comportamiento actualizan
primero las reglas y CA de este requisito; sus concreciones se actualizan en
el diseño y los casos de prueba.

### 5.1. Inicio de sesión

Validar entrada y credenciales corporativas, resolver acceso y aprovisionar la
identidad local cuando corresponda. Solo tras autorizar, crear la sesión y
sustituir la anterior. Un rechazo conserva la sesión existente y no entrega
tokens. El perfil pendiente ya confirmado puede conservarse sin crear sesión.
Flujo técnico: [DESIGN-001 / 5.1](../design/DESIGN-001-authentication.md#51-login).

### 5.2. Renovación y cierre de sesión

Renovar exige refresh y sesión vigentes, comprobación actual de AD y consumo
único. El límite absoluto se conserva; reuso de un refresh aún vigente o
rechazo confirmado de cuenta cierran la sesión. Un refresh expirado recibe 401
sin revocar, incluso si ya estaba consumido. Un fallo temporal no consume ni amplía la sesión.
Logout cierra la sesión sin exigir un access token vigente.
Flujos: [DESIGN-001 / 5.2 y 5.3](../design/DESIGN-001-authentication.md#52-refresh).

### 5.3. Rutas y entrega de tokens

Solicitudes, rutas, DTOs, respuestas y errores están en
[DESIGN-001 / 4](../design/DESIGN-001-authentication.md#4-contrato-http).

#### 5.3.1. Política de validación del login

Los formatos admitidos de usuario, el tratamiento exacto de la contraseña y
los límites de estructura, longitud y cuerpo están en
[DESIGN-001 / 4.1](../design/DESIGN-001-authentication.md#41-operaciones).

### 5.4. Persistencia y límites

Modelo, atomicidad y sesión única:
[DESIGN-001 / 7](../design/DESIGN-001-authentication.md#7-persistencia-y-concurrencia).

### 5.4.1. Ventana deslizante y coordinación entre instancias

Algoritmo, cuotas, claves, cómputo de permisos y respuesta de limitación:
[DESIGN-001 / 7.1](../design/DESIGN-001-authentication.md#71-limitación-compartida-de-autenticación).

### 5.5. Decisiones técnicas de seguridad

Cookie y transporte:
[DESIGN-001 / 4.4](../design/DESIGN-001-authentication.md#44-cookie-y-protección-de-solicitudes).
Firma y verificación de tokens:
[DESIGN-001 / 6](../design/DESIGN-001-authentication.md#6-tokens-y-tiempo).

### 5.5.1. Identidad estable y consulta de estado de AD

Identidad, consulta mediante servicio y clasificación de estados:
[DESIGN-001 / 3.1.1](../design/DESIGN-001-authentication.md#311-integración-de-identidad-y-ad).

### 5.5.2. Contrato antiforgery y validación de origen

Bootstrap, cookies, cabecera, orígenes y errores:
[DESIGN-001 / 4.4.1](../design/DESIGN-001-authentication.md#441-contrato-e-implementación-antiforgery).

### 5.5.3. Contrato JWT y precisión temporal

Claims, usos, tolerancia y representación temporal:
[DESIGN-001 / 6](../design/DESIGN-001-authentication.md#6-tokens-y-tiempo).

### 5.5.4. Fallos de infraestructura y ProblemDetails

Clasificación, mensajes, formato y tratamiento de commits inciertos:
[DESIGN-001 / 4.3.1](../design/DESIGN-001-authentication.md#431-errores-de-persistencia-y-resultado-de-escritura).

### 5.6. Despliegue y configuración

Configuración obligatoria, archivo privado externo y valores iniciales:
[DESIGN-001 / 8](../design/DESIGN-001-authentication.md#8-configuración-y-despliegue).
Preparar el entorno no condiciona el estado de definición de RF-001.

### 5.7. Retención y eliminación de datos de autenticación

Plazos operativos, limpieza coordinada y protección tras restaurar:
[DESIGN-001 / 7.2](../design/DESIGN-001-authentication.md#72-retención-y-limpieza-diaria).

## 6. Criterios de aceptación

Los criterios agrupan resultados verificables de autenticación. Los detalles de
transporte, seguridad, persistencia y fronteras se concretan en las secciones
anteriores y en [DESIGN-001, sección 10](../design/DESIGN-001-authentication.md#10-verificación-y-trazabilidad),
que conserva los 85 casos de prueba planificados. Un criterio puede necesitar
varios casos de prueba; sus números no tienen que coincidir.

Esta revisión sustituye la lista anterior de 85 criterios por 20 agrupados.
Los identificadores CP-001 a CP-085 se conservan y la matriz de DESIGN-001
permite seguir cada criterio anterior mediante el CP que tenía el mismo número.
No se eliminan reglas, contratos ni escenarios de verificación.

| Criterio | Resultado esperado | Casos de prueba en DESIGN-001 |
| --- | --- | --- |
| CA-001 | Con credenciales válidas y acceso autorizado, el login crea una sesión y entrega los tokens y datos de usuario mediante el contrato único definido. | CP-001, CP-046 |
| CA-002 | Las solicitudes no admitidas se rechazan antes de validar credenciales o modificar sesiones; las contraseñas válidas se entregan intactas al directorio. | CP-004, CP-057, CP-058, CP-059, CP-060 |
| CA-003 | La autenticación resuelve el dominio permitido y conserva la identidad estable del usuario ante cambios de nombre, sin transferir acceso a una cuenta recreada. | CP-012, CP-013, CP-014, CP-061 |
| CA-004 | Las credenciales o cuentas rechazadas y los usuarios sin pertenencia autorizante no obtienen tokens; los mensajes distinguen autenticación de autorización sin revelar estados de cuenta. | CP-002, CP-008, CP-015, CP-036, CP-037 |
| CA-005 | Login aplica los roles de RF-002 y su restricción de taller; crea una única identidad local tras validar AD y grupos y conserva el perfil del Asesor pendiente sin concederle sesión. | CP-009, CP-010, CP-011, CP-027, CP-068, CP-069 |
| CA-006 | Cada usuario mantiene como máximo una sesión activa, incluso con logins concurrentes. Un nuevo login satisfactorio sustituye la anterior y uno fallido la conserva. | CP-016, CP-017, CP-018, CP-042 |
| CA-007 | Tokens y sesión respetan las duraciones configuradas y el límite absoluto sin extensiones por renovación, fallos, tolerancia o redondeo; el acceso anterior conserva solo su vigencia normal. | CP-005, CP-024, CP-031, CP-032, CP-033, CP-034, CP-071, CP-072 |
| CA-008 | Solo un refresh válido y una sesión vigente permiten renovar tras comprobar el estado y acceso actuales en AD; la rotación consume el refresh una sola vez y un rechazo confirmado de cuenta cierra la sesión. | CP-007, CP-020, CP-021, CP-028, CP-029, CP-062, CP-063 |
| CA-009 | Reutilizar un refresh auténtico, consumido y aún vigente revoca su familia, incluidos los tokens emitidos por una renovación concurrente. Si ya expiró, recibe 401 sin revocar ni cambiar cookies o vigencias, aunque haya sucesor activo. No se almacenan tokens recuperables. | CP-022, CP-023, CP-054 |
| CA-010 | Logout cierra la sesión aunque el access token haya expirado si el refresh sigue vigente. El cierre invalida todos los tokens de la sesión; tokens o sesiones no vigentes no permiten renovarla. | CP-019, CP-025, CP-048, CP-050 |
| CA-011 | Solo un JWT válido para su uso y una sesión comprobada vigente autorizan la operación; un refresh no autoriza operaciones de negocio y una caché obsoleta no conserva acceso. | CP-052, CP-053, CP-070 |
| CA-012 | Todos los consumidores usan las mismas rutas, métodos, solicitudes, respuestas y códigos HTTP definidos; las respuestas sensibles no se almacenan en caché y el refresh solo se entrega y recibe por su cookie. | CP-039, CP-040, CP-044, CP-045, CP-049, CP-056 |
| CA-013 | Bootstrap prepara el contexto antiforgery sin crear sesión ni modificar refresh; login, refresh y logout exigen los controles acordados. Un rechazo no modifica sesiones ni cookies y logout elimina ambas cookies correctamente. | CP-055, CP-064, CP-065, CP-066, CP-067 |
| CA-014 | Los fallos de AD o persistencia producen errores seguros sin conceder acceso ni éxito parcial; un fallo temporal de renovación conserva su estado. Un commit indeterminado no se afirma revertido ni se repite automáticamente. | CP-003, CP-030, CP-038, CP-047, CP-073, CP-074, CP-075, CP-076 |
| CA-015 | Login y refresh respetan las cuotas por IP y usuario entre todas las instancias, con ventana y conteo definidos. Agotar cuota produce rechazo con tiempo de reintento; no poder comprobarla impide admitir la solicitud. | CP-043, CP-051, CP-077, CP-078, CP-079, CP-080 |
| CA-016 | No se almacenan contraseñas ni se revelan credenciales o tokens en logs y errores. La integración AD utiliza LDAPS con validación de certificado. | CP-006, CP-041 |
| CA-017 | La configuración obligatoria ausente o inválida impide arrancar y genera un diagnóstico sin secretos; los valores privados se suministran externamente. | CP-035 |
| CA-018 | Sesiones, huellas y auditoría se conservan y eliminan conforme a los plazos aprobados, mediante limpieza coordinada, sin borrar familias activas ni datos relacionados fuera de su alcance. | CP-081, CP-082, CP-083 |
| CA-019 | Una restauración no habilita acceso hasta verificar retención y revocaciones; la eliminación operativa no se presenta como eliminación de copias de seguridad. | CP-084 |
| CA-020 | Los cambios efectivos de taller y las desactivaciones publicadas cierran las sesiones afectadas; cambios de rol o pérdida de acceso AD se detectan en refresh. Reactivar un taller permite nuevo login sin recuperar sesiones anteriores. | CP-026, CP-085 |

Las decisiones de autenticación que consumen contratos de RF-002/RF-009 se
prueban en este tramo con dobles en Application. La publicación real del catálogo,
el mantenimiento de asignaciones, la autorización de recursos de negocio y su
auditoría se verifican al integrar sus respectivos tramos. No se requiere
implementar esas funcionalidades para probar las decisiones de RF-001 ni se
consideran verificadas sus integraciones mediante dobles.

Las pruebas implementadas y su evidencia se registran en
[TASKS-001](../tasks/TASKS-001-authentication.md). Las pruebas unitarias no usan
red ni base de datos; HTTP usa WebApplicationFactory y SQL Server aislado.
La integración real de AD requiere su entorno de pruebas y no se considera
verificada mediante los dobles utilizados en los tests HTTP.

## 7. Estado de definición y notas operativas

Las reglas funcionales y los contratos de autenticación están definidos para
el alcance descrito. No quedan decisiones funcionales pendientes de RF-001.

**Preparación operativa:** configuración privada, certificados y entornos de
verificación se preparan conforme a DESIGN-001 / 8 y TASKS-001. No son decisiones
funcionales abiertas. Los nombres internos se resuelven durante implementación
respetando contratos e invariantes.
