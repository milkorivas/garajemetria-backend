# TASKS-003 — Recepción de eventos LPR

| Campo | Valor |
| --- | --- |
| Requerimiento | [RF-003](../requirements/RF-003-lpr-event-ingestion.md) |
| Diseño | [DESIGN-003](../design/DESIGN-003-lpr-event-ingestion.md) |
| Coordinación | [TASKS-001](TASKS-001-authentication.md), [TASKS-002](TASKS-002-roles-and-workshop-access.md) y [TASKS-009](TASKS-009-workshop-catalog.md) |
| Estado | T003-01 a T003-20 completadas; cierre del backend con evidencia automatizada y limitaciones externas explícitas |

Los RN y CA pertenecen a RF-003; los CP se definen en DESIGN-003 / 9.
Una casilla completa exige implementación y evidencia de su verificación,
no solamente código escrito o documentos creados. El estado verificado se
registra al final de este documento. Las dependencias son tareas concretas, no la finalización íntegra
de otros requerimientos. Los identificadores no determinan por sí solos el orden.

## 1. Prerrequisitos y límites

Reutilizar sesiones administrativas de RF-001/RF-002 y la copia local confirmada
de talleres de RF-009. No exigir el origen corporativo definitivo ni un AD real
para las pruebas automatizadas de LPR: usar identidades sintéticas en el entorno
HTTP aislado y SQL Server real para persistencia. Las verificaciones externas
pendientes de otros tramos no se dan por realizadas al completar este plan.

Conservar Clean Architecture y .NET 10/EF Core 10. Domain no usa paquetes ni I/O;
Application no referencia HTTP, EF u OpenIddict. Api adapta protocolo y DTOs;
Infrastructure mantiene todos los stores, mappings y SQL. Propagar cancelación,
usar scopes adecuados y evitar un DbContext compartido concurrentemente.

Este plan cubre el backend y una comprobación HTTP sintética. No incluye UI del
panel, integración con una cámara concreta, imágenes, historial ni estancias.
No autoriza commits, publicación, despliegues, migraciones en producción ni
borrados. Las migraciones previstas se verifican únicamente en bases aisladas.

## 2. Preparación, modelo y contratos

- [x] **T003-01 — Preparar la integración y el entorno de verificación.**
  Dependencias: ninguna; inspeccionar la base existente de RF-001/002/009.
  Comprobar la versión estable de OpenIddict indicada en DESIGN-003 / 2.1,
  compatibilidad con los paquetes de la solución y avisos de seguridad antes
  del restore de implementación. Registrar cualquier actualización justificada
  en el diseño y fijar la misma versión para sus paquetes. Preparar configuración
  privada sintética, certificados de firma/cifrado y SQL Server aislado,
  reutilizando el patrón de `AuthenticationFixture`. Preparar escenarios con
  dos instancias y sin dependencia AD para el tráfico de dispositivos.
  Cierre: prerrequisitos reproducibles documentados, sin secretos versionados
  y sin cambiar el runtime o introducir paquetes en capas internas.
  Trazabilidad: CA-001, CA-004, CA-011; CP-001, CP-015, CP-018.

- [x] **T003-02 — Implementar el ciclo de vida del dispositivo en Domain.**
  Dependencias: ninguna.
  Modelar identidad, asociación inmutable, actividad, estado de credenciales,
  `Version` y `AuthorizationVersion`. Implementar renombrado, desactivación,
  revocación, rotación y reactivación según DESIGN-003 / 3, incluidos no-ops.
  Modelar auditoría no secreta sin motivo obligatorio y fechas suministradas.
  Cierre: pruebas unitarias deterministas de transiciones, asociación y versiones;
  el dominio no genera secretos ni accede a persistencia.
  Trazabilidad: CA-003, CA-005, CA-014, CA-015, CA-016;
  CP-003, CP-005, CP-012, CP-013.

- [x] **T003-03 — Implementar normalización y clasificación de matrículas.**
  Dependencias: ninguna.
  Implementar el clasificador puro `es-v1` y el catálogo de DESIGN-003 / 5:
  actuales, provinciales antiguas, especiales, temporales, diplomáticas y
  estatales/autonómicas. Incorporar prefijos históricos con fuentes oficiales,
  documentar reglas de compatibilidad y cubrir cada familia/prefijo con fixtures
  sintéticos positivos y negativos. Conservar original, ceros y versión; no
  corregir O/0, no eliminar prefijos por heurística y resolver ambigüedad como
  define el diseño. Un formato no reconocido produce una marca inválida.
  Cierre: pruebas unitarias por fila del catálogo, separadores, mayúsculas,
  ambigüedad y formatos inválidos; no afirmar existencia administrativa del
  vehículo ni exhaustividad histórica sin evidencia suficiente.
  Trazabilidad: CA-012, CA-017; CP-010, CP-011.

- [x] **T003-04 — Implementar evento, comparación y ventana temporal en Domain.**
  Dependencias: T003-03.
  Modelar `LprEvent`, captura/recepción UTC, dirección opcional, confianza
  decimal exacta y diagnóstico de matrícula. Implementar comparación semántica
  de RF-003 / 4.2 y ventana inclusiva configurable para eventos nuevos.
  Mantener dirección ausente/null equivalentes y texto original ordinal.
  Cierre: pruebas unitarias de ±30 minutos, un tick fuera, instantes equivalentes,
  confianza baja, texto distinto con igual normalización y dos UUID distintos.
  No inferir movimientos o estancias.
  Trazabilidad: CA-006, CA-007, CA-008, CA-009, CA-010, CA-018;
  CP-006, CP-007, CP-008, CP-011.

- [x] **T003-05 — Definir contratos y validación de Application.**
  Dependencias: T003-02, T003-04.
  Introducir repositorios, consultas materializadas, reloj, unidad de trabajo,
  credenciales, auditoría, cuotas y contexto de dispositivo según DESIGN-003 / 6.
  Reutilizar el contexto de usuario para administración. Definir comandos y
  queries MediatR, DTOs y resultados con errores conocidos, sin filtrar entidades
  de persistencia o tipos de protocolo. Registrar validadores y comportamiento
  asíncrono antes de handlers; impedir que secretos aparezcan en logging/ToString.
  Cierre: pruebas Application con dobles sin SQL/red, entrada inválida sin
  efectos y autorización previa a consulta de recursos; arquitectura preservada.
  Trazabilidad: CA-001, CA-002, CA-005, CA-017; CP-002, CP-011, CP-018.

## 3. Persistencia y servicios compartidos

- [x] **T003-06 — Integrar stores OAuth y crear la migración aditiva.**
  Dependencias: T003-01, T003-02, T003-04, T003-05.
  Registrar Core/EF de OpenIddict en Infrastructure y extender el contexto local
  existente, sin duplicar talleres. Añadir mappings de dispositivos, eventos,
  auditoría y relación OAuth 1:1; UUID global único de evento, FK compuesta de
  dispositivo/taller, precisión temporal/decimal, checks, índices y restricciones
  de borrado según DESIGN-003 / 7. Generar migración y snapshot nuevos con
  `--context AuthenticationDbContext`, sin modificar migraciones aplicadas.
  Cierre: migración probada en SQL aislado vacío y desde esquema anterior con
  datos sintéticos RF-001/002/009; restricciones efectivas, sin credenciales
  precargadas ni cascadas destructivas. Revisar script y recuperación antes de
  su uso futuro; no aplicar automáticamente al arranque.
  Trazabilidad: CA-003, CA-005, CA-007, CA-010, CA-015;
  CP-005, CP-007, CP-018.

