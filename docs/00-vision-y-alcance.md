# GarajeMetría — Visión y alcance

Estado: borrador para definición funcional.

Este documento recoge la visión general del backend, su alcance, los actores,
la arquitectura de alto nivel y las reglas comunes. Las decisiones pendientes
deben concretarse antes de implementar los casos de uso afectados. Describe el
producto previsto; no implica que sus funcionalidades ya estén implementadas.

## 1. Objetivo

Servir de backend a la aplicación **GarajeMetría**, que permite controlar los
vehículos que entran, salen y permanecen en las instalaciones de un taller
mecánico.

El sistema debe permitir consultar cuándo un vehículo específico estuvo en las
instalaciones, a partir de su historial de entradas, salidas y estancias. La
identificación de vehículos se apoya en eventos de reconocimiento de matrículas
(LPR).

## 2. Alcance

### 2.1. Funcionalidades incluidas

- Exponer una API REST para la aplicación GarajeMetría y sus integraciones
  autorizadas.
- Recibir y conservar eventos LPR asociados a un dispositivo y a un taller,
  conforme al contrato de integración que se acuerde.
- Almacenar en disco las imágenes de matrícula asociadas a los eventos LPR
  cuando se reciban. La imagen es opcional; su ausencia no impide recibir el
  evento. La ruta de almacenamiento será configurable mediante un archivo de
  configuración. Las imágenes no se almacenarán en la base de datos.
- Registrar y consultar entradas, salidas y estancias de vehículos según las
  reglas de negocio que se definan.
- Utilizar la dirección indicada por la cámara cuando esté disponible y
  determinarla en el backend cuando no lo esté: primera lectura como entrada
  y última lectura como salida, agrupando por vehículo, taller y día.
- En esta primera versión, si solo hay una lectura para un vehículo, taller y
  día, asignar una duración de estancia de X minutos, configurable en un archivo.
- Permitir al Administrador realizar correcciones manuales de entradas,
  salidas y estancias, con trazabilidad. Los campos corregibles se concretarán.
- Permitir consultar las imágenes almacenadas mediante la API exclusivamente
  al Administrador.
- Consultar los vehículos presentes en un taller y el historial de presencia de
  un vehículo, con filtros temporales y paginación.
- Proporcionar los datos necesarios para el panel de administración. Sus
  operaciones e indicadores concretos quedan pendientes de definición.
- Controlar el inicio de sesión de usuarios existentes en el directorio activo
  corporativo y emitir un token de acceso o sesión JWT y un token de
  refrescamiento JWT.
- Permitir la renovación de la sesión mediante el token de refrescamiento,
  conforme a las políticas de sesión que se acuerden.
- Aplicar autorización y aislamiento de datos por taller en las operaciones
  expuestas por la API, con varios talleres y un taller asociado por defecto
  a cada usuario, salvo que el Administrador puede acceder sin taller asignado.

### 2.2. Funcionalidades fuera del alcance

- Registro público o autorregistro de usuarios.
- Alta, baja y mantenimiento de cuentas, credenciales y contraseñas del
  directorio activo; estas tareas se realizan fuera de GarajeMetría.
- Implementación de la interfaz de usuario dentro de este repositorio backend.
- Desarrollo del motor de reconocimiento de matrículas; el backend recibe los
  eventos producidos por el sistema LPR externo.
- Integraciones adicionales a Active Directory local, los sistemas LPR y la
  fuente corporativa de talleres definida en RF-009, en la primera versión.
- Limpieza de imágenes y eventos antiguos desde la aplicación; este
  mantenimiento se realiza externamente.

- Gestión de reparaciones, citas y órdenes de trabajo.
- Facturación.
- Notificaciones.
- Exportaciones.

## 3. Usuarios, actores y sistemas externos

| Actor o sistema | Responsabilidad o interacción |
| --- | --- |
| Administrador | Usuario corporativo que puede consultar cualquier taller, realizar correcciones manuales y consultar las imágenes almacenadas. Puede acceder sin taller asignado y seleccionar cualquier taller. |
| Asesor | Usuario corporativo que solo puede consultar los datos de su taller asociado, sin acceso a imágenes ni permiso para correcciones manuales. |
| Administración externa del directorio | Mantiene las cuentas corporativas y su ciclo de vida fuera de la aplicación. |
| Aplicación cliente GarajeMetría | Consume la API para iniciar y renovar sesiones y acceder a consultas y operaciones autorizadas. |
| Dispositivo o sistema LPR | Envía lecturas de matrículas identificando el dispositivo, el taller y el evento conforme al contrato acordado. |
| Active Directory local | Mantiene las identidades corporativas y permite al backend validar el usuario y la contraseña recibidos en el login. |
| Mantenimiento externo de datos | Realiza fuera de la aplicación la limpieza de imágenes y eventos antiguos cuando corresponda. |

