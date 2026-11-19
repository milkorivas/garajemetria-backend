# TASKS-005 — Consulta de vehículos e historial

| Campo | Valor |
| --- | --- |
| Requerimiento | [RF-005](../requirements/RF-005-vehicle-history-and-search.md), prioridad Media; consulta de eventos LPR como prioridad operativa |
| Diseño | [DESIGN-005](../design/DESIGN-005-vehicle-history-and-search.md) |
| Coordinación | RF-001/RF-002 (sesiones, permisos y asignaciones), RF-003 (eventos), RF-004 (proyección, ubicación y cierre), RF-009 (catálogo confirmado) |
| Fecha | 19 de noviembre de 2026 |
| Estado | T005-01 a T005-24 completadas para el backend; 202 casos relevantes correctos; límites externos en sección 10.3 |

RN, CA y CP pertenecen a RF-005; los casos están definidos en RF-005 / 7 y
desarrollados técnicamente en DESIGN-005 / 12. P005-01 y P005-02 están resueltas:
auditoría sin borrado automático y fallo cerrado con 503 sin entregar datos.
La retención definitiva aplazada no impide implementar esa política actual.

Los identificadores de tarea son estables y las dependencias expresas definen
el orden. Una casilla solo se completa con entregable, verificaciones realmente
ejecutadas y evidencia. Este plan no completa tareas, no ejecuta pruebas y no
autoriza commit, publicación, despliegue ni migraciones en producción.

## 1. Alcance, base y prerrequisitos

Implementar las cuatro consultas, detalles y selectores autorizados, salto de
página con total exacto, actualización manual, recorrido entre talleres, Excel
acotado y auditoría persistente. Reutilizar el cálculo de RF-004; no introducir
reglas de entrada/salida nuevas, correcciones RF-006, imágenes RF-007, UI RF-008,
datos maestros de vehículos ni borrado de eventos o auditoría.

Inspeccionar estado Git e instrucciones aplicables antes de cada lote y conservar
cambios existentes. Mantener C# 14/.NET 10, SDK compatible fijado en `global.json`
y límites de arquitectura: DTOs/puertos en Application, SQL/EF/Open XML en
Infrastructure y endpoints sin DbContext. No introducir dependencias de motor,
HTTP o Excel en Domain/Application ni exponer `IQueryable`.

Reutilizar `AuthenticationDbContext`, `Outcome<T>`, sesión RF-001,
`ICurrentUserContext`, asignación local, `ILprClock`, `IWorkshopCalendar`,
`CloseStayDayCommand` e infraestructura de locks de RF-004. Los lectores internos
`IStayObservationQuery`/`IObservationAuditQuery` no son buscadores públicos ni
auditoría de acceso. Confirmar contratos reales al comenzar, sin renombrar el
contexto ni rehacer componentes terminados.

Prerrequisitos: SDK fijado, SQL Server aislado, `Atlantic/Canary` disponible,
configuración privada y datos sintéticos. Reutilizar `AuthenticationFixture`,
`GARAJEMETRIA_TEST_SQL` y `WebApplicationFactory` cuando corresponda, comprobando
sus mecanismos efectivos antes de usarlos. No exigir AD corporativo, cámara real,
fuente corporativa definitiva ni cierre integral de otros TASKS: se requieren
las capacidades concretas de sesiones, catálogo local y proyección disponibles.
Permisos de creación/eliminación de bases exclusivamente sobre recursos de
prueba identificados; nunca usar una conexión de producción.

## 2. Preparación, contratos y persistencia

- [x] **T005-01 — Preparar inventario y entorno reproducible.**
  Dependencias: ninguna.
  Localizar contratos, endpoints, mappings, validadores, fixtures, DI y cuotas
  existentes; registrar cambios previstos y componentes reutilizados. Preparar
  reloj controlado, usuarios de ambos roles, varios talleres/dispositivos,
  inactivos, lecturas inválidas y barreras para dos hosts/contextos independientes.
  Cierre: preparación reproducible documentada y selección de pruebas reales
  prevista por tarea; SQL aislado comprobado sin exponer conexión ni secretos.
  Consultar historial diario antes de decidir verificaciones de desarrollo.
  Trazabilidad: base para CA-001 a CA-017; DESIGN-005 / 1 y 12.

- [x] **T005-02 — Definir contratos, DTOs y validación Application.**
  Dependencias: T005-01.
  Crear `Application.History`, queries, `ExportHistoryCommand`, puertos de
  lectura/cierre/auditoría/Excel y resultados según DESIGN-005 / 2 y 3.
  Incluir scope confiable, página con total de 64 bits, resolución de identidad,
  timeline discriminada, procedencia de ubicación y duración nullable. Validar
  con FluentValidation arrays de hasta 100 IDs, texto 1..32, enums, exclusiones,
  confianza `[0,1]`, precisión y parámetros específicos por consulta. DTOs y
  solicitudes con representación segura para evitar logging accidental.
  Cierre: unitarias de límites y combinaciones sin persistencia; dependencias
  de Application limpias y cancelación en todos los contratos asíncronos.
  No hacer opcional la confianza de ingesta ni convertir cero en ausencia.
  Trazabilidad: CA-003 a CA-008, CA-010 a CA-013, CA-015 a CA-017.

- [x] **T005-03 — Resolver autorización y ámbito confiable.**
  Dependencias: T005-02.
  Implementar `HistoryAccess`: sesión activa, rol y taller asignado desde datos
  confiables; selección Admin de uno/varios/todos incluyendo inactivos. Asesor
  queda restringido a su taller activo; IDs de talleres ajenos producen 403,
  recursos individuales no visibles 404 y dependencia indisponible 503.
  Revalidar en lectura y antes de auditoría/entrega, también tras generar Excel.
  Cierre: unitarias con stubs de sesión/asignación y casos de cambio de taller,
  sesión terminada, rol doble resuelto según RF-002 y denegación a dispositivos.
  No reutilizar el gate exclusivo de Administrador ni confiar en filtros.
  Trazabilidad: CA-001, CA-013, CA-014, CA-017.

