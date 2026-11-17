# TASKS-004 — Entradas, salidas y estancias

| Campo | Valor |
| --- | --- |
| Requerimiento | [RF-004](../requirements/RF-004-arrivals-departures-and-stays.md), prioridad Alta |
| Diseño | [DESIGN-004](../design/DESIGN-004-arrivals-departures-and-stays.md) |
| Coordinación | [TASKS-003](TASKS-003-lpr-event-ingestion.md), sesiones y permisos de RF-001/RF-002 y catálogo local de RF-009 |
| Fecha | 17 de noviembre de 2026 |
| Estado | T004-01 a T004-24 completadas para el backend; 810 casos superados y 1 omitido tras correcciones focalizadas. Límites RF-005/006 y externos en sección 19 |

RN y CA pertenecen a RF-004; CP se define en DESIGN-004 / 12 y 12.1. Las
decisiones P004-01 a P004-03 están resueltas y no son prerrequisitos pendientes.
Los números de tarea son estables; las dependencias expresas determinan el orden.
Completar una casilla requiere entregable, verificaciones reales y evidencia
registrada, no solo código escrito. Redactar este plan no completa sus tareas.

## 1. Alcance, prerrequisitos y límites

Implementar el backend de cálculo, identidad y configuración histórica,
conservación de observaciones, ubicación descriptiva y su historia, integración
síncrona con ingesta, cierre diario recuperable y proyección inicial. Mantener
C# 14/.NET 10/EF Core 10, Clean Architecture y los contratos de RF-003 salvo
las ampliaciones compatibles de dispositivos descritas en DESIGN-004 / 9.

La base reutilizable es `ReceiveLprEventHandler`, `ILprUnitOfWork`,
`SqlTransactions`, `AuthenticationDbContext`, dispositivos, eventos y sesiones.
Inspeccionar su estado efectivo al iniciar cada tarea y preservar cambios ajenos.
No exigir que se cierre todo TASKS-001/002/003/009: las dependencias externas
son las capacidades concretas existentes de sesión administrativa, autorización
de dispositivo y catálogo local confirmado.

Prerrequisitos de integración: SDK fijado en `global.json`, SQL Server aislado,
permisos para crear/eliminar exclusivamente las bases sintéticas de pruebas y
configuración privada sin secretos versionados. Reutilizar el patrón de
`AuthenticationFixture` y `GARAJEMETRIA_TEST_SQL` cuando corresponda. La zona
`Atlantic/Canary` debe resolverse en el entorno Windows/IIS previsto. Pruebas
HTTP con identidades sintéticas y `WebApplicationFactory`; no necesitan AD
corporativo, cámara física, origen corporativo definitivo ni producción.

No se construyen endpoints de historial RF-005, correcciones RF-006, imágenes
RF-007 ni UI RF-008. La representación de capturas adelantadas en consultas
queda en RF-005, conservando su captura original y sin introducir rechazo nuevo
en RF-003. No hay autorización de commit, publicación, despliegue ni aplicación
de migraciones en producción. Los ensayos siguientes usan recursos aislados.

## 2. Preparación, dominio y contratos

- [x] **T004-01 — Preparar la base y el entorno de verificación.**
  Dependencias: ninguna.
  Inventariar los componentes anteriores, fixtures, registro DI y pruebas de
  RF-003 afectadas; revisar instrucciones y ejecución conocida. Preparar datos
  sintéticos, reloj y calendario deterministas, harness de dos instancias y
  barreras de concurrencia. Identificar archivos concretos a modificar sin
  introducir paquetes ni cambios de runtime.
  Cierre: entorno reproducible documentado, conexión aislada comprobada sin
  exponerla y casos de DESIGN-004 / 12.1 preparados como datos de prueba.
  Trazabilidad: CA-002, CA-017, CA-021 a CA-027; CP homólogos.
  Entrega: [preparación reproducible](../configuration/stays-test-preparation.md),
  fixtures compartidos de escenarios/tiempo/calendario y harness de dos hosts
  con barrera SQL. Evidencias y limitaciones en sección 10; no cierra los CP
  funcionales de cálculo todavía pendientes.

- [x] **T004-02 — Modelar estancias, extremos, políticas e identidad en Domain.**
  Dependencias: T004-01.
  Introducir `VehicleStay`, `StayPolicySnapshot`, proyecciones de observación,
  estados y fuentes según DESIGN-004 / 2.1. Modelar ancla de origen inmutable,
  creación distinta de captura, duración UTC y reglas de nulabilidad. No generar
  eventos LPR para estimaciones ni guardar duración creciente de abiertas.
  Cierre: unitarias de invariantes y transiciones con instantes suministrados,
  incluidos extremos estimados sin evento y observados con soporte coherente.
  Trazabilidad: CA-002, CA-005, CA-006, CA-018, CA-026, CA-027.
  Entrega: `Domain/Stays/VehicleStay.cs`, `StayPolicySnapshot.cs`,
  `StayObservation.cs` y `StayBoundary.cs`, con estados/fuentes, revisiones
  atómicas y pruebas unitarias. Evidencia en sección 11; agrupamiento,
  emparejamiento, cierre automático y linaje siguen en tareas posteriores.

- [x] **T004-03 — Implementar selección, orden y grupos de repetición.**
  Dependencias: T004-02.
  Seleccionar válidas sin umbral de confianza; ordenar por captura y UUID
  canónico, no por recepción. Agrupar por equivalencia del diseño entre cámaras,
  con R inclusivo desde la primera captura y representantes según papel.
  Preservar opuestos, integrantes y grupos que atraviesan medianoche.
  Cierre: unitarias de R menos/exacto/más un tick, no encadenamiento, direcciones
  mixtas, empate de captura, cambio de cámara y prioridad sobre cambio de día.
  Entrega: selección y grupos puros con miembros inmutables y orden canónico;
  pruebas y resultados en sección 12.
  Trazabilidad: CA-003, CA-013, CA-014, CA-015, CA-024; CP homólogos.

- [x] **T004-04 — Implementar visitas explícitas, estimaciones y cierre lógico.**
  Dependencias: T004-03.
  Implementar emparejamiento, entradas/salidas consecutivas, abiertas y salidas
  pendientes, sustitución virtual/inferida dentro de M y separación por días
  después de agrupar. Usar límites de día recibidos del calendario y políticas
  históricas. No truncar a medianoche ni a una nueva entrada una salida estimada.
  Cierre: unitarias de varias visitas, cruces de fecha, M inclusivo, inferencias,
  entradas 23:50/00:10 y repetidas 23:59:40/00:00:10; comparar cierre antes/después
  de la segunda observación. Mantener íntegro el rastro intermedio.
  Entrega: evaluador explícito puro, candidatos con evidencias y límites de día
  suministrados; pruebas y resultados en sección 13.
  Trazabilidad: CA-001, CA-002, CA-005 a CA-010, CA-019, CA-023, CA-024.

- [x] **T004-05 — Implementar asociación sin dirección y fallback diario.**
  Dependencias: T004-04.
  Filtrar movimientos explícitos elegibles por distancia absoluta M y límite
  desde la entrada antes de elegir el más cercano, con empate al anterior.
  Dar prioridad a asociación entre días y aplicar primera/última diaria solo
  a lecturas sin candidato. Resolver integrantes a ambos lados del punto de
  proximidad sin arrastrar todo un grupo desconocido a una visita incorrecta.
  Cierre: unitarias de ampliación 07:55–09:05, fallback, asociación de 00:05 a
  entrada 23:50, M histórico e inclusivo y caso con movimiento cercano pero
  entrada fuera de M. No inventar dirección en el evento original.
  Entrega: asociación individual con evidencia del movimiento elegido y fallback
  diario; pruebas y resultados en sección 14.
  Trazabilidad: CA-004, CA-011, CA-012, CA-025; CP homólogos.

- [x] **T004-06 — Implementar recálculo puro, fusiones y divisiones.**
  Dependencias: T004-05.
  Producir un conjunto determinista de cambios sin I/O. En fusión, conservar
  ID/ancla y snapshot completo de la creada primero, con desempate de creación
  persistido; en división, heredar toda la política y mantener ID en la parte
  que contiene el ancla, asignando identidad a las restantes. Modelar linaje
  y no-op al repetir cálculo sin cambios.
  Cierre: unitarias con políticas distintas en X/R/M/versión, captura anterior
  en la creada después, ancla en la parte posterior y división múltiple;
  verificar que ninguna operación usa la configuración actual por defecto.
  Trazabilidad: CA-016, CA-018, CA-026, CA-027; CP homólogos.

- [x] **T004-07 — Definir contratos y validación Application.**
  Dependencias: T004-02.
  Definir contratos de observaciones paginadas, repositorio de estancias,
  políticas, cierre, calendario y unidad de trabajo de cierre. Incorporar
  `CloseStayDayCommand`, `UpdateDeviceLocationCommand`, DTOs y resultados;
  extender contratos de dispositivo de forma compatible. Validar entradas
  con FluentValidation y conservar autorización antes de acceso a recursos.
  Cierre: unitarias de validación sin efectos y pruebas de contratos/arquitectura;
  Application sin EF/HTTP, sin `IQueryable` ni identidad administrativa ficticia.
  Trazabilidad: CA-017, CA-018, CA-020, CA-021, CA-022.
  Entrega de código: contratos de `Application/Stays` y ampliaciones compatibles
  de solicitudes, DTO y validadores de dispositivos. Compilación y revisión
  registradas en sección 15; el aplazamiento de entonces queda sustituido por
  la solicitud de pruebas final y los resultados de sección 19.

## 3. Persistencia, políticas y orquestación

- [x] **T004-08 — Crear mappings y migración aditiva SQL Server.**
  Dependencias: T004-07.
  Implementar las tablas de DESIGN-004 / 5, incluido `StayLineage`, versiones
  de ubicación y cierres; añadir `CurrentLocationId` y `EventOrderKey` sin
  modificar UUID ni cargas originales. Mantener FK restrictivas, unicidad,
  precisión UTC, checks y claves ordinales; ampliar auditoría para ubicación.
  Generar migración/snapshot nuevos del contexto actual y script revisable.
  Cierre: SQL aislado vacío y desde esquema previo con datos sintéticos; comprobar
  constraints, claves compuestas, ausencia de cascadas, orden binario UUID y
  preservación de eventos/credenciales. No alterar migraciones aplicadas.
  Trazabilidad: CA-003, CA-013, CA-017 a CA-022, CA-026, CA-027.
  Entrega de código: filas y mappings de persistencia, migración aditiva
  `20261117125011_StayPersistence`, snapshot y script SQL revisable. Cambios,
  compilación y límites históricos en sección 16; ensayos y cierre en sección 19.

- [x] **T004-09 — Implementar calendario, opciones y activación de políticas.**
  Dependencias: T004-08.
  Registrar opciones tipadas y validación de valores, conversiones y rangos
  operativos; resolver `Atlantic/Canary`. Persistir política inmutable y activa,
  comprobar coincidencia entre instancias y preparar activación explícita fuera
  del arranque automático de producción. Separar valores operativos de negocio.
  Cierre: pruebas de configuración inválida, política discrepante, primavera/
  otoño, días de duración distinta y snapshot histórico inalterado. Validar que
  una instancia discrepante no acepta escrituras con otros parámetros.
  Trazabilidad: CA-002, CA-005, CA-006, CA-018, CA-025 a CA-027.
  Entrega de código: calendario, opciones inmutables, repositorio de políticas,
  puerta de admisión y script de activación explícita. Cambios y compilación
  en sección 17; ensayos de activación en SQL sintético y cierre en sección 19.
  No se ha activado la política en un entorno compartido.

- [x] **T004-10 — Implementar repositorios y coordinación transaccional.**
  Dependencias: T004-08.
  Implementar lectura materializada keyset y contratos de escritura en el mismo
  contexto scoped. Extender orden catálogo, usuario opcional, dispositivo,
  evento, partición; cierre usa catálogo/partición. Mantener `Serializable`,
  versiones, clock compartido, timeouts y clasificación de fallos. Locks sin
  matrículas; ninguna transacción anidada ni llamada externa dentro de ella.
  Cierre: SQL aislado de creación concurrente de partición, FKs de pertenencia,
  cancelación, rollback y timeout; no confundir colisión de estancia con PK LPR
  ni habilitar reintentos ciegos sobre commit incierto.
  Trazabilidad: CA-003, CA-017, CA-021, CA-022, CA-026, CA-027.

- [x] **T004-11 — Orquestar recálculo incremental y persistencia del linaje.**
  Dependencias: T004-06, T004-09, T004-10.
  Implementar `StayRecalculationService` y DI. Expandir contexto hasta bordes
  estables, usando M histórico y carry de grupos/visitas entre páginas, sin
  cargar todo el historial en memoria ni cortar arbitrariamente a dos días.
  Aplicar diferencias, identidad, revisiones, asociaciones y linaje juntos.
  Cierre: unitarias Application con stubs; SQL de propagación entre páginas,
  no-op, configuración heredada, orden de creación y presupuesto agotado sin
  guardar resultados parciales. Inyectar fallos entre asociación y linaje.
  Trazabilidad: CA-012, CA-016 a CA-019, CA-023 a CA-027.

## 4. Ubicación e integración con RF-003

- [x] **T004-12 — Implementar ubicación descriptiva e historia de observaciones.**
  Dependencias: T004-07, T004-08, T004-10.
  Extender dominio y casos de uso del dispositivo, snapshots y operación
  `LocationChanged`. Escribir descripción, versión, auditoría y puntero en una
  transacción, con no-op y sin revocar credenciales por el cambio. Resolver
  ubicación por captura, desconocida histórica y procedencia de captura futura.
  Cierre: unitarias Application y SQL de fallo/auditoría atómicos, cambio con
  captura tardía, ausencia histórica y estabilidad del vínculo en reintentos;
  limpieza administrativa no elimina versiones ni vínculos de ubicación.
  Trazabilidad: CA-019, CA-020, CA-022; CP-019, CP-020, CP-022.