Los roles iniciales son **Administrador** y **Asesor**. El taller asociado por
defecto no es obligatorio para el Administrador. El Administrador puede cambiar el taller de
consulta a cualquier taller; el Asesor solo puede ver su taller. Cambiar el taller de consulta no
implica modificar la asociación por defecto del usuario.

El rol procede de la pertenencia directa o anidada a grupos de AD:
`web_GarajeMetria_Admin` concede Administrador y `web_GarajeMetria_Advisor`
concede Asesor. Si pertenece a ambos, prevalece Administrador. No se asignan
roles manualmente en GarajeMetría. La pertenencia se consulta en login y cada
renovación; al detectar cambio de rol efectivo o pérdida de acceso, se cierra
la sesión y se exige nuevo login. No se consulta AD en cada petición de negocio.
El taller predeterminado sigue asignándose localmente, solo por Administradores;
su cambio efectivo cierra inmediatamente la sesión e invalida sus tokens.
Cambiar el taller de consulta del Administrador no altera esa asignación ni
cierra su sesión. Los detalles están en
[RF-002 — Roles y acceso a talleres](requirements/RF-002-roles-and-workshop-access.md).
RF-002 define el mantenimiento del taller con motivo obligatorio, auditoría y
control de concurrencia. Las identidades se crean automáticamente tras validar
credenciales y grupos; el Asesor queda sin acceso hasta su primera asignación.
Después, su taller solo puede sustituirse por otro, sin retirarlo. Los permisos
de otras operaciones se detallan en sus respectivos requerimientos. El rol
Administrador no implica permisos adicionales a los expresamente definidos.

Se rechaza el inicio de sesión de usuarios sin pertenencia autorizante en AD. El Administrador
puede acceder con o sin taller asignado. El Asesor puede acceder si tiene un
taller asignado y solo puede consultar ese taller; si no tiene taller asignado,
se rechaza el acceso.

### 3.1. Identidad y sesión

- Los usuarios no se registran en GarajeMetría: deben existir previamente en el
  directorio activo.
- La integración inicial utiliza un único dominio de AD y vincula los usuarios
  mediante `objectGUID`. Una identidad de servicio de lectura permite consultar
  su estado durante la renovación sin conservar sus contraseñas.
- GarajeMetría controla el flujo de login y la emisión de sus tokens de sesión
  y refrescamiento, ambos JWT.
- Solo se permite una sesión activa por usuario. Un nuevo login satisfactorio
  cierra la sesión anterior e impide seguir accediendo o renovando con ella.
- La sesión tiene una duración máxima configurable en archivo, contada desde
  el login satisfactorio. Las renovaciones no amplían ese plazo. Al alcanzarlo,
  ningún token de esa sesión permite acceder o renovar y se exige un nuevo login.
- El cierre de sesión explícito invalida inmediatamente tanto el token de
  acceso como el de refrescamiento, aunque no hayan expirado.
- Cada renovación satisfactoria emite nuevos tokens de acceso y refrescamiento
  e invalida el token de refrescamiento utilizado. Cada token de refrescamiento
  es de un solo uso.
- Cada token de refrescamiento tiene una duración configurable contada desde
  su emisión. Su expiración se limita al máximo de la sesión si este se alcanza
  antes, sin ampliar ese límite con las renovaciones.
- Una renovación normal permite que el token de acceso anterior siga siendo
  válido hasta su expiración natural mientras la sesión siga activa. Su duración
  será corta y configurable en archivo; de 5 a 15 minutos es un ejemplo
  orientativo. Los cierres o revocaciones invalidan todos los tokens de acceso
  vigentes de la sesión, incluidos los anteriores a una renovación.
- Reutilizar un refresh auténtico, consumido y aún vigente cierra la sesión e
  invalida los tokens de la familia. Si ya expiró al decidir sobre el reuso,
  se rechaza con 401 sin revocar, incluso con un sucesor activo. El rechazo no
  cambia cookies ni amplía la sesión; se aplica RF-001 / RN-010.
- En cada renovación se comprueba el estado del usuario en Active Directory
  local. Si está deshabilitado, bloqueado o con contraseña expirada, se rechaza
  la renovación, se cierra su sesión y se invalidan inmediatamente todos sus tokens vigentes.
