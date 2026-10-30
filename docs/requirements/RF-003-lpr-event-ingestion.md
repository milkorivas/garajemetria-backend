# RF-003 — Recepción de eventos LPR

| ID | Requerimiento | Prioridad | Estado |
| --- | --- | --- | --- |
| RF-003 | Recepción de eventos LPR | Por definir | Definido funcionalmente; diseño redactado e implementación pendiente |

## 1. Objetivo y alcance

Recibir y conservar observaciones LPR mediante una API independiente del
fabricante y de la tecnología del emisor. Una cámara o un programa externo
debe adaptarse al contrato de GarajeMetría. Se envía un JSON por evento;
las primeras comprobaciones manuales pueden realizarse con Postman, Bruno o similar.

RF-003 incluye la administración de identidades de dispositivos y la emisión
OAuth Client Credentials. El panel consumidor pertenece a RF-008; este
repositorio implementará sus operaciones de backend, no la interfaz gráfica.
Las imágenes se tratarán en RF-007 y no forman parte de esta entrega.
La interpretación de entradas, salidas y estancias corresponde a RF-004.

Los contratos HTTP, opciones técnicas, persistencia y trazabilidad de pruebas
se concretan en [DESIGN-003](../design/DESIGN-003-lpr-event-ingestion.md).
El trabajo y sus dependencias se planifican en
[TASKS-003](../tasks/TASKS-003-lpr-event-ingestion.md).

## 2. Datos del evento

Los nombres siguientes han sido indicados por el usuario. Se concretan las
representaciones del contrato como decisiones técnicas de esta especificación.

| Campo | Regla |
| --- | --- |
| `event_id` | Obligatorio. UUID único generado por el emisor; se conserva al reenviar. |
| `capture_timestamp` | Obligatorio. Instante original de captura, distinto de la recepción. RFC 3339 con zona explícita y conversión a UTC. |
| `device_id` | Obligatorio. Identifica el dispositivo registrado; debe corresponder a la identidad autenticada. |
| `site_id` | Obligatorio. Id interno del taller de RF-009, transportado como string; debe coincidir con la asociación autorizada. |
| `plate` | Obligatorio. Texto reconocido. Se normaliza y evalúa como matrícula española; un formato no reconocido no impide conservar la lectura, que se marca inválida. |
| `confidence` | Obligatorio. Número entre 0 y 1, ambos incluidos. Se conserva incluso con baja confianza. |
| `direction` | Opcional. Valores `entry` y `exit`; ausencia o null indica desconocida. |

El backend establece el instante de recepción en UTC. No sustituir la fecha
de captura por la de reenvío ni inferir una dirección durante la ingesta.

## 3. Identidad y administración de dispositivos

| ID | Regla |
| --- | --- |
| RN-001 | GarajeMetría proporciona la emisión de tokens mediante OAuth Client Credentials, separada de la autenticación corporativa de RF-001. |
| RN-002 | Cada dispositivo tiene credenciales propias. Un programa intermediario debe utilizar las del dispositivo correspondiente; no se acuerda una credencial compartida para varias cámaras. |
| RN-003 | Solo el Administrador registra dispositivos y los asocia a un taller mediante operaciones administrativas. Un taller puede tener varios dispositivos. |
| RN-004 | La asociación de un dispositivo a su taller es inmutable. Para cambiarlo se desactiva el registro anterior y se crea otro. Conservar el vínculo de los eventos históricos. |
| RN-005 | Desactivar un dispositivo o revocar sus credenciales invalida inmediatamente sus tokens ya emitidos, también entre instancias de la API. |
| RN-006 | Rechazar la ingesta si el dispositivo o el taller están inactivos, aunque la captura sea anterior a la desactivación. La emisión y las escrituras deben coordinarse con cambios concurrentes de autorización. |
| RN-007 | El token solo permite operar para el dispositivo autenticado y su taller. Los identificadores recibidos en el JSON no conceden autorización. No conceder permisos de usuario corporativo a un dispositivo. |

### 3.1. Credenciales y tokens

- Generar `client_id` y `client_secret` por dispositivo. Mostrar el secreto
  únicamente al generarlo, también en rotación o reactivación. Persistir un
  verificador seguro, sin permitir recuperar el secreto desde consultas.
- El secreto no caduca por tiempo; permanece vigente hasta rotación o
  revocación. Su revocación no depende del vencimiento de los tokens.
- Los tokens de acceso duran inicialmente 15 minutos, configurables. No emitir
  refresh tokens: al caducar, el dispositivo solicita otro token mediante sus
  credenciales. No aplicar la sesión única de usuarios a los dispositivos.
- Rotar sustituye el secreto e invalida inmediatamente tanto el anterior como
  sus tokens. Un fallo de la operación no puede dejar un resultado parcial.
- Reactivar el mismo registro conserva identificador y taller, genera nuevas
  credenciales e impide recuperar la validez de secretos o tokens anteriores.
- Revocar o desactivar impide obtener nuevos tokens con las credenciales
  anteriores. Cada solicitud debe comprobar el estado vigente necesario para
  cumplir la invalidación inmediata; comprobar solo firma y caducidad no basta.