- [x] **T003-07 — Implementar unidad de trabajo, reloj y repositorios.**
  Dependencias: T003-06.
  Implementar contratos de T003-05 con contexto scoped, lectura UTC compartida,
  proyecciones y versiones condicionales. Respetar orden catálogo, usuario
  administrador cuando corresponda, dispositivo y evento; timeout y cancelación
  acotados. Compartir transacción y DbContext con los gestores OAuth. Reutilizar
  clasificación de fallos y resultado de commit indeterminado, sin reintentos
  automáticos de escrituras. Mantener SQL específico en Infrastructure.
  Cierre: pruebas SQL de locks, rollback, timeout, FKs, comparación ordinal,
  precisiones sin redondeos y commit incierto; ningún éxito parcial o consulta
  externa dentro de una transacción local.
  Trazabilidad: CA-004, CA-007, CA-009, CA-011, CA-016, CA-018;
  CP-007, CP-008, CP-009, CP-013, CP-015, CP-018.

- [x] **T003-08 — Implementar configuración y cuotas compartidas de LPR.**
  Dependencias: T003-05, T003-07.
  Registrar opciones tipadas de DESIGN-003 / 8, certificados y validación al
  arranque. Extender el patrón de cuotas SQL con namespaces/políticas separados
  de login, reloj compartido y fingerprint común entre instancias. Aplicar
  límites IP previos, por cliente/dispositivo y por administrador, incluyendo
  duplicados; cuotas fuera de locks de negocio, sin cola en memoria. Devolver
  Retry-After y respetar proxies confiables. No reutilizar el store actual sin
  adaptar su restricción de políticas de autenticación.
  Cierre: pruebas de configuración inválida, cuotas entre instancias, ventana
  exacta y SQL fallido; errores OAuth para token y ProblemDetails para recursos.
  Trazabilidad: CA-004, CA-010, CA-011, CA-017; CP-014, CP-015, CP-017.

## 4. Administración y OAuth

- [x] **T003-09 — Implementar credenciales y casos de uso administrativos.**
  Dependencias: T003-02, T003-05, T003-07.
  Generar secretos aleatorios con API criptográfica establecida y delegar
  hash/verificación en el gestor OpenIddict, sin copia recuperable ni caché
  obsoleta. Implementar alta, consultas, renombrado, desactivación, revocación,
  rotación y reactivación. Verificar Administrador/sesión y ETag vigentes dentro
  de la frontera transaccional. Confirmar dispositivo, credenciales, auditoría
  y versiones conjuntamente. Taller activo al crear/reactivar/rotar; nunca
  transferir asociación, borrar dispositivo o exigir motivo.
  Cierre: unitarias Application y SQL focalizadas para transiciones, no-op,
  primera entrega de secreto, fallo del gestor/auditoría y rollback atómico.
  Trazabilidad: CA-001, CA-003, CA-005, CA-014, CA-015, CA-016;
  CP-002, CP-003, CP-005, CP-012, CP-013.

- [x] **T003-10 — Exponer endpoints administrativos de dispositivos.**
  Dependencias: T003-08, T003-09.
  Implementar las ocho rutas de DESIGN-003 / 4.3 con DTOs camelCase, JSON
  estricto, límites, filtros/paginación y orden fijo. Exigir Bearer de usuario
  Administrador; ETag fuerte y If-Match para modificaciones, con 428/400/412
  según contrato. Credenciales solo en las respuestas de generación y no-store.
  Activar metadata `LprTraffic.Administration` para timeout total; la cuota por
  Administrador ya se consume en Application y no debe duplicarse en Api.
  Cierre: WebApplicationFactory verifica rutas, Location de alta, paginación,
  errores, no-op, permisos y ausencia de secretos en GET. No añadir DELETE
  ni aceptar workshopId editable o campos internos por overposting.
  Trazabilidad: CA-001, CA-002, CA-005, CA-014, CA-015, CA-016, CA-017;
  CP-002, CP-005, CP-012, CP-013, CP-014.

- [x] **T003-11 — Implementar emisión OAuth y autenticación de dispositivos.**
  Dependencias: T003-08, T003-09.
  Configurar `/connect/token` con Basic/form y Client Credentials, scope único,
  audiencia/issuer exclusivos y tokens protegidos de 15 minutos configurables.
  Activar metadata Token para cuota IP previa a autenticación e integrar
  `LprRateLimits.AdmitAsync` con `lpr:tokens-client` tras el parsing del clientId
  por OpenIddict y antes de autenticar el secreto, incluso para clientes rechazados.
  Preparar grant bajo lock con revalidación de secreto y estado; mantener la
  versión que se verificó aunque rote antes de firmar. Activar validación local
  y de entrada de token, más comprobación SQL de versión/actividad/taller.
  Separar esquemas y metadata del Bearer de usuario, conservar sus rutas y
  antiforgery, y tratar errores de middleware con el protocolo apropiado.
  Cierre: pruebas HTTP/OAuth EF real de Basic, grants/scopes rechazados,
  expiración, dos dispositivos, coexistencia de tokens, no refresh/cookies,
  certificados y cruces entre esquemas. Tokens/secreto antiguos no funcionan
  en otra instancia tras rotar o revocar; SQL fallido no concede acceso.
  Trazabilidad: CA-001, CA-002, CA-003, CA-004, CA-011, CA-013, CA-014;
  CP-001, CP-002, CP-003, CP-012, CP-014, CP-015.

- [x] **T003-12 — Extender publicación del catálogo e invalidación de tokens.**
  Dependencias: T003-02, T003-07.
  Al publicar desactivación RF-009, renovar versiones de todos sus dispositivos
  en la misma transacción del catálogo. Conservar actividad propia, secretos,
  registros y asociaciones; al reactivar taller, exigir token nuevo sin recuperar
  el anterior. Reutilizar última copia confirmada ante caída y devolver 503
  sin copia inicial cuando sea necesaria. Mantener efectos existentes en Asesor
  y Administrador y actualizar DESIGN-009 con el estado realmente implementado.
  Cierre: pruebas focalizadas del publicador/SQL para estados y rollback;
  las carreras con emisión/ingesta se completan en T003-17. No dejar la extensión
  como tarea de RF-009 sin responsable dentro de este tramo.
  Trazabilidad: CA-004, CA-005, CA-015; CP-004, CP-005, CP-015.

## 5. Recepción y operación

- [x] **T003-13 — Implementar ingesta idempotente en Application y persistencia.**
  Dependencias: T003-03, T003-04, T003-05, T003-07, T003-11, T003-12.
  Revalidar estado/versión/expiración dentro de la transacción. Consultar UUID
  antes de aplicar ventana a eventos nuevos; confirmar duplicado idéntico con
  metadatos originales, rechazar conflicto y no revelar datos de otro taller.
  Guardar formato inválido y confianza baja válida. Insertar bajo lock de UUID
  y restricción única; ante colisión conocida, abortar y releer autorizado en
  otra transacción. No reclasificar reintentos ni crear efectos RF-004.
  Cierre: pruebas Application y SQL de comparación semántica, UUIDs distintos,
  límites temporales, rollback y respuesta perdida. No transformar cualquier
  error de persistencia en duplicado ni confirmar antes del commit.
  Trazabilidad: CA-002, CA-004, CA-006, CA-007, CA-008, CA-009, CA-010,
  CA-011, CA-012, CA-018; CP-006, CP-007, CP-008, CP-009, CP-011, CP-015.