- [x] **T004-13 — Exponer mantenimiento compatible y OpenAPI de ubicación.**
  Dependencias: T004-12.
  Admitir campo opcional en alta, ampliar GET y añadir PUT de ubicación con
  cuerpo allowlist, Administrador e `If-Match`. Actualizar parser estricto,
  DTOs, mapeo de errores y OpenAPI; conservar alta anterior, paginación, cuotas,
  límite 8192 bytes y separación de identidades.
  Cierre: HTTP sintético de alta antigua/nueva, GET, PUT, ETag, JSON inválido,
  campos duplicados/extra, longitudes, 401/403/404/412/428/429 y fallos seguros;
  Asesor y dispositivo no acceden al mantenimiento. Recibo LPR sin cambios.
  Trazabilidad: CA-020, CA-022; CP-020, CP-022.

- [x] **T004-14 — Integrar cálculo síncrono en recepción LPR.**
  Dependencias: T004-11, T004-12.
  Tras autorización, comparación y ventana de RF-003, incorporar expresamente
  el evento nuevo aún `Added` al cálculo y guardar evento, ubicación, estancias,
  linaje y siguiente cierre en el mismo commit. Inválidas se conservan sin
  efectos de estancia. Duplicados no cambian política, ubicación ni proyección.
  Cierre: unitarias Application y HTTP/SQL de 201/200/409, observación nueva
  incluida, caída antes de commit, resultado incierto y reconciliación por UUID;
  no responder éxito ni encolar el cálculo si no se confirmó el conjunto.
  Trazabilidad: CA-015 a CA-019, CA-021, CA-023 a CA-027; CP homólogos.

## 5. Cierre diario y proyección inicial

- [x] **T004-15 — Implementar caso de uso de cierre recuperable por partición.**
  Dependencias: T004-09, T004-10, T004-11.
  Materializar cierres vencidos con revisión de entrada y clave partición/fecha;
  calcular con reloj compartido y políticas históricas. Confirmar resultados
  y progreso juntos, también en talleres/dispositivos ahora inactivos. Una
  lectura tardía invalida progreso anterior para su nueva revisión.
  Cierre: Application/SQL de día aún no terminado, cierre no-op, estimaciones,
  inferencias, recuperación de varios días y fallo antes/después del commit;
  no marcar trabajo procesado antes de confirmar sus efectos.
  Trazabilidad: CA-005, CA-006, CA-009, CA-010, CA-016, CA-021, CA-023, CA-024.

- [x] **T004-16 — Añadir worker supervisado e integración de cierres en ingesta.**
  Dependencias: T004-14, T004-15.
  Implementar polling/lotes del diseño con scope por unidad, cancelación y sin
  DbContext concurrente. Descubrir trabajo vencido al iniciar; coordinar varias
  instancias con locks/relectura. Ingesta procesa cierres vencidos de su contexto
  antes de confirmar; no depender del instante de ejecución del worker.
  Cierre: SQL/host de reinicio, dos workers, fallo aislado de partición, apagado,
  ingesta simultánea y medianoche; ningún lock retenido durante espera ni
  transacción global del lote. No introducir endpoint de cierre público.
  Trazabilidad: CA-017, CA-021, CA-023, CA-024; CP homólogos.

- [x] **T004-17 — Implementar proyección inicial reanudable.**
  Dependencias: T004-11, T004-12, T004-14, T004-16.
  Preparar herramienta/procedimiento de mantenimiento para política inicial,
  backfill de clave de orden y cálculo desde eventos previos, con checkpoint
  persistido por partición y páginas acotadas. No inferir ubicación histórica
  desde la actual ni tocar UUID, clasificación o captura originales.
  Cierre: SQL aislado con esquema/datos previos, interrupción/reanudación y
  segunda ejecución; mismas observaciones, asociaciones coherentes, IDs sin
  duplicar y cierres actualizados. Ensayar con ingesta detenida, no en producción.
  Trazabilidad: CA-003, CA-015, CA-018 a CA-021, CA-024, CA-026, CA-027.

## 6. Verificación transversal y preparación operativa

- [x] **T004-18 — Verificar concurrencia, atomicidad y recuperación.**
  Dependencias: T004-13, T004-14, T004-16.
  Completar pruebas SQL con barreras de ingesta duplicada/concurrente, partición
  compartida entre cámaras, ingesta/cierre, cambios de ubicación y catálogo,
  dos instancias y fusiones/divisiones. Reproducir fallos conocidos y commit
  incierto sin replay servidor de escrituras no acreditadas.
  Cierre: evidencias CP-017/021/023/024/026/027 y regresiones de autorización
  RF-003 afectadas; sin pérdidas, cambios parciales, duplicación ni interbloqueo
  por orden invertido. Los timeouts producen el resultado seguro previsto.
  Trazabilidad: CA-003, CA-017, CA-020, CA-021, CA-023, CA-024, CA-026, CA-027.

- [x] **T004-19 — Añadir observabilidad segura y diagnóstico.**
  Dependencias: T004-13, T004-14, T004-16.
  Instrumentar recálculo, cierres, antigüedad pendiente, locks, rollback y
  estimaciones con etiquetas acotadas; añadir diagnóstico interno de esquema/
  política y progreso. Mantener correlación y errores públicos seguros.
  Cierre: pruebas de logs/métricas sin matrículas completas, cargas, secretos
  o descripciones libres; sin IDs sensibles como etiquetas; diagnóstico no
  expone SQL ni servidores. Medir retención del lock global de catálogo.
  Trazabilidad: CA-017, CA-021, CA-022; CP-017, CP-021, CP-022.

- [x] **T004-20 — Completar regresiones HTTP, seguridad y arquitectura.**
  Dependencias: T004-18, T004-19.
  Ejecutar comprobaciones focalizadas de contratos RF-003, OAuth, sesiones,
  cuotas, ETag, invalidez de matrícula y portabilidad de capas afectadas.
  Verificar conservación/acceso por contratos internos de todo el rastro y
  separación por taller; no exponer entidades ni conceder lectura a dispositivos.
  Cierre: HTTP/arquitectura y pruebas focalizadas de RF-001/002/003/009 afectadas
  pasando. Registrar explícitamente la consulta HTTP de historial diferida a
  RF-005, sin presentarla como endpoint construido ni prueba ejecutada.
  Trazabilidad: CA-003, CA-015, CA-017, CA-019, CA-020, CA-022.

- [x] **T004-21 — Ensayar migración, activación y recuperación aisladas.**
  Dependencias: T004-17, T004-18.
  Reproducir fases de DESIGN-004 / 10: esquema aditivo, datos previos, política,
  proyección y cierre, validación y activación conjunta del nuevo camino/worker.
  Preparar script y procedimiento de recuperación, verificando que volver al
  handler antiguo después de activar RF-004 no conserva la garantía síncrona.
  Cierre: ensayo sintético documentado con recuentos antes/después, sin pérdidas;
  rollback/reanudación acreditados según alcance real. No equiparar este ensayo
  a backup/restore corporativo físico ni despliegue de producción.
  Trazabilidad: CA-017 a CA-021, CA-026, CA-027; DESIGN-004 / 10.

- [x] **T004-22 — Medir carga de referencia y presupuestos operativos.**
  Dependencias: T004-17, T004-18, T004-19.
  Medir en SQL aislado la referencia aproximada 20–40 eventos/minuto, con
  múltiples cámaras, repeticiones, retrasos aceptables y cierres pendientes.
  Añadir historial paginado y cambios de política; medir picos de recuperación
  como exploración, sin convertirlos en un SLA no acordado.
  Cierre: registrar entorno, datos, comandos, latencias, contención, memoria,
  antigüedad de cierre y fallos. Presupuesto agotado revierte y devuelve 503;
  nunca mejora tiempos truncando cálculo o confirmándolo pendiente. Investigar
  incumplimientos de corrección antes del cierre; no afirmar capacidad productiva.
  Trazabilidad: CA-017, CA-018, CA-021, CA-025; DESIGN-004 / 7 y 11.

- [x] **T004-23 — Preparar runbook y flujo HTTP sintético.**
  Dependencias: T004-13, T004-14, T004-16, T004-17, T004-19, T004-21, T004-22.
  Documentar configuración privada, activación de políticas, zona, diagnóstico,
  backlog, proyección inicial, fallos, recuperación y límites de rollback.
  Preparar ejemplos de alta/ubicación/ingesta y comprobación de persistencia en
  entorno aislado sin inventar rutas de historial. Actualizar documentación de
  RF-003 para distinguir ingesta original de la integración RF-004 entregada.
  Cierre: recorrido reproducible con valores sintéticos y evidencias reales;
  sin secretos ni datos de producción. Separar verificación HTTP automatizada
  de cualquier ejecución manual de cliente o hardware que no se haya realizado.
  Trazabilidad: CA-017, CA-018, CA-020, CA-021, CA-022; DESIGN-004 / 9 a 11.

- [x] **T004-24 — Consolidar evidencias y cierre del backend implementado.**
  Dependencias: T004-20, T004-21, T004-22, T004-23.
  Relacionar los 27 CA/CP con pruebas concretas, resultados y artefactos;
  comprobar todas las tareas y diferencias respecto de RF/diseño. Actualizar
  estados documentales y registrar limitaciones, incluyendo RF-005/RF-006 y
  verificaciones externas. Aplicar la frecuencia de suite de sección 8.
  Cierre: checks aplicables pasando sin nuevas advertencias, documentación
  coherente y ningún CA dado por completo con evidencia exclusivamente diferida.
  Si falta una consulta RF-005, describir cierre del backend de cálculo y el
  alcance pendiente, sin declarar RF-004 íntegramente verificado por ello.
  Trazabilidad: CA-001 a CA-027; CP-001 a CP-027; matriz de sección 7.

## 7. Dependencias de entrega y matriz CA/CP

Hitos de revisión sin tareas adicionales ni fechas de entrega inventadas:

| Hito | Tareas necesarias | Condición para continuar |
| --- | --- | --- |
| Dominio comprobable | T004-01 a T004-07 | Reglas y decisiones resueltas cubiertas sin SQL/red. |
| Persistencia y orquestación | T004-08 a T004-11 | Esquema, políticas, locks, recálculo y linaje comprobados en SQL aislado. |
| Integración funcional | T004-12 a T004-16 | Ubicación compatible, evento y estancia atómicos, cierre recuperable. |
| Preparación operativa | T004-17 a T004-23 | Proyección/migración ensayadas, regresiones y medidas registradas, runbook reproducible. |
| Cierre con evidencia | T004-24 | Matriz real completa o pendientes explícitos por alcance, sin inventar resultados. |

La tabla de hitos no reemplaza las dependencias de cada tarea. T004-07 puede
avanzar tras T004-02 sin esperar al algoritmo completo; la rama de ubicación
puede avanzar tras persistencia. Esto describe el grafo de trabajo, no autoriza
crear subagentes ni implica implementar ramas concurrentemente con un DbContext.

| CA | CP de DESIGN-004 | Tareas de implementación principales | Verificación de integración/cierre |
| --- | --- | --- | --- |
| CA-001 | CP-001 | T004-04, T004-11, T004-14 | T004-20, T004-24 |
| CA-002 | CP-002 | T004-02, T004-04, T004-09 | T004-20, T004-24 |
| CA-003 | CP-003 | T004-03, T004-08, T004-10, T004-14 | T004-18, T004-20 |
| CA-004 | CP-004 | T004-05, T004-11, T004-14 | T004-20, T004-24 |
| CA-005 | CP-005 | T004-04, T004-09, T004-15, T004-16 | T004-18, T004-24 |
| CA-006 | CP-006 | T004-04, T004-09, T004-15 | T004-18, T004-24 |
| CA-007 | CP-007 | T004-04, T004-11, T004-14 | T004-18, T004-24 |
| CA-008 | CP-008 | T004-04, T004-11, T004-14 | T004-18, T004-24 |
| CA-009 | CP-009 | T004-04, T004-15 | T004-18, T004-24 |
| CA-010 | CP-010 | T004-04, T004-15 | T004-18, T004-24 |
| CA-011 | CP-011 | T004-05, T004-11, T004-14 | T004-20, T004-24 |
| CA-012 | CP-012 | T004-05, T004-11 | T004-20, T004-24 |
| CA-013 | CP-013 | T004-03, T004-08, T004-11 | T004-20, T004-24 |
| CA-014 | CP-014 | T004-03, T004-11 | T004-20, T004-24 |
| CA-015 | CP-015 | T004-03, T004-14, T004-17 | T004-20; consulta HTTP en RF-005 |
| CA-016 | CP-016 | T004-06, T004-11, T004-14, T004-15 | T004-18, T004-24 |
| CA-017 | CP-017 | T004-10, T004-11, T004-14, T004-16 | T004-18, T004-19, T004-21, T004-22 |
| CA-018 | CP-018 | T004-06, T004-09, T004-11, T004-17 | T004-20, T004-21, T004-22 |
| CA-019 | CP-019 | T004-04, T004-11, T004-12, T004-17 | T004-20; consulta HTTP en RF-005 |
| CA-020 | CP-020 | T004-12, T004-13, T004-17 | T004-18, T004-20, T004-21 |
| CA-021 | CP-021 | T004-14, T004-15, T004-16 | T004-18, T004-19, T004-21, T004-22 |
| CA-022 | CP-022 | T004-07, T004-13, T004-19 | T004-20; autorización HTTP del rastro en RF-005 |
| CA-023 | CP-023 | T004-04, T004-11, T004-14, T004-16 | T004-18, T004-24 |
| CA-024 | CP-024 | T004-03, T004-04, T004-11, T004-16 | T004-18, T004-24 |
| CA-025 | CP-025 | T004-05, T004-09, T004-11, T004-14 | T004-20, T004-22, T004-24 |
| CA-026 | CP-026 | T004-06, T004-08, T004-10, T004-11 | T004-18, T004-21, T004-24 |
| CA-027 | CP-027 | T004-06, T004-08, T004-10, T004-11 | T004-18, T004-21, T004-24 |

