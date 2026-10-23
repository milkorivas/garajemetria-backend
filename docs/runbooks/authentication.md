# RF-001 — Configuración, verificación y operación

## 1. Alcance y estado

La implementación incluye los cuatro endpoints de autenticación, sus casos de
uso, JWT RS256, adaptador LDAPS asíncrono, persistencia EF Core/SQL Server,
limitación compartida, retención y documentación OpenAPI/Swagger UI.
Los contratos normativos permanecen en
[DESIGN-001](../design/DESIGN-001-authentication.md).
No incluye administración de asignaciones ni sincronización corporativa.

Las pruebas HTTP sustituyen únicamente el directorio por un doble sintético y
usan SQL Server real para persistencia. Las pruebas de JWT utilizan firma RSA
real. La integración completa de LDAPS requiere el dominio de pruebas externo;
una prueba omitida no acredita esa integración ni el cierre de RF-001.

## 2. Configuración y certificados

Usar [authentication.example.json](../configuration/authentication.example.json)
como plantilla. Sus placeholders y audiencias de ejemplo deben sustituirse por
los valores del entorno; no contiene credenciales operativas. Las opciones de
`Authentication` se suministran explícitamente, sin defaults silenciosos en código.
Los valores iniciales aprobados siguen definidos en DESIGN-001.
`CleanupBatchSize=100` es el tamaño inicial operativo de lote; admite 1 a 10.000.

Guardar el JSON privado fuera del contenido de la aplicación y del repositorio.
`GARAJEMETRIA_PRIVATE_CONFIG` contiene su ruta absoluta. No enviar sus valores
por chat, logs, argumentos de consola ni documentación versionada.
No se recarga dinámicamente: cambiar configuración requiere reiniciar y coordinar
las instancias cuando corresponda. La API valida opciones y material de firma,
y verifica la política compartida en SQL antes de admitir operaciones que
dependan de ella. Esta verificación ya no bloquea el arranque del host: se
ejecuta en segundo plano y se repite cada 30 segundos. SQL ausente, inválido o
inaccesible mantiene esas operaciones cerradas con `503`; no se omiten cuotas
ni se crean sesiones sin persistencia. Si se recupera la conexión configurada,
la siguiente verificación correcta permite reanudar la operación sin reiniciar.
Los cambios del archivo privado siguen requiriendo reinicio.

Para diagnosticar, consultar `GET /api/health` por HTTPS sin sesión ni bootstrap.
Devuelve estados independientes de servicio, SQL y AD: `OK`, `ERROR` o
`NOT_CONFIGURED`. Solo todos en `OK` producen HTTP `200`; el resto produce `503`
con el mismo JSON. SQL sin cadena produce `NOT_CONFIGURED`; cadena inválida o
conexión/consulta fallida produce `ERROR`. AD comprueba LDAPS y bind de servicio,
sin autenticar usuarios. Cada comprobación tiene un máximo de cinco segundos.
El diagnóstico no revela configuración y admite 60 peticiones/minuto por
instancia, después devuelve `429` ProblemDetails. La configuración no SQL y
los certificados siguen siendo obligatorios. Véase el ejemplo en README.

El certificado JWT es RSA de al menos 2.048 bits y contiene clave privada.
`SigningKeyId` identifica la clave actual. `VerificationKeys` admite certificados
públicos anteriores con `KeyId` y `CertificatePath` para verificar tokens durante
la rotación; no reutilizar identificadores ni retirar una clave mientras deba
validar tokens aún vigentes. Los certificados y sus claves no se versionan.

`Http:DataProtectionKeyPath` apunta a un directorio compartido y externo al
contenido de todas las instancias. Las claves se cifran con el certificado
privado indicado en `DataProtectionCertificatePath` y su contraseña externa.
Todas las instancias deben poder descifrarlas y mantener el mismo discriminador
`GarajeMetria.Api`. Conservar acceso a certificados anteriores al rotarlos.
Restringir permisos NTFS a las identidades autorizadas de IIS y operación.