- Si Active Directory no está disponible al renovar, se rechaza temporalmente
  la renovación sin cerrar la sesión ni consumir el token de refrescamiento.
  Puede reintentarse mientras el token siga vigente y la sesión esté activa;
  el fallo no amplía la duración de los tokens.
- El endpoint de login recibe usuario y contraseña. El backend valida esas
  credenciales contra Active Directory local y, si son válidas y el acceso
  está autorizado, emite los tokens JWT.
- Se admiten los formatos `usuario` y `DOMINIO\usuario`. Para `usuario`, se
  utiliza el dominio predeterminado establecido en el archivo de configuración;
  para `DOMINIO\usuario`, se utiliza el dominio indicado.
- Los dominios permitidos se limitan mediante una lista en el archivo de
  configuración. El dominio resuelto, incluido el predeterminado, debe estar
  en esa lista; los dominios no permitidos se rechazan antes de validar las
  credenciales contra el directorio.
- La conexión con Active Directory local utiliza LDAPS (LDAP sobre SSL/TLS),
  con validación de certificados y configuración por entorno. Las contraseñas
  recibidas se utilizan para validar el login y no se almacenan en la aplicación.
- La duración de los tokens y los plazos aplicables a su renovación, rotación
  y revocación, al cierre de sesión y a la comprobación de usuarios deshabilitados
  en el directorio durante una sesión existente deben ser configurables mediante
  un archivo de configuración, sin valores fijos en el código. Las políticas de
  comportamiento de estos procesos están definidas en RF-001 y DESIGN-001.
- La autenticación de dispositivos LPR se definirá por separado de la sesión
  de los usuarios corporativos.
- Se expone una única API para aplicaciones consumidoras de cualquier tecnología
  o plataforma, sin un cliente de escritorio específico ni contratos por tipo
  de aplicación. El access token se devuelve en JSON y se envía mediante
  Authorization Bearer. Todos los consumidores reciben el refresh token
  exclusivamente en una cookie HttpOnly y Secure y la reenvían en refresh y
  logout. El refresh token no se devuelve en JSON. La cookie utiliza
  `SameSite=Strict`.
- Todos los consumidores obtienen protección antiforgery mediante
  `GET /api/auth/antiforgery` y envían la cookie correspondiente y la cabecera
  `X-CSRF-Token` en login, refresh y logout. La ausencia de Origin no exime
  de esa validación; los rechazos devuelven 403 sin modificar la sesión.
- El estado de sesiones y familias de tokens se persiste en SQL Server. El
  login y la renovación tienen límites configurables por IP y usuario, con
  ventana deslizante exacta de 60 segundos y estado compartido en SQL Server
  entre los varios procesos o servidores de la API.
- Los rechazos de autenticación utilizan un mensaje genérico; con credenciales
  válidas, los rechazos por falta de rol o taller utilizan mensajes descriptivos.
  La indisponibilidad del directorio tiene un mensaje temporal específico.
  Los textos exactos están definidos en DESIGN-001 / 4.3.
- La configuración obligatoria ausente o inválida impide el arranque de la API;
  el diagnóstico se registra sin exponer secretos.
- Los datos de Active Directory, las conexiones SQL Server y demás valores
  sensibles se cargan desde un archivo privado de configuración externo que no
  se comparte ni se versiona. En requerimientos solo se documentan las tecnologías
  y los grupos de configuración; no sus valores privados.
- Login y renovación devuelven 200; logout devuelve 204 sin cuerpo. El login
  devuelve datos del usuario, rol y taller; la renovación solo devuelve tokens
  y sus metadatos de vigencia. El logout admite un refresh token vigente aunque
  el access token haya expirado. Los contratos y mensajes se detallan en
  [RF-001 — Autenticación](requirements/RF-001-authentication.md).

RF-001 y DESIGN-001 están definidos para el primer tramo independiente de
autenticación. Configurar, implementar y verificar son tareas de TASKS-001.
La administración de asignaciones de RF-002 y la sincronización de RF-009 se
entregan después y no son prerrequisitos para completar RF-001.

## 4. Arquitectura de alto nivel

Se contemplan desarrollo y producción, con alojamiento inicial en Windows/IIS.
En producción, la web utiliza `https://garajemetria.com` y la API
`https://api.garajemetria.com`. Se prevén aproximadamente 200 usuarios.

El backend utiliza C# 14 y .NET 10 con ASP.NET Core Web API. La arquitectura
sigue Clean Architecture, con dependencias hacia el dominio.

