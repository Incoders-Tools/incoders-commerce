# Informe del seed de proveedores y catálogo de Vaca Verde

Generado por `generate_suppliers_catalog.py` a partir de las planillas del dueño. Incluye lo que se cargó, las decisiones tomadas y todo lo que hay que **revisar**.

## Resumen

| Qué | Cantidad |
| --- | --- |
| Proveedores | 9 |
| Contactos de proveedores | 8 |
| Categoría de proveedores | 1 (Carne) |
| Categorías de productos | 8 (Almacén y Bebidas vacías) |
| Productos | 90 |
| Presentaciones («Por kg», pesables, kg) | 90 |
| Precios en la lista Mostrador | 74 |
| Precios en la lista Reparto | 25 |
| Conjunto de tasas | 1, solo de la lista Reparto (IVA 10,5 %, IB 2,5 %, Flete 7 %, Remarcación 25 %, todas sobre la base) |
| Lista Clientes | no se carga (los clientes se precian con Reparto) |

Todos los precios rigen desde el 01/10/2026. La sucursal es «Ruta 51». Volver a correr el seed no cambia nada.

## Proveedores

| Proveedor | Ciudad | Dirección | Teléfono | Email | Contactos |
| --- | --- | --- | --- | --- | --- |
| Swift | Rosario | — | 3416254888 | nelson.beber@minervafoods.com | Nelson Luis Beber Valdemarin |
| Urien Loza | Pilar | — | 1153021969 | — | Marcos Loza |
| Ventas DF Distribuidora | Munro | — | 1139493069 | — | Gustavo (Depósito) |
| Carnicería Cow | Ciudad de Buenos Aires | Av. Nazca 5096 | 1160223630 | — | — |
| Frigorífico Merlo | Merlo | Elías Alippi 1890 | 2204995149 | info@frigorificomerlo.com | Eduardo Manchessot |
| Frigorífico Gorina | — | — | 1139844090 | — | Lautaro Silvestri |
| Frigorífico Frigolar | — | — | 2216550132 | — | Carlos Charton |
| Amalia | San Pedro | — | 3329544332 | — | — |
| Frimsa | — | — | — | — | Agustín Villarino 1160521221; Matías Villarino 1163701224 |

Cambios de nombre: «VentasDF Distribuid.» pasó a «Ventas DF Distribuidora»; «Amalia (San Pedro)» pasó a «Amalia» (San Pedro quedó como ciudad); «Frigorifico»/«Carniceria» se escribieron con tilde. Las dos filas «Villarino … FRIMSA» se unieron en **un** proveedor «Frimsa» con dos contactos (Agustín y Matías Villarino, cada uno con su teléfono). Los teléfonos quedaron solo con dígitos.

## Catálogo

### Vacuno (39)

| Código | Producto | Mostrador (final, 01/10) | Reparto (base) |
| --- | --- | --- | --- |
| 001 | Aguja | 17.000,00 | — |
| 002 | Aguja especial | 18.000,00 | — |
| 003 | Asado completo | — | 10.600,00 |
| 004 | Asado rueda | — | 9.000,00 |
| 005 | Bife de chorizo | 25.000,00 | — |
| 006 | Bola de lomo | 18.000,00 | 11.400,00 |
| 007 | Bola de lomo feteada | — | 12.900,00 |
| 008 | Cima | 18.000,00 | — |
| 009 | Colita de cuadril | 23.000,00 | 15.000,00 |
| 010 | Corazón de cuadril | — | 13.500,00 |
| 011 | Corte americano | 25.000,00 | — |
| 012 | Costeleta | 19.000,00 | — |
| 013 | Costilla | 17.800,00 | — |
| 014 | Cuadrada | 21.000,00 | 12.000,00 |
| 015 | Cuadrada feteada | — | 13.500,00 |
| 016 | Cuadril | 22.000,00 | — |
| 017 | Entraña | 23.500,00 | 22.500,00 |
| 018 | Entraña vaca congelada | 20.300,00 | 14.500,00 |
| 019 | Falda | 17.000,00 | — |
| 020 | Lomo | 25.000,00 | — |
| 021 | Matambre | 18.000,00 | 8.000,00 |
| 022 | Nalga | 23.000,00 | — |
| 023 | Nalga con tapa | — | 12.000,00 |
| 024 | Nalga feteada | — | 15.000,00 |
| 025 | Nalga sin tapa | — | 13.500,00 |
| 026 | Osobuco | 14.000,00 | — |
| 027 | Paleta | 18.000,00 | 11.300,00 |
| 028 | Palomita | 17.000,00 | — |
| 029 | Peceto | 23.000,00 | 13.500,00 |
| 030 | Pecho | — | 7.400,00 |
| 031 | Picada | 15.000,00 | — |
| 032 | Roast beef | — | 11.300,00 |
| 033 | Rueda | — | 8.000,00 |
| 034 | Sin ral | — | 7.900,00 |
| 035 | Tapa de asado | 20.500,00 | 11.300,00 |
| 036 | Tapa de nalga | 23.000,00 | 10.000,00 |
| 037 | Vacío | 21.500,00 | 11.000,00 |
| 038 | Vacío marca Santa Inés | — | 9.500,00 |
| 039 | Vacío novillo seleccionado | — | 12.000,00 |