En CA-015/019/022 distinguir evidencia del backend de conservación, asociación y
aislamiento de la futura consulta HTTP completa. La verificación diferida no
equivale a éxito ni permite ocultar un criterio pendiente. RF-006 ampliará la
auditoría de correcciones; el linaje y la historia de ubicación de RF-004 no
esperan a RF-006 para conservarse correctamente.

## 8. Estrategia de verificación y frecuencia

Añadir pruebas de comportamiento en cada tarea, no posponer toda la verificación
a T004-18. Domain/Application usan dobles y tiempo determinista, sin red/DB.
SQL real aislado valida constraints, consultas, transacciones, tiempo, locks y
migraciones. Las pruebas concurrentes utilizan barreras y conexiones/contextos
independientes, no sleeps arbitrarios ni un mismo DbContext concurrente.
No utilizar EF InMemory ni SQLite. Los escenarios temporales no necesitan
esperar una medianoche real.

Ejecutar filtros por clases/casos efectivamente implementados; preparar listas
concretas en T004-01 y ajustarlas a los archivos reales. No documentar un filtro
vacío que devuelve cero pruebas como evidencia de éxito. Usar comandos de
restore/build/format cuando los cambios lo requieran y checks focalizados tras
lotes coherentes de cambios. Regresiones RF-003 deben incluir no-op de duplicados
después de cambiar política/ubicación, revocaciones y ventanas de captura.

La suite completa solo se ejecuta por petición explícita o una vez en la primera
sesión de desarrollo del día si no existe intento conocido, según `Atlantic/Canary`
y AGENTS.md. Antes consultar historial y `artifacts/agent-checks/full-suite.json`;
después registrar fecha local, UTC, comando, resultado y recuentos reales. Un
intento fallido también consume el intento diario; investigar con pruebas
focalizadas. Este plan documental no inicia la suite ni una automatización.

Comandos raíz de referencia para una verificación completa cuando proceda:

```powershell
dotnet restore
dotnet build --no-restore --configuration Release
dotnet format --verify-no-changes --no-restore
dotnet test --no-build --configuration Release
```

Adaptar rutas de artefactos y logger a las herramientas reales del repositorio.
No ejecutar de nuevo la suite al completar cada tarea. Si se reutiliza evidencia
del día, indicar qué cambios son posteriores y las pruebas focalizadas que los
cubren; no atribuirles un resultado de suite anterior como si fueran probados.

## 9. Registro de ejecución y definición de cierre

Estado al redactar el plan: ninguna T004 completada ni pruebas RF-004 ejecutadas.
El registro local consultado conserva un intento completo del 16 de noviembre
de 2026: 579 aprobadas, 0 fallidas y 1 omitida. Es evidencia de la base previa,
no de RF-004 ni una ejecución del 17 de noviembre. Releer el registro al comenzar
desarrollo; no asumir que sigue siendo el último conocido.

Para cada tarea completada, añadir una entrada con:

- ID y fecha local/UTC, cambios y diferencia justificada respecto al diseño.
- Comandos ejecutados, entorno aislado y prerequisites efectivos, sin secretos.
- Pruebas concretas/CA/CP cubiertos, recuentos, resultado y ruta local de TRX o
  evidencia equivalente bajo artefactos ignorados; motivo de cada omisión.
- Riesgos, resultados no acreditados y dependencias que se trasladan a otro RF.

Cierre exige invariantes y arquitectura preservadas, checks aplicables pasando,
sin nuevas advertencias, contratos compatibles, recuperación ensayada en el
alcance declarado y documentación actualizada. No declarar cámara real, AD,
backup/restore corporativo, carga productiva, despliegue ni consultas RF-005
verificados por ejecutar pruebas sintéticas de este plan. La entrega final
debe separar cálculo backend completado, criterios parciales por RF-005 y
preparación operativa de una eventual puesta en producción autorizada.

## 10. Evidencia de T004-01 — 17 de noviembre de 2026

Estado: **completada**. Registro consolidado a las 11:12 UTC (11:12 en
`Atlantic/Canary`). Se completó preparación, no cálculo funcional de estancias.
No hay cambios en código de producción, paquetes, runtime ni migraciones.

### 10.1. Entregables y alcance acreditado

- [Preparación reproducible](../configuration/stays-test-preparation.md):
  inventario de componentes/DI/archivos a modificar, configuración privada,
  fixtures reutilizados, filtros existentes y límites de los escenarios.
- `tests/Shared/StayTestData.cs`, enlazado desde `tests/Directory.Build.props`:
  diez casos de lecturas para CP-023/024/025, políticas y estados previos de
  fusión/división CP-026/027, reloj controlado y calendario de pruebas.
- `tests/GarajeMetria.Domain.Tests/StayFixtureTests.cs`: dos comprobaciones de
  tiempo y calendario, incluidas medianoche y transiciones de 23/25 horas.
- `tests/GarajeMetria.IntegrationTests/StayPreparationTests.cs`: dos hosts,
  SQL real aislado/migrado, contextos independientes, tiempo compartido,
  repositorio/UoW resolubles y cero llamadas al directorio; barrera asíncrona
  reutilizable y comprobación de espera/liberación del lock SQL real de catálogo.
- `LprTestEnvironment` admite instante opcional para vigencia de certificados
  sintéticos. La corrección mantiene el comportamiento por defecto de RF-003,
  no deshabilita validación y permite que el reloj fijo arranque hosts válidos.

La conexión y los permisos de creación/eliminación se verificaron con el ciclo
del fixture y sus bases `GarajeMetria_AuthTests_<UUID>`, sin imprimir cadenas ni
contraseñas. No se modificó la conexión ni la base de la API del desarrollador.
Los datos de fusión/división son estados preparados para el calculador futuro,
no demostración de que esas operaciones ya estén implementadas.

### 10.2. Comandos y resultados reales

Salida de compilación separada: `artifacts/agent-checks/rf004-build`.

| Verificación | Resultado |
| --- | --- |
| `dotnet --version` | 10.0.401, compatible con `global.json`. |
| Restore de solución | Primer intento restringido falló con NU1900 al consultar NuGet; repetido con acceso de red terminó con código 0. No se desactivó auditoría ni se cambiaron dependencias. |
| Build Release de solución | Código 0; 0 advertencias y 0 errores. |
| Formato de los archivos C# nuevos | Código 0. El primer intento restringido no pudo abrir el pipe de MSBuild; la comprobación con acceso local adecuado pasó. |
| Suite completa diaria, 11:04:26–11:10:13 UTC | 586 resultados: 584 aprobados, 1 fallido y 1 omitido. Application 111/0; Architecture 14/0; Domain 181/0; Integration 278 aprobados, 1 fallido, 1 omitido. Código 1. |
| Diagnóstico y corrección | Falló el nuevo harness de reloj fijo: sus certificados LPR creados con fecha de sistema aún no eran válidos en la fecha simulada; el error visible fue durante disposición del host no arrancado. Se alineó su vigencia con el reloj sintético. |
| Build Release de IntegrationTests tras corrección | Código 0; 0 advertencias y 0 errores. |
| Formato tras corrección, incluyendo `LprPreparationTests.cs` | Código 0. |
| Reejecución focalizada tras corrección, TRX de 11:11:31 UTC | 24 aprobadas, 0 fallidas y 0 omitidas: `StayPreparationTests`, `LprPreparationTests` y `LprConfigurationTests`. Las dos nuevas pruebas SQL pasaron. |

Comandos de restore y build ejecutados:

```powershell
dotnet restore GarajeMetria.slnx --artifacts-path artifacts/agent-checks/rf004-build --disable-parallel -m:1 /nr:false
dotnet build GarajeMetria.slnx --artifacts-path artifacts/agent-checks/rf004-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false
dotnet build tests/GarajeMetria.IntegrationTests/GarajeMetria.IntegrationTests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false
```

Comprobación final de formato, sin cambios:

```powershell
dotnet format GarajeMetria.slnx --verify-no-changes --no-restore --include tests/Shared/StayTestData.cs tests/GarajeMetria.Domain.Tests/StayFixtureTests.cs tests/GarajeMetria.IntegrationTests/StayPreparationTests.cs tests/GarajeMetria.IntegrationTests/LprPreparationTests.cs --verbosity minimal
```

Suite diaria y reejecución focalizada:

```powershell
dotnet test GarajeMetria.slnx --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --logger trx --results-directory artifacts/agent-checks/2026-11-17-full
dotnet test tests/GarajeMetria.IntegrationTests/GarajeMetria.IntegrationTests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --filter 'FullyQualifiedName~StayPreparationTests|FullyQualifiedName~LprPreparationTests|FullyQualifiedName~LprConfigurationTests' --logger trx --results-directory artifacts/agent-checks/2026-11-17-t004-01-focused
```

Evidencia local ignorada por Git: cuatro TRX en
`artifacts/agent-checks/2026-11-17-full/` y el TRX
`artifacts/agent-checks/2026-11-17-t004-01-focused/mrivas_NEU_ITV_INFMI_2026-11-17_11_11_31_net10.0.trx`.
`artifacts/agent-checks/full-suite.json` conserva el intento diario como
**fallido**, con sus tiempos UTC y recuentos originales. La corrección focalizada
no lo sustituye por un supuesto éxito completo ni se suman ejecuciones repetidas
para inventar un total de pruebas únicas.

### 10.3. Limitaciones y siguiente tarea

La omitida es la prueba LDAPS real
`DirectoryIntegrationTests.RealLdapsAuthenticatesTheSameStableObjectAndReadsNestedMembershipWithoutPassword`;
no acredita AD corporativo. El fallo nuevo de preparación quedó corregido y
verificado mediante las 24 pruebas focalizadas; no se repitió la suite completa
el mismo día, conforme a AGENTS.md. No se ejecutó un format global de toda la
solución: la verificación final se limitó a los cuatro archivos C# afectados.

La cobertura de T004-01 es preparación de CA-002/017/021 a CA-027: no marcar
esos CA/CP funcionales como aprobados por disponer de fixtures. No existen aún
las tablas, cálculo, worker, ubicación histórica ni consultas de estancias.
Al cerrar T004-01, T004-02 era la siguiente tarea habilitada y T004-02 a T004-24
seguían pendientes. El avance posterior se registra en sección 11.

## 11. Evidencia de T004-02 — 17 de noviembre de 2026

Estado: **completada**. Registro consolidado a las 11:29 UTC (11:29 en
`Atlantic/Canary`). Se entrega el modelo base de Domain, sin persistencia,
dependencias nuevas, I/O ni lectura del reloj del sistema.

### 11.1. Cambios e invariantes verificadas

| Entregable | Comportamiento implementado |
| --- | --- |
| `src/GarajeMetria.Domain/Stays/StayPolicySnapshot.cs` | Valor inmutable con identidad, X/R/M positivos suministrados, zona acordada y versión positiva de algoritmo. No establece una duración fija por defecto. |
| `src/GarajeMetria.Domain/Stays/StayObservation.cs` | Proyección inmutable desde datos guardados o `LprEvent`, con captura/recepción UTC, validez y versión de clasificación preservadas, confianza y dirección. No vuelve a clasificar la matrícula ni descarta observaciones inválidas de la proyección. |
| `src/GarajeMetria.Domain/Stays/StayBoundary.cs` | Fuentes explícita/inferida con evento válido y tiempo de captura inseparable de su soporte; estimada sin evento. Impide usar matrícula inválida como soporte de un extremo. |
| `src/GarajeMetria.Domain/Stays/VehicleStay.cs` | Identidad, ancla, contexto, creación/orden y política inmutables. Estados derivados de nulabilidad; duración UTC; revisión validada antes de mutar, no-op y cronología de actualización. Estimaciones requieren contraparte observada y duración X histórica. |
| `tests/GarajeMetria.Domain.Tests/VehicleStayTests.cs` y `StayValueTests.cs` | 39 casos nuevos de comportamiento e invariantes; reutilizan los fixtures de T004-01. Los otros 2 casos del filtro final son `StayFixtureTests`, ya existentes. |

Para extremos explícitos, el movimiento debe coincidir con su papel de entrada
o salida; los inferidos mantienen la dirección original sin sobrescribirla.
Ambos deben corresponder a la misma matrícula/taller con comparación ordinal.
No se crean lecturas para representar estimaciones. El modelo conserva la
lectura de origen aunque el recálculo aporte una entrada más antigua.

`Duration` es null en estancias abiertas o pendientes; `GetDurationAt(instant)`
calcula el tiempo transcurrido suministrado sin guardarlo ni alterar la entidad.
Si la captura está adelantada, devuelve null antes de esa entrada, evitando
duración negativa. Esto no rechaza capturas admitidas por RF-003 ni define la
representación HTTP de RF-005. Los cierres siguen siendo provisionales y la
revisión permite reemplazarlos o reabrirlos según el cálculo futuro.

### 11.2. Verificaciones ejecutadas y resultados