- [x] **T003-14 — Exponer el contrato HTTP de recepción.**
  Dependencias: T003-08, T003-11, T003-13.
  Implementar `POST /api/lpr/events` con scope de dispositivo, DTO snake_case,
  UUIDs/fechas/decimal exactos, dirección nullable y lectura JSON estricta con
  límites de bytes/profundidad. Mapear 201/200 y plate_status según diseño;
  400/401/403/409/413/415/422/429/500/503, con errores seguros. No añadir imágenes,
  GET de historial, Location ficticia ni autenticación por cookies de usuario.
  Activar metadata Event para cuota IP/timeout; la cuota por dispositivo ya está
  en el pipeline Application antes del handler e incluye duplicados, sin duplicarla.
  Cierre: pruebas HTTP de estructuras/tipos/campos desconocidos o duplicados,
  precisiones, límites, scopes, baja confianza, matrícula inválida y errores
  transaccionales sin exponer matrículas o detalles internos.
  Trazabilidad: CA-002, CA-006, CA-007, CA-008, CA-009, CA-010, CA-011,
  CA-012, CA-017, CA-018; CP-002, CP-006, CP-007, CP-008, CP-009,
  CP-011, CP-014, CP-015, CP-017.

- [x] **T003-15 — Integrar retención en el supervisor existente.**
  Dependencias: T003-06, T003-09, T003-11.
  Incorporar auditoría administrativa de dispositivos con AuditRetentionDays
  y metadatos elegibles de token con DeviceTokenRetentionHours. Reutilizar lease,
  reloj, lotes y calendario de DESIGN-001 / 7.2; evitar pruning independiente.
  No borrar tokens vigentes/no elegibles, clientes, secretos vigentes, dispositivos,
  talleres, eventos ni referencias históricas. Mantener margen configurable y
  validación al arranque, sin crear un endpoint de limpieza.
  Cierre: pruebas SQL con frontera temporal, ejecución concurrente, pérdida de
  lease y cancelación; auditoría inmutable durante retención y sin cascadas.
  Trazabilidad: CA-005, CA-014, CA-015, CA-016; CP-005, CP-012, CP-016.

- [x] **T003-16 — Completar OpenAPI, observabilidad y configuración documentada.**
  Dependencias: T003-10, T003-11, T003-12, T003-14, T003-15.
  Describir DTOs, esquemas diferenciados, formulario OAuth, respuestas, cuotas
  y precondiciones. Documentar las rutas anónimas de metadatos que efectivamente
  publique OpenIddict; no registro dinámico o revocación anónima. Revisar CORS
  administrativo para métodos/cabeceras necesarios sin ampliar orígenes.
  Añadir métricas/logs seguros y diagnóstico SQL, sin secretos, payloads o
  matrículas en OpenIddict, IdentityModel, EF, validación o logging MediatR.
  Actualizar opciones de ejemplo sin valores privados y sin habilitar Swagger
  por defecto en producción.
  Cierre: OpenAPI y logs verificados por pruebas focalizadas, incluido secreto
  de respuestas de generación y errores anteriores al handler; ninguna métrica
  usa matrícula o identificador arbitrario como etiqueta de alta cardinalidad.
  Trazabilidad: CA-001, CA-002, CA-011, CA-014, CA-016, CA-017;
  CP-002, CP-012, CP-014, CP-015, CP-017, CP-018.

## 6. Verificación transversal y entrega

- [x] **T003-17 — Verificar carreras, aislamiento y regresiones compartidas.**
  Dependencias: T003-10, T003-11, T003-12, T003-14, T003-15, T003-16.
  Completar la matriz de DESIGN-003 / 9 con WebApplicationFactory y SQL Server
  aislado. Coordinar de forma determinista dos instancias para ingesta duplicada,
  rotación/revocación frente a emisión, expiración durante espera, desactivación
  de taller frente a ingesta y reactivación sin recuperar tokens antiguos.
  Probar commit incierto, fallo de gestor/auditoría, cuotas y limpieza con lease.
  Ejecutar regresiones focalizadas de autenticación, administración, catálogo,
  configuración, logging, health, OpenAPI y arquitectura afectadas por cambios.
  Cierre: resultados trazables de CP-001 a CP-018; no sustituir SQL por SQLite
  o EF InMemory ni dar por probada concurrencia con dobles de repositorio.
  Trazabilidad: CA-001 a CA-018; CP-001 a CP-018.

- [x] **T003-18 — Crear runbook y comprobar el flujo HTTP sintético.**
  Dependencias: T003-16, T003-17.
  Crear `docs/runbooks/lpr-event-ingestion.md` y colección Postman/Bruno o
  equivalente sin secretos. Documentar certificados/configuración privada,
  migración, registro, custodia/rotación y pérdida de respuesta con secreto.
  Reproducir login de Administrador, alta, token Basic/form, ingesta 201,
  duplicado 200, conflicto 409, matrícula inválida aceptada y token revocado
  rechazado. Explicar límites temporales para recuperación tras caídas,
  reactivación, errores OAuth frente a ProblemDetails y conservación externa.
  Cierre: flujo ejecutado y evidencia segura registrada. No presentar estas
  llamadas como prueba de hardware o proveedor real, que queda fuera del tramo.
  Trazabilidad: CA-001, CA-004, CA-007, CA-008, CA-011, CA-012, CA-014,
  CA-015; CP-001, CP-003, CP-007, CP-009, CP-011, CP-012, CP-014.

- [x] **T003-19 — Ejecutar checks proporcionados y registrar evidencia.**
  Dependencias: T003-17, T003-18.
  Completar restore/build Release/format necesarios para el cambio y pruebas
  focalizadas, conservando resultados y prerequisitos. Antes de cualquier suite
  completa comprobar historial conocido y `artifacts/agent-checks/full-suite.json`
  con fecha Atlantic/Canary. Si no hay intento diario y corresponde sesión de
  desarrollo, ejecutar una sola vez y registrar intento, hora UTC, comando,
  resultado y recuentos disponibles; un fallo también consume ese intento.
  Si ya se intentó, usar evidencia conocida y pruebas focalizadas para cambios
  nuevos o fallos, sin repetir suite completa salvo petición explícita.
  Cierre: compilación sin nuevas advertencias, formato correcto, verificaciones
  requeridas por política pasando, y evidencia o impedimentos expresos sin
  inventar resultados. No iniciar un segundo intento completo para cerrar la casilla.
  Trazabilidad: CA-001 a CA-018; CP-001 a CP-018.

- [x] **T003-20 — Reconciliar documentación y cerrar el tramo con sus límites.**
  Dependencias: T003-18, T003-19.
  Actualizar RF-003, DESIGN-003, catálogo, arquitectura, DESIGN-009 y este plan
  con los contratos/versiones realmente entregados y enlaces al runbook.
  Registrar por CP la prueba y evidencia que lo cubren; mantener abiertas tareas
  sin verificación. Informar del último intento de suite conocido y de checks
  diferidos por frecuencia, distinguiendo una comprobación pendiente de una fallida.
  Cierre: 18 CA cubiertos por evidencia verificable, 18 CP resueltos y ninguna
  tarea requerida abierta; documentar las limitaciones externas que sigan fuera
  del alcance. No cambiar «Definido» por «probado» solo por completar documentación.
  Trazabilidad: CA-001 a CA-018; CP-001 a CP-018.