- [x] **T005-04 — Normalizar calendario y semántica temporal.**
  Dependencias: T005-02.
  Implementar ventana `[inicio,fin)` y valores predeterminados: día actual para
  eventos/estancias/timeline; presencia al instante de consulta sin período.
  Fechas completas canarias o instantes con offset, nunca mezcla. Máximo de
  un año por calendario local con ajuste de 29 de febrero según DESIGN-005 / 5.
  Extender el puerto de calendario solo si es necesario, con conversiones de
  zona en Infrastructure. Centralizar selección por solapamiento como decisión
  Application, incluidas abiertas, pendientes, puntos y capturas futuras.
  Cierre: pruebas deterministas de DST, límites exactos y un tick fuera,
  366 días, entrada anterior al filtro, duración cero, falta de entrada y
  duración futura no negativa. Traducción SQL se comprobará en T005-08.
  Trazabilidad: CA-005, CA-007 a CA-010.

- [x] **T005-05 — Crear mappings y migración aditiva de consulta/auditoría.**
  Dependencias: T005-02.
  Añadir `HistoryAccessAudits` y `HistoryAccessAuditWorkshops` con relaciones
  restrictivas, longitudes, precisión UTC e índices de DESIGN-005 / 10.1.
  Preparar clave de orden canónica de estancia y candidatos de índices según
  sección 7, evitando redundancias y posponiendo ajustes finales a mediciones.
  Generar migración/snapshot nuevos y script revisable sin alterar migraciones
  previas ni eventos originales. Sin tablas de filtros sensibles ni limpieza.
  Cierre: SQL aislado verifica PK/FK, unicidad, identidad ordinal, precisión,
  restricciones y conservación de datos al migrar desde esquema RF-004.
  No modificar nulabilidad de confianza RF-003.
  Trazabilidad: CA-001, CA-004, CA-012, CA-014, CA-017.

- [x] **T005-06 — Implementar unidad de lectura coherente.**
  Dependencias: T005-03, T005-05.
  Implementar `IHistoryReadSession` con transacción SQL `Serializable`, reloj
  compartido y total/filas de la misma lectura. Revalidar ámbito; no tomar por
  defecto el bloqueo exclusivo global de `SqlTransactions.ExecuteAsync` ni usar
  `NOLOCK`. Cerrar transacción tras materializar, antes de Excel/auditoría/envío.
  Cierre: integración con barreras y conexiones separadas demuestra coherencia
  bajo inserción/recálculo concurrente; timeout/deadlock no entrega datos
  parciales, rollback y disposal correctos. No usar el DbContext concurrentemente.
  Trazabilidad: CA-001, CA-012, CA-013, CA-015.

## 3. Lectores, cierres y consultas Application

- [x] **T005-07 — Implementar búsqueda y detalle de eventos LPR.**
  Dependencias: T005-04, T005-06.
  Crear proyecciones sobre eventos con left joins de ubicación y asociación,
  ámbito en raíz y joins, filtros completos y clasificación persistida.
  Matrícula exacta/parcial normalizada; original ordinal literal con escapes
  sin comodines involuntarios. Captura/recepción como eje de filtro y captura
  como orden; UUID canónico estable. Total exacto, páginas 50/200 y salto sin
  overflow. Representar confianza ausente sin fabricar registros nulos en SQL.
  Cierre: integración de válidas/invalidas/repetidas/intermedias/sin asociación,
  `%`, `_`, `[`, ceros y separadores; confianza cero y filtro missing vacío con
  esquema actual; filtros combinados y capturas simultáneas sin filas duplicadas.
  Trazabilidad: CA-002 a CA-007, CA-010, CA-012, CA-014.

- [x] **T005-08 — Implementar lectura de estancias y presencia.**
  Dependencias: T005-04, T005-06.
  Proyectar solo identidades vigentes y filtrar según semántica Application de
  solapamiento, estados y fuentes. Duración al único `asOfUtc`, motivos de
  indisponibilidad y estimación diferenciada; ordenar extremos y clave canónica.
  Presencia usa estado abierto vigente, sin incluir pendientes ni cerradas
  estimadas. El lector no sustituye la preparación de cierre de T005-09.
  Cierre: integración de equivalencia entre reglas Application y SQL,
  ventanas abarcadas, extremos de frontera, puntos, abiertas, pendientes y
  capturas adelantadas. Listado sin identidades absorbidas ni duración persistida creciente.
  Trazabilidad: CA-008 a CA-010, CA-012, CA-014.

- [x] **T005-09 — Coordinar cierres lógicos antes de consultar.**
  Dependencias: T005-03, T005-04, T005-06.
  Implementar descubrimiento paginado de trabajo vencido autorizado y
  `HistoryClosureService` mediante `CloseStayDayCommand`. Preparar fuera de
  la transacción de lectura y volver a comprobar dentro con el `asOfUtc`
  definitivo; repetir solo dentro del presupuesto. Incluir abiertas antiguas,
  detalle y asociación, filtro por recepción y lecturas sin estancia sin
  inventar particiones para inválidas. Respetar catálogo → partición de RF-004.
  Cierre: pruebas unitarias de orquestación e integración SQL tras medianoche,
  worker retrasado y nueva ingesta durante preparación. Presupuesto agotado:
  503 `history_projection_pending` y Retry-After, sin ocultar filas vencidas.
  Sin transacciones anidadas, proyección inicial administrativa o fire-and-forget.
  Trazabilidad: CA-002, CA-005, CA-008 a CA-010, CA-013.

- [x] **T005-10 — Resolver detalle de estancia, observaciones y linaje.**
  Dependencias: T005-07, T005-08, T005-09.
  Resolver ID vigente/absorbido en su partición y ámbito; informar requested y
  resolved ID, sin redirección cacheable. No redirigir una división a todas
  sus partes. Paginar todas las observaciones actuales sin heredar filtros del
  listado ni recortar al año de búsqueda. Detectar ciclos/inconsistencias
  con error seguro, sin exponer documentos internos de revisión.
  Cierre: integración SQL de fusión encadenada, división, ID ajeno/inexistente,
  observaciones intermedias y volumen que supere una página sin truncamiento.
  Trazabilidad: CA-001, CA-002, CA-010, CA-012, CA-013.

