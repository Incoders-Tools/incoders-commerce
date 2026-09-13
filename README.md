# Plataforma de Gestión Comercial Incoders

> Plataforma offline-first para gestionar ventas, pedidos, stock, compras, caja, cuentas corrientes y distribución desde una operación integrada.

**Estado:** definición de producto  
**Nombre comercial:** pendiente  
**Primer vertical:** carnicerías y distribución cárnica  
**Plataforma local inicial:** Windows moderno x64

---

## Visión

El proyecto nació como un punto de venta, pero evolucionó hacia una plataforma integral de gestión comercial y operativa.

El POS continúa siendo una interfaz crítica, aunque forma parte de un sistema que relaciona:

- Ventas y pagos.
- Pedidos presenciales, por WhatsApp y online.
- Clientes, contactos y proveedores.
- Compras e inventario.
- Listas de precios minoristas y mayoristas.
- Cuentas corrientes.
- Caja principal, cajas por terminal y caja chica.
- Gastos y cuentas financieras.
- Repartos, remitos y cobranzas.
- Empleados y tareas.
- Campañas, reportes e inteligencia artificial.
- Procesos específicos para carnicerías, incluido el romaneo.

La primera carnicería operará inicialmente con una notebook POS por sucursal. Esa notebook alojará físicamente el nodo local, sin eliminar su frontera lógica. La arquitectura de producto conserva el modelo escalable de varias terminales por sucursal. El núcleo debe poder adaptarse a otros comercios minoristas, mayoristas y distribuidores.

## Principio fundamental

> **La venta local nunca debe depender de una conexión a Internet.**

Cloud no es solamente respaldo: también origina pedidos online, pagos web, campañas y administración remota. La plataforma debe combinar continuidad local con sincronización bidireccional confiable.

## Alcance

Todo lo definido en [PRD.md](./PRD.md) pertenece al producto. Las etapas indican orden y dependencias, no funcionalidades opcionales.

La aplicación futura que calculará romaneos automáticamente mediante videos, imágenes y machine learning será un producto independiente. La plataforma de gestión incluirá el romaneo operativo y podrá recibir resultados confirmados desde esa aplicación.

## Topología funcional

~~~mermaid
flowchart TD
    POS["POS de caja"] --> LOCAL["Nodo local de sucursal"]
    ADMIN["Administración local"] --> LOCAL
    HW["Balanza e impresoras"] --> LOCAL
    LOCAL <--> CLOUD["Plataforma cloud"]
    WEB["Administración web/móvil"] --> CLOUD
    ORDERS["Pedidos y pagos online"] --> CLOUD
~~~

### Nodo local de sucursal

Cada sucursal contará con un nodo responsable de reglas y datos locales, comunicación con las terminales, hardware, eventos pendientes, sincronización, diagnóstico y actualización.

En el perfil inicial de una sola notebook POS, el nodo local se aloja físicamente en esa notebook. Esto no elimina su frontera lógica: el mismo límite deberá permitir una topología futura con varias terminales conectadas a un nodo de sucursal. La definición y el reemplazo coordinado de este perfil se documentan en [SINGLE_DEVICE_BRANCH_PROFILE.md](./SINGLE_DEVICE_BRANCH_PROFILE.md).

### Clientes locales

- Interfaz de caja y administración operativa Windows; la administración local requiere inicio de sesión administrativo.
- Paridad funcional con la administración web para la gestión de negocio autorizada: clientes, proveedores, cuentas corrientes, catálogo, configuración, reportes y demás operaciones aplicables.
- POS y control directo de hardware exclusivamente locales.
- Comunicación con el nodo local.
- Sin base de negocio independiente por terminal.
- Identidad y configuración propias para hardware y diagnóstico.

### Plataforma cloud

- Pedidos y pagos online.
- Administración remota y móvil autenticada, con paridad funcional respecto de la administración local para la gestión de negocio autorizada.
- Reportes consolidados, remotos y móviles.
- Sincronización entre sucursales.
- Campañas, notificaciones e IA.
- Respaldo y recuperación.

La web no reemplaza el POS ni controla periféricos. Cuando una sucursal está sin conexión, los datos remotos se consideran actuales solo hasta su última sincronización cloud exitosa; la interfaz deberá hacerlo visible.

## Persistencia local y cloud

La tecnología de base de datos todavía no está decidida.

SQLite continúa siendo candidata para el nodo local por simplicidad, rendimiento y facilidad de distribución. Deberá compararse con una base servidor para validar concurrencia, mantenimiento y recuperación en el perfil escalable de varias terminales por sucursal.

La velocidad del POS dependerá principalmente de:

- Ejecutar el flujo crítico completamente en la red local.
- No esperar respuestas cloud para confirmar una venta.
- Mantener índices y consultas adecuados.
- Separar reportes pesados del flujo transaccional.
- Procesar sincronización y tareas fuera del camino crítico.

Cloud podrá utilizar otra tecnología. No se busca replicar archivos o tablas ciegamente, sino sincronizar operaciones y eventos mediante contratos explícitos.

La decisión se documentará en un ADR comparando SQLite, una base servidor local, concurrencia, instalación, mantenimiento, backups, migraciones, recuperación, sincronización y costos cloud.

## Sincronización bidireccional

- **Local → cloud:** ventas, pagos, stock, caja, entregas, clientes y cambios autorizados.
- **Cloud → local:** pedidos online, pagos aprobados, configuraciones, usuarios y cambios comerciales.

Principios:

- La venta se confirma localmente.
- Ante una interrupción de Internet, la operación local conserva la autoridad resiliente y offline-first.
- Cada operación tiene un identificador global.
- Los mensajes son idempotentes.
- Los eventos se guardan hasta recibir confirmación.
- El sistema detecta automáticamente conexión recuperada y trabajo pendiente.
- La sincronización se ejecuta en segundo plano.
- Los conflictos siguen reglas por tipo de dato.
- Los conflictos no resolubles requieren revisión.
- El estado es visible sin bloquear la caja.
- Existe una acción manual de diagnóstico, sin reemplazar la sincronización automática.

Estados sugeridos: sincronizado, sincronizando, pendiente, sin conexión y requiere atención.

“Tiempo real” significa sincronización continua con consistencia eventual controlada; no que la venta dependa de una conexión permanente.

## Instalación en el cliente

La instalación será guiada, repetible y apta para soporte remoto.

### Alta de una organización

1. Crear organización y sucursales.
2. Generar una credencial de vinculación de corta duración.
3. Instalar y registrar el nodo de cada sucursal.
4. Configurar red, terminales y permisos.
5. Detectar o configurar hardware.
6. Probar venta, impresión y sincronización.
7. Importar datos iniciales.
8. Generar y verificar un backup inicial.
9. Registrar instalación y versión.

El reemplazo de una notebook es una operación coordinada de identidad, datos y periféricos; no es una actualización de aplicación. Debe seguir el procedimiento definido en [SINGLE_DEVICE_BRANCH_PROFILE.md](./SINGLE_DEVICE_BRANCH_PROFILE.md).

### Alta de una terminal

1. Instalar el cliente.
2. Descubrir o indicar el nodo de sucursal.
3. Vincular mediante un código seguro.
4. Asignar sucursal, caja y permisos.
5. Configurar periféricos.
6. Verificar conectividad y operación.

### Requisitos del instalador

- Instalador firmado para Windows x64.
- Instalación y reparación idempotentes.
- Validación de prerrequisitos.
- Configuración segura.
- Registro de versión y canal.
- Desinstalación sin borrar datos de negocio.
- Exportación de diagnóstico.
- Instalación silenciosa futura.

## Versionado y actualizaciones

La rama **main** representará código integrado y potencialmente liberable, pero una actualización no se instalará en clientes solamente porque exista un commit nuevo.

Flujo:

1. Un cambio se integra en main.
2. CI compila, prueba y valida migraciones.
3. Se construyen artefactos reproducibles.
4. Se asigna una versión semántica.
5. Se firman paquetes y manifiesto.
6. La versión se publica en un canal.
7. Los clientes detectan automáticamente la versión.
8. La instalación se programa o autoriza según política.
9. Se verifica salud y compatibilidad.
10. Se informa el resultado y se permite recuperación segura.

Canales:

- **internal:** pruebas internas.
- **pilot:** una terminal o sucursal controlada.
- **stable:** despliegue general.

Así, main automatiza la generación de versiones sin convertir cada merge en un despliegue riesgoso a todas las cajas.

### Requisitos del actualizador

- Consulta periódica y al iniciar una sesión administrativa.
- Manifiesto firmado con versión, hash, tamaño, requisitos y notas.
- Descarga en segundo plano y reanudable.
- Validación de firma e integridad.
- Prohibición de actualizar durante una venta.
- Coordinación de nodo y terminales.
- Compatibilidad durante despliegues graduales.
- Backup antes de migraciones.
- Health check posterior.
- Rollback o recuperación documentada.
- Actualización obligatoria solo para seguridad o fallos críticos.
- Registro central de instalaciones y resultados.

Cada release declarará desde qué versiones puede actualizarse. Las migraciones no dependerán de una reversión destructiva.

## Compatibilidad

- Windows moderno x64.
- Una sola base de código.
- Sin soluciones separadas para x86 y x64.
- x86 o ARM solamente ante una necesidad real.
- Administración web responsive.

## Orden de implementación

1. Fundaciones, catálogo, permisos, POS, caja e inventario.
2. Pedidos, cuentas corrientes, reparto y tareas.
3. Compras, proveedores, cuentas financieras, caja chica y gastos.
4. Cloud, sincronización, web, pedidos y pagos online.
5. Campañas, IA, tableros y recepción inteligente.
6. Romaneo integrado.

Sincronización, auditoría, aislamiento y actualización deben diseñarse desde el comienzo.

## Decisiones tecnológicas

El stack todavía no está aprobado. Toda decisión importante deberá partir del PRD, comparar alternativas, incluir implicancias operativas, registrarse mediante ADR y responder a una necesidad concreta.

## Documentación

El repositorio será la fuente de verdad:

- PRD.
- Glosario y mapa de módulos.
- Modelo conceptual.
- Matriz de permisos y catálogo de estados.
- SDD local/cloud.
- Diseño de sincronización.
- Diseño de instalación y actualización.
- [Perfil inicial de sucursal de un equipo y reemplazo coordinado](./SINGLE_DEVICE_BRANCH_PROFILE.md).
- Inventario, cuentas corrientes y hardware.
- Seguridad, auditoría y testing.
- ADR.
- Descubrimiento del romaneo.

## Filosofía de desarrollo

El proyecto seguirá un flujo de especificación, diseño, pruebas e implementación asistido por agentes.

Las personas son responsables de visión, relevamiento, reglas, decisiones, revisión y aceptación. Los agentes asisten con organización, documentación, propuestas, implementación, refactoring, pruebas y verificación.

Ninguna implementación crítica se basará solamente en supuestos no validados.

## Estado

El proyecto se encuentra en **definición de producto**.

Próximos pasos:

1. Validar el dominio con ambas sucursales.
2. Relevar hardware y operación.
3. Documentar el romaneo observado.
4. Definir arquitectura local/cloud.
5. Comparar persistencia y sincronización.
6. Diseñar instalación, actualización y recuperación.
7. Elaborar SDD, ADR y plan de pruebas.