## 7. Matriz CP → tareas responsables

DESIGN-003 / 9 mantiene la relación CA → CP; esta tabla asigna cada CP a trabajo
concreto. T003-17 integra la evidencia y no sustituye las pruebas de cada tarea.

| CP | Tareas principales |
| --- | --- |
| CP-001 | T003-01, T003-11, T003-18 |
| CP-002 | T003-05, T003-09, T003-10, T003-11, T003-14 |
| CP-003 | T003-02, T003-09, T003-11, T003-17 |
| CP-004 | T003-11, T003-12, T003-13, T003-17 |
| CP-005 | T003-02, T003-06, T003-09, T003-10, T003-12, T003-15 |
| CP-006 | T003-04, T003-13, T003-14 |
| CP-007 | T003-04, T003-06, T003-07, T003-13, T003-14, T003-17 |
| CP-008 | T003-04, T003-07, T003-13, T003-14, T003-17 |
| CP-009 | T003-07, T003-13, T003-14, T003-17, T003-18 |
| CP-010 | T003-03, T003-17 |
| CP-011 | T003-03, T003-04, T003-05, T003-13, T003-14 |
| CP-012 | T003-02, T003-09, T003-10, T003-11, T003-16, T003-18 |
| CP-013 | T003-02, T003-07, T003-09, T003-10, T003-17 |
| CP-014 | T003-08, T003-10, T003-11, T003-14, T003-16, T003-18 |
| CP-015 | T003-01, T003-07, T003-08, T003-11, T003-12, T003-13, T003-16 |
| CP-016 | T003-15, T003-17 |
| CP-017 | T003-08, T003-14, T003-16, T003-17 |
| CP-018 | T003-01, T003-05, T003-06, T003-07, T003-16, T003-17 |

## 8. Puertas de verificación

| Hito | Tareas necesarias | Condición |
| --- | --- | --- |
| Base persistente | T003-01 a T003-07 | Modelo/contratos, migración, restricciones y transacciones verificadas sin acoplar capas internas. |
| Identidad de dispositivos | T003-08 a T003-12 | Administración, OAuth, cuotas y publicación de catálogo coherentes; credenciales antiguas no recuperan acceso. |
| Recepción completa | T003-13 a T003-16 | Ingesta, errores, retención, OpenAPI y observabilidad con sus pruebas focalizadas. |
| Tramo entregable | T003-17 a T003-20 | Carreras/regresiones, flujo HTTP y evidencia final; limitaciones externas explícitas. |

El orden de trabajo permite preparar Domain sin entorno SQL, pero completar
una puerta no habilita publicar rutas parciales. No exponer ingesta/emisión como
terminadas antes de integrar autorización, cuotas e invalidación del catálogo.
No exigir autorización para estas tareas reversibles de desarrollo; los límites
de publicación y producción se mantienen según AGENTS.md y las instrucciones
expresas del usuario.

## 9. Política de ejecución y evidencia actual

Durante implementación, adaptar los comandos a los proyectos y filtros de las
pruebas que se creen. Los comandos raíz de referencia, sujetos a frecuencia, son:

```sh
dotnet restore
dotnet build --no-restore --configuration Release
dotnet format --verify-no-changes --no-restore
dotnet test --no-build --configuration Release
```

El último comando sin filtro es suite completa; no usarlo para una comprobación
focalizada. Entre intentos completos seleccionar proyecto y pruebas afectadas,
sin repetir o ampliar checks que ya pasaron salvo cambio, fallo o incertidumbre
nueva. Guardar resultados locales bajo `artifacts/agent-checks/` sin secretos;
no confundir estos resultados con el estado de las verificaciones AD o hardware.

**Evidencia de T003-01/02/03, 13 de noviembre de 2026:**

- Preparación reproducible en [lpr-test-preparation](../configuration/lpr-test-preparation.md).
  OpenIddict 7.7.2 fijado y resuelto en todas sus dependencias; restore con
  NuGet Audit y consulta de vulnerabilidades transitivas sin avisos conocidos.
- `LprDevice`, snapshots y auditoría de dominio implementados, sin persistencia
  ni generación de secretos. `SpanishPlateClassifier` implementa `es-v1` con
  tests por prefijo/familia; las limitaciones históricas están documentadas.
- Build Release de solución en salida aislada: cero errores y advertencias.
  La salida normal estaba bloqueada por una API en ejecución, que se conservó.
- Filtro `LprDeviceTests|SpanishPlateClassifierTests`: 128 correctas, cero
  fallidas/omitidas. La primera ejecución tuvo un fallo en un fixture Unicode
  pasado mediante atributo xUnit; se corrigió para construirlo en ejecución.
- Filtro `LprPreparationTests`: una correcta en SQL Server aislado con dos hosts,
  certificados compartidos, scopes separados y cero llamadas al directorio.
- Filtro `DependencyTests`: seis correctas. Formato de archivos afectados y
  comprobación de espacios del diff correctos.
- Evidencia local ignorada: `artifacts/agent-checks/2026-11-13-rf003-*`;
  compilación bajo `artifacts/agent-checks/rf003-build`, con `--artifacts-path`
  al restaurar/compilar/probar. Auditoría en `2026-11-13-rf003-package-audit.json`.

No se repitió la suite completa: el registro diario ya contiene un intento
interrumpido sin resultados, que no acredita pruebas pasando. Solo se ejecutaron
los filtros anteriores. No se aplicaron migraciones de RF-003 ni se emitieron
credenciales o tokens. Ningún CP de HTTP/OAuth/concurrencia de ingesta se considera
cerrado por estas pruebas de preparación y dominio. La continuación se registra debajo.

**Evidencia de T003-04/05, 13 de noviembre de 2026:**

- `LprEvent` conserva valores originales, diagnóstico de placa, decimal exacto y
  dirección opcional, con instantes UTC. Comparación ordinal/semántica sin
  reclasificación y ventana inclusiva independiente para eventos nuevos.
- Contratos concretos de repositorios, credenciales, autorización, reloj, unidad
  de trabajo, auditoría, cuotas, DTOs y once comandos/consultas MediatR definidos.
  Validadores registrados y comportamiento asíncrono anterior a handlers;
  entradas inválidas se rechazan sin consultas de autorización ni efectos.
- Autorización previa de administrador y dispositivo mediante comportamientos
  MediatR restringidos a cada familia. Los tests usan dobles sin SQL/red y
  comprueban que los rechazos no alcanzan el handler de recursos. Los handlers
  de escritura y la revalidación transaccional siguen pendientes.
- Build Release de solución en salida aislada: cero errores y advertencias.
  Filtro Domain LPR: 150 correctas (incluye las 128 anteriores). Filtro
  `LprContractsTests|LoggingBehaviorTests`: 23 correctas. Dependencias de
  arquitectura: seis correctas. Regresión HTTP SQL aislada
  `LoginRotationAndLogoutRespectTheHttpAndCookieContract`: una correcta.
  Ninguna fallida u omitida en estas ejecuciones finales.
- Formato de archivos afectados y `git diff --check` correctos. Evidencia local
  bajo `artifacts/agent-checks/2026-11-13-rf003-04-05-*`; resultados finales en
  los directorios con sufijo `-final`. Se conserva la salida de compilación
  aislada y no se detiene la API del desarrollador.