- [x] **T005-11 — Construir recorrido mixto por matrícula exacta.**
  Dependencias: T005-07, T005-08, T005-09.
  Combinar observaciones y estancias autorizadas en una cronología materializada
  y paginada, ordenada por captura/entrada/salida disponible, tipo y UUID
  canónico según DESIGN-005 / 7. Filtrar matrícula exacta y período acotado;
  no deduplicar estancia contra evento ni inferir viajes o tiempos de traslado.
  Cierre: integración de varios talleres, capturas iguales y cambios de orden;
  total cuenta elementos y Asesor nunca accede a talleres ajenos.
  Trazabilidad: CA-001, CA-007, CA-011, CA-012, CA-014.

- [x] **T005-12 — Implementar selectores históricos autorizados.**
  Dependencias: T005-03, T005-06.
  Crear proyecciones mínimas de dispositivos y versiones de ubicación, con
  búsqueda acotada, paginación y orden estable. Identificar ubicación por ID
  histórico, descripción, dispositivo y vigencia, incluidos inactivos y nombres
  repetidos. No exponer endpoints administrativos completos al Asesor.
  Cierre: integración de versiones anteriores, dispositivos desactivados,
  ámbito múltiple/Admin y ausencia de filtración por totales o nombres ajenos.
  Trazabilidad: CA-001, CA-004, CA-012, CA-014.

- [x] **T005-13 — Persistir auditoría de liberación de acceso.**
  Dependencias: T005-03, T005-05, T005-06.
  Implementar `IHistoryAccessAuditStore`: actor, instante, operación, correlación,
  filas y conjunto efectivo de talleres, también sin resultados y en ámbito
  «todos». Guardar acceso y talleres atómicamente, sin búsqueda/URL/payloads.
  Sin borrado automático ni participación en la retención de sesiones.
  Cierre: integración de commit, fallo previo y commit incierto; 503 sin
  liberar resultado cuando no hay confirmación, sin afirmar rollback ni
  reintentar ciegamente. ID estable de operación y recuperación controlada
  sin duplicados. El registro acredita liberación autorizada, no descarga.
  Trazabilidad: CA-001, CA-017; P005-01 y P005-02.

- [x] **T005-14 — Integrar handlers y registrar dependencias.**
  Dependencias: T005-07 a T005-13.
  Unir validación, autorización, preparación de cierre, lectura coherente,
  revalidación y auditoría antes de devolver DTOs; incluir listado vacío,
  detalle y selectores. Registrar scoped/transient según responsabilidades.
  Reutilizar errores `Outcome` y logging seguro; no auditar como entrega una
  petición fallida ni devolver éxito cuando falla auditoría.
  Cierre: unitarias Application con stubs, orden observable de efectos y
  cancelación; pruebas de arquitectura de DI y puertos sin EF/HTTP/SQL.
  Trazabilidad: CA-001 a CA-014, CA-017.

## 4. Transporte, Excel y controles operativos

- [x] **T005-15 — Publicar contratos HTTP de consulta.**
  Dependencias: T005-14.
  Crear Minimal APIs y DTOs camelCase de DESIGN-005 / 3: eventos y detalle,
  estancias/detalle/observaciones, presencia, timeline y selectores. Aplicar
  `HistoryReader`, ProblemDetails, `no-store`, límites de entrada y
  `RequestAborted`. Mantener intacto POST LPR de RF-003 y su esquema de dispositivo.
  Cierre: WebApplicationFactory verifica formas, enums, duplicados escalares,
  campos desconocidos, códigos/headers, página vacía fuera de rango, offsets
  y autorización de cada ruta. Sin lógica de negocio o DbContext en endpoints.
  Trazabilidad: CA-001 a CA-014, CA-017.

- [x] **T005-16 — Implementar escritor Excel seguro y acotado.**
  Dependencias: T005-02.
  Elegir y fijar versión estable del Open XML SDK compatible según gestión
  de paquetes real, solo en Infrastructure. Crear hoja Results y metadatos;
  textos externos como cadenas, ceros iniciales, fechas locales ISO con offset,
  números/duraciones numéricos y ausencias distinguibles. Sin fórmulas, macros,
  enlaces externos o rutas de imágenes. Cancelación, disposal y máximo 32 MiB.
  Cierre: inspección automatizada de Open XML y apertura de muestra sintética;
  signos de fórmula se conservan como datos, DST no ambiguo y fallos liberan
  memoria sin archivo parcial. No requiere Excel/COM en servidor.
  Trazabilidad: CA-015, CA-016.

- [x] **T005-17 — Integrar exportación de las cuatro consultas.**
  Dependencias: T005-14, T005-15, T005-16.
  Implementar command y `POST /api/history/exports` con mismo filtro/orden
  del listado y sin paginación. Leer hasta 10.001 filas coherentes: 10.000
  admitidas y 10.001 rechazadas con 422 antes de generar. Liberar transacción,
  generar, revalidar permisos, auditar una exportación y entregar solo con
  commit confirmado. Nombre genérico, MIME y headers de DESIGN-005 / 9.
  Cierre: HTTP/SQL de cuatro kinds, varias páginas, exceso, cuerpo >32 KiB,
  tamaño de salida, fallo de escritor, revocación durante generación y fallo
  de auditoría sin bytes XLSX ni respuesta parcial. Sin archivo permanente.
  Trazabilidad: CA-001, CA-013, CA-015 a CA-017.

