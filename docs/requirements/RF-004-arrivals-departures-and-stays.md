# RF-004 — Entradas, salidas y estancias

| ID | Requerimiento | Prioridad | Estado |
| --- | --- | --- | --- |
| RF-004 | Entradas, salidas y estancias | Alta | Backend de cálculo implementado y verificado; consulta HTTP del rastro en RF-005; evidencias en TASKS-004 |

## 1. Objetivo y alcance

Interpretar las observaciones conservadas por [RF-003](RF-003-lpr-event-ingestion.md)
para obtener entradas, salidas y estancias por vehículo y taller, sin perder el
rastro de las lecturas originales. Las decisiones se acordaron el 16 de noviembre
de 2026 y se completaron el 17 de noviembre de 2026 con las decisiones de
DESIGN-004 / 14. Este documento sustituye la regla inicial de una única estancia diaria
cuando existen direcciones que permiten distinguir varias visitas.

Un vehículo puede tener varias estancias en el mismo día y una estancia puede
atravesar medianoche. Se utilizan las direcciones explícitas y las lecturas sin
dirección conforme a las reglas siguientes. Una estancia estimada no acredita
la presencia física del vehículo durante todo su intervalo.

Incluye cálculo, estimaciones, cierre diario, recálculo automático, trazabilidad
de lecturas y ubicación descriptiva del dispositivo. Las consultas de vehículos
e historial corresponden a RF-005; las operaciones de corrección manual y su
auditoría, a RF-006; las imágenes, a RF-007; el panel, a RF-008.
No se implementan esas funcionalidades por el mero cierre de esta especificación.

## 2. Conceptos y datos funcionales

| Concepto | Significado |
| --- | --- |
| Lectura u observación | Evento original aceptado por RF-003, con identidad propia, matrícula original y normalizada, validez, confianza, dirección opcional, dispositivo, taller, captura y recepción. |
| Movimiento explícito | Entrada o salida indicada por el dispositivo. Tiene prioridad interpretativa sobre una lectura sin dirección, sin impedir la ampliación de extremos acordada. |
| Extremo inferido | Entrada o salida obtenida de una lectura que no aporta ese movimiento explícito; por ejemplo, utilizar la segunda entrada como salida. |
| Extremo estimado | Instante calculado sin lectura que lo observe: entrada igual a salida menos X, o salida virtual igual a entrada más X. |
| Estancia abierta | Entrada disponible sin salida durante el día en curso; su duración transcurrida se calcula hasta el momento de consulta. |
| Estancia cerrada provisional | Intervalo con extremos disponibles que puede ampliarse o recalcularse al recibir nuevas lecturas. El cierre no implica inmutabilidad. |
| Salida pendiente | Salida sin entrada asociada durante el día en curso, a la espera de otras observaciones antes de estimar la entrada al cierre diario. |
| Ubicación del dispositivo | Descripción del punto de observación, como «acceso principal» o «zona de entrega», asociada al dispositivo. |

El resultado debe permitir distinguir extremos explícitos, inferidos y estimados,
identificar las lecturas utilizadas y conservar la configuración aplicada.
Una lectura conservada puede estar asociada a una estancia sin determinar sus
extremos, o quedar sin asociación. No fabricar un evento LPR para representar
una estimación.

## 3. Identidad, tiempo y configuración

| ID | Regla |
| --- | --- |
| RN-001 | Asociar las lecturas por matrícula normalizada y taller, independientemente del dispositivo que las capture. La misma matrícula en talleres distintos se calcula de forma independiente. |
| RN-002 | Ordenar e interpretar por instante de captura, no por recepción. Conservar los instantes en UTC y utilizar `Atlantic/Canary` para el día local y su cierre, respetando sus cambios de horario. |
| RN-003 | Una estancia puede atravesar medianoche; no dividirla por cambio de fecha. La duración se obtiene entre instantes, sin sumar o restar artificialmente cambios de horario local. |
| RN-004 | La configuración es común para todos los talleres: X comienza en 30 minutos; el intervalo de repetición, en 1 minuto; el máximo para enlazar una salida real posterior, en 48 horas desde la entrada. Los tres valores son configurables. |
| RN-005 | Fijar la configuración al crear la estancia y conservarla en ampliaciones y recálculos automáticos. Cambiar la configuración no recalcula el historial ni modifica la configuración de estancias existentes. |

Los valores se suministran mediante configuración, no constantes de negocio
inmodificables en el código. El diseño concretará sus nombres, validación y
representación persistida, así como la asignación estable de configuración cuando
un recálculo cambie la asociación de lecturas entre estancias.