```text
Aplicación cliente / Sistema LPR
                |
                v
               API
                |
                v
           Application ---> Domain
                ^              ^
                |              |
           Infrastructure -----+
                |
                +-- SQL Server, mediante EF Core
                +-- Adaptador de Active Directory local
                +-- Imágenes opcionales en disco (ruta configurable)
```

El esquema representa dependencias entre capas y los sistemas externos a los
que acceden los adaptadores. La API registra Infrastructure en el arranque como
raíz de composición; sus endpoints utilizan contratos de Application.

| Capa | Responsabilidad |
| --- | --- |
| Domain | Entidades, objetos de valor e invariantes de negocio, sin dependencias externas ni acceso a datos. |
| Application | Casos de uso, DTO, validación y contratos de persistencia y servicios externos. |
| Infrastructure | Persistencia con EF Core, repositorios, unidad de trabajo y adaptadores de sistemas externos. |
| Api | Contratos HTTP, endpoints, autenticación en el transporte, middleware y composición de dependencias. |

Los casos de uso emplean CQRS con MediatR por defecto. Las operaciones CRUD
simples pueden utilizar servicios de Application directamente, manteniendo
validación, autorización y límites transaccionales. FluentValidation se utiliza
para validar las entradas de Application antes de ejecutar los casos de uso.

SQL Server es el motor inicial. Domain, Application y los contratos REST deben
permanecer independientes del proveedor de base de datos. La incorporación de
otros motores requerirá adaptar y verificar Infrastructure; no se declara
soporte simultáneo para varios proveedores.

El repositorio implementa el tramo RF-001 con persistencia SQL Server y pruebas
automatizadas, además de las comprobaciones de arquitectura. La verificación
externa de AD y la restauración física aislada siguen pendientes. Los casos
administrativos y LPR aún no están implementados. Consultar el
[runbook de autenticación](runbooks/authentication.md).

## 5. Reglas comunes

### 5.1. Vehículos, eventos y estancias

- Una lectura LPR es una observación. No determina por sí sola una entrada,
  salida o estancia: debe interpretarse según las reglas de negocio.
- Cuando la cámara aporte la dirección del movimiento, se utilizará esa
  información. Cuando no la aporte, el backend deberá determinar si se trata de
  una entrada o una salida usando la primera lectura como entrada y la última
  como salida, agrupando las lecturas por vehículo, taller y día. La salida
  corresponde a la última lectura de ese vehículo en ese taller durante ese día.
- En esta primera versión, cuando ese grupo contenga una sola lectura, se
  asignará una duración de estancia de X minutos. X se establece en un archivo
  de configuración, sin un valor fijo en el código. Esta duración es una
  estimación; no representa una salida observada por la cámara. La entrada
  corresponde al instante de captura de esa lectura y la salida estimada se
  calcula sumando X minutos a dicho instante.
- El día de agrupación se determina a partir de la fecha de captura de cada
  lectura en la zona horaria del taller, no de su fecha de recepción. Los
  instantes se conservan en UTC.
- Quedan por definir el tratamiento de eventos tardíos y del día todavía en
  curso. No se infieren visitas individuales
  adicionales a partir de las lecturas intermedias sin una regla acordada.
- Antes de implementar la ingesta se definirán la identidad de eventos y
  dispositivos, los reintentos y el tratamiento de duplicados y eventos fuera
  de orden.
- La entrega repetida o concurrente de un mismo evento no debe producir
  efectos de negocio duplicados. La persistencia debe respaldar esta garantía
  mediante un mecanismo atómico.
- Se distinguirá la fecha de captura de la fecha de recepción. Los instantes
  se representarán con `DateTimeOffset` en UTC; la zona horaria del taller se
  aplicará cuando corresponda.
- Las reglas de normalización de matrículas, asociación de lecturas y cálculo
  de estancias deben definirse antes de su implementación.
- Solo el Administrador puede realizar correcciones manuales, que deben conservar su trazabilidad:
  quién realizó el cambio, cuándo y cuáles fueron los datos anteriores y
  posteriores. Los campos corregibles y el efecto sobre las estancias están
  pendientes de definición.
- La imagen de la matrícula es opcional. Cuando se reciba, Infrastructure la
  almacenará en disco, fuera de la base de datos, en la ruta establecida en el
  archivo de configuración.
- Solo el Administrador puede consultar las imágenes almacenadas a través de
  la API. El Asesor no tiene acceso a ellas. Esta restricción también debe
  aplicarse al acceso a los archivos; la ruta en disco no constituye un acceso
  público a las imágenes.

### 5.2. Acceso y protección de datos

- Los endpoints estarán protegidos por defecto; cualquier acceso anónimo
  deberá justificarse expresamente.