- [x] **T005-18 — Aplicar opciones, cuotas, concurrencia y timeouts.**
  Dependencias: T005-02, T005-05.
  Añadir `HistoryOptions` y validación de arranque con valores de DESIGN-005 /
  10.2: páginas 50/200, 10.000 filas, cuerpo 32 KiB, salida 32 MiB, consulta
  30 s, exportación 60 s y SQL 15 s acotado por presupuesto restante.
  Cuotas 60 consultas/minuto y 5 exportaciones/minuto por usuario; 1 exportación
  concurrente por usuario y 2 por instancia. Integrar gates antes de trabajo
  costoso en T005-15/17 y cuotas compartidas/leases con expiración cuando aplique.
  Cierre: configuración inválida rechazada; dos hosts verifican límite por
  usuario, cancelación/disposal libera admisión y expiración recupera abandono;
  429/Retry-After y 503 diferenciados. Sin capturar scoped en singleton.
  Trazabilidad: CA-001, CA-012, CA-015, CA-017; controles transversales del diseño.

- [x] **T005-19 — Proteger logs y añadir observabilidad mínima.**
  Dependencias: T005-15, T005-17, T005-18.
  Registrar métricas por operación/resultado, duración, filas, cierres pendientes,
  rechazos y fallo de auditoría, sin etiquetas personales. Logs con plantilla
  de ruta/operación y correlación segura, sin query strings, cuerpos, matrícula
  o DTOs serializados. Revisar health y guía de IIS/proxy/APM para las rutas GET.
  Cierre: capturas de logs sintéticos verifican ausencia de datos sensibles y
  errores no filtran SQL/stack; documentar configuración de alojamiento y
  comprobarla en ensayo aislado si está disponible. Un hosting no probado
  queda como pendiente externo explícito, nunca como garantía satisfecha.
  Trazabilidad: CA-001, CA-017.

## 5. Verificación transversal y entrega documental

- [x] **T005-20 — Verificar seguridad HTTP, arquitectura y regresiones.**
  Dependencias: T005-19.
  Consolidar pruebas focalizadas de rol/ámbito por ruta, filtro, total, selector,
  linaje y Excel; IDs ajenos/inexistentes indistinguibles; revocación y cambio
  de taller. Comprobar `no-store`, errores y cero entrega tras fallo de auditoría.
  Ampliar architecture tests de puertos y dependencias; verificar regresiones
  RF-003/004: confianza obligatoria, duplicados idempotentes, ventanas, ubicación
  y cálculo/políticas históricas intactos.
  Cierre: resultados reales registrados, sin incluir como éxito pruebas omitidas
  por falta de SQL. Añadir nuevas pruebas desde las tareas previas, no esperar
  a esta tarea para diseñarlas o ejecutarlas.
  Trazabilidad: CA-001 a CA-017; foco CA-001, CA-014, CA-017.

- [x] **T005-21 — Verificar coherencia, concurrencia y fallos.**
  Dependencias: T005-14, T005-17, T005-18.
  Usar SQL real, barreras y dos hosts para total/página durante ingesta,
  medianoche entre preparación y lectura, cierre simultáneo con worker,
  fusión/división durante navegación, revocación durante Excel y cuotas.
  Inyectar timeout, cancelación y fallo/commit incierto de auditoría sin
  inventar rollback ni duplicar reintentos de escritura. Verificar liberación
  de transacciones, conexiones, buffers y leases.
  Cierre: ninguna respuesta parcial o estado vencido como actual; coherencia
  dentro de cada respuesta y cambios entre páginas admitidos según contrato.
  Reloj/barreras deterministas, sin esperar medianoche real ni sleeps arbitrarios.
  Trazabilidad: CA-001, CA-009, CA-012, CA-013, CA-015, CA-017.

- [x] **T005-22 — Ensayar migración y carga sintética; ajustar índices.**
  Dependencias: T005-20, T005-21.
  Ensayar nueva base y actualización desde RF-004 con eventos/asociaciones/
  linaje conservados. Medir un año, todos los talleres, contains, filtro por
  recepción, páginas profundas, total exacto y 10.000 filas junto a ingesta/
  cierre. Registrar volúmenes, entorno, latencias, planes, bloqueos y memoria;
  justificar índices finales o retirada de candidatos sin evidencia útil.
  Cierre: script revisado y resultados reproducibles, rollback operativo que
  conserva auditoría. No ejecutar Down destructivo sobre historia real ni
  afirmar SLA, soporte de proveedor adicional o rendimiento productivo.
  Si cambia código/índices, repetir solo escenarios afectados y actualizar diseño.
  Trazabilidad: CA-002, CA-004, CA-005, CA-009, CA-012, CA-015, CA-017.

- [x] **T005-23 — Completar OpenAPI, ejemplos y runbook operativo.**
  Dependencias: T005-22.
  Documentar todas las rutas, filtros/defaults/límites, orden, errores, zona,
  páginas/totales, IDs absorbidos y descarga binaria. Crear ejemplos HTTP y
  preparación reproducible con datos sintéticos; runbook de permisos,
  medianoche, fallos de auditoría, exportación excesiva, cuotas, migración,
  diagnóstico seguro y crecimiento de auditoría sin limpieza automática.
  Cierre: ejemplos ejecutados contra host/SQL aislados y resultado real
  registrado; diferenciar manual de usuario futuro de backend verificado.
  Actualizar requisitos/diseño/arquitectura/catálogo solo según implementación
  efectiva y registrar hosting externo no verificado.
  Trazabilidad: CA-001 a CA-017; P005-01 y P005-02.

- [x] **T005-24 — Consolidar evidencias y cerrar el tramo.**
  Dependencias: T005-23.
  Revisar criterios y matriz CA/CP, resultados focalizados y último intento
  completo conocido conforme a frecuencia diaria. Documentar advertencias,
  omisiones y riesgos, estados de migración y límites externos. Inspeccionar
  diff para cambios ajenos, secretos o modificaciones de alcance involuntarias.
  Cierre: criterios satisfechos con evidencia, arquitectura preservada, sin
  nuevas advertencias y documentación coherente. Si falta una verificación
  requerida, registrar pendiente y no marcar esa tarea/criterio como completado
  ni RF-005 como verificado integralmente. No exige otra suite completa ese día.
  Trazabilidad: CA-001 a CA-017 y matriz siguiente.

## 6. Hitos y trazabilidad CA/CP