## 4. Conservación y selección de observaciones

| ID | Regla |
| --- | --- |
| RN-006 | Conservar todas las observaciones aceptadas por RF-003, aunque no participen en el cálculo final: intermedias, repetidas, inválidas o sin asociación. El cálculo no elimina ni sobrescribe los eventos originales. |
| RN-007 | Excluir del cálculo las matrículas de formato inválido. Utilizar todas las de formato válido sin umbral de confianza. No corregir automáticamente caracteres de matrícula. |
| RN-008 | Las lecturas equivalentes dentro del intervalo de repetición no deben producir movimientos adicionales. Medir el intervalo desde la primera captura del grupo, incluyendo exactamente el límite. La lectura posterior al límite inicia otro grupo. |
| RN-009 | En un grupo repetido, utilizar la primera lectura para representar una entrada y la última para representar una salida. Conservar todas las integrantes y su trazabilidad. |
| RN-010 | No suprimir una entrada y una salida explícitas opuestas por estar dentro del intervalo de repetición: pueden representar una visita corta real. |

La repetición es una regla de interpretación de RF-004, distinta de la
idempotencia de RF-003. Dos UUID diferentes siguen siendo dos observaciones.
El diseño deberá precisar la equivalencia al agrupar lecturas de varios
dispositivos o con dirección mixta, respetando RN-001 y RN-008 a RN-010.

## 5. Asociación y cálculo de estancias

| ID | Regla |
| --- | --- |
| RN-011 | Emparejar entradas y salidas explícitas para identificar visitas distintas, permitiendo varias estancias diarias. Aplicar primero la interpretación de repeticiones. |
| RN-012 | Utilizar todas las lecturas elegibles, con prioridad a las direcciones explícitas. Las lecturas sin dirección pueden ampliar la entrada o salida de una estancia; la prioridad no obliga a ignorarlas. |
| RN-013 | Asociar una lectura sin dirección al movimiento explícito elegible más cercano en tiempo, incluso en días distintos, dentro del horizonte de RN-035; ante igual distancia, al anterior. Su asociación puede revisarse al recibir nuevas observaciones. |
| RN-014 | Para las lecturas sin dirección que no tengan un movimiento explícito elegible según RN-013/RN-035, utilizar la primera como entrada y la última como salida dentro de cada día, sin deducir visitas intermedias. Una sola lectura se trata como entrada provisional. |
| RN-015 | Con dos o más lecturas sin dirección ya diferenciadas por la regla de repetición, mostrar una estancia cerrada provisional de primera a última, ampliable con nuevas lecturas. |
| RN-016 | Ante entradas consecutivas del mismo día sin salida intermedia, conservar la más antigua como entrada. Si al cierre diario no hay salida, utilizar la entrada más reciente como salida inferida cuando esté fuera del intervalo de repetición. Para días distintos aplicar RN-033/RN-034. |
| RN-017 | Ante salidas consecutivas sin entrada intermedia, utilizar la más reciente como salida, ampliando una estancia previamente cerrada si procede. Si no hay entrada anterior asociable, utilizar la salida más antigua como entrada inferida y consolidar al cierre diario. |

Una lectura intermedia no genera por sí sola otra estancia. Ejemplo: entrada
08:00, entrada 08:30 y salida 09:30 producen una estancia 08:00–09:30; la entrada
08:30 permanece registrada y disponible como evidencia de observación.

## 6. Estimaciones, medianoche y movimientos incompletos

| ID | Regla |
| --- | --- |
| RN-018 | Durante el día en curso, una entrada sin salida permanece abierta con duración hasta el momento actual. Una lectura única sin dirección recibe ese mismo tratamiento provisional. |
| RN-019 | Al finalizar el día local, si falta salida y no puede inferirse con las lecturas disponibles, generar una salida virtual en entrada más X. Se genera al cierre diario, aunque el instante estimado sea anterior al cierre. |
| RN-020 | Ante una salida sin entrada, buscar una entrada anterior asociable, también en días anteriores. Si no existe, mantenerla pendiente durante el día y estimar al cierre una entrada en salida menos X, salvo que otras lecturas permitan inferirla. |
| RN-021 | Una salida real puede sustituir una salida virtual o una salida inferida a partir de entradas consecutivas si está dentro del máximo de 48 horas desde la entrada, o del valor configurado aplicable. El máximo se mide como tiempo transcurrido, no como días naturales; el límite exacto está incluido. |
| RN-022 | Una nueva entrada posterior inicia otra estancia y la anterior conserva su salida estimada; no utilizar la salida de la nueva visita para prolongar automáticamente la anterior. Distinguir nueva entrada de repetición mediante RN-033/RN-034, sin depender de cuándo se ejecutó el cierre diario. |
| RN-023 | Una salida fuera del máximo no reemplaza la salida virtual antigua. Si no tiene otra entrada asociable, origina una estancia independiente con entrada estimada conforme a RN-020. |
| RN-024 | Permitir que una estimación atraviese medianoche: una salida aislada a las 00:10 con X de 30 minutos produce entrada estimada a las 23:40 del día anterior. No truncar estimaciones al límite diario. |

