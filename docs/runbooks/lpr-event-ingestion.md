# GarajeMetría — Operación y aceptación sintética de RF-003

Este procedimiento utiliza un taller y un dispositivo de pruebas. Crea dos
eventos y conserva su historial; al terminar desactiva el dispositivo. No usar
un dispositivo en servicio ni matrículas, credenciales o datos de producción.
Las llamadas verifican el contrato HTTP, no una cámara ni un proveedor LPR real.

## 1. Preparar API, SQL y certificados

1. Seguir [preparación LPR](../configuration/lpr-test-preparation.md), usando
   [lpr.example.json](../configuration/lpr.example.json) como plantilla sin secretos.
   La configuración privada y los PFX deben permanecer fuera del repositorio,
   con permisos de lectura limitados a la identidad que ejecuta la API.
2. Configurar `DeviceOAuth.Issuer` como URI HTTPS estable del emisor propio y
   `Audience` como identificador de esta API LPR. Deben distinguirse de los de
   autenticación de usuarios; no son valores que el dispositivo pueda elegir.
   El endpoint de emisión de esta API es `POST /connect/token`.
3. Proporcionar certificados privados distintos para firma y cifrado LPR,
   vigentes y aptos para sus usos, y sus contraseñas mediante configuración
   privada. No reutilizar las claves de usuarios ni el certificado HTTPS.
   Conservar claves necesarias para validar tokens durante su vigencia y
   coordinar su sustitución entre instancias. No copiar contraseñas a esta guía.
4. Revisar las migraciones antes de aplicarlas al entorno de pruebas. Debe
   figurar `20261113115955_LprPersistence` en el historial de migraciones,
   además de las anteriores. Usar el procedimiento de migración del proyecto
   con conexión privada al destino correcto; no ejecutar scripts sobre producción
   ni habilitar migración automática al arrancar. Los tests crean, migran y
   eliminan exclusivamente su propia base temporal.
5. Arrancar por HTTPS con la cadena del certificado confiable para el cliente.
   Mantener habilitada la verificación TLS en Postman. El certificado HTTPS
   protege el transporte; los PFX de DeviceOAuth protegen los tokens.
6. Confirmar diagnóstico SQL correcto, catálogo publicado, taller de pruebas
   activo y un usuario Administrador habilitado. El diagnóstico comprueba los
   almacenes LPR/OAuth además de conectar a SQL. Ante fallo, revisar el log seguro
   correlacionado y la configuración privada, sin publicar conexiones ni SQL.
7. Sincronizar relojes y revisar límites compartidos entre instancias. El ejemplo
   usa tokens de 15 minutos, 10 emisiones por cliente y 120 por IP, y tolerancias
   de captura de 30 minutos hacia el pasado y el futuro. `RateLimitNamespace`
   debe ser estable y común a las instancias del mismo servicio/entorno; no es
   una credencial ni debe cambiarse para eludir cuotas.

## 2. Importar la colección y preparar Vault

Importar [RF-003.postman_collection.json](../http/RF-003.postman_collection.json).
No contiene credenciales, tokens ni respuestas guardadas. No importar variables
de otro entorno que oculten las de esta colección.

Configurar estas variables de colección:

| Variable | Valor |
| --- | --- |
| `baseUrl` | URL HTTPS real de la API, sin barra final; por ejemplo `https://localhost:7040` si se usa ese perfil. |
| `adminUsername` | Usuario admitido por RF-001, con rol Administrador. |
| `siteId` | ID exacto del taller activo del catálogo; no su nombre ni código visible. |
| `deviceName` | Nombre reconocible y distinto para esta ejecución, por ejemplo `RF003 acceptance-20261116-run01`. |

Dejar vacías las demás variables: las llamadas guardan identificadores, ETag,
UUID de evento y fechas automáticamente. `site_id` debe coincidir exactamente
con el taller asociado al dispositivo; `device_id` se obtiene del alta.

En Postman Vault crear únicamente `garajemetria-admin-password` con la contraseña
del usuario de pruebas. Habilitar **Support in scripts** y autorizar esta colección
para usar Vault. Los scripts escriben también `garajemetria-csrf-token`,
`garajemetria-admin-token`, `garajemetria-device-secret` y
`garajemetria-device-token`. No introducir estos valores en variables de colección,
de entorno ni en ejemplos guardados. Los alias locales de cada petición se
resuelven desde Vault; el login construye el JSON con escape correcto.