- Cada lectura y escritura debe comprobar los permisos y el acceso al taller,
  también para el tráfico de dispositivos. Un identificador de taller recibido
  del cliente no constituye una autorización.
- Las matrículas y los eventos asociados se consideran datos sensibles. No se
  registrarán matrículas completas, cargas LPR completas, contraseñas ni tokens
  en los logs.
- Los secretos se mantendrán fuera del código fuente. Los despliegues utilizarán
  HTTPS, orígenes CORS explícitos y límites de peticiones apropiados.
- Los eventos y sus imágenes se conservarán indefinidamente, sin caducidad ni
  eliminación automática por antigüedad dentro de la aplicación.
- La limpieza de imágenes y eventos antiguos corresponde al mantenimiento
  externo. No se incluyen pantallas, endpoints ni tareas programadas para esa
  limpieza en la primera versión. El procedimiento externo deberá preservar
  la coherencia entre eventos, imágenes e historial de estancias.
- Se utilizarán datos sintéticos en pruebas y ejemplos.
- Las sesiones, huellas y auditoría siguen los plazos y el mecanismo de limpieza
  definidos en [DESIGN-001 / 7.2](design/DESIGN-001-authentication.md#72-retención-y-limpieza-diaria).
  Esa es la fuente de los valores configurables. La política no elimina perfiles,
  talleres, eventos LPR ni imágenes ni sustituye la de copias de seguridad.

### 5.3. Contratos y calidad

- La API utilizará DTO explícitos, semántica HTTP coherente, documentación
  OpenAPI y errores uniformes mediante ProblemDetails, sin revelar detalles
  internos.
- Las consultas tendrán paginación acotada, orden determinista y filtros
  permitidos explícitamente.
- Las invariantes pertenecen a Domain y la orquestación a Application. El
  acceso a datos permanece en Infrastructure, detrás de contratos de
  Application.
- La validación se ejecutará antes de la lógica del caso de uso y de la
  persistencia, tanto en flujos mediados como en servicios CRUD directos.
- Las pruebas cubrirán las reglas de dominio, las decisiones de aplicación,
  los contratos HTTP y la arquitectura. Las pruebas de persistencia utilizarán
  SQL Server aislado, sin EF InMemory ni sustitutos SQLite.

## 6. Decisiones pendientes para cerrar el alcance

El catálogo de talleres procede de una base o vista corporativa de solo lectura
que devuelve talleres activos. GarajeMetría conserva una copia local con id
interno, código corporativo permanente y no reutilizable, nombre y estado.
Se actualiza en un máximo de 15 minutos en operación normal; ante una caída se
permite la última copia y se registra la incidencia, incluso superado ese plazo.
Una ausencia en una lectura completa desactiva el taller y cierra sesiones de
Asesor, conservando asignación e historial. La reactivación permite nuevo login
con la misma asignación, sin recuperar sesiones cerradas. El mantenimiento
corresponde al sistema externo. Detalles y contrato de origen pendiente en
[RF-009 — Catálogo corporativo de talleres](requirements/RF-009-workshop-catalog.md).

1. **Permisos de otras operaciones administrativas:** concretar los detalles
   propios de cada funcionalidad en su requerimiento. Roles, primer acceso y
   mantenimiento del taller ya están definidos en RF-002; solo los
   Administradores pueden mantener las asignaciones.
2. **Origen y significado de eventos:** dispositivos o proveedor LPR, datos
   disponibles, configuración de la zona horaria de cada taller y tratamiento
   de direcciones inconsistentes. El agrupamiento por vehículo, taller y día
   utiliza la fecha de captura en la zona horaria del taller; primera lectura
   como entrada y última del día como salida.
3. **Estancias e incidencias:** tratamiento de lecturas repetidas, movimientos
   incompletos y eventos tardíos; campos corregibles y efecto de las correcciones
   sobre las estancias y los informes. Para una lectura única está acordada una
   duración configurable de X minutos y una salida estimada en el instante de
   captura más X minutos; falta concretar el tratamiento del día todavía en curso.
4. **Primera versión:** consultas, operaciones administrativas e indicadores
   imprescindibles. Las exportaciones, las notificaciones y las integraciones
   adicionales a AD, LPR y RF-009 quedan fuera del alcance.
5. **Operación:** volumen esperado de eventos e imágenes, necesidades de
   actualización de las consultas y procedimiento de mantenimiento externo.

Las respuestas a estas cuestiones deben incorporarse a este documento o a los
documentos específicos de requisitos antes de dar por cerradas las decisiones
correspondientes.