### 3.2. Operaciones administrativas

El Administrador puede registrar, consultar, cambiar el nombre, desactivar,
reactivar y rotar o revocar credenciales de dispositivos. Las consultas no
devuelven secretos. El Asesor y los propios dispositivos no pueden realizar
estas operaciones. Las listas estarán paginadas y ordenadas de forma estable.

No ofrecer borrado físico de dispositivos, tengan o no eventos. Conservar
siempre el registro y la relación histórica con su taller. Para altas, utilizar
un taller existente y activo del catálogo confirmado de RF-009; la reactivación
no permite eludir la restricción de ingesta para un taller inactivo.

Registrar auditoría inmutable de altas, cambios de nombre, desactivaciones,
reactivaciones, rotaciones y revocaciones: administrador, instante UTC,
dispositivo, operación, valores anteriores y nuevos no secretos, y correlación.
No exigir motivo para estas operaciones. El motivo obligatorio de las
asignaciones de usuarios de RF-002 no se traslada a dispositivos.
Confirmar el cambio, auditoría y efectos de revocación de forma atómica.
Aplicar la política común de auditoría de DESIGN-001 / 7.2, sin extenderla a
eventos LPR ni permitir borrar dispositivos como parte de esa limpieza.

## 4. Recepción, idempotencia y tiempo

| ID | Regla |
| --- | --- |
| RN-008 | Recibir un único evento por petición JSON, sin imagen. |
| RN-009 | Conservar `event_id` y captura originales durante reintentos y recuperación tras pérdida de conexión. |
| RN-010 | Un `event_id` ya guardado con los mismos datos se confirma sin crear otro registro ni duplicar efectos, incluso con solicitudes concurrentes. La unicidad debe estar respaldada por persistencia atómica. |
| RN-011 | El mismo `event_id` con datos distintos produce conflicto y conserva el registro original. Aplicar la comparación semántica de 4.2, no comparar únicamente los bytes del JSON. |
| RN-012 | Dos UUID distintos representan observaciones distintas; no descartar una por coincidir matrícula, dispositivo o captura. La interpretación posterior corresponde a RF-004. |
| RN-013 | Para un evento nuevo, admitir hasta 30 minutos de antigüedad y hasta 30 minutos de adelanto respecto de la recepción. Ambos límites serán configurables por separado. Rechazar si se supera cualquiera; el límite exacto está incluido. |
| RN-014 | Un reintento idéntico de un evento persistido se confirma aunque ya supere la antigüedad máxima. Comprobar primero autorización y estado activo de dispositivo y taller; esta excepción no recupera permisos revocados. |
| RN-015 | Conservar las lecturas de baja confianza; no establecer un umbral de rechazo por ese motivo. Rechazar confianza fuera del intervalo de 0 a 1. |
| RN-016 | Confirmar éxito solo tras persistencia duradera. Una respuesta perdida permite reenviar el mismo evento. Los fallos no permiten confirmar una escritura no acreditada. |

El límite temporal se calcula sobre instantes UTC, no sobre la fecha local del
taller. La zona del taller para agrupación diaria pertenece a RF-004. El plazo
de 30 minutos restringe la recuperación de eventos nuevos tras caídas largas;
conservar la fecha original no exime de ese límite.

### 4.1. Matrículas y lecturas inválidas

Convertir las letras a mayúsculas, eliminar espacios y guiones separadores y
conservar ceros iniciales. No sustituir caracteres supuestamente confundidos
por el reconocimiento, como `O` y `0` o `I` y `1`.

Admitir todos los formatos españoles actuales y antiguos, incluidos los
provinciales y especiales. El diseño debe enumerar los formatos y sus fuentes
oficiales; no limitar la validación al patrón ordinario de cuatro cifras y tres
letras. Validar el formato no acredita la existencia administrativa del vehículo.

Conservar el texto recibido y su forma normalizada, como datos sensibles.
Marcar explícitamente si el formato normalizado es válido o inválido. Conservar
una lectura de formato inválido con los demás datos del evento, también en
reintentos y auditoría técnica segura. No atribuirle automáticamente una
matrícula corregida ni descartar el evento por baja confianza.

La aceptación de formatos inválidos no exime de validar el contrato: rechazar
JSON mal formado, campos obligatorios ausentes, tipos incorrectos, texto vacío
o límites de tamaño excedidos. El diseño fijará esos límites. La utilización
de lecturas inválidas para calcular estancias se definirá en RF-004.

### 4.2. Comparación de reintentos

La identidad del evento es el UUID global `event_id`. Comparar dispositivo,
taller, instante de captura, texto original de matrícula, confianza y dirección;
no comparar recepción ni metadatos generados por el servidor. Fechas que
representan el mismo instante son equivalentes; ausencia y null de dirección
son equivalentes; cambios de orden o espacios del JSON no crean diferencias.
Cambiar el texto original de matrícula para un mismo UUID es conflicto, incluso
si produce la misma matrícula normalizada. No recalcular ni sobrescribir el
evento original al confirmar un reintento.

## 5. Seguridad y contratos pendientes de diseño