| Hito | Tareas requeridas | Condición de revisión |
| --- | --- | --- |
| Base implementable | T005-01 a T005-06 | Contratos, seguridad, calendario, esquema y sesión de lectura verificados en su ámbito. |
| Consultas Application | T005-07 a T005-14 | Proyecciones, cierres, detalle, recorrido y auditoría coherentes; eventos como primer lector. |
| Backend integrado | T005-15 a T005-19 | HTTP/Excel con límites, cuotas, fallo cerrado y diagnóstico seguro. |
| Verificación y operación | T005-20 a T005-23 | Seguridad, concurrencia, carga/migración y documentación con evidencias reales. |
| Cierre | T005-24 | Matriz reconciliada y límites expresos sin resultados inventados. |

Los hitos no sustituyen las dependencias de cada tarea ni fijan fechas de
entrega. T005-16/18 pueden avanzar con sus contratos sin esperar lectores
terminados; no autorizan crear subagentes ni usar simultáneamente un DbContext.

| CA | CP de RF-005 / 7 | Implementación principal | Verificación transversal |
| --- | --- | --- | --- |
| CA-001 | CP-001 | T005-03, T005-06, T005-10 a T005-15, T005-17 | T005-20, T005-21 |
| CA-002 | CP-002 | T005-07, T005-09, T005-10 | T005-20, T005-22 |
| CA-003 | CP-003 | T005-02, T005-07 | T005-20 |
| CA-004 | CP-004 | T005-05, T005-07, T005-12 | T005-20, T005-22 |
| CA-005 | CP-005 | T005-04, T005-07, T005-09 | T005-20, T005-22 |
| CA-006 | CP-006 | T005-02, T005-07 | T005-20 |
| CA-007 | CP-007 | T005-04, T005-07, T005-11 | T005-20 |
| CA-008 | CP-008 | T005-04, T005-08, T005-09 | T005-20 |
| CA-009 | CP-009 | T005-04, T005-08, T005-09 | T005-20, T005-21, T005-22 |
| CA-010 | CP-010 | T005-04, T005-07, T005-08, T005-10 | T005-20 |
| CA-011 | CP-011 | T005-03, T005-11 | T005-20 |
| CA-012 | CP-012 | T005-02, T005-06 a T005-08, T005-10 a T005-12 | T005-20 a T005-22 |
| CA-013 | CP-013 | T005-03, T005-09, T005-10, T005-14, T005-17 | T005-20, T005-21 |
| CA-014 | CP-014 | T005-03, T005-07, T005-08, T005-11, T005-12 | T005-20 |
| CA-015 | CP-015 | T005-16 a T005-18 | T005-20 a T005-22 |
| CA-016 | CP-016 | T005-16, T005-17 | T005-20 |
| CA-017 | CP-017 | T005-03, T005-05, T005-13 a T005-15, T005-17 a T005-19 | T005-20 a T005-23 |

T005-23/24 consolidan todos los criterios, sin sustituir los casos focalizados.
El rastro HTTP pendiente de RF-004 / CA-015, CA-019 y CA-022 debe reconciliarse
en su documentación cuando RF-005 lo pruebe; este plan no los declara resueltos.

## 7. Estrategia y frecuencia de verificación

Añadir pruebas proporcionales desde cada tarea. Domain/Application: reloj
determinista y stubs sin red/DB; no duplicar en Domain reglas ya probadas si
RF-005 no las cambia. SQL Server real aislado: consultas, constraints,
transacciones, orden, collation, migraciones y concurrencia. No EF InMemory ni
SQLite. HTTP con WebApplicationFactory y auth sintética; integración no exige
AD real ni producción. Excel se verifica estructuralmente y con muestra visual,
sin introducir automatización COM como dependencia del backend.

T005-01 concreta clases/filtros de pruebas existentes o a crear. No registrar
un filtro con cero pruebas descubiertas como ejecución satisfactoria. Agrupar
cambios coherentes antes de ejecutar checks focalizados; repetir o ampliar
solo por cambios nuevos, fallos o dudas no resueltas. Restore/build/format se
ejecutan cuando el cambio lo requiera, no de forma automática por cada tarea.

Suite completa: solo por petición explícita o una vez durante la primera sesión
de desarrollo del día si no hubo intento conocido, fecha `Atlantic/Canary`.
Antes consultar historial y `artifacts/agent-checks/full-suite.json`; después
de intento real registrar fecha local, UTC, comando, resultado y recuentos
aprobados/fallidos/omitidos disponibles. Intento fallido también cuenta; resolver
con pruebas afectadas. Otra suite ese día requiere petición explícita.

Comandos raíz de referencia, únicamente cuando proceda la verificación completa:

```powershell
dotnet restore
dotnet build --no-restore --configuration Release
dotnet format --verify-no-changes --no-restore
dotnet test --no-build --configuration Release
```

Adaptar filtros y logger a las herramientas reales, con artefactos ignorados
en `artifacts/agent-checks/` sin información sensible. Una suite previa no
verifica cambios posteriores: indicar fecha/alcance y pruebas focalizadas que
los cubren. El plan documental se revisa por diff, enlaces y consistencia;
no inicia suite, automatización ni trabajo de implementación.

## 8. Registro de ejecución y definición de cierre

Estado al redactar el plan: **0 de 24 tareas completadas**. No se atribuyen resultados de
RF-004 a RF-005. No se ha ejecutado una suite por redactar este plan; el último
intento conocido se consultará antes de comenzar desarrollo, sin inventar
recuentos ni asumir vigente una evidencia de otra sesión.

Para cada tarea completada, añadir entrada con ID, fecha local y UTC, cambios,
archivos relevantes, desviaciones justificadas del diseño, comandos realmente
ejecutados, pruebas concretas y CA/CP cubiertos, resultado/recuentos/omisiones y
ruta de evidencia local. Incluir prerequisitos efectivos, riesgos y pendientes,
sin cadenas de conexión, matrículas reales ni otros secretos. Mientras no se
ejecute, conservar la tarea pendiente aunque exista código parcial.

El cierre debe distinguir backend implementado, criterios verificados,
configuración de hosting ensayada y operación corporativa no realizada.
Producción, AD aislado corporativo y backup/restore físico ajenos a este tramo
no se presentan como verificados. Una limitación externa se describe con su
impacto; no transforma un criterio pendiente en éxito. La autorización para
implementar o desplegar no se infiere de completar este documento.