El cierre diario no impide revisiones posteriores. Una salida virtual no acredita
una salida física ni impide enlazar una salida real posterior conforme a estas
reglas. Los estados provisionales y la procedencia de los extremos deben ser
distinguibles por los consumidores, evitando presentar estimaciones como hechos.

## 7. Actualización, recálculo y fiabilidad

| ID | Regla |
| --- | --- |
| RN-025 | Al confirmar con éxito la recepción de un evento nuevo, sus efectos sobre las estancias afectadas deben estar actualizados y persistidos. No confirmar la ingesta dejando pendiente ese cálculo en una cola posterior. Una lectura excluida se conserva sin efectos sobre estancias. |
| RN-026 | Recalcular automáticamente ante lecturas tardías aceptadas por RF-003, incluso tras el cierre diario, revisando asociaciones, extremos, estimaciones y duraciones. Respetar la configuración histórica de la estancia. |
| RN-027 | Reenvíos idénticos y entregas concurrentes no duplican lecturas, movimientos ni estancias. Un fallo no permite confirmar un estado parcial entre el evento nuevo y los efectos de cálculo exigidos por RN-025. |
| RN-028 | El cierre diario y la ingesta concurrente deben producir resultados coherentes, sin duplicar estimaciones ni perder una salida real. La recuperación tras interrupciones debe completar los cierres pendientes sin duplicar efectos. |

RF-004 no amplía la ventana de aceptación de RF-003: inicialmente 30 minutos
de antigüedad y 30 de adelanto, configurables por separado. El límite de 48 horas
para asociar una estancia no permite enviar cualquier evento con 48 horas de
retraso. Un reintento idéntico sigue sujeto a las reglas de RF-003 y no es un
evento nuevo.

Las correcciones manuales pertenecen a RF-006. Deberán recalcular las estancias
afectadas, conservar los eventos originales y mantener quién cambió qué y cuándo.
RF-004 no concede permisos ni define operaciones de corrección manual.

## 8. Ubicación y rastro de observaciones

Cada dispositivo tendrá una ubicación descriptiva. Su incorporación requiere
ampliar el mantenimiento de dispositivos de RF-003; esta capacidad se entrega en RF-004, sin suponer implementada
en sus contratos actuales. El diseño concretará el campo, sus límites y la
compatibilidad con los dispositivos ya registrados. Se mantienen los permisos
administrativos y el aislamiento por taller de RF-002/RF-003.

| ID | Regla |
| --- | --- |
| RN-029 | Identificar el dispositivo y su ubicación descriptiva para reconstruir el punto en que se observó el vehículo. Preservar el significado histórico de la ubicación cuando cambie su descripción; el diseño concretará el mecanismo. |
| RN-030 | RF-005 deberá permitir consultar todas las observaciones conservadas, incluidas las que no determinan extremos o no pertenecen a ninguna estancia. Incluir captura, recepción, taller, dispositivo, ubicación y datos originales pertinentes, sujetos a autorización. |
| RN-031 | Las lecturas inválidas deben poder localizarse por texto de matrícula y dispositivo para investigar errores de reconocimiento, aunque estén excluidas del cálculo. |
| RN-032 | El rastro representa observaciones en puntos e instantes concretos; no acredita una ubicación continua entre lecturas. Las estimaciones no deben aparecer como observaciones físicas. |

Se mantiene la conservación indefinida acordada en la visión y el mantenimiento
externo coherente allí previsto. No se incorporan borrados automáticos. Las
matrículas y el historial son sensibles: aplicar los permisos de RF-002 a toda
consulta y escritura, sin exponer matrículas completas ni cargas en los logs.
Las restricciones de imágenes de RF-007 permanecen vigentes.

### 8.1. Decisiones completadas el 17 de noviembre de 2026

