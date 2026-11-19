# RF-005 — Consulta de vehículos e historial

| ID | Requerimiento | Prioridad | Estado |
| --- | --- | --- | --- |
| RF-005 | Consulta de vehículos e historial | Media | Backend implementado; verificaciones y límites en TASKS-005 |

## 1. Objetivo y alcance

Consultar los eventos LPR, los vehículos presentes, el historial de estancias
y una matrícula, respetando el ámbito autorizado del usuario. La consulta de
eventos LPR es el caso de uso de mayor importancia operativa dentro de RF-005.
Decisiones funcionales acordadas el 19 de noviembre de 2026.

Referencias: [RF-002 — Permisos](RF-002-roles-and-workshop-access.md),
[RF-003 — Eventos LPR](RF-003-lpr-event-ingestion.md),
[RF-004 — Estancias](RF-004-arrivals-departures-and-stays.md),
[DESIGN-004](../design/DESIGN-004-arrivals-departures-and-stays.md) y
[RF-009 — Talleres](RF-009-workshop-catalog.md).

Incluye consultas paginadas, detalle, recorrido registrado de una matrícula,
exportación Excel y auditoría de consultas y exportaciones. Las correcciones
manuales pertenecen a RF-006; las imágenes y sus permisos específicos, a RF-007;
el panel, a RF-008. Esta especificación no acredita endpoints, interfaz,
exportaciones ni pruebas por sí misma; la implementación y evidencia efectiva
se registran en TASKS-005.

## 2. Actores y permisos

| Actor | Consultas y exportaciones |
| --- | --- |
| Asesor | Las cuatro consultas, su detalle y las lecturas inválidas o sin estancia, exclusivamente en su taller asignado. Exportación Excel dentro del mismo ámbito. |
| Administrador | Las mismas operaciones para uno, varios o todos los talleres, incluido el historial de talleres y dispositivos inactivos. Puede consultar el recorrido registrado entre talleres. |
| Dispositivo LPR | Su credencial de ingesta no concede acceso a estas consultas ni exportaciones. |

Aplicar RF-002 a cada operación y recurso, incluidos detalle, recuentos,
selectores, navegación y archivos exportados. Un filtro enviado por el cliente
no concede permisos. La selección administrativa no cambia el taller asignado.
La consulta de datos originales no habilita imágenes para el Asesor.

## 3. Consultas y datos

| Consulta | Resultado funcional |
| --- | --- |
| Eventos LPR | Todas las observaciones conservadas y autorizadas que cumplan filtros, incluidas inválidas, repetidas, intermedias y sin estancia. |
| Vehículos presentes | Únicamente vehículos con estancia abierta según RF-004 al momento de consulta. Una salida estimada o una salida pendiente no acredita presencia. |
| Historial de estancias | Estancias cuyo intervalo coincida en cualquier momento con el intervalo solicitado, aunque su entrada sea anterior. |
| Búsqueda de matrícula | Búsqueda exacta o parcial y acceso a sus estancias y observaciones autorizadas. Para una matrícula exacta, recorrido cronológico conjunto entre los talleres autorizados seleccionados. |

### 3.1. Eventos LPR

Mostrar texto original, matrícula normalizada cuando sea válida, validez,
instantes de captura y recepción, taller, dispositivo, ubicación en el momento
de captura, dirección, confianza y referencia a la estancia asociada si existe.
Mantener diferenciadas captura y recepción y no sustituir el texto original.
Una confianza ausente se presenta como «No informada», nunca como cero.

### 3.2. Estancias y presencia

El listado muestra matrícula, taller, entrada, salida, duración, estado y tipo
de cada extremo: observado, inferido o estimado. «Observado» corresponde al
movimiento explícito de RF-004; no confundir una estimación con una lectura.
Los estados funcionales son abierta, pendiente de entrada y cerrada; el cierre
es provisional según RF-004, no una garantía de inmutabilidad.

El detalle presenta el resultado actual y una cronología de todas sus
observaciones, con captura, recepción, dispositivo, ubicación, dirección y
confianza, además de los datos originales pertinentes del evento. Incluye las
lecturas asociadas que no determinan extremos. No se exige una pantalla de
historial de revisiones, fusiones o divisiones.

