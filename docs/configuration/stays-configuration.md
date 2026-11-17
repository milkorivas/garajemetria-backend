# GarajeMetría — Configuración y activación de estancias

Configuración integrada del backend RF-004. Las verificaciones y sus resultados
se registran en [TASKS-004](../tasks/TASKS-004-arrivals-departures-and-stays.md).
La activación de un entorno compartido requiere una operación explícita.

## Configuración local

`Stays` tiene estos valores iniciales en `appsettings.json`; la configuración
privada y las variables de entorno pueden sobrescribirlos con el mecanismo
existente del host:

```json
{
  "Stays": {
    "EstimatedDurationMinutes": 30,
    "RepeatWindowSeconds": 60,
    "MaximumAssociationHours": 48,
    "ClosurePollSeconds": 30,
    "ClosureBatchSize": 100,
    "ObservationPageSize": 500,
    "ContextObservationLimit": 20000,
    "ContextStayLimit": 2000
  }
}
```

Si falta la sección se usan los mismos valores iniciales. Los tres primeros
parámetros definen una política de negocio global común a todos los talleres.
Los cinco últimos son operativos de cada instancia y no cambian la huella de
la política ni las estancias históricas. `AlgorithmVersion = 1` y
`Atlantic/Canary` están fijados en código; no se permite seleccionar otra zona
por taller ni reinterpretar historia mediante una versión configurable.

Las opciones enlazadas son inmutables. Se validan valores positivos, lotes de
1–100 y páginas de 1–1000. La conversión a ticks es entera y comprobada; rechaza
desbordamientos. El polling debe caber en el rango de temporizadores .NET
(hasta 4.294.967 segundos enteros). El arranque valida además suma/resta de
las duraciones alrededor del día local actual, con `TimeProvider`, y resuelve
la zona horaria. Un error impide arrancar con esos valores y no revela secretos.
Fechas extremas durante futuros cálculos requieren asimismo validar la
aritmética al operar; el control de arranque no prueba todo el rango histórico.

## Calendario y política histórica

`WorkshopCalendar` convierte cada medianoche local y la del día siguiente por
separado a UTC; no suma 24 horas al inicio UTC. Devuelve intervalos semiabiertos
`[Start, End)`. Esto permite representar el cambio estacional de hora. Rechaza
fechas sin siguiente medianoche representable y límites locales inválidos o
ambiguos, sin inventar un offset. Las pruebas de calendario cubren primavera
y otoño de 2026; el entorno IIS corporativo requiere su propia verificación.

`IStayPolicyRepository.FindAsync` devuelve el snapshot histórico guardado, aunque
no coincida con la configuración actual del nodo. No renormaliza lecturas,
modifica políticas antiguas ni sustituye X/R/M/versión por valores activos.
`GetActiveAsync` comprueba los datos persistidos, su huella y la coincidencia
con la configuración local. La huella SHA-256 usa UTF-8 y formato invariante:

```text
garajemetria-stays:v1|<X en ticks>|<R en ticks>|<M en ticks>|Atlantic/Canary|1
```

Cada tick son 100 ns. El GUID de política y los parámetros operativos no forman
parte de la huella; valores idénticos reutilizan la política inmutable existente.
Una futura versión de algoritmo requiere un cambio explícito y evaluadores
históricos compatibles, fuera de T004-09.

## Activación explícita de despliegue

Prerrequisito: esquema `20261117125011_StayPersistence` aplicado mediante el
[procedimiento de migraciones](migration-procedure.md), con destino, copia y
recuperación revisados. El arranque del host no crea ni activa la política.

El script revisable [activate-stay-policy.sql](sql/activate-stay-policy.sql)
prepara una activación manual. Revisar sus tres parámetros de negocio y
`@ExpectedVersion` antes de ejecutarlo mediante el procedimiento autorizado
del entorno; con `sqlcmd`, mantener `-I -b`. La versión vacía significa que se
espera una configuración todavía inexistente. Para cambiar una política, leer
la versión vigente y usar exactamente ese GUID como expectativa. No eliminar
filas para forzar una activación ni sobrescribir políticas referenciadas.

La operación se coordina mediante el mismo bloqueo `workshops:catalog` que
la ingesta, dentro de una transacción `Serializable`. Guarda una política nueva
o reutiliza la misma huella, y cambia el puntero activo con una versión GUID
nueva. Si esa política ya está activa y la expectativa coincide, es no-op:
no cambia versión ni instante. Una expectativa obsoleta no modifica nada;
releer y revisar antes de reintentar. Ante commit incierto, comprobar estado
en lugar de repetir a ciegas. La operación no modifica estancias, asociaciones
ni políticas históricas. El script usa la hora UTC del servidor.

El mismo puerto `ActivateAsync` queda disponible para una herramienta de
despliegue autorizada: contexto limpio, sin transacción previa, snapshot que
coincida con las opciones del proceso, expectativa explícita e instante UTC.
Tiene timeout, control de versión y rollback compartidos con la infraestructura.
No se publica un endpoint ni se invoca este método al arrancar.

Coordinar activación y configuración de los nodos durante el despliegue. Un nodo
con otros valores rechaza eventos nuevos con 503 hasta que vuelva a coincidir.
Se recomienda drenar admisión durante el cambio; un cambio de política no
actualiza la configuración en memoria de los nodos ya arrancados.

## Admisión y límites de esta entrega

La DI registra `PolicyCheckedLprEventRepository` para el puerto de eventos.
Tras autorización y comparación RF-003, un evento inexistente requiere una
política activa coincidente antes de añadirse. La comprobación y el alta usan
la misma transacción de ingesta que ya mantiene el bloqueo de catálogo.
La activación no puede cambiar el puntero entre esa comprobación y el commit.
Se aplica también a lecturas inválidas nuevas. Una política ausente o distinta
produce indisponibilidad, sin exponer sus valores en HTTP o logs.

Los reintentos idénticos y los conflictos conservan la comparación RF-003;
no necesitan consultar la política actual ni recalcular historia. El decorador
impide añadir un evento si no se verificó su ID dentro de esa transacción.
No acepta una comprobación de una transacción anterior como autorización.

La ingesta nueva guarda sus efectos de estancia y ubicación dentro del mismo
commit. La validación de cierre, su handler y el worker supervisado están
registrados. Los límites de contexto son presupuestos operativos positivos:
si se agotan, se revierte la unidad completa; no se trunca historia ni se
confirma el cálculo para más tarde. El cierre usa scope por unidad y conserva
su progreso para reinicios y varias instancias.

La herramienta `GarajeMetria.StayMaintenance` permite diagnóstico, activación,
proyección inicial reanudable y cierre de trabajo vencido. No se ejecuta al
arrancar la API. Ver [runbook de estancias](../runbooks/stays.md), incluidos
configuración privada, secuencia de despliegue y límites de recuperación.