## 9. Registro de implementación — 19 de noviembre de 2026

Trabajo autorizado por el usuario para RF-005. Fecha local `Atlantic/Canary`;
los artefactos de verificación conservan además timestamp UTC. Se preservan
los cambios anteriores del catálogo y los documentos RF/DESIGN/TASKS-005.
No hay commit, publicación, despliegue o migración productiva.

Los archivos de código nuevos se agrupan en `Application/History`,
`Infrastructure/History`, `Api/History` y los adaptadores `Persistence/History*`.
Los nombres de clase de la tabla son archivos/escenarios reales; los resultados
y comandos se centralizan en sección 10 para no repetir recuentos de pruebas.

| Tarea | Entrega y verificación registrada |
| --- | --- |
| T005-01 | Inventario de sesión, catálogo, proyección y fixtures; `configuration/history-test-preparation.md`, SQL local sintético y reloj controlado. Intento diario y restauración registrados por separado. |
| T005-02 | `Contracts.cs` y `Validation.cs`: queries/command, puertos, DTOs y validación asíncrona común. `HistoryRulesTests` comprueba límites, combinaciones, normalización y ausencia de overflow. |
| T005-03 | `HistoryAccess` resuelve ámbito y revalida sesión/asignación. `HistoryServiceTests` y `HistoryAdvisorIsolationIncludesDetailSelectorsCountsAndExcel` cubren roles, taller ajeno, detalle, totales, selectores y Excel. |
| T005-04 | `HistoryTime`, `HistoryCalendar` e integración con `IWorkshopCalendar`; solapamiento/duración en Application. Pruebas de año bisiesto, puntos, pendientes, abiertas y reloj de consulta; adaptador canario con casos DST propios. |
| T005-05 | `HistoryMappings.cs`, migración `20261119090322_VehicleHistoryQueries`, designer/snapshot y script SQL. Upgrade desde RF-004 conserva originales; Down rechaza auditoría existente. Sin cambios en migraciones anteriores. |
| T005-06 | `HistoryStore.ExecuteAsync`: lectura Serializable, sin lock exclusivo global por defecto; transacción cerrada antes de Excel/auditoría. `SerializableReadPreventsPhantomBetweenCountAndRows` comprueba el bloqueo de una inserción concurrente y su éxito después del cierre. |
| T005-07 | `HistoryQuery`: filtros, original literal, clasificación guardada, left joins, orden canónico, total/salto. Casos HTTP de inválidas/sin estancia, confianza cero/missing, búsqueda parcial, páginas extremas y SQL de captura/recepción/original con espacio final. |
| T005-08 | Proyección de estancias/presencia con solapamiento y fuentes, duración suministrada por Application. HTTP con ingesta real genera dos visitas y solo una presencia; listado excluye absorbidas. |
| T005-09 | Coordinación de cierre dentro de `HistoryService` con descubrimiento paginado relevante y `CloseStayDayCommand`; máximo tres ciclos y presupuesto. La prueba HTTP avanza a después de medianoche sin esperar al worker y confirma presencia vacía. |
| T005-10 | Resolver de linaje y observaciones paginadas; HTTP comprueba ID absorbido/resuelto y enlace a observaciones actuales, con denegación de recursos ajenos. El linaje sintético aísla el resolver del algoritmo de fusión ya probado en RF-004. |
| T005-11 | Timeline mixta de eventos/estancias, total por elementos y matrícula exacta; la prueba con ingesta real devuelve cinco elementos para tres eventos y dos visitas, sin inferir trayectos. |
| T005-12 | Selectores de dispositivos/versiones de ubicación con ámbito y paginación. Corregida traducción SQL de la ordenación cuando la proyección de dispositivo no contiene DeviceId; HTTP y llamada directa al puerto verifican la consulta. |
| T005-13 | Auditoría atómica de acceso/talleres, sin campos sensibles ni limpieza automática. Pruebas Application y HTTP bloquean JSON/Excel si falla auditoría; commit incierto se distingue en Application; guardia Down y conservación verificadas en SQL. |
| T005-14 | Handlers MediatR y DI scoped/transient; orquestador compartido sin service locator. Pruebas Application de auditoría vacía, validación previa, límite antes del escritor y revocación durante generación; arquitectura de puertos verificada. |
| T005-15 | Diez rutas de DESIGN-005 / 3, DTOs explícitos, ProblemDetails, no-store, esquema de usuario y cancelación desde HTTP. Pruebas de shape/límites/offset y credencial de dispositivo rechazada; GET no modifica POST de ingesta. |
| T005-16 | `HistoryWorkbookWriter`, Open XML SDK 3.1.1 en Infrastructure; cadenas seguras, offset local, metadatos y números. Archivo abierto con SDK y validado con `OpenXmlValidator`, sin fórmulas ni enlaces externos; no se afirma prueba visual en Excel de escritorio. |
| T005-17 | Exportación de cuatro kinds, orden/filtros completos, 10.000/10.001, revalidación y auditoría previa. Prueba real de todas las filas y rechazo 422, sin truncar; fallo de auditoría no devuelve MIME Excel. |
| T005-18 | Opciones validadas, gates compartidos y lease SQL por usuario con propietario/expiración; semáforo por instancia. Dos contextos verifican exclusión, recuperación de lease y fencing del propietario antiguo; cuota 60/61 y límite de dos exportaciones por instancia. |
| T005-19 | `HistoryTelemetry`, métricas sin IDs, diagnóstico por etapas/filas y logs por operación. Runbook de IIS/proxy/APM y auditoría sin contenido de filtros. Hosting corporativo no modificado ni ensayado: su validación queda como límite externo, no como garantía satisfecha. |
| T005-20 | `HistoryBoundaryTests`, arquitectura transversal y casos HTTP de autorización y contratos. Regresiones concretas de RF-003/004 y resultados en sección 10; no se atribuye éxito al intento diario sin pruebas. |
| T005-21 | Lectura Serializable frente a escritura, dos contextos de admisión, expiración/fencing y deadline de exportación; errores de auditoría y revocación cubiertos sin respuestas parciales. No se usa un mismo DbContext concurrentemente. |
| T005-22 | Ensayo de migración y datos originales, búsqueda de un año, todos los talleres, contains, recepción, página 100 y exportación de 10.000. Tiempos/tamaño y plan de conteo representativo bajo `history-load-evidence`; no es un benchmark productivo ni un SLA. |
| T005-23 | OpenAPI tipado para consultas y binario, sin If-Match administrativo en GET; `RF-005-history.http`, preparación y runbook. Los escenarios se ejercitan vía HTTP automatizado; no se afirma ejecución de un cliente manual o IIS corporativo. |
| T005-24 | Reconciliación de CA/CP, registro de fallos y recuperaciones, frecuencia diaria, build/formato y límites. El estado final depende de las evidencias efectivas de sección 10, sin convertir pendientes externos en pruebas aprobadas. |