Configurar el dominio único, servidores LDAPS, base de búsqueda, identidad de
lectura y DNs de los dos grupos autorizantes. El adaptador comprueba nombres de
grupo, pertenencia transitiva, identidad estable y estado sin conservar la
contraseña del usuario. Verificar cadena, nombre y revocación de certificados
LDAPS; no deshabilitar estas comprobaciones. Cada operación tiene timeout y
cancelación. No usar cuentas ni datos de producción para verificarlo.

`Http:AllowedOrigins` define todos los orígenes autorizados, tanto en desarrollo
como en producción. La lista debe contener orígenes HTTPS explícitos; no se
exigen dominios concretos ni se permiten orígenes adicionales por defecto.
Un fallo al validar `Http` identifica el nombre del campo inválido, sin mostrar
su valor. Las rutas de Data Protection y las IPs de proxies también se validan;
un error en ellas no implica que el origen configurado sea incorrecto.
En desarrollo añadir explícitamente los orígenes HTTPS locales autorizados.
`TrustedProxies` contiene solo IPs de proxies conocidos; se acepta un salto
reenviado. Sin proxies configurados, las cabeceras reenviadas no se procesan.
Revisar la topología antes de ampliar esa configuración. Sin IP disponible se
utiliza la partición común `unknown-ip`.

## 3. Migraciones y arranque

La API no aplica migraciones ni crea bases de datos al arrancar. Generar y
revisar el script de la migración `InitialAuthentication` antes de aplicarlo al
entorno con los permisos de despliegue correspondientes. La factoría de diseño
usa `GARAJEMETRIA_MIGRATIONS_CONNECTION`, suministrada de forma privada.

Con `dotnet-ef` 10.0.12 instalado, desde la raíz:

```sh
dotnet ef migrations script --idempotent --project src/GarajeMetria.Infrastructure --startup-project src/GarajeMetria.Infrastructure --configuration Release
```

La migración incorpora perfiles, auditoría de aprovisionamiento, sesiones,
huellas, cuotas y calendario de limpieza. `WorkshopAccess` y
`WorkshopCatalogState` contienen solo las referencias locales necesarias para
comprobar acceso; no importan ni inventan talleres. Sin asignación, un nuevo
Asesor conserva su perfil y recibe 403. El Administrador puede entrar sin taller.
Los productores del catálogo y de asignaciones se entregan después.

Tras preparar la base, certificados, archivo externo y variable de ruta:

```sh
dotnet run --project src/GarajeMetria.Api --launch-profile https
```

Mantener HTTPS y sincronización de relojes. Verificar en IIS/proxies los límites
de cuerpo y el contrato de errores: un rechazo del alojamiento anterior a la
API puede impedir que esta produzca ProblemDetails.

## 4. OpenAPI y Swagger UI

La generación utiliza `Microsoft.AspNetCore.OpenApi`; Swagger UI se integra con
`Swashbuckle.AspNetCore.SwaggerUI`. Con `Http:EnableApiDocumentation=true` se
publican `/openapi/v1.json` y `/swagger/index.html`. La publicación se habilita
explícitamente por entorno; la plantilla la deja desactivada. No se incluyen
credenciales, JWT reales ni configuración privada en el documento.

Para probar autenticación desde Swagger UI, ejecutar primero el bootstrap,
copiar su `requestToken` en `X-CSRF-Token` y conservar las cookies del mismo
origen HTTPS. Login/refresh/logout requieren esta protección incluso sin Origin.
El Bearer no autentica estas cuatro rutas ni modifica el contexto antiforgery.
Swagger UI no elimina restricciones de cookies u origen ni concede permisos.

## 5. Ejecución de pruebas