### Cerdo (15)

| Código | Producto | Mostrador (final, 01/10) | Reparto (base) |
| --- | --- | --- | --- |
| 040 | Bola de lomo de cerdo | 9.000,00 | — |
| 041 | Bondiola | 10.500,00 | — |
| 042 | Costilla de cerdo | 9.200,00 | — |
| 043 | Cuadrada de cerdo | 8.000,00 | — |
| 044 | Cuadril de cerdo | 9.000,00 | — |
| 045 | Lomo de cerdo | 9.000,00 | — |
| 046 | Matambre de cerdo | 16.000,00 | — |
| 047 | Nalga de cerdo | 8.000,00 | — |
| 048 | Papada | 9.000,00 | — |
| 049 | Pechito | 7.800,00 | — |
| 050 | Pernil | 14.000,00 | — |
| 051 | Picada de cerdo | 10.000,00 | — |
| 052 | Pulpa | 8.000,00 | — |
| 053 | Solomillo | 9.000,00 | — |
| 054 | Tortuguita | 8.000,00 | — |

### Embutidos (10)

| Código | Producto | Mostrador (final, 01/10) | Reparto (base) |
| --- | --- | --- | --- |
| 055 | Bondiola salada | 31.000,00 | — |
| 056 | Chorizo colorado | 23.200,00 | — |
| 057 | Chorizo fresco | 11.500,00 | — |
| 058 | Chorizo seco | 25.100,00 | — |
| 059 | Jamón crudo | 31.500,00 | — |
| 060 | Morcilla | 8.100,00 | — |
| 061 | Salame | 25.500,00 | — |
| 062 | Salamín picado fino | 23.200,00 | — |
| 063 | Salamín picado grueso | 23.200,00 | — |
| 064 | Salchicha parrillera | 13.500,00 | — |

### Pollo (9)

| Código | Producto | Mostrador (final, 01/10) | Reparto (base) |
| --- | --- | --- | --- |
| 065 | Alitas | 5.500,00 | — |
| 066 | Carcaza | 3.500,00 | — |
| 067 | Menudos | 2.000,00 | — |
| 068 | Pata | 7.300,00 | — |
| 069 | Pata y muslo | 6.500,00 | — |
| 070 | Patitas | 10.700,00 | — |
| 071 | Pechuga | 14.000,00 | — |
| 072 | Pollo entero | 6.000,00 | — |
| 073 | Trozado | 5.500,00 | — |

### Achuras (12)

| Código | Producto | Mostrador (final, 01/10) | Reparto (base) |
| --- | --- | --- | --- |
| 074 | Centro | — | — |
| 075 | Chinchulín | 8.000,00 | — |
| 076 | Corazón | 7.500,00 | — |
| 077 | Cuajada | 6.000,00 | — |
| 078 | Hígado | 7.500,00 | — |
| 079 | Lengua | 11.570,00 | — |
| 080 | Molleja | 32.000,00 | — |
| 081 | Mondongo | 12.000,00 | — |
| 082 | Rabo | 10.200,00 | — |
| 083 | Riñón | 7.000,00 | — |
| 084 | Sesos | 5.000,00 | — |
| 085 | Tripa gorda | 8.000,00 | — |

### Milanesas (5)

