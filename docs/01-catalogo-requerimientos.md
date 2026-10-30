# GarajeMetría — Catálogo de requerimientos del backend

## 1. Propósito

Este documento es el índice central de los requerimientos del backend. Cada
requerimiento agrupa una funcionalidad y tendrá su propio documento con reglas,
permisos, contratos y criterios de aceptación.

Las decisiones ya acordadas se recogen en
[Visión y alcance.md](00-vision-y-alcance.md). Las especificaciones indicadas
bajo `requirements/` son relativas a `docs/`. Las que aparecen como rutas en
código están pendientes de crear; los enlaces apuntan a archivos existentes.

Los diseños técnicos para desarrollo guiado por especificaciones (SDD)
se mantienen en `design/`. Para RF-001, consultar
[DESIGN-001 — Autenticación](design/DESIGN-001-authentication.md).
Para RF-002, consultar
[DESIGN-002 — Roles y acceso a talleres](design/DESIGN-002-roles-and-workshop-access.md).
Para RF-003, consultar
[DESIGN-003 — Recepción de eventos LPR](design/DESIGN-003-lpr-event-ingestion.md).

Los planes de implementación se mantienen en `tasks/`:
[TASKS-001 — Autenticación](tasks/TASKS-001-authentication.md) y
[TASKS-002 — Roles y acceso a talleres](tasks/TASKS-002-roles-and-workshop-access.md).
Los requisitos definen qué construir, los diseños cómo hacerlo y las tareas
el trabajo pendiente con sus dependencias y verificaciones. Los contratos
técnicos de autenticación de usuarios tienen una sola fuente: DESIGN-001; RF-001 mantiene
las reglas funcionales y referencias estables a ese diseño.

## 2. Convenciones

### 2.1. Identificadores

- `RF-XXX`: requerimiento funcional.
- `RNF-XXX`: requerimiento no funcional.
- `DESIGN-XXX`: documento de diseño técnico de un requerimiento funcional.
- `TASKS-XXX`: plan de implementación y verificación de un requerimiento funcional.
- `TXXX-NN`: tarea individual dentro de `TASKS-XXX`.
- `RN-XXX`: regla de negocio.
- `CA-XXX`: criterio de aceptación.
- `CP-XXX`: caso de prueba.

`XXX` representa un número de tres dígitos. Para los documentos funcionales
existentes se conserva la correspondencia: `RF-001` → `DESIGN-001` →
`TASKS-001`. `NN` es el número de tarea con al menos dos dígitos; por ejemplo,
`T001-03` pertenece a `TASKS-001`. El identificador es estable y no determina
por sí solo el orden de ejecución: las dependencias se indican en el plan.

RN, CA y CP se numeran dentro de cada requerimiento. Para evitar ambigüedad al
citarlos desde otro documento, indicar su contexto: `RF-001 / CA-001`.
Los diseños y tareas reutilizan esos identificadores para mantener trazabilidad;
un criterio puede estar cubierto por varios casos de prueba y sus números no
necesitan coincidir. La matriz del diseño relaciona explícitamente CA y CP.
La notación `CA/CP-001` usada en documentos con correspondencia uno a uno se
interpreta únicamente dentro de ese requerimiento; RF-001 distingue ambas listas.

Los archivos usan nombres descriptivos en inglés y conservan su prefijo:
`requirements/RF-001-authentication.md`,
`design/DESIGN-001-authentication.md` y
`tasks/TASKS-001-authentication.md`. El contenido se redacta en español.

### 2.2. Prioridad

Alta, Media o Baja. Se usa «Por definir» cuando no se ha acordado.
La prioridad no modifica las restricciones obligatorias del proyecto.

### 2.3. Estado

- **Pendiente:** aún no se han concretado las reglas de la funcionalidad.
- **En definición:** hay decisiones acordadas y faltan detalles para cerrar la especificación.
- **Definido:** la especificación está completa para su alcance.

El estado refleja la definición del requerimiento, no su implementación.
«Definido» no significa implementado ni probado. La configuración de servidores,
credenciales y certificados, y la preparación de entornos de prueba, no son
decisiones funcionales pendientes ni condicionan ese estado.

La ejecución se registra mediante las tareas de `tasks/`: una casilla marcada
requiere trabajo completado y verificado, no solo documentación redactada.
Los estados propios de los diseños y planes no sustituyen el estado del
requerimiento en este catálogo.

### 2.4. Desarrollo por tramos

Cada tramo se implementa a partir de su requerimiento, diseño y plan de tareas.
No es necesario cerrar todos los requerimientos del proyecto para iniciar uno
definido. Las dependencias compartidas deben identificarse y sus contratos
respetarse; las pruebas unitarias pueden usar dobles. Una integración real solo
se considera verificada cuando se ejecutan sus pruebas con los recursos
correspondientes. El primer tramo acordado es RF-001, autenticación.

## 3. Requerimientos funcionales