Usar HTTPS y configuración privada para secretos. No registrar credenciales,
tokens, matrículas completas ni payloads completos. Conservar eventos conforme
a la política indefinida de la visión; no incorporar limpieza automática.
Usar una implementación establecida de OAuth, no criptografía propia.

DESIGN-003 define rutas, autenticación del cliente OAuth, alcance de ingesta,
formato de token, límites de peticiones y cuerpos, transacciones y concurrencia.
La ruta de ingesta es `POST /api/lpr/events`, según ese diseño. Los errores
OAuth deben seguir el protocolo; las operaciones
de recursos seguirán ProblemDetails. No imponer el JSON de ingesta al endpoint
de token si el protocolo requiere otro formato.

## 6. Criterios de aceptación

| ID | Resultado esperado |
| --- | --- |
| CA-001 | Un dispositivo registrado y activo obtiene un token mediante Client Credentials; un usuario Asesor no puede registrar dispositivos. |
| CA-002 | Una credencial no permite enviar en nombre de otro dispositivo o taller. |
| CA-003 | Varios dispositivos activos pueden pertenecer al mismo taller, con credenciales independientes. |
| CA-004 | Desactivación o revocación impiden utilizar tokens previos; un taller inactivo no admite ingesta. Incluir carreras con emisión y persistencia. |
| CA-005 | La asociación no puede modificarse; un nuevo registro conserva la asociación histórica del anterior. |
| CA-006 | Un evento válido se conserva con captura, recepción y dirección opcional diferenciadas, sin imagen. |
| CA-007 | Reenvíos idénticos secuenciales y concurrentes generan un único registro; datos distintos para el mismo UUID producen conflicto sin sobrescritura. |
| CA-008 | Un duplicado idéntico ya persistido se confirma fuera de plazo si mantiene autorización y estados activos. |
| CA-009 | Con reloj controlado, se aceptan las fronteras de 30 minutos anteriores y posteriores; se rechaza al superarlas y se respetan cambios de configuración. |
| CA-010 | Lecturas con confianza baja válida se conservan. Dos UUID distintos no se deduplican por similitud. |
| CA-011 | Un fallo de persistencia no devuelve una confirmación falsa; repetir tras pérdida de respuesta no duplica el registro. |
| CA-012 | Se reconocen formatos españoles actuales, antiguos y especiales; se normalizan mayúsculas y separadores, conservando ceros sin corregir caracteres. Un formato inválido se guarda con marca explícita y texto original, y admite reintentos idénticos. |
| CA-013 | Los tokens duran inicialmente 15 minutos y respetan configuración; no hay refresh token y credenciales vigentes permiten obtener otro token. |
| CA-014 | El secreto se muestra únicamente al generarlo, no caduca automáticamente y no es recuperable por consulta. Rotar invalida secreto y tokens anteriores entre instancias. |
| CA-015 | Reactivar conserva registro y taller, genera nuevas credenciales y no recupera la validez de tokens anteriores. No existe borrado físico. |
| CA-016 | Las operaciones administrativas requieren Administrador y dejan auditoría sin secretos ni motivo obligatorio; ante fallo se evita éxito parcial. |
| CA-017 | Se rechazan errores de estructura, valores obligatorios vacíos y confianza fuera de rango. La marca de matrícula inválida no evita las comprobaciones de autorización o plazo. |
| CA-018 | Distinto orden del JSON, fechas equivalentes y dirección ausente/null permiten confirmar un reintento. Cambiar el texto original de matrícula o cualquier otro dato comparado produce conflicto. |

Los casos de prueba previstos y la trazabilidad detallada se recogen en
DESIGN-003; TASKS-003 planifica su ejecución pendiente. No hay implementación ni verificación
de estos criterios todavía.

## 7. Capacidad de referencia y trabajo posterior

Estimación facilitada por el usuario: 20 talleres, uno o dos dispositivos por
taller y aproximadamente una lectura por minuto y dispositivo. Equivale a
20–40 dispositivos y 20–40 eventos por minuto como carga nominal aproximada.
No es un límite contractual ni un pico acreditado; dimensionar también los
reintentos tras caídas. No se ha acordado un objetivo de latencia o disponibilidad.

La definición funcional está cerrada para este alcance. La prioridad de
planificación permanece por definir y no impide redactar el diseño.
DESIGN-003 concreta implementación OAuth establecida, contratos HTTP,
catálogo verificable de formatos de matrícula, límites, control de concurrencia,
integración con el catálogo de talleres y persistencia. TASKS-003
planifica pruebas unitarias, HTTP y SQL Server aislado, y verificación con
Postman y dispositivos o emisores externos. No declarar implementado o probado
lo documentado. La interfaz del panel continúa fuera de este repositorio.

## 8. Referencias

- [RFC 6749, Client Credentials](https://www.rfc-editor.org/rfc/rfc6749.html#section-4.4).
- [RFC 9562, UUID](https://www.rfc-editor.org/rfc/rfc9562.html).
- [RFC 3339, fechas](https://www.rfc-editor.org/rfc/rfc3339.html).
- [DGT, matriculaciones de vehículos](https://sede.dgt.gob.es/es/vehiculos/matriculaciones-de-vehiculos/).