| Código | Producto | Mostrador (final, 01/10) | Reparto (base) |
| --- | --- | --- | --- |
| 086 | Medallón de pollo | 10.700,00 | — |
| 087 | Medallón de pollo jamón y queso | 12.500,00 | — |
| 088 | Milanesa de ternera de bola de lomo | — | — |
| 089 | Milanesas de muslo | 8.000,00 | — |
| 090 | Milanesas de pechuga | 10.500,00 | — |

### Almacén (0)

Sin productos por ahora.

### Bebidas (0)

Sin productos por ahora.

Los códigos 001, 002, … son **provisorios**: se asignan por categoría y luego por nombre, y se reemplazarán cuando se integre la balanza.

## Productos unidos (un mismo corte en varias listas)

Un corte presente en varias planillas es **un solo producto** con un precio por lista. Los siguientes aparecen a la vez en las listas de reparto y en las de mostrador (las hojas «Vacuna» y «MEDIA» son idénticas):

- Bola de lomo: Reparto, Clientes, Vacuna, MEDIA
- Colita de cuadril: Reparto, Clientes, Vacuna, MEDIA
- Cuadrada: Reparto, Clientes, Vacuna, MEDIA
- Entraña: Reparto, Clientes, Vacuna, MEDIA
- Entraña vaca congelada: Reparto, Clientes, Vacuna, MEDIA
- Matambre: Reparto, Clientes, Vacuna, MEDIA
- Paleta: Reparto, Clientes, Vacuna, MEDIA
- Peceto: Reparto, Clientes, Vacuna, MEDIA
- Tapa de asado: Reparto, Clientes, Vacuna, MEDIA
- Tapa de nalga: Reparto, Clientes, Vacuna, MEDIA
- Vacío: Reparto, Clientes, Vacuna, MEDIA

## Para revisar

1. **Milanesa de ternera de bola de lomo** es un producto de ejemplo (provisorio, categoría Milanesas) y **no tiene precio**; se puede editar o dar de baja. Cargale un precio cuando lo definas.
2. **Centro** (Achuras) dice «NO» en «Precio con aumento» (precio original 9.300,00): se creó el producto **sin precio**.
3. **Achuras: los porcentajes no cierran.** Se usó siempre la columna «Precio con aumento» como precio final de Mostrador. Comparación con precio original × (1 + %):

   | Producto | Original | % | Calculado | Precio con aumento (usado) |
   | --- | --- | --- | --- | --- |
   | Hígado | 3.500,00 | 50 % | 5.250,00 | 7.500,00 |
   | Corazón | 5.000,00 | 30 % | 6.500,00 | 7.500,00 |
   | Riñón | 4.600,00 | 30 % | 5.980,00 | 7.000,00 |
   | Cuajada | 4.500,00 | 30 % | 5.850,00 | 6.000,00 |
   | Rabo | 7.800,00 | 30 % | 10.140,00 | 10.200,00 |
   | Mondongo | 7.800,00 | 30 % | 10.140,00 | 12.000,00 |
   | Chinchulín | 4.100,00 | 30 % | 5.330,00 | 8.000,00 |
   | Tripa gorda | 4.000,00 | 30 % | 5.200,00 | 8.000,00 |
   | Sesos | 2.500,00 | 30 % | 3.250,00 | 5.000,00 |
   | Molleja | 24.000,00 | 30 % | 31.200,00 | 32.000,00 |

