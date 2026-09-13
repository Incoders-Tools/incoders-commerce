# Perfil inicial de sucursal de un equipo y reemplazo coordinado

**Estado:** definición de arquitectura  
**Ámbito:** perfil inicial de sucursal, continuidad local y evolución a multiestación

## Decisión

El perfil inicial de cada sucursal de la primera carnicería utiliza una **notebook POS** que aloja físicamente el nodo local y los datos operativos locales. Esta simplificación física no elimina la frontera lógica del nodo local ni modifica la arquitectura de producto multiestación.

El modelo escalable se conserva: en una evolución futura, varias terminales podrán comunicarse con el mismo nodo local de sucursal sin disponer de bases de negocio independientes.

## Qué no cambia

- La venta, el POS y los periféricos directos continúan siendo operaciones locales y offline-first.
- La sincronización con cloud continúa siendo bidireccional, en segundo plano, auditable e idempotente.
- La notebook inicial mantiene las funciones administrativas locales autorizadas; la web no reemplaza el POS ni el control directo de periféricos.
- El cloud solo puede restaurar información que recibió y confirmó durablemente.

## Reemplazo coordinado de notebook

El reemplazo de notebook es una operación coordinada de identidad, datos y periféricos. **No es una actualización de aplicación.** Una actualización conserva la instalación vinculada; un reemplazo crea una instalación nueva y retira otra.

### Secuencia obligatoria

1. Detener el cambio operativo en el equipo anterior y completar la sincronización pendiente.
2. Esperar confirmación durable del cloud y reconciliar todo el estado local sincronizable, incluidos ventas, pagos, movimientos, configuración y demás cambios autorizados.
3. Generar y verificar un backup local antes de intervenir el equipo. Este resguardo es obligatorio porque el cloud no puede recuperar operaciones que nunca llegaron a ser confirmadas.
4. Instalar la nueva notebook. Al iniciar sesión o enrolarse, recibe una identidad nueva; no reutiliza la identidad del equipo anterior.
5. Ejecutar el bootstrap consistente de organización y sucursal: recibir un snapshot versionado y aplicar los deltas necesarios antes de habilitar el POS.
6. Validar datos operativos, estado de sincronización y periféricos antes de vender u operar. La validación incluye los dispositivos configurados para esa terminal y su diagnóstico operativo.
7. Una vez validada la nueva instalación, retirar y revocar el equipo anterior y registrar el resultado del reemplazo.

## Resguardo y recuperación

El backup local verificable es un resguardo adicional al cloud, no un sustituto de la sincronización. El procedimiento debe conservar evidencia de la última confirmación durable, del backup verificado, del bootstrap, de la validación de datos y periféricos, y de la revocación del equipo anterior.

Si no se puede completar la sincronización o la reconciliación antes del reemplazo, el procedimiento se detiene y se trata como una incidencia de recuperación; no se habilita una nueva notebook mediante un bootstrap que pueda ocultar operaciones locales pendientes.

## Evolución a multiestación

La evolución posterior conserva el mismo modelo de dominio, sincronización y organización/sucursal. Cambia únicamente la topología física:

- El nodo local puede trasladarse a un equipo o servicio designado de la sucursal.
- Las terminales nuevas se enrolan con identidad propia, configuración de periféricos y permisos asignados.
- Las terminales se comunican con el nodo local; no obtienen una base de negocio independiente.
- La migración se realizará con un procedimiento coordinado de backup, bootstrap, validación y reconciliación equivalente al de reemplazo.

## Fuera de alcance

Este documento no selecciona tecnologías, motores de base de datos, protocolos de periféricos ni mecanismos de implementación. Esas decisiones permanecen pendientes de sus respectivos ADR y relevamientos operativos.