Para abiertas, calcular la duración hasta el momento de consulta; para cerradas,
entre sus extremos, indicando si depende de estimaciones. Sin entrada, mostrar
«No disponible». Calcular duraciones entre instantes, respetando los cambios
de horario sin alterar el tiempo transcurrido.

El recorrido de matrícula exacta ordena estancias y observaciones por captura,
identifica cada taller y distingue ambos tipos de elemento. No infiere viajes,
trayectos ni tiempos de desplazamiento a partir de la ausencia de lecturas.

## 4. Reglas de búsqueda, filtros y tiempo

| ID | Regla |
| --- | --- |
| RN-001 | Ofrecer las cuatro consultas de la sección 3 y priorizar operativamente los eventos LPR. No omitir observaciones por no contribuir al cálculo de estancias. |
| RN-002 | Autorizar todas las lecturas y exportaciones según la sección 2, sin revelar datos ni totales de talleres ajenos al ámbito del usuario. |
| RN-003 | Permitir búsqueda exacta y parcial por matrícula; la parcial encuentra el texto en cualquier posición: `234` coincide con `1234BCD`. No corregir automáticamente caracteres ni usar coincidencia aproximada. |
| RN-004 | Para matrículas válidas, ignorar mayúsculas y separadores siguiendo la normalización de RF-003, sin perder ceros. Ofrecer búsqueda por texto original para las lecturas inválidas, conservando su contenido y sin convertirlas en matrículas válidas. El detalle técnico de comparación de texto original se fijará en el diseño. |
| RN-005 | Filtrar eventos por matrícula/texto original, intervalo, talleres, dispositivos, ubicaciones, dirección, validez, confianza mínima/máxima y asociación a estancia (todas, asociadas o sin estancia). |
| RN-006 | Permitir varios dispositivos y varias ubicaciones simultáneamente. La ubicación corresponde al momento de captura, no a la configuración actual. Combinar selecciones del mismo filtro como alternativas y filtros diferentes de forma conjunta. |
| RN-007 | Usar captura como fecha predeterminada del filtro de eventos y permitir seleccionar recepción para investigar retrasos. El recorrido sigue ordenándose por captura. |
| RN-008 | Mostrar «No informada» para confianza ausente, excluirla al aplicar un rango numérico y ofrecer selección específica de lecturas sin confianza informada. Validar los límites conforme al contrato de RF-003. |
| RN-009 | Filtrar estancias por matrícula, talleres, intervalo, estado (abierta, pendiente de entrada o cerrada) y tipo de entrada/salida (observada, inferida o estimada). Los filtros de eventos no obligan a ocultar el resto de observaciones al abrir el detalle completo de una estancia autorizada. |
| RN-010 | El período inicial de eventos es el día actual. Usar `Atlantic/Canary` para introducir y mostrar fechas y respetar sus cambios de horario; los instantes se conservan y transportan en UTC conforme a los contratos del proyecto. |
| RN-011 | Una selección de fechas sin horas incluye todos los días elegidos: desde el inicio local del primero hasta el inicio local del día siguiente al último, con límite superior exclusivo. Los intervalos con hora también usan inicio incluido y fin excluido. Validar orden y límites. |
| RN-012 | Limitar cada búsqueda temporal de eventos o historial de estancias a un año natural de intervalo como máximo, no a un número fijo de 365 días. El diseño concretará la validación de fronteras de calendario, incluidos años bisiestos, sin ampliar el máximo funcional. |
| RN-013 | El historial incluye las estancias que se solapen con el período solicitado, no solo las que empiecen dentro. Para abiertas, considerar hasta el instante de consulta. Para pendientes sin entrada, su salida conocida permite localizarlas en el intervalo sin inventar una entrada. |
| RN-014 | Considerar presente únicamente un vehículo con estancia abierta vigente según RF-004. Resolver el cierre lógico vencido antes de presentar un estado como actual: justo después de medianoche no mostrar como presente una estancia abierta de ayer por retraso del worker. No presentar resultados pendientes como consolidados. |

## 5. Navegación y vigencia