Al cerrar ese tramo, T003-06 a T003-20 seguían abiertas. No se repitió la suite completa por el intento
diario previo interrumpido sin resultados; no se acredita que pase. No se han
implementado rutas, persistencia LPR, emisión OAuth o ingesta transaccional, ni
se cierran todavía los CP de concurrencia/persistencia/HTTP de RF-003.

**Evidencia de T003-06/07, 13 de noviembre de 2026:**

- Core/stores EF de OpenIddict registrados en Infrastructure sobre el mismo
  contexto scoped, sin caché de entidades. Relación 1:1 con cliente mediante
  `OAuthApplicationId` único y restrictivo; el adaptador de credenciales futuro
  utilizará como clave interna `deviceId:N` y como client_id `lpr_<deviceId:N>`.
- Migración aditiva `20261113115955_LprPersistence`: siete tablas nuevas,
  checks, índices, PK global de eventos y FK compuesta dispositivo/taller.
  Snapshot verificado con `has-pending-model-changes`. Script Up revisado:
  sin cambios de columnas/datos anteriores ni clientes o secretos precargados,
  todas las FKs nuevas sin cascada. Solo aplicada en bases aisladas de tests.
- Repositorios, auditoría, consultas materializadas, reloj SQL y unidad de
  trabajo scoped implementados. Comparación delegada al valor Domain
  `LprObservation`, ordinal y sin reclasificación. Versiones condicionales,
  lecturas sin tracking, locks catálogo/administrador/dispositivo/evento,
  cancelación y timeouts 15/5 segundos configurables. Resultado fallido revierte
  también SaveChanges internos OAuth; commit incierto conserva su clasificación
  y no reintenta escrituras automáticamente.
- SQL inicial: 13 correctas y dos fallos de fixture (longitud de dirección
  enmascaraba el CHECK y refresh sintético sin precisión de segundos RF-001).
  Correcciones focalizadas: siete correctas. Regresiones afectadas y snapshot
  previo/nuevo: nueve correctas. Con los últimos resultados por caso, 15 casos
  nuevos y ocho regresiones SQL correctos, sin fallos pendientes.
- Domain `LprEventTests`: 22 correctas; dependencias de arquitectura: seis
  correctas. Build Release de solución sin errores ni advertencias; formato y
  diff verificados. Evidencia local: `artifacts/agent-checks/2026-11-13-rf003-06-07-*`
  y script `artifacts/agent-checks/2026-11-13-LprPersistence.sql`.

No se repitió la suite completa por el intento diario previo interrumpido sin
resultados. El backup/restauración física pendiente del proyecto no se considera
verificado. T003-08 a T003-20 permanecen abiertas: cuotas, credenciales y casos
de uso, publicación de catálogo, OAuth/HTTP de dispositivos e ingesta completa.
Las pruebas de repositorio no acreditan todavía los CP HTTP/OAuth de extremo a extremo.

**Evidencia de T003-08/09, 13 de noviembre de 2026:**

- Configuración tipada obligatoria Lpr/DeviceOAuth; validación de ventanas,
  cuotas, timeouts, issuer/audience propios y PFX RSA privados/vigentes al arranque.
  Ejemplo sin secretos en [lpr.example.json](../configuration/lpr.example.json).
  Integrar esas secciones en el archivo privado antes de arrancar el nuevo binario;
  no se ha modificado el archivo privado habitual ni generado material de despliegue.
- Store de cuotas SQL independiente de RF-001/AD, fingerprint común por namespace,
  ventana exacta y Retry-After. Metadata/middleware y adaptador de errores listos
  para la publicación de rutas en T003-10/11/14; no se han creado endpoints nuevos.
  Cuota por dispositivo y duplicados en el pipeline, y por administrador antes
  de locks de negocio. La limpieza conserva la mayor ventana de ambos subsistemas
  y comparte lock de partición, sin mezclar sus políticas/particiones hash.
- Ocho handlers administrativos MediatR implementados con validación y sesión/
  rol previos, revalidación transaccional, versiones condicionales, no-ops, taller
  activo cuando corresponde y auditoría atómica. Sin transferencia ni DELETE.
- Secretos de 32 bytes RNG/base64url, hash/verificación OpenIddict, sin expiración
  temporal ni copia recuperable. Rotación/reactivación conservan el cliente y
  cambian secreto/versión. Revocación elimina hash y permisos conservando cliente
  confidencial; el store oficial se usa para quitar el secreto porque el gestor
  exige secreto o clave en clientes confidenciales. No se habilita un cliente público.
- Application: 34 pruebas correctas, incluidas regresiones de validación/logging.
  Configuración, SQL, componentes de transporte y regresiones: 43 casos distintos
  correctos en las ejecuciones focalizadas, sin fallos/omitidas. Arquitectura:
  14 correctas. Build Release sin errores ni advertencias; formato y diff correctos.
  Evidencia local en `artifacts/agent-checks/2026-11-13-rf003-08-09-*`.

No se repitió la suite completa: sigue constando el intento diario previo
interrumpido sin resultados. T003-10 a T003-20 siguen abiertas; todavía no hay
emisión OAuth HTTP, ingesta persistente por endpoint ni verificación de revocación
de tokens emitidos. Las versiones persistidas ya cambian; T003-11 conectará esa
comprobación a todos los tokens de dispositivo. Sin migraciones adicionales en este tramo.

**Evidencia de T003-10/11, 13 de noviembre de 2026:**

- Ocho rutas administrativas con Bearer exclusivo de Administrador, JSON estricto,
  límites/filtros/paginación, Location, ETag/If-Match, no-ops y respuestas no-store.
  Los GET no entregan secretos ni versiones internas en el cuerpo. Sin DELETE,
  transferencia de taller o campos internos editables.
- `/connect/token` con parsing OpenIddict, Basic/form, Client Credentials y scope
  único devuelto explícitamente; sin refresh, cookies, discovery/JWKS públicos.
  Cuota IP previa al protocolo y por cliente parseado antes de verificar secreto,
  también para rechazos. Errores OAuth seguros incluso antes del handler.
- Grant con revalidación de secreto/estado bajo locks y fechas SQL compartidas;
  conserva la versión verificada aunque rote antes de firmar. Validación local
  de JWT firmado/cifrado, entrada de token y estado/versión/taller actuales en SQL.
  Separación de usuarios/dispositivos, sin consultas AD para tráfico de dispositivos.
- WebApplicationFactory con SQL real: 27 casos HTTP nuevos, incluida rotación
  entre preparación y firma, invalidación entre hosts, expiración, entrada revocada,
  certificados distintos, dos dispositivos, cuotas y fallos SQL sin acceso.
  Logs de emisión comprobados sin secretos, tokens ni Basic; exclusión absoluta
  de categorías OpenIddict/IdentityModel con contenido potencialmente sensible.
- Application: 36 casos correctos. Integración/configuración/logging y regresiones:
  108 casos distintos correctos con el último resultado por caso. Arquitectura:
  14 correctos. Los fallos detectados durante desarrollo están corregidos;
  resultados TRX locales en `artifacts/agent-checks/2026-11-13-rf003-10-11-*`.
- Compilación Release de la solución correcta, sin errores ni advertencias;
  formato y diff verificados. Se utilizó salida separada porque la API local
  mantiene ocupadas las DLL Debug. Evidencia consolidada local en
  `artifacts/agent-checks/2026-11-13-rf003-10-11-result.json`.