| Verificación | Resultado y evidencia |
| --- | --- |
| Primera ejecución focalizada de Domain | 40 aprobadas, 0 fallidas, 0 omitidas; antes de añadir el caso adicional de X histórico configurable. TRX en `artifacts/agent-checks/2026-11-17-t004-02-domain/`. |
| Build Release de solución tras el último cambio | Código 0; 0 advertencias y 0 errores; salida separada `artifacts/agent-checks/rf004-build`. |
| Ejecución final focalizada de Domain | 41 aprobadas, 0 fallidas, 0 omitidas. Incluye 39 casos nuevos y 2 del calendario/reloj preparado. TRX `artifacts/agent-checks/2026-11-17-t004-02-domain-final/mrivas_NEU_ITV_INFMI_2026-11-17_11_28_06_net10.0.trx`. |
| Pruebas de arquitectura | 14 aprobadas, 0 fallidas, 0 omitidas. TRX `artifacts/agent-checks/2026-11-17-t004-02-architecture/mrivas_NEU_ITV_INFMI_2026-11-17_11_28_06_net10.0.trx`. |
| Formato final del modelo y sus dos archivos de pruebas | Código 0, sin cambios. |
| Revisión documental y de diferencias | Enlaces y tareas actualizados; `git diff --check` sin errores de whitespace. Se preservan cambios previos ajenos al alcance de T004-02. |

Comandos de compilación y pruebas finales:

```powershell
dotnet build GarajeMetria.slnx --artifacts-path artifacts/agent-checks/rf004-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false
dotnet test tests/GarajeMetria.Domain.Tests/GarajeMetria.Domain.Tests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --filter 'FullyQualifiedName~VehicleStayTests|FullyQualifiedName~StayValueTests|FullyQualifiedName~StayFixtureTests' --logger trx --results-directory artifacts/agent-checks/2026-11-17-t004-02-domain-final
dotnet test tests/GarajeMetria.ArchitectureTests/GarajeMetria.ArchitectureTests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --logger trx --results-directory artifacts/agent-checks/2026-11-17-t004-02-architecture
dotnet format GarajeMetria.slnx --verify-no-changes --no-restore --include src/GarajeMetria.Domain/Stays tests/GarajeMetria.Domain.Tests/VehicleStayTests.cs tests/GarajeMetria.Domain.Tests/StayValueTests.cs --verbosity minimal
```

La primera ejecución usó el mismo filtro Domain sin `--no-build`, con destino
`artifacts/agent-checks/2026-11-17-t004-02-domain`. Tras añadir el caso de X=45
se compiló de nuevo y se repitió solo el filtro afectado; no se suman esas dos
ejecuciones como pruebas únicas. No fue necesario restaurar ni cambiar paquetes.

### 11.3. Alcance y verificación diferida

CA-002 queda cubierto aquí en aritmética de instantes, medianoche y ambos
cambios horarios; CA-005/006 en estados, duración y soporte de extremos;
CA-018 en snapshot histórico y revisiones; CA-026/027 en identidad, ancla y
política inmutables necesarios para las operaciones posteriores. Esto no cierra
los criterios completos de emparejamiento, cierre diario, fusión/división o SQL.
`ObservationGroup` (entregado posteriormente en T004-03, sección 12),
`StayCalculator`, selección por M, linaje y persistencia se verifican en sus tareas; el modelo no deduce esos resultados.

Solo se ejecutaron pruebas puras de Domain y arquitectura para este cambio,
sin SQL ni red. No se repitió la suite completa: ya existe el intento diario
de 17 de noviembre registrado en sección 10 y `full-suite.json` (584 aprobadas,
1 fallida, 1 omitida, con corrección focalizada posterior en T004-01). Ese registro
permanece intacto y no se presenta como verificación de los cambios T004-02.

Al cerrar T004-02, T004-03 (agrupamiento) y T004-07 (contratos Application)
quedaban habilitadas; T004-03 a T004-24 seguían pendientes. El avance posterior
se registra en sección 12. No se aplicaron
migraciones ni se modificaron endpoints, dispositivos, autenticación o ingesta.


## 12. Evidencia de T004-03 — 17 de noviembre de 2026

Estado: **completada**. Registro consolidado a 11:40:28 UTC (misma hora
local en `Atlantic/Canary`). Selección, orden y agrupamiento puros en Domain,
sin nuevas dependencias, persistencia, reloj ni cambios en RF-003.

### 12.1. Entregables y cobertura

- `src/GarajeMetria.Domain/Stays/StayObservationOrder.cs`: comparación por captura
  UTC y UUID en bytes de red; no usa recepción ni el orden nativo SQL Server.
- `src/GarajeMetria.Domain/Stays/StayObservationGrouping.cs`: selección por
  taller/matrícula normalizada con comparación ordinal y validez histórica;
  confianza cero incluida. No reclasifica, elimina ni modifica observaciones.
  Recibe la política del contexto, sin sustituir R histórico por un valor fijo.
- `src/GarajeMetria.Domain/Stays/ObservationGroup.cs`: miembros inmutables,
  dirección, primera/última captura y representantes para ambos papeles.
  Equivalencia entre dispositivos, dirección desconocida separada, opuestos
  explícitos que interrumpen repetición y R inclusivo desde la primera captura.
  El cambio de fecha no interviene en la agrupación.
- `tests/GarajeMetria.Domain.Tests/StayObservationGroupingTests.cs`: **17 casos
  nuevos**. R menos/exacto/más un tick; no encadenamiento; primera/última para
  las tres direcciones; cambio de cámara; opuestos en ambos sentidos; mezcla
  desconocida/explícita; empate UUID canónico con recepción invertida;
  medianoche con límite exacto y más un tick; selección por partición/validez
  sin umbral; R=90 histórico frente a R=60; entrada vacía, argumentos e
  inmutabilidad de las colecciones.

CA-003/CP-003 se verifica en selección conjunta de cámaras y aislamiento de
partición; CA-013/014 y CP homólogos, en grupos y representantes conservados;
CA-015/CP-015, en exclusión de inválidas sin filtrar válidas de baja confianza;
CA-024/CP-024, en agrupación que atraviesa medianoche antes de separar visitas.
Son verificaciones del componente, no del cálculo completo ni de consultas
HTTP de auditoría. Se mantienen todas las integrantes y los eventos originales.
No hay desviaciones funcionales respecto a DESIGN-004 / 4.1–4.2.

### 12.2. Comandos y resultados reales

| Verificación | Resultado |
| --- | --- |
| Pruebas focalizadas de Domain, compilando el proyecto afectado | 58 aprobadas, 0 fallidas, 0 omitidas: 17 nuevas y 41 del modelo/fixtures previos. |
| Build Release de la solución | Código 0; 0 advertencias y 0 errores. |
| Pruebas de arquitectura | 14 aprobadas, 0 fallidas, 0 omitidas. |
| Formato de los cuatro archivos C# nuevos | Verificación final código 0, sin cambios. El primer intento restringido no pudo conectar al pipe de MSBuild; se repitió con acceso local adecuado. |
| Revisión documental y diferencias | `git diff --check` sin errores; casillas y referencias de avance actualizadas, cambios previos preservados. |

```powershell
dotnet test tests/GarajeMetria.Domain.Tests/GarajeMetria.Domain.Tests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --filter 'FullyQualifiedName~StayObservationGroupingTests|FullyQualifiedName~VehicleStayTests|FullyQualifiedName~StayValueTests|FullyQualifiedName~StayFixtureTests' --logger trx --results-directory artifacts/agent-checks/2026-11-17-t004-03-domain

dotnet build GarajeMetria.slnx --artifacts-path artifacts/agent-checks/rf004-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false

dotnet test tests/GarajeMetria.ArchitectureTests/GarajeMetria.ArchitectureTests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --logger trx --results-directory artifacts/agent-checks/2026-11-17-t004-03-architecture

dotnet format GarajeMetria.slnx --verify-no-changes --no-restore --include src/GarajeMetria.Domain/Stays/StayObservationOrder.cs src/GarajeMetria.Domain/Stays/ObservationGroup.cs src/GarajeMetria.Domain/Stays/StayObservationGrouping.cs tests/GarajeMetria.Domain.Tests/StayObservationGroupingTests.cs --verbosity minimal

git diff --check
```

TRX locales ignorados por Git, comprobados con sus contadores:

- `artifacts/agent-checks/2026-11-17-t004-03-domain/mrivas_NEU_ITV_INFMI_2026-11-17_11_37_20_net10.0.trx`.
- `artifacts/agent-checks/2026-11-17-t004-03-architecture/mrivas_NEU_ITV_INFMI_2026-11-17_11_39_23_net10.0.trx`.

### 12.3. Límites y trabajo pendiente

La operación recibe un contexto completo para una partición y una política.
La selección de la política histórica y el carry entre páginas corresponden a
T004-11; no se debe agrupar cada página como contexto independiente. La clave
SQL equivalente y su paginación se verifican en T004-08/10. La asociación de
integrantes desconocidas a distintas visitas corresponde a T004-05; conservar
miembros aquí permite resolverla individualmente sin arrastrar todo el grupo.

No se ejecutó SQL ni HTTP para este cambio puro de Domain, ni se restauraron
paquetes. La suite completa no se repitió conforme a AGENTS.md: permanece el
intento diario del 17 de noviembre (584 aprobadas, 1 fallida y 1 omitida) y su
corrección focalizada documentados en sección 10. `full-suite.json` conserva
ese resultado original; no acredita este cambio posterior.

Al cerrar T004-03 estaban completadas T004-01 a T004-03 y pendientes T004-04
a T004-24. T004-04 y T004-07 quedaban habilitadas por sus dependencias.
El avance posterior de emparejamiento y cierre lógico se registra en sección 13.


## 13. Evidencia de T004-04 — 17 de noviembre de 2026

Estado: **completada**. Registro consolidado a 11:49:51 UTC (misma hora en
`Atlantic/Canary`). Se entrega cálculo explícito puro de Domain, sin nuevas
dependencias, I/O, acceso al reloj del sistema ni cambios en RF-003.

### 13.1. Entregables y reglas verificadas

| Entregable | Comportamiento |
| --- | --- |
| `src/GarajeMetria.Domain/Stays/StayDayWindow.cs` | Fecha local y límites UTC semiabiertos suministrados por calendario; normalización UTC y fin posterior al inicio. No presupone días de 24 horas. |
| `src/GarajeMetria.Domain/Stays/ExplicitStayCalculator.cs` | Ordena grupos explícitos; exige una partición, calendario sin fechas repetidas/solapamientos y cobertura para cada grupo. Empareja, mantiene abiertas/pendientes durante el día, aplica cierre lógico al alcanzar End y resuelve política por origen de candidata mediante lookup puro suministrado. |
| `src/GarajeMetria.Domain/Stays/ExplicitStayCandidate.cs` | Resultado sin ID persistido: extremos con fuente/soporte, política, estado/duración y colecciones inmutables de grupos e integrantes. Conserva evidencia intermedia y repetida; no muta eventos, agregados ni resultados previos. |
| `tests/GarajeMetria.Domain.Tests/ExplicitStayCalculatorTests.cs` | 32 casos nuevos de cálculo y validación de contexto, reutilizando reloj/fechas y calendario sintéticos de T004-01. |

Las entradas consecutivas del mismo día mantienen la primera. Sin salida,
al terminar ese día se usa como salida inferida la última lectura del último
grupo distinto. Una única entrada/grupo obtiene salida estimada con X histórico.
Las salidas aisladas permanecen pendientes hasta el cierre; grupos distintos
permiten inferir entrada desde la primera salida y mantener la última como
salida explícita. Un solo grupo repetido requiere estimación de entrada.

La primera captura del grupo inicial determina el día relevante. Una nueva
entrada de otro día inicia candidata distinta después de agrupar; conserva la
estimación/inferencia anterior, incluso si se solapan los tiempos estimados.
Una salida real posterior puede sustituir el cierre virtual/inferido hasta M
histórico inclusive; fuera de M crea candidata pendiente que estima entrada
al cierre de su propio día. Una nueva visita intermedia impide prolongar la
anterior. M no trunca intervalos con salida explícita observada ni ampliaciones
por salidas consecutivas. Un grupo de salida iniciado antes del cierre diario
mantiene ese carácter aunque sus repeticiones atraviesen medianoche.

No se fabrican eventos para estimaciones ni se sobrescribe la dirección de
una lectura usada como extremo inferido. Toda candidata conserva los grupos
explícitos y sus observaciones originales. Las desconocidas siguen disponibles
en la entrada del cálculo y se reservan a T004-05, sin asociarlas prematuramente.
No hay desviaciones funcionales respecto al diseño; DESIGN-004 / 4.6 registra
los nombres concretos y el alcance de esta implementación parcial.

### 13.2. Trazabilidad de pruebas de comportamiento

| CA/CP | Evidencia de Domain |
| --- | --- |
| CA-001 / CP-001; CA-019 / CP-019 | Dos visitas y caso 08:00/08:30/09:30; todos los eventos intermedios/repetidos retenidos, entrada desordenada por recepción independiente. Ubicación/consulta HTTP siguen pendientes. |
| CA-002 / CP-002 | Entrada 23:50/salida 00:20 sin división; duración UTC en ambas transiciones horarias, 48 h transcurridas que equivalen a 49/47 h locales; cierre en medianoche local de verano. |
| CA-005/006; CP-005/006 | Abierta/pendiente hasta End menos un tick; cierre exactamente en End; X=45 histórico y estimaciones que cruzan fecha en ambos sentidos. Un solo grupo repetido no crea visita artificial. Parte sin dirección pendiente de T004-05. |
| CA-007/009; CP-007/009 | Reemplazo de cierre estimado e inferido a M menos/exacto/más un tick; fuera de M conserva candidata antigua y estima otra al cerrar; R/M/X recibidos, con caso histórico M=72 frente a activo M=48. |
| CA-008/023; CP-008/023 | Entradas 23:50 y 00:10, estimación antigua hasta 00:20 sin recorte, salida asociada solo a visita nueva; comparación antes/después del cierre lógico. |
| CA-010 / CP-010 | Salidas consecutivas amplían inmediatamente una visita observada; sin entrada infieren al cierre con soporte original. Entrada futura no se usa para una salida anterior; opuestos permiten visita corta o de duración cero por orden UUID. |
| CA-024 / CP-024 | Entradas repetidas 23:59:40/00:00:10; R exacto y R más un tick; comparar primera estimación antes/después de nuevas lecturas sin segunda visita artificial. |