Las reglas siguientes cierran P004-01, P004-02 y P004-03 de DESIGN-004.
Complementan las reglas anteriores sin cambiar sus identificadores.

| ID | Regla |
| --- | --- |
| RN-033 | Dos entradas consecutivas en días locales distintos inician estancias distintas y la anterior conserva su salida estimada, salvo que pertenezcan al mismo grupo de repetición de RN-034. Resolver según las capturas, no según la ejecución efectiva del cierre diario. |
| RN-034 | La repetición tiene prioridad sobre el cambio de día. Entradas equivalentes dentro de R desde la primera captura, incluido el límite, representan una sola entrada aunque crucen medianoche. Conservar ambas lecturas y la primera como representante. |
| RN-035 | Para asociar una lectura sin dirección, buscar movimientos explícitos hasta una distancia temporal absoluta M, inicialmente 48 horas, con el límite configurable e inclusivo. Aplicar también el límite de asociación desde la entrada correspondiente. Tiene prioridad esta asociación entre días sobre el agrupamiento diario; solo las lecturas sin movimiento elegible aplican RN-014. Respetar la política histórica de la estancia candidata. |
| RN-036 | Si un recálculo fusiona estancias, la resultante conserva el identificador y toda la configuración de la estancia que se creó primero, no necesariamente la de entrada más antigua. Registrar la relación con las restantes sin perder sus observaciones ni su historia. |
| RN-037 | Si un recálculo divide una estancia, todas las partes conservan toda la configuración original. La parte que contiene la lectura de origen conserva el identificador; las demás reciben identificadores nuevos y mantienen su relación de procedencia con la original. No aplicarles la configuración vigente por ser nuevas filas. |

## 9. Criterios de aceptación

Los ejemplos utilizan matrículas sintéticas y, salvo indicación contraria,
horas locales del mismo día, X de 30 minutos y repetición de 1 minuto.