| ID | Regla |
| --- | --- |
| RN-015 | Paginar los listados con 50 resultados predeterminados y máximo de 200 por página. Devolver total exacto de coincidencias autorizadas y permitir saltar a una página concreta. Aplicar límites y orden determinista, incluso con capturas simultáneas. |
| RN-016 | Ordenar eventos por captura descendente por defecto y permitir invertir el orden. El diseño fijará desempates estables y los órdenes de los demás listados; el recorrido es cronológico por captura. |
| RN-017 | Actualizar únicamente a petición del usuario; no incorporar refresco automático. Cada actualización devuelve el resultado vigente y su fecha de actualización. Los recálculos pueden cambiar asociaciones, extremos y totales entre consultas. |
| RN-018 | Una consulta a una identidad de estancia absorbida por fusión debe informar de su sustitución y permitir acceder a la vigente conforme al linaje de RF-004. Revalidar la autorización; no devolver un identificador antiguo como si siguiera siendo la estancia actual. |
| RN-019 | El Administrador puede incluir historia conservada de talleres y dispositivos inactivos. La inactividad no borra observaciones, ni concede acceso al Asesor a otro taller o permite eludir las condiciones de sesión de RF-002. |

Cada respuesta debe mantener coherencia entre sus filas y su total; no se promete
una fotografía inmutable entre peticiones de páginas diferentes. Los mecanismos
de consistencia y actualización se concretarán en el diseño técnico.

## 6. Exportación Excel y auditoría

| ID | Regla |
| --- | --- |
| RN-020 | Exportar a Excel (`.xlsx`) las cuatro consultas para ambos roles dentro de sus permisos. Aplicar los filtros y el orden de la consulta a todos sus resultados, no solo a la página visible. |
| RN-021 | Admitir como máximo 10.000 filas de datos por archivo, sin contar cabeceras. Exactamente 10.000 es válido. Si se supera, pedir reducir la búsqueda; no generar un archivo parcial ni truncar silenciosamente. |
| RN-022 | Exportar los campos funcionales del listado correspondiente, manteniendo matrículas y textos originales como texto, incluidos ceros iniciales. Los textos recibidos se escriben como datos, nunca como fórmulas ejecutables. Identificar la zona horaria de las fechas y preservar las distinciones entre ausencia, observación, inferencia y estimación. |
| RN-023 | Registrar de forma persistente quién consulta o exporta, cuándo, qué operación realiza y qué talleres abarca. No guardar matrículas, textos buscados, payloads completos, credenciales ni tokens en esa auditoría ni en logs. Aplicar permisos también a la generación y entrega del archivo. |

La auditoría de acceso es distinta de la auditoría de correcciones de RF-006.
Se conserva sin borrado automático por ahora, según decisión del usuario del
19 de noviembre de 2026. Una futura eliminación requiere definir su política;
no se hereda automáticamente la retención de sesiones. Si no se puede confirmar
el registro de auditoría, bloquear la consulta o exportación y devolver 503 sin
entregar datos.
La exportación no permite acceder a imágenes ni añade reglas de retención de
eventos. Se consulta exclusivamente el historial conservado disponible.

## 7. Criterios de aceptación y casos de prueba previstos

Los casos siguientes son requisitos de verificación, no pruebas ejecutadas.