4. Reparto: base × 1,45 coincide con el PRECIO FINAL de la planilla en los 25 cortes.
5. **Mostrador pasa a precio base** (seed 004, rige desde 02/10/2026): IVA 10,5 % + IB 2,5 % + Remarcación 35 % sobre la base (sin flete, ×1,48). La base de cada corte que también está en Reparto es la base de Reparto (11 cortes, p. ej. Bola de lomo: 11.400 × 1,48 = 16.872 contra 18.000 antes); la de los cortes que solo se venden en mostrador sale del precio final cargado dividido 1,48 y redondeado a centavos (63 cortes, p. ej. Lengua: 11.570 / 1,48 = 7.817,57). Los precios anteriores de Mostrador quedan en el historial.
6. Piso de Mostrador: ningún corte con precio en las dos listas queda por debajo de Reparto (×1,48 contra ×1,45 sobre la misma base). Los cortes que solo se venden en mostrador no tienen precio de Reparto con qué comparar.
7. Mostrador baja respecto del precio final anterior en los cortes que también están en Reparto. La «Lista Clientes» original (14.500 para Asado completo) no se carga: Reparto compone ese corte en 15.370.
8. Productos **sin precio en Mostrador** (15; solo existen en la lista de Reparto o no tienen precio): Asado completo, Asado rueda, Bola de lomo feteada, Centro, Corazón de cuadril, Cuadrada feteada, Nalga con tapa, Nalga feteada, Nalga sin tapa, Pecho, Roast beef, Rueda, Sin ral, Vacío marca Santa Inés, Vacío novillo seleccionado.
9. Cortes de mostrador **sin precio en Reparto** (esa planilla es de cortes al por mayor): Aguja, Aguja especial, Bife de chorizo, Cima, Corte americano, Costeleta, Costilla, Cuadril, Falda, Lomo, Nalga, Osobuco, Palomita, Picada.
10. **«Nalga» (mostrador) y «Nalga con tapa» / «Nalga sin tapa» / «Nalga feteada» (reparto)** quedaron como productos distintos porque no son el mismo nombre. «Cuadrada», «Matambre», «Paleta», «Peceto», «Tapa de asado», «Tapa de nalga», «Colita de cuadril», «Entraña», «Entraña vaca congelada», «Vacío» y «Bola de lomo» sí se unieron entre listas. Avisá si querés unir o separar alguno.
11. **«Vacío novillo seleccionado»** y **«Vacío marca Santa Inés»** (esta última escrita «Ines» en la planilla) son productos separados de **«Vacío»**.
12. **Cerdo:** los cortes que comparten nombre con uno vacuno se llamaron «… de cerdo» (Costilla, Matambre, Cuadril, Lomo, Cuadrada, Nalga, Bola de lomo, Picada). «Bondiola», «Pulpa», «Papada», «Pernil», «Solomillo», «Pechito» y «Tortuguita» quedaron igual.
13. **«Pollo»** pasó a **«Pollo entero»**. «Bondiola Salda» (error de tipeo) pasó a **«Bondiola salada»**. «Medallón de Pollo Jamón y Ques» pasó a **«Medallón de pollo jamón y queso»**. «Milanesas Pechuga» pasó a **«Milanesas de pechuga»** (como «Milanesas de muslo»).
14. **«Patitas»** (Pollo) y **«Medallón de pollo»** tienen el mismo precio (10.700): puede ser un error de carga.
15. **Sin ral:** se dejó el nombre como en la planilla; no está claro si es «Sin ral» u otra palabra. Confirmar.
16. La planilla de Reparto tiene **25** cortes (5 colgados y 20 al vacío), no 26.
17. Nota de la planilla Clientes sobre **Asado rueda**: «es el costillar completo con el mocho». Los productos no tienen campo de descripción, por eso queda solo acá.
18. Nota de la planilla Clientes sobre **Sin ral**: «es la media res sin los bifes de cuadril y lomo: es el costillar con el pecho y la rueda». Los productos no tienen campo de descripción, por eso queda solo acá.
19. **Proveedor Swift:** el contacto «Nelson Luis Beber Valdemarin» se dejó **entero** en el nombre (no se sabe si «Nelson Luis» es nombre compuesto); su email se cargó en el proveedor y en el contacto.
20. **Ventas DF Distribuidora:** la celda decía «Paso contacto Gustavo del deposito»; se cargó el contacto «Gustavo» con rol «Depósito». El teléfono de la planilla quedó en el proveedor (no se sabe si es el de Gustavo). La ciudad decía «Munro Florida GBA»: se usó **Munro**.
21. **Ciudades con homónimos:** «Pilar» se cargó como Pilar (Buenos Aires), «San Pedro» como San Pedro (Buenos Aires) y «Merlo» como Merlo (Buenos Aires); la planilla no aclara la provincia. «Capital Federal» es Ciudad de Buenos Aires.
22. **Gorina, Frigolar y Frimsa** no tienen ciudad en la planilla: quedaron **sin ciudad** para no adivinar.
23. **Frimsa** quedó sin teléfono propio: cada contacto tiene el suyo.
24. **Carnicería Cow** y **Amalia** no tienen contacto en la planilla. La dirección «Av nazca 5096» / «Elias Alippi 1890 Merlo» se separó en calle y número.
25. Todos los proveedores se categorizaron como **Carne** (la planilla no distingue).