Se comprueban además capturas adelantadas aceptables sin cambiarlas por el
reloj actual, rechazo de contexto mixto/faltante, conservación de snapshots y
repeticiones de salida que cruzan medianoche con M corto. SQL, admisión RF-003,
concurrencia, ubicación e identidad persistida no se acreditan con estas pruebas.

### 13.3. Verificaciones ejecutadas y resultados

| Verificación | Resultado y evidencia |
| --- | --- |
| Primera ejecución focalizada de Domain | 89 aprobadas, 0 fallidas, 0 omitidas: 31 nuevas y 58 previas; antes de añadir la regresión de salida repetida sobre medianoche. TRX de 11:45:57 UTC. |
| Build Release de solución tras el último cambio | Código 0, 0 advertencias y 0 errores; artefactos separados de la API local en ejecución. |
| Ejecución final focalizada de Domain | 90 aprobadas, 0 fallidas, 0 omitidas: 32 nuevas y 58 previas. TRX de 11:48:26 UTC. No se suman ejecuciones repetidas como pruebas únicas. |
| Arquitectura | 14 aprobadas, 0 fallidas, 0 omitidas. TRX de 11:48:26 UTC. |
| Formato de los cuatro archivos C# nuevos | Código 0, sin cambios; se utilizó acceso al pipe local de MSBuild para evitar la restricción ya conocida del sandbox. |
| Revisión documental y diferencias | Enlaces, casillas y avance actualizados; `git diff --check` sin errores de whitespace. Cambios anteriores ajenos preservados. |

Comandos finales:

```powershell
dotnet build GarajeMetria.slnx --artifacts-path artifacts/agent-checks/rf004-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false

dotnet test tests/GarajeMetria.Domain.Tests/GarajeMetria.Domain.Tests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --filter 'FullyQualifiedName~ExplicitStayCalculatorTests|FullyQualifiedName~StayObservationGroupingTests|FullyQualifiedName~VehicleStayTests|FullyQualifiedName~StayValueTests|FullyQualifiedName~StayFixtureTests' --logger trx --results-directory artifacts/agent-checks/2026-11-17-t004-04-domain-final

dotnet test tests/GarajeMetria.ArchitectureTests/GarajeMetria.ArchitectureTests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --logger trx --results-directory artifacts/agent-checks/2026-11-17-t004-04-architecture

dotnet format GarajeMetria.slnx --verify-no-changes --no-restore --include src/GarajeMetria.Domain/Stays/ExplicitStayCalculator.cs src/GarajeMetria.Domain/Stays/ExplicitStayCandidate.cs src/GarajeMetria.Domain/Stays/StayDayWindow.cs tests/GarajeMetria.Domain.Tests/ExplicitStayCalculatorTests.cs --verbosity minimal

git diff --check
```

La primera ejecución Domain usó ese filtro sin `--no-build`, con destino
`artifacts/agent-checks/2026-11-17-t004-04-domain`. No fue necesario restaurar
paquetes. TRX reales con contadores comprobados, bajo rutas ignoradas por Git:

- `artifacts/agent-checks/2026-11-17-t004-04-domain/mrivas_NEU_ITV_INFMI_2026-11-17_11_45_57_net10.0.trx`.
- `artifacts/agent-checks/2026-11-17-t004-04-domain-final/mrivas_NEU_ITV_INFMI_2026-11-17_11_48_26_net10.0.trx`.
- `artifacts/agent-checks/2026-11-17-t004-04-architecture/mrivas_NEU_ITV_INFMI_2026-11-17_11_48_26_net10.0.trx`.

### 13.4. Límites y siguiente trabajo habilitado

No se repitió la suite completa conforme a AGENTS.md. Se conserva el intento
diario del 17 de noviembre en `full-suite.json`: 584 aprobadas, 1 fallida y 1
omitida, con corrección focalizada posterior documentada en sección 10. Ese
resultado histórico no representa el código nuevo de T004-04. Las pruebas de
esta tarea son puras de Domain y arquitectura, sin SQL ni red.

Los candidatos se reconstruyen desde contexto completo; el resolver de política
debe ser un lookup puro preparado por el llamador y los grupos deben usar el R
histórico apropiado. Selección/reconciliación de contexto histórico, identidad,
fusiones/divisiones y carry entre páginas siguen en T004-06/11. Los límites de
calendario reales se adaptarán en T004-09; el fixture verifica aquí su consumo.
No se crea identidad persistida nueva ni se pierde la anterior por esta tarea:
la asignación de IDs y revisiones se hará al reconciliar candidatos en T004-06.

Al cerrar T004-04 estaban completadas T004-01 a T004-04; T004-05 a T004-24
seguían pendientes y T004-05/T004-07 estaban habilitadas. La ingesta y el
worker aún no invocaban este cálculo. El avance posterior se registra en sección 14.
No se modificaron endpoints, migraciones,
credenciales ni datos de la API del desarrollador.


## 14. Evidencia de T004-05 — 17 de noviembre de 2026

Estado: **completada**. Registro consolidado a 12:06:51 UTC (misma hora en
`Atlantic/Canary`). Se entrega la asociación sin dirección y fallback diario en
Domain, sin paquetes, I/O, reloj del sistema ni cambios en ingesta o persistencia.

### 14.1. Entregables y comportamiento

| Entregable | Comportamiento implementado |
| --- | --- |
| `src/GarajeMetria.Domain/Stays/UnknownStayCalculator.cs` | Interpreta integrantes desconocidas de una partición con eventos únicos; filtra por M histórico al movimiento y desde la entrada antes de elegir proximidad, con desempate captura/UUID. Asocia entre días, amplía/completa extremos con fuente inferida y calcula fallback solo para restos sin candidato. |
| `src/GarajeMetria.Domain/Stays/UnknownObservationAssociation.cs` | Evidencia inmutable por lectura con grupo original y movimiento explícito elegido; null identifica fallback, sin fabricar una dirección. |
| `src/GarajeMetria.Domain/Stays/StayCandidate.cs` | Resultado general inmutable con extremos, política, estado/duración, grupos, observaciones y asociaciones desconocidas; sin ID persistido. |
| `tests/GarajeMetria.Domain.Tests/UnknownStayCalculatorTests.cs` | 39 casos nuevos, incluidos los seis fixtures CA-025 preparados en T004-01 y regresiones de límites, asociación individual, horario local y política histórica. |

La búsqueda usa representantes de grupos explícitos (primera entrada, última
salida), incluidos los grupos intermedios que no quedaron como extremo de la
visita. No trata una repetición, estimación o extremo inferido como un movimiento
explícito nuevo. Cada lectura consulta la misma base explícita inmutable para
que su orden de evaluación no cambie las candidatas de las siguientes.

Cada candidata conserva solo sus integrantes asignadas, mediante subgrupos;
la asociación referencia además el grupo original completo para mantener su
procedencia. Así un grupo puede repartirse entre dos visitas o entre visita y
fallback sin arrastrar otras lecturas ni duplicarlas. La dirección original de
las desconocidas sigue siendo null.

Las observaciones anteriores/posteriores amplían extremos o completan un
extremo ausente/estimado con evidencia inferida. Evidencia observada prevalece
sobre una estimación; una observación interior/empatada mantiene el soporte
previo. Si se amplía solo la contraparte observada de una estimación, esta se
recalcula con X histórico para respetar la invariante del agregado.

Solo las integrantes sin candidato elegible pasan al fallback. Grupos distintos
con primera integrante restante en el mismo día local producen una candidata
de primera a última, cerrada provisional y ampliable desde la segunda lectura
diferenciada por repetición. Un único grupo queda abierto y estima al alcanzar
el fin del día relevante. Un grupo repetido que cruza medianoche no se divide
por fecha; conserva todas sus integrantes. Los límites de día y el snapshot de
fallback se suministran por contexto, sin imponer UTC como fecha local ni
sustituir configuración histórica por parámetros activos dentro del algoritmo.
DESIGN-004 / 4.7 concreta estos nombres y reglas técnicas de la entrega parcial.

### 14.2. Cobertura CA/CP y verificaciones reales

| CA/CP | Pruebas de comportamiento |
| --- | --- |
| CA-004 / CP-004 | Sin dirección 08:00/09:00/16:00/17:00: una candidata 08:00–17:00, cierre inmediato tras la segunda lectura diferenciada y cuatro observaciones retenidas. Un solo grupo hasta R exacto sigue abierto hasta cierre; más un tick permite cierre observado. |
| CA-011 / CP-011 | 07:55 + entrada 08:00 + salida 09:00 + 09:05 produce 07:55–09:05; fuentes inferidas con soporte, direcciones originales intactas. Interiores mantienen extremos explícitos. |
| CA-012 / CP-012 | Proximidad entre visitas, empate al anterior y captura idéntica resuelta por UUID; integrantes de un mismo grupo a ambos lados del punto medio se asignan a visitas distintas. Nueva evidencia explícita permite reasociar sin mutar resultados previos. |
| CA-025 / CP-025 | Entrada 23:50/desconocida 00:05 se asocian entre días; M menos/exacto/más un tick en ambos sentidos. Salida cercana pero entrada fuera de M queda excluida; una candidata más lejana elegible gana tras filtrar, con políticas distintas. |
| Complementarias | M histórico frente a activo, UTC en ambos cambios horarios, fechas locales de verano, grupo dividido entre asociación y fallback, X histórico tras ampliación, eventos únicos, contexto aislado, calendario y argumentos inválidos, colecciones inmutables y ToString sin matrícula. |

| Verificación ejecutada | Resultado |
| --- | --- |
| Primera ejecución focalizada Domain | 119 aprobadas, 0 fallidas, 0 omitidas: 29 nuevas y 90 previas, antes de añadir el último lote de regresiones. |
| Build Release de solución tras los últimos cambios | Código 0; 0 advertencias y 0 errores; salida separada de la API local. |
| Ejecución final focalizada Domain | 129 aprobadas, 0 fallidas, 0 omitidas: 39 nuevas y 90 previas. Los TRX confirman los recuentos; no se suman ejecuciones como pruebas únicas. |
| Arquitectura | 14 aprobadas, 0 fallidas, 0 omitidas. |
| Formato de los cuatro archivos nuevos | Código 0, sin cambios; acceso al pipe local de MSBuild, según restricción conocida del sandbox. |
| Documentación y diferencias | Casilla de tarea, referencias de avance y diseño actualizados; enlaces/bloques revisados y `git diff --check` sin errores. Cambios anteriores ajenos preservados. |

Comandos finales ejecutados:

```powershell
dotnet build GarajeMetria.slnx --artifacts-path artifacts/agent-checks/rf004-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false

dotnet test tests/GarajeMetria.Domain.Tests/GarajeMetria.Domain.Tests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --filter 'FullyQualifiedName~UnknownStayCalculatorTests|FullyQualifiedName~ExplicitStayCalculatorTests|FullyQualifiedName~StayObservationGroupingTests|FullyQualifiedName~VehicleStayTests|FullyQualifiedName~StayValueTests|FullyQualifiedName~StayFixtureTests' --logger trx --results-directory artifacts/agent-checks/2026-11-17-t004-05-domain-final

dotnet test tests/GarajeMetria.ArchitectureTests/GarajeMetria.ArchitectureTests.csproj --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --logger trx --results-directory artifacts/agent-checks/2026-11-17-t004-05-architecture

dotnet format GarajeMetria.slnx --verify-no-changes --no-restore --include src/GarajeMetria.Domain/Stays/UnknownStayCalculator.cs src/GarajeMetria.Domain/Stays/UnknownObservationAssociation.cs src/GarajeMetria.Domain/Stays/StayCandidate.cs tests/GarajeMetria.Domain.Tests/UnknownStayCalculatorTests.cs --verbosity minimal

git diff --check
```

La primera ejecución Domain usó el mismo filtro sin `--no-build`, con destino
`artifacts/agent-checks/2026-11-17-t004-05-domain`. No fue necesario restaurar
paquetes. Evidencia local ignorada por Git, con contadores comprobados:

- `artifacts/agent-checks/2026-11-17-t004-05-domain/mrivas_NEU_ITV_INFMI_2026-11-17_12_02_25_net10.0.trx`.
- `artifacts/agent-checks/2026-11-17-t004-05-domain-final/mrivas_NEU_ITV_INFMI_2026-11-17_12_04_54_net10.0.trx`.
- `artifacts/agent-checks/2026-11-17-t004-05-architecture/mrivas_NEU_ITV_INFMI_2026-11-17_12_04_55_net10.0.trx`.

### 14.3. Límites y siguiente trabajo habilitado

No se ejecutaron SQL ni HTTP para este componente puro de Domain. No se
repitió la suite completa conforme a AGENTS.md: permanece el intento diario
del 17 de noviembre de `full-suite.json`, 584 aprobadas, 1 fallida y 1 omitida,
con corrección focalizada posterior documentada en sección 10. Ese resultado
histórico no se atribuye al código nuevo de T004-05.

El cálculo recibe candidatas/grupos de contexto completo con R histórico
apropiado y lookups puros de política. La selección y reconciliación del
contexto histórico, IDs, fusiones/divisiones y diferencias persistibles sigue
en T004-06/11; el carry entre páginas y presupuesto también queda en T004-11.
La consulta del rastro y su ubicación no se acredita aquí; las observaciones
originales se conservan fuera de la proyección y RF-005 expondrá su consulta.
Estos casos no amplían la admisión de eventos RF-003 ni prueban nuevos eventos
aceptados con 48 horas de retraso: usan lecturas sintéticas ya disponibles.