| **ID** | **Requerimiento** | **Prioridad** | **Estado** | **Especificación** |
| --- | --- | --- | --- | --- |
| RF-001 | Autenticación | Alta | Definido | [RF-001-authentication.md](requirements/RF-001-authentication.md) |
| RF-002 | Roles y acceso a talleres | Por definir | Definido | [RF-002-roles-and-workshop-access.md](requirements/RF-002-roles-and-workshop-access.md) |
| RF-003 | Recepción de eventos LPR | Por definir | Definido | [RF-003-lpr-event-ingestion.md](requirements/RF-003-lpr-event-ingestion.md) |
| RF-004 | Entradas, salidas y estancias | Por definir | En definición | `requirements/RF-004-arrivals-departures-and-stays.md` |
| RF-005 | Consulta de vehículos e historial | Por definir | En definición | `requirements/RF-005-vehicle-history-and-search.md` |
| RF-006 | Correcciones manuales y auditoría | Por definir | En definición | `requirements/RF-006-manual-corrections-and-audit.md` |
| RF-007 | Almacenamiento y consulta de imágenes | Por definir | En definición | `requirements/RF-007-images.md` |
| RF-008 | Panel de administración | Por definir | Pendiente | `requirements/RF-008-administration-dashboard.md` |
| RF-009 | Catálogo corporativo de talleres | Por definir | En definición | [RF-009-workshop-catalog.md](requirements/RF-009-workshop-catalog.md) |

Autenticación incluye login contra Active Directory local, tokens JWT de acceso
y refrescamiento, renovación y ciclo de sesión. Roles y acceso a talleres incluye
los roles derivados de grupos de AD, el taller predeterminado asignado
localmente por Administradores y los permisos de acceso. Admin tiene prioridad
sobre Advisor; se admite pertenencia anidada y se consulta en login y refresh.
Cada especificación recogerá los detalles de su funcionalidad, sin convertir
cada regla en una fila adicional del catálogo.

RF-009 define la copia local de la fuente corporativa de solo lectura, su
actualización en 15 minutos en operación normal y continuidad con la última
copia ante fallos, junto con los efectos de desactivación y reactivación.
El esquema de origen y su diseño/plan técnico siguen pendientes para implementar
esa integración; no son decisiones pendientes del requisito de autenticación.

RF-003 define la ingesta JSON de un evento por petición y la administración de
dispositivos con OAuth Client Credentials emitido por GarajeMetría. DESIGN-003
concreta sus contratos y persistencia; el plan de implementación está pendiente.
La definición no acredita ejecución.

## 4. Requerimientos no funcionales

Definen las condiciones de seguridad, calidad y operación del backend. Su base
está en la visión y en [AGENTS.md](../AGENTS.md); sus especificaciones detalladas
están pendientes de crear. No hay objetivos numéricos de rendimiento o
disponibilidad acordados.

| **ID** | **Requerimiento** | **Prioridad** | **Estado** | **Especificación** |
| --- | --- | --- | --- | --- |
| RNF-001 | Seguridad y protección de datos | Por definir | En definición | `requirements/RNF-001-security-and-data-protection.md` |
| RNF-002 | Rendimiento y capacidad | Por definir | Pendiente | `requirements/RNF-002-performance-and-capacity.md` |
| RNF-003 | Fiabilidad e integridad de datos | Por definir | En definición | `requirements/RNF-003-reliability-and-data-integrity.md` |
| RNF-004 | Disponibilidad y recuperación | Por definir | Pendiente | `requirements/RNF-004-availability-and-recovery.md` |
| RNF-005 | Mantenibilidad, arquitectura y pruebas | Por definir | En definición | `requirements/RNF-005-maintainability-architecture-and-testing.md` |
| RNF-006 | Independencia del proveedor de base de datos | Por definir | En definición | `requirements/RNF-006-database-independence.md` |
| RNF-007 | Observabilidad y operación | Por definir | En definición | `requirements/RNF-007-observability-and-operations.md` |
| RNF-008 | Calidad y consistencia de los contratos API | Por definir | En definición | `requirements/RNF-008-api-contract-quality.md` |

Rendimiento y capacidad requiere acordar carga y tiempos de respuesta;
disponibilidad y recuperación requiere acordar objetivos de servicio y
recuperación. Estas filas identifican aspectos por definir, sin comprometer
valores ni mecanismos todavía no acordados.

## 5. Reglas comunes

Las reglas de fechas, configuración y conservación se mantienen en
[Visión y alcance, secciones 3.1 y 5](00-vision-y-alcance.md). Cada especificación funcional
recoge o recogerá las que correspondan: tiempos de sesión configurables, agrupación por
fecha de captura y zona horaria del taller, estancia estimada configurable,
imágenes opcionales en disco con ruta configurable, y conservación indefinida
de eventos LPR e imágenes con limpieza externa. Las sesiones, huellas de refresh
y auditoría tienen la retención y limpieza interna definidas en DESIGN-001 / 7.2;
no se les aplica la conservación indefinida. Los permisos por rol y taller
están definidos en RF-002, y el catálogo corporativo se desarrolla en RF-009.

## 6. Fuera del alcance

Las exclusiones se mantienen en
[Visión y alcance, sección 2.2](00-vision-y-alcance.md#22-funcionalidades-fuera-del-alcance).

## 7. Mantenimiento

Al crear una especificación, se sustituirá su ruta prevista por un enlace al
archivo y se actualizarán la prioridad y el estado cuando corresponda. Las
preguntas pendientes se tratarán en la especificación de cada requerimiento.
Al crear o modificar diseños y planes, mantener sus enlaces, dependencias y
trazabilidad con RN/CA/CP. Actualizar el conjunto de documentos afectado cuando
cambie un contrato, sin dar por implementado lo que solo está documentado.

Los identificadores de este catálogo sustituyen la descomposición anterior por
reglas individuales. A partir de esta agrupación se mantendrán estables.
