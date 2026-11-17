# GarajeMetría — Operación de entradas, salidas y estancias

Backend RF-004. Este procedimiento no autoriza despliegues ni ejecución sobre
producción. Usar configuración externa, credenciales operativas del entorno y
copias/recuperación revisadas. Las pruebas del repositorio usan exclusivamente
bases SQL Server sintéticas con nombres `GarajeMetria_AuthTests_<UUID>`.

## Preparación y activación

1. Detener o drenar la ingesta antes de una primera proyección. Comprobar el
   destino desde el procedimiento privado del entorno, sin imprimir conexiones.
2. Revisar/aplicar la cadena de migraciones hasta
   `20261117125011_StayPersistence`. La aplicación no migra al arrancar. La
   columna UUID persistida cubre lecturas existentes sin cambiar sus campos.
3. Preparar X/R/M y los valores operativos comunes al despliegue; ver
   [configuración de estancias](../configuration/stays-configuration.md).
   La zona es `Atlantic/Canary` y la versión de algoritmo actual es 1.
4. Activar explícitamente la política con expectativa de versión. Primera
   activación: GUID vacío; cambios: GUID vigente leído por el operador. Se puede
   usar el script revisable de configuración o la herramienta siguiente.
5. Proyectar el historial con ingesta detenida, revisar diagnóstico y ejecutar
   cierres pendientes. Reiniciar los nodos con valores coincidentes y habilitar
   conjuntamente el nuevo camino de ingesta y el worker. Ningún nodo debe volver
   a usar un handler anterior que no guarde estancias dentro del mismo commit.

No se rellenan ubicaciones históricas con la descripción actual. Las políticas
y versiones de ubicación son inmutables; sus cambios no reescriben historia.

## Herramienta de mantenimiento

Compilar la solución, que incluye `tools/GarajeMetria.StayMaintenance`.
La herramienta no aloja endpoints, no inicia el worker de cierre ni necesita
inventar una identidad administrativa o de dispositivo. Lee el archivo privado
absoluto indicado mediante `GARAJEMETRIA_PRIVATE_CONFIG` o `--config`. El archivo
debe contener `ConnectionStrings:Authentication` y, si se sobrescriben valores,
`Stays` y los timeouts de persistencia en `Lpr`.

Ejemplos de operación, para un entorno previamente autorizado:

```powershell
# Solo diagnóstico; no muestra SQL, servidor, matrículas o descripciones.
dotnet run --project tools/GarajeMetria.StayMaintenance --configuration Release -- diagnose

# Primera activación explícita. Para un cambio usar la versión vigente real.
dotnet run --project tools/GarajeMetria.StayMaintenance --configuration Release -- activate `
    --expected-version 00000000-0000-0000-0000-000000000000

# Primera tanda acotada; la confirmación expresa que la ingesta ya está detenida.
dotnet run --project tools/GarajeMetria.StayMaintenance --configuration Release -- project `
    --confirm-ingestion-stopped --max-units 100

# Reanudar: conserva identidades, revisiones y asociaciones ya confirmadas.
dotnet run --project tools/GarajeMetria.StayMaintenance --configuration Release -- project `
    --confirm-ingestion-stopped