| ID | Criterio de aceptación | Caso previsto |
| --- | --- | --- |
| CA-001 | Las cuatro consultas y sus exportaciones respetan el ámbito de cada rol, incluidos detalle, filtros y totales. | CP-001: Asesor con taller propio/ajeno; Administrador con uno, varios y todos; credencial de dispositivo rechazada. |
| CA-002 | Eventos conserva visibles inválidas, repeticiones, intermedias y lecturas sin estancia. | CP-002: consultar cada categoría y filtrar asociadas/sin estancia sin perder datos originales. |
| CA-003 | Búsqueda exacta y parcial respeta normalización sin corregir caracteres y permite buscar inválidas por texto original. | CP-003: `234` dentro de `1234BCD`, mayúsculas, separadores, ceros iniciales y caracteres confundibles que no se sustituyen. |
| CA-004 | Todos los filtros de eventos y la selección múltiple se aplican conjuntamente, usando ubicación histórica. | CP-004: combinar talleres, dispositivos, ubicaciones, dirección y validez; cambiar la ubicación actual y comprobar capturas anteriores. |
| CA-005 | Captura es la fecha predeterminada y recepción es una alternativa explícita. | CP-005: evento recibido con retraso que entra en períodos distintos según el eje seleccionado. |
| CA-006 | Confianza ausente se distingue de cero y de un valor dentro del rango. | CP-006: consulta sin rango, con mínimo/máximo y selección de «No informada». |
| CA-007 | El día actual y los días completos se interpretan en Canarias, con máximo de un año. | CP-007: límites incluidos/excluidos, cambios de horario, año bisiesto, intervalo invertido y exceso del máximo. |
| CA-008 | Historial selecciona por solapamiento y aplica estados y tipos de extremos. | CP-008: estancia iniciada antes del período, que lo abarca entero, abierta, pendiente y extremos observados/inferidos/estimados; fronteras sin solapamiento. |
| CA-009 | Presencia incluye únicamente abiertas vigentes y no conserva falsos abiertos por retraso del cierre. | CP-009: abierta, salida pendiente, cerrada estimada y consulta tras medianoche antes del worker. |
| CA-010 | Listados y detalles muestran los datos acordados y duraciones correctas. | CP-010: abierta hasta momento de consulta, cerrada estimada, sin entrada, cambio horario y observaciones asociadas sin efecto en extremos. |
| CA-011 | El recorrido exacto integra talleres autorizados sin inferir desplazamientos. | CP-011: misma matrícula en varios talleres, cronología por captura y restricción del Asesor. |
| CA-012 | Paginación permite total exacto y salto, con 50 por defecto y máximo 200. | CP-012: primeras/últimas páginas, selección directa, capturas simultáneas, límites y total sin datos ajenos. |
| CA-013 | Solo la actualización manual obtiene cambios y muestra fecha de actualización; las identidades absorbidas remiten a las vigentes. | CP-013: lectura tardía, cambio de asociación y fusión con consulta por ID anterior, revalidando permisos. |
| CA-014 | El Administrador consulta el historial conservado de talleres y dispositivos inactivos. | CP-014: desactivación sin pérdida de resultados y mantenimiento de las restricciones de sesión del Asesor. |
| CA-015 | Excel contiene todas las coincidencias con filtros y orden aplicados hasta 10.000 filas. | CP-015: exportar las cuatro consultas con más de una página; 10.000 admitidas y 10.001 rechazadas sin archivo truncado. |
| CA-016 | Excel preserva textos, ceros, fechas y tipos de extremos, sin ejecutar fórmulas de entrada. | CP-016: abrir archivo generado con textos sintéticos que comienzan por signos de fórmula, confianza ausente y extremos estimados. |
| CA-017 | Consultas y exportaciones generan auditoría persistente sin datos sensibles prohibidos, sin borrado automático; si no se confirma su registro, devolver 503 sin datos. | CP-017: verificar autor, instante, operación y talleres; ausencia de matrículas, texto buscado, payloads y tokens en auditoría/logs; fallo de auditoría sin entrega de filas/Excel y ausencia de limpieza automática. |

## 8. Diseño y dependencias pendientes

La definición funcional queda recogida en este documento.
[DESIGN-005](../design/DESIGN-005-vehicle-history-and-search.md) concreta rutas
y DTOs HTTP, errores ProblemDetails, validación de filtros, comparación del
texto original, índices y consultas, consistencia de filas/totales, desempates,
cierre lógico al leer, resolución de linaje, generación y entrega de Excel,
horas con offset y fronteras del año natural. Su sección 13 registra las
decisiones de auditoría: sin borrado automático y fallo cerrado con 503.

El plan de implementación y verificación se recoge en
[TASKS-005](../tasks/TASKS-005-vehicle-history-and-search.md); sus tareas están
registradas con su evidencia. El OpenAPI ejecutable describe los contratos
implementados; consultar el plan para resultados reales y límites operativos.
No se cambia el cálculo de RF-004 ni se implementan correcciones, imágenes,
eliminación de datos o soporte de otros motores por definir estas consultas.