No se repitió la suite completa: el intento diario previo continúa interrumpido
sin resultados. T003-12 a T003-20 siguen abiertas. El publicador de catálogo debe
invalidar versiones al desactivar talleres para que reactivarlos no recupere
tokens anteriores; tampoco hay ingesta HTTP/persistente todavía. No se aplican
migraciones nuevas ni cambios a la configuración privada del desarrollador.

**Evidencia de T003-12/13, 13 de noviembre de 2026:**

- El publicador RF-009 renueva ambas versiones de todos los dispositivos de
  talleres desactivados en la misma transacción. Conserva actividad propia,
  secretos y asociaciones, sin afectar versiones de talleres que siguen activos.
  Reactivar exige token nuevo y no recupera tokens anteriores. Se mantienen
  cierres de sesiones de Asesor y acceso del Administrador; DESIGN-009 actualizado.
- `ReceiveLprEventHandler` revalida identidad, scope, estado, taller, versión y
  caducidad después de adquirir locks. Consulta UUID antes de la ventana; devuelve
  el recibo original del duplicado, rechaza conflictos sin datos ajenos y conserva
  lecturas inválidas o de confianza baja. Sin efectos RF-004 ni rutas HTTP nuevas.
- Una colisión conocida de la PK global, con rollback confirmado, se adapta en
  Infrastructure a un contrato propio de Application. Se reconcilia una vez en
  otra transacción con autorización actual, sin volver a insertar. Otros errores
  y commit incierto no devuelven éxito ni activan reintentos de escritura.
- Application: 43 casos distintos correctos, incluidos 17 nuevos de ingesta y
  regresiones de contratos/autorización/token. SQL y HTTP: 32 correctos, incluidas
  concurrencia entre hosts, UUIDs distintos, comparación semántica, conflicto
  entre talleres, duplicados antiguos, colisión real, rollback, commit confirmado
  sin acuse, copia antigua/sin publicación inicial y reactivación con token real.
  Evidencia TRX local en `artifacts/agent-checks/2026-11-13-rf003-12-13-*`.
- Arquitectura: 14 correctas; total de 89 casos focalizados distintos correctos,
  sin fallos ni omitidos. Build Release final sin errores ni advertencias;
  formato y diff correctos. Un bloqueo transitorio de DLL durante build/test
  simultáneos se resolvió al terminar el testhost y confirmar el build.
  Evidencia consolidada en `artifacts/agent-checks/2026-11-13-rf003-12-13-result.json`.

Sin nuevas migraciones ni modificaciones a la base local del desarrollador en
este tramo. La suite completa no se repitió: permanece el intento diario previo
interrumpido sin resultados. T003-14 a T003-20 siguen abiertas; recepción HTTP,
retención, observabilidad, carreras transversales y runbook final siguen pendientes.

**Evidencia de T003-14/15, 13 de noviembre de 2026:**

- POST `/api/lpr/events` con esquema exclusivo de dispositivo, scope, metadata
  Event y cuotas IP/dispositivo sin duplicarlas. JSON snake_case estricto con
  bytes/profundidad acotados, UUID canónico y timestamps sin truncar. Confianza
  normalizada léxicamente antes de convertir, sin pérdida silenciosa de precisión.
- Respuestas 201/200 con diagnóstico y recibo original, sin matrícula o Location;
  errores 400/401/403/409/413/415/422/429/500/503 seguros. Verificados duplicados
  antiguos, baja confianza, formato inválido, offsets/versiones UUID, scopes,
  redondeo rechazado, cuerpo sin Content-Length, UTF-8 mal formado, commit incierto
  reconciliable por HTTP y logs sin tokens/matrículas. OpenAPI describe la ruta.
- El supervisor existente limpia auditoría de dispositivos con AuditRetentionDays
  y tokens elegibles vinculados a dispositivos con DeviceTokenRetentionHours.
  Margen desde expiración, también para revocados, porque no hay fecha de
  revocación persistida. Fechas incompletas, expiración futura, actividad reciente,
  estados/tipos ajenos o clientes sin dispositivo se conservan; comparación exacta
  de tipos/estados, incluidas mayúsculas y espacios finales.
- Se reutilizan lease/calendario SQL, lotes y transacción. Lease vencido dentro
  del lote o cancelación revierte todas las eliminaciones. Concurrencia y frontera
  temporal de un tick probadas con SQL real; conservación de eventos, dispositivos
  y secretos comprobada. CleanupResult añade DeviceTokens al recuento del supervisor.
- Pruebas y evidencia local en `artifacts/agent-checks/2026-11-13-rf003-14-15-*`.
  Guía de preparación actualizada con envío desde Postman y límites de retención;
  DESIGN-001 documenta la ampliación del mismo supervisor.
- Últimos resultados por caso: 58 correctos de integración/configuración/HTTP y
  14 de arquitectura, total 72 sin fallos ni omitidos. Incluyen 28 casos HTTP y
  cinco de retención nuevos. Build Release final sin errores ni advertencias;
  formato y diff verificados. Evidencia consolidada en
  `artifacts/agent-checks/2026-11-13-rf003-14-15-result.json`.

Sin migraciones ni configuración privada nuevas. No se repitió la suite completa:
se conserva el intento diario previo interrumpido sin resultados. T003-16 a
T003-20 siguen abiertas; no se da por completa la matriz transversal ni la
verificación final de RF-003.

**Implementación de T003-16/17, 16 de noviembre de 2026:**

- OpenAPI completa formatos, valores por defecto, precondiciones y cabeceras
  Cache-Control/Pragma/WWW-Authenticate/Retry-After/Location/ETag, manteniendo
  Basic, OAuth de dispositivo y Bearer de Administrador separados. El error
  OAuth documenta error_description opcional. No se publican metadatos OAuth
  anónimos; Swagger/OpenAPI siguen opt-in, también en el ejemplo de configuración.
- CORS conserva orígenes/métodos explícitos y expone Location y Retry-After junto
  con ETag. Pruebas verifican preflight administrativo y origen no autorizado.
- Meter GarajeMetria.Lpr: contador de intentos y duración, con etiquetas acotadas
  traffic/outcome/status_code/plate_status. Incluye rechazos anteriores al handler
  y no etiqueta identificadores, matrícula, IP o secreto. Log de finalización
  seguro y correlacionado; se comprueba la ausencia de payloads/credenciales.
- El diagnóstico SQL verifica los siete almacenes LPR/OAuth con proyecciones de
  cero filas; no basta conectar. La prueba renombra/restaura un store únicamente
  en la base SQL temporal y comprueba error/recuperación sin detalles internos.
- Barreras SQL deterministas entre dos hosts: duplicado HTTP bloqueado sobre
  catálogo, expiración durante espera y ambos órdenes de publicación/ingesta.
  La emisión pausada conserva versión previa frente a rotación, revocación o
  desactivación/reactivación de taller. Commit de rotación sin acuse: 500 seguro,
  GET/ETag y nueva rotación recuperan acceso sin recuperar el secreto perdido.
- Nuevas comprobaciones focalizadas: 12 casos distintos correctos, sin fallos ni
  omitidos, en `artifacts/agent-checks/2026-11-16-rf003-16-17-*`. Restore con
  NuGet Audit de todo el grafo sin avisos informados; no se cambian dependencias.
  La suite completa diaria se registra en `artifacts/agent-checks/full-suite.json`.