dotnet run --project tools/GarajeMetria.StayMaintenance --configuration Release -- close-due
```

Cada unidad tiene su propia transacción. El progreso de proyección inicial se
conserva por partición mediante revisiones y asignaciones por `EventId`, y por
observación mediante el vínculo de ubicación. El descubrimiento selecciona
solo lecturas a las que falta alguno de esos efectos; no usa offsets volátiles.
Una interrupción antes de commit revierte esa unidad; la reanudación relee
progreso. Si ya se proyectó una lectura pero falta su vínculo histórico, solo
se completa ese vínculo, sin recrear estancias. Una nueva ejecución terminada
sin trabajo no asigna IDs ni genera revisiones.

Salidas: 0 operación confirmada; 1 argumentos/configuración/error no esperado;
2 conflicto/indisponibilidad/resultado incierto; 3 cancelación. Ante resultado
incierto, comprobar estado antes de repetir. La herramienta deja el progreso
confirmado intacto y no hace replay ciego de escrituras. Ctrl+C solicita
cancelación y rollback de la unidad que aún no se haya confirmado.

## Ingesta y cierres

Una recepción nueva autorizada guarda evento, ubicación, cambios de estancias,
asociaciones, revisión, linaje y siguiente cierre antes de responder 201.
Una lectura inválida conserva evento y ubicación y no crea una partición de
estancia. Un reintento idéntico devuelve 200 y su recibo original sin recalcular,
activar políticas o volver a resolver ubicación; un conflicto conserva 409.
La admisión temporal sigue siendo la de RF-003, aunque M sea mayor.

El worker consulta lotes al arrancar y después de cada espera, crea un scope
por unidad y coordina catálogo/partición con la ingesta. Varias instancias
pueden descubrir el mismo trabajo. La revisión de entrada y la clave
partición/fecha hacen el cierre repetido no-op; una lectura tardía permite
reabrir la evaluación de esa fecha. Talleres y dispositivos ahora inactivos
no impiden cerrar historia. No hay un endpoint público de cierre.

El procesamiento de una ingesta también materializa los cierres vencidos del
contexto que está recalculando. No depende de que el worker haya ejecutado
exactamente en medianoche. Las esperas no retienen transacciones o locks;
los errores de una unidad no detienen las restantes del lote. El cursor de
descubrimiento continúa entre lotes para no bloquear otras particiones tras
una unidad persistentemente fallida.

## Presupuestos, diagnóstico y fallos

La lectura usa keyset por captura/UUID de red, conserva el grupo y el contexto
entre páginas y expande la región por observaciones/visitas hasta bordes
conservadores estables. Las políticas de la partición determinan el máximo de
descubrimiento; la interpretación usa el snapshot completo de cada estancia.
Fusión: ID/ancla/política de la primera creada, con orden de creación persistido.
División: mismo snapshot en todas las partes y ID original donde quedó el ancla.

`ContextObservationLimit` (inicial 20000) y `ContextStayLimit` (inicial 2000)
son límites operativos de memoria, separados de X/R/M. El contexto puede ser
amplio si hay una cadena densa de observaciones; los límites y el timeout
rechazan el conjunto completo con 503 en vez de truncar historia o devolver
éxito diferido. Para mantenimiento, revisar densidad y ajustar presupuestos
operativos junto a sus recursos, sin alterar una política histórica para
sortear un límite. No se promete capacidad productiva a partir del ensayo local.

El diagnóstico interno informa esquema/política, número de particiones vencidas
y edad del trabajo más antiguo. La indisponibilidad de política se comprueba
también dentro de la transacción de un evento nuevo; un nodo discrepante
rechaza nuevas escrituras. El contrato público de health no se amplía con
datos de talleres, matrículas o configuración.

El meter `GarajeMetria.Stays` registra duración de cálculo, tamaño de contexto,
cambios/estimaciones preparados, cierres, edad pendiente, espera/retención del
lock de catálogo y rollback. Etiquetas solo de causa, cambio y resultado con
valores acotados; no IDs ni matrículas. Los contadores de proyección/estimación
son intentos preparados dentro de una transacción, no una prueba de commit.
Correlacionar con el resultado del caso de uso y los errores seguros existentes.

Ante 503 revisar política activa/nodos, esquema, backlog y presupuesto. Ante
fallo de invariantes investigar la revisión y sus diferencias mediante acceso
operativo autorizado, sin copiar cargas a logs. Un fallo conocido con rollback
confirmado conserva la posibilidad de reconciliar; un commit incierto requiere
comprobar por UUID/progreso, sin repetir automáticamente escrituras no acreditadas.

## Recuperación y límites

`Down` se detiene si hay datos RF-004. No eliminar políticas, ubicaciones,
linaje o revisiones para desbloquearlo. Mantener esquema y datos y preparar
una recuperación revisada. Volver al binario/handler anterior después de la
activación pierde la garantía de efectos de estancia síncronos: drenar admisión
y resolver una migración/recuperación de aplicación, no presentar ese rollback
como equivalente al funcionamiento actual.

Los ensayos SQL, rollback transaccional y reanudación no acreditan el
backup/restore físico corporativo pendiente de RF-003. Tampoco prueban cámaras
reales, IIS corporativo o AD aislado. El flujo HTTP sintético está en
[RF-004-stays.http](../examples/RF-004-stays.http); sus comprobaciones de
persistencia son las pruebas de integración, sin inventar rutas de historial.

RF-005 incorporará la consulta HTTP completa y su autorización de rastro;
existen puertos internos materializados para estancias y para observaciones
por dispositivo/texto original, incluidas inválidas. RF-006 añadirá correcciones
administrativas. No interpretar estimaciones como presencia física continua.
Los resultados efectivamente ejecutados y sus artefactos se registran en
[TASKS-004](../tasks/TASKS-004-arrivals-departures-and-stays.md).