T004-01 a T004-05 completadas; T004-06 a T004-24 pendientes. T004-06
(recálculo/identidad) y T004-07 (contratos Application) están habilitadas por
sus dependencias. La ingesta y el worker todavía no invocan estos evaluadores;
no se cambiaron endpoints, migraciones, credenciales ni datos de la API local.

## 15. Entrega T004-07 — Contratos y validación Application

Fecha: 17 de noviembre de 2026. El usuario solicitó implementar T004-07 y
aplazar las pruebas hasta que las pida. Este apartado registra exclusivamente
esta entrega; la sección 14 refleja el estado al finalizar T004-05 y no
acredita ni revierte el trabajo de T004-06 que ya existe en el árbol de trabajo.

### 15.1. Código entregado

- `Application/Stays/ReadContracts.cs`: calendario `Atlantic/Canary`, partición
  persistida, ventana UTC semiabierta validada y páginas materializadas de
  observaciones con ubicación/procedencia. Páginas de 1–1000 elementos,
  avance/retroceso con cursor exclusivo captura/UUID canónico y sin `IQueryable`.
  El consumidor conserva grupos y amplía bordes; la consulta no reclasifica,
  filtra confianza ni exige asociación a estancia.
- `Application/Stays/PersistenceContracts.cs`: repositorios de estancias,
  políticas y cierres; entrada nueva frente a reintento, revisiones optimistas,
  lectura paginada de contexto, cambios con linaje, activación explícita y
  descubrimiento de cierres con lotes de 1–100. El contrato de escritura aplica
  diferencias sin borrar datos fuera del contexto y no crea revisiones por no-op.
- `IStayClosureUnitOfWork`: resultado `Outcome<T>`, coordinación catálogo/partición
  persistida y rollback de efectos/progreso ante fallo. No requiere dispositivo
  ni administrador ficticios y no abre transacciones anidadas.
- `Application/Stays/Requests.cs`: `CloseStayDayCommand`, resultado y validador
  asíncrono FluentValidation. Comprueba ID, fecha representable y día terminado
  con `IWorkshopCalendar`/`ILprClock`; no consulta repositorios durante validación.
  El futuro handler debe releer revisión y hora dentro de la transacción.
- `Application/Lpr`: `UpdateDeviceLocationCommand` conserva el marcador
  `ILprAdministrationRequest` y el resultado/versionado existentes; validador de
  ID, versión y descripción no vacía tras trim, hasta 256 caracteres sin
  controles. Alta con propiedad opcional `LocationDescription`; `DeviceDto`
  incorpora descripción e instante nullable mediante propiedades adicionales,
  conservando constructores y deconstrucción posicional anteriores. Se evita
  incluir descripciones libres en `ToString` de los nuevos contratos sensibles.

El registro existente `AddValidatorsFromAssemblyContaining<LoginValidator>`
descubre el validador de ubicación; el behavior asíncrono existente lo ejecuta
antes de handlers. `CloseStayDayValidator` se excluye del escaneo general para
no introducir una dependencia irresoluble del calendario todavía no implementado
en el host actual. `AddStayClosureValidation` permite registrarlo explícitamente
en T004-09 junto al calendario, antes de activar su handler/worker. El comando
de ubicación sigue sujeto al behavior administrativo antes
de acceso al dispositivo. El cierre es interno y no incorpora ese marcador ni
una ruta HTTP. Los puertos son internos a casos de uso: no autorizan acceso por
el mero hecho de suministrar una partición o un taller.

### 15.2. Verificaciones y resultado

- Compilación focalizada de `GarajeMetria.Application.csproj` en Release, con
  `--no-restore`, `--disable-build-servers`, `-m:1`, `/nr:false` y salida aislada
  `--artifacts-path artifacts/agent-checks/rf004-build`: correcta, 0 advertencias
  y 0 errores. Compila Domain y Application; no ejecuta pruebas.
- Revisión manual de firmas, límites, cancelación, referencias y autorización:
  Application utiliza Domain, FluentValidation y los contratos existentes;
  no se introducen EF, HTTP, SQL, `IQueryable`, paquetes ni acceso a datos.
- `git diff --check`: sin errores de espacios; Git avisa de normalización futura
  LF/CRLF en archivos del árbol de trabajo. Los nuevos archivos no versionados
  se revisaron directamente, ya que no forman parte de ese diff.
- Pruebas unitarias de validación sin efectos, compatibilidad y arquitectura:
  **pendientes; no creadas ni ejecutadas en esta entrega por petición expresa**.
  No se ejecutan suite completa, SQL ni HTTP ni se atribuyen resultados anteriores
  al código nuevo. La casilla permanece abierta hasta verificar su cierre.

Verificaciones que deberán solicitarse: descripción nula/opcional/vacía/control/
longitud límite, ID y versión vacíos, fecha inválida/futura y límite exacto de
fin de día con reloj/calendario deterministas; ausencia de llamadas a repositorios
al fallar validación; marcadores de autorización y compatibilidad de DTO;
dependencias de Application. Los adaptadores deberán comprobar orden canónico,
aislamiento, cursores y límites con SQL aislado en T004-10.

### 15.3. Límites y continuación

Esta entrega define los puertos; sus adaptadores y registro scoped están en
T004-09/10. T004-09 debe invocar `AddStayClosureValidation` junto al registro de
`IWorkshopCalendar` y `ILprClock`. No se registra una implementación ficticia del calendario. Los
handlers de ubicación y cierre pertenecen a T004-12 y T004-15; todavía no se
pueden enviar los comandos nuevos como operaciones funcionales. La ampliación
del alta es contractual: su persistencia de ubicación llegará con T004-12 y su
aceptación mediante el parser HTTP con T004-13. No exponer ese campo como
operación efectiva antes de completar ambas tareas. Los DTO existentes devuelven
null para ubicación hasta conectar la persistencia.

No se modifica ingesta, transacciones existentes, endpoints, migraciones ni
datos. T004-08 puede partir de estas firmas; el cierre verificado de T004-07
queda pendiente de las pruebas aplazadas.

## 16. Entrega T004-08 — Mappings y migración aditiva SQL Server

Fecha: 17 de noviembre de 2026. Implementación solicitada con aplazamiento
expreso de pruebas. Se entrega el código y el script; la casilla permanece
abierta hasta ejecutar las verificaciones de cierre cuando el usuario las pida.

### 16.1. Cambios realizados

| Archivo o componente | Entrega |
| --- | --- |
| `Infrastructure/Persistence/StayRows.cs` | Diez filas de persistencia: políticas, configuración activa, particiones, estancias, revisiones, linaje, asociaciones, cierres, versiones de ubicación y ubicación de observaciones. Sin duplicar el payload LPR ni guardar duración creciente. |
| `Infrastructure/Persistence/StayMappings.cs` | Configuraciones separadas con longitudes, precisión UTC 7, fecha local SQL, índices, claves compuestas y FKs restrictivas. Checks de estado/extremos/estimación, cronología, revisiones, causas, JSON de diferencias, ubicación y linaje. |
| `AuthenticationDbContext.cs` | DbSets de las diez tablas, utilizando el contexto scoped existente. No introduce repositorios, transacciones nuevas ni migración automática. |
| `LprMappings.cs` | Puntero sombra nullable `CurrentLocationId`, clave UUID `EventOrderKey` calculada/persistida, claves alternativas de pertenencia, índice de lecturas por taller/matrícula/captura/clave y ampliación de auditoría. |
| `Domain/Lpr/DeviceAdministrationAuditEntry.cs` | Operación `LocationChanged` y metadatos nullable de ubicación en snapshots, conservando firmas anteriores. Valida la coherencia del cambio y exige conservar credenciales/autorización, nombre y actividad; no implementa el caso de uso de ubicación. |
| `PersistenceConnectionInterceptor.cs` | Opciones de sesión SQL al abrir conexiones asíncronas, necesarias para escribir con índices calculados/filtrados. Mantiene la clasificación existente de fallos de conexión. |
| `Migrations/20261117125011_StayPersistence.cs` y `.Designer.cs` | Migración nueva desde RF-003. Up aditivo; Down rechaza borrar historia si ya existe cualquier dato RF-004. Ninguna migración anterior modificada. |
| `Migrations/AuthenticationDbContextModelSnapshot.cs` | Snapshot actualizado con el esquema de esta migración. |
| `docs/configuration/sql/20261117125011-stay-persistence.sql` | Script idempotente de actualización desde `20261113115955_LprPersistence`, generado y revisable, sin aplicar. |
| `DESIGN-004 / 5.1` y guía de migraciones | Detalles reales de mapping, requisitos operativos y límites de recuperación. |

Decisiones técnicas del mapping:

- X/R/M se almacenan como ticks positivos `bigint` de `TimeSpan`, sin pérdida
  de precisión respecto al snapshot de Domain. Las unidades de configuración
  siguen siendo minutos/segundos/horas; conversión, huella y activación son T004-09.
- La identidad ordinal de partición utiliza un índice único sobre bytes UTF-16
  calculados de taller/matrícula. No depende de la collation predeterminada ni
  equipara espacios finales. El índice no usa un predicado sobre columnas
  calculadas. Las columnas de texto de las FKs conservan compatibilidad con
  el esquema anterior; T004-10 debe comprobar correspondencia ordinal exacta
  al resolver y persistir contexto, además de las FKs SQL.
- UUID de orden: hexadecimal canónico de `EventId` convertido a `binary(16)`,
  independiente del orden nativo `uniqueidentifier`. Es calculado/persistido,
  cubre también eventos previos y no cambia su UUID ni sus campos originales.
- Estancias, revisiones y asociaciones guardan taller/matrícula para las FKs
  compuestas de contexto y eventos de soporte. Linaje, absorciones y asociaciones
  referencian estancias de la misma partición. La pertenencia de un extremo a
  su estancia, su clasificación/dirección y la igualdad de captura se validarán
  también en Domain/adaptadores bajo lock; las FKs no sustituyen esas reglas.
- Políticas, ancla/creación/orden/política de estancias y versiones de ubicación
  se protegen contra cambios mediante propiedades EF inmutables. Los futuros
  adaptadores no deben eludir esta protección con escrituras masivas.
- Ubicación actual, versión y vínculo de evento usan FKs compuestas por
  dispositivo/taller. La historia de ubicación no depende de auditorías sujetas
  a limpieza. No se precargan políticas, ubicaciones, estancias ni credenciales;
  las observaciones antiguas siguen sin ubicación atribuida.

### 16.2. Verificaciones realizadas

- Compilación focalizada final de Infrastructure y sus dependencias Domain/
  Application en Release: **correcta, 0 advertencias y 0 errores**. Comando:

```powershell
dotnet build src/GarajeMetria.Infrastructure/GarajeMetria.Infrastructure.csproj `
    --artifacts-path artifacts/agent-checks/rf004-build --no-restore `
    --configuration Release --disable-build-servers -m:1 /nr:false
```

- EF Core 10.0.12 generó correctamente la migración y el snapshot mediante
  `dotnet ef migrations add StayPersistence`, con proyecto/startup Infrastructure,
  contexto `AuthenticationDbContext`, salida `Persistence/Migrations`, Release
  y `--no-build`. Se utilizaron los artefactos aislados mediante `ArtifactsPath`.
  La fábrica recibió una cadena sintética de diseño; no se abrió una conexión.