- Suite completa del 16 de noviembre: 579 correctas, cero fallidas y una omitida
  (LDAPS real no habilitado): Domain 179, Application 111, Architecture 14 e
  Integration 275 correctas/una omitida. Se ejecutó una sola vez, con cuatro TRX
  en `artifacts/agent-checks/2026-11-16-full`; no se presenta como verificación AD real.
- Build Release sin errores ni advertencias, formato y diff verificados.
  Evidencia consolidada local: `artifacts/agent-checks/2026-11-16-rf003-16-17-result.json`.

Sin migraciones, cambios a configuración privada ni despliegues. T003-18 a
T003-20 siguen pendientes, además de las verificaciones externas AD/cámara/backup.

**Implementación de T003-18/19, 16 de noviembre de 2026:**

- [Runbook operativo](../runbooks/lpr-event-ingestion.md) con preparación privada,
  certificados/TLS, migración, custodia y recuperación ante pérdida de secreto,
  reactivación, errores, cola externa y límites temporales de recuperación.
- [Colección Postman](../http/RF-003.postman_collection.json) sin secretos ni
  respuestas guardadas. Usa Vault y alias locales; las variables exportables
  contienen solo configuración pública e identificadores. Incluye 22 peticiones
  desde bootstrap/login HTTP hasta desactivación final, con aserciones de estado
  y recibo. Solo ejecución manual/Runner compatible con Vault; no Newman/CLI.
- Dos pruebas nuevas correctas, sin fallos ni omitidas. Las 22 plantillas HTTP
  se ejecutaron con WebApplicationFactory, SQL Server real temporal, migraciones
  y directorio sintético: dos eventos, seis auditorías, recibo original preservado
  y dispositivo final inactivo/sin credenciales. El harness no ejecuta scripts
  de Postman ni Vault; esos scripts pasan comprobación sintáctica con Node.
- Build Release sin errores ni advertencias; formato y diff verificados.
  Se reutiliza el restore auditado y la suite completa diaria anterior (579
  correctas, cero fallidas, una omitida LDAPS). No se repite la suite completa.
- Evidencia segura local: `artifacts/agent-checks/rf003-runbook-flow.json`, TRX
  en `artifacts/agent-checks/2026-11-16-rf003-18-19` y consolidación en
  `artifacts/agent-checks/2026-11-16-rf003-18-19-result.json`.

T003-20 permanece abierta. La ejecución nativa en Postman, el emisor físico,
el AD corporativo y el backup/restore físico no se presentan como verificados.
No hay cambios de dependencias, migraciones nuevas ni despliegues en T003-18/19.

## 10. Trazabilidad automatizada de T003-17

La matriz identifica pruebas existentes y nuevas, ejecutadas con datos sintéticos.
Dos hosts WebApplicationFactory tienen contenedores/scopes separados y comparten
SQL Server real; las señales SQL evitan depender de sleeps para forzar carreras.

| CP | Evidencia automatizada representativa |
| --- | --- |
| CP-001 | LprHttpTests: ClientCredentialsUsesProtectedTokensSeparateFromUsersWithoutRefreshCookiesOrSessionReplacement; LprConfigurationTests. |
| CP-002 | LprHttpTests: AdministrativeLimitsQueriesAndPermissionsAreExplicit, EventBoundaryEnforcesDeviceIdentitySizesMediaAndDuplicateQuotas; LprContractsTests. |
| CP-003 | LprHttpTests: RotationRevocationAndDeactivationInvalidateTokensInAnotherHostImmediately, CredentialChangeAfterGrantPreparationNeverUpgradesTheAlreadyVerifiedAuthorizationVersion. |
| CP-004 | LprHttpTests: CatalogPublicationAndIngestionRespectTheCommitOrderAndNeverReviveOldTokens (ambos órdenes), CatalogReactivationRequiresANewTokenInAnotherHostButPreservesTheDeviceSecret, carrera catálogo/emisión. |
| CP-005 | LprDeviceTests, LprPersistenceTests y AdminRoutesPreserveEtagsSecretsPaginationAndDeviceLifecycle; FKs/historial y ausencia de DELETE/traslado. |
| CP-006 | LprEventTests; LprIngestionTests; EventEndpointPersistsInvalidLowConfidenceReadingsAndConfirmsOldEquivalentRetries. |
| CP-007 | OverlappingHttpDeliveriesWaitOnTheRealCatalogLockAndProduceOneEvent; IngestionPreservesInvalidAndLowConfidenceReadingsAndConfirmsOldSemanticRetries; ForeignUuidConflictsWithoutLeakingTheReceiptAndRevokedDevicesCannotConfirmDuplicates. |
| CP-008 | LprEventTests: ThirtyMinuteWindowIncludesExactBoundariesAndRejectsOneTickBeyond y WindowTolerancesAreIndependentAndDoNotOverflowAtDateTimeLimits; ReceiveLprEventTests; EventNumbersAreConvertedExactlyAndOffsetsAndOlderUuidVersionsAreAccepted. |
| CP-009 | LprIngestionTests: FailedOrUnacknowledgedCommitNeverReturnsSuccessAndAnExplicitRetryReconciles; EventErrorsAreSafeAndAnUnacknowledgedCommitCanBeReconciledOverHttp. |
| CP-010 | SpanishPlateClassifierTests: familias, prefijos, normalización, ambigüedad, originales y Unicode. Se mantienen las limitaciones históricas documentadas. |
| CP-011 | EventJsonRejectsMalformedOrInexactValuesBeforePersistence; EventBoundaryEnforcesDeviceIdentitySizesMediaAndDuplicateQuotas; LprEventTests. |
| CP-012 | UnacknowledgedRotationIsRecoveredThroughFreshEtagAndAnotherRotationWithoutRecoveringTheLostSecret; MetricsAndLogsDescribeOutcomesWithoutIdentityPayloadOrSecretLabels; pruebas de DTOs/logs en LprContractsTests. |
| CP-013 | PreconditionsRejectMissingWeakWildcardOrMultipleEtags; LprAdministrationAndQuotaTests: FailedRotationRestoresSecretDeviceVersionAndAudit, NoOpStaleVersionAndMissingCatalogDoNotGenerateSecretsOrExtraAudits. |
| CP-014 | LprHttpTests: Basic/form, grants/scopes, medios/tamaños, cuotas y errores seguros; OpenApiAndCorsDescribeEveryLprContractWithoutPublishingAnonymousOAuthMetadata. |
| CP-015 | TokenExpiringWhileWaitingForTheCatalogLockCannotPersistAnEvent; ClientQuotaCountsRejectedSecretsAndSqlFailureNeverIssuesOrAcceptsAToken; LprConfigurationTests; SqlHealthRejectsAMissingOAuthStoreWithoutExposingSchemaOrConnectionDetails. |
| CP-016 | LprRetentionTests: elegibilidad/margen/frontera, propietarios concurrentes, lease vencido, cancelación y ausencia de cascadas; regresiones SqlStoreTests y UserAdministrationTests. |
| CP-017 | LprAdministrationAndQuotaTests: QuotasAreSharedByTwoHostsAndIsolatedFromUserAuthenticationPolicies, ExactQuotaWindowMismatchAndUnavailableSqlFailClosed, SharedCleanupDoesNotDeletePermitsInsideTheLongerLprWindow; EventBoundaryEnforcesDeviceIdentitySizesMediaAndDuplicateQuotas. |
| CP-018 | ArchitectureTests; LprMigrationTests/LprPersistenceTests y regresiones de asignación/catálogo, SQL Server aislado. |


