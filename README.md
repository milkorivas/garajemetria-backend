# GarageMetrics Backend

API REST centralizada desarrollada en **.NET (C#)** para el sistema **GarajeMetría**. Su función principal es recibir los eventos de lectura de matrículas (LPR) enviados desde los diferentes equipos locales (*Desde una app o desde Hardware especializado*) instalados en los talleres, procesar las reglas de negocio (tiempos de estancia, entradas y salidas), persistir los datos en SQL Server y servir como backend para la consulta y administración de la información desde el panel web de frontend (permitiendo visualizar registros en tiempo real, modificar datos y realizar operaciones de gestión).

## Tecnologías Principales
* **.NET 8.0+ / C#**
* **Entity Framework Core**
* **SQL Server**

## Estructura del Proyecto
* `Controllers/` - Endpoints de la API REST.
* `Models/` - Entidades de base de datos y DTOs de comunicación.
* `Services/` - Lógica de negocio y validaciones.
* `Data/` - Configuración de Entity Framework y migraciones.

## Configuración y Ejecución
1. Clona el repositorio:
   ```bash
   git clone https://github.com/milkorivas/garajemetria-backend.git