| ID | Resultado esperado |
| --- | --- |
| CA-001 | Entrada 08:00, salida 09:00, entrada 16:00 y salida 17:00 producen dos estancias, conservando las cuatro lecturas. |
| CA-002 | Entrada lunes 23:50 y salida martes 00:20 producen una estancia de 30 minutos, sin división diaria. Validar también la duración por instantes durante cambios de horario. |
| CA-003 | Lecturas de la misma matrícula normalizada desde varias cámaras del taller se interpretan conjuntamente; la misma matrícula en otro taller no modifica esas estancias. |
| CA-004 | Sin dirección: 08:00, 09:00, 16:00 y 17:00 producen una estancia 08:00–17:00 y cuatro observaciones conservadas. Tras la segunda lectura ya existe cierre provisional ampliable. |
| CA-005 | Una entrada o lectura única sin dirección permanece abierta durante el día. Al cierre obtiene salida virtual en captura más 30 minutos. |
| CA-006 | Una salida aislada permanece pendiente durante el día. Sin entrada anterior asociable, al cierre obtiene entrada estimada 30 minutos antes, aunque cruce medianoche. |
| CA-007 | Una salida real posterior reemplaza una salida virtual dentro de las 48 horas desde la entrada, incluyendo el límite exacto; fuera del límite no la modifica y, sin otra entrada, produce otra estancia estimada. |
| CA-008 | Entrada lunes 08:00 con salida virtual y nueva entrada martes 09:00 con salida 10:00 producen estancias independientes; la salida del martes no prolonga la del lunes. |
| CA-009 | Entradas 08:00 y 16:00 sin salida producen al cierre estancia 08:00–16:00. Una salida real posterior dentro del máximo puede sustituir ese extremo inferido. |
| CA-010 | Entrada 08:00, salida 09:00 y salida 17:00 producen estancia 08:00–17:00. Sin entrada anterior, las salidas 09:00 y 17:00 producen al cierre estancia 09:00–17:00. |
| CA-011 | Lectura sin dirección 07:55, entrada 08:00, salida 09:00 y lectura sin dirección 09:05 producen estancia 07:55–09:05. |
| CA-012 | Una lectura sin dirección entre visitas se asocia al movimiento temporalmente más cercano; ante empate se asocia al anterior. Nuevas lecturas pueden revisar esa asociación. |
| CA-013 | Lecturas equivalentes a 08:00:00 y 08:01:00 pertenecen al mismo grupo; 08:01:01 inicia otro. Para 08:00:00, 08:00:50 y 08:01:40, la tercera no se agrupa por encadenamiento con la segunda. |
| CA-014 | En repeticiones se representa una entrada con la primera lectura y una salida con la última; todas se conservan. Entrada explícita y salida explícita dentro de 1 minuto mantienen ambos movimientos. |
| CA-015 | Lecturas inválidas no generan ni amplían estancias; lecturas válidas con confianza baja sí participan. Ambas clases se conservan y las inválidas son localizables por texto y dispositivo. |
| CA-016 | Una lectura tardía aceptada recalcula automáticamente las estancias, también después de medianoche. Una captura rechazada por RF-003 no se admite por el límite de asociación de 48 horas. |
| CA-017 | Al recibir la confirmación de un evento nuevo ya están persistidos sus efectos sobre las estancias. Reintentos, concurrencia y fallos no duplican efectos ni confirman cambios parciales. |
| CA-018 | Cambiar X, repetición o máximo de asociación no altera el historial; una estancia existente conserva sus parámetros en posteriores ampliaciones y recálculos. |
| CA-019 | Entrada 08:00, entrada 08:30 y salida 09:30 producen estancia 08:00–09:30, manteniendo consultable la observación de las 08:30 con su dispositivo y ubicación. Lo mismo se exige para lecturas repetidas o sin asociación. |
| CA-020 | Cada observación permite identificar su punto de captura sin atribuir presencia continua; un cambio descriptivo del dispositivo no reinterpreta silenciosamente las ubicaciones históricas. |
| CA-021 | Cierre diario, ingesta simultánea y recuperación de cierres tras interrupción no pierden lecturas ni crean cierres o estimaciones duplicados. |
| CA-022 | La consulta del rastro respeta el taller autorizado y los permisos de RF-002; las estimaciones se distinguen de observaciones y no se revelan datos sensibles mediante logs. |
| CA-023 | Entrada lunes 23:50 y entrada martes 00:10 producen dos estancias, conservando la estimación de la primera. El resultado es el mismo si el cierre diario se ejecuta antes o después de recibir la segunda lectura. |
| CA-024 | Entradas equivalentes lunes 23:59:40 y martes 00:00:10 pertenecen al mismo grupo y no crean una segunda estancia. Exactamente R también se agrupa; más de R aplica la separación por día. Conservar todas las observaciones. |
| CA-025 | Entrada lunes 23:50 y lectura sin dirección martes 00:05 se asocian a la misma estancia. La búsqueda admite M exacto y rechaza M más un tick, respeta además el límite desde la entrada y el desempate al anterior; sin movimiento elegible aplica el agrupamiento diario. |
| CA-026 | Al fusionar una estancia creada con X de 30 minutos y otra creada después con X de 45, conservar ID y toda la política de la creada primero. Mantener relación con la otra, observaciones e historia, incluso si la entrada más antigua pertenecía a la creada después. |
| CA-027 | Al dividir una estancia histórica después de cambiar la configuración, todas las partes heredan su política original; conserva ID la parte con la lectura de origen, aunque no sea la primera cronológicamente. Las demás tienen ID nuevo y trazabilidad de procedencia. Repetir el recálculo no crea partes duplicadas. |

## 10. Diseño y dependencias pendientes

La definición funcional no acredita implementación ni pruebas ejecutadas.
[DESIGN-004](../design/DESIGN-004-arrivals-departures-and-stays.md) concreta
contratos, persistencia, concurrencia, cierre diario, estados expuestos,
ubicación histórica y matriz CA/CP.
[TASKS-004](../tasks/TASKS-004-arrivals-departures-and-stays.md) planifica su
implementación y verificación, incluida la integración síncrona con RF-003;
el plan registra la integración del dominio, persistencia, ingesta síncrona,
ubicación, políticas, cierre y mantenimiento reanudable. Las verificaciones
finales, evidencias y límites de consulta RF-005 se documentan en sus secciones
18 y 19. No se ha realizado un despliegue ni una activación compartida.

El diseño establece el orden determinista para capturas con el mismo instante,
la equivalencia de repeticiones mixtas o entre dispositivos y la evolución
compatible del catálogo de dispositivos. Su sección 14 registra las tres decisiones
funcionales resueltas el 17 de noviembre de 2026: entradas alrededor de
medianoche, asociación sin dirección entre días y políticas históricas ante
fusiones/divisiones. RN-033 a RN-037 y CA-023 a CA-027 incorporan esas decisiones;
su implementación y pruebas ejecutables se detallan en TASKS-004.

RF-005 concretará búsqueda, filtros, paginación y contratos de consulta del rastro;
RF-006 concretará campos corregibles, permisos específicos y auditoría de cambios.
La prioridad de planificación de RF-004 es Alta.