## 11. Cierre de T003-20

Cierre del backend RF-003: 16 de noviembre de 2026. T003-01 a T003-20
completadas para este alcance; no quedan tareas de implementación de este plan
abiertas. Se reconciliaron RF-003, DESIGN-003, catálogo, arquitectura y la extensión
de DESIGN-009 con el código y los resultados locales. El
[runbook](../runbooks/lpr-event-ingestion.md) y la
[colección HTTP](../http/RF-003.postman_collection.json) constituyen la guía de operación.

### Contratos y versiones entregados

| Elemento | Entrega verificada |
| --- | --- |
| Plataforma | SDK fijado 10.0.401 con latestPatch, C# 14, .NET 10, EF Core/SQL Server 10.0.12 y OpenIddict 7.7.2. Único motor verificado: SQL Server. |
| Administración | Ocho rutas bajo `/api/lpr/devices`, Bearer de Administrador, ETag/If-Match en mutaciones, asociación inmutable y sin DELETE. Secreto solo al generar; auditoría atómica. |
| Emisión | POST `/connect/token`, HTTPS, Client Credentials, Basic o formulario, scope `lpr.events.write`, token firmado/cifrado sin refresh ni sesión de usuario; vigencia inicial 15 minutos. |
| Autorización | Estado de dispositivo, taller, entrada de token y versión SQL en cada uso; invalidación inmediata y coordinación transaccional con catálogo RF-009. |
| Ingesta | POST `/api/lpr/events`, un JSON snake_case sin imagen, máximo 8 KiB y profundidad 4; confidence exacta hasta ocho decimales. 201 nuevo, 200 duplicado con recibo original, 409 conflicto. |
| Identidad y tiempo | UUID global respaldado por PK; comparación semántica/ordinal y UTC. Ventanas inclusivas iniciales de ±30 minutos solo para eventos nuevos; reintentos siguen sujetos a autorización vigente. |
| Matrículas | Clasificador sintáctico versionado `es-v1`; familias/prefijos y compatibilidad histórica de DESIGN-003 / 5. Conserva original, inválidas y baja confianza; no acredita existencia ni exhaustividad administrativa. |
| Persistencia | Migración aditiva `20261113115955_LprPersistence`, FKs restrictivas, FK compuesta dispositivo/taller, transacciones y locks ordenados. Commit incierto sin falso éxito ni repetición ciega. |
| Retención y operación | Eventos/dispositivos sin borrado; auditoría y metadatos elegibles de tokens con supervisor/lease compartido. Logs seguros, métricas acotadas, diagnóstico SQL y OpenAPI opt-in. |

### Reconciliación CA → CP → resultado

Fuente F: cuatro TRX reales en `artifacts/agent-checks/2026-11-16-full`,
con los nombres de pruebas de la sección 10. Todos los selectores de la matriz
se contrastaron con resultados Passed; ningún CP depende de la prueba LDAPS
omitida. La consolidación local `2026-11-16-rf003-20-result.json` conserva por
CP los selectores, resultados y rutas TRX. Cada fila es una selección trazable,
no una medida de cobertura de código; sus recuentos se solapan y no se suman.

| CP | CA cubiertos | Casos contrastados en F | Estado |
| --- | --- | --- | --- |
| CP-001 | CA-001, CA-003, CA-013 | 22 | Verificado en el alcance automatizado |
| CP-002 | CA-001, CA-002 | 3 | Verificado en el alcance automatizado |
| CP-003 | CA-004, CA-014 | 4 | Verificado en el alcance automatizado |
| CP-004 | CA-004, CA-015 | 4 | Verificado en el alcance automatizado |
| CP-005 | CA-005, CA-015 | 3 | Verificado en el alcance automatizado |
| CP-006 | CA-006, CA-010 | 2 | Verificado en el alcance automatizado |
| CP-007 | CA-007, CA-008, CA-018 | 3 | Verificado en el alcance automatizado |
| CP-008 | CA-009 | 7 | Verificado en el alcance automatizado |
| CP-009 | CA-011 | 3 | Verificado en el alcance automatizado |
| CP-010 | CA-012 | 116 | Verificado en el alcance automatizado |
| CP-011 | CA-012, CA-017 | 22 | Verificado en el alcance automatizado |
| CP-012 | CA-014, CA-015, CA-016 | 2 | Verificado en el alcance automatizado |
| CP-013 | CA-016 | 7 | Verificado en el alcance automatizado |
| CP-014 | CA-001, CA-013, CA-017 | 7 | Verificado en el alcance automatizado |
| CP-015 | CA-004, CA-011 | 24 | Verificado en el alcance automatizado |
| CP-016 | CA-016 | 5 | Verificado en el alcance automatizado |
| CP-017 | CA-010, CA-017 | 4 | Verificado en el alcance automatizado |
| CP-018 | CA-002, CA-005 | 29 | Verificado en el alcance automatizado |

CP-010 se apoya en 116 casos de SpanishPlateClassifierTests: familias, positivos/
negativos por prefijo provincial/estatal, históricos de tres letras, ambigüedad,
Unicode y normalización. Verifica el catálogo `es-v1` definido; no consulta DGT,
no distingue países con sintaxis coincidente ni prueba todas las asignaciones
históricas posibles. Las lecturas no reconocidas permanecen conservadas invalid.

Como comprobación operativa adicional, los dos casos de LprRunbookTests pasaron
en `artifacts/agent-checks/2026-11-16-rf003-18-19`: 22 plantillas HTTP, dos eventos,
seis auditorías y dispositivo final inactivo/sin credenciales. Sus scripts solo
se comprobaron sintácticamente; el harness HTTP no ejecuta Postman ni Vault.

### Verificación reutilizada y límites abiertos fuera del tramo

- Última suite completa: 16 de noviembre, inicio UTC 08:29:21 y fin 08:34:57;
  579 correctas, cero fallidas y una omitida (LDAPS externo no habilitado).
  El registro `artifacts/agent-checks/full-suite.json` conserva comando y cuatro
  TRX. Las dos pruebas nuevas de runbook se ejecutaron después por separado;
  no se presentan como parte de esa suite ni se inventa una segunda ejecución.
- T003-19 acreditó build Release con cero errores/advertencias, formato y diff
  correctos, comprobación sintáctica de scripts y restore auditado reutilizado
  de T003-16/17. Evidencia: `2026-11-16-rf003-18-19-result.json`.
- T003-20 solo modifica documentación. Se revisan enlaces locales, consistencia
  de CA/CP, casillas y diff; no procede repetir restore/build/format o tests de
  aplicación. La suite completa se reutiliza conforme a la frecuencia diaria
  de AGENTS.md, no queda pendiente por fallo ni se requiere otra para este cierre.
- Siguen sin verificar Postman/Vault nativo, cámara/proveedor real, AD corporativo
  y backup/restore físico. No se ha realizado despliegue ni validación de capacidad
  de producción o de otro motor. Estas limitaciones no se marcan como superadas.
- Fuera de RF-003: panel RF-008, imágenes RF-007, historial/consulta RF-005 e
  interpretación de entradas/salidas/estancias RF-004. La cola y retención del
  emisor son externas; RF-003 no las implementa.

Este cierre no aplica migraciones, cambia configuración privada, publica ni
despliega. La evidencia ejecutable queda local e ignorada por Git; para otra
máquina se deben preparar los prerrequisitos y reproducir las pruebas.