- Generación del script idempotente: correcta con `dotnet ef migrations script
  20261113115955_LprPersistence 20261117125011_StayPersistence --idempotent`,
  mismo proyecto/contexto/Release y `--no-build`. Comando reproducible en la
  [guía de migraciones](../configuration/migration-procedure.md#migración-de-estancias--t004-08).
  Esto genera SQL; no lo ejecuta contra un motor.
- Revisión manual del Up, Down, snapshot y script: altas aditivas, FKs sin
  cascadas, claves de pertenencia e índices revisados; reemplazo del check de
  auditoría, sin cambios a columnas previas de carga LPR ni operaciones DML
  sobre observaciones/credenciales. Se eliminó el filtro automático de EF sobre
  el índice de identidad calculada, incompatible con ese tipo de columna SQL.
- `git diff --check`: correcto, sin errores de espacios. Git solo advierte de
  futura normalización LF/CRLF en archivos del árbol de trabajo. Los archivos
  nuevos no versionados se revisaron directamente; no aparecen en ese diff.

No se han creado ni ejecutado pruebas unitarias, de arquitectura, HTTP o SQL,
ni se ha ejecutado la suite completa, conforme a la instrucción del usuario.
No se ha aplicado Up ni Down, no se ha reiniciado la API ni modificado datos.
Los resultados anteriores de otras tareas no acreditan este esquema nuevo.

### 16.3. Verificaciones aplazadas y continuación

Pendientes hasta que el usuario las solicite:

1. Migración de cadena completa sobre SQL Server aislado vacío y actualización
   desde RF-003 con observaciones, dispositivos, credenciales y auditorías sintéticas;
   comprobar conservación exacta y ubicaciones históricas desconocidas.
2. Constraints de nulabilidad/extremos/UTC/revisiones, unicidad, FKs compuestas,
   rechazo entre particiones/dispositivos/talleres y ausencia de borrados en cascada.
3. Comparación del orden SQL UUID con bytes de red de Domain, incluyendo
   empates de captura; identidad ordinal con diferencias de caso/acentos/espacios.
4. Escritura y consultas reales con las opciones de sesión requeridas,
   cancelación/fallos y rollback; modelo/snapshot coherentes.
5. Protección de inmutabilidad, guardia Down con datos y retroceso sobre esquema
   nuevo vacío; recuperación operativa con datos mediante procedimiento revisado.
6. Compatibilidad de las operaciones de auditoría anteriores y de
   `LocationChanged`, y limpieza administrativa que conserva las ubicaciones.

T004-09/10 pueden partir del esquema y firmas entregados. Siguen pendientes
calendario/opciones/política activa, repositorios, coordinación y persistencia
de recálculo, ubicación funcional, ingesta y cierre. T004-12 debe insertar primero
la versión histórica antes de actualizar el puntero del dispositivo, dentro de
la misma transacción; al crear un dispositivo, guardar primero el dispositivo
con puntero null para respetar las FKs restrictivas del ciclo dispositivo/ubicación.
El formato de diferencias de revisión y su reconstrucción se concretan en
T004-10/11; `ChangesJson` no es un almacén alternativo de eventos.

El script entregado es solo el salto RF-003 → RF-004; una base vacía necesita
la cadena previa. El nuevo binario requiere migración antes de usar el contexto
actualizado. No hay autorización ni ejecución de despliegue. `Down` no es una
reparación automática: con datos se detiene y exige un procedimiento revisado
de preservación/restauración. T004-08 queda implementada con cierre verificado
pendiente, conforme al aplazamiento expreso de pruebas.

## 17. Entrega T004-09 — Calendario, opciones y activación de políticas

Fecha: 17 de noviembre de 2026. Código implementado a petición del usuario;
pruebas aplazadas hasta que las solicite. La casilla se mantiene abierta por
verificación pendiente. No se ha activado una política ni accedido a una base
de datos para esta entrega.

### 17.1. Cambios realizados

| Archivo o componente | Entrega |
| --- | --- |
| `Infrastructure/Stays/StayOptions.cs` | Opciones tipadas e inmutables con valores iniciales 30 min / 60 s / 48 h, polling 30 s, lote 100 y página 500. Conversiones enteras comprobadas a ticks; huella SHA-256 canónica, independiente de cultura, GUID y valores operativos. Zona y versión fijadas en código. |
| `Infrastructure/Stays/WorkshopCalendar.cs` | Implementación `IWorkshopCalendar` con resolución de `Atlantic/Canary`, fecha local y límites UTC semiabiertos obtenidos desde dos medianoches locales. No supone días de 24 horas. Rechaza fechas extremas y límites locales irresolubles. |
| `Infrastructure/Stays/StayConfigurationStartup.cs` | Validación local al arrancar: valores, rangos operativos y sumas/restas de duraciones alrededor del día actual con `TimeProvider`. Sin SQL, creación ni activación automática de políticas. |
| `Infrastructure/Persistence/StayPolicyRepository.cs` | Puerto `IStayPolicyRepository`: resolución activa con comprobación de huella/coincidencia local, lectura histórica sin sustituir valores y activación explícita con expectativa de versión, bloqueo de catálogo, transacción Serializable, timeout y protección de snapshots. |
| `Infrastructure/Persistence/PolicyCheckedLprEventRepository.cs` | Decorador del puerto LPR que comprueba política solo para eventos inexistentes y exige alta dentro de la misma transacción verificada. Ausencia/discrepancia produce indisponibilidad; duplicados/conflictos conservan comparación y recibos RF-003. |
| `Infrastructure/DependencyInjection.cs` | Registro de opciones, calendario singleton, validación de arranque, repositorio scoped y decorador de eventos. Invoca `AddStayClosureValidation` ahora que el calendario está disponible. No añade worker de cierre ni captura DbContext en singletons. |
| `Application/Stays/PersistenceContracts.cs` | Documentación del contrato de admisión bajo coordinación de catálogo, bootstrap con expectativa vacía, conflicto de versión sin cambios y no-op/reutilización de huella. Firmas conservadas. |
| `Api/appsettings.json` | Sección `Stays` con los seis valores iniciales documentados, sin secretos ni parámetros por taller. |
| `docs/configuration/sql/activate-stay-policy.sql` | Script preparado para activación manual con parámetros de negocio, versión esperada, reloj UTC SQL, huella compatible, rollback y bloqueo compartido con ingesta. No ejecutado. |
| Guía `stays-configuration.md`, migraciones, diseño y catálogo | Configuración, procedimiento de activación/despliegue, comportamiento de admisión y estado de las verificaciones. |

Detalles de comportamiento:

- Todas las duraciones deben ser positivas. Lote 1–100 y página 1–1000; el
  polling entero debe caber en el temporizador .NET. Desbordamientos de ticks
  se rechazan con diagnóstico seguro. El arranque comprueba también aritmética
  alrededor del día actual; no acredita operaciones en todas las fechas históricas.
- La zona se resuelve durante el registro; el calendario transforma cada
  medianoche separadamente y puede devolver días de distinta duración. Las
  pruebas de cambios de hora siguen pendientes, no se atribuyen resultados a
  esta compilación.
- Una huella representa X/R/M en ticks, zona y algoritmo completos. Políticas
  idénticas reutilizan su ID persistido; un ID existente con otros valores no
  puede reemplazarse. `FindAsync` no exige coincidencia con las opciones activas,
  preservando el snapshot histórico para T004-11.
- `ActivateAsync` requiere contexto limpio sin transacción previa y un snapshot
  coincidente con las opciones del proceso de despliegue. Versión vacía espera
  configuración inexistente; una versión obsoleta devuelve false sin escrituras.
  Misma política activa con expectativa vigente es no-op. Una activación real
  cambia solo configuración activa/versión/instante y, si procede, agrega una
  política inmutable; no modifica estancias ni políticas anteriores.
- El decorador de ingesta verifica bajo la transacción que ya mantiene
  `workshops:catalog`, tras la autorización y comparación existentes. La
  activación usa ese mismo bloqueo, evitando cambiar la política entre
  comprobación y commit. No conserva una autorización válida entre transacciones.
  No se modifica el handler RF-003 ni se incorpora aún recálculo de estancias.
- La puerta de admisión se aplica a todas las lecturas nuevas, incluidas
  inválidas. Falta de política o discrepancia de valores devuelve 503 mediante
  el error de persistencia existente. Los logs indican ausencia/discrepancia
  sin exponer valores, matrículas ni payloads. No se altera el contrato de health
  ni se incorpora un sondeo nuevo; la comprobación compartida ocurre en admisión.

### 17.2. Verificaciones realizadas

Compilación focalizada final de Infrastructure y sus dependencias Domain/
Application: **correcta, 0 advertencias y 0 errores**, con el siguiente comando:

```powershell
dotnet build src/GarajeMetria.Infrastructure/GarajeMetria.Infrastructure.csproj `
    --artifacts-path artifacts/agent-checks/rf004-build --no-restore `
    --configuration Release --disable-build-servers -m:1 /nr:false
```

Revisión manual de contratos, DI, cancelación, huella, condiciones de no-op,
versión esperada, orden de bloqueo y separación de política histórica/activa.
`git diff --check` sin errores de espacios; solo avisos de normalización futura
LF/CRLF de Git. Los archivos nuevos no versionados se revisaron directamente.

No se han creado ni ejecutado pruebas de ningún tipo, suite completa, SQL o
HTTP, conforme a la instrucción expresa. No se han ejecutado migraciones,
arrancado/reiniciado la API, modificado configuración privada ni activado una
política. No hay evidencia aún de funcionamiento en el motor o de disponibilidad
de la zona en el host destino. Las migraciones de T004-08 no cambian.

### 17.3. Verificaciones pendientes y continuación

Cuando el usuario solicite pruebas:

1. Opciones nulas/ausentes, ceros, negativos, límites de lote/página/temporizador,
   desbordamientos de ticks y de aritmética de fecha con reloj determinista.
   Cambios operativos no cambian la huella; cambios de X/R/M sí.
2. Calendario en primavera/otoño, medianoche y capturas con offset, días de
   duración distinta, zona no disponible y fechas extremas. Verificación en el
   entorno Windows/IIS previsto.
3. SQL aislado: bootstrap, no-op, expectativa obsoleta, políticas de igual huella,
   conflicto de ID, rollback/cancelación y activaciones concurrentes; snapshots
   históricos completos e inalterados tras un cambio de activa.
4. Dos instancias con parámetros distintos: rechazo seguro de escrituras nuevas
   desde la discrepante y ausencia de carrera entre validación y activación.
   Reintento idéntico conserva recibo; conflicto conserva 409 sin consultar activa.
5. Adaptar los fixtures sintéticos de HTTP que admiten eventos para activar
   explícitamente la política en su preparación aislada; no añadir seed de
   producción ni una activación al arrancar para hacer pasar esos casos.
6. Registro de validadores de cierre sin dependencias irresolubles, autorización
   previa de ingesta y arquitectura/ausencia de datos sensibles en errores/logs.

La [guía de configuración](../configuration/stays-configuration.md) detalla la
activación y la coordinación de nodos. El despliegue debe aplicar primero el
esquema y preparar una política coincidente antes de admitir eventos nuevos;
esta entrega no ejecuta ninguna de esas operaciones. Durante una discrepancia
los eventos nuevos reciben 503; la configuración en memoria requiere reiniciar
o redesplegar el nodo con los valores acordados.

T004-10/11 incorporarán repositorios restantes, coordinación por partición y
recálculo; T004-14 conectará cálculo a ingesta y T004-15/16 el cierre. Las
constantes de algoritmo/zona no habilitan otras versiones: cualquier evolución
debe mantener evaluadores históricos explícitos. T004-09 queda implementada,
con cierre verificado pendiente de las pruebas aplazadas.


## 18. Integración de T004-06 a T004-24 — 17 de noviembre de 2026

La petición de completar todas las tareas y ejecutar pruebas sustituye el
aplazamiento anterior de T004-07/08/09. Las secciones 10 a 17 conservan la
evidencia histórica de cada entrega; los pendientes de aquellas fechas se
resuelven o delimitan aquí, sin atribuirles resultados posteriores.

| Tareas | Cambios implementados | Verificación preparada |
| --- | --- | --- |
| 06–07 | Reconciliación pura, identidad/ancla, fusión por primera creación, división con snapshot completo; comandos y validación asíncrona | `StayRecalculatorTests`, `StayCommandValidationTests` |
| 08–10 | Mappings aditivos, orden UUID de red, repositorios exactos por taller/matrícula, keyset, calendario, políticas CAS, UOW catálogo/partición | `StayMigrationTests`, `StayConfigurationTests`, `StayPolicyPersistenceTests`, `StayPersistenceTests` |
| 11 | Servicio incremental con contexto conservador por fechas y movimientos vecinos, carry entre páginas, diferencias JSON antes/después, linaje, presupuestos y cierres por estancia | `StayRecalculationServiceTests`, SQL de fusión/división/fallos/no-op |
| 12–13 | Versiones inmutables de ubicación, captura histórica/futura/desconocida, alta compatible, GET ampliado, PUT administrativo con If-Match y OpenAPI | `DeviceLocationTests`, `LprHttpStayTests`, auditoría SQL |
| 14 | Ingesta nueva incluye la observación guardada, ubicación y cálculo antes de 201; inválidas auditables, idénticos 200 sin efectos nuevos | `ReceiveLprEventTests`, `LprHttpStayTests`, `LprStayIngestionTests` |
| 15–16 | Cierre por revisión, checkpoint y siguiente vencimiento; worker supervisado por unidad, varias instancias y avance del cursor tras fallos | Validación temporal, SQL de cierre/ingesta con barreras, recuperación y host real |
| 17 | CLI `GarajeMetria.StayMaintenance`: diagnose/activate/project/close-due, reanudación por efectos persistidos y confirmación de ingesta detenida | `StayMaintenanceToolTests` ejecuta procesos reales y reanuda una tanda interrumpida |
| 18–20 | Barreras SQL entre contextos/hosts, rollback antes de commit, acuse perdido después, logs y métricas acotadas, diagnóstico seguro y rastro interno paginado | Pruebas RF-003 afectadas, concurrencia, aislamiento, HTTP y arquitectura |
| 21–22 | Ensayo desde esquema RF-003, datos originales intactos, guardia Down, activación y carga pausada de referencia con dos cámaras e historial | `StayMigrationTests`, `StayPolicyPersistenceTests`, `StayReferenceLoadTests` |
| 23–24 | Runbook, configuración, flujo HTTP sintético y consolidación de trazabilidad | Resultados reales de sección 19 y límites explícitos de RF-005/006 |

No se añaden tablas para checkpoint inicial: la revisión/asignación por evento
y el vínculo de ubicación conservan el avance confirmado. No se renormalizan
lecturas anteriores ni se inventa su ubicación. Los grupos originales sin
dirección se guardan una vez por revisión, referenciados por su hash, evitando
repetir listas completas por cada integrante asociado.

Los vencimientos se actualizan incluso en recálculos sin diferencias de estancia;
se conserva el próximo cierre de las estancias fuera de la región recalculada.
La expansión sigue salidas explícitas vecinas sin imponer M como límite general
de duración observada ni cargar todas las fechas intermedias de un hueco largo.
Los bordes históricos sin cambios detienen la expansión; superar el presupuesto
revierte todo el caso de uso.

Documentación operativa: [runbook](../runbooks/stays.md),
[configuración](../configuration/stays-configuration.md) y
[flujo HTTP](../examples/RF-004-stays.http). Este flujo está automatizado por
HTTP/SQL; no se afirma haber ejecutado un cliente manual o cámaras reales.

## 19. Verificación final, matriz y límites

La verificación comprende el intento completo expresamente solicitado y
repeticiones focalizadas de los casos afectados. No se presenta la suite
completa fallida como una ejecución íntegra posterior superada.


### 19.1. Trazabilidad concreta de CA/CP

Los resultados de ejecución aplicables a esta matriz se registran en 19.2.
Los nombres son clases/métodos reales de `tests/`; no equivalen por sí solos
a evidencia de ejecución. «HTTP RF-005» identifica una parte aún no expuesta.

| CA / CP | Evidencia implementada | Límite de cobertura |
| --- | --- | --- |
| 001 | `ExplicitStayCalculatorTests.SeveralVisitsRetainIntermediateAndRepeatedObservationsWithoutExtraStays` | Cálculo puro y persistencia compartida |
| 002 | `MissingEntrySearchIncludesThePreviousDayAndAllowsCrossMidnightDuration`, `FortyEightHoursIsElapsedUtcAcrossBothClockChanges`, `StayConfigurationTests` | Calendario local y duración UTC; sin ensayo IIS corporativo |
| 003 | `StayObservationGroupingTests`, `StayPersistenceTests.KeysetPagesUseCanonicalUuidBytesInBothDirectionsAndIncludeLowConfidence`, carga con dos cámaras | Misma partición y aislamiento ordinal SQL |
| 004 | `UnknownStayCalculatorTests.DailyFallbackClosesFromFirstToLastAndExpandsWithoutInventingIntermediateVisits` | Cierre provisional ampliable |
| 005 | `MissingBoundaryRemainsIncompleteUntilExactlyTheLocalDayEnd`, `OneRepeatGroupStaysOpenUntilClosureInsteadOfCreatingAShortArtificialVisit` | Tick exacto y grupo único |
| 006 | `EstimatesCrossMidnightUsingHistoricalXWithoutClipping` | Entrada estimada sin recorte |
| 007 | `RealExitReplacesVirtualOrInferredExitOnlyInsideInclusiveHistoricalM` | Límite M y M+tick |
| 008 | `NextDayEntrySeparatesVisitsAndKeepsOverlappingEstimateRegardlessOfClosureTiming` | Nueva visita sin prolongar la previa |
| 009 | `ConsecutiveGroupsInferTheMissingBoundaryOnlyAtDailyClose` | Entradas consecutivas y procedencia |
| 010 | `NewEntryAfterExplicitExitStartsAnotherVisitAndConsecutiveExitsExtendTheLastOne`, `MDoesNotImposeAGeneralMaximumOnObservedIntervals`, prueba Application de hueco largo | Salidas consecutivas, incluidas observadas más allá de M |
| 011 | `UnknownObservationsExpandBothExplicitBoundsAndPreserveTheirOriginalDirections` | Extremos inferidos; dirección original intacta |
| 012 | `MembersOfOneUnknownGroupChooseDifferentVisitsAcrossTheProximityMidpoint`, `NewExplicitEvidenceCanReassignUnknownReadingsWithoutMutatingEarlierResults` | Asociación individual, empate y revisión |
| 013 | `StayObservationGroupingTests`, `RepetitionAndVisitStateCrossKeysetPages` | R inclusive desde la primera, sin encadenamiento |
| 014 | `AnExitCannotUseAFutureEntryAndOppositeMovementsCanFormAShortVisit`, agrupamiento de representantes | Se conservan ambos movimientos y lecturas |
| 015 | `ReceiveLprEventTests`, `InvalidReadingsRemainAuditableByOriginalTextAndDeviceWithoutAStayPartition`, keyset SQL con confianza cero | Conservación y consulta interna; búsqueda HTTP en RF-005 |
| 016 | `AnEarlierAcceptedReadingUpdatesTheBoundaryWithoutChangingIdentityOrAnchor`, `LprHttpStayTests`, regresiones de ventana RF-003 | Recálculo de tardías aceptadas; M no admite capturas rechazadas |
| 017 | `ValidIngestionIsAtomicAndExplicitRetryReconcilesConfirmedRollbackOrUncertainCommit`, regresiones concurrentes RF-003 | Sin éxito antes de commit, rollback y acuse perdido |
| 018 | `ExistingPolicyAndIdentitySurviveAnActivePolicyChange`, `StayPolicyPersistenceTests`, corte de política en carga | Snapshot completo; nodo discrepante rechaza nuevas escrituras |
| 019 | `SeveralVisitsRetainIntermediateAndRepeatedObservationsWithoutExtraStays`, `LocationHistoryAndSynchronousStaysAreVisibleAtTheIngestionSuccessBoundary`, rastro interno SQL | Lectura intermedia y ubicación conservadas; consulta HTTP RF-005 |
| 020 | `DeviceLocationTests`, `LocationUpdateAndIngestionUseOneDeviceOrderAndRetryKeepsTheOriginalBinding`, `LocationAuditFailureRollsBackVersionPointerHistoryAndDeviceMutation` | Historia inmutable, no-op, captura tardía/futura y desconocida |
| 021 | `DailyClosureAndNewExitSerializeAcrossHostsAndPreserveTheRealBoundary`, `TwoInstancesProjectAndCloseOnePartitionWithoutDuplicatingHistory`, CLI y worker en carga | Recuperación/serialización en SQL sintético; sin backup físico |
| 022 | HTTP de permisos administrativos/identidad dispositivo, rastro interno con taller exacto, logs/métricas sin datos libres | Autorización HTTP del historial completo pendiente de RF-005; no se declara el CA íntegro |
| 023 | `NextDayEntrySeparatesVisitsAndKeepsOverlappingEstimateRegardlessOfClosureTiming`, fixture `separate-days` en Application | Separación por día, antes/después del cierre |
| 024 | `MidnightRepeatIsGroupedBeforeDaySeparationAndDailyClosure`, fixtures `repeat-*`, páginas de tamaño uno | R exacto y grupo entre medianoches |
| 025 | `PreparedAssociationRegressionsNowProduceTheExpectedVisits`, pruebas de elegibilidad antes de proximidad, fixtures Application | M exacto/+tick, desde entrada/movimiento, empate al anterior |
| 026 | `StayRecalculatorTests.FusionKeepsTheFirstCreatedIdentityAnchorAndEntirePolicyRatherThanEarliestCapture`, `FailedFusionRollsBackEveryPhaseAndRetryRetainsFirstCreatedIdentityAndPolicy` | Orden de creación, política y linaje persistido |
| 027 | `SplitAllPartsInheritOriginalPolicyAndTheLaterAnchorPartKeepsIdentity`, `SplitPreservesLaterAnchorAndHistoricalPolicyAndRetryDoesNotCreateMoreParts` | Ancla tardía, snapshot heredado, segunda ejecución sin IDs nuevos |

### 19.2. Comandos, resultados y artefactos

| Comprobación | Resultado real |
| --- | --- |
| Restore de la herramienta con `--artifacts-path artifacts/agent-checks/rf004-build --disable-parallel -m:1 /nr:false` | Correcto; sin paquetes nuevos. Dos intentos iniciales sin salida diagnóstica se resolvieron deshabilitando reutilización de nodos. |
| Build Release de `GarajeMetria.slnx` con salida aislada, `--no-restore --disable-build-servers -m:1 /nr:false` | Correcto, 0 advertencias y 0 errores, incluidas las correcciones finales. Compilaciones intermedias detectaron errores de construcción de pruebas, corregidos antes de ejecutarlas. |
| `dotnet format GarajeMetria.slnx --verify-no-changes --no-restore` | Correcto tras formatear archivos modificados y la nueva migración. MSBuild necesitó acceso a canalizaciones locales fuera del sandbox. Verificación focalizada final correcta; se corrigió además una diferencia de sangrado en el ensayo de migración. |
| EF `migrations has-pending-model-changes` sobre los ensamblados compilados de Infrastructure | Correcto: «No changes have been made to the model since the last migration». Comparación sin conectar a SQL, usando factory y conexión ficticia. |
| Suite completa explícita, `2026-11-17-rf004-final/*.trx` | 811 casos: 623 pasaron, 187 fallaron, 1 omitido. Domain: 335/335; arquitectura: 14/14; Application: 137/142; integración: 137/320 y 1 omitido. SQL local falló por SSPI en sandbox y dos pruebas TLS locales por timeout; no se acreditan como superadas. |
| Repetición focalizada `StayRecalculationServiceTests`, `rf004-application-recovery/*.trx` | 19/19 correctas. Cinco expectativas de fixtures se adaptaron a proyectar cada observación guardada: una semilla no proyecta días de buffer independientes. Los otros 123 casos Application conservan su resultado de la suite. |
| Primera repetición SQL `StayPersistenceTests`, `rf004-sql-recovery/*.trx` | 7 correctas, 1 fallida por comparar el orden GUID de memoria con el nativo SQL. Aserción corregida para comparar el conjunto ordenado en memoria. |
| Repetición de integración afectada por SSPI, con acceso Windows local autorizado, `rf004-integration-recovery/*.trx` | 316 correctas, 3 fallidas, 1 omitida. Incluye migraciones, políticas/script real, CLI real, rollback, fusión/división, paginación, autorización, dos hosts y TLS local. Las tres fallidas motivan la repetición focalizada siguiente. |
| `git diff --check` y enlaces documentales relativos | Sin errores de whitespace ni destinos inexistentes en los documentos RF-004 revisados. |

Correcciones de las tres pruebas: OpenAPI pasa a 18 rutas y verifica el contrato
PUT de ubicación; el recibo de reintento comprueba `duplicate=true` y conserva
los demás campos; la carga observa commits de ambos workers, porque el primero
podía cerrar antes del segundo. Además, el diagnóstico devuelve indisponibilidad
segura cuando falta el esquema SQL; el ensayo de migración lo comprueba antes
de aplicar Up. Las cinco pruebas afectadas han pasado en la repetición final
`rf004-corrections/*.trx` (5/5, 1 min 49 s).

El intento completo y el anterior del día se conservan en
`artifacts/agent-checks/full-suite.json` y
`2026-11-17-full-before-rf004-completion.json`. La petición expresa de pruebas
permitió el nuevo intento; tras su fallo se repitieron únicamente las capas/casos
afectados, sin presentar una nueva suite completa como ejecutada.



Resultado consolidado por nombre de caso y última evidencia aplicable:
**810 superadas, 0 fallidas pendientes y 1 omitida, de 811 casos únicos**.
Domain 335, Application 142, arquitectura 14 e integración 319 superadas más
la omitida LDAPS. No se suman ejecuciones repetidas como casos diferentes.
El detalle por caso/TRX queda en el artefacto local ignorado
`artifacts/agent-checks/rf004-verification-summary.json`. El registro diario
conserva el estado fallido del intento completo y enlaza este seguimiento;
no se transforma ese intento en una supuesta suite íntegra posterior exitosa.

Comandos finales reproducibles (salida separada para evitar bloquear la API local):

```powershell
dotnet build GarajeMetria.slnx --artifacts-path artifacts/agent-checks/rf004-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false
dotnet format GarajeMetria.slnx --verify-no-changes --no-restore
dotnet test GarajeMetria.slnx --artifacts-path artifacts/agent-checks/rf004-build --no-build --no-restore --configuration Release --disable-build-servers -m:1 /nr:false --logger trx --results-directory artifacts/agent-checks/2026-11-17-rf004-final
```

La última línea es el intento completo real, sujeto a frecuencia/autorización;
no una indicación de repetirlo después de cada cambio. Las repeticiones
posteriores usaron el proyecto Application con filtro de servicio y el de
IntegrationTests para las capas/casos afectados descritos en la tabla.

### 19.2.1. Medidas de T004-22

Artefacto real `artifacts/agent-checks/rf004-reference-load.json`, generado solo
tras comprobar persistencia y recuperación. Windows local, .NET 10.0.12,
8 procesadores visibles, SQL Server real aislado y WebApplicationFactory.
Datos: 1200 lecturas históricas paginadas, 40 eventos nuevos válidos, 4 reintentos
idénticos, 2 cámaras, repeticiones y retrasos de segundos dentro de RF-003;
cambio explícito de X=30 a 45 y cierre histórico recuperado por worker.

| Medida | Valor observado |
| --- | --- |
| Intervalo medido / cadencia efectiva | 83,255 s / 28,827 eventos nuevos por minuto |
| Latencia HTTP mediana / p95 / máxima | 266,599 / 1761,576 / 3095,438 ms |
| Retención catálogo p95 / máxima | 1319,262 / 1703,957 ms |
| Espera catálogo p95 | 4,084 ms |
| Cálculo p95 | 955,618 ms |
| Memoria gestionada antes / después | 40.636.832 / 44.921.200 bytes |
| Working set antes / después / pico del proceso | 229.539.840 / 259.710.976 / 270.209.024 bytes |
| Asignación gestionada durante el intervalo | 128.981.016 bytes |
| Resultado | 1240 eventos y asignaciones; 4 duplicados sin efectos nuevos; cierre confirmado y vencimiento limpio; corte de política correcto |

El historial sembrado es de tres días antes, con trabajo de cierre vencido
forzado para el ensayo. El cálculo inicial de ese historial y la recuperación
exploran una ráfaga sintética, sin afirmar un techo de capacidad. Las pruebas
de presupuesto agotado acreditan rollback completo y resultado 503, sin
truncar el cálculo. La retención del catálogo llega a 1,704 s en este ensayo:
el bloqueo global puede limitar una carga mayor; estas medidas no son un SLA
ni justifican eliminar coordinación sin una decisión y prueba adicionales.


### 19.3. Alcance del cierre y comprobaciones externas

El cierre de estas tareas corresponde al backend de cálculo, persistencia e
ingesta de RF-004. No declara RF-004 íntegramente verificado: CA-015/019/022
conservan su parte de consulta/autorización HTTP del rastro para RF-005. El
puerto interno incluye inválidas, texto original, dispositivo y ubicación, con
límites y orden determinista; no se inventan rutas públicas ni se habilita
acceso de dispositivo al historial. RF-006 definirá correcciones y su auditoría.

El único caso omitido por prerrequisito externo es LDAPS con AD aislado;
las pruebas TLS locales y dobles no lo sustituyen. No se han probado cámaras
reales, IIS corporativo, restore físico ni capacidad productiva. El ensayo
mide un host local con SQL Server real aislado y datos sintéticos; sus métricas
no constituyen SLA. La migración/activación compartida y el despliegue quedan
como operaciones explícitas del entorno, descritas en el runbook.

Se preservan las modificaciones previas de prioridades/alcance RF-002/RF-003.
No se han realizado commits, publicación de ramas, PR ni despliegues.