Desde la raíz, ejecutar los cuatro checks definidos en
[README.md](../../README.md#verificación). Las pruebas de Domain y Application
no utilizan red ni base de datos. Las de persistencia utilizan una base temporal
`GarajeMetria_AuthTests_<UUID>`, aplican migraciones y la eliminan al terminar;
validan ese prefijo y UUID antes de eliminarla.

En Windows, la instancia local predeterminada utiliza conexión de memoria
compartida (`lpc:localhost`) con identidad integrada. No hay tráfico SQL de red
en esa configuración de test. Para otra instancia de pruebas, suministrar
`GARAJEMETRIA_TEST_SQL` externamente. La identidad debe poder crear y eliminar
únicamente las bases temporales de pruebas según la política del entorno.
No apuntar esta variable a producción. En Linux, las pruebas SQL se omiten si
no hay una instancia explícita; la CI suministra su propio servicio aislado.

Las pruebas verifican HTTP, JSON estricto, cookies, antiforgery, firma/claims y
fronteras temporales, perfil único, sesión única, consumo concurrente, replay,
cuotas compartidas, rollback y commit con confirmación perdida, y limpieza sin
cascadas. La prueba TLS local comprueba rechazo de certificados no confiables;
no sustituye las pruebas del dominio AD real.

Para ejecutar LDAPS real, `GARAJEMETRIA_AD_TEST_CONFIG` apunta a otro JSON privado
con `ConfirmedIsolatedTestDirectory=true`, `Directory` con la misma estructura
del adaptador, y estos datos sintéticos:

- `Accounts`: username, password, objectId y expectedRole, con nombres de
  propiedades `Username`, `Password`, `ObjectId` y `ExpectedRole`. Incluir
  Administrador, Asesor, ambos grupos, sin grupo y pertenencia anidada.
- `RejectedObjectIds`: GUIDs de cuentas bloqueadas, deshabilitadas, con contraseña
  expirada o cambio obligatorio, preparados por el responsable del dominio.
- `AbsentObjectId`: GUID confirmado inexistente.

La prueba no cambia AD ni genera intentos deliberados de contraseña incorrecta.
Además, verificar permisos denegados, atributos ausentes, cambio de nombre,
eliminación/recreación y cambios de grupos con el responsable de integración.
Sin esta configuración el test se informa como omitido y el cierre externo
continúa pendiente.

## 6. Cuotas, limpieza y recuperación

Todas las instancias de un entorno usan el mismo `RateLimitNamespace` y los
mismos límites. Una discrepancia con la huella persistida impide habilitar la
instancia. No borrar cuotas para corregir una discrepancia. Cambiar la política
exige despliegue coordinado y conservar el histórico necesario; si se amplía la
ventana sin histórico suficiente, detener admisiones durante la nueva ventana.
No hay reintento automático de escrituras de autenticación ni de permisos.

El supervisor comprueba cada minuto el calendario SQL. El intervalo de limpieza
y los umbrales se obtienen de las opciones; cada lote mantiene un lease y bloqueo
transaccional compartidos, con corte horario de SQL. La pérdida del lease impide
que otro lote continúe con un propietario antiguo. No se eliminan perfiles,
asignaciones ni talleres por cascada. La auditoría administrativa se integra en
RF-002 con el mismo supervisor.

Antes de restaurar una copia, bloquear tráfico en IIS/balanceador y detener
todas las instancias, incluidos los procesos de limpieza. Restaurar únicamente
en el destino autorizado y verificar integridad, versión de esquema y relojes.
Reconciliar las revocaciones posteriores al punto de la copia. Si no se pueden
reconstruir, invalidar las sesiones restauradas antes de permitir nuevos logins;
no habilitar tráfico basándose en que una copia muestre sesiones activas.
Reaplicar limpieza de registros elegibles y comprobar que tokens de sesiones
cerradas o vencidas se rechazan antes de reabrir tráfico. Ejecutar este
procedimiento sobre una restauración aislada y registrar la evidencia.

La retención de backups se define por separado. Los tests de rollback y limpieza
no acreditan por sí solos una restauración física ni revocaciones posteriores al
backup. La comprobación de restauración aislada sigue siendo condición de cierre
operativo. Si no se puede confirmar un commit, no asumir rollback: login de nuevo
al recuperar el servicio; no reenviar automáticamente el mismo refresh.