Vault requiere scripts asíncronos compatibles. Usar envío manual ordenado o
Collection Runner manual; no Newman, Postman CLI, monitores ni ejecuciones
programadas, que no admiten estos métodos de Vault según la
[documentación de Postman](https://learning.postman.com/v11/docs/tests-and-scripts/write-scripts/postman-sandbox-reference/pm-vault/).
No abrir ni compartir trazas de peticiones/respuestas con credenciales. Aunque
los scripts no imprimen secretos, las vistas HTTP, el historial local y las
cookies de sesión pueden contenerlos. No exportar resultados o guardar respuestas
con datos de autenticación. Mantener la cookie jar habilitada para RF-001 y no
añadir manualmente `Origin` o cabeceras de navegación de otro sitio.

## 3. Ejecutar las 22 peticiones en orden

Ejecutar una iteración, sin paralelismo y sin pausas que agoten la sesión del
Administrador o la ventana de captura. Detenerse ante un estado inesperado y
diagnosticarlo antes de repetir escrituras. El Runner se detiene ante un estado
HTTP distinto del esperado; revisar también las aserciones funcionales.

| Pasos de la colección | Resultado esperado |
| --- | --- |
| 00–01: antiforgery y login HTTP | 200 y 200. Cookie antiforgery más `X-CSRF-Token`; Bearer de Administrador en Vault. |
| 02: alta | 201 con `device`, credenciales de una sola entrega, `Location` y ETag fuerte. |
| 03: emisión de dispositivo | 200 OAuth con `access_token`, `token_type: Bearer`, `scope: lpr.events.write`; sin refresh token. |
| 04: primera lectura | 201, `duplicate: false`, `plate_status: valid`. |
| 05: reenvío idéntico | 200, `duplicate: true` y mismo `received_at`. |
| 06: mismo UUID con matrícula distinta | 409 `event_conflict`. No sustituye el original. |
| 07: lectura de formato de matrícula inválido y confianza cero | 201, `plate_status: invalid`; se conserva como lectura inválida. |
| 08–09: GET y rotación con ETag | 200 y 200; GET sin secreto y rotación con secreto nuevo en Vault. |
| 10–12: token anterior, nueva emisión y reenvío | 401, 200 y 200. La rotación invalida inmediatamente el token anterior. |
| 13–15: revocar, usar token y emitir con secreto revocado | 204, 401 ProblemDetails y 401 OAuth `invalid_client`. |
| 16–19: desactivar, reactivar, emitir y reenviar | 204, 200, 200 y 200. Reactivación genera credenciales nuevas y conserva identidad y recibo histórico. |
| 20–21: desactivación final y uso del último token | 204 y 401; registro inactivo, historial conservado. |

En `/connect/token` la colección usa **Basic Auth**: usuario `clientId` y contraseña
del dispositivo desde Vault. El cuerpo **x-www-form-urlencoded** contiene
`grant_type=client_credentials` y `scope=lpr.events.write`. No enviar JSON,
Bearer de Administrador ni la contraseña de AD en este endpoint. Se admite como
alternativa autenticación del cliente mediante `client_id` y `client_secret`
en el formulario; usar un solo método por petición, siempre sobre HTTPS.

La ingesta usa Bearer de dispositivo y `Content-Type: application/json`:

```json
{
  "event_id": "10000000-0000-4000-8000-000000000001",
  "capture_timestamp": "2026-11-16T09:00:00Z",
  "device_id": "20000000-0000-4000-8000-000000000002",
  "site_id": "synthetic-workshop",
  "plate": "0000 BBB",
  "confidence": 0.97,
  "direction": "entry"
}
```

Este ejemplo es sintético: sustituir UUID, taller y fecha por los del dispositivo
y captura reales de pruebas. La colección lo hace automáticamente. No copiar la
fecha fija para una primera ingesta. `direction` admite `entry`, `exit` u omisión/null;
`confidence` admite 0 a 1 con precisión exacta de hasta ocho decimales. El cuerpo
máximo es 8 KiB, sin imágenes. La clasificación española normaliza la matrícula
para diagnosticarla y conserva el original. No crea entradas, salidas ni estancias.

## 4. Custodiar, rotar y recuperar acceso

Guardar el secreto en el almacén privado del emisor al provisionarlo y conservar
el vínculo dispositivo/taller fuera de los logs. La API no permite volver a leerlo.
GET/listados no devuelven secretos; los registros y eventos no se borran físicamente.
La asociación a taller es inmutable: para trasladar el emisor desactivar el registro
anterior y crear otro. No reescribir los eventos históricos con la identidad nueva.

Las mutaciones administrativas requieren Bearer de Administrador y `If-Match`
con el ETag fuerte y entrecomillado obtenido por GET o la mutación anterior.
Ante 428 falta la precondición; ante 400 revisar su sintaxis; ante 412 volver a
leer el recurso y reconsiderar la operación con el estado actual.

Una rotación invalida secreto y tokens anteriores inmediatamente. Una revocación
impide emitir o ingerir, aunque el registro siga activo. La desactivación elimina
las credenciales operativas; la reactivación emite otras. Si el taller se desactiva,
no se puede emitir ni ingerir; al reactivarlo hay que emitir un token nuevo,
incluso si se conserva el secreto válido del dispositivo.

Si se pierde la respuesta de una rotación o reactivación, no se puede recuperar
el secreto emitido. Hacer GET, obtener el ETag actual y rotar de nuevo; instalar
el nuevo secreto y solicitar token. Ante pérdida del acuse del alta, no repetir
el POST a ciegas: consultar el listado filtrado por taller, identificar de forma
inequívoca el registro creado y continuar por GET/rotación. El nombre no es único;
si hay ambigüedad, resolverla administrativamente antes de cambiar credenciales.

Al terminar, comprobar que el paso 20 dejó inactivo el registro y limpiar del Vault
las entradas generadas que ya no se necesiten, además del historial privado según
la política del entorno. Si se aborta antes, desactivar explícitamente el dispositivo
con GET/ETag y autorización vigente. Borrar variables o cerrar Postman no revoca
credenciales. No borrar eventos o auditoría para limpiar una prueba.

## 5. Recuperación tras desconexión y errores

El emisor externo debe conservar una cola duradera con UUID y fecha original de
captura. Reenviar el mismo evento sin cambiar matrícula original, confianza,
dirección ni identidad. Distinguir captura de `received_at`, generado por la API.
Una entrega nueva solo se acepta dentro de las tolerancias configuradas, inicialmente
30 minutos en ambas direcciones, inclusive. Un duplicado idéntico ya persistido
puede confirmarse fuera de esa ventana si el dispositivo sigue autorizado.
No rejuvenecer fechas ni cambiar UUID para eludir el rechazo de eventos antiguos.

La retención, protección y revisión de la cola del emisor corresponden al sistema
externo y necesitan su propia política. RF-003 no implementa esa cola ni elimina
automáticamente eventos LPR. La limpieza interna de auditoría y tokens tiene sus
propias ventanas; no implica eliminar lecturas ni habilitar credenciales revocadas.

| Respuesta | Actuación |
| --- | --- |
| OAuth `/connect/token`: `error`, `error_description` opcional | Corregir cliente, secreto, grant o scope; no interpretar el cuerpo como ProblemDetails. `invalid_client` exige revisar vigencia y método de autenticación. |
| API ProblemDetails: `status`, `code`, correlación | Usar el código y el identificador de correlación para diagnóstico; no esperar mensajes SQL o secretos. |
| 400 / 413 / 415 | Corregir forma, tamaño o tipo de contenido. No reintentar el mismo error de forma indefinida. |
| 401 / 403 | Comprobar tipo de token, expiración, credenciales, actividad del dispositivo y taller. Un token nuevo no resuelve una revocación. |
| 409 `event_conflict` | Revisar colisión o modificación del evento; conservar el original, no sobrescribirlo ni generar un UUID para ocultar el conflicto. |
| 422 | Revisar captura fuera de tolerancia y reloj. Conservar el caso para revisión externa; no modificar la fecha original. |
| 429 | Respetar `Retry-After`; no cambiar namespace, identidad o IP para eludir límites. |
| 500 / 503 o pérdida de conexión | El resultado puede ser incierto. En ingesta, reenviar exactamente el mismo evento autorizado permite reconciliarlo sin efectos duplicados. Para administración, leer estado/ETag antes de decidir la recuperación. |

## 6. Evidencia y límites de verificación

`LprRunbookTests.ExportedHttpTemplatesCompleteTheSyntheticLifecycleAgainstIsolatedSql`
carga las 22 plantillas de esta colección y las ejecuta por WebApplicationFactory,
con SQL Server real aislado, migraciones y directorio sintético. Comprueba recibo
original, conflicto, lectura inválida, rotación/revocación/reactivación, dos eventos,
seis auditorías y dispositivo final sin actividad ni credenciales. El otro test
comprueba variables exportadas y ausencia de respuestas guardadas. Los scripts de
la colección se comprueban sintácticamente con Node; el harness HTTP no ejecuta
JavaScript de Postman ni Vault. La ejecución manual nativa en Postman y la prueba
con emisor físico/proveedor quedan fuera de esta evidencia.

La prueba genera únicamente estados y códigos en
`artifacts/agent-checks/rf003-runbook-flow.json`; no guarda IDs, matrículas,
payloads, secretos o tokens. La evidencia consolidada de T003-18/19 y los TRX
quedan en `artifacts/agent-checks/`, ignorado por Git. No publicar TRX o capturas
sin revisar su contenido. Para repetir solo estos tests, con los prerrequisitos
SQL de [preparación](../configuration/lpr-test-preparation.md):

```sh
dotnet test tests/GarajeMetria.IntegrationTests --artifacts-path artifacts/agent-checks/rf003-build --no-build --no-restore --configuration Release --filter FullyQualifiedName~LprRunbookTests --logger trx --results-directory artifacts/agent-checks/2026-11-16-rf003-18-19
```

La suite completa diaria del 16 de noviembre de 2026 ya pasó: 579 correctas,
cero fallidas y una omitida (LDAPS real). T003-19 reutiliza esa evidencia y añade
las comprobaciones focalizadas de esta entrega, conforme a AGENTS.md. No valida
AD corporativo real, hardware LPR, backup/restore físico ni despliegue de producción.
El cierre documental global de RF-003 se registra en
[TASKS-003 / 11](../tasks/TASKS-003-lpr-event-ingestion.md#11-cierre-de-t003-20),
con los 18 CA/18 CP contrastados y estas limitaciones conservadas.