### 9.1. Ajustes de implementación respecto al diseño

La coordinación de cierre reside en `HistoryService` para compartir los pasos
de consulta y exportación sin otra abstracción. No cambia la lógica de RF-004.
Puertos de lectura y admisión siguen perteneciendo a Application.
EF Core 10 usa `EF.Parameter` para el ámbito y recuperaciones de offsets grandes.
El detalle de páginas ordinarias y Excel usa ahora la página como subconsulta
SQL, evitando expandir parámetros escalares o reenviar 10.000 IDs en JSON con
una estimación de cardinalidad pequeña. Los ensayos anteriores agotaron el
presupuesto de lectura, incluso tras una ejecución correcta; se conservan como
fallos y el caso se repite tras cambiar esa selección, sin aumentar los timeouts.

La prueba de bloqueo conserva abierta la conexión que establece LOCK_TIMEOUT;
una conexión devuelta al pool perdía ese ajuste. La aserción inspecciona el
SqlException raíz porque EF puede envolver el error transitorio. No se ha
habilitado retry automático de escrituras para hacer pasar la prueba.

Los checks de formato detectaron codificación/EOL y namespace de la migración
nueva generada en Windows. Se ajusta solo esa entrega y su snapshot al formato
del repositorio, sin editar migraciones aplicadas anteriores.

## 10. Verificaciones, evidencias y límites de cierre

Ejecuciones del 19 de noviembre de 2026, con SQL Server local real y bases
sintéticas aisladas. Resumen calculado a partir de TRX: **202 casos distintos
con último resultado correcto, 0 fallidos y 0 omitidos**. Son 160 de Application,
15 de arquitectura y 27 de SQL/HTTP/calendario (RF-005 y regresiones concretas).
No se suman las repeticiones como casos nuevos ni se presenta este conjunto
como una suite completa de la solución.

Evidencia local ignorada por Git:
`artifacts/agent-checks/rf005-verification-summary.json` contiene el último
resultado de cada caso y su ejecución de origen. Los fallos intermedios se
conservan; un último resultado correcto no borra sus logs o TRX.

| Comprobación realmente ejecutada | Resultado y evidencia |
| --- | --- |
| Intento diario: `dotnet test GarajeMetria.slnx --configuration Release --artifacts-path artifacts/agent-checks/rf005-build --disable-build-servers -m:1 /nr:false --logger trx --results-directory artifacts/agent-checks/2026-11-19-rf005-baseline` | Falló restore con NU1900 al consultar NuGet; no ejecutó pruebas. Registro `full-suite.json` y `2026-11-19-rf005-baseline.json`. Cuenta como intento diario y no se repitió la suite completa. |
| `dotnet restore GarajeMetria.slnx` y restauración previa en ruta de artefactos | Correctas con acceso de red autorizado; no se desactivó auditoría NuGet ni warnings-as-errors. Open XML SDK fijado en 3.1.1. |
| `dotnet build GarajeMetria.slnx --no-restore --configuration Release --disable-build-servers -m:1 /nr:false` | Correcto, cero advertencias/errores; `rf005-validated-build.log`. Las correcciones posteriores de los proyectos afectados también compilan al ejecutar sus pruebas focalizadas. |
| Application: `dotnet test tests/GarajeMetria.Application.Tests/GarajeMetria.Application.Tests.csproj --no-build --no-restore --configuration Release --logger trx --results-directory artifacts/agent-checks/rf005-application-regression` | 160/160 correctas; `rf005-application-regression/*.trx`. Se verifica el proyecto completo por el nuevo behavior transversal de validación MediatR, sin volver a ejecutar la solución completa. Incluye las 18 pruebas nuevas de reglas/orquestación RF-005. |
| Arquitectura: mismo patrón sobre `tests/GarajeMetria.ArchitectureTests/GarajeMetria.ArchitectureTests.csproj`, salida `rf005-architecture-final` | 15/15 correctas, incluida independencia de puertos History frente a EF/HTTP/Excel y límites de Api. |
| Selección SQL inicial/final amplia: `--filter 'FullyQualifiedName~LprHttpTests.History\|FullyQualifiedName~HistoryConcurrencyTests\|FullyQualifiedName~HistoryPersistenceTests\|FullyQualifiedName~HistoryCalendarTests\|FullyQualifiedName~StayMigrationTests\|FullyQualifiedName~LocationHistoryAndSynchronous\|FullyQualifiedName~ConcurrentHostsDeduplicateOneUuid\|FullyQualifiedName~DailyClosureAndNewExit\|FullyQualifiedName~InvalidReadingsRemainAuditable\|FullyQualifiedName~TwoInstancesProjectAndCloseOnePartition'` | `rf005-final-sql/*.trx`: 22 correctas y 2 fallidas (deadline/exportación). Sus regresiones RF-003/004 pasan; los dos fallos se investigaron y repitieron focalizadamente. |
| Corrección SQL/contratos: filtro de RF-005 más `OpenApiAndCors`, `OpenApiDescribesJson`, `OpenApiAndSwagger` | `rf005-corrected-sql/*.trx`: 17 correctas y 2 fallidas. Deadline, selectores, roles, SQL, calendario, detalle/linaje y dos contratos OpenAPI pasan. Los pendientes eran el recuento antiguo de rutas y la prueba de carga. |
| Repetición `HistoryExcelAllows\|OpenApiAndSwagger` | `rf005-load-recovery/*.trx`: OpenAPI correcto tras reflejar las 27 rutas; carga supera funcionalmente consultas/Excel pero falla su captura de plan. Esa aserción se corrige sin cambiar la lógica del producto. |
| Repetición exclusiva `FullyQualifiedName~HistoryExcelAllows` con build de proyectos afectados | `rf005-load-plan/*.trx`: 1/1 correcta. Consultas de un año, contains, todos los talleres, recepción, página 100, 10.000 filas y rechazo de 10.001, validación Open XML y plan guardado. |
| Última revisión JSON: `HistoryEventsPreserve\|HistoryExcelExports`, con build sin restore | `rf005-json-boundary/*.trx`: 2/2 correctas. Nombres camelCase estrictos, fechas sin offset y paginación con otra capitalización rechazadas, duplicados rechazados y exportación válida conservada. Reemplazan los resultados anteriores de esos dos casos en el resumen, sin aumentar su total. |
| Tooling EF: script incremental y `migrations has-pending-model-changes`, contexto `AuthenticationDbContext`, configuración Release, `--no-build` | Correctos: «No changes have been made to the model since the last migration». Script en `configuration/sql/20261119090322-vehicle-history-queries.sql`; registro local `rf005-schema-verification.json`. No conecta a producción. |
| Formato y diff | Comprobaciones correctas `dotnet format --verify-no-changes --no-restore` de solución y de últimos archivos; logs `rf005-format-confirmation.log`, `rf005-format-last-verify.log` y `rf005-final-format.log`. Última frontera JSON también comprobada con `dotnet format whitespace . --folder --verify-no-changes --include src/GarajeMetria.Api/History/HistoryEndpoints.cs tests/GarajeMetria.IntegrationTests/HistoryHttpTests.cs`, log `rf005-json-format.log`. `git diff --check` correcto. |

