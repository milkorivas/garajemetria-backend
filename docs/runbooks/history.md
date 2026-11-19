# RF-005 — Consultas e historial

Referencias: [requerimiento](../requirements/RF-005-vehicle-history-and-search.md),
[diseño](../design/DESIGN-005-vehicle-history-and-search.md),
[plan y evidencias](../tasks/TASKS-005-vehicle-history-and-search.md),
[preparación aislada](../configuration/history-test-preparation.md).

## Acceso y uso

Usar token de acceso de usuario RF-001; una credencial de dispositivo no concede
acceso. El Asesor consulta/exporta solo su taller activo asignado. El Admin
puede seleccionar uno/varios talleres o no enviar selección para todos,
incluidos históricos inactivos. Desactivar no borra historia ni reactiva cámaras.

Las rutas y parámetros están descritos en DESIGN-005 / 3 y OpenAPI. Consultas
de eventos son la entrada operativa principal. Búsqueda de matrícula exacta o
fragmento normaliza separadores/mayúsculas sin corregir caracteres; texto
original es literal y sensible a mayúsculas y espacios. La ubicación es la
versión histórica guardada; procedencia desconocida no se presenta como certeza.

Por defecto se consulta el día actual de `Atlantic/Canary`. Para días completos
usar `fromDate`/`throughDate`; para instantes `from`/`to` con offset o `Z`.
No mezclar modos. Un año se valida por calendario local, no como 365 días fijos.
Captura es el eje por defecto de eventos; recepción permite investigar retrasos.
El orden de eventos sigue siendo por captura.

Páginas 50 por defecto, máximo 200, total exacto y salto directo. Actualizar
manualmente para obtener cambios; páginas distintas pueden reflejar estados
distintos tras una lectura tardía. Guardar/mostrar `asOfUtc` y `updatedAtUtc`
como referencia de lectura. La consulta de presencia solo contiene abiertas
vigentes, no salidas pendientes ni cierres estimados. Detalle de una identidad
absorbida informa de sustitución y permite consultar sus observaciones actuales.

## Exportación

`POST /api/history/exports` con `kind`, `filters`, `order`; sin parámetros de
paginación. Se exportan todas las coincidencias, hasta 10.000 filas, con el
mismo orden; 10.001 produce 422 para reducir la búsqueda. La cronología cuenta
eventos y estancias como filas distintas. Ambos roles conservan su ámbito.
La respuesta es `.xlsx` sin almacenamiento permanente en servidor, con texto
como texto, fechas ISO locales con offset y hoja de metadatos. No incluye imágenes.

Configuración `History`: consulta 30 s, exportación 60 s y SQL 15 s como máximos.
Límites: JSON 32 KiB, archivo 32 MiB; 60 consultas y 5 exportaciones por minuto
por usuario; una exportación concurrente por usuario, dos por instancia.
Leases compartidas expiran tras abandono; no borrar manualmente una lease viva.
No aumentar límites funcionales sin revisar RF-005 y su verificación.

## Incidencias y auditoría

| Respuesta | Actuación |
| --- | --- |
| 400 | Revisar filtros, límites, combinación temporal y parámetros desconocidos. |
| 401/403 | Comprobar sesión, rol y asignación, sin usar taller recibido como autorización. |
| 404 | Recurso inexistente o no visible; no revelar existencia en otro taller. |
| 422 | Reducir exportación, sin esperar archivo parcial. |
| 429 | Respetar Retry-After y reducir solicitudes concurrentes. |
| 503 `history_projection_pending` | Cierre no consolidado dentro del presupuesto; comprobar worker y backlog RF-004, luego actualizar manualmente. |
| 503 `history_audit_unavailable` | No se han liberado datos. Comprobar SQL/esquema/permisos de auditoría; no desactivar el gate. |
| 503 `operation_outcome_unknown` | Commit no confirmado; puede existir auditoría. No afirmar rollback ni reintentar la escritura ciegamente. |

Cada liberación autorizada de datos (también vacía y selectores) registra actor,
instante, operación, talleres y filas. Una entrada no garantiza que el cliente
completase la descarga. No se guardan filtros/matrículas/textos buscados y no
hay limpieza automática de esta auditoría. Vigilar volumen y capacidad; definir
política explícita antes de una eliminación futura.

Métricas del medidor `GarajeMetria.History` identifican operación/estado y duración
sin matrículas o usuario como etiquetas. Correlacionar mediante identificadores
seguros, sin añadir query strings ni contenido de celdas a logs de incidencia.

## Alojamiento y migraciones

Aplicar la migración RF-005 mediante el procedimiento controlado de
[migraciones](../configuration/migration-procedure.md), primero en base aislada
y tras revisar script/índices. No se aplica al arrancar la API. La reversión
rechaza eliminar auditoría existente; desactivar rutas/revertir código
conservando datos es preferible a ejecutar Down sobre historia real.

IIS/proxy/APM no deben registrar query strings de rutas RF-005, cuerpos de
exportación, headers Authorization ni URLs completas con filtros. En IIS revisar
campos de W3C logging y retirar `cs-uri-query` del sitio afectado; comprobar también
trazas de solicitudes fallidas y logs del proxy. Registrar la configuración
efectiva y una petición sintética de prueba antes de habilitar acceso externo.
La aplicación usa logs por operación/ruta plantilla y `Cache-Control: no-store`.
No habilitar EF sensitive-data logging o captura de cuerpos para diagnosticar.

La comprobación de hosting corporativo, AD, producción y backup/restore físico
no se acredita por las pruebas del host aislado. Registrar esos límites por
separado en TASKS-005 y no activar el servicio compartido por este runbook.