Los comandos SQL usan el proyecto `tests/GarajeMetria.IntegrationTests/` con
configuración Release, logger TRX y `--blame-hang-timeout 120s
--blame-hang-dump-type none`. Los filtros de la tabla muestran `|` escapado por
Markdown: en PowerShell usar `|` literal dentro de comillas como en preparación.
Algunas repeticiones incluyen build sin restore y otras `--no-build`; los logs
conservan qué proyectos compilaron. No atribuir esos resultados a una suite nueva.

### 10.1. Fallos y recuperaciones

El primer SQL aislado falló por SSPI antes de probar comportamiento. Se repitió
solo la selección afectada con acceso Windows local autorizado. Un filtro amplio
anterior quedó bloqueado y mantuvo abierto testhost; se detuvo únicamente ese
proceso y se añadieron límites de espera, sin manipular procesos del usuario.
Un build concurrente que encontró ese ensamblado abierto también queda registrado
como fallo de entorno, no como build satisfactorio.

Los defectos reales corregidos incluyen traducción de selector, selección de
páginas para Excel, clasificación de cancelación SQL envuelta como fallo de
persistencia y descripción de respuesta binaria/headers en OpenAPI. La prueba
antigua de catálogo OpenAPI esperaba 18 rutas y asimilaba todo `/api/lpr/events`
a tráfico de dispositivo: ahora reconoce 27 rutas y diferencia GET de POST,
manteniendo las aserciones del contrato previo.

La captura de plan devuelve texto XML mediante SqlClient aunque el tipo de
columna no siempre se identifique como `xml`; el test acepta el contenido XML
o SqlXml real. No se cambian timeouts ni se elimina la validación del plan para
ocultar el fallo. La aserción de bloqueo busca el error SQL raíz 1222, sin
depender del wrapper de EF y sin activar retries de escritura.

### 10.2. Carga medida e índices

Última evidencia, UTC `2026-11-19T09:54:22.0027707+00:00`:
10.000 filas sintéticas, siembra 12,47 s, página 100 con filtro parcial y ventana
de un año 1,35 s, exportación 5,43 s, archivo de 66.148 bytes. Incluye apertura
y validación del archivo con SDK, cero fórmulas y rechazo explícito de 10.001.
Tiempos locales de referencia, sin promesa de latencia productiva.

Copias conservadas bajo `artifacts/agent-checks/rf005-load-evidence/`:
`10000-rows.json` y `capture-count.sqlplan`. El plan es representativo del
conteo por captura/taller, no una afirmación sobre todos los planes posibles:
contiene Stream Aggregate e Index Scan. La comparación ordinal y búsqueda
parcial pueden requerir escaneo; los índices nuevos son acotados a recepción
y estados/extremos vigentes y reutilizan el soporte previo de captura.
No se introdujo otro motor, Dapper, buscador externo o abstracción de repositorio
genérico para mejorar una cifra de este ensayo.

### 10.3. Límites expresos

La entrega y las casillas se refieren al backend y sus ensayos aislados.
No se ha desplegado, habilitado política ni aplicado migración en producción;
no se han validado AD corporativo, cámaras físicas o backup/restore físico.
IIS/proxy/APM corporativos deben comprobar exclusión de query strings y cuerpos
antes de exponer el servicio. La guía existe, pero ese hosting no se declara
verificado por WebApplicationFactory. No se ha abierto Excel de escritorio ni
ejecutado manualmente una colección HTTP: se usaron SDK/OpenXmlValidator y
peticiones HTTP equivalentes automatizadas.

No existe nueva política de borrado de auditoría. El presupuesto puede producir
503 si hay más cierres relevantes que los consolidables en tres ciclos; nunca
se ocultan filas vencidas para aparentar presencia actual. No hay fotografía
inmutable entre páginas ni SLA acreditado para volúmenes productivos.
Las verificaciones de SQL/HTTP no prometen PostgreSQL/MySQL ya ensayados.
